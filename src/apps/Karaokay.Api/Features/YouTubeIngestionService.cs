using Karaokay.Api.Hubs;
using Karaokay.Application;
using Karaokay.Application.Downloads;
using Karaokay.Contracts;
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
using WireMediaSource = Karaokay.Contracts.Hubs.MediaSource;

namespace Karaokay.Api.Features;

/// <summary>
/// Shared YouTube ingestion core used by both the SignalR hub and the REST endpoints
/// (docs/ARCHITECTURE.md §4.3/§7, Slice 5). It owns the feature gate, video-id validation, the
/// <c>Songs.VideoId</c> cache-hit/dedup lookup, queue insertion, and pushing one coalesced job into
/// the bounded download channel.
/// </summary>
public sealed partial class YouTubeIngestionService
{
    private const int MaxTitleLength = 512;
    private const int MaxChannelLength = 512;
    private const int MaxThumbnailLength = 2048;

    /// <summary>
    /// Bounded retries for the practically-unreachable window where the single-flight ticket
    /// completes between the cache lookup and <see cref="DownloadJobTicket.TryAddTarget"/>.
    /// </summary>
    private const int AttachRetryCount = 5;

    private const int AttachRetryDelayMilliseconds = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IYouTubeService _youtube;
    private readonly DownloadJobChannel _queue;
    private readonly DownloadJobCoordinator _coordinator;
    private readonly QueueService _roomQueue;
    private readonly MediaLibrary _library;
    private readonly IHubContext<StageHub, IStageHubClient> _hub;
    private readonly FeatureOptions _features;
    private readonly YtDlpOptions _ytDlp;
    private readonly ILogger<YouTubeIngestionService> _logger;

    public YouTubeIngestionService(
        IServiceScopeFactory scopeFactory,
        IYouTubeService youtube,
        DownloadJobChannel queue,
        DownloadJobCoordinator coordinator,
        QueueService roomQueue,
        MediaLibrary library,
        IHubContext<StageHub, IStageHubClient> hub,
        IOptions<FeatureOptions> features,
        IOptions<YtDlpOptions> ytDlp,
        ILogger<YouTubeIngestionService> logger)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(ytDlp);

