using System.Collections.Concurrent;
using System.Threading.Channels;
using Karaokay.Api.Hubs;
using Karaokay.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using PlaybackStatus = Karaokay.Domain.PlaybackStatus;
using QueueItemState = Karaokay.Domain.QueueItemState;

namespace Karaokay.Api.Features;

/// <summary>
/// Single-writer playback authority (docs/ARCHITECTURE.md §5.1). Hub playback methods enqueue a
/// <see cref="RoomCommand"/> on the bounded command channel; this hosted service is the only
/// component that drives room playback transitions, so command ordering and broadcast ordering can
/// never interleave. Every durable transition is persisted before it is broadcast (write-ahead,
/// §5.1 rule 6) by the shared <see cref="QueueService"/> transition bodies; the count-in tick is
/// derived progress and broadcasts without a DB write.
/// </summary>
/// <remarks>
/// The service also owns the time-driven parts of the state machine:
/// <list type="bullet">
/// <item>the authority-owned count-in (§5.1 rule 3): after Start the room sits in
/// <c>Countdown</c> for four 500 ms beats, broadcasting <c>CountdownTicked(4 → 1)</c>, then
/// auto-transitions to <c>Playing</c> with a fresh alignment anchor. The count-in is start-only:
/// advance (song ended / skip) goes straight to <c>Playing</c> and never re-enters it;</item>
/// <item>the <c>AwaitingDownload</c> completion (§5.1 rule 3): a room started on a still-downloading
/// head is parked, and this loop begins the count-in as soon as the cursor item becomes
/// <c>Ready</c>;</item>
/// <item>the duration watchdog — a room left <c>Playing</c> past
/// <c>PlaybackStartedAtUtc + Song.DurationMs + 5s</c> is ended through the same advance path as a
/// stage report, sharing its <c>queueItemId == CurrentQueueItemId</c> idempotency guard. A room in
/// <c>Countdown</c> is never a watchdog candidate (it is not <c>Playing</c>).</item>
/// </list>
/// </remarks>
public sealed partial class PlaybackAuthority : BackgroundService
{
    /// <summary>
    /// Command channel capacity. Playback commands are tiny and already serialized per room; a
    /// generous bound only exists to keep a runaway client from growing the queue without limit.
    /// </summary>
    public const int CommandCapacity = 256;

    /// <summary>
    /// Upper bound on how long a hub invocation waits to place its command on the channel. A
    /// stalled consumer must surface as a failed invocation instead of hanging the caller forever.
    /// </summary>
    public static readonly TimeSpan CommandWriteTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Grace period past the song duration before the watchdog ends a stalled song.</summary>
    public static readonly TimeSpan WatchdogGrace = TimeSpan.FromSeconds(5);

    /// <summary>How often the watchdog/countdown loop runs.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly Channel<PlaybackCommand> _commands;
    private readonly ConcurrentDictionary<Guid, CountdownCursor> _countdownCursors = new();
    private readonly RoomRegistry _registry;
    private readonly QueueService _queue;
    private readonly IHubContext<StageHub, IStageHubClient> _hub;
    private readonly ILogger<PlaybackAuthority> _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _commandWriteTimeout;

    public PlaybackAuthority(
        RoomRegistry registry,
        QueueService queue,
        IHubContext<StageHub, IStageHubClient> hub,
        ILogger<PlaybackAuthority> logger)
        : this(registry, queue, hub, logger, TimeProvider.System, TickInterval)
    {
    }

