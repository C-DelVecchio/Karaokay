using System.Threading.Channels;
using Karaokay.Api.Hubs;
using Karaokay.Application.Downloads;
using Karaokay.Contracts.Hubs;
using Karaokay.Domain;
using Karaokay.Infrastructure.MediaIngest;
using Karaokay.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DownloadStage = Karaokay.Domain.DownloadStage;
using MediaSource = Karaokay.Domain.MediaSource;
using QueueItemState = Karaokay.Domain.QueueItemState;

namespace Karaokay.Api.Features;

/// <summary>
/// Background YouTube ingestion worker (docs/ARCHITECTURE.md §5.1/§7, Slice 5). Reads the bounded
/// <see cref="DownloadJobChannel"/>, drives yt-dlp into a staging directory, atomically moves the
/// finished MP4 into the configured <c>YtDlp.CacheDirectory</c>
/// (<c>{videoId}.mp4</c>), persists song/job state, and broadcasts progress, <c>SongReady</c>, and
/// <c>DownloadFailed</c> to the owning room group.
/// </summary>
public sealed partial class MediaDownloadWorker : BackgroundService
{
    private readonly DownloadJobChannel _queue;
    private readonly DownloadJobCoordinator _coordinator;
    private readonly IYouTubeService _youtube;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RoomRegistry _registry;
    private readonly QueueService _roomQueue;
    private readonly IHubContext<StageHub, IStageHubClient> _hub;
    private readonly MediaLibrary _library;
    private readonly YtDlpOptions _options;
    private readonly ILogger<MediaDownloadWorker> _logger;

    public MediaDownloadWorker(
        DownloadJobChannel queue,
        DownloadJobCoordinator coordinator,
        IYouTubeService youtube,
        IServiceScopeFactory scopeFactory,
        RoomRegistry registry,
        QueueService roomQueue,
        IHubContext<StageHub, IStageHubClient> hub,
        MediaLibrary library,
        IOptions<YtDlpOptions> options,
        ILogger<MediaDownloadWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _queue = queue;
        _coordinator = coordinator;
        _youtube = youtube;
        _scopeFactory = scopeFactory;
        _registry = registry;
        _roomQueue = roomQueue;
        _hub = hub;
        _library = library;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(_logger);

        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogJobCrashed(_logger, job.VideoId, exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: the channel reader is drained by ReadAllAsync cancellation.
        }

        LogWorkerStopped(_logger);
    }

    private async Task ProcessJobAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var ticket = _coordinator.Find(job.VideoId);
        if (ticket is null || ticket.Result is not null)
        {
            ticket = _coordinator.GetOrCreate(job.VideoId, job.SongId, out _);
        }

