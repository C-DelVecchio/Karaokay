using Karaokay.Api.Hubs;
using Karaokay.Contracts.Hubs;
using Karaokay.Domain;
using Karaokay.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PlaybackStatus = Karaokay.Domain.PlaybackStatus;
using QueueItemState = Karaokay.Domain.QueueItemState;

namespace Karaokay.Api.Features;

/// <summary>
/// Queue and playback command surface for a live room (docs/ARCHITECTURE.md §5.1). Every mutation
/// is serialized per room, persisted before broadcast (write-ahead rule 6), and emits a
/// <see cref="QueueDelta"/> with a monotonic per-room sequence. Full single-writer
/// <c>PlaybackAuthority</c> behavior arrives in Slice 4; this is the deliberately small, correct
/// subset needed by the phones + stage of Slice 3.
/// </summary>
public sealed partial class QueueService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SongCatalog _catalog;
    private readonly IHubContext<StageHub, IStageHubClient> _hub;
    private readonly ILogger<QueueService> _logger;

    public QueueService(
        IServiceScopeFactory scopeFactory,
        SongCatalog catalog,
        IHubContext<StageHub, IStageHubClient> hub,
        ILogger<QueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>Builds the full authoritative room snapshot used on join/rejoin.</summary>
    public static RoomSnapshot BuildSnapshot(RoomState room)
    {
        lock (room.SyncRoot)
        {
            return new RoomSnapshot(
                room.Code,
                ToWireStatus(room.Status),
                BuildPlaybackStateLocked(room),
                [.. room.QueueEntries.OrderBy(entry => entry.Position).Select(ToDto)],
                room.ParticipantsByConnection.Values.Count(participant => participant.IsGuest),
                room.QueueSequence);
        }
    }

    /// <summary>Builds the current playback projection (safe to call without the command lock).</summary>
    public static PlaybackStateDto BuildPlaybackState(RoomState room)
    {
        lock (room.SyncRoot)
        {
            return BuildPlaybackStateLocked(room);
        }
    }

    /// <summary>Enqueues a library song. Multitrack items are immediately <c>Ready</c> (§5.1).</summary>
    public async Task<QueueAck> EnqueueLibrarySongAsync(
        RoomState room,
        RoomParticipant participant,
        Guid songId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(participant);

        var song = await _catalog.FindSongAsync(songId, cancellationToken).ConfigureAwait(false);
        if (song is null)
        {
            return new QueueAck(false, Guid.Empty, HubErrorCodes.NotFound);
        }

        await room.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RoomQueueEntry entry;
            lock (room.SyncRoot)
            {
                entry = new RoomQueueEntry
                {
                    Id = Guid.NewGuid(),
                    SongId = songId,
                    Position = room.QueueEntries.Count,
                    RequestedByName = participant.DisplayName,
                    State = QueueItemState.Ready,
                    SessionTokenId = participant.SessionTokenId,
                    RequestedAtUtc = DateTime.UtcNow,
                    Song = song,
                };
                room.QueueEntries.Add(entry);
            }

            await PersistQueueAsync(room, cancellationToken).ConfigureAwait(false);

            var sequence = room.NextQueueSequence();
            await _hub.Clients.Group(room.Code)
                .QueueChanged(new QueueDelta([ToDto(entry)], [], [], sequence))
                .ConfigureAwait(false);

            LogEnqueued(_logger, room.Code, entry.Id, songId);
            return new QueueAck(true, entry.Id, null);
        }
        finally
        {
            room.CommandLock.Release();
        }
    }

    /// <summary>Removes a queue entry and renumbers the remainder densely.</summary>
    public async Task<bool> RemoveQueueItemAsync(
        RoomState room,
        Guid queueItemId,
        CancellationToken cancellationToken)
    {
        await room.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RoomQueueEntry? removed;
            List<RoomQueueEntry> updated;
            var playbackChanged = false;

            lock (room.SyncRoot)
            {
                removed = room.QueueEntries.FirstOrDefault(entry => entry.Id == queueItemId);
                if (removed is null)
                {
                    return false;
                }

                room.QueueEntries.Remove(removed);
                RenumberLocked(room.QueueEntries);
                updated = [.. room.QueueEntries];

                if (room.CurrentQueueItemId == queueItemId)
                {
                    room.CurrentQueueItemId = null;
                    room.Status = PlaybackStatus.Idle;
                    room.PlaybackStartedAtUtc = null;
                    room.PausedAtPositionMs = 0;
                    playbackChanged = true;
                }
            }

            await PersistQueueAsync(room, cancellationToken).ConfigureAwait(false);

            var sequence = room.NextQueueSequence();
            await _hub.Clients.Group(room.Code)
                .QueueChanged(new QueueDelta([], [queueItemId], [.. updated.Select(ToDto)], sequence))
                .ConfigureAwait(false);

            if (playbackChanged)
            {
                await BroadcastPlaybackAsync(room).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            room.CommandLock.Release();
        }
    }

    /// <summary>Moves a queue entry to a new dense position.</summary>
    public async Task<bool> MoveQueueItemAsync(
        RoomState room,
        Guid queueItemId,
        int newPosition,
        CancellationToken cancellationToken)
    {
        await room.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<RoomQueueEntry> updated;
            lock (room.SyncRoot)
            {
                var entry = room.QueueEntries.FirstOrDefault(item => item.Id == queueItemId);
                if (entry is null)
                {
                    return false;
                }

                room.QueueEntries.Remove(entry);
                var target = Math.Clamp(newPosition, 0, room.QueueEntries.Count);
                room.QueueEntries.Insert(target, entry);
                RenumberLocked(room.QueueEntries);
                updated = [.. room.QueueEntries];
            }

            await PersistQueueAsync(room, cancellationToken).ConfigureAwait(false);

            var sequence = room.NextQueueSequence();
            await _hub.Clients.Group(room.Code)
                .QueueChanged(new QueueDelta([], [], [.. updated.Select(ToDto)], sequence))
                .ConfigureAwait(false);

            return true;
        }
        finally
        {
            room.CommandLock.Release();
        }
    }

    /// <summary>Starts playback of the current item or the first playable queue entry.</summary>
    public Task StartPlaybackAsync(RoomState room, CancellationToken cancellationToken) =>
        RunPlaybackCommandAsync(
            room,
            static state =>
            {
                if (state.Status == PlaybackStatus.Playing)
                {
                    return null;
                }

                var current = FindCurrentLocked(state);
                if (current is null || current.State is QueueItemState.Ended or QueueItemState.Failed)
                {
                    current = state.QueueEntries
                        .OrderBy(entry => entry.Position)
                        .FirstOrDefault(entry => entry.State is QueueItemState.Ready or QueueItemState.Queued);
                }

                if (current is null)
                {
                    state.Status = PlaybackStatus.Idle;
                    state.CurrentQueueItemId = null;
                    state.PlaybackStartedAtUtc = null;
                    state.PausedAtPositionMs = 0;
                    return null;
                }

                return BeginPlayingLocked(state, current);
            },
            cancellationToken);

    /// <summary>Pauses playback, folding the elapsed time into <c>PausedAtPositionMs</c>.</summary>
    public Task PausePlaybackAsync(RoomState room, CancellationToken cancellationToken) =>
        RunPlaybackCommandAsync(
            room,
            static state =>
            {
                if (state.Status != PlaybackStatus.Playing)
                {
                    return null;
                }

                state.PausedAtPositionMs = CalculatePositionLocked(state);
                state.Status = PlaybackStatus.Paused;
                state.PlaybackStartedAtUtc = null;
                return FindCurrentLocked(state);
            },
            cancellationToken);

    /// <summary>Resumes from the preserved pause position.</summary>
    public Task ResumePlaybackAsync(RoomState room, CancellationToken cancellationToken) =>
        RunPlaybackCommandAsync(
            room,
            static state =>
            {
                if (state.Status != PlaybackStatus.Paused)
                {
                    return null;
                }

                state.PlaybackStartedAtUtc = DateTime.UtcNow.AddMilliseconds(-state.PausedAtPositionMs);
                state.PausedAtPositionMs = 0;
                state.Status = PlaybackStatus.Playing;
                return FindCurrentLocked(state);
            },
            cancellationToken);

    /// <summary>Restarts the current song from position zero.</summary>
    public Task RestartSongAsync(RoomState room, CancellationToken cancellationToken) =>
        RunPlaybackCommandAsync(
            room,
            static state =>
            {
                var current = FindCurrentLocked(state);
                if (current is null)
                {
                    return null;
                }

                return BeginPlayingLocked(state, current);
            },
            cancellationToken);

    /// <summary>Marks the current song ended and advances to the next playable item.</summary>
    public Task SkipSongAsync(RoomState room, CancellationToken cancellationToken) =>
        RunPlaybackCommandAsync(room, static state => AdvanceLocked(state), cancellationToken);

    /// <summary>
    /// Idempotent stage report: ignored unless <paramref name="queueItemId"/> is the room's current
    /// item, so a duplicate stage report can never double-advance the queue (§5.1 rule 1).
    /// </summary>
    public async Task<bool> ReportSongEndedAsync(
        RoomState room,
        Guid queueItemId,
        CancellationToken cancellationToken)
    {
        await room.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (room.SyncRoot)
            {
                if (room.CurrentQueueItemId != queueItemId)
                {
                    LogIgnoredEndedReport(_logger, room.Code, queueItemId, room.CurrentQueueItemId);
                    return false;
                }
            }

            await AdvanceUnderLockAsync(room, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            room.CommandLock.Release();
        }
    }

    /// <summary>Sets the guide-vocal bus level, clamped to 0..1, and broadcasts the new state.</summary>
    public async Task SetGuideVocalVolumeAsync(
        RoomState room,
        double volume,
        CancellationToken cancellationToken)
    {
        var clamped = double.IsNaN(volume) ? 0 : Math.Clamp(volume, 0, 1);
        await room.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (room.SyncRoot)
            {
                if (room.GuideVocalVolume == clamped)
                {
                    return;
                }

                room.GuideVocalVolume = clamped;
            }

            await PersistRoomAsync(room, cancellationToken).ConfigureAwait(false);
            await BroadcastPlaybackAsync(room).ConfigureAwait(false);
        }
        finally
        {
            room.CommandLock.Release();
        }
    }

    private async Task RunPlaybackCommandAsync(
        RoomState room,
        Func<RoomState, RoomQueueEntry?> mutate,
        CancellationToken cancellationToken)
    {
        await room.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RoomQueueEntry? current;
            bool playbackChanged;
            bool queueChanged;

            lock (room.SyncRoot)
            {
                var playbackBefore = PlaybackSnapshot.Capture(room);
                var queueBefore = CaptureQueueLocked(room);

                current = mutate(room);

                playbackChanged = playbackBefore != PlaybackSnapshot.Capture(room);
                queueChanged = !queueBefore.SequenceEqual(CaptureQueueLocked(room));
            }

            // Pause while Idle, Start on an empty queue, etc. must not consume a queue sequence.
            if (!playbackChanged && !queueChanged)
            {
                return;
            }

            if (playbackChanged)
            {
                await PersistRoomAsync(room, cancellationToken).ConfigureAwait(false);
            }

            if (queueChanged)
            {
                await PersistQueueAsync(room, cancellationToken).ConfigureAwait(false);
            }

            if (queueChanged)
            {
                var sequence = room.NextQueueSequence();
                var snapshot = BuildSnapshot(room);
                await _hub.Clients.Group(room.Code)
                    .QueueChanged(new QueueDelta([], [], [.. snapshot.Queue], sequence))
                    .ConfigureAwait(false);
            }

            if (playbackChanged)
            {
                await BroadcastPlaybackAsync(room).ConfigureAwait(false);
            }

            if (queueChanged && current is not null)
            {
                await _hub.Clients.Group(room.Code)
                    .QueueItemStateChanged(current.Id, ToWireState(current.State))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            room.CommandLock.Release();
        }
    }

    private async Task AdvanceUnderLockAsync(RoomState room, CancellationToken cancellationToken)
    {
        RoomQueueEntry? current;
        bool playbackChanged;
        bool queueChanged;

        lock (room.SyncRoot)
        {
            var playbackBefore = PlaybackSnapshot.Capture(room);
            var queueBefore = CaptureQueueLocked(room);

            current = AdvanceLocked(room);

            playbackChanged = playbackBefore != PlaybackSnapshot.Capture(room);
            queueChanged = !queueBefore.SequenceEqual(CaptureQueueLocked(room));
        }

        if (!playbackChanged && !queueChanged)
        {
            return;
        }

        if (playbackChanged)
        {
            await PersistRoomAsync(room, cancellationToken).ConfigureAwait(false);
        }

        if (queueChanged)
        {
            await PersistQueueAsync(room, cancellationToken).ConfigureAwait(false);
        }

        if (queueChanged)
        {
            var sequence = room.NextQueueSequence();
            RoomSnapshot snapshot = BuildSnapshot(room);
            await _hub.Clients.Group(room.Code)
                .QueueChanged(new QueueDelta([], [], [.. snapshot.Queue], sequence))
                .ConfigureAwait(false);
        }

        if (playbackChanged)
        {
            await BroadcastPlaybackAsync(room).ConfigureAwait(false);
        }

        if (queueChanged && current is not null)
        {
            await _hub.Clients.Group(room.Code)
                .QueueItemStateChanged(current.Id, ToWireState(current.State))
                .ConfigureAwait(false);
        }
    }

    private static (Guid Id, int Position, QueueItemState State)[] CaptureQueueLocked(RoomState room) =>
        [.. room.QueueEntries.Select(entry => (entry.Id, entry.Position, entry.State))];

    private readonly record struct PlaybackSnapshot(
        PlaybackStatus Status,
        Guid? CurrentQueueItemId,
        DateTime? PlaybackStartedAtUtc,
        long PausedAtPositionMs,
        double GuideVocalVolume,
        int CountdownRemainingBeats)
    {
        public static PlaybackSnapshot Capture(RoomState room) => new(
            room.Status,
            room.CurrentQueueItemId,
            room.PlaybackStartedAtUtc,
            room.PausedAtPositionMs,
            room.GuideVocalVolume,
            room.CountdownRemainingBeats);
    }

    private static RoomQueueEntry? AdvanceLocked(RoomState room)
    {
        var current = FindCurrentLocked(room);
        if (current is not null)
        {
            current.State = QueueItemState.Ended;
        }

        var next = room.QueueEntries
            .OrderBy(entry => entry.Position)
            .FirstOrDefault(entry =>
                entry != current
                && (current is null || entry.Position > current.Position)
                && entry.State is QueueItemState.Ready or QueueItemState.Queued);

        if (next is null)
        {
            room.CurrentQueueItemId = null;
            room.Status = PlaybackStatus.Idle;
            room.PlaybackStartedAtUtc = null;
            room.PausedAtPositionMs = 0;
            return current;
        }

        next.State = QueueItemState.Playing;
        room.CurrentQueueItemId = next.Id;
        room.Status = PlaybackStatus.Playing;
        room.PlaybackStartedAtUtc = DateTime.UtcNow;
        room.PausedAtPositionMs = 0;
        return current;
    }

    private static RoomQueueEntry BeginPlayingLocked(RoomState room, RoomQueueEntry entry)
    {
        foreach (var other in room.QueueEntries)
        {
            if (other != entry && other.State == QueueItemState.Playing)
            {
                other.State = QueueItemState.Ready;
            }
        }

        entry.State = QueueItemState.Playing;
        room.CurrentQueueItemId = entry.Id;
        room.Status = PlaybackStatus.Playing;
        room.PlaybackStartedAtUtc = DateTime.UtcNow;
        room.PausedAtPositionMs = 0;
        return entry;
    }

    private static RoomQueueEntry? FindCurrentLocked(RoomState room) =>
        room.CurrentQueueItemId is { } id
            ? room.QueueEntries.FirstOrDefault(entry => entry.Id == id)
            : null;

    private static long CalculatePositionLocked(RoomState room)
    {
        if (room.Status == PlaybackStatus.Paused)
        {
            return room.PausedAtPositionMs;
        }

        if (room.Status != PlaybackStatus.Playing || room.PlaybackStartedAtUtc is not { } started)
        {
            return 0;
        }

        return (long)Math.Max(0, (DateTime.UtcNow - started).TotalMilliseconds);
    }

    private static PlaybackStateDto BuildPlaybackStateLocked(RoomState room)
    {
        var current = FindCurrentLocked(room);
        return new PlaybackStateDto(
            ToWireStatus(room.Status),
            room.CurrentQueueItemId,
            current?.Song,
            CalculatePositionLocked(room),
            DateTimeOffset.UtcNow,
            room.GuideVocalVolume,
            room.Status == PlaybackStatus.Countdown ? room.CountdownRemainingBeats : null);
    }

    private static void RenumberLocked(List<RoomQueueEntry> entries)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index].Position = index;
        }
    }

    private async Task BroadcastPlaybackAsync(RoomState room)
    {
        var state = BuildPlaybackState(room);
        await _hub.Clients.Group(room.Code).PlaybackChanged(state).ConfigureAwait(false);
    }

    private static QueueItemDto ToDto(RoomQueueEntry entry) => new(
        entry.Id,
        entry.SongId,
        entry.Position,
        entry.RequestedByName,
        ToWireState(entry.State));

    private static Karaokay.Contracts.Hubs.PlaybackStatus ToWireStatus(PlaybackStatus status) =>
        (Karaokay.Contracts.Hubs.PlaybackStatus)(int)status;

    private static Karaokay.Contracts.Hubs.QueueItemState ToWireState(QueueItemState state) =>
        (Karaokay.Contracts.Hubs.QueueItemState)(int)state;

    private async Task PersistQueueAsync(RoomState room, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
        if (database is null)
        {
            return;
        }

        var entries = room.Queue;
        var ids = entries.Select(entry => entry.Id).ToHashSet();

        var rows = await database.QueueItems
            .Where(item => item.RoomId == room.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        database.QueueItems.RemoveRange(rows.Where(row => !ids.Contains(row.Id)));

        foreach (var entry in entries)
        {
            var row = rows.FirstOrDefault(candidate => candidate.Id == entry.Id);
            if (row is null)
            {
                if (!await database.Songs.AnyAsync(song => song.Id == entry.SongId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    LogMissingSong(_logger, entry.SongId, room.Code);
                    continue;
                }

                row = new QueueItem
                {
                    Id = entry.Id,
                    RoomId = room.Id,
                    SongId = entry.SongId,
                    RequestedAtUtc = entry.RequestedAtUtc,
                };
                database.QueueItems.Add(row);
            }

            row.Position = entry.Position;
            row.RequestedByName = entry.RequestedByName;
            row.State = entry.State;
            row.SessionTokenId = entry.SessionTokenId;
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistRoomAsync(RoomState room, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
        if (database is null)
        {
            return;
        }

        var row = await database.Rooms
            .FirstOrDefaultAsync(candidate => candidate.Id == room.Id, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        lock (room.SyncRoot)
        {
            row.Status = room.Status;
            row.CurrentQueueItemId = room.CurrentQueueItemId;
            row.PlaybackStartedAtUtc = room.PlaybackStartedAtUtc;
            row.PausedAtPositionMs = room.PausedAtPositionMs;
            row.GuideVocalVolume = room.GuideVocalVolume;
            row.CountdownRemainingBeats = room.CountdownRemainingBeats;
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Room {RoomCode} enqueued item {QueueItemId} (song {SongId})")]
    private static partial void LogEnqueued(ILogger logger, string roomCode, Guid queueItemId, Guid songId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Room {RoomCode} ignored ended report for {QueueItemId} (current {CurrentQueueItemId})")]
    private static partial void LogIgnoredEndedReport(
        ILogger logger,
        string roomCode,
        Guid queueItemId,
        Guid? currentQueueItemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped persisting queue item for missing song {SongId} in room {RoomCode}")]
    private static partial void LogMissingSong(ILogger logger, Guid songId, string roomCode);
}
