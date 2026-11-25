using Karaokay.Api.Features;
using Karaokay.Api.Hubs;
using Karaokay.Contracts.Hubs;
using Karaokay.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlaybackStatus = Karaokay.Domain.PlaybackStatus;
using QueueItemState = Karaokay.Domain.QueueItemState;
using WireMediaSource = Karaokay.Contracts.Hubs.MediaSource;
using WirePlaybackStatus = Karaokay.Contracts.Hubs.PlaybackStatus;
using WireQueueItemState = Karaokay.Contracts.Hubs.QueueItemState;

namespace Karaokay.UnitTests;

/// <summary>
/// Regression coverage for the Slice 3 review's M1 finding: a guest reorder must never let the
/// playback cursor skip a queued song or end the room while songs remain. Also pins the basic
/// playback transitions (start / pause / resume / skip / idle) at the service layer.
/// </summary>
public sealed class QueueServiceTests
{
    [Fact]
    public async Task MovingFutureItemAbovePlayingClampsAfterCurrentAndPlaysNext()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var a = AddEntry(room, QueueItemState.Playing, position: 0, name: "A");
        var b = AddEntry(room, QueueItemState.Ready, position: 1, name: "B");
        var c = AddEntry(room, QueueItemState.Ready, position: 2, name: "C");
        BeginPlaying(room, a);

        var result = await service.MoveQueueItemAsync(room, c.Id, 0, CancellationToken.None);

