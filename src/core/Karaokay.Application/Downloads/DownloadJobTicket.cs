using Karaokay.Domain;

namespace Karaokay.Application.Downloads;

/// <summary>One queue row waiting for (or observing) a video download.</summary>
public sealed record DownloadTarget(Guid QueueItemId, string RoomCode);

/// <summary>Latest progress sample for a download; mirrors the future <c>DownloadProgressDto</c>.</summary>
public sealed record DownloadProgressSnapshot(
    DownloadStage Stage,
    double Percent,
    double BytesPerSec,
    double EtaSec);

/// <summary>Terminal outcome shared by every queue row attached to one video download.</summary>
public sealed record DownloadJobResult(bool Success, string? FailureReason);

/// <summary>
/// In-memory single-flight handle for one YouTube video (docs/ARCHITECTURE.md §5.1). Concurrent
/// enqueue requests for the same video share this ticket (and therefore one download and one
/// progress stream) instead of pushing duplicate jobs into the channel.
/// </summary>
public sealed class DownloadJobTicket
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, DownloadTarget> _targets = new();
    private DownloadProgressSnapshot _progress = new(DownloadStage.Queued, 0, 0, 0);
    private DownloadJobResult? _result;

    public DownloadJobTicket(string videoId, Guid songId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        VideoId = videoId;
        SongId = songId;
    }

    /// <summary>Validated YouTube video id shared by every attached queue row.</summary>
    public string VideoId { get; }

    /// <summary>Song row every attached queue row points at.</summary>
    public Guid SongId { get; }

    /// <summary>Completes once the worker reaches a terminal stage; shared by attached requests.</summary>
    public TaskCompletionSource<DownloadJobResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Terminal result, or <c>null</c> while the download is still running.</summary>
    public DownloadJobResult? Result
    {
        get
        {
            lock (_sync)
            {
                return _result;
            }
        }
    }

    /// <summary>Latest progress sample for late joiners and diagnostics.</summary>
    public DownloadProgressSnapshot Progress
    {
        get
        {
            lock (_sync)
            {
                return _progress;
            }
        }
    }

    /// <summary>Snapshot of the queue rows currently attached to this download.</summary>
    public IReadOnlyList<DownloadTarget> Targets
    {
        get
        {
            lock (_sync)
            {
                return [.. _targets.Values];
            }
        }
    }

    /// <summary>Attaches a queue row unless the ticket already reached a terminal state.</summary>
    public bool TryAddTarget(DownloadTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            if (_result is not null)
            {
                return false;
            }

            _targets[target.QueueItemId] = target;
            return true;
        }
    }

    /// <summary>Removes an attachment that could not be persisted; returns true when it existed.</summary>
    public bool TryRemoveTarget(Guid queueItemId)
    {
        lock (_sync)
        {
            return _targets.Remove(queueItemId);
        }
    }

    /// <summary>Records the newest progress sample.</summary>
    public void Report(DownloadProgressSnapshot progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        lock (_sync)
        {
            if (_result is null)
            {
                _progress = progress;
            }
        }
    }

    /// <summary>
    /// Marks the ticket terminal and returns the attached queue rows. The first caller wins, so a
    /// retry/duplicate completion never broadcasts twice.
    /// </summary>
    public IReadOnlyList<DownloadTarget> Complete(DownloadJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        DownloadTarget[] targets;
        lock (_sync)
        {
            if (_result is not null)
            {
                return [];
            }

            _result = result;
            targets = [.. _targets.Values];
            _targets.Clear();
        }

        Completion.TrySetResult(result);
        return targets;
    }
}
