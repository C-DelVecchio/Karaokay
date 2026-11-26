# Slice 4 Code Walkthrough — one writer, one clock, one stage

This is a guided read of Slice 4: the work that answered *who is allowed to move playback, and how
does every screen converge on the same position?* Slice 3 gave the party a shared queue; Slice 4
gave that queue a single driver, a count-in of its own, a watchdog, a cold-start memory, and a stage
that actually plays the room's song. It is written for the repo owner as a junior-developer tour:
every term is defined where it first matters, the code claims are tied to real files, and the last
sections collect the questions that are easy to ask twice.

Read this beside the preserved snapshots of earlier work:

- [`slice-1-walkthrough.md`](slice-1-walkthrough.md) — MIDI, `lyrics.json`, FFmpeg, and the importer.
- [`slice-2-walkthrough.md`](slice-2-walkthrough.md) — media endpoints, Web Audio, and the Canvas
  renderer as the project stood after Slice 2.
- [`slice-3-walkthrough.md`](slice-3-walkthrough.md) — rooms, the command lock, the queue engine, the
  M1 reorder bug, and the phone remote. Slice 4 corrects several promises that walkthrough made.
- [`slice-5-walkthrough.md`](slice-5-walkthrough.md) — the YouTube ingestion work that landed early.
  It is the companion to this document, not a successor.
- [`../PROJECT_STATE.md`](../PROJECT_STATE.md) — the living snapshot. If this walkthrough and that
  snapshot disagree, the snapshot plus the code win, and one of them should be fixed.
- [`../adr/007-slice-4-5-deliverable-ordering.md`](../adr/007-slice-4-5-deliverable-ordering.md) —
  why a commit titled Slice 4 contains Slice 5.
- [`../adr/008-slice-4-playback-authority-decisions.md`](../adr/008-slice-4-playback-authority-decisions.md)
  — the implementation choices this slice actually made.
- [`../review/slice-4-review.md`](../review/slice-4-review.md) — the review trail, including the
  completion follow-up and the still-open nits.

## Read the ordering first, because it confuses everyone

The git history does **not** match the slice numbers. `git log --oneline` on this tree shows:

```text
0a7c92c chore: add AGENTS.md and update opencode config
8265eac fix(slice-4): address code review findings
28fa633 feat(slice-4): playback authority, clock sync, authoritative stage playback & rehydration
eba8078 fix(slice-4-tests): repair two integration tests that failed in CI since 324da8a
50cb366 docs(slice-4): record review verdicts, ADR-007 ordering correction, and truthful state
a558150 fix(slice-4-review): resolve majors M3-M5, enforce H.264/AAC, harden CI and contract tests
324da8a feat(slice-4): YouTube search, yt-dlp download worker, stage video mode & guest YouTube UI
```

- **`324da8a`** is titled `feat(slice-4)` but its contents implement the roadmap's **Slice 5**
  (YouTube search, the `yt-dlp` download worker, cache identity, progress streaming, the guest
  YouTube tab, and the stage video surface). It landed *before* the documented Slice 4.
- **ADR-007** recorded that and kept the original roadmap unchanged: the YouTube work is credited as
  early Slice 5, and the documented Slice 4 stays owed.
- **`28fa633`** is the commit that actually implements the documented Slice 4 (playback authority,
  count-in, watchdog, clock sync, authoritative stage playback, cold-start rehydration).
  **`8265eac`** is the review follow-up that closed the gaps that pass found, and **`eba8078`**
  repaired two integration tests that had always been Docker-skipped locally.

So this document is about *the documented Slice 4*, implemented in `28fa633` + `8265eac`. The
YouTube feature has its own walkthrough, [`slice-5-walkthrough.md`](slice-5-walkthrough.md). Test
counts below are quoted from [`../PROJECT_STATE.md`](../PROJECT_STATE.md) §2.5 and
[`../review/slice-4-review.md`](../review/slice-4-review.md) rather than re-counted here: the final
tree reports **197 unit + 11 architecture + 31 integration passing** (239 passed of 267 total), with
**28 Docker-gated integration tests skipped locally**, and **Vitest 133 tests in 16 files**. The
Playwright lane is owned by a parallel web-client change and is *not* claimed green by the backend.

---

## 1. What Slice 4 set out to solve

Slices 1–3 answered:

1. *Where do legal songs and lyric timelines come from?* (Slice 1.)
2. *How does a browser fetch a song and paint synchronized lyrics?* (Slice 2.)
3. *How do many phones share one queue while one stage stays the single source of truth?* (Slice 3.)

Slice 3 shipped a deliberately small playback subset: start, pause, resume, skip, restart, and an
idempotent stage end report. Those methods each took the room `CommandLock`, mutated memory, persisted,
and broadcast. That worked, but it left two questions open:

- **Who is the writer?** With several hub methods each able to mutate playback, ordering was only as
  good as every call site remembering the same lock-and-persist dance.
- **What does "the same position" mean?** A phone computed a position from a wall-clock anchor, the
  stage played local demo audio that ignored the room, and the database was written but never read
  back. There was no count-in, and nobody noticed a stage that went silent.

Slice 4's target loop:

```text
   phone Start ──▶ RoomCommand ──▶ [bounded channel] ──▶ PlaybackAuthority
                                                             │
                              ┌──────────────────────────────┼─────────────────────────┐
                              ▼                              ▼                         ▼
                        Countdown (4 × 500 ms)         Playing / Paused           watchdog tick
                        CountdownTicked 4→1            persist → broadcast        duration + 5 s
                              │                              │                         │
                              └──────────────▶ every screen ◀─┘─────────────────────────┘
                                                stage aligns ONCE to server anchor
                                                audio is ground truth afterwards
```

Three constraints shape the design:

- **One writer for playback.** Enqueue and move may stay on the direct path because they touch neither
  playback nor the cursor. Everything that moves the cursor — including *removing* the current item,
  which resets playback — goes through one authority.
- **Persist before broadcast, and one transaction per transition.** A crash must not leave the room
  `Idle` while the removed row is still queued, and a client must never see a state the database
  rejected.
- **The stage is authoritative about its own audio, not about time.** The server says *where* the song
  should be; the browser's audio clock says *where it is*. They are reconciled once at start.

What Slice 4 deliberately does **not** do:

- **Stage-role trust (M2)** — the role is still self-declared in `JoinRequest`. Slice 4 added no
  authentication.
- **Room garbage collection (M3)** — every `/stage` load still consumes a code from the finite
  279,841-code space.
- **Android** — the native Oboe stage is a later slice; the web stage is the only ground truth today.
- **A full offline mode** — rehydration is a startup database read, not a background sync.

---

## 2. New concepts, defined once

### 2.1 `BackgroundService` and the hosted-service lifetime

A **hosted service** is a class ASP.NET Core starts with the app and stops with it. The base class is
`BackgroundService`; you override `ExecuteAsync(CancellationToken stoppingToken)` and keep running
until the token fires. It is the standard home for a loop that is not tied to a request.

`PlaybackAuthority` is registered twice in `Program.cs`:

```csharp
builder.Services.AddSingleton<PlaybackAuthority>();
builder.Services.AddHostedService(static services => services.GetRequiredService<PlaybackAuthority>());
```

The singleton registration lets `StageHub` inject the same instance and call `EnqueueAsync`; the
hosted-service registration is what actually runs `ExecuteAsync`. Registering twice the naive way
(`AddSingleton<T>()` *and* `AddHostedService<T>()`) would create two objects, one of which nobody can
enqueue to. The factory overload avoids that.

### 2.2 `System.Threading.Channels`: bounded, `BoundedChannelFullMode`, single reader

A **`Channel<T>`** is an in-process producer/consumer queue with `await`-friendly reads and writes.
It is not a network socket and not a database queue; it lives in memory for the life of the process.

- **Bounded** means the channel holds at most N items. `PlaybackAuthority.CommandCapacity` is **256**.
- **`BoundedChannelFullMode.Wait`** means a producer whose item does not fit *awaits* until there is
  room. The alternatives (`DropWrite`, `DropOldest`) silently lose commands, which is wrong for
  playback.
- **`SingleReader = true`** is a promise that exactly one loop reads. The runtime can then skip
  coordination it would otherwise need, and the code gets a useful invariant: commands are applied in
  the order they were written.
- **`SingleWriter = false`** because hub invocations arrive on many connections.

```csharp
_commands = Channel.CreateBounded<PlaybackCommand>(
    new BoundedChannelOptions(CommandCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
```

`AllowSynchronousContinuations = false` keeps the producer's continuation off the consumer's thread,
so a slow broadcast cannot run arbitrary hub code inline.

### 2.3 `TaskCompletionSource` and read-your-writes

A **`TaskCompletionSource<T>`** (TCS) is a hand-controlled `Task`. You create it, hand it to someone,
and later call `SetResult`, `SetException`, or `SetCanceled` to complete it. It is how a producer can
await a result that another loop computes.

Slice 4's envelope is exactly that pair:

```csharp
private sealed record PlaybackCommand(
    RoomCommand Command,
    TaskCompletionSource<bool> Completion);
```

The hub writes `PlaybackCommand(command, completion)`, then awaits `completion.Task`. The consume loop
persists and broadcasts, then calls `TrySetResult(handled)`. **Read-your-writes** means the caller's
next read observes its own write: a phone that calls `StartPlayback` and then asks for a snapshot sees
`Countdown`, not a stale `Idle`. Returning as soon as the command is *queued* would break that, which
is why `EnqueueAsync` awaits the completion rather than the write.

The TCS is created with `TaskCreationOptions.RunContinuationsAsynchronously` so completing it does not
synchronously run the hub's continuation on the authority's consume loop.

### 2.4 Single-writer pattern versus Slice 3's multi-method mutation

Slice 3 had **many writers**: `StartPlaybackAsync`, `PausePlaybackAsync`, `SkipSongAsync`, and the
rest each took the lock themselves. That is correct if every writer is disciplined, but the ordering
guarantee is spread across call sites, and two different call sites can interleave at the `await`
boundaries.

The **single-writer pattern** funnels every mutation through one loop. Here:

```text
many hub connections ──▶ one Channel ──▶ one consume loop ──▶ one CommandLock per room
```

The authority is the only component that calls the playback transition bodies. `QueueService` still
owns the bodies (they need the `DbContext` and the hub broadcast), but it is no longer the *initiator*
for playback. Enqueue and move stay direct because they are not playback.

The side benefit is visible in the duplicate-end fix: one guard, in one place, protects both the stage
report and the watchdog.

### 2.5 Playback anchors and the position projection

An **anchor** is a wall-clock instant paired with a media position. `PlaybackStartedAtUtc` is the
instant the current `Playing` span began at position zero. `PlaybackStateDto` carries:

```csharp
PlaybackStateDto(
    Status, CurrentQueueItemId, CurrentSong,
    PositionMs, TimeAnchorUtc, GuideVocalVolume, CountdownRemainingBeats)
```

A client computes:

```text
expectedMs = PositionMs + (serverNow − TimeAnchorUtc)     // only while Playing
```

While paused or counting in, `PositionMs` is already the folded value and there is no extrapolation.
This is the §5.3 **Layer 2** contract; the **Layer 1** contract is estimating `serverNow`, which is
§2.7 below.

`QueueService.BuildPlaybackStateLocked` is the projector. Note that it uses `DateTime.UtcNow` for the
read-side projection even though the mutation path uses the injectable `TimeProvider`; only anchors
and watchdog decisions need the fake clock.

### 2.6 `TimeProvider` and why injectable time matters

`TimeProvider` is .NET's abstraction over "now" and timers. `TimeProvider.System` is the real clock;
a test can subclass it and hand back any instant.

Slice 4 makes it injectable in two places:

```csharp
// QueueService — anchor minting for transitions.
internal QueueService(..., TimeProvider timeProvider)
private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

// PlaybackAuthority — watchdog and count-in, plus a tick-interval seam.
internal PlaybackAuthority(..., TimeProvider timeProvider, TimeSpan tickInterval, ...)
```

Why not just call `DateTime.UtcNow`? Because the watchdog fires five seconds after a song's duration,
and the count-in is a two-second window. Testing those with real sleeps is slow, flaky, and cannot
exercise "the loop ran 300 ms late". With `TestTimeProvider.Advance(...)` the test drives time exactly:

```csharp
clock.Advance(PlaybackTiming.CountdownWindow);
await authority.TickAsync(CancellationToken.None);
```

The public constructors keep the production defaults (`TimeProvider.System`, 100 ms tick), so DI is
unchanged. The internal constructor is the test seam; the unit-test project can see it through
`InternalsVisibleTo`.

### 2.7 Write-ahead minting versus write-behind

A **sequence number** is the monotonic counter attached to `QueueDelta` (Slice 3, §2.5 there). The
question Slice 4 had to answer is *when* to increment it relative to persistence.

- **Write-behind** would mutate, persist, then read the stored sequence back and broadcast it. A crash
  between the broadcast and the persist could store a *lower* number than clients already saw, so
  after rehydration a reconnecting client would see deltas rewind.
- **Write-ahead minting** increments first, then persists that value in the same `SaveChanges` as the
  state it describes. The database can never be behind the wire.

The code is explicit about it:

```csharp
// Mint the sequence before persisting/broadcasting so the persisted value is already
// the one clients are about to observe (write-ahead, §5.1 rule 6).
var sequence = queueChanged ? room.NextQueueSequence() : 0;
await PersistRoomStateAsync(room, playbackChanged, queueChanged, cancellationToken);
```

`PartyRoom.QueueSequence` is the persisted copy, added by migration
`20261125160000_AddRoomQueueSequence`.

### 2.8 NTP-style clock offset: RTT, min-RTT best-of-window, and rejection

**RTT** (round-trip time) is how long a message takes to go to the server and back. The clock-sync
exchange is the classic NTP (Network Time Protocol) shape:

```text
client: t1 = now; send Ping{t1}
server: t2 = now; reply Pong{t1, t2}
client: t4 = now
        RTT          = t4 − t1
        clockOffset  = t2 − (t1 + RTT / 2)
        serverNow   ≈ localNow + clockOffset
```

The formula assumes the outbound and inbound halves take the same time. If the main thread stalls
during the exchange, the RTT inflates and half of that error lands in the offset. **Min-RTT
best-of-window** is the standard robustness trick: keep the last 8 samples and use the offset from
the *fastest* one, because the fastest exchange is least likely to have been disturbed. Samples with
an RTT above **250 ms** are rejected outright rather than allowed to poison the window.

Two accepted samples are required before the offset is trusted. Until then the client falls back to a
**receipt anchor**: the offset implied by the moment a `PlaybackChanged` message arrived locally,
which cancels the unknown clock difference and leaves only the one-way delay.

### 2.9 `createMediaElementSource` and audio as ground truth

`AudioContext.createMediaElementSource(video)` routes a `<video>`/`<audio>` element's sound into the
Web Audio graph. A browser allows **one** source node per media element per context, so Karaokay keeps
one persistent `<video>` element for the whole page session and changes only its `src`.

**Audio as ground truth** means the engine's own clock — `AudioContext.currentTime` for stems, the
element's `currentTime` for video — is what playback and lyrics follow. The server anchor is used
**once**, at start, to compute a seek:

```text
seekOffset = expectedMs − audioPositionMs
```

After that, the audio is never periodically re-slaved. Re-seeking mid-song would click, stutter, and
fight the hardware clock. The video surface is the one exception: it gets periodic small nudges
because a `<video>` can adjust `playbackRate` and seek cheaply, and because a dropped frame there is
visible.

### 2.10 Cold-start rehydration and `AsNoTracking`

**Rehydration** means rebuilding in-memory state from persisted state after a restart. Slice 3 wrote
rooms and queues to Postgres but never read them back; a container restart produced an empty
`RoomRegistry` and rooms that had "vanished".

`RoomRehydrator` runs once at startup, after migrations and demo seeding:

```csharp
await app.Services.GetRequiredService<RoomRehydrator>()
    .RehydrateAsync(CancellationToken.None);
```

**`AsNoTracking()`** tells EF Core not to keep change-tracking snapshots for the entities it
materializes. Rehydration only reads: nothing will be saved back through this context, so tracking
would be pure overhead, especially with a queue and songs per room.

The mapper is deliberately pure (`RoomStateMapper.Map`), so the row-to-live-state rules are unit
testable without PostgreSQL, while the query shape is pinned separately.

---

## 3. File-by-file tour

Paths are relative to the repo root. "Why it exists" is the one-line reason you would keep the file.

### 3.1 Server and contracts

| Path | Role | Why it exists |
|---|---|---|
| `src/apps/Karaokay.Api/Features/PlaybackAuthority.cs` | Single writer | Consumes the command channel, owns count-in, `AwaitingDownload`, and the watchdog. |
| `src/apps/Karaokay.Api/Features/PlaybackTiming.cs` | Pinned timing | `CountdownBeats = 4`, `CountdownBeatMs = 500`, `CountdownWindow`. |
| `src/apps/Karaokay.Api/Features/QueueService.cs` | Transition bodies | Keeps the lock/persist/broadcast bodies; adds `TimeProvider`, `PersistRoomStateAsync`, count-in/AwaitingDownload transitions. |
| `src/apps/Karaokay.Api/Features/QueueOrdering.cs` | Pure cursor rules | Protects the count-in cursor item from moves. |
| `src/apps/Karaokay.Api/Features/RoomRehydrator.cs` | Startup read | One `AsNoTracking` query over open rooms; rebuilds the registry. |
| `src/apps/Karaokay.Api/Features/RoomStateMapper.cs` | Pure mapping | Row → `RoomState`: re-densify, restore anchors, demote stale `Playing`, derive count-in beat. |
| `src/apps/Karaokay.Api/Features/RoomRegistry.cs` | Hot state | Adds `RoomState.CountdownAwaitingResume` and `RestoreQueueSequence`. |
| `src/apps/Karaokay.Api/Hubs/StageHub.cs` | Hub | Playback methods now enqueue-and-await; `Ping` echoes `t1`/`t2`. |
| `src/apps/Karaokay.Api/Program.cs` | Host wiring | Registers the authority as singleton *and* hosted service, plus the rehydrator. |
| `src/core/Karaokay.Contracts/Hubs/RoomCommand.cs` | Command payload | No longer a placeholder: the channel's actual wire shape. |
| `src/core/Karaokay.Contracts/Hubs/Dtos.cs` | Wire records | `PingPayload`/`Pong`, `CountdownDto`, `PlaybackStateDto`. |
| `src/core/Karaokay.Contracts/Hubs/IStageHub.cs` | Client→server | `Ping(PingPayload)` returns `Pong`. |
| `src/core/Karaokay.Contracts/Hubs/IStageHubClient.cs` | Server→client | `CountdownTicked` is now emitted. |
| `src/core/Karaokay.Contracts/Hubs/WireEnums.cs` | Wire enums | `Countdown` and `AwaitingDownload` are now exercised states. |
| `src/core/Karaokay.Domain/PartyRoom.cs` | Entity | Adds persisted `QueueSequence`. |
| `src/core/Karaokay.Infrastructure/Persistence/PostgresMigrations/20261125160000_AddRoomQueueSequence.cs` | Migration | `Rooms.QueueSequence bigint NOT NULL DEFAULT 0`. |

### 3.2 Web client and tests

| Path | Role | Why it exists |
|---|---|---|
| `src/clients/web/src/lib/signalr/clockSync.ts` | Layer 1 | Ping/Pong sampler, 8-sample min-RTT filter, 250 ms rejection, receipt fallback. |
| `src/clients/web/src/lib/signalr/roomState.ts` | Pure reducer | Adds `applyCountdownTick` and count-in projection. |
| `src/clients/web/src/lib/signalr/stageClient.ts` | Hub proxy | Adds `ping()`, `onCountdownTicked`. |
| `src/clients/web/src/lib/signalr/protocol.ts` | Wire mirror | `PingPayload`, `Pong`, `CountdownDto`, `RoomCommand`, enums. |
| `src/clients/web/src/features/stage/playbackController.ts` | Stage playback | `StagePlaybackController` + `useStagePlayback`; align-once and deduped end reports. |
| `src/clients/web/src/features/stage/songMedia.ts` | Media resolution | GUID→slug catalog bridge; fetches stems and lyrics. |
| `src/clients/web/src/features/stage/useStageVideo.ts` | Video surface | Owns the persistent `<video>`, periodic corrections, event wiring. |
| `src/clients/web/src/features/stage/videoSync.ts` | Pure video math | `HARD_NUDGE_MS = 120`, `SOFT_NUDGE_MS = 40`, expected position. |
| `src/clients/web/src/features/stage/useStageClock.ts` | RAF clock | One animation-frame loop feeding the canvas without React re-renders. |
| `src/clients/web/src/features/stage/StageShell.tsx` | Stage shell | Room playback takeover, `data-*` test attributes, count-in display. |
| `src/clients/web/src/features/stage/WebAudioEngine.ts` | Audio engine | `pause()` preserves position; decoded buffers reused. |
| `src/clients/web/src/features/stage/useStageRoom.ts` | Stage hub hook | Clock sampler, `serverNow`, `reportSongEnded`, `setGuideVocalVolume`. |
| `src/clients/web/src/features/stage/StageRoomPanel.tsx` | Room overlay | Code, QR, guest count, first four rows; renders nothing when offline. |
| `src/clients/web/src/features/remote/PlaybackControls.tsx` | Phone controls | SVG countdown ring; Pause enabled during the count-in. |
| `src/clients/web/src/features/remote/useStageClient.ts` | Phone binding | Folds `CountdownTicked` into the reducer. |
| `src/clients/web/e2e/playback-authority.spec.ts` | Two-context E2E | Phone drives the stage; mid-song join under a +40 ms bias. |
| `src/tests/Karaokay.UnitTests/PlaybackAuthorityTests.cs` | Authority unit tests | Count-in, watchdog, duplicate end, AwaitingDownload, write timeout. |
| `src/tests/Karaokay.UnitTests/RoomStateMapperTests.cs` | Mapper tests | Dangling cursor, stale `Playing`, count-in resume. |
| `src/tests/Karaokay.UnitTests/RoomRehydrationQueryTests.cs` | Query shape | Proves the ordered/filtered include translates to SQL. |
| `src/tests/Karaokay.UnitTests/QueueServiceTests.cs` | Transition tests | Extended for count-in, `AwaitingDownload`, atomic removal. |
| `src/tests/Karaokay.UnitTests/SignalRContractTests.cs` | Contract tests | Adds DTO field-parity assertions. |
| `src/tests/Karaokay.IntegrationTests/RoomHubIntegrationTests.cs` | Wire tests | Five Docker-gated Slice 4 tests. |