        Assert.Equal(QueueMoveResult.Moved, result);
        Assert.Equal([a.Id, c.Id, b.Id], QueueIds(room));
        Assert.Equal([0, 1, 2], room.Queue.Select(entry => entry.Position));
        Assert.Equal(QueueItemState.Playing, a.State);

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(c.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, c.State);
        Assert.Equal(QueueItemState.Ended, a.State);
        Assert.Equal(QueueItemState.Ready, b.State);
    }

    [Fact]
    public async Task MovingThePlayingItemIsRejectedAndAdvanceOrderIsUnchanged()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var a = AddEntry(room, QueueItemState.Playing, position: 0, name: "A");
        var b = AddEntry(room, QueueItemState.Ready, position: 1, name: "B");
        var c = AddEntry(room, QueueItemState.Ready, position: 2, name: "C");
        BeginPlaying(room, a);

        var result = await service.MoveQueueItemAsync(room, a.Id, 2, CancellationToken.None);

        Assert.Equal(QueueMoveResult.NotMovable, result);
        Assert.Equal([a.Id, b.Id, c.Id], QueueIds(room));
        Assert.Equal([0, 1, 2], room.Queue.Select(entry => entry.Position));

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(b.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, b.State);
        Assert.Equal(QueueItemState.Ended, a.State);
        Assert.Equal(QueueItemState.Ready, c.State);
    }

    [Fact]
    public async Task MovingEndedHistoryIsRejected()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var a = AddEntry(room, QueueItemState.Ended, position: 0, name: "A");
        var b = AddEntry(room, QueueItemState.Playing, position: 1, name: "B");
        var c = AddEntry(room, QueueItemState.Ready, position: 2, name: "C");
        BeginPlaying(room, b);

        var result = await service.MoveQueueItemAsync(room, a.Id, 2, CancellationToken.None);

        Assert.Equal(QueueMoveResult.NotMovable, result);
        Assert.Equal([a.Id, b.Id, c.Id], QueueIds(room));
    }

    [Fact]
    public async Task AdvancePlaysAnEarlierReadyItemInsteadOfGoingIdle()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();

        // An item that became Ready behind the cursor (download finished while the current song
        // played) must still be playable; positions alone must not strand it.
        var earlier = AddEntry(room, QueueItemState.Ready, position: 0, name: "Earlier");
        var current = AddEntry(room, QueueItemState.Playing, position: 1, name: "Current");
        BeginPlaying(room, current);

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(earlier.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, earlier.State);
        Assert.Equal(QueueItemState.Ended, current.State);
    }

    [Fact]
    public async Task StartPlaybackBeginsTheFirstPlayableItem()
    {
        var (service, client) = CreateService();
        var room = CreateRoom();
        var ready = AddEntry(room, QueueItemState.Ready, position: 0, name: "Ready");

        await service.StartPlaybackAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(ready.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, ready.State);
        Assert.NotNull(room.PlaybackStartedAtUtc);
        Assert.Equal(
            WirePlaybackStatus.Playing,
            client.PlaybackStates[^1].Status);
    }

    [Fact]
    public async Task PauseAndResumePreserveTheCurrentItem()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var ready = AddEntry(room, QueueItemState.Ready, position: 0);
        await service.StartPlaybackAsync(room, CancellationToken.None);

        await service.PausePlaybackAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Paused, room.Status);
        Assert.Null(room.PlaybackStartedAtUtc);
        Assert.True(room.PausedAtPositionMs >= 0);

        await service.ResumePlaybackAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(ready.Id, room.CurrentQueueItemId);
        Assert.NotNull(room.PlaybackStartedAtUtc);
        Assert.Equal(0, room.PausedAtPositionMs);
    }

    [Fact]
    public async Task SkipAdvancesToTheNextItemAndGoesIdleAfterTheLast()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var first = AddEntry(room, QueueItemState.Ready, position: 0, name: "First");
        var second = AddEntry(room, QueueItemState.Ready, position: 1, name: "Second");
        await service.StartPlaybackAsync(room, CancellationToken.None);

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(second.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Ended, first.State);
        Assert.Equal(QueueItemState.Playing, second.State);

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Idle, room.Status);
        Assert.Null(room.CurrentQueueItemId);
        Assert.Null(room.PlaybackStartedAtUtc);
        Assert.Equal(QueueItemState.Ended, second.State);
    }

    [Fact]
    public async Task SkipIgnoresFailedAndDownloadingItems()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var current = AddEntry(room, QueueItemState.Playing, position: 0, name: "Current");
        AddEntry(room, QueueItemState.Failed, position: 1, name: "Failed");
        AddEntry(room, QueueItemState.Downloading, position: 2, name: "Downloading");
        var ready = AddEntry(room, QueueItemState.Ready, position: 3, name: "Ready");
        BeginPlaying(room, current);

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(ready.Id, room.CurrentQueueItemId);
        Assert.Equal(PlaybackStatus.Playing, room.Status);
    }

    private static RoomState CreateRoom() => new(Guid.NewGuid(), "ABCD", DateTime.UtcNow);

    private static void BeginPlaying(RoomState room, RoomQueueEntry entry)
    {
        room.CurrentQueueItemId = entry.Id;
        room.Status = PlaybackStatus.Playing;
        room.PlaybackStartedAtUtc = DateTime.UtcNow;
    }

    private static RoomQueueEntry AddEntry(
        RoomState room,
        QueueItemState state,
        int position,
        string name = "Song")
    {
        var entry = new RoomQueueEntry
        {
            Id = Guid.NewGuid(),
            SongId = Guid.NewGuid(),
            Position = position,
            RequestedByName = "Ada",
            State = state,
            RequestedAtUtc = DateTime.UtcNow,
            Song = new SongDto(Guid.NewGuid(), name, "Artist", 1_000, WireMediaSource.Multitrack, null),
        };

        room.QueueEntries.Add(entry);
        return entry;
    }

    private static Guid[] QueueIds(RoomState room) =>
        [.. room.QueueEntries.OrderBy(entry => entry.Position).Select(entry => entry.Id)];

    private static (QueueService Service, RecordingHubClient Client) CreateService()
    {
        var client = new RecordingHubClient();
        var scopeFactory = new NoDatabaseScopeFactory();
        var library = new MediaLibrary(
            Options.Create(new MediaLibraryOptions
            {
                MediaRoot = Path.Combine(Path.GetTempPath(), "karaokay-unit-media"),
            }),
            new TestHostEnvironment(),
            NullLogger<MediaLibrary>.Instance);

        var service = new QueueService(
            scopeFactory,
            new SongCatalog(scopeFactory, library),
            new FakeHubContext(client),
            NullLogger<QueueService>.Instance);

        return (service, client);
    }

    /// <summary>Scope factory with no <c>KaraokayDbContext</c>, so persistence degrades to memory.</summary>
    private sealed class NoDatabaseScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new EmptyScope();

        private sealed class EmptyScope : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) => null;

            public void Dispose()
            {
            }
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Karaokay.UnitTests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeHubContext : IHubContext<StageHub, IStageHubClient>
    {
        public FakeHubContext(IStageHubClient client) => Clients = new FakeHubClients(client);

        public IHubClients<IStageHubClient> Clients { get; }

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FakeHubClients : IHubClients<IStageHubClient>
    {
        private readonly IStageHubClient _client;

        public FakeHubClients(IStageHubClient client) => _client = client;

        public IStageHubClient All => _client;

        public IStageHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => _client;

        public IStageHubClient Client(string connectionId) => _client;

        public IStageHubClient Clients(IReadOnlyList<string> connectionIds) => _client;

        public IStageHubClient Group(string groupName) => _client;

        public IStageHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            _client;

        public IStageHubClient Groups(IReadOnlyList<string> groupNames) => _client;

        public IStageHubClient User(string userId) => _client;

        public IStageHubClient Users(IReadOnlyList<string> userIds) => _client;
    }

    private sealed class RecordingHubClient : IStageHubClient
    {
        public List<string> Events { get; } = [];

        public List<PlaybackStateDto> PlaybackStates { get; } = [];

        public List<(Guid QueueItemId, WireQueueItemState State)> QueueItemStates { get; } = [];

        public Task JoinedRoom(RoomSnapshot snapshot) => Record(nameof(JoinedRoom));

        public Task ParticipantJoined(string displayName, int guestCount) =>
            Record(nameof(ParticipantJoined));

        public Task ParticipantLeft(string displayName, int guestCount) =>
            Record(nameof(ParticipantLeft));

        public Task QueueChanged(QueueDelta delta) => Record(nameof(QueueChanged));

        public Task QueueItemStateChanged(Guid queueItemId, WireQueueItemState state)
        {
            QueueItemStates.Add((queueItemId, state));
            return Record(nameof(QueueItemStateChanged));
        }

        public Task DownloadProgressChanged(DownloadProgressDto dto) =>
            Record(nameof(DownloadProgressChanged));

        public Task SongReady(SongReadyDto dto) => Record(nameof(SongReady));

        public Task DownloadFailed(DownloadFailedDto dto) => Record(nameof(DownloadFailed));

        public Task PlaybackChanged(PlaybackStateDto dto)
        {
            PlaybackStates.Add(dto);
            return Record(nameof(PlaybackChanged));
        }

        public Task CountdownTicked(CountdownDto dto) => Record(nameof(CountdownTicked));

        public Task LyricsLoaded(Guid songId, LyricsDto lyricsRef) => Record(nameof(LyricsLoaded));

        public Task SearchCompleted(string correlationId, IReadOnlyList<YouTubeSearchResultDto> results) =>
            Record(nameof(SearchCompleted));

        public Task RoomClosed(string reason) => Record(nameof(RoomClosed));

        public Task HubError(HubErrorDto hubError) => Record(nameof(HubError));

        private Task Record(string name)
        {
            Events.Add(name);
            return Task.CompletedTask;
        }
    }
}
