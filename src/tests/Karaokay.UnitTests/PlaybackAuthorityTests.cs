using Karaokay.Api.Features;
using Karaokay.Contracts.Hubs;
using Microsoft.Extensions.Logging.Abstractions;
using PlaybackStatus = Karaokay.Domain.PlaybackStatus;
using QueueItemState = Karaokay.Domain.QueueItemState;
using WirePlaybackStatus = Karaokay.Contracts.Hubs.PlaybackStatus;

namespace Karaokay.UnitTests;

/// <summary>
/// Slice 4 coverage for the single-writer playback authority (docs/ARCHITECTURE.md §5.1/§7):
/// authority-owned count-in, command-channel processing, the duration watchdog, the shared
/// duplicate-end guard, and the count-in ticker. Everything runs against in-memory rooms with a
/// fake clock and a recording hub client.
/// </summary>
public sealed class PlaybackAuthorityTests
{
    private static readonly int[] ExpectedBeats = [4, 3, 2, 1];
    private static readonly int[] FirstBeatOnly = [4];
    private static readonly int[] FirstTwoBeats = [4, 3];
    private static readonly int[] RestartedBeats = [4, 3, 4];
    private static readonly int[] ResumedBeats = [2, 1];
    private static readonly int[] FirstResumedBeat = [2];

    [Fact]
    public async Task StartEntersCountdownThenAutoTransitionsToPlayingWithFreshAnchor()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var entry = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 60_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        // Start pins the count-in contract: Countdown, position 0, four beats, cursor on the item.
        Assert.Equal(PlaybackStatus.Countdown, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Equal(PlaybackTiming.CountdownBeats, room.CountdownRemainingBeats);
        Assert.Equal(QueueItemState.Ready, entry.State);
        var countdownAnchor = Assert.IsType<DateTime>(room.PlaybackStartedAtUtc);

        var countdownState = client.PlaybackStates[^1];
        Assert.Equal(WirePlaybackStatus.Countdown, countdownState.Status);
        Assert.Equal(entry.Id, countdownState.CurrentQueueItemId);
        Assert.Equal(0L, countdownState.PositionMs);
        Assert.Equal(PlaybackTiming.CountdownBeats, countdownState.CountdownRemainingBeats);

        // Beat 4 is pushed immediately, then one beat per 500 ms; a fast loop must not duplicate.
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(FirstBeatOnly, client.CountdownTicks.Select(tick => tick.RemainingBeats));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(FirstBeatOnly, client.CountdownTicks.Select(tick => tick.RemainingBeats));

        clock.Advance(TimeSpan.FromMilliseconds(400));
        await authority.TickAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await authority.TickAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(ExpectedBeats, client.CountdownTicks.Select(tick => tick.RemainingBeats));
        Assert.All(
            client.CountdownTicks,
            tick =>
            {
                Assert.Equal(entry.Id, tick.QueueItemId);
                Assert.Equal(PlaybackTiming.CountdownBeatMs, tick.BeatMs);
            });
        Assert.Equal(PlaybackStatus.Countdown, room.Status);

        // The fourth beat marks the end of the window: the authority starts the song itself.
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, entry.State);
        Assert.Equal(0, room.CountdownRemainingBeats);

        var playingAnchor = Assert.IsType<DateTime>(room.PlaybackStartedAtUtc);
        Assert.Equal(PlaybackTiming.CountdownWindow, playingAnchor - countdownAnchor);

        var playingState = client.PlaybackStates[^1];
        Assert.Equal(WirePlaybackStatus.Playing, playingState.Status);
        Assert.Equal(entry.Id, playingState.CurrentQueueItemId);
        Assert.Equal(0L, playingState.PositionMs);
        Assert.Null(playingState.CountdownRemainingBeats);

