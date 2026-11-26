namespace Karaokay.Api.Features;

/// <summary>
/// Authority-owned count-in timing (docs/ARCHITECTURE.md §5.1 rules 3 and 5). Pinned wire contract:
/// the room enters <c>Countdown</c> for four beats of 500 ms, broadcasting one
/// <c>CountdownTicked(queueItemId, remainingBeats, beatMs: 500)</c> per beat with
/// <c>remainingBeats</c> running 4 → 1, then auto-transitions to <c>Playing</c> with a fresh
/// playback anchor.
/// </summary>
internal static class PlaybackTiming
{
    /// <summary>Count-in length in beats.</summary>
    public const int CountdownBeats = 4;

    /// <summary>Count-in beat length in milliseconds, as pushed to phones.</summary>
    public const double CountdownBeatMs = 500;

    /// <summary>Total count-in window before playback starts.</summary>
    public static readonly TimeSpan CountdownWindow =
        TimeSpan.FromMilliseconds(CountdownBeats * CountdownBeatMs);
}
