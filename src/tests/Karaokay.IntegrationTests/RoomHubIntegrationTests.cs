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
    private const string HubErrorEvent = "HubError";

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
        var started = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(first.QueueItemId, started.Playback.CurrentQueueItemId);
        Assert.Equal(PlaybackStatus.Playing, started.Status);

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
        var playing = await GetSnapshotAsync(http, room.Code).ConfigureAwait(false);
        Assert.Equal(PlaybackStatus.Playing, playing.Status);
        Assert.Equal(PlaybackStatus.Playing, playing.Playback.Status);
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
}
