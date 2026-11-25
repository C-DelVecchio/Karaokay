namespace Karaokay.Domain;

/// <summary>
/// Progress stage of a YouTube download job (docs/ARCHITECTURE.md §4.2/§5.1). The
/// <c>DownloadJob</c> entity itself belongs to Slice 5; the enum is defined now because the
/// SignalR <c>DownloadProgressDto</c> already carries it.
/// </summary>
public enum DownloadStage
{
    /// <summary>Waiting for a download worker slot.</summary>
    Queued = 0,

    /// <summary>Resolving the best H.264/AAC stream with yt-dlp.</summary>
    Resolving = 1,

    /// <summary>Transferring the media stream.</summary>
    Downloading = 2,

    /// <summary>Remuxing/transcoding into the MP4 cache file.</summary>
    Transcoding = 3,

    /// <summary>Finished; the media file is ready.</summary>
    Completed = 4,

    /// <summary>Failed after the retry budget; the item is marked failed.</summary>
    Failed = 5,
}
