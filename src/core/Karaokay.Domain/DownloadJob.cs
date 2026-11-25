namespace Karaokay.Domain;

/// <summary>
/// Persistent record of one YouTube ingestion attempt (docs/ARCHITECTURE.md §4.2/§5.1). A job is
/// created when a guest enqueues a search result and is driven through
/// <see cref="DownloadStage"/> by the background download worker.
/// </summary>
public class DownloadJob
{
    /// <summary>Job identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Target song row; created at enqueue time so the queue can reference it immediately.</summary>
    public Guid SongId { get; set; }

    /// <summary>Validated eleven-character YouTube video id.</summary>
    public string VideoId { get; set; } = "";

    /// <summary>Current stage of the download pipeline.</summary>
    public DownloadStage Stage { get; set; }

    /// <summary>Last reported transfer percentage, 0..100.</summary>
    public double ProgressPercent { get; set; }

    /// <summary>Human-readable failure reason after the retry budget is exhausted.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Number of attempts made so far (retry up to two with backoff).</summary>
    public int AttemptCount { get; set; }

    /// <summary>Job creation timestamp (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Terminal timestamp (UTC) for completed or failed jobs.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Queue entry this job reports progress for, when known.</summary>
    public Guid? QueueItemId { get; set; }

    /// <summary>Target song navigation.</summary>
    public Song Song { get; set; } = null!;
}
