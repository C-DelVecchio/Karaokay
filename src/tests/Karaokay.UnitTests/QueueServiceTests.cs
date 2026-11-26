using Karaokay.Api.Features;
using Karaokay.Contracts.Hubs;
using Karaokay.Domain;
using Karaokay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using PlaybackStatus = Karaokay.Domain.PlaybackStatus;
using QueueItemState = Karaokay.Domain.QueueItemState;
using WirePlaybackStatus = Karaokay.Contracts.Hubs.PlaybackStatus;

namespace Karaokay.UnitTests;

/// <summary>
/// Regression coverage for the Slice 3 review's M1 finding: a guest reorder must never let the
/// playback cursor skip a queued song or end the room while songs remain. Also pins the basic
/// playback transitions (start / pause / resume / skip / idle) at the service layer.
/// </summary>
public sealed class QueueServiceTests
{
    [Fact]
    public async Task MovingFutureItemAbovePlayingClampsAfterCurrentAndPlaysNext()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var a = AddEntry(room, QueueItemState.Playing, position: 0, name: "A");
        var b = AddEntry(room, QueueItemState.Ready, position: 1, name: "B");
        var c = AddEntry(room, QueueItemState.Ready, position: 2, name: "C");
        BeginPlaying(room, a);

        var result = await service.MoveQueueItemAsync(room, c.Id, 0, CancellationToken.None);