---

## 4. Server deep dive

### 4.1 The command envelope and `EnqueueAsync`

The public entry point is small enough to quote whole:

```csharp
public async Task<bool> EnqueueAsync(RoomCommand command, CancellationToken cancellationToken)
{
    var completion = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);

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
        throw new TimeoutException(...);
    }

    return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
}
```

Three details matter:

1. **The 2 s write timeout.** `CommandWriteTimeout` exists because a full channel with a stalled
   consumer would otherwise make `WriteAsync` wait forever and hang the hub invocation. The `when`
   filter distinguishes "our timeout fired" from "the caller disconnected", so only the first becomes
   a `TimeoutException`.
2. **The completion is awaited separately.** The write timeout covers admission, not processing. Once
   the command is in the channel, the caller waits for the applied result; a client may cancel that
   wait with its own token, but the authority still applies the command.
3. **The bool is meaningful.** For `RemoveQueueItem` it reports whether the item existed; for
   `ReportSongEnded` and `SetGuideVocalVolume` it reports that the command was handled. The hub uses
   only the removal result, to keep its `NotFound` error.

### 4.2 The consume loop

`ExecuteAsync` runs two loops concurrently:

```csharp
await Task.WhenAll(
        ConsumeCommandsAsync(stoppingToken),
        RunTickLoopAsync(stoppingToken))
    .ConfigureAwait(false);
```

The consume loop is the ordering guarantee:

```csharp
while (await _commands.Reader.WaitToReadAsync(stoppingToken))
{
    while (_commands.Reader.TryRead(out var command))
    {
        await ApplyCommandAsync(command, stoppingToken);
    }
}
```

`ApplyCommandAsync` is a try/catch boundary per command: a handled command sets `true`, a
cancellation sets canceled, any other exception is logged and set on the completion. The caller sees
the exception; the loop keeps running.

On shutdown, `ConsumeCommandsAsync` drains whatever is already queued with
`HandleCommandAsync(command, CancellationToken.None)` before completing the writer. That is a
deliberate choice: a command whose caller was already promised read-your-writes should not vanish
because the host stopped. Anything written *after* `TryComplete` fails at the writer, which is the
correct outcome for a shutting-down process.

`HandleCommandAsync` maps a `RoomCommand.Type` string to a `QueueService` body. Unknown rooms log and
return `false`; unowned types (enqueue, move) log "does not own" and return `false` rather than
throwing — a defensive guard, not an expected path.

### 4.3 The two-phase Start

Start is the most interesting transition because it is where the authority's time ownership begins.

```csharp
StartPlaybackAsync
  if status is Playing/Countdown/AwaitingDownload/Paused -> null       // duplicate/no-op
  current = current item unless it is Ended/Failed
  current ??= first Ready/Queued by position
  if current is null:
      downloading = first Downloading by position
      return downloading is null ? Idle : AwaitingDownload
  return current.State == Downloading
      ? AwaitingDownload
      : BeginCountdown(current, now)
```

`BeginCountdownLocked` does **not** mark the row `Playing`:

```csharp
room.CurrentQueueItemId = entry.Id;
room.Status = PlaybackStatus.Countdown;
room.PlaybackStartedAtUtc = nowUtc;
room.PausedAtPositionMs = 0;
room.CountdownRemainingBeats = PlaybackTiming.CountdownBeats;
room.CountdownAwaitingResume = false;
```

Keeping the row `Ready` until audio actually starts is what lets a paused-count-in restore cleanly
and what lets the UI show "queued but not yet playing". The cursor, not the row state, is the
preload hint: clients can fetch the song because `CurrentQueueItemId` is set.

`RunPlaybackCommandAsync` is the shared body. It snapshots playback and queue under `SyncRoot`,
applies the mutation, compares, and only persists/broadcasts when something actually changed:

```csharp
playbackChanged = playbackBefore != PlaybackSnapshot.Capture(room);
queueChanged = !queueBefore.SequenceEqual(CaptureQueueLocked(room));
if (!playbackChanged && !queueChanged) return;
```

That is why a duplicate Start consumes no sequence and re-anchors nothing, and why a Pause while
`Idle` is a genuine no-op.

When both changed, `PersistRoomStateAsync(room, playback: true, queue: true, ct)` writes the room row
and the queue rows in **one** `SaveChangesAsync`. EF Core wraps a single save in one transaction, so a
crash cannot persist the playback reset while the removed or advanced queue row stays behind. This is
the fix the review's addendum calls out for `RemoveQueueItemAsync` as well.

### 4.4 The count-in ticker

`TickRoomAsync` is the per-room half of the 100 ms loop. It first takes a cheap `SyncRoot` read to
copy the fields it needs and does the expensive work outside the lock:

```csharp
if (status == PlaybackStatus.AwaitingDownload && currentQueueItemId is { } awaitingId) { ... return; }
if (status == PlaybackStatus.Playing && currentQueueItemId is not null
    && IsWatchdogExpired(playbackAnchorUtc, durationMs, now)) { ... return; }
if (status == PlaybackStatus.Countdown && currentQueueItemId is { } countdownItemId) { ... return; }
_countdownCursors.TryRemove(room.Id, out _);
```

Three things fall out of that shape:

- A room in `Countdown` is never a watchdog candidate; only `Playing` is.
- Leaving `Countdown` for any reason drops the per-room cursor, so the next count-in starts at beat 4.
- Idle and paused rooms cost one field read per tick.

`TickCountdownAsync` derives the beat from **elapsed time**, not from a counter:

```csharp
var elapsedMs = Math.Max(0, (now.UtcDateTime - anchor).TotalMilliseconds);
var beatsElapsed = (int)Math.Floor(elapsedMs / PlaybackTiming.CountdownBeatMs);
var windowComplete = beatsElapsed >= PlaybackTiming.CountdownBeats;
var targetBeat = windowComplete ? 1 : Math.Max(1, PlaybackTiming.CountdownBeats - beatsElapsed);
```

Then it computes the beats that have not been broadcast yet and emits them in order. That is why a
stalled loop catches up instead of skipping: if the process was busy for 1.4 s, the next tick emits
2, 1 before completing the window. The `CountdownCursor` holds `LastBroadcastRemainingBeats` with
`int.MinValue` as "none yet".

The lock discipline is the subtle part:

```csharp
await room.CommandLock.WaitAsync(cancellationToken);
try
{
    // re-check status/item/anchor under the lock
    for (var beat = nextBeat; beat >= targetBeat; beat--) beats.Add(beat);
    if (beats.Count > 0)
    {
        cursor.LastBroadcastRemainingBeats = beats[^1];
        lock (room.SyncRoot) room.CountdownRemainingBeats = beats[^1];
    }
}
finally
{
    room.CommandLock.Release();
}

foreach (var beat in beats)
{
    await _hub.Clients.Group(room.Code).CountdownTicked(new CountdownDto(
        queueItemId, beat, PlaybackTiming.CountdownBeatMs));
}
```

Why is the broadcast outside the lock? Because it is a **derived-progress** message, not a durable
transition, and awaiting a slow client's send while holding the room's single command semaphore would
block Pause and Skip for up to the length of the count-in. ADR-008 records exactly this: "the count-in
tick is the explicit exception: it mutates derived in-memory remaining-beat progress and awaits its
fan-out after releasing the command lock". The re-check under the lock is what keeps it safe: a Pause
that won the race changes `Status`, and the tick returns without emitting a stale beat.

Once the window is complete, `CompleteCountdownAsync` runs through the normal
`RunPlaybackCommandAsync` path. It is guarded on `Status == Countdown && CurrentQueueItemId == id`,
then calls `BeginPlayingLocked`, which sets a **fresh** `PlaybackStartedAtUtc = nowUtc`. That fresh
anchor is the media alignment anchor clients use; it is not the count-in anchor.

`room.CountdownAwaitingResume` is the cold-restart seam. When the mapper restores a `Countdown` room,
it sets the flag. On the first tick this process sees, it seeds the cursor to `targetBeat + 1`, capped
at `CountdownBeats + 1` (`Math.Min(CountdownBeats + 1, targetBeat + 1)`), instead of the sentinel; the
cap only binds at the window-complete edge, where `targetBeat` is 1. That stops beats the previous
process already broadcast from being replayed. A count-in that
started in *this* process has the flag cleared by `BeginCountdownLocked` and therefore emits beat 4.

### 4.5 `AwaitingDownload` auto-start

`StartPlaybackAsync` parks the room when the only candidate is still downloading:

```csharp
private static RoomQueueEntry BeginAwaitingDownloadLocked(RoomState room, RoomQueueEntry entry)
{
    room.CurrentQueueItemId = entry.Id;
    room.Status = PlaybackStatus.AwaitingDownload;
    room.PlaybackStartedAtUtc = null;      // no count-in clock until media lands
    room.PausedAtPositionMs = 0;
    room.CountdownRemainingBeats = 0;
    room.CountdownAwaitingResume = false;
    return entry;
}
```

The tick loop completes it:

```csharp
if (currentState == QueueItemState.Ready)
    await _queue.CompleteAwaitingDownloadAsync(room, awaitingId, ct);
else if (currentState is QueueItemState.Failed or QueueItemState.Ended)
    await _queue.CancelAwaitingDownloadAsync(room, awaitingId, ct);
```

