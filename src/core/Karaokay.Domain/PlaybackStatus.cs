namespace Karaokay.Domain;

/// <summary>
/// Room-level playback state machine (docs/ARCHITECTURE.md §5.1). Persisted as an integer; the
/// numeric order is part of the storage contract and must never be reused.
/// </summary>
public enum PlaybackStatus
{
    /// <summary>Nothing is playing and the queue may be empty.</summary>
    Idle = 0,

    /// <summary>The head-of-queue item is still downloading.</summary>
    AwaitingDownload = 1,

    /// <summary>The stage is running its lead-in/count-in before playback.</summary>
    Countdown = 2,

    /// <summary>A song is playing.</summary>
    Playing = 3,

    /// <summary>Playback is paused, preserving the position in <c>PausedAtPositionMs</c>.</summary>
    Paused = 4,

    /// <summary>The current song ended; the authority is advancing the queue.</summary>
    SongEnded = 5,
}
