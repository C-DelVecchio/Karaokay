extern alias ApiHost;

using System.Collections.Concurrent;
using System.Net.Http.Json;
using Karaokay.Contracts;
using Karaokay.Contracts.Hubs;
using Karaokay.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Karaokay.IntegrationTests;

/// <summary>
/// End-to-end room/hub tests against a real PostgreSQL container (docs/ARCHITECTURE.md §7,
/// Slice 3). Docker-less machines skip every test through <see cref="PostgresApiFixture"/>.
/// The API runs the production migration + demo-seed path; no in-memory shortcuts are used here.
/// </summary>
public sealed class RoomHubIntegrationTests : IClassFixture<PostgresApiFixture>
{
    private const string Alphabet = "ABCDEFGHJKLMNPRSTUVWXYZ";
    private const string JoinRoomMethod = "JoinRoom";
    private const string EnqueueLibrarySongMethod = "EnqueueLibrarySong";
    private const string RemoveQueueItemMethod = "RemoveQueueItem";
    private const string MoveQueueItemMethod = "MoveQueueItem";
    private const string ReportSongEndedMethod = "ReportSongEnded";
    private const string UpdateStageCapabilitiesMethod = "UpdateStageCapabilities";
    private const string JoinedRoomEvent = "JoinedRoom";
    private const string QueueChangedEvent = "QueueChanged";
    private const string PlaybackChangedEvent = "PlaybackChanged";
    private const string CountdownTickedEvent = "CountdownTicked";
    private const string HubErrorEvent = "HubError";

    private static readonly int[] ExpectedCountdownBeats = [4, 3, 2, 1];

    private readonly PostgresApiFixture _fixture;

