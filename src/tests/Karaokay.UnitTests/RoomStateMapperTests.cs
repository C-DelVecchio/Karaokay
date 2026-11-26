using Karaokay.Api.Features;
using Karaokay.Domain;
using PlaybackStatus = Karaokay.Domain.PlaybackStatus;
using QueueItemState = Karaokay.Domain.QueueItemState;

namespace Karaokay.UnitTests;

/// <summary>
/// Cold-start rehydration mapping (docs/ARCHITECTURE.md §5.1 rule 6, §5.2; Slice 3 review M4).
/// The mapper is pure, so the row → live-state rebuild is covered without PostgreSQL; the
/// integration suite covers the actual query path.
/// </summary>
public sealed class RoomStateMapperTests
{
    [Fact]
    public void MapsQueuePlaybackAnchorVolumeAndSequence()
    {
        var startedAt = DateTime.UtcNow.AddSeconds(-12);
        var first = CreateItem(position: 0, QueueItemState.Ended, "A");
        var second = CreateItem(position: 5, QueueItemState.Ready, "B");

        var row = new PartyRoom
        {
            Id = Guid.NewGuid(),
            Code = "ABCD",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
            Status = PlaybackStatus.Playing,
            CurrentQueueItemId = second.Id,
            PlaybackStartedAtUtc = startedAt,
            PausedAtPositionMs = 0,
            GuideVocalVolume = 0.3,
            CountdownRemainingBeats = 2,
            QueueSequence = 7,
            Queue = [first, second],
        };

        var room = RoomStateMapper.Map(row);

        Assert.Equal(row.Id, room.Id);
        Assert.Equal(row.Code, room.Code);
        Assert.Equal(row.CreatedAtUtc, room.CreatedAtUtc);
        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(second.Id, room.CurrentQueueItemId);
        Assert.Equal(startedAt, room.PlaybackStartedAtUtc);
        Assert.Equal(0, room.PausedAtPositionMs);
        Assert.Equal(0.3, room.GuideVocalVolume);
        Assert.Equal(2, room.CountdownRemainingBeats);
        Assert.Equal(7, room.QueueSequence);
        Assert.Equal(8, room.NextQueueSequence());

        // Positions are re-densified and the persisted order is preserved.
        Assert.Equal(new[] { first.Id, second.Id }, room.Queue.Select(entry => entry.Id));
        Assert.Equal(0, room.Queue[0].Position);
        Assert.Equal(1, room.Queue[1].Position);
        Assert.Equal(QueueItemState.Ended, room.Queue[0].State);

        // The cursor keeps its anchor (never auto-paused) and is normalized to Playing.
        var current = room.Queue[1];
        Assert.Equal(QueueItemState.Playing, current.State);
        Assert.NotNull(current.Song);
        Assert.Equal("B", current.Song!.Title);
        Assert.Equal(1_000L, current.Song.DurationMs);
        Assert.Equal("Ada", current.RequestedByName);
    }

    [Fact]
    public void DanglingCursorFallsBackToIdle()
    {
        var item = CreateItem(position: 0, QueueItemState.Ready, "A");

        var row = new PartyRoom
        {
            Id = Guid.NewGuid(),
            Code = "WXYZ",
            CreatedAtUtc = DateTime.UtcNow,
            Status = PlaybackStatus.Playing,
            CurrentQueueItemId = Guid.NewGuid(),
            PlaybackStartedAtUtc = DateTime.UtcNow.AddSeconds(-3),
            Queue = [item],
        };

        var room = RoomStateMapper.Map(row);

        Assert.Equal(PlaybackStatus.Idle, room.Status);
        Assert.Null(room.CurrentQueueItemId);
        Assert.Null(room.PlaybackStartedAtUtc);
        Assert.Equal(0, room.PausedAtPositionMs);
        Assert.Equal(QueueItemState.Ready, room.Queue.Single().State);
    }

    [Fact]
    public void StaleDuplicatePlayingStateIsDemotedToReady()
    {
        var first = CreateItem(position: 0, QueueItemState.Playing, "A");
        var second = CreateItem(position: 1, QueueItemState.Playing, "B");

        var row = new PartyRoom
        {
            Id = Guid.NewGuid(),
            Code = "PQRS",
            CreatedAtUtc = DateTime.UtcNow,
            Status = PlaybackStatus.Paused,
            CurrentQueueItemId = second.Id,
            PausedAtPositionMs = 4_200,
            QueueSequence = 3,
            Queue = [first, second],
        };

        var room = RoomStateMapper.Map(row);

        Assert.Equal(PlaybackStatus.Paused, room.Status);
        Assert.Equal(second.Id, room.CurrentQueueItemId);
        Assert.Equal(4_200, room.PausedAtPositionMs);
        Assert.Equal(QueueItemState.Ready, room.Queue[0].State);
        Assert.Equal(QueueItemState.Playing, room.Queue[1].State);
    }

    [Fact]
    public void CountdownRoomKeepsTheCursorItemReady()
    {
        // NOTE: wall-clock fixture. The "-1 s" anchor is expected to map to 2 remaining beats; if
        // more than ~500 ms elapses between this fixture and `Map` (a stalled test host, a debugger
        // breakpoint) the derived value drops to 1 and this assertion fails. Low risk in normal
        // runs — do not "fix" it by widening the expectation without understanding the derivation.
        var anchor = DateTime.UtcNow.AddSeconds(-1);
        var item = CreateItem(position: 0, QueueItemState.Ready, "A");

        var row = new PartyRoom
        {
            Id = Guid.NewGuid(),
            Code = "LMNO",
            CreatedAtUtc = DateTime.UtcNow,
            Status = PlaybackStatus.Countdown,
            CurrentQueueItemId = item.Id,
            PlaybackStartedAtUtc = anchor,
            CountdownRemainingBeats = 3,
            QueueSequence = 5,
            Queue = [item],
        };

        var room = RoomStateMapper.Map(row);

        // The count-in anchor is restored so the authority can resume (or finish) the window, and
        // the queue row stays Ready until audio actually starts. The remaining-beat value is derived
        // from the persisted anchor (one second elapsed → two beats left), not copied from the stale
        // Start-time row value, so a restart resumes at beat 2 instead of replaying 4 → 1.
        Assert.Equal(PlaybackStatus.Countdown, room.Status);
        Assert.Equal(item.Id, room.CurrentQueueItemId);
        Assert.Equal(anchor, room.PlaybackStartedAtUtc);
        Assert.Equal(2, room.CountdownRemainingBeats);
        Assert.True(room.CountdownAwaitingResume);
        Assert.Equal(5, room.QueueSequence);
        Assert.Equal(QueueItemState.Ready, room.Queue.Single().State);
    }

    private static QueueItem CreateItem(int position, QueueItemState state, string title)
    {
        var song = new Song
        {
            Id = Guid.NewGuid(),
            Title = title,
            Artist = "Artist",
            DurationMs = 1_000,
            Source = MediaSource.Multitrack,
        };

        return new QueueItem
        {
            Id = Guid.NewGuid(),
            SongId = song.Id,
            Position = position,
            RequestedByName = "Ada",
            State = state,
            RequestedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            Song = song,
        };
    }
}
