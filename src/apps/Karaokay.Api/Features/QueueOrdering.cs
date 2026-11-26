using Karaokay.Domain;
using QueueItemState = Karaokay.Domain.QueueItemState;

namespace Karaokay.Api.Features;

/// <summary>Outcome of a queue reorder request.</summary>
public enum QueueMoveResult
{
    /// <summary>The entry was reordered.</summary>
    Moved = 0,

    /// <summary>No queue entry has the supplied id.</summary>
    NotFound = 1,

    /// <summary>The entry is playback cursor history (<c>Playing</c>/<c>Ended</c>) and cannot move.</summary>
    NotMovable = 2,
}

/// <summary>
/// Pure queue-ordering rules for a live room (docs/ARCHITECTURE.md §5.1). Playback advances
/// strictly forward through the queue: the <c>Playing</c> entry and the <c>Ended</c> history before
/// it are the cursor, so only upcoming entries may be reordered, and only into slots after the
/// current entry. Keeping these rules free of persistence and hub dependencies makes the
/// skip/strand regressions unit-testable.
/// </summary>
internal static class QueueOrdering
{
    /// <summary>Can this entry be selected by playback?</summary>
    public static bool IsPlayable(RoomQueueEntry entry) =>
        entry.State is QueueItemState.Ready or QueueItemState.Queued;

    /// <summary>
    /// The entry that plays next: the first playable entry after <paramref name="current"/> in
    /// queue order, or — when nothing follows — the first playable entry before it. The fallback
    /// prevents a room from going <c>Idle</c> while a song that became ready behind the cursor is
    /// still queued (for example an item whose download finished while the current song played).
    /// </summary>
    public static RoomQueueEntry? SelectNext(
        List<RoomQueueEntry> ordered,
        RoomQueueEntry? current)
    {
        var currentIndex = current is null ? -1 : IndexOf(ordered, current);

        for (var index = currentIndex + 1; index < ordered.Count; index++)
        {
            if (IsPlayable(ordered[index]))
            {
                return ordered[index];
            }
        }

        for (var index = 0; index < currentIndex; index++)
        {
            if (IsPlayable(ordered[index]))
            {
                return ordered[index];
            }
        }

        return null;
    }

    /// <summary>
    /// Moves one upcoming entry to <paramref name="newPosition"/> (a dense queue index) and
    /// renumbers the queue. <c>Playing</c>/<c>Ended</c> entries and the current cursor (which is
    /// also the count-in item while the room is <c>Countdown</c>) are rejected; a target before the
    /// current entry is clamped to the first upcoming slot, so a guest asking to put a song "on
    /// top" makes it play next instead of stranding it behind the cursor.
    /// </summary>
    public static QueueMoveResult Move(
        List<RoomQueueEntry> entries,
        Guid queueItemId,
        int newPosition,
        Guid? currentQueueItemId)
    {
        var entry = entries.FirstOrDefault(candidate => candidate.Id == queueItemId);
        if (entry is null)
        {
            return QueueMoveResult.NotFound;
        }

        if (entry.State is QueueItemState.Playing or QueueItemState.Ended
            || (currentQueueItemId is { } currentId && entry.Id == currentId))
        {
            return QueueMoveResult.NotMovable;
        }

        var ordered = entries.OrderBy(item => item.Position).ToList();
        ordered.Remove(entry);

        // Upcoming entries must stay after the cursor. When the room is idle, keep them after the
        // ended-history prefix so history and upcoming rows never interleave.
        var lowerBound = currentQueueItemId is { } cursorId
            ? IndexOf(ordered, cursorId) + 1
            : CountEndedPrefix(ordered);
        lowerBound = Math.Max(0, lowerBound);

        var target = Math.Clamp(newPosition, lowerBound, ordered.Count);
        ordered.Insert(target, entry);

        entries.Clear();
        entries.AddRange(ordered);
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index].Position = index;
        }

        return QueueMoveResult.Moved;
    }

    private static int CountEndedPrefix(List<RoomQueueEntry> ordered)
    {
        var count = 0;
        while (count < ordered.Count && ordered[count].State == QueueItemState.Ended)
        {
            count++;
        }

        return count;
    }

    private static int IndexOf(List<RoomQueueEntry> ordered, RoomQueueEntry entry)
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            if (ReferenceEquals(ordered[index], entry) || ordered[index].Id == entry.Id)
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOf(List<RoomQueueEntry> ordered, Guid entryId)
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            if (ordered[index].Id == entryId)
            {
                return index;
            }
        }

        return -1;
    }
}