    public RoomHubIntegrationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task FiveHundredRoomsHaveUniqueCodesAndPersist()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var codes = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < 500; index++)
        {
            var room = await CreateRoomAsync(http).ConfigureAwait(false);
            Assert.True(codes.Add(room.Code), $"Duplicate room code '{room.Code}' at iteration {index}.");
        }

        Assert.Equal(500, codes.Count);
        foreach (var code in codes)
        {
            Assert.Equal(4, code.Length);
            Assert.All(code, character => Assert.Contains(character, Alphabet));
            Assert.DoesNotContain('I', code);
            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('Q', code);
            Assert.DoesNotContain('0', code);
            Assert.DoesNotContain('1', code);
        }

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<KaraokayDbContext>();
        var persisted = await database.Rooms
            .AsNoTracking()
            .CountAsync(room => codes.Contains(room.Code))
            .ConfigureAwait(false);

        Assert.Equal(500, persisted);
    }

    [SkippableFact]
    public async Task GuestJoinReceivesSnapshotAndUnknownCodeFails()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);

        await using var stage = await ConnectAsync().ConfigureAwait(false);
        await using var guest = await ConnectAsync().ConfigureAwait(false);

        var stageJoin = await JoinAsync(stage, room.Code, "Stage", "stage").ConfigureAwait(false);
        Assert.True(stageJoin.Ack.Success);
        Assert.False(string.IsNullOrWhiteSpace(stageJoin.Ack.SessionToken));

        var guestJoin = await JoinAsync(guest, room.Code, "Ada", "guest").ConfigureAwait(false);
        Assert.True(guestJoin.Ack.Success);
        Assert.False(string.IsNullOrWhiteSpace(guestJoin.Ack.SessionToken));

        var snapshot = Assert.IsType<RoomSnapshot>(guestJoin.Snapshot);
        Assert.Equal(room.Code, snapshot.RoomCode);
        Assert.Empty(snapshot.Queue);
        Assert.Equal(1, snapshot.GuestCount);
        Assert.Equal(PlaybackStatus.Idle, snapshot.Status);
        Assert.Equal(0, snapshot.QueueSequence);
        Assert.Equal(PlaybackStatus.Idle, snapshot.Playback.Status);
        Assert.Equal(0, snapshot.Playback.PositionMs);

        await using var unknownRoomGuest = await ConnectAsync().ConfigureAwait(false);
        var rejected = await JoinAsync(unknownRoomGuest, "ZZZZ", "Lost", "guest").ConfigureAwait(false);
        Assert.False(rejected.Ack.Success);
        Assert.Null(rejected.Snapshot);
        Assert.Equal(HubErrorCodes.RoomNotFound, rejected.Ack.Reason);
    }

    [SkippableFact]
    public async Task QueueDeltasAreMonotonicForBothClients()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);
        var songIds = await GetDemoSongIdsAsync(http, 2).ConfigureAwait(false);

        await using var stage = await ConnectAsync().ConfigureAwait(false);
        await using var guest = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(stage, room.Code, "Stage", "stage").ConfigureAwait(false)).Ack.Success);
        Assert.True((await JoinAsync(guest, room.Code, "Ada", "guest").ConfigureAwait(false)).Ack.Success);

        using var stageDeltas = new DeltaCollector(stage);
        using var guestDeltas = new DeltaCollector(guest);

        var first = await guest
            .InvokeAsync<QueueAck>(EnqueueLibrarySongMethod, songIds[0])
            .ConfigureAwait(false);
        Assert.True(first.Success);
        await WaitForDeltasAsync(stageDeltas, guestDeltas, 1).ConfigureAwait(false);

        var second = await guest
            .InvokeAsync<QueueAck>(EnqueueLibrarySongMethod, songIds[1])
            .ConfigureAwait(false);
        Assert.True(second.Success);
        await WaitForDeltasAsync(stageDeltas, guestDeltas, 2).ConfigureAwait(false);

        await guest
            .InvokeAsync(MoveQueueItemMethod, second.QueueItemId, 0)
            .ConfigureAwait(false);
        await WaitForDeltasAsync(stageDeltas, guestDeltas, 3).ConfigureAwait(false);

        await guest
            .InvokeAsync(RemoveQueueItemMethod, first.QueueItemId)
            .ConfigureAwait(false);
        await WaitForDeltasAsync(stageDeltas, guestDeltas, 4).ConfigureAwait(false);

        var stageSequences = stageDeltas.Deltas.Select(delta => delta.Sequence).ToArray();
        var guestSequences = guestDeltas.Deltas.Select(delta => delta.Sequence).ToArray();
        Assert.Equal([1L, 2L, 3L, 4L], stageSequences);
        Assert.Equal(stageSequences, guestSequences);
        Assert.True(stageSequences.Zip(stageSequences.Skip(1)).All(pair => pair.Second > pair.First));

        var snapshot = await http
            .GetFromJsonAsync<RoomSnapshot>($"/api/rooms/{room.Code}")
            .ConfigureAwait(false);
        Assert.NotNull(snapshot);
        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(second.QueueItemId, item.Id);
        Assert.Equal(0, item.Position);
        Assert.Equal(4, snapshot.QueueSequence);

        Assert.Contains(stageDeltas.Deltas[0].Added, added => added.Id == first.QueueItemId);
        Assert.Contains(stageDeltas.Deltas[1].Added, added => added.Id == second.QueueItemId);
        Assert.Contains(stageDeltas.Deltas[2].Updated, updated => updated.Id == second.QueueItemId && updated.Position == 0);
        Assert.Contains(first.QueueItemId, stageDeltas.Deltas[3].Removed);
    }

    [SkippableFact]
    public async Task NoOpPlaybackCommandsDoNotConsumeQueueSequence()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);
        var songIds = await GetDemoSongIdsAsync(http, 1).ConfigureAwait(false);

        await using var guest = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(guest, room.Code, "Ada", "guest").ConfigureAwait(false)).Ack.Success);

        using var deltas = new DeltaCollector(guest);

        // Neither command changes room state: Pause while Idle, Start with an empty queue.
        await guest.InvokeAsync("PausePlayback").ConfigureAwait(false);
        await guest.InvokeAsync("StartPlayback").ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);
        Assert.Empty(deltas.Deltas);

        Assert.True((await guest
            .InvokeAsync<QueueAck>(EnqueueLibrarySongMethod, songIds[0])
            .ConfigureAwait(false)).Success);
        await WaitForCountAsync(deltas, 1).ConfigureAwait(false);

        // The first real mutation must be sequence 1, proving the no-ops were skipped.
        Assert.Equal(1, deltas.Deltas[0].Sequence);
    }

    [SkippableFact]
    public async Task ReconnectingClientConvergesFromSnapshot()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);
        var songIds = await GetDemoSongIdsAsync(http, 2).ConfigureAwait(false);

        await using var clientA = await ConnectAsync().ConfigureAwait(false);
        var clientB = await ConnectAsync().ConfigureAwait(false);

        Assert.True((await JoinAsync(clientA, room.Code, "Ada", "guest").ConfigureAwait(false)).Ack.Success);
        Assert.True((await JoinAsync(clientB, room.Code, "Grace", "guest").ConfigureAwait(false)).Ack.Success);

        using var deltasA = new DeltaCollector(clientA);
        using var deltasB = new DeltaCollector(clientB);

        Assert.True((await clientA
            .InvokeAsync<QueueAck>(EnqueueLibrarySongMethod, songIds[0])
            .ConfigureAwait(false)).Success);
        await WaitForDeltasAsync(deltasA, deltasB, 1).ConfigureAwait(false);

        await clientB.DisposeAsync().ConfigureAwait(false);

        Assert.True((await clientA
            .InvokeAsync<QueueAck>(EnqueueLibrarySongMethod, songIds[1])
            .ConfigureAwait(false)).Success);
        await WaitForCountAsync(deltasA, 2).ConfigureAwait(false);

        var current = await http
            .GetFromJsonAsync<RoomSnapshot>($"/api/rooms/{room.Code}")
            .ConfigureAwait(false);
        Assert.NotNull(current);

        await using var reconnected = await ConnectAsync().ConfigureAwait(false);
        using var replayDeltas = new DeltaCollector(reconnected);
        var rejoin = await JoinAsync(reconnected, room.Code, "Grace", "guest").ConfigureAwait(false);
        Assert.True(rejoin.Ack.Success);

        var restored = Assert.IsType<RoomSnapshot>(rejoin.Snapshot);
        Assert.Equal(current!.QueueSequence, restored.QueueSequence);
        Assert.Equal(
            current.Queue.Select(item => (item.Id, item.Position, item.SongId)),
            restored.Queue.Select(item => (item.Id, item.Position, item.SongId)));
        Assert.Equal(2, restored.Queue.Count);
        Assert.Empty(replayDeltas.Deltas);
    }

    [SkippableFact]
    public async Task GuestCannotUseStageOnlyMethodsButStageCan()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);

        await using var guest = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(guest, room.Code, "Ada", "guest").ConfigureAwait(false)).Ack.Success);

        using var guestErrors = new ErrorCollector(guest);

        await Assert.ThrowsAsync<HubException>(() =>
            guest.InvokeAsync(ReportSongEndedMethod, Guid.NewGuid())).ConfigureAwait(false);
        var reportError = await guestErrors
            .WaitForAsync(error => error.Code == HubErrorCodes.ForbiddenRole)
            .ConfigureAwait(false);
        Assert.Equal(HubErrorCodes.ForbiddenRole, reportError.Code);

        await Assert.ThrowsAsync<HubException>(() =>
            guest.InvokeAsync(UpdateStageCapabilitiesMethod, new StageCapabilities(120))).ConfigureAwait(false);
        var capabilitiesError = await guestErrors
            .WaitForAsync(error => error.Code == HubErrorCodes.ForbiddenRole)
            .ConfigureAwait(false);
        Assert.Equal(HubErrorCodes.ForbiddenRole, capabilitiesError.Code);

        await using var stage = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(stage, room.Code, "Stage", "stage").ConfigureAwait(false)).Ack.Success);
        using var stageErrors = new ErrorCollector(stage);

        await stage.InvokeAsync(ReportSongEndedMethod, Guid.NewGuid()).ConfigureAwait(false);
        await stage.InvokeAsync(UpdateStageCapabilitiesMethod, new StageCapabilities(80)).ConfigureAwait(false);

        await Task.Delay(150).ConfigureAwait(false);
        Assert.DoesNotContain(stageErrors.Errors, error => error.Code == HubErrorCodes.ForbiddenRole);
    }

    [SkippableFact]
    public async Task MovingUpcomingItemAboveCurrentPlaysItNext()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);
        var songIds = await GetDemoSongIdsAsync(http, 3).ConfigureAwait(false);

        await using var guest = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(guest, room.Code, "Ada", "guest").ConfigureAwait(false)).Ack.Success);

        var first = await EnqueueAsync(guest, songIds[0]).ConfigureAwait(false);
        var second = await EnqueueAsync(guest, songIds[1]).ConfigureAwait(false);
        var third = await EnqueueAsync(guest, songIds[2]).ConfigureAwait(false);

        await guest.InvokeAsync("StartPlayback").ConfigureAwait(false);
        var started = await WaitForPlaybackStatusAsync(
                http,
                room.Code,
                PlaybackStatus.Playing,
                TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        Assert.Equal(first.QueueItemId, started.Playback.CurrentQueueItemId);

        // A guest puts the last song "on top"; it may not land before the playing song or the
        // cursor would skip it when the current song ends (review M1).
        await guest.InvokeAsync(MoveQueueItemMethod, third.QueueItemId, 0).ConfigureAwait(false);
        var moved = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(
            [first.QueueItemId, third.QueueItemId, second.QueueItemId],
            moved.Queue.Select(item => item.Id));
        Assert.Equal(first.QueueItemId, moved.Playback.CurrentQueueItemId);

        await guest.InvokeAsync("SkipSong").ConfigureAwait(false);
        var afterSkip = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Playing, afterSkip.Status);
        Assert.Equal(third.QueueItemId, afterSkip.Playback.CurrentQueueItemId);
        Assert.Equal(
            QueueItemState.Playing,
            afterSkip.Queue.Single(item => item.Id == third.QueueItemId).State);
        Assert.Equal(
            QueueItemState.Ready,
            afterSkip.Queue.Single(item => item.Id == second.QueueItemId).State);
    }

    [SkippableFact]
    public async Task MovingThePlayingItemIsRejectedAndAdvanceOrderIsUnchanged()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);
        var songIds = await GetDemoSongIdsAsync(http, 2).ConfigureAwait(false);

        await using var guest = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(guest, room.Code, "Ada", "guest").ConfigureAwait(false)).Ack.Success);

        var first = await EnqueueAsync(guest, songIds[0]).ConfigureAwait(false);
        var second = await EnqueueAsync(guest, songIds[1]).ConfigureAwait(false);

        await guest.InvokeAsync("StartPlayback").ConfigureAwait(false);
        var started = await WaitForPlaybackStatusAsync(
                http,
                room.Code,
                PlaybackStatus.Playing,
                TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        Assert.Equal(first.QueueItemId, started.Playback.CurrentQueueItemId);
        using var errors = new ErrorCollector(guest);

        await guest.InvokeAsync(MoveQueueItemMethod, first.QueueItemId, 1).ConfigureAwait(false);
        var error = await errors
            .WaitForAsync(candidate => candidate.Code == HubErrorCodes.InvalidRequest)
            .ConfigureAwait(false);
        Assert.Equal(HubErrorCodes.InvalidRequest, error.Code);

        await guest.InvokeAsync("SkipSong").ConfigureAwait(false);
        var afterSkip = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Playing, afterSkip.Status);
        Assert.Equal(second.QueueItemId, afterSkip.Playback.CurrentQueueItemId);
        Assert.Equal(
            QueueItemState.Ended,
            afterSkip.Queue.Single(item => item.Id == first.QueueItemId).State);
    }

    [SkippableFact]
    public async Task PlaybackLifecycleStartsPausesResumesSkipsAndIdles()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);
        var songIds = await GetDemoSongIdsAsync(http, 2).ConfigureAwait(false);

        await using var guest = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(guest, room.Code, "Ada", "guest").ConfigureAwait(false)).Ack.Success);

        var first = await EnqueueAsync(guest, songIds[0]).ConfigureAwait(false);
        var second = await EnqueueAsync(guest, songIds[1]).ConfigureAwait(false);

        await guest.InvokeAsync("StartPlayback").ConfigureAwait(false);

        // Start is two-phase: the authority counts in, then starts the song with a fresh anchor.
        var countingIn = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Countdown, countingIn.Status);
        Assert.Equal(PlaybackStatus.Countdown, countingIn.Playback.Status);
        Assert.Equal(first.QueueItemId, countingIn.Playback.CurrentQueueItemId);
        Assert.Equal(0L, countingIn.Playback.PositionMs);
        Assert.Equal(4, countingIn.Playback.CountdownRemainingBeats);

        var playing = await WaitForPlaybackStatusAsync(
                http,
                room.Code,
                PlaybackStatus.Playing,
                TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        Assert.Equal(first.QueueItemId, playing.Playback.CurrentQueueItemId);

        await guest.InvokeAsync("PausePlayback").ConfigureAwait(false);
        var paused = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Paused, paused.Status);
        Assert.Equal(first.QueueItemId, paused.Playback.CurrentQueueItemId);

        await guest.InvokeAsync("ResumePlayback").ConfigureAwait(false);
        var resumed = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Playing, resumed.Status);
        Assert.Equal(first.QueueItemId, resumed.Playback.CurrentQueueItemId);

        await guest.InvokeAsync("SkipSong").ConfigureAwait(false);
        var advanced = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Playing, advanced.Status);
        Assert.Equal(second.QueueItemId, advanced.Playback.CurrentQueueItemId);
        Assert.Equal(
            QueueItemState.Ended,
            advanced.Queue.Single(item => item.Id == first.QueueItemId).State);
        Assert.Equal(
            QueueItemState.Playing,
            advanced.Queue.Single(item => item.Id == second.QueueItemId).State);

        await guest.InvokeAsync("SkipSong").ConfigureAwait(false);
        var idle = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Idle, idle.Status);
        Assert.Null(idle.Playback.CurrentQueueItemId);
    }

    [SkippableFact]
    public async Task PlaybackAuthorityBroadcastsAnchorAndPreservesPositionAcrossPauseResume()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);
        var songIds = await GetDemoSongIdsAsync(http, 2).ConfigureAwait(false);

        await using var guest = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(guest, room.Code, "Ada", "guest").ConfigureAwait(false)).Ack.Success);
        using var playback = new PlaybackCollector(guest);
        using var countdown = new CountdownCollector(guest);

        var first = await EnqueueAsync(guest, songIds[0]).ConfigureAwait(false);
        var second = await EnqueueAsync(guest, songIds[1]).ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        await guest.InvokeAsync("StartPlayback").ConfigureAwait(false);

        var countingIn = await playback
            .WaitForAsync(state => state.Status == PlaybackStatus.Countdown)
            .ConfigureAwait(false);
        Assert.Equal(first.QueueItemId, countingIn.CurrentQueueItemId);
        Assert.Equal(0L, countingIn.PositionMs);
        Assert.Equal(4, countingIn.CountdownRemainingBeats);

        var playing = await playback
            .WaitForAsync(state =>
                state.Status == PlaybackStatus.Playing
                && state.CurrentQueueItemId == first.QueueItemId)
            .ConfigureAwait(false);
        Assert.InRange(playing.PositionMs, 0, 1_000);
        Assert.Null(playing.CountdownRemainingBeats);
        Assert.InRange(
            playing.TimeAnchorUtc,
            startedAt.AddSeconds(-5),
            startedAt.AddSeconds(20));

        // The pinned wire contract: one tick per beat with remainingBeats 4 → 1 at beatMs 500.
        await countdown.WaitForCountAsync(4).ConfigureAwait(false);
        Assert.Equal(
            ExpectedCountdownBeats,
            countdown.Ticks.Select(tick => tick.RemainingBeats));
        Assert.All(countdown.Ticks, tick => Assert.Equal(500, tick.BeatMs));
        Assert.All(countdown.Ticks, tick => Assert.Equal(first.QueueItemId, tick.QueueItemId));

        await Task.Delay(200).ConfigureAwait(false);
        await guest.InvokeAsync("PausePlayback").ConfigureAwait(false);

        var paused = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Paused, paused.Playback.Status);
        Assert.Equal(first.QueueItemId, paused.Playback.CurrentQueueItemId);
        Assert.True(paused.Playback.PositionMs >= 150);

        var resumeRequestedAt = DateTimeOffset.UtcNow;
        await guest.InvokeAsync("ResumePlayback").ConfigureAwait(false);
        var resumed = await playback
            .WaitForAsync(state =>
                state.Status == PlaybackStatus.Playing
                && state.CurrentQueueItemId == first.QueueItemId
                && state.PositionMs >= paused.Playback.PositionMs)
            .ConfigureAwait(false);

        // DoD: pause/resume preserves the position within ±30 ms of the authority's PositionMs.
        // Comparing `resumed.PositionMs - paused.PositionMs` directly would conflate real drift
        // with the database write that happens between the command and the broadcast: because the
        // room is persisted before PlaybackChanged, resumed.PositionMs already includes that write
        // latency and can exceed 30 ms purely from persistence. Instead reconstruct the authority's
        // playback-start instant from the resumed broadcast itself (anchor − position). Both values
        // come from the same message, so persist and transport latency cancel; the remaining delta
        // is only the resume request's delivery into the authority. If resume had silently
        // restarted at zero, the implied start would sit ~pausedPos later and fail loudly.
        var resumedStartUtc = resumed.TimeAnchorUtc - TimeSpan.FromMilliseconds(resumed.PositionMs);
        var expectedStartUtc = resumeRequestedAt - TimeSpan.FromMilliseconds(paused.Playback.PositionMs);
        Assert.InRange(
            Math.Abs((resumedStartUtc - expectedStartUtc).TotalMilliseconds),
            0,
            30);

        await guest.InvokeAsync("SkipSong").ConfigureAwait(false);
        var afterSkip = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Playing, afterSkip.Status);
        Assert.Equal(second.QueueItemId, afterSkip.Playback.CurrentQueueItemId);
        Assert.Equal(
            QueueItemState.Ended,
            afterSkip.Queue.Single(item => item.Id == first.QueueItemId).State);
    }

    [SkippableFact]
    public async Task SetGuideVocalVolumeBroadcastReachesEveryClient()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);

        await using var guestA = await ConnectAsync().ConfigureAwait(false);
        await using var guestB = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(guestA, room.Code, "Ada", "guest").ConfigureAwait(false)).Ack.Success);
        Assert.True((await JoinAsync(guestB, room.Code, "Grace", "guest").ConfigureAwait(false)).Ack.Success);

        using var playbackA = new PlaybackCollector(guestA);
        using var playbackB = new PlaybackCollector(guestB);

        await guestA.InvokeAsync("SetGuideVocalVolume", 0.35).ConfigureAwait(false);

        var stateA = await playbackA
            .WaitForAsync(state => Math.Abs(state.GuideVocalVolume - 0.35) < 0.0001)
            .ConfigureAwait(false);
        var stateB = await playbackB
            .WaitForAsync(state => Math.Abs(state.GuideVocalVolume - 0.35) < 0.0001)
            .ConfigureAwait(false);

        Assert.Equal(0.35, stateA.GuideVocalVolume);
        Assert.Equal(0.35, stateB.GuideVocalVolume);
    }

    [SkippableFact]
    public async Task WatchdogEndsStalledPlaybackAfterDurationPlusGrace()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);
        var shortSongId = await InsertShortSongAsync(durationMs: 250).ConfigureAwait(false);

        await using var guest = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(guest, room.Code, "Ada", "guest").ConfigureAwait(false)).Ack.Success);

        var entry = await EnqueueAsync(guest, shortSongId).ConfigureAwait(false);
        await guest.InvokeAsync("StartPlayback").ConfigureAwait(false);

        var started = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Countdown, started.Status);
        Assert.Equal(entry.QueueItemId, started.Playback.CurrentQueueItemId);

        // count-in (2 s) + duration (250 ms) + 5 s grace; poll generously for a slow CI container.
        var ended = await WaitForSnapshotAsync(
                http,
                room.Code,
                snapshot => snapshot.Playback.Status == PlaybackStatus.Idle,
                TimeSpan.FromSeconds(25))
            .ConfigureAwait(false);

        Assert.Null(ended.Playback.CurrentQueueItemId);
        Assert.Equal(
            QueueItemState.Ended,
            ended.Queue.Single(item => item.Id == entry.QueueItemId).State);
    }

    [SkippableFact]
    public async Task DuplicateEndReportCannotDoubleAdvance()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);
        var songIds = await GetDemoSongIdsAsync(http, 2).ConfigureAwait(false);

        await using var stage = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(stage, room.Code, "Stage", "stage").ConfigureAwait(false)).Ack.Success);

        var first = await EnqueueAsync(stage, songIds[0]).ConfigureAwait(false);
        var second = await EnqueueAsync(stage, songIds[1]).ConfigureAwait(false);
        await stage.InvokeAsync("StartPlayback").ConfigureAwait(false);

        // Let the authority-owned count-in complete so the report targets a Playing song.
        var started = await WaitForPlaybackStatusAsync(
                http,
                room.Code,
                PlaybackStatus.Playing,
                TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        Assert.Equal(first.QueueItemId, started.Playback.CurrentQueueItemId);

        await stage
            .InvokeAsync(ReportSongEndedMethod, first.QueueItemId)
            .ConfigureAwait(false);

        var advanced = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(second.QueueItemId, advanced.Playback.CurrentQueueItemId);
        Assert.Equal(PlaybackStatus.Playing, advanced.Status);

        // The stage (or the watchdog) reports the same end again: the guard drops it because the
        // cursor already moved, so B keeps playing and the room never skips to Idle.
        await stage
            .InvokeAsync(ReportSongEndedMethod, first.QueueItemId)
            .ConfigureAwait(false);

        var afterDuplicate = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Playing, afterDuplicate.Status);
        Assert.Equal(second.QueueItemId, afterDuplicate.Playback.CurrentQueueItemId);
        Assert.Equal(
            QueueItemState.Playing,
            afterDuplicate.Queue.Single(item => item.Id == second.QueueItemId).State);
        Assert.Equal(
            QueueItemState.Ended,
            afterDuplicate.Queue.Single(item => item.Id == first.QueueItemId).State);
    }

    [SkippableFact]
    public async Task ColdRestartRehydratesQueueSequenceAndPlaybackAnchor()
    {
        RequireDocker();

        using var http = _fixture.Factory.CreateClient();
        var room = await CreateRoomAsync(http).ConfigureAwait(false);
        var songIds = await GetDemoSongIdsAsync(http, 2).ConfigureAwait(false);

        await using var guest = await ConnectAsync().ConfigureAwait(false);
        Assert.True((await JoinAsync(guest, room.Code, "Ada", "guest").ConfigureAwait(false)).Ack.Success);

        var first = await EnqueueAsync(guest, songIds[0]).ConfigureAwait(false);
        await EnqueueAsync(guest, songIds[1]).ConfigureAwait(false);
        await guest.InvokeAsync("SetGuideVocalVolume", 0.25).ConfigureAwait(false);
        await guest.InvokeAsync("StartPlayback").ConfigureAwait(false);

        // Let the count-in complete; the persisted anchor is then the playing anchor.
        await WaitForPlaybackStatusAsync(
                http,
                room.Code,
                PlaybackStatus.Playing,
                TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        await Task.Delay(250).ConfigureAwait(false);

        var before = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Playing, before.Playback.Status);
        Assert.Equal(first.QueueItemId, before.Playback.CurrentQueueItemId);

        // A brand-new host against the same database runs the real startup path: migrate, seed,
        // then rebuild the live registry from persisted rooms. No in-memory state survives.
        await using var restartedFactory = new WebApplicationFactory<ApiHost::Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(
                    "ConnectionStrings:Karaokay",
                    _fixture.ConnectionString);
                builder.UseSetting("MediaRoot", RepoPaths.DemoMediaRoot);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Features:EnableYouTubeIngestion"] = "false",
                    }));
            });

        using var restartedHttp = restartedFactory.CreateClient();
        using var health = await restartedHttp.GetAsync("/healthz").ConfigureAwait(false);
        health.EnsureSuccessStatusCode();

        var restored = await GetSnapshotAsync(restartedHttp, room.Code).ConfigureAwait(false);

        Assert.Equal(before.QueueSequence, restored.QueueSequence);
        Assert.Equal(
            before.Queue.Select(item => (item.Id, item.Position, item.State)),
            restored.Queue.Select(item => (item.Id, item.Position, item.State)));
        Assert.Equal(PlaybackStatus.Playing, restored.Playback.Status);
        Assert.Equal(before.Playback.CurrentQueueItemId, restored.Playback.CurrentQueueItemId);
        Assert.Equal(0.25, restored.Playback.GuideVocalVolume);

        // The anchor keeps flowing from the persisted start instant: the song was never paused.
        Assert.True(restored.Playback.PositionMs >= before.Playback.PositionMs);
    }

    private void RequireDocker() =>
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason ?? "Docker unavailable.");

    private static async Task<QueueAck> EnqueueAsync(HubConnection connection, Guid songId)
    {
        var ack = await connection
            .InvokeAsync<QueueAck>(EnqueueLibrarySongMethod, songId)
            .ConfigureAwait(false);
        Assert.True(ack.Success);
        return ack;
    }

    private static async Task<RoomSnapshot> GetSnapshotAsync(HttpClient http, string roomCode)
    {
        var snapshot = await http
            .GetFromJsonAsync<RoomSnapshot>($"/api/rooms/{roomCode}")
            .ConfigureAwait(false);
        return Assert.IsType<RoomSnapshot>(snapshot);
    }

    private static async Task<RoomSnapshot> WaitForPlaybackStatusAsync(
        HttpClient http,
        string roomCode,
        PlaybackStatus status,
        TimeSpan timeout) =>
        await WaitForSnapshotAsync(
                http,
                roomCode,
                snapshot => snapshot.Playback.Status == status,
                timeout)
            .ConfigureAwait(false);

    private async Task<HubConnection> ConnectAsync()
    {
        using var client = _fixture.Factory.CreateClient();
        var baseAddress = client.BaseAddress ?? new Uri("http://localhost/");

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, "/hubs/stage"), options =>
            {
                // TestServer has no real socket, so long polling is the supported transport here.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _fixture.Factory.Server.CreateHandler();
            })
            .Build();

        await connection.StartAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<(JoinAck Ack, RoomSnapshot? Snapshot)> JoinAsync(
        HubConnection connection,
        string roomCode,
        string displayName,
        string role)
    {
        var snapshotSource = new TaskCompletionSource<RoomSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = connection.On<RoomSnapshot>(
            JoinedRoomEvent,
            snapshot => snapshotSource.TrySetResult(snapshot));

        var ack = await connection
            .InvokeAsync<JoinAck>(JoinRoomMethod, new JoinRequest(roomCode, displayName, role))
            .ConfigureAwait(false);

        if (!ack.Success)
        {
            return (ack, null);
        }

        var snapshot = await snapshotSource.Task
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        return (ack, snapshot);
    }

    private static async Task<RoomResponse> CreateRoomAsync(HttpClient http)
    {
        using var response = await http.PostAsync("/api/rooms", content: null).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var room = await response.Content.ReadFromJsonAsync<RoomResponse>().ConfigureAwait(false);
        return Assert.IsType<RoomResponse>(room);
    }

    private static async Task<IReadOnlyList<Guid>> GetDemoSongIdsAsync(HttpClient http, int count)
    {
        var songs = await http
            .GetFromJsonAsync<List<SongSummaryDto>>("/api/songs")
            .ConfigureAwait(false);

        Assert.NotNull(songs);
        var ids = songs!
            .Select(song => song.EnqueueId)
            .Where(id => id is not null && Guid.TryParse(id, out _))
            .Select(id => Guid.Parse(id!))
            .Distinct()
            .Take(count)
            .ToArray();

        Assert.Equal(count, ids.Length);
        return ids;
    }

    /// <summary>
    /// Inserts a dedicated short song so the watchdog can be exercised without waiting for a real
    /// demo track to finish. The row is not a demo song, so no other test is affected.
    /// </summary>
    private async Task<Guid> InsertShortSongAsync(long durationMs)
    {
        var songId = Guid.NewGuid();
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<KaraokayDbContext>();
        database.Songs.Add(new Karaokay.Domain.Song
        {
            Id = songId,
            Title = $"Watchdog sprint {songId:N}",
            DurationMs = durationMs,
            Source = Karaokay.Domain.MediaSource.Multitrack,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await database.SaveChangesAsync().ConfigureAwait(false);
        return songId;
    }

    private static async Task<RoomSnapshot> WaitForSnapshotAsync(
        HttpClient http,
        string roomCode,
        Func<RoomSnapshot, bool> predicate,
        TimeSpan timeout)
    {
        var timeoutAt = DateTime.UtcNow + timeout;
        RoomSnapshot? last = null;

        while (DateTime.UtcNow < timeoutAt)
        {
            last = await GetSnapshotAsync(http, roomCode).ConfigureAwait(false);
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        Assert.Fail($"Timed out waiting for the expected room snapshot; last status was {last?.Status}.");
        return last!;
    }

    private static async Task WaitForDeltasAsync(
        DeltaCollector first,
        DeltaCollector second,
        int expectedCount)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeoutAt)
        {
            if (first.Deltas.Count >= expectedCount && second.Deltas.Count >= expectedCount)
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        Assert.Fail(
            $"Timed out waiting for {expectedCount} deltas; " +
            $"first had {first.Deltas.Count}, second had {second.Deltas.Count}.");
    }

    private static async Task WaitForCountAsync(DeltaCollector collector, int expectedCount)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeoutAt)
        {
            if (collector.Deltas.Count >= expectedCount)
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        Assert.Fail(
            $"Timed out waiting for {expectedCount} deltas; observed {collector.Deltas.Count}.");
    }

    private sealed record RoomResponse(string Code, string RoomId);

    private sealed class DeltaCollector : IDisposable
    {
        private readonly ConcurrentQueue<QueueDelta> _deltas = new();
        private readonly IDisposable _subscription;

        public DeltaCollector(HubConnection connection)
        {
            _subscription = connection.On<QueueDelta>(
                QueueChangedEvent,
                delta => _deltas.Enqueue(delta));
        }

        public IReadOnlyList<QueueDelta> Deltas => [.. _deltas];

        public void Dispose() => _subscription.Dispose();
    }

    private sealed class ErrorCollector : IDisposable
    {
        private readonly ConcurrentQueue<HubErrorDto> _errors = new();
        private readonly IDisposable _subscription;

        public ErrorCollector(HubConnection connection)
        {
            _subscription = connection.On<HubErrorDto>(
                HubErrorEvent,
                error => _errors.Enqueue(error));
        }

        public IReadOnlyList<HubErrorDto> Errors => [.. _errors];

        public async Task<HubErrorDto> WaitForAsync(Func<HubErrorDto, bool> predicate)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < timeoutAt)
            {
                var match = _errors.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }

            Assert.Fail("Timed out waiting for the expected HubError.");
            return null!;
        }

        public void Dispose() => _subscription.Dispose();
    }

    private sealed class PlaybackCollector : IDisposable
    {
        private readonly ConcurrentQueue<PlaybackStateDto> _states = new();
        private readonly IDisposable _subscription;

        public PlaybackCollector(HubConnection connection)
        {
            _subscription = connection.On<PlaybackStateDto>(
                PlaybackChangedEvent,
                state => _states.Enqueue(state));
        }

        public IReadOnlyList<PlaybackStateDto> States => [.. _states];

        public async Task<PlaybackStateDto> WaitForAsync(Func<PlaybackStateDto, bool> predicate)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < timeoutAt)
            {
                var match = _states.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }

            Assert.Fail("Timed out waiting for the expected PlaybackChanged broadcast.");
            return null!;
        }

        public void Dispose() => _subscription.Dispose();
    }

    private sealed class CountdownCollector : IDisposable
    {
        private readonly ConcurrentQueue<CountdownDto> _ticks = new();
        private readonly IDisposable _subscription;

        public CountdownCollector(HubConnection connection)
        {
            _subscription = connection.On<CountdownDto>(
                CountdownTickedEvent,
                tick => _ticks.Enqueue(tick));
        }

        public IReadOnlyList<CountdownDto> Ticks => [.. _ticks];

        public async Task WaitForCountAsync(int expectedCount)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < timeoutAt)
            {
                if (_ticks.Count >= expectedCount)
                {
                    return;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }

            Assert.Fail(
                $"Timed out waiting for {expectedCount} CountdownTicked beat(s); " +
                $"observed {_ticks.Count}.");
        }

        public void Dispose() => _subscription.Dispose();
    }
}
