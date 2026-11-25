using System.Collections.Concurrent;
using Karaokay.Api.Features;
using Karaokay.Api.Hubs;
using Karaokay.Application.Downloads;
using Karaokay.Contracts.Hubs;
using Karaokay.Domain;
using Karaokay.Infrastructure.MediaIngest;
using Karaokay.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using DownloadStage = Karaokay.Domain.DownloadStage;
using QueueItemState = Karaokay.Domain.QueueItemState;

namespace Karaokay.UnitTests;

/// <summary>
/// Drives <see cref="MediaDownloadWorker"/> through its real hosted-service loop with a fake
/// <see cref="IYouTubeService"/> (docs/ARCHITECTURE.md §5.1/§7, Slice 5). No process, no network,
/// and no database: the persistence scope intentionally resolves no <c>KaraokayDbContext</c>, which
/// is the same graceful-degradation path the API uses when no connection string is configured.
/// </summary>
public sealed class MediaDownloadWorkerTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    [Fact]
    public void DefaultMaxAttemptsMeansTwoRetries()
    {
        // docs/ARCHITECTURE.md §4.2: "retry up to 2 with backoff". MaxAttempts counts the total
        // attempts, so the initial try + two retries = 3.
        Assert.Equal(3, new YtDlpOptions().MaxAttempts);
    }

    [Fact]
    public void PersistedCachePathIsRelativeToTheConfiguredCacheDirectory()
    {
        // Convention: Song.CachePath for YouTube songs stores the file path relative to
        // YtDlp.CacheDirectory (a second trusted root), not relative to MediaRoot. The worker's
        // persistence path uses this helper, so a cache outside the media root still resolves.
        var cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "karaokay-cache-" + Guid.NewGuid().ToString("N"));

        var stored = MediaDownloadWorker.CachePathForStorage(
            Path.Combine(cacheDirectory, VideoId + ".mp4"),
            cacheDirectory);

        Assert.Equal(VideoId + ".mp4", stored);
        Assert.DoesNotContain("..", stored, StringComparison.Ordinal);
        Assert.False(Path.IsPathRooted(stored));
    }

    [Fact]
    public async Task HappyPathMovesTheFileAndBroadcastsProgressAndSongReady()
    {
        await using var harness = new WorkerHarness();
        var queueItem = harness.AddQueueItem();
        var (job, ticket) = harness.PrepareJob(queueItem.Id);
        await harness.Worker.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(harness.Channel.TryEnqueue(job));

            var outcome = await ticket.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(outcome.Success);
            Assert.Null(outcome.FailureReason);

            var cacheFile = Path.Combine(harness.Root, "ytcache", VideoId + ".mp4");
            Assert.True(File.Exists(cacheFile));
            Assert.Equal(FakeYouTubeService.SampleVideoBytes.Length, new FileInfo(cacheFile).Length);

            Assert.Equal(DownloadStage.Completed, job.Stage);
            Assert.Equal(100, job.ProgressPercent);
            Assert.NotNull(job.CompletedAtUtc);
            Assert.Null(job.FailureReason);

            var progresses = harness.Hub.Client.DownloadProgressEvents
                .Where(dto => dto.QueueItemId == queueItem.Id)
                .Select(dto => dto.Percent)
                .ToArray();
            Assert.Contains(25, progresses);
            Assert.Contains(50, progresses);
            Assert.Contains(75, progresses);
            Assert.Contains(100, progresses);

            var ready = Assert.Single(
                harness.Hub.Client.SongReadyEvents,
                dto => dto.QueueItemId == queueItem.Id);
            Assert.Equal(job.SongId, ready.SongId);
            Assert.Empty(harness.Hub.Client.DownloadFailedEvents);
            await WaitForQueueStateAsync(harness.Room, queueItem.Id, QueueItemState.Ready);

            // The per-attempt staging directory (including .part leftovers) is always deleted.
            Assert.Empty(harness.StagingDirectories());
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task FailureIsRetriedTwiceThenMarksTheJobAndQueueRowFailed()
    {
        await using var harness = new WorkerHarness();
        harness.YouTube.FailuresBeforeSuccess = int.MaxValue;
        var queueItem = harness.AddQueueItem();
        var (job, ticket) = harness.PrepareJob(queueItem.Id);
        await harness.Worker.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(harness.Channel.TryEnqueue(job));

            var outcome = await ticket.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(outcome.Success);
            Assert.Contains("simulated yt-dlp failure", outcome.FailureReason);

            Assert.Equal(DownloadStage.Failed, job.Stage);
            // Default MaxAttempts = 3: one initial attempt plus the two retries required by
            // docs/ARCHITECTURE.md §4.2 and §7.
            Assert.Equal(3, job.AttemptCount);
            Assert.Equal(3, harness.YouTube.Attempts);
            Assert.NotNull(job.CompletedAtUtc);
            Assert.Contains("simulated yt-dlp failure", job.FailureReason);

            Assert.False(File.Exists(Path.Combine(harness.Root, "ytcache", VideoId + ".mp4")));
            Assert.Empty(harness.StagingDirectories());

            var failed = Assert.Single(
                harness.Hub.Client.DownloadFailedEvents,
                dto => dto.QueueItemId == queueItem.Id);
            Assert.Contains("simulated yt-dlp failure", failed.Reason);
            Assert.Empty(harness.Hub.Client.SongReadyEvents);
            await WaitForQueueStateAsync(harness.Room, queueItem.Id, QueueItemState.Failed);
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ATransientFailureIsRetriedAndSucceedsOnTheSecondAttempt()
    {
        await using var harness = new WorkerHarness();
        harness.YouTube.FailuresBeforeSuccess = 1;
        var queueItem = harness.AddQueueItem();
        var (job, ticket) = harness.PrepareJob(queueItem.Id);
        await harness.Worker.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(harness.Channel.TryEnqueue(job));

            var outcome = await ticket.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(outcome.Success);
            Assert.Equal(2, harness.YouTube.Attempts);
            Assert.Equal(2, job.AttemptCount);
            Assert.True(File.Exists(Path.Combine(harness.Root, "ytcache", VideoId + ".mp4")));
            Assert.Empty(harness.StagingDirectories());
            Assert.Single(harness.Hub.Client.SongReadyEvents);
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CancellationMidDownloadCleansTheStagingDirectory()
    {
        await using var harness = new WorkerHarness();
        harness.YouTube.BlockUntilCancelled = true;
        var queueItem = harness.AddQueueItem();
        var (job, ticket) = harness.PrepareJob(queueItem.Id);
        await harness.Worker.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(harness.Channel.TryEnqueue(job));
            await harness.YouTube.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await harness.Worker.StopAsync(CancellationToken.None);

            Assert.False(ticket.Completion.Task.IsCompleted);
            Assert.False(File.Exists(Path.Combine(harness.Root, "ytcache", VideoId + ".mp4")));
            Assert.Empty(harness.StagingDirectories());
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CodecMismatchFailsTheJobWithoutRetrying()
    {
        await using var harness = new WorkerHarness();
        harness.CodecProbe.Result = new CodecProbeResult(
            CodecProbeOutcome.Mismatch,
            "Expected H.264/AAC but found video=[vp9] audio=[opus].");
        var queueItem = harness.AddQueueItem();
        var (job, ticket) = harness.PrepareJob(queueItem.Id);
        await harness.Worker.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(harness.Channel.TryEnqueue(job));

            var outcome = await ticket.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(outcome.Success);
            Assert.Contains("not H.264/AAC", outcome.FailureReason);
            Assert.Equal(1, harness.YouTube.Attempts);
            Assert.Equal(DownloadStage.Failed, job.Stage);
            Assert.False(File.Exists(Path.Combine(harness.Root, "ytcache", VideoId + ".mp4")));
            Assert.Empty(harness.StagingDirectories());

            var failed = Assert.Single(harness.Hub.Client.DownloadFailedEvents);
            Assert.Contains("vp9", failed.Reason);
            Assert.Empty(harness.Hub.Client.SongReadyEvents);
            await WaitForQueueStateAsync(harness.Room, queueItem.Id, QueueItemState.Failed);
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task UnavailableCodecProbeDegradesToReady()
    {
        await using var harness = new WorkerHarness();
        harness.CodecProbe.Result = new CodecProbeResult(
            CodecProbeOutcome.Unavailable,
            "ffprobe is not installed");
        var queueItem = harness.AddQueueItem();
        var (job, ticket) = harness.PrepareJob(queueItem.Id);
        await harness.Worker.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(harness.Channel.TryEnqueue(job));

            var outcome = await ticket.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(outcome.Success);
            Assert.True(File.Exists(Path.Combine(harness.Root, "ytcache", VideoId + ".mp4")));
            // SongReady and the queue-state update are broadcast after ticket completion, so wait
            // for the row instead of racing the worker.
            await WaitForQueueStateAsync(harness.Room, queueItem.Id, QueueItemState.Ready);
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForQueueStateAsync(
        RoomState room,
        Guid queueItemId,
        QueueItemState expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (room.FindQueueEntry(queueItemId)?.State == expected)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"Timed out waiting for queue item {queueItemId} to reach {expected}.");
    }

    private sealed class WorkerHarness : IAsyncDisposable
    {
        public WorkerHarness(int maxAttempts = 3)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "karaokay-worker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            Channel = new DownloadJobChannel();
            Coordinator = new DownloadJobCoordinator();
            YouTube = new FakeYouTubeService();
            CodecProbe = new StubMediaCodecProbe();
            Registry = new RoomRegistry();
            Hub = new RecordingHubContext();
            ScopeFactory = new NoDatabaseScopeFactory();

            var library = new MediaLibrary(
                Options.Create(new MediaLibraryOptions { MediaRoot = Root }),
                new FakeHostEnvironment(Root),
                NullLogger<MediaLibrary>.Instance);
            var catalog = new SongCatalog(ScopeFactory, library);
            var queue = new QueueService(
                ScopeFactory,
                catalog,
                Hub,
                NullLogger<QueueService>.Instance);

            Room = new RoomState(Guid.NewGuid(), "ABCD", DateTime.UtcNow);
            Registry.TryAdd(Room);

            Worker = new MediaDownloadWorker(
                Channel,
                Coordinator,
                YouTube,
                CodecProbe,
                ScopeFactory,
                Registry,
                queue,
                Hub,
                library,
                Options.Create(new YtDlpOptions
                {
                    StagingRoot = ".staging",
                    CacheDirectory = "ytcache",
                    MaxAttempts = maxAttempts,
                    RetryBackoffSeconds = 0,
                }),
                NullLogger<MediaDownloadWorker>.Instance);
        }

        public string Root { get; }

        public DownloadJobChannel Channel { get; }

        public DownloadJobCoordinator Coordinator { get; }

        public FakeYouTubeService YouTube { get; }

        public StubMediaCodecProbe CodecProbe { get; }

        public RoomRegistry Registry { get; }

        public RecordingHubContext Hub { get; }

        public NoDatabaseScopeFactory ScopeFactory { get; }

        public RoomState Room { get; }

        public MediaDownloadWorker Worker { get; }

        public RoomQueueEntry AddQueueItem()
        {
            var entry = new RoomQueueEntry
            {
                Id = Guid.NewGuid(),
                SongId = Guid.NewGuid(),
                Position = Room.QueueEntries.Count,
                RequestedByName = "Ada",
                State = QueueItemState.Downloading,
                RequestedAtUtc = DateTime.UtcNow,
            };

            Room.QueueEntries.Add(entry);
            return entry;
        }

        public (DownloadJob Job, DownloadJobTicket Ticket) PrepareJob(Guid queueItemId)
        {
            var songId = Room.FindQueueEntry(queueItemId)!.SongId;
            var job = new DownloadJob
            {
                Id = Guid.NewGuid(),
                SongId = songId,
                VideoId = VideoId,
                Stage = DownloadStage.Queued,
                QueueItemId = queueItemId,
                CreatedAtUtc = DateTime.UtcNow,
            };

            var ticket = Coordinator.GetOrCreate(VideoId, songId, out _);
            Assert.True(ticket.TryAddTarget(new DownloadTarget(queueItemId, Room.Code)));
            return (job, ticket);
        }

        public string[] StagingDirectories()
        {
            var staging = Path.Combine(Root, ".staging");
            return Directory.Exists(staging)
                ? Directory.EnumerateDirectories(staging).ToArray()
                : [];
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();

            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; the OS reclaims it later.
            }
        }
    }

    private sealed class FakeYouTubeService : IYouTubeService
    {
        public static readonly byte[] SampleVideoBytes = LoadSampleVideo();

        private int _attempts;

        public int FailuresBeforeSuccess { get; set; }

        public bool BlockUntilCancelled { get; set; }

        public int Attempts => Volatile.Read(ref _attempts);

        public TaskCompletionSource<bool> DownloadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<YouTubeSearchResultDto>> SearchAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<YouTubeSearchResultDto>>([]);

        public async Task<YouTubeDownloadResult> DownloadAsync(
            string videoId,
            string stagingDirectory,
            IProgress<DownloadProgressInfo>? progress,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            Directory.CreateDirectory(stagingDirectory);

            // A partial file is exactly what a cancelled/failed yt-dlp run leaves behind.
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, videoId + ".mp4.part"),
                "partial-download",
                cancellationToken);

            if (BlockUntilCancelled)
            {
                DownloadStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (attempt <= FailuresBeforeSuccess)
            {
                throw new YouTubeServiceException("simulated yt-dlp failure");
            }

            var output = Path.Combine(stagingDirectory, videoId + ".mp4");
            await File.WriteAllBytesAsync(output, SampleVideoBytes, cancellationToken);

            foreach (var percent in new[] { 25.0, 50.0, 75.0, 100.0 })
            {
                progress?.Report(new DownloadProgressInfo(percent, 1_000_000, 5));
                await Task.Delay(10, cancellationToken);
            }

            return new YouTubeDownloadResult(output, SampleVideoBytes.Length);
        }

        private static byte[] LoadSampleVideo()
        {
            var path = Path.Combine(
                TestPaths.RepoRoot,
                "src",
                "tests",
                "fixtures",
                "youtube-sample.mp4.b64");
            return Convert.FromBase64String(File.ReadAllText(path).Trim());
        }
    }

    private sealed class StubMediaCodecProbe : IMediaCodecProbe
    {
        public CodecProbeResult Result { get; set; } =
            new(CodecProbeOutcome.Match, "stubbed H.264/AAC");

        public Task<CodecProbeResult> ProbeAsync(string file, CancellationToken cancellationToken) =>
            Task.FromResult(Result);
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
        public ConcurrentQueue<DownloadProgressDto> DownloadProgressEvents { get; } = new();

        public ConcurrentQueue<SongReadyDto> SongReadyEvents { get; } = new();

        public ConcurrentQueue<DownloadFailedDto> DownloadFailedEvents { get; } = new();

        public Task JoinedRoom(RoomSnapshot snapshot) => Task.CompletedTask;

        public Task ParticipantJoined(string displayName, int guestCount) => Task.CompletedTask;

        public Task ParticipantLeft(string displayName, int guestCount) => Task.CompletedTask;

        public Task QueueChanged(QueueDelta delta) => Task.CompletedTask;

        public Task QueueItemStateChanged(
            Guid queueItemId,
            Karaokay.Contracts.Hubs.QueueItemState state) => Task.CompletedTask;

        public Task DownloadProgressChanged(DownloadProgressDto dto)
        {
            DownloadProgressEvents.Enqueue(dto);
            return Task.CompletedTask;
        }

        public Task SongReady(SongReadyDto dto)
        {
            SongReadyEvents.Enqueue(dto);
            return Task.CompletedTask;
        }

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