        var cacheFile = CacheFilePath(job.VideoId);
        if (File.Exists(cacheFile))
        {
            LogCacheHit(_logger, job.VideoId);
            await CompleteSuccessAsync(
                    job,
                    ticket,
                    new YouTubeDownloadResult(cacheFile, new FileInfo(cacheFile).Length),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var failureReason = "YouTube download failed.";
        var maxAttempts = Math.Max(1, _options.MaxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            job.AttemptCount = attempt;
            var stagingDirectory = Path.Combine(
                ResolveDirectory(_options.StagingRoot),
                $"{job.VideoId}-{Guid.NewGuid():N}");

            try
            {
                Directory.CreateDirectory(stagingDirectory);
                job.Stage = DownloadStage.Resolving;
                job.ProgressPercent = 0;
                await PersistJobAsync(job, cancellationToken).ConfigureAwait(false);
                await BroadcastProgressAsync(job, ticket, cancellationToken).ConfigureAwait(false);

                var result = await DownloadWithProgressAsync(job, ticket, stagingDirectory, cancellationToken)
                    .ConfigureAwait(false);

                job.Stage = DownloadStage.Transcoding;
                await PersistJobAsync(job, cancellationToken).ConfigureAwait(false);
                await BroadcastProgressAsync(job, ticket, cancellationToken).ConfigureAwait(false);

                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                File.Move(result.FilePath, cacheFile, overwrite: true);

                await CompleteSuccessAsync(
                        job,
                        ticket,
                        new YouTubeDownloadResult(cacheFile, new FileInfo(cacheFile).Length),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failureReason = exception.Message;
                LogAttemptFailed(_logger, job.VideoId, attempt, maxAttempts, exception);

                if (attempt < maxAttempts)
                {
                    await Task.Delay(
                            TimeSpan.FromSeconds(Math.Max(0, _options.RetryBackoffSeconds)),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }

        await CompleteFailureAsync(job, ticket, failureReason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<YouTubeDownloadResult> DownloadWithProgressAsync(
        DownloadJob job,
        DownloadJobTicket ticket,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var progressChannel = Channel.CreateUnbounded<DownloadProgressInfo>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        var pump = PumpProgressAsync(job, ticket, progressChannel.Reader, cancellationToken);

        try
        {
            return await _youtube
                .DownloadAsync(
                    job.VideoId,
                    stagingDirectory,
                    new ChannelProgress(progressChannel.Writer),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            progressChannel.Writer.TryComplete();
            await pump.ConfigureAwait(false);
        }
    }

    private async Task PumpProgressAsync(
        DownloadJob job,
        DownloadJobTicket ticket,
        ChannelReader<DownloadProgressInfo> reader,
        CancellationToken cancellationToken)
    {
        var lastBroadcast = DateTime.MinValue;
        var lastPercent = -1.0;

        await foreach (var sample in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = DateTime.UtcNow;
            var shouldBroadcast = sample.Percent >= 100
                || Math.Abs(sample.Percent - lastPercent) >= 1
                || now - lastBroadcast >= TimeSpan.FromMilliseconds(250);

            if (!shouldBroadcast)
            {
                continue;
            }

            lastBroadcast = now;
            lastPercent = sample.Percent;
            job.ProgressPercent = sample.Percent;

            if (job.Stage is DownloadStage.Queued or DownloadStage.Resolving)
            {
                job.Stage = DownloadStage.Downloading;
                await PersistJobAsync(job, cancellationToken).ConfigureAwait(false);
            }

            var snapshot = new DownloadProgressSnapshot(
                DownloadStage.Downloading,
                sample.Percent,
                sample.BytesPerSec,
                sample.EtaSec);
            ticket.Report(snapshot);

            foreach (var target in ResolveTargets(ticket, job))
            {
                var room = ResolveRoom(target);
                if (room is null)
                {
                    continue;
                }

                await _hub.Clients.Group(room.Code)
                    .DownloadProgressChanged(new DownloadProgressDto(
                        target.QueueItemId,
                        job.SongId,
                        ToWireStage(snapshot.Stage),
                        snapshot.Percent,
                        snapshot.BytesPerSec,
                        snapshot.EtaSec))
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task CompleteSuccessAsync(
        DownloadJob job,
        DownloadJobTicket ticket,
        YouTubeDownloadResult result,
        CancellationToken cancellationToken)
    {
        var cacheRelative = CachePathForStorage(
            result.FilePath,
            YouTubeCachePaths.ResolveDirectory(_options, _library.RootPath));

        job.Stage = DownloadStage.Completed;
        job.ProgressPercent = 100;
        job.FailureReason = null;
        job.CompletedAtUtc = DateTime.UtcNow;
        await PersistJobAsync(job, cancellationToken).ConfigureAwait(false);
        await PersistSongAsync(job, cacheRelative, result.FileSizeBytes, cancellationToken)
            .ConfigureAwait(false);

        var targets = ticket.Complete(new DownloadJobResult(true, null));
        _coordinator.TryRemove(ticket);

        if (targets.Count == 0)
        {
            targets = FallbackTargets(job);
        }

        foreach (var target in targets)
        {
            var room = ResolveRoom(target);
            if (room is null)
            {
                continue;
            }

            await _hub.Clients.Group(room.Code)
                .DownloadProgressChanged(new DownloadProgressDto(
                    target.QueueItemId,
                    job.SongId,
                    ToWireStage(DownloadStage.Completed),
                    100,
                    0,
                    0))
                .ConfigureAwait(false);

            await _roomQueue
                .UpdateQueueItemStateAsync(room, target.QueueItemId, QueueItemState.Ready, cancellationToken)
                .ConfigureAwait(false);

            await _hub.Clients.Group(room.Code)
                .SongReady(new SongReadyDto(target.QueueItemId, job.SongId))
                .ConfigureAwait(false);
        }

        LogJobCompleted(_logger, job.VideoId, result.FileSizeBytes);
    }

    private async Task CompleteFailureAsync(
        DownloadJob job,
        DownloadJobTicket ticket,
        string reason,
        CancellationToken cancellationToken)
    {
        job.Stage = DownloadStage.Failed;
        job.FailureReason = Truncate(reason, 1024);
        job.CompletedAtUtc = DateTime.UtcNow;
        await PersistJobAsync(job, cancellationToken).ConfigureAwait(false);

        var targets = ticket.Complete(new DownloadJobResult(false, job.FailureReason));
        _coordinator.TryRemove(ticket);

        if (targets.Count == 0)
        {
            targets = FallbackTargets(job);
        }

        foreach (var target in targets)
        {
            var room = ResolveRoom(target);
            if (room is null)
            {
                continue;
            }

            await _roomQueue
                .UpdateQueueItemStateAsync(room, target.QueueItemId, QueueItemState.Failed, cancellationToken)
                .ConfigureAwait(false);

            await _hub.Clients.Group(room.Code)
                .DownloadFailed(new DownloadFailedDto(
                    target.QueueItemId,
                    job.SongId,
                    reason))
                .ConfigureAwait(false);
        }

        LogJobFailed(_logger, job.VideoId, reason);
    }

    private async Task BroadcastProgressAsync(
        DownloadJob job,
        DownloadJobTicket ticket,
        CancellationToken cancellationToken)
    {
        foreach (var target in ResolveTargets(ticket, job))
        {
            var room = ResolveRoom(target);
            if (room is null)
            {
                continue;
            }

            await _hub.Clients.Group(room.Code)
                .DownloadProgressChanged(new DownloadProgressDto(
                    target.QueueItemId,
                    job.SongId,
                    ToWireStage(job.Stage),
                    job.ProgressPercent,
                    0,
                    0))
                .ConfigureAwait(false);
        }
    }

    private IReadOnlyList<DownloadTarget> ResolveTargets(DownloadJobTicket ticket, DownloadJob job)
    {
        var attached = ticket.Targets;
        return attached.Count > 0 ? attached : FallbackTargets(job);
    }

    private List<DownloadTarget> FallbackTargets(DownloadJob job)
    {
        var targets = new List<DownloadTarget>();
        if (job.QueueItemId is not { } queueItemId)
        {
            return targets;
        }

        var room = _registry.FindByQueueItemId(queueItemId);
        if (room is not null)
        {
            targets.Add(new DownloadTarget(queueItemId, room.Code));
            return targets;
        }

        foreach (var candidate in _registry.ActiveCodes)
        {
            var state = _registry.Find(candidate);
            if (state?.FindQueueEntry(queueItemId) is not null)
            {
                targets.Add(new DownloadTarget(queueItemId, state.Code));
                break;
            }
        }

        return targets;
    }

    private RoomState? ResolveRoom(DownloadTarget target) =>
        _registry.Find(target.RoomCode) ?? _registry.FindByQueueItemId(target.QueueItemId);

    private async Task PersistJobAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
        if (database is null)
        {
            return;
        }

        var row = await database.DownloadJobs
            .FirstOrDefaultAsync(candidate => candidate.Id == job.Id, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            database.DownloadJobs.Add(job);
        }
        else
        {
            row.Stage = job.Stage;
            row.ProgressPercent = job.ProgressPercent;
            row.FailureReason = job.FailureReason;
            row.AttemptCount = job.AttemptCount;
            row.CompletedAtUtc = job.CompletedAtUtc;
            row.QueueItemId = job.QueueItemId;
            row.SongId = job.SongId;
            row.VideoId = job.VideoId;
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistSongAsync(
        DownloadJob job,
        string cacheRelative,
        long fileSizeBytes,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
        if (database is null)
        {
            return;
        }

        var song = await database.Songs
            .FirstOrDefaultAsync(candidate => candidate.Id == job.SongId, cancellationToken)
            .ConfigureAwait(false);
        if (song is null)
        {
            LogSongMissing(_logger, job.SongId, job.VideoId);
            return;
        }

        song.Source = MediaSource.YouTube;
        song.VideoId = job.VideoId;
        song.CachePath = cacheRelative;
        song.FileSizeBytes = fileSizeBytes;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private string CacheFilePath(string videoId) =>
        YouTubeCachePaths.FilePath(_options, _library.RootPath, videoId);

    /// <summary>
    /// Canonical persisted <see cref="Song.CachePath"/> value for a YouTube song: a slash-separated
    /// path relative to <see cref="YtDlpOptions.CacheDirectory"/> (normally <c>{videoId}.mp4</c>).
    /// Multitrack rows never come through here and stay relative to the media root.
    /// </summary>
    internal static string CachePathForStorage(string cacheFilePath, string cacheDirectory) =>
        YouTubeCachePaths.ToStoredPath(cacheFilePath, cacheDirectory);

    private string ResolveDirectory(string configured) =>
        Path.GetFullPath(
            Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(_library.RootPath, configured));

    private static Karaokay.Contracts.Hubs.DownloadStage ToWireStage(DownloadStage stage) =>
        (Karaokay.Contracts.Hubs.DownloadStage)(int)stage;

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogStagingCleanupFailed(_logger, directory, exception);
        }
    }

    /// <summary>Synchronous bridge from yt-dlp's line callback into the async progress pump.</summary>
    private sealed class ChannelProgress(ChannelWriter<DownloadProgressInfo> writer)
        : IProgress<DownloadProgressInfo>
    {
        public void Report(DownloadProgressInfo value) => writer.TryWrite(value);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "YouTube download worker started")]
    private static partial void LogWorkerStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "YouTube download worker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "YouTube download job for {VideoId} crashed unexpectedly")]
    private static partial void LogJobCrashed(ILogger logger, string videoId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "YouTube cache already contains {VideoId}; completing job")]
    private static partial void LogCacheHit(ILogger logger, string videoId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "YouTube download attempt {Attempt}/{MaxAttempts} for {VideoId} failed")]
    private static partial void LogAttemptFailed(ILogger logger, string videoId, int attempt, int maxAttempts, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "YouTube job {VideoId} completed ({Bytes} bytes)")]
    private static partial void LogJobCompleted(ILogger logger, string videoId, long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "YouTube job {VideoId} failed after retries: {Reason}")]
    private static partial void LogJobFailed(ILogger logger, string videoId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Song {SongId} for video {VideoId} disappeared before the download completed")]
    private static partial void LogSongMissing(ILogger logger, Guid songId, string videoId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete staging directory {Directory}")]
    private static partial void LogStagingCleanupFailed(ILogger logger, string directory, Exception exception);
}