        // The ring is finished: no further ticks are emitted.
        clock.Advance(TimeSpan.FromSeconds(1));
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(ExpectedBeats.Length, client.CountdownTicks.Count);
    }

    [Fact]
    public async Task PauseDuringCountdownThenResumeStartsPlayingAtZero()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var entry = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 60_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.PausePlayback, room.Code),
            CancellationToken.None);

        Assert.Equal(PlaybackStatus.Paused, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Equal(0, room.PausedAtPositionMs);
        Assert.Null(room.PlaybackStartedAtUtc);
        Assert.Equal(0, room.CountdownRemainingBeats);
        Assert.Equal(QueueItemState.Ready, entry.State);

        // Once paused the count-in timer is stopped, even if the room sits for minutes.
        clock.Advance(TimeSpan.FromMinutes(5));
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(PlaybackStatus.Paused, room.Status);
        Assert.Empty(client.CountdownTicks);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.ResumePlayback, room.Code),
            CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, entry.State);
        Assert.Equal(0, room.PausedAtPositionMs);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, room.PlaybackStartedAtUtc);
        Assert.Equal(WirePlaybackStatus.Playing, client.PlaybackStates[^1].Status);
        Assert.Equal(0L, client.PlaybackStates[^1].PositionMs);
    }

    [Fact]
    public async Task SkipDuringCountdownCancelsTheCountdownAndAdvances()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var first = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 60_000);
        var second = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 1, "B", 60_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);
        Assert.Equal(PlaybackStatus.Countdown, room.Status);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.SkipSong, room.Code),
            CancellationToken.None);

        // Skip keeps the existing advance path: the count-in is cancelled and B plays now.
        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(second.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Ended, first.State);
        Assert.Equal(QueueItemState.Playing, second.State);
        Assert.Equal(0, room.CountdownRemainingBeats);

        clock.Advance(TimeSpan.FromSeconds(3));
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(second.Id, room.CurrentQueueItemId);
        Assert.Empty(client.CountdownTicks);
    }

    [Fact]
    public async Task RestartDuringCountdownRestartsTheCountIn()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var entry = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 60_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);
        var firstAnchor = Assert.IsType<DateTime>(room.PlaybackStartedAtUtc);

        await authority.TickAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(900));
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(FirstTwoBeats, client.CountdownTicks.Select(tick => tick.RemainingBeats));

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.RestartSong, room.Code),
            CancellationToken.None);

        Assert.Equal(PlaybackStatus.Countdown, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Equal(PlaybackTiming.CountdownBeats, room.CountdownRemainingBeats);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, room.PlaybackStartedAtUtc);
        Assert.NotEqual(firstAnchor, room.PlaybackStartedAtUtc);

        // The restarted window re-emits beat 4 from the new anchor.
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(RestartedBeats, client.CountdownTicks.Select(tick => tick.RemainingBeats));
    }

    [Fact]
    public async Task DuplicateStartIsIgnoredWhileCountingInAndWhilePlaying()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, _, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var entry = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 60_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);
        var countdownAnchor = Assert.IsType<DateTime>(room.PlaybackStartedAtUtc);

        clock.Advance(TimeSpan.FromMilliseconds(700));
        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        Assert.Equal(PlaybackStatus.Countdown, room.Status);
        Assert.Equal(countdownAnchor, room.PlaybackStartedAtUtc);
        Assert.Equal(PlaybackTiming.CountdownBeats, room.CountdownRemainingBeats);

        clock.Advance(TimeSpan.FromMilliseconds(1_300));
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(PlaybackStatus.Playing, room.Status);
        var playingAnchor = Assert.IsType<DateTime>(room.PlaybackStartedAtUtc);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(playingAnchor, room.PlaybackStartedAtUtc);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
    }

    [Fact]
    public async Task WatchdogEndsStalledPlaybackOnlyAfterDurationPlusGrace()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var only = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "Short", 1_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        clock.Advance(PlaybackTiming.CountdownWindow);
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(PlaybackStatus.Playing, room.Status);

        // A count-in is never a watchdog candidate; only Playing past duration + grace is.
        clock.Advance(TimeSpan.FromMilliseconds(5_500));
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(PlaybackStatus.Playing, room.Status);

        clock.Advance(TimeSpan.FromMilliseconds(1_000));
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(PlaybackStatus.Idle, room.Status);
        Assert.Null(room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Ended, only.State);
        Assert.Equal(WirePlaybackStatus.Idle, client.PlaybackStates[^1].Status);
    }

    [Fact]
    public async Task WatchdogExemptsPausedRooms()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, _, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var only = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "Paused", 1_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);
        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.PausePlayback, room.Code),
            CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(10));
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(PlaybackStatus.Paused, room.Status);
        Assert.Equal(only.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Ready, only.State);
    }

    [Fact]
    public async Task DuplicateEndReportCannotDoubleAdvance()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, _, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var first = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 60_000);
        var second = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 1, "B", 60_000);

        var authority = CreateAuthority(registry, queue, hub, clock);
        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(
                RoomCommandTypes.ReportSongEnded,
                room.Code,
                queueItemId: first.Id),
            CancellationToken.None);

        Assert.Equal(second.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, second.State);

        // The stage retries the same report; the item is no longer current, so it is ignored.
        await authority.HandleCommandAsync(
            RoomTestHarness.Command(
                RoomCommandTypes.ReportSongEnded,
                room.Code,
                queueItemId: first.Id),
            CancellationToken.None);

        Assert.Equal(second.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, second.State);
        Assert.Equal(QueueItemState.Ended, first.State);
        Assert.Equal(PlaybackStatus.Playing, room.Status);
    }

    [Fact]
    public async Task WatchdogAfterStageReportCannotDoubleAdvance()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, _, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var first = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 1_000);
        var second = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 1, "B", 60_000);

        var authority = CreateAuthority(registry, queue, hub, clock);
        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        // The stage reports the end just as the watchdog deadline passes.
        clock.Advance(TimeSpan.FromSeconds(6.5));
        await authority.HandleCommandAsync(
            RoomTestHarness.Command(
                RoomCommandTypes.ReportSongEnded,
                room.Code,
                queueItemId: first.Id),
            CancellationToken.None);

        Assert.Equal(second.Id, room.CurrentQueueItemId);

        // The watchdog now runs for the same expiring room: B owns the cursor, so the stale end
        // for A must not advance a second time.
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(second.Id, room.CurrentQueueItemId);
        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(QueueItemState.Playing, second.State);
        Assert.Equal(QueueItemState.Ended, first.State);
    }

    [Fact]
    public async Task WatchdogBeforeStaleStageReportCannotDoubleAdvance()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, _, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var first = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 1_000);
        var second = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 1, "B", 60_000);

        var authority = CreateAuthority(registry, queue, hub, clock);
        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);
        clock.Advance(PlaybackTiming.CountdownWindow);
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(first.Id, room.CurrentQueueItemId);

        // The watchdog wins the race: it ends the expired song and advances the cursor to B.
        clock.Advance(TimeSpan.FromSeconds(6.5));
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(second.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Ended, first.State);

        // The stage's report for A was in flight and arrives afterwards. A is no longer current, so
        // the shared guard drops it and B keeps playing instead of double-advancing to Idle.
        await authority.HandleCommandAsync(
            RoomTestHarness.Command(
                RoomCommandTypes.ReportSongEnded,
                room.Code,
                queueItemId: first.Id),
            CancellationToken.None);

        Assert.Equal(second.Id, room.CurrentQueueItemId);
        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(QueueItemState.Playing, second.State);
        Assert.Equal(QueueItemState.Ended, first.State);
    }

    [Fact]
    public async Task RemoveCurrentItemCommandResetsPlaybackThroughTheAuthority()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var current = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 60_000);
        var upcoming = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 1, "B", 60_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);
        clock.Advance(PlaybackTiming.CountdownWindow);
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(PlaybackStatus.Playing, room.Status);
        var broadcastsBefore = client.PlaybackStates.Count;

        var removed = await authority.HandleCommandAsync(
            RoomTestHarness.Command(
                RoomCommandTypes.RemoveQueueItem,
                room.Code,
                queueItemId: current.Id),
            CancellationToken.None);

        Assert.True(removed);
        Assert.DoesNotContain(room.QueueEntries, entry => entry.Id == current.Id);
        Assert.Equal([upcoming.Id], room.QueueEntries.Select(entry => entry.Id));
        Assert.Equal(0, upcoming.Position);
        Assert.Equal(PlaybackStatus.Idle, room.Status);
        Assert.Null(room.CurrentQueueItemId);
        Assert.Null(room.PlaybackStartedAtUtc);
        Assert.Equal(0, room.PausedAtPositionMs);
        Assert.Equal(broadcastsBefore + 1, client.PlaybackStates.Count);
        Assert.Equal(WirePlaybackStatus.Idle, client.PlaybackStates[^1].Status);

        // The command result doubles as the hub's not-found signal.
        Assert.False(
            await authority.HandleCommandAsync(
                RoomTestHarness.Command(
                    RoomCommandTypes.RemoveQueueItem,
                    room.Code,
                    queueItemId: current.Id),
                CancellationToken.None));
    }

    [Fact]
    public async Task RemoveUpcomingItemCommandLeavesPlaybackUntouched()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var current = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 60_000);
        var upcoming = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 1, "B", 60_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);
        clock.Advance(PlaybackTiming.CountdownWindow);
        await authority.TickAsync(CancellationToken.None);
        var anchor = room.PlaybackStartedAtUtc;
        var broadcastsBefore = client.PlaybackStates.Count;

        var removed = await authority.HandleCommandAsync(
            RoomTestHarness.Command(
                RoomCommandTypes.RemoveQueueItem,
                room.Code,
                queueItemId: upcoming.Id),
            CancellationToken.None);

        Assert.True(removed);
        Assert.Equal([current.Id], room.QueueEntries.Select(entry => entry.Id));
        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(current.Id, room.CurrentQueueItemId);
        Assert.Equal(anchor, room.PlaybackStartedAtUtc);
        Assert.Equal(broadcastsBefore, client.PlaybackStates.Count);
    }

    [Fact]
    public async Task MovingTheCountingInItemIsRejected()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, _, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var first = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 60_000);
        var second = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 1, "B", 60_000);

        var authority = CreateAuthority(registry, queue, hub, clock);
        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        var result = await queue.MoveQueueItemAsync(room, first.Id, 1, CancellationToken.None);

        Assert.Equal(QueueMoveResult.NotMovable, result);
        Assert.Equal(first.Id, room.Queue[0].Id);
        Assert.Equal(second.Id, room.Queue[1].Id);
    }

    [Fact]
    public async Task CountdownCatchesUpWhenTheTickLoopRunsLate()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var entry = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 60_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        // A stalled loop must still push every beat in order before starting the song.
        clock.Advance(PlaybackTiming.CountdownWindow);
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(ExpectedBeats, client.CountdownTicks.Select(tick => tick.RemainingBeats));
        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
    }

    [Fact]
    public async Task StartOnADownloadingHeadAwaitsDownloadThenCountsInWhenItBecomesReady()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var entry = RoomTestHarness.AddEntry(
            room,
            QueueItemState.Downloading,
            position: 0,
            "A",
            60_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        // §5.1 rule 3: Start parks the room on the downloading head; no count-in clock yet.
        Assert.Equal(PlaybackStatus.AwaitingDownload, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Null(room.PlaybackStartedAtUtc);
        Assert.Equal(WirePlaybackStatus.AwaitingDownload, client.PlaybackStates[^1].Status);

        // A tick while the item is still downloading must not start anything.
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(PlaybackStatus.AwaitingDownload, room.Status);
        Assert.Empty(client.CountdownTicks);

        // The download worker marks the row Ready on its direct path...
        await queue.UpdateQueueItemStateAsync(room, entry.Id, QueueItemState.Ready, CancellationToken.None);

        // ...and the authority starts the count-in by itself, with no second Start press.
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(PlaybackStatus.Countdown, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Equal(PlaybackTiming.CountdownBeats, room.CountdownRemainingBeats);
        Assert.Equal(WirePlaybackStatus.Countdown, client.PlaybackStates[^1].Status);

        clock.Advance(PlaybackTiming.CountdownWindow);
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Equal(QueueItemState.Playing, entry.State);
        Assert.Equal(ExpectedBeats, client.CountdownTicks.Select(tick => tick.RemainingBeats));
    }

    [Fact]
    public async Task DuplicateStartWhileAwaitingDownloadDoesNotReanchor()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var entry = RoomTestHarness.AddEntry(
            room,
            QueueItemState.Downloading,
            position: 0,
            "A",
            60_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);
        var broadcastsBefore = client.PlaybackStates.Count;

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        Assert.Equal(PlaybackStatus.AwaitingDownload, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Equal(broadcastsBefore, client.PlaybackStates.Count);
    }

    [Fact]
    public async Task RestartWhileAwaitingDownloadStaysParkedInsteadOfStartingTheSong()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var entry = RoomTestHarness.AddEntry(
            room,
            QueueItemState.Downloading,
            position: 0,
            "A",
            60_000);
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);
        Assert.Equal(PlaybackStatus.AwaitingDownload, room.Status);

        // Restart mirrors Start (§5.1 rule 3): the media has not landed yet, so the room stays
        // parked instead of entering Countdown/Playing on a song that cannot play.
        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.RestartSong, room.Code),
            CancellationToken.None);

        Assert.Equal(PlaybackStatus.AwaitingDownload, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Null(room.PlaybackStartedAtUtc);
        Assert.Empty(client.CountdownTicks);

        // Once the item is Ready, a restart begins the count-in rather than skipping straight to
        // Playing (which the old unconditional BeginPlayingLocked branch would have done).
        await queue.UpdateQueueItemStateAsync(room, entry.Id, QueueItemState.Ready, CancellationToken.None);
        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.RestartSong, room.Code),
            CancellationToken.None);

        Assert.Equal(PlaybackStatus.Countdown, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.Equal(PlaybackTiming.CountdownBeats, room.CountdownRemainingBeats);
    }

    [Fact]
    public async Task DownloadCompletingForANonCursorItemDoesNotStartTheCountdown()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, _, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var awaited = RoomTestHarness.AddEntry(room, QueueItemState.Downloading, position: 0, "A");
        var other = RoomTestHarness.AddEntry(room, QueueItemState.Downloading, position: 1, "B");
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);
        Assert.Equal(awaited.Id, room.CurrentQueueItemId);

        await queue.UpdateQueueItemStateAsync(room, other.Id, QueueItemState.Ready, CancellationToken.None);
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(PlaybackStatus.AwaitingDownload, room.Status);
        Assert.Equal(awaited.Id, room.CurrentQueueItemId);

        await queue.UpdateQueueItemStateAsync(room, awaited.Id, QueueItemState.Ready, CancellationToken.None);
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(PlaybackStatus.Countdown, room.Status);
        Assert.Equal(awaited.Id, room.CurrentQueueItemId);
    }

    [Fact]
    public async Task FailedDownloadOfTheAwaitedItemReturnsTheRoomToIdle()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var entry = RoomTestHarness.AddEntry(room, QueueItemState.Downloading, position: 0, "A");
        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.HandleCommandAsync(
            RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
            CancellationToken.None);

        await queue.UpdateQueueItemStateAsync(room, entry.Id, QueueItemState.Failed, CancellationToken.None);
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(PlaybackStatus.Idle, room.Status);
        Assert.Null(room.CurrentQueueItemId);
        Assert.Equal(WirePlaybackStatus.Idle, client.PlaybackStates[^1].Status);
        Assert.Empty(client.CountdownTicks);
    }

    [Fact]
    public async Task ColdRestartMidCountInDoesNotReplayAlreadyBroadcastBeats()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, client, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        var entry = RoomTestHarness.AddEntry(room, QueueItemState.Ready, position: 0, "A", 60_000);

        // Simulate rehydration: the room was counting in when the process died 1.2 s into the
        // window (beats 4 and 3 already went out) and the mapper restored the anchor and the
        // resume flag.
        room.Status = PlaybackStatus.Countdown;
        room.CurrentQueueItemId = entry.Id;
        room.PlaybackStartedAtUtc = clock.GetUtcNow().UtcDateTime.AddMilliseconds(-1_200);
        room.CountdownRemainingBeats = 2;
        room.CountdownAwaitingResume = true;

        var authority = CreateAuthority(registry, queue, hub, clock);

        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(FirstResumedBeat, client.CountdownTicks.Select(tick => tick.RemainingBeats));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        await authority.TickAsync(CancellationToken.None);
        Assert.Equal(ResumedBeats, client.CountdownTicks.Select(tick => tick.RemainingBeats));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        await authority.TickAsync(CancellationToken.None);

        Assert.Equal(PlaybackStatus.Playing, room.Status);
        Assert.Equal(entry.Id, room.CurrentQueueItemId);
        Assert.False(room.CountdownAwaitingResume);

        // The old process's 4 and 3 are never re-broadcast.
        Assert.DoesNotContain(client.CountdownTicks, tick => tick.RemainingBeats is 3 or 4);
    }

    [Fact]
    public async Task EnqueueFailsFastWhenTheCommandChannelIsFull()
    {
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (queue, _, hub) = RoomTestHarness.CreateQueueService(clock);
        var registry = new RoomRegistry();
        var room = RoomTestHarness.CreateRoom();
        Assert.True(registry.TryAdd(room));

        // A very short write timeout so the test fails fast; the authority is never started, so
        // nothing drains the channel.
        var authority = new PlaybackAuthority(
            registry,
            queue,
            hub,
            NullLogger<PlaybackAuthority>.Instance,
            clock,
            TimeSpan.FromHours(1),
            TimeSpan.FromMilliseconds(100));

        using var fill = new CancellationTokenSource();
        var pending = Enumerable.Range(0, PlaybackAuthority.CommandCapacity)
            .Select(_ => authority.EnqueueAsync(
                RoomTestHarness.Command(RoomCommandTypes.StartPlayback, room.Code),
                fill.Token))
            .ToArray();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (authority.PendingCommandCount < PlaybackAuthority.CommandCapacity
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(PlaybackAuthority.CommandCapacity, authority.PendingCommandCount);

        await Assert.ThrowsAsync<TimeoutException>(() => authority.EnqueueAsync(
            RoomTestHarness.Command(RoomCommandTypes.PausePlayback, room.Code),
            CancellationToken.None));

        fill.Cancel();
        await Task.WhenAll(pending.Select(async task =>
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected: the filled commands are cancelled while waiting for the consume loop.
            }
        }));
    }

    private static PlaybackAuthority CreateAuthority(
        RoomRegistry registry,
        QueueService queue,
        FakeHubContext hub,
        TimeProvider? timeProvider = null) =>
        new(
            registry,
            queue,
            hub,
            NullLogger<PlaybackAuthority>.Instance,
            timeProvider ?? TimeProvider.System,
            TimeSpan.FromHours(1));
}