        Assert.Equal(QueueMoveResult.Moved, result);
        Assert.Equal([a.Id, c.Id, b.Id], QueueIds(room));
        Assert.Equal([0, 1, 2], room.Queue.Select(entry => entry.Position));
        Assert.Equal(QueueItemState.Playing, a.State);

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(c.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, c.State);
        Assert.Equal(QueueItemState.Ended, a.State);
        Assert.Equal(QueueItemState.Ready, b.State);
    }

    [Fact]
    public async Task MovingThePlayingItemIsRejectedAndAdvanceOrderIsUnchanged()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var a = AddEntry(room, QueueItemState.Playing, position: 0, name: "A");
        var b = AddEntry(room, QueueItemState.Ready, position: 1, name: "B");
        var c = AddEntry(room, QueueItemState.Ready, position: 2, name: "C");
        BeginPlaying(room, a);

        var result = await service.MoveQueueItemAsync(room, a.Id, 2, CancellationToken.None);

        Assert.Equal(QueueMoveResult.NotMovable, result);
        Assert.Equal([a.Id, b.Id, c.Id], QueueIds(room));
        Assert.Equal([0, 1, 2], room.Queue.Select(entry => entry.Position));

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(b.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, b.State);
        Assert.Equal(QueueItemState.Ended, a.State);
        Assert.Equal(QueueItemState.Ready, c.State);
    }

    [Fact]
    public async Task MovingEndedHistoryIsRejected()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var a = AddEntry(room, QueueItemState.Ended, position: 0, name: "A");
        var b = AddEntry(room, QueueItemState.Playing, position: 1, name: "B");
        var c = AddEntry(room, QueueItemState.Ready, position: 2, name: "C");
        BeginPlaying(room, b);

        var result = await service.MoveQueueItemAsync(room, a.Id, 2, CancellationToken.None);

        Assert.Equal(QueueMoveResult.NotMovable, result);
        Assert.Equal([a.Id, b.Id, c.Id], QueueIds(room));
    }

    [Fact]
    public async Task AdvancePlaysAnEarlierReadyItemInsteadOfGoingIdle()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();

        // An item that became Ready behind the cursor (download finished while the current song
        // played) must still be playable; positions alone must not strand it.
        var earlier = AddEntry(room, QueueItemState.Ready, position: 0, name: "Earlier");
        var current = AddEntry(room, QueueItemState.Playing, position: 1, name: "Current");
        BeginPlaying(room, current);

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(earlier.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, earlier.State);
        Assert.Equal(QueueItemState.Ended, current.State);
    }

    [Fact]
    public async Task StartPlaybackCountsInThenBeginsTheFirstPlayableItem()
    {
        var (service, client) = CreateService();
        var room = CreateRoom();
        var ready = AddEntry(room, QueueItemState.Ready, position: 0, name: "Ready");

        await service.StartPlaybackAsync(room, CancellationToken.None);

        // Start enters the authority-owned count-in; the ticker completes it in the authority.
        Assert.Equal(PlaybackStatus.Countdown, room.Status);
        Assert.Equal(ready.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Ready, ready.State);
        Assert.NotNull(room.PlaybackStartedAtUtc);
        Assert.Equal(PlaybackTiming.CountdownBeats, room.CountdownRemainingBeats);
        Assert.Equal(WirePlaybackStatus.Countdown, client.PlaybackStates[^1].Status);

        await service.CompleteCountdownAsync(room, ready.Id, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(ready.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, ready.State);
        Assert.Equal(0, room.CountdownRemainingBeats);
        Assert.Equal(WirePlaybackStatus.Playing, client.PlaybackStates[^1].Status);
    }

    [Fact]
    public async Task PauseAndResumePreserveTheCurrentItem()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var ready = AddEntry(room, QueueItemState.Ready, position: 0);
        await service.StartPlaybackAsync(room, CancellationToken.None);

        await service.PausePlaybackAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Paused, room.Status);
        Assert.Null(room.PlaybackStartedAtUtc);
        Assert.True(room.PausedAtPositionMs >= 0);

        await service.ResumePlaybackAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(ready.Id, room.CurrentQueueItemId);
        Assert.NotNull(room.PlaybackStartedAtUtc);
        Assert.Equal(0, room.PausedAtPositionMs);
    }

    [Fact]
    public async Task StartFromPausedIsIgnoredSoResumeRemainsThePath()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (service, client, _) = RoomTestHarness.CreateQueueService(clock);
        var room = CreateRoom();
        var entry = AddEntry(room, QueueItemState.Ready, position: 0);

        await service.StartPlaybackAsync(room, CancellationToken.None);
        await service.CompleteCountdownAsync(room, entry.Id, CancellationToken.None);

        clock.Advance(TimeSpan.FromMilliseconds(1_500));
        await service.PausePlaybackAsync(room, CancellationToken.None);
        var pausedAt = room.PausedAtPositionMs;
        Assert.Equal(1_500, pausedAt);
        var broadcastsBefore = client.PlaybackStates.Count;

        // Start is only meaningful from Idle/SongEnded: from Paused it must not rewind to zero and
        // start a fresh count-in behind the phone's back (Resume is the explicit path).
        await service.StartPlaybackAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Paused, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Equal(pausedAt, room.PausedAtPositionMs);
        Assert.Null(room.PlaybackStartedAtUtc);
        Assert.Equal(0, room.CountdownRemainingBeats);
        Assert.Equal(broadcastsBefore, client.PlaybackStates.Count);
    }

    [Fact]
    public async Task RemovingTheCurrentItemResetsPlaybackToIdle()
    {
        var (service, client) = CreateService();
        var room = CreateRoom();
        var current = AddEntry(room, QueueItemState.Ready, position: 0, name: "Current");
        var upcoming = AddEntry(room, QueueItemState.Ready, position: 1, name: "Upcoming");
        await service.StartPlaybackAsync(room, CancellationToken.None);
        await service.CompleteCountdownAsync(room, current.Id, CancellationToken.None);
        var broadcastsBefore = client.PlaybackStates.Count;

        var removed = await service.RemoveQueueItemAsync(room, current.Id, CancellationToken.None);

        Assert.True(removed);
        Assert.Equal([upcoming.Id], QueueIds(room));
        Assert.Equal(0, upcoming.Position);
        Assert.Equal(PlaybackStatus.Idle, room.Status);
        Assert.Null(room.CurrentQueueItemId);
        Assert.Null(room.PlaybackStartedAtUtc);
        Assert.Equal(0, room.PausedAtPositionMs);
        Assert.Equal(WirePlaybackStatus.Idle, client.PlaybackStates[^1].Status);
        Assert.Equal(broadcastsBefore + 1, client.PlaybackStates.Count);
    }

    [Fact]
    public async Task RemovingTheCurrentItemPersistsTheIdlePlaybackReset()
    {
        var room = CreateRoom();
        var current = AddEntry(room, QueueItemState.Playing, position: 0, name: "Current");
        var upcoming = AddEntry(room, QueueItemState.Ready, position: 1, name: "Upcoming");
        BeginPlaying(room, current);

        var options = new DbContextOptionsBuilder<KaraokayDbContext>()
            .UseInMemoryDatabase($"queue-remove-current-{Guid.NewGuid():N}")
            .Options;
        await using var database = new KaraokayDbContext(options);

        database.Songs.AddRange(CreateSong(current.SongId), CreateSong(upcoming.SongId));
        database.Rooms.Add(new PartyRoom
        {
            Id = room.Id,
            Code = room.Code,
            CreatedAtUtc = room.CreatedAtUtc,
            Status = PlaybackStatus.Playing,
            CurrentQueueItemId = current.Id,
            PlaybackStartedAtUtc = room.PlaybackStartedAtUtc,
        });
        await database.SaveChangesAsync();

        var (service, client, _) = RoomTestHarness.CreateQueueService(
            scopeFactory: new FixedDatabaseScopeFactory(database));

        var removed = await service.RemoveQueueItemAsync(room, current.Id, CancellationToken.None);

        Assert.True(removed);

        // §5.1 rule 6: the reset the broadcast announced must already be durable. Reading through
        // a fresh, no-tracking query proves SaveChanges wrote the playback columns, not just memory.
        var persistedRoom = await database.Rooms
            .AsNoTracking()
            .SingleAsync(row => row.Id == room.Id);
        Assert.Equal(PlaybackStatus.Idle, persistedRoom.Status);
        Assert.Null(persistedRoom.CurrentQueueItemId);
        Assert.Null(persistedRoom.PlaybackStartedAtUtc);
        Assert.Equal(0, persistedRoom.PausedAtPositionMs);
        Assert.Equal(room.QueueSequence, persistedRoom.QueueSequence);

        var persistedQueue = await database.QueueItems
            .AsNoTracking()
            .Where(item => item.RoomId == room.Id)
            .ToListAsync();
        var remaining = Assert.Single(persistedQueue);
        Assert.Equal(upcoming.Id, remaining.Id);
        Assert.Equal(0, remaining.Position);

        Assert.Equal(WirePlaybackStatus.Idle, client.PlaybackStates[^1].Status);
    }

    [Fact]
    public async Task RemovingAnUpcomingItemLeavesPlaybackUntouched()
    {
        var (service, client) = CreateService();
        var room = CreateRoom();
        var current = AddEntry(room, QueueItemState.Ready, position: 0, name: "Current");
        var upcoming = AddEntry(room, QueueItemState.Ready, position: 1, name: "Upcoming");
        await service.StartPlaybackAsync(room, CancellationToken.None);
        await service.CompleteCountdownAsync(room, current.Id, CancellationToken.None);
        var anchor = room.PlaybackStartedAtUtc;
        var broadcastsBefore = client.PlaybackStates.Count;

        var removed = await service.RemoveQueueItemAsync(room, upcoming.Id, CancellationToken.None);

        Assert.True(removed);
        Assert.Equal([current.Id], QueueIds(room));
        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(current.Id, room.CurrentQueueItemId);
        Assert.Equal(anchor, room.PlaybackStartedAtUtc);
        Assert.Equal(broadcastsBefore, client.PlaybackStates.Count);

        // Absent item: idempotent no-op, reported so the hub can send NotFound.
        Assert.False(
            await service.RemoveQueueItemAsync(room, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task SkipAdvancesToTheNextItemAndGoesIdleAfterTheLast()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var first = AddEntry(room, QueueItemState.Ready, position: 0, name: "First");
        var second = AddEntry(room, QueueItemState.Ready, position: 1, name: "Second");
        await service.StartPlaybackAsync(room, CancellationToken.None);

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(second.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Ended, first.State);
        Assert.Equal(QueueItemState.Playing, second.State);

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(PlaybackStatus.Idle, room.Status);
        Assert.Null(room.CurrentQueueItemId);
        Assert.Null(room.PlaybackStartedAtUtc);
        Assert.Equal(QueueItemState.Ended, second.State);
    }

    [Fact]
    public async Task SkipIgnoresFailedAndDownloadingItems()
    {
        var (service, _) = CreateService();
        var room = CreateRoom();
        var current = AddEntry(room, QueueItemState.Playing, position: 0, name: "Current");
        AddEntry(room, QueueItemState.Failed, position: 1, name: "Failed");
        AddEntry(room, QueueItemState.Downloading, position: 2, name: "Downloading");
        var ready = AddEntry(room, QueueItemState.Ready, position: 3, name: "Ready");
        BeginPlaying(room, current);

        await service.SkipSongAsync(room, CancellationToken.None);

        Assert.Equal(ready.Id, room.CurrentQueueItemId);
        Assert.Equal(PlaybackStatus.Playing, room.Status);
    }

    private static RoomState CreateRoom() => RoomTestHarness.CreateRoom();

    private static Song CreateSong(Guid id) => new()
    {
        Id = id,
        Title = "Song",
        DurationMs = 1_000,
        Source = Karaokay.Domain.MediaSource.Multitrack,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static void BeginPlaying(RoomState room, RoomQueueEntry entry)
    {
        room.CurrentQueueItemId = entry.Id;
        room.Status = PlaybackStatus.Playing;
        room.PlaybackStartedAtUtc = DateTime.UtcNow;
    }

    private static RoomQueueEntry AddEntry(
        RoomState room,
        QueueItemState state,
        int position,
        string name = "Song") =>
        RoomTestHarness.AddEntry(room, state, position, name);

    private static Guid[] QueueIds(RoomState room) =>
        [.. room.QueueEntries.OrderBy(entry => entry.Position).Select(entry => entry.Id)];

    private static (QueueService Service, RecordingHubClient Client) CreateService()
    {
        var (service, client, _) = RoomTestHarness.CreateQueueService();
        return (service, client);
    }
}
