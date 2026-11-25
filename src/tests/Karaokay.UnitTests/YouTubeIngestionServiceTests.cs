using System.Collections.Concurrent;
using Karaokay.Api.Endpoints;
using Karaokay.Api.Features;
using Karaokay.Api.Hubs;
using Karaokay.Application.Downloads;
using Karaokay.Contracts;
using Karaokay.Contracts.Hubs;
using Karaokay.Domain;
using Karaokay.Infrastructure.MediaIngest;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using DownloadStage = Karaokay.Domain.DownloadStage;
using QueueItemState = Karaokay.Domain.QueueItemState;

namespace Karaokay.UnitTests;

/// <summary>
/// Admission-control coverage for <see cref="YouTubeIngestionService"/> (docs/ARCHITECTURE.md §5.1/§7,
/// Slice 5): channel and per-room caps, the coalescing bypass, session-token resolution, and the
/// queue-full cleanup path. The harness intentionally resolves no <c>KaraokayDbContext</c>, which is
/// the same no-database degradation the API uses for a bare <c>dotnet run</c>.
/// </summary>
public sealed class YouTubeIngestionServiceTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    [Fact]
    public async Task FullChannelRejectsEnqueueAndFailsTheQueueRow()
    {
        await using var harness = new IngestionHarness();
        var room = harness.AddRoom("ABCD");
        for (var index = 0; index < DownloadJobChannel.Capacity; index++)
        {
            Assert.True(harness.Channel.TryEnqueue(NewJob()));
        }

        var ack = await harness.Service.EnqueueAsync(
            room,
            "Ada",
            sessionTokenId: null,
            Request(VideoId),
            CancellationToken.None);

        Assert.False(ack.Success);
        Assert.Equal(HubErrorCodes.QueueFull, ack.Reason);

        var row = Assert.Single(room.Queue);
        Assert.Equal(ack.QueueItemId, row.Id);
        Assert.Equal(QueueItemState.Failed, row.State);

        var failure = Assert.Single(harness.Hub.Client.DownloadFailedEvents);
        Assert.Equal(row.Id, failure.QueueItemId);
        Assert.Contains("queue is full", failure.Reason);

        // The rejected job never reached the worker, so its ticket is gone and the channel is
        // untouched. The harness has no database, so there is also no DownloadJobs row to leave
        // behind on this path (the API's no-connection-string fallback).
        Assert.Equal(0, harness.Coordinator.ActiveCount);
        Assert.Equal(DownloadJobChannel.Capacity, harness.Channel.Reader.Count);
    }

    [Fact]
    public async Task PerRoomCapRejectsTheFifthPendingDownloadButAnotherRoomStillEnqueues()
    {
        await using var harness = new IngestionHarness();
        var room = harness.AddRoom("ABCD");
        for (var index = 0; index < YouTubeIngestionService.MaxPendingDownloadsPerRoom; index++)
        {
            IngestionHarness.AddPendingDownload(room);
        }

        var rejected = await harness.Service.EnqueueAsync(
            room,
            "Ada",
            sessionTokenId: null,
            Request("roomCap0001"),
            CancellationToken.None);

        Assert.False(rejected.Success);
        Assert.Equal(HubErrorCodes.QueueFull, rejected.Reason);
        Assert.Equal(YouTubeIngestionService.MaxPendingDownloadsPerRoom, room.Queue.Count);
        Assert.Equal(0, harness.Coordinator.ActiveCount);

        var otherRoom = harness.AddRoom("EFGH");
        var accepted = await harness.Service.EnqueueAsync(
            otherRoom,
            "Ada",
            sessionTokenId: null,
            Request("roomCap0002"),
            CancellationToken.None);

        Assert.True(accepted.Success);
        var row = Assert.Single(otherRoom.Queue);
        Assert.Equal(QueueItemState.Downloading, row.State);
        Assert.Equal(1, harness.Coordinator.ActiveCount);
    }

    [Fact]
    public async Task CoalescedRequestBypassesTheRoomCapAndAttachesToThePendingTicket()
    {
        await using var harness = new IngestionHarness();
        var room = harness.AddRoom("ABCD");
        for (var index = 0; index < YouTubeIngestionService.MaxPendingDownloadsPerRoom; index++)
        {
            IngestionHarness.AddPendingDownload(room);
        }

        var ticket = harness.Coordinator.GetOrCreate(VideoId, Guid.NewGuid(), out var created);
        Assert.True(created);

        var ack = await harness.Service.EnqueueAsync(
            room,
            "Ada",
            sessionTokenId: null,
            Request(VideoId),
            CancellationToken.None);

        // The existing ticket means this request adds no download work, so the room cap does not
        // apply and no second channel entry is written.
        Assert.True(ack.Success);
        Assert.Equal(YouTubeIngestionService.MaxPendingDownloadsPerRoom + 1, room.Queue.Count);
        Assert.Contains(
            ticket.Targets,
            target => target.QueueItemId == ack.QueueItemId && target.RoomCode == room.Code);
        Assert.Same(ticket, harness.Coordinator.Find(VideoId));
        Assert.Equal(0, harness.Channel.Reader.Count);
    }

    [Fact]
    public async Task QueueFullCleanupFailsCoalescedTargetsInsteadOfLeavingThemDownloading()
    {
        var gate = new GateScopeFactory();
        await using var harness = new IngestionHarness(gate);
        var creatorRoom = harness.AddRoom("ABCD");
        var coalescedRoom = harness.AddRoom("EFGH");
        for (var index = 0; index < DownloadJobChannel.Capacity; index++)
        {
            Assert.True(harness.Channel.TryEnqueue(NewJob()));
        }

        // The creator is blocked inside TryQueueJobAsync, after its target is attached but before
        // the channel rejects the job. A second request then coalesces onto the same ticket.
        var creator = Task.Run(() => harness.Service.EnqueueAsync(
            creatorRoom,
            "Ada",
            sessionTokenId: null,
            Request(VideoId),
            CancellationToken.None));

        await gate.ThirdScopeReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var coalesced = await harness.Service.EnqueueAsync(
            coalescedRoom,
            "Bea",
            sessionTokenId: null,
            Request(VideoId),
            CancellationToken.None);
        Assert.True(coalesced.Success);

        gate.ReleaseThird.TrySetResult();
        var creatorAck = await creator.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(creatorAck.Success);
        Assert.Equal(HubErrorCodes.QueueFull, creatorAck.Reason);
        Assert.Equal(QueueItemState.Failed, Assert.Single(creatorRoom.Queue).State);
        Assert.Equal(QueueItemState.Failed, Assert.Single(coalescedRoom.Queue).State);

        Assert.Equal(2, harness.Hub.Client.DownloadFailedEvents.Count);
        Assert.Contains(
            harness.Hub.Client.DownloadFailedEvents,
            failure => failure.QueueItemId == coalesced.QueueItemId);
        Assert.Equal(0, harness.Coordinator.ActiveCount);
    }

    [Fact]
    public void FindParticipantBySessionTokenReturnsKnownAndUnknownResults()
    {
        var room = new RoomState(Guid.NewGuid(), "ABCD", DateTime.UtcNow);
        var tokenId = Guid.NewGuid();
        var participant = new RoomParticipant(
            "connection-1",
            "Ada",
            RoomRoles.Guest,
            tokenId.ToString("N"),
            tokenId);
        room.UpsertParticipant(participant);

        Assert.Same(participant, room.FindParticipantBySessionToken(tokenId));
        Assert.Null(room.FindParticipantBySessionToken(Guid.NewGuid()));
    }

    [Fact]
    public void ResolveSessionTokenIdFallsBackToNullForMalformedAndUnknownTokens()
    {
        var room = new RoomState(Guid.NewGuid(), "ABCD", DateTime.UtcNow);
        var tokenId = Guid.NewGuid();
        room.UpsertParticipant(new RoomParticipant(
            "connection-1",
            "Ada",
            RoomRoles.Guest,
            tokenId.ToString("N"),
            tokenId));

        Assert.Null(ResolveSessionTokenId(room, header: "not-a-guid"));
        Assert.Null(ResolveSessionTokenId(room, Guid.NewGuid().ToString("N")));
        Assert.Null(ResolveSessionTokenId(room, header: null));
        Assert.Equal(tokenId, ResolveSessionTokenId(room, tokenId.ToString("N")));
    }

    private static Guid? ResolveSessionTokenId(RoomState room, string? header)
    {
        var context = new DefaultHttpContext();
        if (header is not null)
        {
            context.Request.Headers[YouTubeHttpHeaders.SessionToken] = header;
        }

        return YouTubeEndpoints.ResolveSessionTokenId(context, room, NullLogger.Instance);
    }

    private static YouTubeQueueRequest Request(string videoId) =>
        new(videoId, "Queue Test", "Karaokay CI", 60);

    private static DownloadJob NewJob() => new()
    {
        Id = Guid.NewGuid(),
        SongId = Guid.NewGuid(),
        VideoId = VideoId,
        Stage = DownloadStage.Queued,
        QueueItemId = Guid.NewGuid(),
        CreatedAtUtc = DateTime.UtcNow,
    };

    private sealed class IngestionHarness : IAsyncDisposable
    {
        public IngestionHarness(IServiceScopeFactory? scopeFactory = null)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "karaokay-ingest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            ScopeFactory = scopeFactory ?? new NoDatabaseScopeFactory();
            Library = new MediaLibrary(
                Options.Create(new MediaLibraryOptions { MediaRoot = Root }),
                new FakeHostEnvironment(Root),
                NullLogger<MediaLibrary>.Instance);
            Queue = new QueueService(
                ScopeFactory,
                new SongCatalog(ScopeFactory, Library),
                Hub,
                NullLogger<QueueService>.Instance);
            Service = new YouTubeIngestionService(
                ScopeFactory,
                YouTube,
                Channel,
                Coordinator,
                Registry,
                Queue,
                Library,
                Hub,
                Options.Create(new FeatureOptions { EnableYouTubeIngestion = true }),
                Options.Create(new YtDlpOptions
                {
                    StagingRoot = ".staging",
                    CacheDirectory = "ytcache",
                }),
                NullLogger<YouTubeIngestionService>.Instance);
        }

        public string Root { get; }

        public DownloadJobChannel Channel { get; } = new();

        public DownloadJobCoordinator Coordinator { get; } = new();

        public RoomRegistry Registry { get; } = new();

        public StubYouTubeService YouTube { get; } = new();

        public RecordingHubContext Hub { get; } = new();

        public IServiceScopeFactory ScopeFactory { get; }

        public MediaLibrary Library { get; }

        public QueueService Queue { get; }

        public YouTubeIngestionService Service { get; }

        public RoomState AddRoom(string code)
        {
            var room = new RoomState(Guid.NewGuid(), code, DateTime.UtcNow);
            Registry.TryAdd(room);
            return room;
        }

        public static void AddPendingDownload(RoomState room)
        {
            room.QueueEntries.Add(new RoomQueueEntry
            {
                Id = Guid.NewGuid(),
                SongId = Guid.NewGuid(),
                Position = room.QueueEntries.Count,
                RequestedByName = "Ada",
                State = QueueItemState.Downloading,
                RequestedAtUtc = DateTime.UtcNow,
            });
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubYouTubeService : IYouTubeService
    {
        public Task<IReadOnlyList<YouTubeSearchResultDto>> SearchAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<YouTubeSearchResultDto>>([]);

        public Task<YouTubeDownloadResult> DownloadAsync(
            string videoId,
            string stagingDirectory,
            IProgress<DownloadProgressInfo>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Admission tests never reach the download worker.");
    }

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

    /// <summary>
    /// Blocks the creator's TryQueueJobAsync scope request (the third: song lookup, queue persist,
    /// then job queue) until the test has attached a coalesced request, making the queue-full
    /// cleanup race deterministic.
    /// </summary>
    private sealed class GateScopeFactory : IServiceScopeFactory
    {
        private readonly NoDatabaseScopeFactory _inner = new();
        private int _scopeCount;

        public TaskCompletionSource ThirdScopeReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseThird { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _scopeCount) == 3)
            {
                ThirdScopeReached.TrySetResult();
                ReleaseThird.Task.GetAwaiter().GetResult();
            }

            return _inner.CreateScope();
        }
    }

    private sealed class FakeHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "Karaokay.UnitTests";

        public string ContentRootPath { get; set; } = contentRoot;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class RecordingHubContext : IHubContext<StageHub, IStageHubClient>
    {
        public RecordingClient Client { get; } = new();

        public IHubClients<IStageHubClient> Clients => new RecordingHubClients(Client);

        public IGroupManager Groups { get; } = new NoopGroupManager();
    }

    private sealed class RecordingHubClients(RecordingClient client) : IHubClients<IStageHubClient>
    {
        public IStageHubClient All => client;

        public IStageHubClient Client(string connectionId) => client;

        public IStageHubClient Clients(IReadOnlyList<string> connectionIds) => client;

        public IStageHubClient Group(string groupName) => client;

        public IStageHubClient Groups(IReadOnlyList<string> groupNames) => client;

        public IStageHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => client;

        public IStageHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            client;

        public IStageHubClient User(string userId) => client;

        public IStageHubClient Users(IReadOnlyList<string> userIds) => client;
    }

    private sealed class RecordingClient : IStageHubClient
    {
        public ConcurrentQueue<DownloadFailedDto> DownloadFailedEvents { get; } = new();

        public Task JoinedRoom(RoomSnapshot snapshot) => Task.CompletedTask;

        public Task ParticipantJoined(string displayName, int guestCount) => Task.CompletedTask;

        public Task ParticipantLeft(string displayName, int guestCount) => Task.CompletedTask;

        public Task QueueChanged(QueueDelta delta) => Task.CompletedTask;

        public Task QueueItemStateChanged(
            Guid queueItemId,
            Karaokay.Contracts.Hubs.QueueItemState state) => Task.CompletedTask;

        public Task DownloadProgressChanged(DownloadProgressDto dto) => Task.CompletedTask;

        public Task SongReady(SongReadyDto dto) => Task.CompletedTask;

        public Task DownloadFailed(DownloadFailedDto dto)
        {
            DownloadFailedEvents.Enqueue(dto);
            return Task.CompletedTask;
        }

        public Task PlaybackChanged(PlaybackStateDto dto) => Task.CompletedTask;

        public Task CountdownTicked(CountdownDto dto) => Task.CompletedTask;

        public Task LyricsLoaded(Guid songId, LyricsDto lyricsRef) => Task.CompletedTask;

        public Task SearchCompleted(
            string correlationId,
            IReadOnlyList<YouTubeSearchResultDto> results) => Task.CompletedTask;

        public Task RoomClosed(string reason) => Task.CompletedTask;

        public Task HubError(HubErrorDto hubError) => Task.CompletedTask;
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