    /// <summary>Test seam: injects the wall clock and tick cadence for deterministic unit tests.</summary>
    internal PlaybackAuthority(
        RoomRegistry registry,
        QueueService queue,
        IHubContext<StageHub, IStageHubClient> hub,
        ILogger<PlaybackAuthority> logger,
        TimeProvider timeProvider,
        TimeSpan tickInterval,
        TimeSpan? commandWriteTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _registry = registry;
        _queue = queue;
        _hub = hub;
        _logger = logger;
        _time = timeProvider;
        _tickInterval = tickInterval > TimeSpan.Zero ? tickInterval : TickInterval;
        _commandWriteTimeout = commandWriteTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : CommandWriteTimeout;

        _commands = Channel.CreateBounded<PlaybackCommand>(
            new BoundedChannelOptions(CommandCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    /// <summary>
    /// Enqueues a playback command and completes when the authority has applied it (persist then
    /// broadcast). Callers therefore observe the same consistent state a subsequent snapshot shows,
    /// without ever mutating playback state themselves. The result is the command outcome: for
    /// <see cref="RoomCommandTypes.RemoveQueueItem"/> it reports whether the item existed, so the
    /// hub can keep its <c>NotFound</c> error; every other command reports whether it was handled.
    /// </summary>
    public async Task<bool> EnqueueAsync(RoomCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // A stalled consumer would otherwise make WriteAsync wait forever on a full channel and hang
        // the hub invocation. Fail fast (the caller sees a hub error) instead of leaking the call.
        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writeTimeout.CancelAfter(_commandWriteTimeout);
        try
        {
            await _commands.Writer
                .WriteAsync(new PlaybackCommand(command, completion), writeTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The playback command channel did not accept {command.Type} for room "
                + $"{command.RoomCode} within {_commandWriteTimeout}.");
        }

        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Test seam: number of commands queued but not yet consumed.</summary>
    internal int PendingCommandCount => _commands.Reader.Count;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_logger);
        await Task.WhenAll(
                ConsumeCommandsAsync(stoppingToken),
                RunTickLoopAsync(stoppingToken))
            .ConfigureAwait(false);
        LogStopped(_logger);
    }

    /// <summary>Applies one command. The consume loop is the only caller.</summary>
    internal async Task<bool> HandleCommandAsync(RoomCommand command, CancellationToken cancellationToken)
    {
        var room = _registry.Find(command.RoomCode);
        if (room is null)
        {
            LogUnknownRoom(_logger, command.Type, command.RoomCode);
            return false;
        }

        switch (command.Type)
        {
            case RoomCommandTypes.StartPlayback:
                await _queue.StartPlaybackAsync(room, cancellationToken).ConfigureAwait(false);
                return true;
            case RoomCommandTypes.PausePlayback:
                await _queue.PausePlaybackAsync(room, cancellationToken).ConfigureAwait(false);
                return true;
            case RoomCommandTypes.ResumePlayback:
                await _queue.ResumePlaybackAsync(room, cancellationToken).ConfigureAwait(false);
                return true;
            case RoomCommandTypes.SkipSong:
                await _queue.SkipSongAsync(room, cancellationToken).ConfigureAwait(false);
                return true;
            case RoomCommandTypes.RestartSong:
                await _queue.RestartSongAsync(room, cancellationToken).ConfigureAwait(false);
                return true;
            case RoomCommandTypes.RemoveQueueItem:
                // Removal lives here because deleting the current item also resets playback to
                // Idle; the queue mutation and that reset happen under one CommandLock so no other
                // command can interleave (§5.1 rule 6). The bool is the not-found signal.
                return command.QueueItemId is { } removedQueueItemId
                    && await _queue
                        .RemoveQueueItemAsync(room, removedQueueItemId, cancellationToken)
                        .ConfigureAwait(false);
            case RoomCommandTypes.ReportSongEnded:
                if (command.QueueItemId is { } endedQueueItemId)
                {
                    await _queue
                        .ReportSongEndedAsync(room, endedQueueItemId, cancellationToken)
                        .ConfigureAwait(false);
                }

                return true;
            case RoomCommandTypes.SetGuideVocalVolume:
                if (command.Volume is { } volume)
                {
                    await _queue
                        .SetGuideVocalVolumeAsync(room, volume, cancellationToken)
                        .ConfigureAwait(false);
                }

                return true;
            default:
                // Enqueue/move stay on the direct service path (§5.1 rule 6 only governs playback
                // and cursor state); a command type not listed above is not authority-owned.
                LogUnhandledCommand(_logger, command.Type, room.Code);
                return false;
        }
    }

    /// <summary>
    /// One watchdog/countdown pass over every live room. Exposed internally so unit tests can drive
    /// it with a fake clock instead of waiting for wall-clock ticks.
    /// </summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        foreach (var code in _registry.ActiveCodes)
        {
            var room = _registry.Find(code);
            if (room is null)
            {
                continue;
            }

            try
            {
                await TickRoomAsync(room, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogTickFailed(_logger, room.Code, exception);
            }
        }
    }

    private async Task TickRoomAsync(RoomState room, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        // Cheap, lock-free pre-filter: idle/paused rooms cost one SyncRoot read per tick.
        PlaybackStatus status;
        Guid? currentQueueItemId = null;
        DateTime? playbackAnchorUtc = null;
        long durationMs = 0;
        QueueItemState currentState = QueueItemState.Queued;

        lock (room.SyncRoot)
        {
            status = room.Status;
            if (status is PlaybackStatus.Playing
                or PlaybackStatus.Countdown
                or PlaybackStatus.AwaitingDownload)
            {
                currentQueueItemId = room.CurrentQueueItemId;
                playbackAnchorUtc = room.PlaybackStartedAtUtc;
                var currentEntry = currentQueueItemId is { } id
                    ? room.QueueEntries.FirstOrDefault(entry => entry.Id == id)
                    : null;
                durationMs = currentEntry?.Song?.DurationMs ?? 0;
                currentState = currentEntry?.State ?? QueueItemState.Queued;
            }
        }

        if (status == PlaybackStatus.AwaitingDownload && currentQueueItemId is { } awaitingId)
        {
            // §5.1 rule 3: Start on a downloading head parks the room; the download completion
            // resumes it here, through the same QueueService transition body the single writer uses.
            if (currentState == QueueItemState.Ready)
            {
                await _queue
                    .CompleteAwaitingDownloadAsync(room, awaitingId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (currentState is QueueItemState.Failed or QueueItemState.Ended)
            {
                await _queue
                    .CancelAwaitingDownloadAsync(room, awaitingId, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (status == PlaybackStatus.Playing
            && currentQueueItemId is not null
            && IsWatchdogExpired(playbackAnchorUtc, durationMs, now))
        {
            await EndExpiredSongAsync(room, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (status == PlaybackStatus.Countdown && currentQueueItemId is { } countdownItemId)
        {
            await TickCountdownAsync(
                    room,
                    countdownItemId,
                    playbackAnchorUtc,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Leaving Countdown (pause/skip/start/end) stops the ring.
        _countdownCursors.TryRemove(room.Id, out _);
    }

    /// <summary>
    /// Count-in ticker. The countdown anchor is persisted in <c>PlaybackStartedAtUtc</c>, so a cold
    /// restart mid-count-in resumes the same window instead of restarting it. Beats remaining are
    /// derived from elapsed time (4 → 1) and each beat is broadcast exactly once; after the full
    /// window the authority transitions the room to <c>Playing</c> with a fresh anchor.
    /// </summary>
    private async Task TickCountdownAsync(
        RoomState room,
        Guid queueItemId,
        DateTime? anchorUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (anchorUtc is not { } anchor)
        {
            // Degenerate state: a count-in without an anchor cannot tick; complete it immediately.
            _countdownCursors.TryRemove(room.Id, out _);
            await _queue.CompleteCountdownAsync(room, queueItemId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var elapsedMs = Math.Max(0, (now.UtcDateTime - anchor).TotalMilliseconds);
        var beatsElapsed = (int)Math.Floor(elapsedMs / PlaybackTiming.CountdownBeatMs);
        var windowComplete = beatsElapsed >= PlaybackTiming.CountdownBeats;
        var targetBeat = windowComplete
            ? 1
            : Math.Max(1, PlaybackTiming.CountdownBeats - beatsElapsed);

        if (!_countdownCursors.TryGetValue(room.Id, out var cursor) || cursor.AnchorUtc != anchor)
        {
            cursor = new CountdownCursor(anchor);

            // Cold restart mid-count-in: this process has no cursor, but every beat whose 500 ms
            // slot already elapsed before `targetBeat` was broadcast by the previous process.
            // Resume at the next due beat instead of replaying 4 → 1, which phones would see as a
            // duplicate `4`. A count-in started in this process begins with the sentinel and does
            // emit beat 4 (and catches up if the loop ran late).
            if (room.CountdownAwaitingResume)
            {
                cursor.LastBroadcastRemainingBeats = Math.Min(
                    PlaybackTiming.CountdownBeats + 1,
                    targetBeat + 1);
            }

            _countdownCursors[room.Id] = cursor;
            room.CountdownAwaitingResume = false;
        }

        var nextBeat = cursor.LastBroadcastRemainingBeats == int.MinValue
            ? PlaybackTiming.CountdownBeats
            : cursor.LastBroadcastRemainingBeats - 1;

        if (nextBeat >= targetBeat)
        {
            List<int> beats = [];
            await room.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (room.Status != PlaybackStatus.Countdown
                    || room.CurrentQueueItemId != queueItemId
                    || room.PlaybackStartedAtUtc != anchor)
                {
                    return;
                }

                // Compute the beats and keep the in-memory projection consistent under the command
                // lock, then release before awaiting the fan-out: a slow client must never block
                // Pause/Skip for the length of the count-in (§5.1 rule 6).
                for (var beat = nextBeat; beat >= targetBeat; beat--)
                {
                    beats.Add(beat);
                }

                if (beats.Count > 0)
                {
                    // Derived progress, not a durable transition: this is the one broadcast that
                    // does not persist first. The durable Countdown → Playing step below does.
                    cursor.LastBroadcastRemainingBeats = beats[^1];
                    lock (room.SyncRoot)
                    {
                        room.CountdownRemainingBeats = beats[^1];
                    }
                }
            }
            finally
            {
                room.CommandLock.Release();
            }

            foreach (var beat in beats)
            {
                await _hub.Clients.Group(room.Code)
                    .CountdownTicked(new CountdownDto(
                        queueItemId,
                        beat,
                        PlaybackTiming.CountdownBeatMs))
                    .ConfigureAwait(false);
            }
        }

        if (!windowComplete)
        {
            return;
        }

        _countdownCursors.TryRemove(room.Id, out _);
        LogCountdownCompleted(_logger, room.Code, queueItemId);
        await _queue.CompleteCountdownAsync(room, queueItemId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Watchdog: ends the current song through <see cref="QueueService.ReportSongEndedUnderLockAsync"/>
    /// so the stage report and the watchdog share one <c>queueItemId == CurrentQueueItemId</c> guard.
    /// Paused and counting-in rooms are exempt by construction (only <c>Playing</c> is considered).
    /// </summary>
    private async Task EndExpiredSongAsync(
        RoomState room,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await room.CommandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid? expiredQueueItemId = null;
            long durationMs = 0;
            DateTime? startedAtUtc = null;

            lock (room.SyncRoot)
            {
                if (room.Status == PlaybackStatus.Playing && room.CurrentQueueItemId is { } id)
                {
                    var duration = room.QueueEntries
                        .FirstOrDefault(entry => entry.Id == id)?.Song?.DurationMs ?? 0;

                    if (IsWatchdogExpired(room.PlaybackStartedAtUtc, duration, now))
                    {
                        expiredQueueItemId = id;
                        durationMs = duration;
                        startedAtUtc = room.PlaybackStartedAtUtc;
                    }
                }
            }

            if (expiredQueueItemId is not { } queueItemId)
            {
                return;
            }

            LogWatchdogFired(_logger, room.Code, queueItemId, durationMs, startedAtUtc);

            // The guard inside re-checks CurrentQueueItemId under this same lock: if a command
            // advanced the room between the read and here, the firewall drops this end.
            await _queue
                .ReportSongEndedUnderLockAsync(room, queueItemId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            room.CommandLock.Release();
        }
    }

    private static bool IsWatchdogExpired(DateTime? startedAtUtc, long durationMs, DateTimeOffset now)
    {
        if (startedAtUtc is not { } started || durationMs <= 0)
        {
            return false;
        }

        // Compare in UTC ticks: persisted timestamptz values come back as UTC (or Unspecified) and
        // the numeric value is what matters, not the local offset.
        var deadline = started
            .AddMilliseconds(durationMs)
            .Add(WatchdogGrace);

        return now.UtcDateTime >= deadline;
    }

    private async Task ConsumeCommandsAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _commands.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                while (_commands.Reader.TryRead(out var command))
                {
                    await ApplyCommandAsync(command, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: apply what is already queued instead of dropping acknowledged work.
            await DrainCommandsAsync().ConfigureAwait(false);
        }
        finally
        {
            _commands.Writer.TryComplete();
        }
    }

    private async Task ApplyCommandAsync(PlaybackCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var handled = await HandleCommandAsync(command.Command, cancellationToken)
                .ConfigureAwait(false);
            command.Completion.TrySetResult(handled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            command.Completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            LogCommandFailed(
                _logger,
                command.Command.Type,
                command.Command.RoomCode,
                exception);
            command.Completion.TrySetException(exception);
        }
    }

    private async Task DrainCommandsAsync()
    {
        var drained = 0;

        while (_commands.Reader.TryRead(out var command))
        {
            drained++;
            try
            {
                var handled = await HandleCommandAsync(command.Command, CancellationToken.None)
                    .ConfigureAwait(false);
                command.Completion.TrySetResult(handled);
            }
            catch (Exception exception)
            {
                LogCommandFailed(
                    _logger,
                    command.Command.Type,
                    command.Command.RoomCode,
                    exception);
                command.Completion.TrySetException(exception);
            }
        }

        if (drained > 0)
        {
            LogCommandsDrained(_logger, drained);
        }
    }

    private async Task RunTickLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_tickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private sealed record PlaybackCommand(
        RoomCommand Command,
        TaskCompletionSource<bool> Completion);

    /// <summary>
    /// Per-room count-in broadcast cursor. Keyed on the persisted anchor so a restarted count-in
    /// (Restart during Countdown) re-emits beat 4.
    /// </summary>
    private sealed class CountdownCursor(DateTime anchorUtc)
    {
        public DateTime AnchorUtc { get; } = anchorUtc;

        /// <summary>Last beat pushed for this count-in; <see cref="int.MinValue"/> means none yet.</summary>
        public int LastBroadcastRemainingBeats { get; set; } = int.MinValue;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Playback authority started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Playback authority stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Playback authority dropped {CommandType} for unknown room {RoomCode}")]
    private static partial void LogUnknownRoom(ILogger logger, string commandType, string roomCode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Playback authority does not own {CommandType} (room {RoomCode})")]
    private static partial void LogUnhandledCommand(ILogger logger, string commandType, string roomCode);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Playback command {CommandType} for room {RoomCode} failed")]
    private static partial void LogCommandFailed(
        ILogger logger,
        string commandType,
        string roomCode,
        Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Playback authority drained {Count} queued command(s) during shutdown")]
    private static partial void LogCommandsDrained(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Count-in finished for {QueueItemId} in room {RoomCode}; starting playback")]
    private static partial void LogCountdownCompleted(ILogger logger, string roomCode, Guid queueItemId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Watchdog ending stalled song {QueueItemId} in room {RoomCode} after {DurationMs} ms (anchor {StartedAtUtc:o})")]
    private static partial void LogWatchdogFired(
        ILogger logger,
        string roomCode,
        Guid queueItemId,
        long durationMs,
        DateTime? startedAtUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Playback authority tick for room {RoomCode} failed")]
    private static partial void LogTickFailed(ILogger logger, string roomCode, Exception exception);
}