Both bodies re-check the room is still awaiting *that exact item* and still `AwaitingDownload`. That
double guard is the difference between "a download finished" and "the room still wants that download".
A different item becoming `Ready` does nothing (`DownloadCompletingForANonCursorItemDoesNotStartTheCountdown`
pins it). A failed awaited item returns the room to `Idle`, so a later Start can pick the next
playable row.

Enqueue and the download worker mark rows `Ready` on their direct path via
`UpdateQueueItemStateAsync`; the authority only observes the state on its next tick. That is a
deliberate split: the worker must not become a second playback writer.

### 4.6 The watchdog

`IsWatchdogExpired` is a pure comparison:

```csharp
var deadline = started.AddMilliseconds(durationMs).Add(WatchdogGrace);
return now.UtcDateTime >= deadline;
```

`WatchdogGrace` is **5 s**, `durationMs` comes from the joined `SongDto`, and `now` comes from
`TimeProvider`. A duration of zero returns `false`, so a song row without a duration cannot be
watchdog-ended instantly.

`EndExpiredSongAsync` takes `CommandLock`, re-checks under `SyncRoot`, and then calls
`ReportSongEndedUnderLockAsync` — the same body a stage report uses:

```csharp
internal async Task<bool> ReportSongEndedUnderLockAsync(
    RoomState room, Guid queueItemId, CancellationToken cancellationToken)
{
    lock (room.SyncRoot)
    {
        if (room.CurrentQueueItemId != queueItemId) { log; return false; }
    }

    await AdvanceUnderLockAsync(room, cancellationToken);
    return true;
}
```

That is the **entire** duplicate-end defence. The stage report and the watchdog both reach
`AdvanceUnderLockAsync` only if the reported id is still the cursor, checked while holding the same
command lock. Whichever wins, the other sees a moved cursor and drops. There is no second guard to
keep in sync.

`AdvanceLocked` marks the old item `Ended`, selects the next playable row with
`QueueOrdering.SelectNext`, and either starts it with a fresh anchor (`Playing`) or resets to `Idle`
and clears every playback field. Note again: **advance never re-enters `Countdown`**. The count-in is
start-only, which is what `ARCHITECTURE.md` §5.1 says and what the count-in tests pin.

### 4.7 `PlaybackTiming`

The whole file is a contract:

```csharp
internal static class PlaybackTiming
{
    public const int CountdownBeats = 4;
    public const double CountdownBeatMs = 500;
    public static readonly TimeSpan CountdownWindow =
        TimeSpan.FromMilliseconds(CountdownBeats * CountdownBeatMs);
}
```

Four beats × 500 ms = a 2 s window. `CountdownBeatMs` is a `double` because it is pushed on the wire
(`CountdownDto.BeatMs`) and consumed by CSS transitions, which want fractional milliseconds.

**Why fixed, when `lyrics.json` has tempo and gaps?** ADR-008's rejected alternative answers it: a
per-song beat would couple the authority to media I/O on every Start or tick, for a cue that room mode
renders from the server's fixed beats anyway. The per-song tempo stays in `lyrics.json` for the local
`DemoTrackPicker`, which has no room and no server. The room count-in is a party cue: "four, three,
two, one, go", not a musical subdivision.

### 4.8 `StageHub` — enqueue and await

Every authority-owned hub method now has the same shape:

```csharp
public async Task StartPlayback()
{
    var (room, _) = await RequireRoomAsync().ConfigureAwait(false);
    await EnqueuePlaybackCommandAsync(room, RoomCommandTypes.StartPlayback)
        .ConfigureAwait(false);
}
```

and:

```csharp
private Task<bool> EnqueuePlaybackCommandAsync(
    RoomState room, string type, Guid? queueItemId = null, double? volume = null) =>
    _playback.EnqueueAsync(
        new RoomCommand
        {
            Type = type,
            RoomCode = room.Code,
            ConnectionId = Context.ConnectionId,
            QueueItemId = queueItemId,
            Volume = volume,
        },
        CancellationToken.None);
```

The membership and role checks stay in the hub, because the hub is the only thing that knows
`Context.ConnectionId`. `ReportSongEnded` still goes through `RequireStageAsync`; `StartPlayback`
does not, because any phone may start the party. `RemoveQueueItem` is the newest authority-owned
method: it keeps `RequireRoomAsync`, sends `RemoveQueueItem`, and maps a `false` result to
`HubError(NotFound)`.

`Ping` is small but worth reading:

```csharp
var receivedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
await RequireRoomAsync().ConfigureAwait(false);
return new Pong(payload?.T1 ?? 0, receivedAt);
```

`t2` is stamped **before** the registry lookup, so server-internal latency does not pollute the offset
estimate. The room check still gates the exchange, so a random connection cannot use the hub as a
clock. There is one `t2` for both receive and send, which is fine at this scale: the difference is the
method body, not the network.

### 4.9 `QueueService`'s new role

`QueueService` did not disappear; it stopped being the initiator. Its new pieces:

| Piece | Why |
|---|---|
| Injectable `TimeProvider` | `StartPlaybackAsync`, `PausePlaybackAsync`, `AdvanceLocked`, and count-in bodies need a driveable clock. |
| `PersistRoomStateAsync(playback, queue)` | One scope, one `SaveChanges`; replaces separate `PersistRoomAsync`/`PersistQueueAsync` calls at the call sites that touch both. |
| `BeginCountdownLocked` / `BeginAwaitingDownloadLocked` | The two new start outcomes. |
| `CompleteCountdownAsync` / `CompleteAwaitingDownloadAsync` / `CancelAwaitingDownloadAsync` | Internal entry points the authority calls back into; each re-checks status and cursor. |
| `ReportSongEndedUnderLockAsync` | The shared guard, callable only while the caller owns `CommandLock`. |
| `CountdownAwaitingResume` resets | `BeginCountdownLocked`, `BeginAwaitingDownloadLocked`, `ResetToIdleLocked`, `AdvanceLocked`, and the removal branch all clear it. |

The removal branch is the clearest example of the "playback + queue in one transaction" rule:

```csharp
if (room.CurrentQueueItemId == queueItemId)
{
    room.CurrentQueueItemId = null;
    room.Status = PlaybackStatus.Idle;
    room.PlaybackStartedAtUtc = null;
    room.PausedAtPositionMs = 0;
    room.CountdownRemainingBeats = 0;
    room.CountdownAwaitingResume = false;
    playbackChanged = true;
}

var sequence = room.NextQueueSequence();
await PersistRoomStateAsync(room, playbackChanged, queue: true, cancellationToken);
await _hub.Clients.Group(room.Code)
    .QueueChanged(new QueueDelta([], [queueItemId], [.. updated.Select(ToDto)], sequence));
if (playbackChanged) await BroadcastPlaybackAsync(room);
```

`RemovingTheCurrentItemPersistsTheIdlePlaybackReset` re-reads the row through a fresh
`AsNoTracking` query, so the assertion proves `SaveChanges` durability rather than in-memory luck.

### 4.10 `QueueOrdering` and the count-in cursor

Slice 3's `Move` rejected `Playing` and `Ended` rows. Slice 4 adds one condition:

```csharp
if (entry.State is QueueItemState.Playing or QueueItemState.Ended
    || (currentQueueItemId is { } currentId && entry.Id == currentId))
{
    return QueueMoveResult.NotMovable;
}
```

Why is that needed? During `Countdown`, the cursor item's row state is still `Ready`. Without the
second clause a guest could move the song that is counting in, and the authority's next tick would
either fail its `CurrentQueueItemId` guard or start a song whose position no longer matches the UI.
`MovingTheCountingInItemIsRejected` pins it.

The rest of the class is unchanged: a lower bound after the cursor, a clamp instead of an error for
"move to the top", `SelectNext`'s forward-then-back fallback.

### 4.11 Cold-start rehydration, step by step

`Program.ApplyDatabaseStartupAsync` does migrations → demo seed → rehydrate, in that order, before the
host starts serving. The order matters: the queue rows join to `Songs`, and those rows must exist.

**Step 1 — the query.** `RoomRehydrator.OpenRoomsWithQueue` is factored out so its SQL can be asserted:

```csharp
return rooms
    .AsNoTracking()
    .Where(room => room.ClosedAtUtc == null)
    .Include(room => room.Queue.OrderBy(item => item.Position))
    .ThenInclude(item => item.Song);
```

`RoomRehydrationQueryTests.OpenRoomQueryTranslatesWithOrderedQueueAndSongs` builds the query on the
Npgsql provider and asserts the string contains `"Rooms"`, `"QueueItems"`, `"Songs"`, `ORDER BY`, and
`"ClosedAtUtc" IS NULL`. That catches the classic "EF Core 10 stopped translating a filtered,
ordered include" regression without needing a container.

**Step 2 — map.** `RoomStateMapper.Map` is pure:

1. `new RoomState(row.Id, row.Code, row.CreatedAtUtc)`.
2. Order by position, map each row, then **re-densify** positions `0..n-1`. The invariant is dense
   positions; a gap from a crashed write must not leak into the live queue.
3. `room.RestoreQueueSequence(row.QueueSequence)`.
4. Clamp the guide-vocal volume (NaN → 0.6, out of range clamped).
5. Resolve the cursor. **Dangling cursor** (id not in the queue) → `Idle` with every playback field
   cleared. This is the "don't play something that no longer exists" rule.
6. If the cursor exists, copy status and anchor **verbatim**. A `Playing` room keeps flowing from the
   persisted instant; it is not silently paused.
7. Keep the count-in row `Ready`; only `Playing`/`Paused` imply the cursor row is `Playing`.
8. Demote any **other** `Playing` row to `Ready`. A stale duplicate would otherwise render two
   cursors after a restart.
9. For a restored `Countdown`, derive the live remaining-beat value from the anchor:

```csharp
room.CountdownRemainingBeats = RemainingCountdownBeats(countdownAnchor, DateTime.UtcNow);
room.CountdownAwaitingResume = true;
```

`RemainingCountdownBeats` floors elapsed milliseconds by `CountdownBeatMs` and clamps to `[1, 4]`.

**Step 3 — install.** `RoomRegistry.TryAdd(state)`; if the code is already live, count it skipped
rather than clobbering a live room. `RoomRegistry.RestoreQueueSequence` is just
`Interlocked.Exchange`, but it is the difference between "deltas continue at 41" and "deltas rewind to
1" after a restart.

### 4.12 The `AddRoomQueueSequence` migration

```csharp
migrationBuilder.AddColumn<long>(
    name: "QueueSequence",
    table: "Rooms",
    type: "bigint",
    nullable: false,
    defaultValue: 0L);
```