        _scopeFactory = scopeFactory;
        _youtube = youtube;
        _queue = queue;
        _coordinator = coordinator;
        _roomQueue = roomQueue;
        _library = library;
        _hub = hub;
        _features = features.Value;
        _ytDlp = ytDlp.Value;
        _logger = logger;
    }

    /// <summary>Feature flag value shared with the hub/endpoints for early gating.</summary>
    public bool IsEnabled => _features.EnableYouTubeIngestion;

    /// <summary>Runs a zero-key yt-dlp search; returns an empty list when ingestion is disabled.</summary>
    public async Task<IReadOnlyList<YouTubeSearchResultDto>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (!_features.EnableYouTubeIngestion)
        {
            return [];
        }

        return await _youtube.SearchAsync(query, maxResults, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enqueues a YouTube search result. A cached song becomes <c>Ready</c> immediately; otherwise a
    /// <c>Downloading</c> queue row is created and a single-flight job is pushed to the worker.
    /// </summary>
    public async Task<QueueAck> EnqueueAsync(
        RoomState room,
        string requestedByName,
        Guid? sessionTokenId,
        YouTubeQueueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(request);

        if (!_features.EnableYouTubeIngestion)
        {
            return new QueueAck(false, Guid.Empty, HubErrorCodes.YouTubeDisabled);
        }

        if (!VideoIdValidator.IsValid(request.VideoId))
        {
            LogInvalidVideoId(_logger, request.VideoId ?? string.Empty);
            return new QueueAck(false, Guid.Empty, HubErrorCodes.InvalidRequest);
        }

        var videoId = request.VideoId;
        var title = Clamp(request.Title, MaxTitleLength, "YouTube video");
        var channel = Clamp(request.Channel, MaxChannelLength, string.Empty);
        var thumbnail = ClampNullable(request.ThumbnailUrl, MaxThumbnailLength);
        var durationMs = ToMilliseconds(request.DurationSeconds);
        var lookup = await ResolveSongAsync(videoId, title, channel, thumbnail, durationMs, cancellationToken)
            .ConfigureAwait(false);

        var song = new SongDto(
            lookup.SongId,
            title,
            channel.Length == 0 ? null : channel,
            durationMs,
            WireMediaSource.YouTube,
            thumbnail);

        // Canonical cache path is checked as well so a no-database dev host (and a row whose
        // CachePath was never written) still short-circuits on an existing file. Both checks use the
        // configured cache directory (a trusted root), never the media root.
        if (TryResolveCachedFile(lookup.CachePath, videoId) is not null)
        {
            LogCacheHit(_logger, videoId);
            return await _roomQueue.EnqueueSongAsync(
                    room,
                    song,
                    requestedByName,
                    sessionTokenId,
                    QueueItemState.Ready,
                    queueItemId: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var queueItemId = Guid.NewGuid();
        var ack = await _roomQueue.EnqueueSongAsync(
                room,
                song,
                requestedByName,
                sessionTokenId,
                QueueItemState.Downloading,
                queueItemId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!ack.Success)
        {
            return ack;
        }

        var target = new DownloadTarget(queueItemId, room.Code);
        for (var attempt = 0; attempt < AttachRetryCount; attempt++)
        {
            var ticket = _coordinator.GetOrCreate(videoId, lookup.SongId, out var created);
            if (!ticket.TryAddTarget(target))
            {
                if (created)
                {
                    _coordinator.TryRemove(ticket);
                }

                // The ticket completed between lookup and attach. A successful download means the
                // file is already on disk, so recover by promoting this row to Ready.
                if (TryResolveCachedFile(lookup.CachePath, videoId) is not null)
                {
                    await _roomQueue.UpdateQueueItemStateAsync(
                            room,
                            queueItemId,
                            QueueItemState.Ready,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return ack;
                }

                if (attempt + 1 < AttachRetryCount)
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(AttachRetryDelayMilliseconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            if (created)
            {
                try
                {
                    await QueueJobAsync(lookup.SongId, videoId, queueItemId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    ticket.TryRemoveTarget(queueItemId);
                    _coordinator.TryRemove(ticket);
                    throw;
                }
            }

            LogEnqueued(_logger, room.Code, queueItemId, videoId, created);
            return ack;
        }

        // Bounded retries exhausted without attaching and no cached file appeared: recover the
        // stranded Downloading row instead of leaving it dangling forever. This mirrors the
        // worker's DownloadFailed + QueueItemStateChanged failure path.
        LogCoalescingRace(_logger, videoId);
        await FailUnattachableQueueItemAsync(room, queueItemId, lookup.SongId, cancellationToken)
            .ConfigureAwait(false);
        return new QueueAck(false, queueItemId, HubErrorCodes.DownloadUnavailable);
    }

    private async Task FailUnattachableQueueItemAsync(
        RoomState room,
        Guid queueItemId,
        Guid songId,
        CancellationToken cancellationToken)
    {
        const string reason =
            "The download for this video could not be attached to the queue item. Please retry.";

        await _roomQueue.UpdateQueueItemStateAsync(
                room,
                queueItemId,
                QueueItemState.Failed,
                cancellationToken)
            .ConfigureAwait(false);

        await _hub.Clients.Group(room.Code)
            .DownloadFailed(new DownloadFailedDto(queueItemId, songId, reason))
            .ConfigureAwait(false);
    }

    private async Task QueueJobAsync(
        Guid songId,
        string videoId,
        Guid queueItemId,
        CancellationToken cancellationToken)
    {
        var job = new DownloadJob
        {
            Id = Guid.NewGuid(),
            SongId = songId,
            VideoId = videoId,
            Stage = DownloadStage.Queued,
            ProgressPercent = 0,
            AttemptCount = 0,
            CreatedAtUtc = DateTime.UtcNow,
            QueueItemId = queueItemId,
        };

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
            if (database is not null)
            {
                try
                {
                    database.DownloadJobs.Add(job);
                    await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogJobPersistFailed(_logger, videoId, exception);
                }
            }
        }

        await _queue.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SongLookup> ResolveSongAsync(
        string videoId,
        string title,
        string channel,
        string? thumbnail,
        long durationMs,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
        if (database is null)
        {
            return new SongLookup(YouTubeSongIds.ForVideo(videoId), null);
        }

        var song = await database.Songs
            .FirstOrDefaultAsync(candidate => candidate.VideoId == videoId, cancellationToken)
            .ConfigureAwait(false);

        if (song is not null)
        {
            return new SongLookup(song.Id, song.CachePath);
        }

        song = new Song
        {
            Id = Guid.NewGuid(),
            Source = MediaSource.YouTube,
            Title = title,
            Artist = channel.Length == 0 ? null : channel,
            Channel = channel.Length == 0 ? null : channel,
            DurationMs = durationMs,
            ThumbnailUrl = thumbnail,
            VideoId = videoId,
            IsSyntheticDemo = false,
            CreatedAtUtc = DateTime.UtcNow,
        };
        database.Songs.Add(song);

        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueConstraintViolation())
        {
            // Concurrent enqueue won the filtered unique index on Songs.VideoId; reuse its row.
            database.ChangeTracker.Clear();
            song = await database.Songs
                .FirstAsync(candidate => candidate.VideoId == videoId, cancellationToken)
                .ConfigureAwait(false);
            LogDedupRace(_logger, videoId);
        }

        return new SongLookup(song.Id, song.CachePath);
    }

    private string? TryResolveCachedFile(string? storedPath, string videoId)
    {
        if (YouTubeCachePaths.TryResolveStored(_ytDlp, _library.RootPath, storedPath) is { } stored)
        {
            return stored;
        }

        var canonical = CacheFilePath(videoId);
        return File.Exists(canonical) ? canonical : null;
    }

    private string CacheFilePath(string videoId) =>
        YouTubeCachePaths.FilePath(_ytDlp, _library.RootPath, videoId);

    private static long ToMilliseconds(double durationSeconds)
    {
        if (double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) || durationSeconds <= 0)
        {
            return 0;
        }

        return (long)Math.Round(Math.Min(durationSeconds * 1000.0, TimeSpan.MaxValue.TotalMilliseconds));
    }

    private static string Clamp(string? value, int maxLength, string fallback)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return fallback;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? ClampNullable(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed record SongLookup(Guid SongId, string? CachePath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "YouTube cache hit for video {VideoId}; enqueueing Ready")]
    private static partial void LogCacheHit(ILogger logger, string videoId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Room {RoomCode} enqueued YouTube item {QueueItemId} for {VideoId} (new job: {Created})")]
    private static partial void LogEnqueued(ILogger logger, string roomCode, Guid queueItemId, string videoId, bool created);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected YouTube video id '{VideoId}'")]
    private static partial void LogInvalidVideoId(ILogger logger, string videoId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Concurrent enqueue reused the song row for video {VideoId}")]
    private static partial void LogDedupRace(ILogger logger, string videoId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not persist DownloadJob for video {VideoId}; continuing with in-memory job")]
    private static partial void LogJobPersistFailed(ILogger logger, string videoId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download ticket for {VideoId} completed before a new queue row attached")]
    private static partial void LogCoalescingRace(ILogger logger, string videoId);
}
