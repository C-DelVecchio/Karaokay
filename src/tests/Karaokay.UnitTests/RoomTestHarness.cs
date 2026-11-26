using Karaokay.Api.Features;
using Karaokay.Api.Hubs;
using Karaokay.Contracts.Hubs;
using Karaokay.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QueueItemState = Karaokay.Domain.QueueItemState;
using WireMediaSource = Karaokay.Contracts.Hubs.MediaSource;
using WireQueueItemState = Karaokay.Contracts.Hubs.QueueItemState;

namespace Karaokay.UnitTests;

/// <summary>
/// Shared in-memory doubles for service-layer tests (no database, no SignalR transport). The queue
/// service degrades to memory when the scope factory resolves no <c>KaraokayDbContext</c>, which is
/// exactly the hermetic path these tests exercise.
/// </summary>
internal static class RoomTestHarness
{
    public static (QueueService Service, RecordingHubClient Client, FakeHubContext Hub) CreateQueueService(
        TimeProvider? timeProvider = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        var client = new RecordingHubClient();
        var hub = new FakeHubContext(client);
        scopeFactory ??= new NoDatabaseScopeFactory();
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
            hub,
            NullLogger<QueueService>.Instance,
            timeProvider ?? TimeProvider.System);

        return (service, client, hub);
    }

    public static RoomState CreateRoom(string code = "ABCD") =>
        new(Guid.NewGuid(), code, DateTime.UtcNow);

    public static RoomQueueEntry AddEntry(
        RoomState room,
        QueueItemState state,
        int position,
        string name = "Song",
        long durationMs = 1_000)
    {
        var entry = new RoomQueueEntry
        {
            Id = Guid.NewGuid(),
            SongId = Guid.NewGuid(),
            Position = position,
            RequestedByName = "Ada",
            State = state,
            RequestedAtUtc = DateTime.UtcNow,
            Song = new SongDto(Guid.NewGuid(), name, "Artist", durationMs, WireMediaSource.Multitrack, null),
        };

        room.QueueEntries.Add(entry);
        return entry;
    }

    public static RoomCommand Command(
        string type,
        string roomCode,
        Guid? queueItemId = null,
        double? volume = null) => new()
        {
            Type = type,
            RoomCode = roomCode,
            ConnectionId = "test-connection",
            QueueItemId = queueItemId,
            Volume = volume,
        };
}

/// <summary>Scope factory with no <c>KaraokayDbContext</c>, so persistence degrades to memory.</summary>
internal sealed class NoDatabaseScopeFactory : IServiceScopeFactory
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
/// Scope factory backed by one real <see cref="KaraokayDbContext"/> (an EF InMemory store in tests),
/// so persistence assertions can read back exactly what <c>SaveChangesAsync</c> wrote. The scope
/// never disposes the supplied context; the test owns it.
/// </summary>
internal sealed class FixedDatabaseScopeFactory(KaraokayDbContext database) : IServiceScopeFactory
{
    public IServiceScope CreateScope() => new FixedScope(database);

    private sealed class FixedScope : IServiceScope, IServiceProvider
    {
        private readonly KaraokayDbContext _database;

        public FixedScope(KaraokayDbContext database) => _database = database;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(KaraokayDbContext) ? _database : null;

        public void Dispose()
        {
        }
    }
}

/// <summary>Deterministic wall clock for countdown/watchdog tests.</summary>
internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;

    public string ApplicationName { get; set; } = "Karaokay.UnitTests";

    public string ContentRootPath { get; set; } = Path.GetTempPath();

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class FakeHubContext : IHubContext<StageHub, IStageHubClient>
{
    public FakeHubContext(IStageHubClient client) =>
        Clients = new FakeHubClients(client, groupName => LastGroupName = groupName);

    public IHubClients<IStageHubClient> Clients { get; }

    public IGroupManager Groups => throw new NotSupportedException();

    /// <summary>Last group targeted by a broadcast; proves events stay room-scoped.</summary>
    public string? LastGroupName { get; private set; }
}

internal sealed class FakeHubClients : IHubClients<IStageHubClient>
{
    private readonly IStageHubClient _client;
    private readonly Action<string> _onGroup;

    public FakeHubClients(IStageHubClient client, Action<string> onGroup)
    {
        _client = client;
        _onGroup = onGroup;
    }

    public IStageHubClient All => _client;

    public IStageHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => _client;

    public IStageHubClient Client(string connectionId) => _client;

    public IStageHubClient Clients(IReadOnlyList<string> connectionIds) => _client;

    public IStageHubClient Group(string groupName)
    {
        _onGroup(groupName);
        return _client;
    }

    public IStageHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
        _client;

    public IStageHubClient Groups(IReadOnlyList<string> groupNames) => _client;

    public IStageHubClient User(string userId) => _client;

    public IStageHubClient Users(IReadOnlyList<string> userIds) => _client;
}

/// <summary>Records every server → client broadcast the services emit.</summary>
internal sealed class RecordingHubClient : IStageHubClient
{
    public List<string> Events { get; } = [];

    public List<PlaybackStateDto> PlaybackStates { get; } = [];

    public List<(Guid QueueItemId, WireQueueItemState State)> QueueItemStates { get; } = [];

    public List<CountdownDto> CountdownTicks { get; } = [];

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

    public Task CountdownTicked(CountdownDto dto)
    {
        CountdownTicks.Add(dto);
        return Record(nameof(CountdownTicked));
    }

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