`DEFAULT 0` is what makes the migration safe for existing rows: a room that predates the column reads
as "no deltas yet", which is exactly the pre-Slice-4 behaviour. The column is written in the same
`SaveChanges` as the state it describes, so a crash cannot leave the sequence behind the wire.

---

## 5. Contracts deep dive

Slice 3's §5.4 said `RoomCommand` was "reserved, not used" and that `CountdownTicked` was a
placeholder. Slice 4 is the promise-kept chapter.

### 5.1 `RoomCommand` becomes the channel payload

The record is unchanged in shape — flat, camelCase-pinned, nullable fields — but its role changed:

```csharp
public sealed record RoomCommand
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("roomCode")] public string RoomCode { get; init; } = string.Empty;
    [JsonPropertyName("connectionId")] public string? ConnectionId { get; init; }
    [JsonPropertyName("queueItemId")] public Guid? QueueItemId { get; init; }
    [JsonPropertyName("songId")] public Guid? SongId { get; init; }
    [JsonPropertyName("newPosition")] public int? NewPosition { get; init; }
    [JsonPropertyName("volume")] public double? Volume { get; init; }
    [JsonPropertyName("audioLatencyEstimateMs")] public int? AudioLatencyEstimateMs { get; init; }
}
```

Not every command type is authority-owned. `RoomCommandTypes` lists twelve names; the authority
handles eight (`StartPlayback`, `PausePlayback`, `ResumePlayback`, `SkipSong`, `RestartSong`,
`RemoveQueueItem`, `ReportSongEnded`, `SetGuideVocalVolume`). Enqueue and move stay direct, and
`UpdateStageCapabilities` never travels over the channel. Keeping one flat record rather than a
polymorphic hierarchy is a deliberate TypeScript-mirroring choice, and the remaining fields are
headroom rather than lies.

### 5.2 Ping and Pong

```csharp
public sealed record PingPayload(long T1);
public sealed record Pong(long T1, long T2);
```

Both are epoch milliseconds (`DateTimeOffset.ToUnixTimeMilliseconds` / `Date.now()`). `T1` is echoed
verbatim so a client can reject a reply from another exchange. There is no `T3`; the client's receive
time is measured locally, which is all the formula needs.

### 5.3 `CountdownTicked` goes live

```csharp
public sealed record CountdownDto(Guid QueueItemId, int RemainingBeats, double BeatMs);
```

Every field is exercised: `QueueItemId` is the cursor at Start, `RemainingBeats` runs 4 → 1,
`BeatMs` is always `500` from `PlaybackTiming.CountdownBeatMs`. The interface doc comment on
`IStageHubClient.CountdownTicked` still says "Count-in tick for phone progress rings"; both the phone
and the stage now subscribe, and the integration test asserts the exact `[4,3,2,1]` / 500 contract.

The review's earlier note that `CountdownTicked`, `LyricsLoaded`, and `SearchCompleted` were "dead
contract entries" has shrunk to two: `CountdownTicked` is now emitted. `LyricsLoaded` and
`SearchCompleted` remain declared but unsent.

### 5.4 Wire enums: the new exercised states

`WireEnums.cs` already had the values in Slice 3; Slice 4 made them real:

| Enum | Value | Exercised by |
|---|---|---|
| `PlaybackStatus.AwaitingDownload` | 1 | Start on a `Downloading` head; auto-start on `Ready`; `Idle` on failure. |
| `PlaybackStatus.Countdown` | 2 | Start, Restart-during-count-in, cold-restart resume. |
| `PlaybackStatus.Paused` | 4 | Pause during count-in or playback; restored by rehydration. |
| `QueueItemState.Playing` | 4 | Only the cursor; rehydration normalizes and demotes duplicates. |
| `QueueItemState.Ready` | 2 | The count-in cursor row stays `Ready` until audio starts. |

`PlaybackStatus.SongEnded` (5) is still not entered by any path: advance goes straight to `Playing`
or `Idle`. The phone's label table can render it, but nothing produces it. That is a contract value
waiting on a future transition, not a bug in Slice 4.

### 5.5 DTO field-parity contract tests

The Slice 3 review's nit was that `SignalRContractTests` compared method names and `RoomCommand`
properties but not the other DTOs. The follow-up added field parity: a `[Theory]` enumerates the
record set (`RoomSnapshot`, `QueueDelta`, `PlaybackStateDto`, `PingPayload`, `Pong`, `CountdownDto`,
and the rest) and asserts that each C# record's property count and camelCase name set match the
TypeScript interface in `protocol.ts`. Name-only parity remains for *types* and optionality — the
test cannot tell `long` from `int` — but a renamed or missing field now fails the build.

### 5.6 What did *not* change

The 15 client→server and 14 server→client method names are unchanged. `RoomSnapshot`, `QueueDelta`,
and `PlaybackStateDto` keep their shapes: `CountdownRemainingBeats` existed in Slice 3 and is now
populated during `Countdown`. That stability is why Slice 3 clients still parse Slice 4 messages; the
sequence guard and snapshot rejoin needed no new protocol.

---

## 6. Client deep dive

### 6.1 `clockSync.ts` — Layer 1

The module has three layers of code:

1. **One sample** — `computeClockSample(sentAtMs, pong, receivedAtMs)`:

```ts
if (pong.t1 !== sentAtMs) return null;             // echo belongs to another exchange
const rttMs = receivedAtMs - sentAtMs;
if (rttMs < 0 || rttMs > CLOCK_SYNC_MAX_RTT_MS) return null;   // 5 000 ms ceiling
return { offsetMs: pong.t2 - (sentAtMs + rttMs / 2), rttMs };
```

The 5 s ceiling is a sanity gate; the 250 ms gate is the alignment gate, applied inside `add`.

2. **The filter** — `ClockOffsetFilter`:

```ts
add(sample) {
  if (!sample || !Number.isFinite(sample.rttMs) || sample.rttMs < 0) return false;
  if (sample.rttMs > CLOCK_SYNC_MAX_ALIGNMENT_RTT_MS) return false;   // 250 ms
  this.samples.push(sample);
  if (this.samples.length > this.capacity) this.samples.shift();      // 8
  return true;
}
get confident() { return this.samples.length >= CLOCK_SYNC_MIN_ACCEPTED_SAMPLES; }  // 2
get bestSample() { /* lowest RTT; ties prefer the newest */ }
get offsetMs() { return this.confident ? this.bestSample?.offsetMs ?? 0 : 0; }
```

Returning `0` until confident is deliberate: callers that add `0` are "no correction", and
`useStageRoom` decides separately to use the receipt anchor instead.

3. **The sampler** — `ClockSyncSampler` drives `ping()` on a timer. The startup burst is four samples
500 ms apart, so the filter becomes usable within the first second instead of after the steady 5 s
cadence. A failed invocation backs off to 5 s so an unreachable connection is never pinged in a tight
loop.

Two subtleties:

- **`sampleOnce` uses `pong?.t1`, not a locally recorded send time.** The transport (`stageClient.ping`)
  stamps `t1` immediately before `invoke`, and the sampler trusts the echo. That excludes a main-thread
  stall between "decide to ping" and "frame actually sent".
- **Generations.** `stop()` increments `generation`; an in-flight reply from the old generation is
  discarded when it resolves. This is what stops a reconnect from installing a stale offset.

The review's open nit is in `tick()`: when `inFlightGeneration === generation`, the method returns
without scheduling the next tick. In practice only one tick is ever scheduled at a time so it is hard
to reach, but the review checked the current tree and the path is unchanged. (The review cites
`clockSync.ts:239-241`; line numbers have shifted, the shape has not.)

### 6.2 `useStageRoom` — wiring Layer 1 into the stage

`useStageRoom` owns one `StageClient` and one `ClockSyncSampler`. It records every playback projection
as a receipt anchor:

```ts
const notePlaybackAnchor = (playback) => {
  const anchorServerMs = Date.parse(playback?.timeAnchorUtc ?? '');
  if (!Number.isFinite(anchorServerMs)) return;
  receiptAnchorRef.current = { anchorServerMs, receivedAtLocalMs: Date.now() };
};
```

`serverNow` then chooses between the two estimates:

```ts
const offsetMs = !clockConfidentRef.current && fallbackOffsetMs !== null
  ? fallbackOffsetMs
  : clockOffsetRef.current;
return serverNowMs(localNow, offsetMs) + bias;
```

So the first seconds after a join use the receipt anchor (one-way-delay bounded), and once two Ping
samples are accepted the min-RTT offset takes over. The `bias` is test-only:
`window.__karaokayClockBiasMs`, which the E2E uses to stand in for RTT/2.

The hook also exposes `reportSongEnded` (swallowing transient hub faults so a video surface never
shows an error banner for a failed report) and `setGuideVocalVolume`. Both are idempotent at the
server.

### 6.3 `playbackController.ts` — Layer 2

This is the biggest new client file, and the cleanest way to read it is as three pure functions plus a
state machine.

**`authoritativePositionMs`** is the projection:

```ts
if (playback.status !== PlaybackStatus.Playing) return base;
return Math.max(0, base + Math.max(0, nowMs - anchorMs));
```

**`planAudioStart`** is the one-time alignment:

```ts
const expectedMs = Math.max(0, authoritativePositionMs(playback, nowMs));
const seekOffsetMs = expectedMs - safeAudioPositionMs;
const startOffsetMs = Math.min(maxOffsetMs, Math.max(0, safeAudioPositionMs + seekOffsetMs));

return { expectedMs, audioPositionMs: safeAudioPositionMs, seekOffsetMs, startOffsetMs };
```

That is the literal §5.3 formula: `seekOffset = expectedMs − audioPositionMs`. For a fresh song
`audioPositionMs` is 0, so it starts at the anchor. For a resume it is the engine's paused position,
so the seek expresses the difference. For a stage joining mid-song it is 0 on a fresh engine and the
whole position becomes the start offset. The 50 ms `AUDIO_END_MARGIN_MS` keeps a start off the exact
buffer end.

**`decidePlayback`** is the transition table:

| Room state | Decision |
|---|---|
| no cursor / no current song | `idle` |
| current song is YouTube | `video` |
| audio gate not passed | `await-unlock` |
| `SongEnded` | `ended` (report once, even if never loaded) |
| loaded item != cursor | `load` |
| `Countdown` | `countdown` |
| `Playing` and engine not playing | `start` |
| `Playing` and engine playing | `hold` (unless a restart is recognised) |
| `Paused` and engine playing | `pause` |
| `Paused` and engine stopped | `hold` paused |
| anything else | `stop` |

The restart rule is the only clever bit:

```ts
const restart = input.enginePlaying
  && plan.expectedMs < RESTART_POSITION_MAX_MS      // authority is at the top
  && input.enginePositionMs > RESTART_AUDIO_MIN_MS; // but audio is not
```

Both thresholds are 1 000 ms. That distinguishes a genuine Restart from ordinary drift: the
authority says "position < 1 s" while the engine says "I am past 1 s", so the only consistent
explanation is a restart. Everything else is `hold`, which is the "never re-slave mid-song" rule.

**`StagePlaybackController`** wraps those functions and drives the engine:

- `update(playback, audioUnlocked)` → `reconcile()`.
- `beginLoad` guards against duplicate loads with a `pending` record and a monotonic `token`; a
  superseded load is discarded after each `await`.
- `handleEngineEnded()` reports the queue item **once**, through a `reportedEnds` set, then publishes
  `ended`. `StageShell` routes `engine.onEnded` through this first, so a room song's natural end is
  not mistaken for a demo song's.
- `cancel()` is the StrictMode-safe cleanup: it stops in-flight work but the remount reconciles again.

`useStagePlayback` binds it to React and publishes a debug surface (`window.__karaokayPlayback`) with
`expectedMs`, `audioPositionMs`, `driftMs`, and `anchor` — the E2E reads exactly these.

### 6.4 `songMedia.ts` — the GUID→slug bridge

The stage receives a `SongDto` from `PlaybackStateDto.currentSong`. In database mode its `id` is the
GUID the media endpoints expect. In the no-database fallback the REST id is the Slice 2 directory
slug while the hub uses the deterministic demo GUID. The catalog exposes both (`id` and `enqueueId`),
so one lookup bridges them:

```ts
export function mediaIdFromCatalog(song, songs) {
  const match = songs.find(
    (entry) => entry.id === song.id || (entry.enqueueId != null && entry.enqueueId === song.id),
  );
  const mediaId = typeof match?.id === 'string' ? match.id.trim() : '';
  return mediaId.length > 0 ? mediaId : song.id;
}
```

The catalog is cached for **30 s** (`SONG_CATALOG_TTL_MS`). A miss against a cached snapshot
invalidates and retries once, because a song can be added or finish downloading while the room is
live. If `/api/songs` is unavailable entirely, `resolveTrackSource` falls back to the hub id — the
best remaining guess, and the only guess in a database-less deployment.

`loadLibraryTrack` fetches stems and `lyrics.json`; `playbackController` hands the lyrics to the
canvas renderer and the URLs to the engine.

### 6.5 `videoSync.ts`, `useStageVideo.ts`, and `useStageClock.ts`

These three have distinct jobs:

| File | Job |
|---|---|
| `videoSync.ts` | Pure math: is this YouTube, where should the video be, hard or soft nudge. |
| `useStageVideo.ts` | Owns the persistent `<video>`, sources/clears it, runs the 500 ms sync loop, reports ends. |
| `useStageClock.ts` | A single `requestAnimationFrame` loop feeding subscribers (the canvas), so React never re-renders per frame. |

The correction thresholds are:

```ts
export const HARD_NUDGE_MS = 120;
export const SOFT_NUDGE_MS = 40;
export const SOFT_NUDGE_RATE = 0.005;
```

- `|drift| > 120 ms` → seek immediately (`video.currentTime = expected / 1000`), rate back to 1.
- `40 < |drift| ≤ 120` → `playbackRate = 1 ± 0.005` for up to 5 s (`SOFT_NUDGE_RESET_MS`).
- `|drift| ≤ 40` → dead band, rate 1.

Note the number: the **hard-nudge threshold is 120 ms**, which is the §5.3 figure. The "+300 ms" you
may remember from the Slice 4 definition of done is the *injected* drift in `e2e/youtube.spec.ts`:
the test pushes the video 300 ms off and expects the hard nudge to correct it. It is not a threshold.

`useStageVideo` also owns the error/autoplay UX: a `NotAllowedError` becomes "Tap the stage to start
the video", and `primeForUnlock()` starts the element muted inside the audio-gesture click so the
subsequent `createMediaElementSource` attach is legal.

### 6.6 `StageShell.tsx` — room takeover

The shell now has three owners of the screen, in priority order:

1. **Video mode** (`video.mode === 'video'`): the room's current song is a YouTube item. The shell
   stops demo stems and shows the `<video>`.
2. **Room library playback** (`roomPlayback.status.active && !videoMode`): the controller drives the
   stems. `effectivePhase = stagePhaseForRoom(roomPlayback.status.phase)`.
3. **Local demo** (`DemoTrackPicker`): only when neither of the above is active. This preserves
   `npm run dev`, the database-less Playwright specs, and the "stage with no API" path.

The takeover is one-way for the duration of a song:

```tsx
useEffect(() => {
  if (!roomActive) return;
  tokenRef.current += 1;          // invalidate any demo load
  setSelection(null);
  setCountdown(null);
  setLoadError(null);
  rendererRef.current?.clear();
}, [roomActive]);
```

Test attributes live on `<main className="stage">`:

```tsx
data-phase={effectivePhase}
data-video={videoMode ? 'true' : 'false'}
data-room={roomActive ? 'true' : 'false'}
data-song-id={roomActive ? roomSong?.id : undefined}
data-queue-item-id={roomActive ? roomPlayback.status.queueItemId ?? undefined : undefined}
```

`data-phase` now includes `paused`, which is what lets the pause/resume E2E assert a state rather
than a pixel.

The count-in display is four dots:

```tsx
{Array.from({ length: roomActive ? roomCountdownBeats : countdown?.beatsLeft ?? 0 }, ...)}
```

One nuance to be honest about: `roomCountdownBeats` comes from `roomPlayback.status.countdownBeats`,
and `useStageRoom` *does* fold `CountdownTicked` into the stage's playback projection via
`applyCountdownTick`. So even though no `PlaybackChanged` is re-broadcast during the count-in, the
stage's dots can decrement as the ticks arrive. ADR-008's sentence that the stage dots "stay at four"
describes the intent (the fixed server cue) rather than this exact wiring; the server's four-beat
contract is authoritative either way. Only the local `DemoTrackPicker` derives dots from
`lyrics.json` gaps.

The guide-vocal slider is server-authoritative when a room is live. A drag emits many `input` events,
and each distinct value would persist through the single-writer channel ahead of the next command, so
`createTrailingDebounce(100 ms)` coalesces to one send:

```tsx
const guideSend = useMemo(
  () => createTrailingDebounce<number>(GUIDE_VOLUME_SEND_DEBOUNCE_MS, (value) => {
    void setGuideVocalVolume(value);
  }),
  [setGuideVocalVolume],
);
```

The local gain changes immediately; the server echo (`playback.guideVocalVolume`) wins afterwards.

### 6.7 `WebAudioEngine.pause()`

The change is small and load-bearing:

```ts
pause(): number {
  const positionMs = Math.max(0, this.positionMs());
  this.stoppedByUs = true;
  this.stopSources();
  this.isPlaying = false;
  this.startCtxTime = 0;
  this.startOffsetMs = positionMs;
  return positionMs;
}
```

`stopSources()` disconnects and stops the buffer sources but leaves the decoded
`AudioBuffer`s in place, so a subsequent `start()` reuses them without re-fetching or re-decoding.
`stoppedByUs = true` makes the `onended` handler ignore the stop, so a pause never looks like a
natural end. `startOffsetMs` is the preserved position; `positionMs()` returns it while stopped.

This is what makes Pause/Resume from a phone feel instant on the stage: the only thing that crosses
the network is the anchor, and the stage already has the media.

### 6.8 Phone: `PlaybackControls.tsx` and `applyCountdownTick`

The phone's ring is an SVG arc driven by one number:

```tsx
const total = Math.max(1, tick.totalBeats);
const remaining = Math.min(total, Math.max(0, tick.remainingBeats));
const progress = remaining / total;
const dashOffset = RING_CIRCUMFERENCE * (1 - progress);
```

`RING_RADIUS = 16`, `viewBox="0 0 40 40"`, and the CSS transition duration is `tick.beatMs` (500), so
each tick animates one beat locally instead of streaming frames. The wrapper carries
`data-remaining-beats`, `data-total-beats`, and `data-beat-ms` for tests.

`canPause` includes `Countdown`:

```tsx
const canPause = isPlaying || playback.status === PlaybackStatus.Countdown;
```

That mirrors `QueueService.PausePlaybackAsync`, which accepts `Playing or Countdown`. Slice 3's UI
only enabled Pause while playing, so the server supported something the phone could not reach.

`roomState.applyCountdownTick` is where the ring state comes from. It ignores a tick for a stale item
or after the room left `Countdown`, clamps the beat, defaults a missing `beatMs` to
`DEFAULT_COUNTDOWN_BEAT_MS = 500`, and remembers the largest `totalBeats` seen so the ring remains
proportional even though the server only sends a decreasing value. Duplicate ticks are value-stable
(the reducer returns the same object), which keeps React from re-rendering on a repeated beat.

The count-in projection also exists for snapshots: a `Countdown` snapshot with no prior tick seeds
the ring from `countdownRemainingBeats`, so a phone that joins mid-count-in still shows something
sensible.

### 6.9 Test seams and debug surfaces

The slice adds a small, deliberate observability surface:

| Surface | Owner | Purpose |
|---|---|---|
| `window.__karaokayClockBiasMs` | `useStageRoom` | Test-only residual clock error (E2E uses 40 ms). |
| `window.__karaokayPlayback` | `useStagePlayback` | `phase`, `songId`, `seekOffsetMs`, `expectedMs`, `driftMs`, `anchor`. |
| `window.__karaokayVideo` | `useStageVideo` | `mode`, `attached`, `notice`, `injectDrift`, `syncNow`. |
| `window.__karaokayTestClock` | `WebAudioEngine` | Deterministic media clock for unit tests and Playwright. |
| `data-room`, `data-song-id`, `data-queue-item-id`, `data-phase` | `StageShell` | Assert the takeover without reading internal state. |

None of these exist in a production path unless the page is a stage, and they are all read-only or
explicitly test hooks.

---

## 7. Architecture decisions, with consequences

### 7.1 A single-writer channel authority

**Context.** Slice 3 had several hub methods mutating playback, each taking the room lock. Ordering
was correct only if every call site stayed disciplined, and the duplicate-end race needed per-call-site
guards.

**Options.** (a) Keep direct mutation and add guards; (b) funnel every playback transition through one
hosted service over a channel.

**Choice.** (b). `PlaybackAuthority` consumes `PlaybackCommand(RoomCommand, TaskCompletionSource)` and
is the only component that applies playback transitions. Enqueue and move stay direct because they
touch neither playback nor the cursor.

