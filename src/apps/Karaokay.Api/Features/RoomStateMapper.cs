using Karaokay.Contracts.Hubs;
using Karaokay.Domain;
using PlaybackStatus = Karaokay.Domain.PlaybackStatus;
using QueueItemState = Karaokay.Domain.QueueItemState;
using WireMediaSource = Karaokay.Contracts.Hubs.MediaSource;

namespace Karaokay.Api.Features;

/// <summary>
/// Rebuilds a live <see cref="RoomState"/> from its persisted <see cref="PartyRoom"/> row
/// (docs/ARCHITECTURE.md §5.1 rule 6, §5.2). Pure and side-effect free so the mapping can be unit
/// tested without PostgreSQL; <see cref="RoomRehydrator"/> owns the query.
/// </summary>
internal static class RoomStateMapper
{
    /// <summary>
    /// Maps one persisted room (with its queue and joined songs loaded) back into the hot-path
    /// state. Playback anchors are copied verbatim — a room that was <c>Playing</c> across a
    /// restart keeps flowing from <see cref="PartyRoom.PlaybackStartedAtUtc"/> instead of being
    /// silently paused.
    /// </summary>
    public static RoomState Map(PartyRoom row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var room = new RoomState(row.Id, row.Code, row.CreatedAtUtc);
        var entries = row.Queue
            .OrderBy(item => item.Position)
            .Select(MapEntry)
            .ToList();

        // Dense positions are an invariant of the in-memory queue; renumber defensively in case a
        // previous write left a gap.
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index].Position = index;
        }

        room.RestoreQueueSequence(row.QueueSequence);
        room.GuideVocalVolume = double.IsNaN(row.GuideVocalVolume)
            ? 0.6
            : Math.Clamp(row.GuideVocalVolume, 0, 1);
        room.CountdownRemainingBeats = Math.Max(0, row.CountdownRemainingBeats);

        var current = row.CurrentQueueItemId is null
            ? null
            : entries.FirstOrDefault(entry => entry.Id == row.CurrentQueueItemId);

        if (current is null)
        {
            // A dangling cursor (item removed or never persisted) must not leave the room playing
            // something that does not exist.
            room.Status = PlaybackStatus.Idle;
            room.CurrentQueueItemId = null;
            room.PlaybackStartedAtUtc = null;
            room.PausedAtPositionMs = 0;
        }
        else
        {
            room.CurrentQueueItemId = current.Id;
            room.Status = (PlaybackStatus)(int)row.Status;
            room.PlaybackStartedAtUtc = row.PlaybackStartedAtUtc;
            room.PausedAtPositionMs = Math.Max(0, row.PausedAtPositionMs);

            // A live count-in keeps its queue row Ready until audio starts; only a Playing/Paused
            // room implies the cursor item is already the Playing entry.
            if (current.State != QueueItemState.Playing
                && (room.Status is PlaybackStatus.Playing or PlaybackStatus.Paused))
            {
                current.State = QueueItemState.Playing;
            }
        }

        // At most the current entry may be Playing; a stale duplicate would show two cursors.
        foreach (var entry in entries)
        {
            if (entry.State == QueueItemState.Playing && entry != current)
            {
                entry.State = QueueItemState.Ready;
            }
        }

        if (room.Status == PlaybackStatus.Countdown && room.PlaybackStartedAtUtc is { } countdownAnchor)
        {
            // A restart can land mid count-in. The stored remaining-beat value is whatever the last
            // durable transition wrote (the Start beat), so derive the live value from the persisted
            // anchor: the authority resumes at this beat instead of replaying 4 → 1 (§5.1 rule 3).
            room.CountdownRemainingBeats = RemainingCountdownBeats(countdownAnchor, DateTime.UtcNow);
        }

        room.CountdownAwaitingResume = room.Status == PlaybackStatus.Countdown;

        room.QueueEntries.AddRange(entries);
        return room;
    }

    private static int RemainingCountdownBeats(DateTime anchorUtc, DateTime nowUtc)
    {
        var elapsedMs = Math.Max(0, (nowUtc - anchorUtc).TotalMilliseconds);
        var elapsedBeats = (int)Math.Floor(elapsedMs / PlaybackTiming.CountdownBeatMs);
        return Math.Clamp(
            PlaybackTiming.CountdownBeats - elapsedBeats,
            1,
            PlaybackTiming.CountdownBeats);
    }

    private static RoomQueueEntry MapEntry(QueueItem item) => new()
    {
        Id = item.Id,
        SongId = item.SongId,
        Position = item.Position,
        RequestedByName = item.RequestedByName,
        State = item.State,
        SessionTokenId = item.SessionTokenId,
        RequestedAtUtc = item.RequestedAtUtc,
        Song = item.Song is null ? null : ToDto(item.Song),
    };

    private static SongDto ToDto(Song song) => new(
        song.Id,
        song.Title,
        song.Artist,
        song.DurationMs,
        (WireMediaSource)(int)song.Source,
        song.ThumbnailUrl);
}
