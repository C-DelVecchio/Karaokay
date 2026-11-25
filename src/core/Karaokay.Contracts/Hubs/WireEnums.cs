namespace Karaokay.Contracts.Hubs;

// Wire mirrors of the domain enums in Karaokay.Domain (docs/ARCHITECTURE.md §4.2/§4.3).
// Contracts must stay BCL-only, so it cannot reference Domain; both copies are persisted as
// integers with identical values. A contract test can assert the numeric parity.

/// <summary>Media origin of a song; mirrors <c>Karaokay.Domain.MediaSource</c>.</summary>
public enum MediaSource
{
    /// <summary>Local multitrack stems (<c>instrumental.opus</c> + <c>vocals.opus</c>).</summary>
    Multitrack = 0,

    /// <summary>Cached YouTube video.</summary>
    YouTube = 1,
}

/// <summary>Queue-entry lifecycle; mirrors <c>Karaokay.Domain.QueueItemState</c>.</summary>
public enum QueueItemState
{
    /// <summary>Accepted, waiting to become ready.</summary>
    Queued = 0,

    /// <summary>Media is downloading.</summary>
    Downloading = 1,

    /// <summary>Media is ready to play.</summary>
    Ready = 2,

    /// <summary>Download failed.</summary>
    Failed = 3,

    /// <summary>Currently playing.</summary>
    Playing = 4,

    /// <summary>Finished playing.</summary>
    Ended = 5,
}

/// <summary>Room playback state; mirrors <c>Karaokay.Domain.PlaybackStatus</c>.</summary>
public enum PlaybackStatus
{
    /// <summary>Nothing is playing.</summary>
    Idle = 0,

    /// <summary>Waiting for the head-of-queue item to download.</summary>
    AwaitingDownload = 1,

    /// <summary>Lead-in/count-in before playback.</summary>
    Countdown = 2,

    /// <summary>A song is playing.</summary>
    Playing = 3,

    /// <summary>Playback is paused.</summary>
    Paused = 4,

    /// <summary>The current song ended; the queue is advancing.</summary>
    SongEnded = 5,
}

/// <summary>YouTube download stage; mirrors <c>Karaokay.Domain.DownloadStage</c>.</summary>
public enum DownloadStage
{
    /// <summary>Waiting for a worker slot.</summary>
    Queued = 0,

    /// <summary>Resolving the stream URL.</summary>
    Resolving = 1,

    /// <summary>Transferring media.</summary>
    Downloading = 2,

    /// <summary>Remuxing/transcoding.</summary>
    Transcoding = 3,

    /// <summary>Completed.</summary>
    Completed = 4,

    /// <summary>Failed after retries.</summary>
    Failed = 5,
}
