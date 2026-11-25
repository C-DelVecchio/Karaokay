namespace Karaokay.Domain;

/// <summary>
/// Persisted party room and authoritative playback state (docs/ARCHITECTURE.md §4.2). The live
/// session lives in the API's singleton <c>RoomRegistry</c>; this row is the cold-restart fallback.
/// </summary>
public class PartyRoom
{
    /// <summary>Room identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Four-letter join code from the 23-letter alphabet; unique index.</summary>
    public string Code { get; set; } = "";

    /// <summary>Room creation timestamp (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Soft-close timestamp; rooms are garbage-collected after 12 h idle (UTC).</summary>
    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>Authoritative room playback state (single writer: PlaybackAuthority, Slice 4).</summary>
    public PlaybackStatus Status { get; set; }

    /// <summary>The queue item currently being played, when any.</summary>
    public Guid? CurrentQueueItemId { get; set; }

    /// <summary>Wall-clock instant when playing began at position zero (UTC).</summary>
    public DateTime? PlaybackStartedAtUtc { get; set; }

    /// <summary>Position when paused; zero otherwise.</summary>
    public long PausedAtPositionMs { get; set; }

    /// <summary>Guide-vocal bus level, 0..1 (multitrack songs only).</summary>
    public double GuideVocalVolume { get; set; } = 0.6;

    /// <summary>Beats remaining in the lead-in count-in, when in <see cref="PlaybackStatus.Countdown"/>.</summary>
    public int CountdownRemainingBeats { get; set; }

    /// <summary>Queued entries, ordered densely by <see cref="QueueItem.Position"/>.</summary>
    public List<QueueItem> Queue { get; set; } = new();
}