**Consequences.** Persistence order and broadcast order are the same order by construction. The
duplicate-end race has one guard. The cost is a queue hop and a channel bound; commands are tiny and
already per-room serialized, so the bound exists only to stop unbounded growth. A full channel fails
the invocation after 2 s instead of hanging it.

### 7.2 Read-your-writes via `TaskCompletionSource`

**Context.** §5.1 requires hub callers to observe persisted-then-broadcast state. A queued-only return
would let a phone Start and immediately read a stale snapshot; tests could not distinguish "queued"
from "applied".

**Options.** (a) Return when the command is queued; (b) return when the authority completes it.

**Choice.** (b). The consume loop signals `TrySetResult(handled)` after persistence and broadcast.

**Consequences.** The phone's immediate read is honest, and integration tests can assert the
post-command state without polling. The command's processing time is now on the hub invocation's
critical path; that is the correct trade for a party-scale app, and the tick loop is deliberately
separate so a count-in never blocks a command.

### 7.3 A fixed 500 ms beat instead of per-song tempo

**Context.** `lyrics.json` carries `countdownBeats`/`beatMs` per song, and the local picker uses them.
The blueprint left the room's beat source open.

**Options.** (a) Read the song's gap metadata per Start/tick; (b) fix the room beat at four × 500 ms.

**Choice.** (b), pinned in `PlaybackTiming`.

**Consequences.** The authority stays independent of media parsing; every screen gets the same cue
without file I/O. The count-in is a party cue, not a musical subdivision. The local
`DemoTrackPicker` still derives its dots from `lyrics.json`, which is why the two count-ins look
similar but are fed from different places.

### 7.4 Write-ahead sequence minting

**Context.** Rehydration must restore a monotonic sequence, or reconnecting clients see deltas rewind.
Slice 3 kept the sequence only in memory.

**Options.** (a) Persist after the mutation and read it back; (b) mint before persisting, in the same
`SaveChanges`.

**Choice.** (b). `NextQueueSequence()` runs before `PersistRoomStateAsync`, which writes
`PartyRoom.QueueSequence` alongside the queue and playback columns.

**Consequences.** The database can never be behind the wire. The cost is a persisted column and a
migration, both small. A manual row edit can still invent a value the authority did not mint;
`RestoreQueueSequence` is its only consumer, and the database is not the live writer.

### 7.5 Audio as ground truth, aligned once

**Context.** A stage can join mid-song, a phone can pause and resume, and a browser's audio clock
drifts from wall time by tens of milliseconds over a song.

**Options.** (a) Periodically re-seek the audio to `PositionMs + elapsed`; (b) align once at start and
let the audio clock run.

**Choice.** (b), with one-time `seekOffset = expectedMs − audioPositionMs`. The video surface is the
exception and gets the §5.3 hard/soft nudges.

**Consequences.** No mid-song clicks or buffer restarts, and lyrics stay frame-accurate because they
render against the same audio clock. The cost is that a long-running stage can drift from the server
by the audio hardware's own error; that error is far smaller than the correction artifacts would be,
and a restart or a new song re-aligns.

### 7.6 Injectable `TimeProvider`

**Context.** The watchdog and count-in are wall-clock behaviours. Reading `DateTime.UtcNow` directly
would force sleep-based tests.

**Options.** (a) Real time and sleeps; (b) inject `TimeProvider` plus a tick-interval seam.

**Choice.** (b). Public constructors default to `TimeProvider.System` and 100 ms; internal
constructors take the fake clock.

**Consequences.** Tests are fast and deterministic, and they can exercise "the loop ran late" and
"the process restarted mid-count-in" — neither of which a sleep-based test can do reliably. The
read-side projection still uses real `DateTime.UtcNow`, which is correct: it is describing the wire,
not deciding a transition.

### 7.7 A full-room rehydration query

**Context.** M4 required a cold restart to restore rooms, queues, anchors, and sequences. A party has
tens of rooms with tens of rows each.

**Options.** (a) Rehydrate lazily on first join; (b) load every open room once at startup; (c) load
only the room being joined.

**Choice.** (b), with one `AsNoTracking` query over open rooms plus an ordered queue include.

**Consequences.** A reconnecting guest finds the room already live, and the party resumes without a
cache-miss path. The cost is a startup query proportional to open rooms; accepted for party scale and
a no-op without a connection string. Lazy rehydration would need a per-room async path and would make
"room exists" depend on database latency at join time.

### 7.8 Deliberately accepted debts

| # | Debt | Why it was not fixed in Slice 4 |
|---|---|---|
| M2 | Stage role is self-declared. | Needs a join-contract change and an ADR (stage secret or one-stage claim). |
| M3 | Rooms are never collected. | Room GC is later-slice work: idle expiry, a sweep, a creation limit. |
| — | Fixed beat ignores per-song tempo. | Accepted in ADR-008; the room cue is operational, not musical. |
| — | 256-command channel can back up. | Accepted: tiny commands, per-room serialization, 2 s fail-fast. |
| — | `clockSync` no-reschedule path. | Hard to reach (one tick scheduled at a time); recorded open in the review. |

---

## 8. Testing the slice

### 8.1 `PlaybackAuthorityTests` — the behavioural core

Every test runs against in-memory rooms, a `TestTimeProvider`, and a `RecordingHubClient` that
captures `PlaybackStates` and `CountdownTicks`. Twenty-one tests, grouped by what they protect:

**Two-phase Start and the tick contract**

- `StartEntersCountdownThenAutoTransitionsToPlayingWithFreshAnchor` — asserts `Countdown`, position 0,
  four beats, the cursor item still `Ready`; the first tick emits `[4]`; a 100 ms tick does not
  duplicate it; the window emits `[4,3,2,1]`; the transition to `Playing` has a fresh anchor exactly
  `CountdownWindow` after the count-in anchor; the ring stops.
- `CountdownCatchesUpWhenTheTickLoopRunsLate` — advancing the clock by the whole window in one step
  still emits all four beats in order before starting.
- `DuplicateStartIsIgnoredWhileCountingInAndWhilePlaying` — the anchor does not move.

**Pause / skip / restart interactions**

- `PauseDuringCountdownThenResumeStartsPlayingAtZero` — `Paused`, position 0, no anchor; five minutes
  later there are still no ticks; Resume starts `Playing` at 0.
- `SkipDuringCountdownCancelsTheCountdownAndAdvances` — B plays immediately, no ticks.
- `RestartDuringCountdownRestartsTheCountIn` — emits `[4,3]`, then Restart, then `[4]`; the anchor is
  new.
- `MovingTheCountingInItemIsRejected` — `QueueMoveResult.NotMovable`.

**Watchdog**

- `WatchdogEndsStalledPlaybackOnlyAfterDurationPlusGrace` — a 1 000 ms song is still `Playing` on a
  tick 5 500 ms past its anchor, then ends `Idle` on the next tick at 6 500 ms, once duration + 5 s
  grace has passed; the last broadcast is `Idle`.
- `WatchdogExemptsPausedRooms` — ten minutes paused changes nothing.

**Duplicate-end idempotency**

- `DuplicateEndReportCannotDoubleAdvance` — the second report for A is ignored while B plays.
- `WatchdogAfterStageReportCannotDoubleAdvance` — the stage wins, the watchdog sees B's cursor.
- `WatchdogBeforeStaleStageReportCannotDoubleAdvance` — the watchdog wins, the in-flight report is
  dropped.

**Removal through the authority**

