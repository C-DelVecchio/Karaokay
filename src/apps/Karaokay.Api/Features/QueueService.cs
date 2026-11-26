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
/// Queue and playback command surface for a live room (docs/ARCHITECTURE.md §5.1). These bodies own
/// the transition logic itself: every mutation is serialized per room, persisted before broadcast
/// (write-ahead rule 6), and emits a <see cref="QueueDelta"/> with a monotonic per-room sequence.
/// Playback transitions are never invoked from a hub directly — <see cref="PlaybackAuthority"/> is
/// the single writer that applies room commands from its command channel by calling into this
/// service. Enqueue/move stay on the direct service path because they mutate neither playback nor
/// cursor state.
/// </summary>
public sealed partial class QueueService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SongCatalog _catalog;
    private readonly IHubContext<StageHub, IStageHubClient> _hub;
    private readonly ILogger<QueueService> _logger;
    private readonly TimeProvider _time;

    public QueueService(
        IServiceScopeFactory scopeFactory,
        SongCatalog catalog,
        IHubContext<StageHub, IStageHubClient> hub,
        ILogger<QueueService> logger)
        : this(scopeFactory, catalog, hub, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Test seam: the anchor clock for playback transitions. Read-side position projection
    /// (<see cref="BuildPlaybackState"/>) always uses the real wall clock.
    /// </summary>
    internal QueueService(
        IServiceScopeFactory scopeFactory,
        SongCatalog catalog,
        IHubContext<StageHub, IStageHubClient> hub,
        ILogger<QueueService> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _hub = hub;
        _logger = logger;
        _time = timeProvider;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

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

        return await EnqueueSongAsync(
                room,
                song,
                participant.DisplayName,
                participant.SessionTokenId,
                QueueItemState.Ready,
                queueItemId: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a song to the room queue in an explicit lifecycle state. The library path uses
    /// <see cref="QueueItemState.Ready"/>; the YouTube ingestion path uses
    /// <see cref="QueueItemState.Downloading"/> so phones can render a progress row immediately.
    /// </summary>
    public async Task<QueueAck> EnqueueSongAsync(
        RoomState room,
        SongDto song,
        string requestedByName,
        Guid? sessionTokenId,
        QueueItemState state,
        Guid? queueItemId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(song);

        await room.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RoomQueueEntry entry;
            lock (room.SyncRoot)
            {
                entry = new RoomQueueEntry
                {
                    Id = queueItemId ?? Guid.NewGuid(),
                    SongId = song.Id,
                    Position = room.QueueEntries.Count,
                    RequestedByName = requestedByName,
                    State = state,
                    SessionTokenId = sessionTokenId,
                    RequestedAtUtc = UtcNow,
                    Song = song,
                };
                room.QueueEntries.Add(entry);
            }

            var sequence = room.NextQueueSequence();
            await PersistQueueAsync(room, cancellationToken).ConfigureAwait(false);

            await _hub.Clients.Group(room.Code)
                .QueueChanged(new QueueDelta([ToDto(entry)], [], [], sequence))
                .ConfigureAwait(false);

            LogEnqueued(_logger, room.Code, entry.Id, song.Id);
            return new QueueAck(true, entry.Id, null);
        }
        finally
        {
            room.CommandLock.Release();
        }
    }

    /// <summary>
    /// Transitions one queue row (download completion/failure) and broadcasts both the delta and
    /// the per-item state event. Returns false when the row was removed while downloading.
    /// </summary>
    public async Task<bool> UpdateQueueItemStateAsync(
        RoomState room,
        Guid queueItemId,
        QueueItemState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);

        await room.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RoomQueueEntry? entry;
            lock (room.SyncRoot)
            {
                entry = room.QueueEntries.FirstOrDefault(candidate => candidate.Id == queueItemId);
                if (entry is null || entry.State == state)
                {
                    return entry is not null;
                }

                entry.State = state;
            }

            var sequence = room.NextQueueSequence();
            await PersistQueueAsync(room, cancellationToken).ConfigureAwait(false);

            await _hub.Clients.Group(room.Code)
                .QueueChanged(new QueueDelta([], [], [ToDto(entry)], sequence))
                .ConfigureAwait(false);
            await _hub.Clients.Group(room.Code)
                .QueueItemStateChanged(entry.Id, ToWireState(entry.State))
                .ConfigureAwait(false);

            return true;
        }
        finally
        {
            room.CommandLock.Release();
        }
    }

    /// <summary>
    /// Removes a queue entry and renumbers the remainder densely. Removing the current item also
    /// resets playback to <c>Idle</c>; because that is a playback mutation, callers reach this body
    /// through the single-writer <c>PlaybackAuthority</c> command channel (§5.1 rule 6).
    /// </summary>
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

            // Removing the current item is also a playback transition (cursor cleared, room Idle),
            // so it follows the same write-ahead ordering as RunPlaybackCommandAsync: mint the
            // sequence, persist playback and queue, then fan out (§5.1 rule 6).
            var sequence = room.NextQueueSequence();

            if (playbackChanged)
            {
                await PersistRoomAsync(room, cancellationToken).ConfigureAwait(false);
            }

            await PersistQueueAsync(room, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Reorders an upcoming queue entry. <c>Playing</c> and <c>Ended</c> entries are cursor
    /// history and cannot move; a target above the playing entry is clamped to the first upcoming
    /// slot so the song plays next instead of being stranded behind the cursor (§5.1).
    /// </summary>
    public async Task<QueueMoveResult> MoveQueueItemAsync(
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
                var result = QueueOrdering.Move(
                    room.QueueEntries,
                    queueItemId,
                    newPosition,
                    room.CurrentQueueItemId);
                if (result != QueueMoveResult.Moved)
                {
                    return result;
                }

                updated = [.. room.QueueEntries];
            }

            var sequence = room.NextQueueSequence();
            await PersistQueueAsync(room, cancellationToken).ConfigureAwait(false);

            await _hub.Clients.Group(room.Code)
                .QueueChanged(new QueueDelta([], [], [.. updated.Select(ToDto)], sequence))
                .ConfigureAwait(false);

            return QueueMoveResult.Moved;
        }
        finally
        {
            room.CommandLock.Release();
        }
    }

    /// <summary>
    /// Starts the room: from <c>Idle</c>/<c>SongEnded</c> with a playable head item the room enters
    /// the authority-owned <c>Countdown</c> (docs/ARCHITECTURE.md §5.1 rule 3) and the ticker
    /// auto-transitions to <c>Playing</c> with a fresh alignment anchor. A duplicate Start while
    /// already counting in or playing is ignored so it can never re-anchor; Start while
    /// <c>Paused</c> is likewise a no-op (Resume preserves the position).
    /// </summary>
    public Task StartPlaybackAsync(RoomState room, CancellationToken cancellationToken) =>
        RunPlaybackCommandAsync(
            room,
            state =>
            {
                // Start is a no-op unless the room is Idle/SongEnded. A duplicate Start while
                // counting in or playing must not re-anchor, and a Start while Paused must not
                // silently rewind to zero with a fresh count-in: Resume is the explicit path back
                // to Playing (the UI only offers Start in Idle/SongEnded).
                if (state.Status is PlaybackStatus.Playing
                    or PlaybackStatus.Countdown
                    or PlaybackStatus.Paused)
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
                    state.CountdownRemainingBeats = 0;
                    return null;
                }

                return BeginCountdownLocked(state, current, UtcNow);
            },
            cancellationToken);

    /// <summary>
    /// Pauses playback, folding the elapsed time into <c>PausedAtPositionMs</c>. A pause during the
    /// count-in is allowed and leaves the position at zero (§5.1 rule 3).
    /// </summary>
    public Task PausePlaybackAsync(RoomState room, CancellationToken cancellationToken) =>
        RunPlaybackCommandAsync(
            room,
            state =>
            {
                if (state.Status is not (PlaybackStatus.Playing or PlaybackStatus.Countdown))
                {
                    return null;
                }

                state.PausedAtPositionMs = CalculatePositionLocked(state, UtcNow);
                state.Status = PlaybackStatus.Paused;
                state.PlaybackStartedAtUtc = null;
                state.CountdownRemainingBeats = 0;
                return FindCurrentLocked(state);
            },
            cancellationToken);

    /// <summary>Resumes from the preserved pause position with a fresh playback anchor.</summary>
    public Task ResumePlaybackAsync(RoomState room, CancellationToken cancellationToken) =>
        RunPlaybackCommandAsync(
            room,
            state =>
            {
                if (state.Status != PlaybackStatus.Paused)
                {
                    return null;
                }

                var current = FindCurrentLocked(state);
                if (current is not null)
                {
                    MarkPlayingLocked(state, current);
                }

                state.PlaybackStartedAtUtc = UtcNow.AddMilliseconds(-state.PausedAtPositionMs);
                state.PausedAtPositionMs = 0;
                state.Status = PlaybackStatus.Playing;
                state.CountdownRemainingBeats = 0;
                return current;
            },
            cancellationToken);

    /// <summary>
    /// Restarts the current song from position zero. During the count-in this restarts the count-in
    /// window; once the song is playing it rewinds immediately.
    /// </summary>
    public Task RestartSongAsync(RoomState room, CancellationToken cancellationToken) =>
        RunPlaybackCommandAsync(
            room,
            state =>
            {
                var current = FindCurrentLocked(state);
                if (current is null)
                {
                    return null;
                }

                return state.Status == PlaybackStatus.Countdown
                    ? BeginCountdownLocked(state, current, UtcNow)
                    : BeginPlayingLocked(state, current, UtcNow);
            },
            cancellationToken);

    /// <summary>Marks the current song ended and advances to the next playable item.</summary>
    public Task SkipSongAsync(RoomState room, CancellationToken cancellationToken) =>
        RunPlaybackCommandAsync(room, state => AdvanceLocked(state, UtcNow), cancellationToken);

    /// <summary>
    /// Authority-owned count-in completion (docs/ARCHITECTURE.md §5.1 rule 3). Guarded by the room's
    /// current status/item so a concurrent Pause/Skip/Start wins cleanly; the transition to
    /// <c>Playing</c> uses a FRESH anchor, which is the audio alignment anchor clients extrapolate
    /// from.
    /// </summary>
    internal Task CompleteCountdownAsync(
        RoomState room,
        Guid queueItemId,
        CancellationToken cancellationToken) =>
        RunPlaybackCommandAsync(
            room,
            state =>
            {
                if (state.Status != PlaybackStatus.Countdown || state.CurrentQueueItemId != queueItemId)
                {
                    return null;
                }

                var current = FindCurrentLocked(state);
                return current is null ? null : BeginPlayingLocked(state, current, UtcNow);
            },
            cancellationToken);

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
            return await ReportSongEndedUnderLockAsync(room, queueItemId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            room.CommandLock.Release();
        }
    }

    /// <summary>
    /// The one idempotency guard shared by the stage report and the duration watchdog
    /// (docs/ARCHITECTURE.md §5.1 rule 1, §7 Slice 4): an end is honored only when the reported
    /// item is still <see cref="RoomState.CurrentQueueItemId"/> at the moment the caller holds
    /// <see cref="RoomState.CommandLock"/>. Callers must already own that lock — the
    /// <c>PlaybackAuthority</c> does, so no second command can interleave.
    /// </summary>
    internal async Task<bool> ReportSongEndedUnderLockAsync(
        RoomState room,
        Guid queueItemId,
        CancellationToken cancellationToken)
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

            // Mint the sequence before persisting/broadcasting so the persisted value is already
            // the one clients are about to observe (write-ahead, §5.1 rule 6).
            var sequence = queueChanged ? room.NextQueueSequence() : 0;

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

            current = AdvanceLocked(room, UtcNow);

            playbackChanged = playbackBefore != PlaybackSnapshot.Capture(room);
            queueChanged = !queueBefore.SequenceEqual(CaptureQueueLocked(room));
        }

        if (!playbackChanged && !queueChanged)
        {
            return;
        }

        var sequence = queueChanged ? room.NextQueueSequence() : 0;

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

    private static RoomQueueEntry? AdvanceLocked(RoomState room, DateTime nowUtc)
    {
        var current = FindCurrentLocked(room);
        if (current is not null)
        {
            current.State = QueueItemState.Ended;
        }

        var ordered = room.QueueEntries.OrderBy(entry => entry.Position).ToList();
        var next = QueueOrdering.SelectNext(ordered, current);

        if (next is null)
        {
            room.CurrentQueueItemId = null;
            room.Status = PlaybackStatus.Idle;
            room.PlaybackStartedAtUtc = null;
            room.PausedAtPositionMs = 0;
            room.CountdownRemainingBeats = 0;
            return current;
        }

        next.State = QueueItemState.Playing;
        room.CurrentQueueItemId = next.Id;
        room.Status = PlaybackStatus.Playing;
        room.PlaybackStartedAtUtc = nowUtc;
        room.PausedAtPositionMs = 0;
        room.CountdownRemainingBeats = 0;
        return current;
    }

    /// <summary>
    /// Enters the authority-owned count-in for <paramref name="entry"/>. The cursor points at the
    /// item (so clients can preload it and phones get its id in <c>CountdownTicked</c>) while its
    /// queue-item state stays <c>Ready</c> until audio actually starts.
    /// </summary>
    private static RoomQueueEntry BeginCountdownLocked(
        RoomState room,
        RoomQueueEntry entry,
        DateTime nowUtc)
    {
        room.CurrentQueueItemId = entry.Id;
        room.Status = PlaybackStatus.Countdown;
        room.PlaybackStartedAtUtc = nowUtc;
        room.PausedAtPositionMs = 0;
        room.CountdownRemainingBeats = PlaybackTiming.CountdownBeats;
        return entry;
    }

    private static void MarkPlayingLocked(RoomState room, RoomQueueEntry entry)
    {
        foreach (var other in room.QueueEntries)
        {
            if (other != entry && other.State == QueueItemState.Playing)
            {
                other.State = QueueItemState.Ready;
            }
        }

        entry.State = QueueItemState.Playing;
    }

    private static RoomQueueEntry BeginPlayingLocked(
        RoomState room,
        RoomQueueEntry entry,
        DateTime nowUtc)
    {
        MarkPlayingLocked(room, entry);
        room.CurrentQueueItemId = entry.Id;
        room.Status = PlaybackStatus.Playing;
        room.PlaybackStartedAtUtc = nowUtc;
        room.PausedAtPositionMs = 0;
        room.CountdownRemainingBeats = 0;
        return entry;
    }

    private static RoomQueueEntry? FindCurrentLocked(RoomState room) =>
        room.CurrentQueueItemId is { } id
            ? room.QueueEntries.FirstOrDefault(entry => entry.Id == id)
            : null;

    private static long CalculatePositionLocked(RoomState room, DateTime nowUtc)
    {
        if (room.Status == PlaybackStatus.Paused)
        {
            return room.PausedAtPositionMs;
        }

        if (room.Status != PlaybackStatus.Playing || room.PlaybackStartedAtUtc is not { } started)
        {
            return 0;
        }

        return (long)Math.Max(0, (nowUtc - started).TotalMilliseconds);
    }

    private static PlaybackStateDto BuildPlaybackStateLocked(RoomState room)
    {
        var current = FindCurrentLocked(room);
        return new PlaybackStateDto(
            ToWireStatus(room.Status),
            room.CurrentQueueItemId,
            current?.Song,
            CalculatePositionLocked(room, DateTime.UtcNow),
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

        // The room row owns the monotonic sequence; write it in the same SaveChanges transaction so
        // a cold restart resumes above every sequence a client already saw (§5.2).
        var roomRow = await database.Rooms
            .FirstOrDefaultAsync(row => row.Id == room.Id, cancellationToken)
            .ConfigureAwait(false);
        if (roomRow is not null)
        {
            roomRow.QueueSequence = room.QueueSequence;
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
            row.QueueSequence = room.QueueSequence;
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
