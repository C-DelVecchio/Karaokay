namespace Karaokay.Domain;

/// <summary>
/// Lifecycle of one queue entry (docs/ARCHITECTURE.md §5.1). Persisted as an integer; the numeric
/// order is part of the storage contract and must never be reused.
/// </summary>
public enum QueueItemState
{
    /// <summary>Accepted and waiting; multitrack songs are promoted to <see cref="Ready"/> immediately.</summary>
    Queued = 0,

    /// <summary>YouTube media is being fetched/transcoded (Slice 5).</summary>
    Downloading = 1,

    /// <summary>Media is on disk and the item may be played.</summary>
    Ready = 2,

    /// <summary>Download failed; the room advances past it.</summary>
    Failed = 3,

    /// <summary>The item is the room's current song.</summary>
    Playing = 4,

    /// <summary>The item finished playing and stays in history until pruned.</summary>
    Ended = 5,
}