- `RemoveCurrentItemCommandResetsPlaybackThroughTheAuthority` — one `PlaybackChanged(Idle)`, cursor
  cleared, remaining item renumbered; a second removal returns `false` (the hub's `NotFound`).
- `RemoveUpcomingItemCommandLeavesPlaybackUntouched` — no extra playback broadcast, anchor unchanged.

**AwaitingDownload**

- `StartOnADownloadingHeadAwaitsDownloadThenCountsInWhenItBecomesReady` — parks, no ticks while
  downloading, then `Countdown` and `[4,3,2,1]` once `Ready`; no second Start press.
- `DuplicateStartWhileAwaitingDownloadDoesNotReanchor`.
- `RestartWhileAwaitingDownloadStaysParkedInsteadOfStartingTheSong` — then a restart after `Ready`
  enters `Countdown`, not `Playing`.
- `DownloadCompletingForANonCursorItemDoesNotStartTheCountdown`.
- `FailedDownloadOfTheAwaitedItemReturnsTheRoomToIdle`.

**Cold restart and backpressure**

- `ColdRestartMidCountInDoesNotReplayAlreadyBroadcastBeats` — the fixture simulates a room restored
  1.2 s into the window; ticks are `[2]` then `[2,1]`, and no `3` or `4` is re-sent.
- `EnqueueFailsFastWhenTheCommandChannelIsFull` — fills 256 slots against an unstarted authority and
  asserts a `TimeoutException` on the 257th within a 100 ms injected write timeout.

### 8.2 Mapper, query, and queue-service tests

`RoomStateMapperTests` (four tests):

- `MapsQueuePlaybackAnchorVolumeAndSequence` — positions `0,5` become `0,1`; sequence 7 restores and
  the next delta is 8; the cursor keeps its anchor and is normalized to `Playing`.
- `DanglingCursorFallsBackToIdle`.
- `StaleDuplicatePlayingStateIsDemotedToReady`.
- `CountdownRoomKeepsTheCursorItemReady` — a 1 s-old anchor derives 2 remaining beats, the row stays
  `Ready`, and `CountdownAwaitingResume` is set. The test carries an explicit wall-clock caveat in a
  comment, which is honest: it is a real-clock fixture with a sub-second tolerance.

`RoomRehydrationQueryTests` (two tests):

- `OpenRoomQueryTranslatesWithOrderedQueueAndSongs` — Npgsql `ToQueryString` contains the three
  tables, `ORDER BY`, and the `ClosedAtUtc IS NULL` filter.
- `RehydrationIsANoOpWithoutADatabase` — an empty scope factory restores zero rooms.

`QueueServiceTests` extends the Slice 3 lifecycle tests with count-in and removal cases, including
`StartPlaybackCountsInThenBeginsTheFirstPlayableItem`, `StartFromPausedIsIgnoredSoResumeRemainsThePath`,
`RemovingTheCountingInItemResetsTheCountdownState`, and
`RemovingTheCurrentItemPersistsTheIdlePlaybackReset` (the EF InMemory read-back).

### 8.3 The five Docker-gated integration tests

All live in `RoomHubIntegrationTests.cs`, all call `RequireDocker()`, and all skip locally without
Docker with a readable reason. They run against Testcontainers PostgreSQL, the real host, and a real
SignalR client over long polling.

| Test | What it proves |
|---|---|
| `PlaybackLifecycleStartsPausesResumesSkipsAndIdles` | Start → `Countdown` (position 0, four beats) → `Playing`; pause/resume/skip/idle across the real wire. |
| `PlaybackAuthorityBroadcastsAnchorAndPreservesPositionAcrossPauseResume` | The two-phase broadcast; `[4,3,2,1]` at 500 ms; paused position ≥ 150 ms; resume within **±30 ms** of the authority's position. |
| `WatchdogEndsStalledPlaybackAfterDurationPlusGrace` | A 250 ms song ends `Idle` after count-in + duration + 5 s grace. |
| `DuplicateEndReportCannotDoubleAdvance` | A second stage report for the ended item leaves B playing. |
| `ColdRestartRehydratesQueueSequenceAndPlaybackAnchor` | A brand-new `WebApplicationFactory` against the same database restores sequence, queue, status, cursor, volume, and a still-advancing position. |

Two test-harness details are worth remembering:

- **`PlaybackCollector`** subscribes to `PlaybackChanged` and `WaitForAsync(predicate)` polls the
  collected broadcasts. The count-in assertion uses it instead of reading a snapshot immediately
  after Start. That is the fix recorded in the review's addendum: on a slow CI runner the 2 s
  count-in can legitimately finish before the snapshot round-trip, so an immediate `Countdown`
  assertion is a flake. The broadcast is the contract.
- **The ±30 ms pause/resume assertion** does not subtract two `PositionMs` values. It reconstructs
  the implied start instant from the resumed broadcast (`TimeAnchorUtc − PositionMs`) and compares it
  with the resume request time minus the paused position. Both terms come from the same message, so
  persistence and transport latency cancel; what remains is only the command's delivery into the
  authority. A silent restart at zero would move the implied start by the whole paused position and
  fail loudly.

### 8.4 Vitest

**`clockSync.test.ts`** (17 tests across six suites) covers the formula, rejection of a mismatched
echo / non-finite values / implausible RTT, the "0 until two samples" gate, the 8-sample trim,
min-RTT selection over a noisy window, rejection of a stall-biased outlier, the receipt-anchor
fallback, the startup burst settling to 5 s, back-off after a failed invocation, discarding an
in-flight sample after `stop()`, and resuming after a restart while a Ping is in flight.

**`playbackController.test.ts`** (18 tests) covers the projection, `planAudioStart` for fresh starts,
mid-song joins, resumes and buffer-end clamping, a ±150 ms budget under an 80 ms residual, the
decision table (idle, video, unlock, load, countdown, start, hold, pause, ended, stop), restart
recognition without a periodic re-slave, and reporting an end even when the stage never loaded the
item.

**`roomState.test.ts`** covers snapshots, contiguous deltas, gaps, duplicates, YouTube progress, and
the count-in block: seeds from the `Countdown` broadcast, counts 4 → 1, is value-stable for a
duplicate tick, ignores stale items and post-countdown ticks, and preserves the latest tick when a
broadcast carries no beat count.

**`useStageClock.test.ts`** covers one delivery per frame, the test-clock override, RAF cancellation
on the last unsubscribe, and `dispose`.

**`videoSync.test.ts`** covers YouTube-only selection, the URL shape, extrapolation while playing,
folding while paused, the test clock, and the three correction bands (hard seek above 120 ms, soft
nudge between 40 and 120 ms, dead band).

**`songMedia.test.ts`** covers the GUID→slug bridge in both modes, the fallback id, URL encoding, TTL
caching, invalidation on a miss, and recovery after a catalog failure.

### 8.5 `e2e/playback-authority.spec.ts`

Two tests, two browser contexts (stage + phone):

1. **`phone start, pause/resume and skip drive the stage from the authoritative anchor`** — joins a
   phone, enqueues two demo songs, injects `__karaokayClockBiasMs = 40` (standing in for RTT/2 of an
   80 ms link), presses Start, waits for `data-room="true"` and `data-phase="playing"`, asserts the
   audio position is past 0.2 s and the drift is under 150 ms. It then pauses at roughly 5 s of
   authoritative playback, asserts `data-phase="paused"`, resumes, and checks the position advanced
   rather than restarting. Skip switches the header to the second song and re-aligns.
2. **`a stage joining mid-song starts aligned to the server anchor under simulated latency`** — starts
   playback on the first stage, waits past 4 s, opens a second stage with `?code=`, biases its clock,
   and asserts its audio starts past 4 000 ms (not at zero), its unbiased drift is under 150 ms, and
   both stages' `expectedMs` agree within 150 ms.

The spec's comments explain why it asserts on the server-derived `expectedMs` for progress and
reserves the audio clock for the drift checks: headless Chrome's `AudioContext` clock intermittently
jumps under the multi-page load. That is a test-environment concession, not a production relaxation.

---

## 9. Frequently confusing points

**Why does the count-in tick not hold the command lock?**

Because it is derived progress, not durable state. Holding the room's single command semaphore while
awaiting a `CountdownTicked` fan-out would let one slow client block Pause and Skip for the whole
count-in. The tick takes the lock only long enough to re-check status/cursor/anchor and record which
beats it is about to send, then releases and broadcasts. A concurrent Pause wins by changing `Status`;
the tick's re-check returns without emitting a stale beat.

**Why is the beat fixed at 500 ms when `lyrics.json` has tempo?**

The room count-in is a party cue, and the authority should not read media to produce it. `lyrics.json`
tempo exists for the local `DemoTrackPicker`, which has no server. If a future product decision wants
per-song beats in rooms, that is an ADR-level change: it would put file I/O on the Start path or the
tick loop, and it would change the wire's `CountdownDto.BeatMs` from a constant to a per-song value.

**What happens if the command channel is full?**

`WriteAsync` waits, bounded by a **2 s** `CommandWriteTimeout`. If the timeout fires, the hub
invocation throws `TimeoutException`, which the client sees as a failed invocation. Commands are
never dropped silently. The 256 bound is a runaway-client guard, not an expected load: commands are
tiny and already serialized per room.

**How does a stage joining mid-song align?**

It receives `PlaybackStateDto` with `Status = Playing`, `PositionMs`, and `TimeAnchorUtc`. It computes
`expectedMs = PositionMs + (serverNow − TimeAnchorUtc)` using the min-RTT clock offset (or the receipt
anchor if not confident yet), then starts its stems at

```text
startOffsetMs = 0 + (expectedMs − 0) = expectedMs
```

so it begins at the correct point in the song. `planAudioStart` records `seekOffsetMs` for the debug
surface and the E2E; after that, the stage's audio is ground truth and is not re-seeked.

**Why does Slice 4 come after Slice 5 in git history?**

Because commit `324da8a` was labeled `feat(slice-4)` but implemented the documented Slice 5. ADR-007
kept the roadmap and credited the YouTube work as early Slice 5; the documented Slice 4 landed later
in `28fa633` and `8265eac`. The commit title is wrong, the contents are not.

**Why does `StartPlayback` while `Paused` do nothing?**

Because Resume is the explicit path back, and it preserves the position. Allowing Start from `Paused`
would silently rewind to position zero with a fresh anchor (or re-enter the count-in), which is
exactly the kind of surprise the single-writer rule exists to prevent. `StartFromPausedIsIgnoredSoResumeRemainsThePath`
pins it.

**Why doesn't `RemoveQueueItem` call `QueueService` directly any more?**

Because removing the item that is currently playing also resets playback to `Idle`. That is a playback
mutation, and Slice 4's rule is that the authority owns playback mutations. The hub still does the
membership check, then routes the command over the channel so the removal and the reset commit in one
`SaveChanges` under one `CommandLock`.

**Why does a `Playing` room survive a restart instead of being paused?**

Because the anchor is persisted and the mapper copies it verbatim. `PositionMs` is computed from
`now − anchor`, so time that passed while the process was down is counted, which is the honest
behaviour for a party that kept going. A dangling cursor is the exception and becomes `Idle`.

**Why does the clock offset stay at 0 at first?**

Two accepted samples are required before the filter's estimate is trusted. Until then `useStageRoom`
uses the receipt anchor, which is derived from the arrival time of the room-state message and cancels
the unknown clock difference. The 0 is a deliberate "no correction" value, not a claim that the
clocks agree.

**What is still NOT solved: M2 and M3?**

- **M2** — the stage role is self-declared in `JoinRequest`. Any guest with the code can join as
  `"stage"` and call `ReportSongEnded`. Slice 4 added no authentication; this needs a join-contract
  change and an ADR before the Slice 6 public demo.
- **M3** — rooms are never closed or garbage-collected. Every `/stage` load consumes a code from the
  279,841-code space. Cold-start rehydration exists now, but room lifecycle does not.

Also still open: the `clockSync` no-reschedule nit, the minor/nit backlog in the Slice 4 review, and
the fact that `LyricsLoaded`/`SearchCompleted` remain declared but unsent contract entries.

**Why does the stage sometimes show a "Tap the stage to start the video" notice?**

Because the browser refused `video.play()` with `NotAllowedError` after the audio gesture, usually
after a source change. The notice is actionable: the retry button re-enters the gesture path, reattaches
the media element if needed, and calls `syncNow()`. It is not a clock-sync failure.

---

## 10. Where Slice 4 ends, and what comes next

Slice 4 closed the Slice 3 review's **M4** (cold-start rehydration) and **M5** (stage playback
wiring), and it turned the blueprint's §5.1 and §5.3 from prose into code: one writer, one count-in,
one watchdog, one duplicate-end guard, one clock offset, one alignment, one rehydration query.

What is left is not playback correctness. It is:

- **M2** — authenticate the stage role before a public demo.
- **M3** — collect idle rooms and bound room creation.
- **Slice 6** — Azure free-tier deployment and CI/CD, including the two-layer cold start the
  blueprint describes (app scale-to-zero and database auto-pause). The startup path Slice 4 built —
  migrate, seed, rehydrate, then serve — is exactly what that deployment will exercise.
- **Slices 7–8** — the Android Oboe engine and the SkiaSharp TV stage, which reuse the same
  decision-level contract: server wall clock, local audio hardware as media clock.

The companion read is [`slice-5-walkthrough.md`](slice-5-walkthrough.md) for the YouTube pipeline that
landed early, and the review trail is in [`../review/slice-4-review.md`](../review/slice-4-review.md).

A good next afternoon: run the API without Postgres, open `/stage`, join from a phone, press Start,
and watch `CountdownTicked` arrive in the stage's Network tab. Then read
`PlaybackAuthority.TickCountdownAsync`, `QueueService.BeginCountdownLocked`, and
`playbackController.planAudioStart` side by side. That path touches the channel, the command lock, the
count-in, the clock filter, and the one-time seek — most of Slice 4 in one read.
