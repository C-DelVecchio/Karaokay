# Slice 3 Code Walkthrough — rooms, a real queue, and the phone remote

This is a guided read of Slice 3: the work that turned Karaokay from "a browser stage that plays
local demo songs" into "a party where phones join one room and change one shared queue." It is
written for the repo owner as a junior-developer tour: every term is defined where it first matters,
the code claims are tied to real files, and the last sections collect the questions that are easy to
ask twice.

Read this beside the preserved snapshots of earlier work:

- [`slice-1-walkthrough.md`](slice-1-walkthrough.md) — MIDI, `lyrics.json`, FFmpeg, and the importer.
- [`slice-2-walkthrough.md`](slice-2-walkthrough.md) — media endpoints, Web Audio, and the Canvas
  renderer as the project stood after Slice 2.
- [`../PROJECT_STATE.md`](../PROJECT_STATE.md) — the living project snapshot. If this walkthrough and
  that snapshot disagree, the snapshot plus the code win, and one of them should be fixed.

Slice 3 is the diff in commit `2d391de` plus the M1 correctness fix in `dc8baf4`. The M1 fix, its
tests, and the review record were committed together. Test counts below are quoted from
[`../review/slice-3-review.md`](../review/slice-3-review.md) rather than re-counted here: after the
fix the tree reports **86 passed / 9 skipped .NET tests**, **64 Vitest tests**, and **8 Playwright
tests** on a Docker-less machine.

---

## 1. What Slice 3 set out to solve

Slices 1 and 2 answered two questions:

1. *Where do legal songs and lyric timelines come from?* (Slice 1.)
2. *How does a browser fetch a song and paint synchronized lyrics?* (Slice 2.)

Slice 3 answers the social question: *how do many phones share one queue while one stage stays the
single source of truth?* The target loop is deliberately small:

```text
  phone A ─┐
  phone B ─┼── join room ABCD ──▶ shared queue ◀── stage shows code + QR
  phone C ─┘        │                  │
                    └── add / move / remove ──▶ every screen changes in real time
```

Three constraints shape the design:

- **One stage, many phones.** The stage is the TV/browser surface. Phones are remotes. They must see
  the same queue without polling.
- **The server owns the queue.** A phone cannot be trusted to be the authority; two phones pressing
  "move" at once must not corrupt ordering.
- **The database is a fallback, not the hot path.** A party needs sub-second reactions; the live room
  lives in memory, and Postgres exists so a future cold restart can rebuild it.

The slice also has a deliberately narrow playback subset. Start, pause, resume, skip, restart, and
"the song ended" exist so the queue has a cursor. The full `PlaybackAuthority`, watchdog, clock sync,
and video mode are Slice 4. That boundary is important: several "why does this feel incomplete?"
questions in §10 come back to it.

---

## 2. New concepts, defined once

### 2.1 SignalR, connections, hubs, and groups

**SignalR** is ASP.NET Core's library for pushing messages from server to client over a persistent
connection. It is not a request/response API that happens to be fast. A client opens one long-lived
connection, and the server can call methods on that client whenever it likes.

- A **persistent connection** stays open across many messages. Under the hood SignalR prefers
  **WebSockets**, a browser protocol that upgrades an HTTP connection into a two-way byte stream. If
  WebSockets are unavailable it falls back to Server-Sent Events or long polling. The application
  code usually does not care which transport won.
- A **hub** is a server class that clients invoke methods on. Karaokay has exactly one:
  `StageHub` at `/hubs/stage`.
- A **group** is a named set of connections inside a hub. Here the group name is the four-letter room
  code, so `Clients.Group("ABCD")` sends to every phone and stage in room ABCD at once.
- A **typed hub** is `Hub<IStageHubClient>` where the server-to-client methods are a C# interface
  (`IStageHubClient`). Instead of calling `SendAsync("QueueChanged", delta)` with a string that can
  silently drift, the server calls `Clients.Group(code).QueueChanged(delta)` and the compiler checks
  the name and arguments.
- **Per-invocation hub lifetime.** SignalR creates a new `StageHub` object for every invocation. You
  cannot store "this connection's room" in a field: the next invocation gets a different object. That
  is why Karaokay keeps connection→room membership in the singleton `RoomRegistry`.

### 2.2 EF Core, DbContext, migrations, and Npgsql

**EF Core** (Entity Framework Core) is Microsoft's **ORM** — an Object-Relational Mapper. It maps C#
classes to database tables and rows so most code works with objects instead of SQL strings.

- A **DbContext** is the unit of database work: it owns the entity sets (`Songs`, `Rooms`,
  `QueueItems`), tracks changes, and saves them in one `SaveChangesAsync` call.
- A **migration** is a checked-in C# file describing a schema change. `dotnet ef migrations add` writes
  it; `Database.MigrateAsync()` applies pending migrations at startup. Migrations are how the repo
  upgrades every developer's database without anyone running SQL by hand.
- **Npgsql** is the .NET driver and EF Core provider for PostgreSQL. The connection string is read
  from `ConnectionStrings:Karaokay`; when it is blank, the API does not register a `DbContext` at all.
- A **unique index** is a database constraint that rejects duplicate values. `Rooms.Code` has one, so
  two processes cannot successfully reserve the same four letters even if their in-memory maps race.
- **Seeding** means inserting starter rows. `DemoSongSeeder` syncs the demo media folders into the
  `Songs` table at startup so a fresh database is immediately playable.

### 2.3 DI lifetimes: singleton versus scoped

**Dependency injection** (DI) means a class asks for its dependencies as constructor parameters and a
container supplies them. The lifetime decides how often an instance is created:

| Lifetime | One instance per… | Used here for |
|---|---|---|
| Singleton | process | `RoomRegistry`, `RoomService`, `QueueService`, `SongCatalog`, `MediaLibrary` |
| Scoped | HTTP request / hub invocation scope | `KaraokayDbContext` |
| Transient | every resolution | small stateless services |

The hub is per-invocation, the registry is singleton, and the `DbContext` is scoped. A singleton
cannot hold a scoped `DbContext` directly — it would keep one context (and its change tracker) alive
forever and share it across threads. `QueueService` and `RoomService` therefore take
`IServiceScopeFactory` and create a fresh scope around each persistence call:

```csharp
await using var scope = _scopeFactory.CreateAsyncScope();
var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
if (database is null)
{
    return;
}
```

`GetService` returning `null` is deliberate: "no database configured" is a supported mode, not an
error. `GetRequiredService` would throw in tests and bare `dotnet run`.

### 2.4 Per-room serialization, locks, and the command lock

A **race condition** happens when two operations read and write shared state in an order nobody
intended. Karaokay avoids it per room, not globally: two different rooms can mutate in parallel, but
one room processes one command at a time.

`RoomState` has two different synchronization tools, and the difference matters:

- **`CommandLock`** is a `SemaphoreSlim(1, 1)`. It is async-aware (`WaitAsync`), so a command can hold
  it across `await` calls while it persists and broadcasts.
- **`SyncRoot`** is a plain `object` used with `lock` for short, synchronous reads and writes. A
  `lock` cannot be held across `await` in C#, and it must not be held while doing I/O.

The shape of every mutation is the same:

```text
await CommandLock
  lock (SyncRoot) { mutate the in-memory queue/playback }
  await persist (fresh DbContext scope)
  increment sequence
  broadcast QueueChanged / PlaybackChanged
finally release CommandLock
```

### 2.5 Monotonic sequence numbers, deltas, and idempotency

A **sequence number** is a counter that only goes up. Each room has `QueueSequence`, starting at 0;
the first queue mutation produces sequence 1, the next 2, and so on.

A **delta** is an incremental update: "these rows were added, these ids were removed, these rows were
updated." `QueueDelta` carries those three lists plus the sequence. Clients apply a delta only when
its sequence is exactly `previous + 1`. A gap means a message was missed; the client asks for a fresh
snapshot instead of trying to replay history.

**Idempotency** means doing the same operation twice has the same effect as doing it once. The key
example is `ReportSongEnded`: a stage may report the end, then a duplicate report or a future
watchdog may fire. The server ignores any report whose queue item is not the room's *current* item,
so the queue can never double-advance.

### 2.6 Testcontainers, QR join, and the rest

- **Testcontainers** starts a real Docker container from a test. `PostgresApiFixture` starts
  `postgres:16-alpine`, boots the real API against it, and therefore exercises real migrations,
  seeding, SQL, and SignalR. When Docker is unavailable the fixture records a skip reason and every
  test in the class skips with a readable message.
- **QR join** is a URL encoded as a QR image. The stage shows the image; a phone camera opens
  `/remote?code=ABCD`. The server renders the PNG with `QRCoder`.
- **A cursor** in this document means "the boundary between history and what may still change": the
  playing row and the ended rows before it. Only upcoming rows may be reordered.

---

## 3. File-by-file tour

Paths are relative to the repo root. "Why it exists" is the one-line reason you would keep the file.

### 3.1 Server and contracts

| Path | Role | Why it exists |
|---|---|---|
| `src/apps/Karaokay.Api/Program.cs` | Host wiring | Registers DI, SignalR, OpenAPI, CORS, optional Npgsql, endpoints, and startup migration/seed. |
| `src/apps/Karaokay.Api/Hubs/StageHub.cs` | Typed hub | The only client→server entry point for room commands. |
| `src/apps/Karaokay.Api/Endpoints/RoomsEndpoints.cs` | Room REST | Creates rooms, returns snapshots, renders QR PNGs. |
| `src/apps/Karaokay.Api/Endpoints/SongsEndpoints.cs` | Library REST | `GET /api/songs` through `SongCatalog` (DB or filesystem). |
| `src/apps/Karaokay.Api/Features/RoomRegistry.cs` | Hot state | `RoomState`, participants, memberships, sequence numbers. |
| `src/apps/Karaokay.Api/Features/RoomService.cs` | Room creation | Reserves a code in memory, persists it, retries on unique-index collisions. |
| `src/apps/Karaokay.Api/Features/QueueService.cs` | Queue engine | Serializes enqueue/move/remove/playback and broadcasts deltas. |
| `src/apps/Karaokay.Api/Features/QueueOrdering.cs` | Pure rules | `Move` and `SelectNext`, unit-testable without a database or hub. |
| `src/apps/Karaokay.Api/Features/SongCatalog.cs` | Song read side | One API over the `Songs` table or the Slice 2 filesystem scan. |
| `src/apps/Karaokay.Api/Features/DemoSongSeeder.cs` | Startup seed | Inserts/updates demo songs with deterministic ids. |
| `src/apps/Karaokay.Api/Features/FeatureOptions.cs` | Feature flags | `Features:EnableYouTubeIngestion`. |
| `src/apps/Karaokay.Api/Features/StartupLog.cs` | Startup logging | Source-generated log messages for migrations. |
| `src/core/Karaokay.Contracts/Hubs/IStageHub.cs` | Client→server contract | 15 typed hub methods. |
| `src/core/Karaokay.Contracts/Hubs/IStageHubClient.cs` | Server→client contract | 12 typed push methods. |
| `src/core/Karaokay.Contracts/Hubs/Dtos.cs` | Wire records | Join, snapshot, delta, playback, error, and YouTube shapes. |
| `src/core/Karaokay.Contracts/Hubs/WireEnums.cs` | Wire enums | BCL-only copies of the domain enums. |
| `src/core/Karaokay.Contracts/Hubs/RoomCommand.cs` | Future command shape | Reserved for Slice 4's single-writer authority. |
| `src/core/Karaokay.Domain/{Song,PartyRoom,QueueItem}.cs` | Persistence entities | The database model. |
| `src/core/Karaokay.Infrastructure/Persistence/KaraokayDbContext.cs` | EF model | Tables, keys, indexes, and the provider-specific `VideoId` filter. |
| `src/core/Karaokay.Infrastructure/Persistence/ProviderIndexFilters.cs` | Provider SQL | `"VideoId" IS NOT NULL` vs `[VideoId] IS NOT NULL`. |
| `src/core/Karaokay.Infrastructure/Persistence/DbUpdateExceptionExtensions.cs` | Collision detection | Recognizes PostgreSQL/SQL Server unique violations. |
| `src/core/Karaokay.Infrastructure/Persistence/PostgresMigrations/` | Schema | The initial `Songs`/`Rooms`/`QueueItems` migration. |
| `src/core/Karaokay.Application/DemoSongIds.cs` | Stable ids | UUIDv5 from a media-folder slug. |
| `src/core/Karaokay.Application/RoomCodeGenerator.cs` | Code generator | 23-letter alphabet, bounded retries, format validation. |
| `src/apps/Karaokay.AppHost/Program.cs` | Aspire | Starts PostgreSQL plus the API with a connection string. |

### 3.2 Web client and tests

| Path | Role | Why it exists |
|---|---|---|
| `src/clients/web/src/lib/signalr/protocol.ts` | Wire mirror | TypeScript names, enums, DTOs, and room-code validation. |
| `src/clients/web/src/lib/signalr/stageClient.ts` | Hub proxy | Connect, join, reconnect, sequence guard, full resync. |
| `src/clients/web/src/lib/signalr/roomState.ts` | Pure reducer | Snapshot/delta/playback/guest-count state transitions. |
| `src/clients/web/src/lib/signalr/sync.ts` | Sequence policy | `shouldAcceptDelta` and `decideReconnectAction`. |
| `src/clients/web/src/features/remote/*` | Phone remote | Join, lobby, library, queue, playback controls. |
| `src/clients/web/src/features/stage/useStageRoom.ts` | Stage room hook | Creates or joins a room, one quiet attempt. |
| `src/clients/web/src/features/stage/StageRoomPanel.tsx` | Stage overlay | Code, QR, guest count, first four queue rows. |
| `src/clients/web/e2e/rooms.spec.ts` | Two-context E2E | Proves the phone/stage loop and snapshot rejoin. |
| `src/tests/Karaokay.UnitTests/QueueServiceTests.cs` | Queue unit tests | M1 regressions plus playback transitions without a database. |
| `src/tests/Karaokay.IntegrationTests/RoomHubIntegrationTests.cs` | Hub integration | Real host + real PostgreSQL + real SignalR client. |
| `src/tests/Karaokay.IntegrationTests/PostgresApiFixture.cs` | Container fixture | Starts Postgres and applies production startup. |
| `src/tests/Karaokay.IntegrationTests/QrJoinUrlTests.cs` | QR URL tests | Absolute URL and `PublicBaseUrl` precedence. |

---

## 4. Server deep dive

### 4.1 `StageHub` — join, groups, and role gates

`StageHub` is declared once:

```csharp
public sealed partial class StageHub : Hub<IStageHubClient>, IStageHub
```

The class inherits the SignalR `Hub<IStageHubClient>` machinery and *also* implements the contract
interface `IStageHub`. That makes the server methods and the client mirror compile-checked, and it
lets `SignalRContractTests` reflect over the interface.

#### Join is a full-state resync

`JoinRoom` does seven things in order:

1. Normalize the code: `Trim().ToUpperInvariant()`, then `RoomRegistry.IsValidCode`.
2. Reject an unknown role (`stage`/`guest` only, case-insensitive).
3. Find the live `RoomState`; an unknown code returns `JoinAck(false, null, RoomNotFound)`.
4. Detach the connection from any previous room and remove any old participant row.
5. Normalize the display name (blank → `"Guest"`, max 64 characters) and mint a fresh session token.
6. Add the connection to the SignalR group named by the room code.
7. Send `JoinedRoom(snapshot)` to the caller and `ParticipantJoined(...)` to everyone else.

The important design sentence is in the source: *"Join and rejoin take exactly the same path: always
a full snapshot, never delta replay."* There is no separate reconnect protocol and no server-side
delta history.

Two smaller details:

- `DetachFromPreviousRoomAsync` removes the registry membership before the new join. If the
  connection is rejoining the *same* room, it returns early and does not broadcast a leave; the
  subsequent `ParticipantJoined` is therefore a duplicate presence event (review minor #5).
- `JoinRoom` returns a failed `JoinAck` instead of throwing. Most other methods send a `HubError` and
  throw a `HubException`, so a client can see two error channels: a structured push and a failed
  invocation. The TypeScript client handles both (`onHubError` and the promise rejection).

#### `RequireRoomAsync` and `RequireStageAsync`

Every non-join command starts with `RequireRoomAsync`. It looks up the connection's membership in
the singleton registry and returns both the room and the participant. If there is no membership, it
pushes `HubError(NotJoined)` and throws.

`RequireStageAsync` adds one check:

```csharp
if (!participant.IsStage)
{
    await SendErrorAsync(HubErrorCodes.ForbiddenRole, "This operation is only available to the stage.");
    throw new HubException("This operation is only available to the stage.");
}
```

Only `ReportSongEnded` and `UpdateStageCapabilities` use it. The gate is real, but the role itself is
self-declared in `JoinRequest.Role`; anyone who knows the code can claim `"stage"`. That is review
major **M2**, scheduled before the public demo rather than fixed in Slice 3.

#### `ReportSongEnded` and idempotency

The hub delegates to the queue service:

```csharp
public async Task ReportSongEnded(Guid queueItemId)
{
    var (room, _) = await RequireStageAsync().ConfigureAwait(false);

    // Idempotent by construction: anything but the current item is silently ignored ...
    await _queue.ReportSongEndedAsync(room, queueItemId, CancellationToken.None).ConfigureAwait(false);
}
```

`QueueService.ReportSongEndedAsync` takes the command lock and compares `room.CurrentQueueItemId`
with the supplied id under `SyncRoot`. A mismatch logs at debug level and returns `false`; a match
calls the same advance path as `SkipSong`. This is why a duplicate stage report, or (in Slice 4) the
duration watchdog firing after a report, cannot advance twice.

#### Disconnect and presence

`OnDisconnectedAsync` removes the connection→room membership, removes the participant from the room,
and broadcasts `ParticipantLeft(displayName, guestCount)` to the room group. `GuestCount` counts only
participants whose role is `guest`, so the stage is never counted as a phone.

### 4.2 `RoomState`, `RoomRegistry`, and room creation

`RoomState` is the hot in-memory room. Its fields are:

| Field | Meaning |
|---|---|
| `Id`, `Code`, `CreatedAtUtc` | Identity and join code. |
| `CommandLock` | Async semaphore serializing whole commands. |
| `SyncRoot` | Monitor protecting short reads/writes. |
| `Status` | `PlaybackStatus` (`Idle`, `Playing`, `Paused`, …). |
| `CurrentQueueItemId` | The playback cursor. |
| `PlaybackStartedAtUtc` | Wall-clock start of the current playing span. |
| `PausedAtPositionMs` | Position frozen at pause. |
| `GuideVocalVolume` | 0..1, default 0.6. |
| `CountdownRemainingBeats` | Reserved for the Slice 4 count-in. |
| `StageCapabilities` | Last stage latency report (not persisted). |
| `QueueSequence` | `long`, read with `Interlocked.Read`. |
| `QueueEntries` | `List<RoomQueueEntry>`, ordered densely by position. |
| `ParticipantsByConnection` | `Dictionary<string, RoomParticipant>`. |

`RoomRegistry` holds two concurrent dictionaries: live rooms by code, and connection→membership.
`ActiveCodes` is a snapshot used to seed `RoomCodeGenerator` on each attempt.

`RoomService.CreateRoomAsync` is a small retry loop:

```text
for attempt = 0; attempt < RoomCodeGenerator.MaxAttempts; attempt++:   // MaxAttempts = 100
    code = generator.GenerateNewCode()          // avoids codes active in this process
    room = new RoomState(Guid.NewGuid(), code, now)
    if !registry.TryAdd(room): continue        // another request won this code in-process
    try persist PartyRoom row
    on unique violation: registry.Remove(code); continue   // DB says a stale row owns it
    on other error: registry.Remove(code); throw
    return room
throw InvalidOperationException("code space exhausted")
```

The in-memory reservation is the fast path. The database unique index is the cross-process backstop:
a second API replica, or a room row left from an earlier run, can still reject a code. The
`DbUpdateExceptionExtensions.IsUniqueConstraintViolation` helper walks the exception chain and
recognizes PostgreSQL `23505` and SQL Server errors `2601`/`2627`.

`POST /api/rooms` maps *any* `InvalidOperationException` to `503` "Room code space exhausted"
(review minor #10), and returns `200` rather than `201 Created` (review nit). When no connection
string is configured, `PersistRoomAsync` returns without writing: the room exists only in memory and
disappears on restart.

### 4.3 `QueueService` — lock, mutate, persist, broadcast

`QueueService` is a singleton with four dependencies: `IServiceScopeFactory`, `SongCatalog`,
`IHubContext<StageHub, IStageHubClient>`, and a logger. The typed `IHubContext` is how a non-hub
singleton broadcasts to a group.

#### Enqueue

`EnqueueLibrarySongAsync` first resolves the song through `SongCatalog.FindSongAsync`. A library song
is immediately `Ready`; there is no download step in Slice 3. It then takes the command lock, creates
a `RoomQueueEntry` at `Position = room.QueueEntries.Count`, persists the whole queue, increments the
sequence, and broadcasts:

```csharp
QueueChanged(new QueueDelta([ToDto(entry)], [], [], sequence))
```

`QueueAck` carries the new queue item id. The request name comes from the participant, and the soft
ownership token id is stored in `SessionTokenId` (not yet enforced; review minor #2).

#### Remove

Remove finds the row, removes it, renumbers every remaining position densely, and persists. If the
removed row was the current item, the room returns to `Idle` and a `PlaybackChanged` is also sent.
The broadcast is `QueueDelta(Added: [], Removed: [id], Updated: [all rows])`; the `Updated` list is
how clients learn the new positions.

#### Move and the M1 fix

`MoveQueueItemAsync` delegates all policy to the pure `QueueOrdering.Move`. If the result is not
`Moved`, it returns before persisting or incrementing the sequence. A successful move broadcasts all
rows as `Updated` with the new positions.

The full M1 story is in §4.4 because it is the most instructive bug in the slice.

#### Playback subset

All playback commands share `RunPlaybackCommandAsync`. It captures the playback fields and the queue
shape before mutation, applies a pure mutation function under `SyncRoot`, and then compares:

```csharp
playbackChanged = playbackBefore != PlaybackSnapshot.Capture(room);
queueChanged = !queueBefore.SequenceEqual(CaptureQueueLocked(room));
```

Only real changes persist and broadcast; a `Pause` while `Idle` or a `Start` on an empty queue is a
no-op and **does not consume a queue sequence**. The integration test
`NoOpPlaybackCommandsDoNotConsumeQueueSequence` pins that: the first real enqueue is still sequence 1.

The transitions are deliberately small:

| Command | Rule |
|---|---|
| `StartPlaybackAsync` | If already playing, no-op. Use the current item unless it is null, `Ended`, or `Failed`; otherwise the first `Ready`/`Queued` by position; otherwise `Idle`. |
| `PausePlaybackAsync` | Only from `Playing`; fold elapsed time into `PausedAtPositionMs`, clear the anchor, set `Paused`. |
| `ResumePlaybackAsync` | Only from `Paused`; set the anchor to `now - pausedMs`, clear paused, set `Playing`. |
| `RestartSongAsync` | Re-begin the current item at position 0. |
| `SkipSongAsync` | Mark current `Ended`, advance to the next playable row. |
| `ReportSongEndedAsync` | Same advance, but only if the id matches the current item. |
| `SetGuideVocalVolumeAsync` | Clamp NaN to 0 and the value to 0..1; persist and broadcast playback if changed. |

`BeginPlayingLocked` demotes any other `Playing` row to `Ready`, marks the chosen row `Playing`, sets
`CurrentQueueItemId`, `Status = Playing`, `PlaybackStartedAtUtc = now`, and clears the pause position.

#### What Slice 3 does not implement from §5.1

The blueprint's full room machine is `Idle → AwaitingDownload → Countdown → Playing ⇄ Paused →
SongEnded → advance`. Slice 3 implements only the rows marked Yes below:

| Status or item state | Reached in Slice 3? | Why |
|---|---|---|
| `PlaybackStatus.Idle` | Yes | Initial state and empty-queue state. |
| `AwaitingDownload` | No | No YouTube downloads yet; library songs are instantly `Ready`. |
| `Countdown` | No | Lead-in/count-in belongs to Slice 4's stage controller. |
| `Playing` | Yes | Set by Start, Skip, Restart, and the end report. |
| `Paused` | Yes | Set by Pause only. |
| `SongEnded` | No | Advance jumps straight to the next `Playing` or back to `Idle`. |
| `QueueItemState.Ready` | Yes | Every enqueued library song. |
| `Queued` | No | `IsPlayable` accepts it, but no Slice 3 path creates one. |
| `Downloading`/`Failed` | No | Not playable — `IsPlayable` accepts only `Ready`/`Queued` — and no Slice 3 path creates them. |
| `QueueItemState.Ended` | Yes | Set on the previous current row during an advance. |

`CountdownRemainingBeats` therefore stays 0, and `CountdownTicked`/`DownloadProgressChanged` are
wire contracts waiting for later slices. That is why the phone's playback card can say "Count-in" or
"Waiting for download" in its label table even though Slice 3 never puts the room in those states.

`BuildPlaybackStateLocked` projects the room into `PlaybackStateDto` with a position computed at
`DateTimeOffset.UtcNow`; that timestamp is the `TimeAnchorUtc` clients will extrapolate from once
clock sync lands in Slice 4. Countdown fields exist but are not exercised by this subset.

#### Persistence details

`PersistQueueAsync` opens a fresh scope, loads every `QueueItems` row for the room, deletes rows that
are no longer in memory, inserts genuinely new rows (skipping a row whose song is missing), and
updates position, requester, state, and token. It rewrites the whole queue on every mutation (review
minor #11), which is simple and correct but not scalable.

`PersistRoomAsync` updates the `PartyRoom` playback fields. Both methods return silently when no
`DbContext` is registered.

There is an honest caveat here, recorded as review minor #9: the code **mutates memory first**, then
persists, then broadcasts. The comment "persisted before broadcast" is true, but it is not
"persisted before mutate." A transient database failure leaves memory ahead of the database with no
compensation marker. Fixing that (write-ahead or a dirty-room retry queue) is scheduled work.

### 4.4 The M1 bug — how a reorder could skip a song

Before the fix, `MoveQueueItemAsync` accepted any row and `RenumberLocked` rewrote every position.
Advance then chose the next row only by `Position > current.Position`. Two failures were reachable
from the phone UI:

```text
Start:      A (playing, position 0)   B (ready, 1)   C (ready, 2)

Guest moves C "to the top":
            C (0)   A (1)   B (2)

A ends. Advance looks only for positions > 1, so B plays and C is skipped.
```

The second failure was worse: a guest could move the playing row itself down, leaving every remaining
row before the cursor. Advance found nothing and the room went `Idle` with songs still queued.
`QueueList` enabled move buttons for `Playing` and `Ended` rows, so both were one tap away.

The chosen fix is **a forward-only cursor over the existing dense positions**, plus a defensive
advance fallback:

- `QueueOrdering.Move` rejects `Playing`/`Ended` rows with `NotMovable`.
- It removes the moved row, computes a lower bound (after the current row, or after the ended-history
  prefix when idle), clamps the requested position into `[lowerBound, count]`, inserts, and
  renumbers.
- `QueueOrdering.SelectNext` scans forward from the current index for the first `Ready`/`Queued` row,
  then falls back to the first playable row *before* it. The fallback prevents `Idle` when an item
  became ready behind the cursor (for example, a download that finished while the current song
  played).

The rejected alternative was a new ordering token (a separate monotonically increasing sort key).
It would have required a migration, a new `QueueItemDto` field, wire changes, client changes, and new
tests for a rule that fits the existing dense positions. The fix keeps `QueueItem.Position` as the
single ordering fact and changes only the policy around it.

`StageHub.MoveQueueItem` maps `NotFound` to `HubError(NotFound)` and `NotMovable` to
`HubError(InvalidRequest)` with "Only upcoming songs can be reordered." The phone mirrors the rule in
`features/remote/queueOrdering.ts` so a guest never sends a request the server will reject.

### 4.5 `QueueOrdering` in detail

The class is intentionally free of EF Core and SignalR, which is why the M1 regressions are fast unit
tests:

```csharp
public static bool IsPlayable(RoomQueueEntry entry) =>
    entry.State is QueueItemState.Ready or QueueItemState.Queued;
```

`SelectNext` takes the list already ordered by position and the current entry. It finds the current
index, scans `currentIndex + 1 .. end`, then scans `0 .. currentIndex - 1`. If the current entry is
`null`, `currentIndex` is `-1`, so the first loop scans the whole list. A `null` return means the
room goes `Idle`.

`Move` has one subtle case worth knowing: when the room is idle, there is no current id, so the lower
bound is `CountEndedPrefix(ordered)` — the length of the leading run of `Ended` rows. That keeps
history at the front and upcoming rows after it, instead of letting a move interleave a future song
into ended history.

### 4.6 `RoomsEndpoints` — create, snapshot, QR

Three routes live under `/api/rooms`:

| Route | Behavior |
|---|---|
| `POST /api/rooms` | `RoomService.CreateRoomAsync`; returns `{ code, roomId }`. |
| `GET /api/rooms/{code}` | `QueueService.BuildSnapshot(room)`; 404 if no live room. |
| `GET /api/rooms/{code}/qr` | PNG QR of the absolute `/remote?code=XXXX` URL. |

The QR path builds an absolute URL because a phone camera cannot open a relative path:

```csharp
var joinPath = $"/remote?code={roomCode}";
var baseUrl = string.IsNullOrWhiteSpace(publicBaseUrl)
    ? $"{requestScheme}://{requestHost}"
    : publicBaseUrl.Trim().TrimEnd('/');
return $"{baseUrl}{joinPath}";
```

**`PublicBaseUrl`** is the configuration escape hatch. When it is set, it always wins, including when
it ends in `/`. When it is unset, the endpoint trusts `Request.Scheme` and `Request.Host`. Behind a
reverse proxy without forwarded-header handling, that can produce an internal hostname the phone
cannot reach. This is a documented nit, not a Slice 3 bug; `QrJoinUrlTests` pins the precedence and
the absolute-URL shape.

The PNG is rendered with `QRCodeGenerator.ECCLevel.Q` and `PngByteQRCode.GetGraphic(10)`, and served
with `Cache-Control: no-store` because the payload is tied to a live code.

### 4.7 `SongCatalog`, `DemoSongSeeder`, and the filesystem fallback

There are two read paths, chosen by whether a `DbContext` is registered:

```text
ConnectionStrings:Karaokay present            no connection string
        │                                             │
        ▼                                             ▼
  Songs table (EF Core)                       MediaLibrary folder scan
  ids are GUIDs                               ids are deterministic demo GUIDs
  media paths stored on the row               media resolved by folder whitelist
```

`SongCatalog.GetSongsAsync` reads the table ordered by title then id, or maps the scanned summaries
with `EnqueueId = DemoSongIds.ForSlug(slug)`. `FindSongAsync` resolves a hub song id the same way.
`ResolveMediaFileAsync` parses the id with `Guid.TryParse` and looks the song row up in database mode,
then re-checks that the stored relative path stays under the media root. `TryParse` tolerates
brace/parenthesis/no-dash GUID spellings beyond the canonical form, but the row lookup still gates the
result; in fallback mode it uses the Slice 2 directory whitelist. This is why a traversal attempt still
returns 404 in both modes.

The deterministic id is an RFC 4122 **version-5 UUID**: SHA-1 of a namespace plus the slug, with the
version and variant bits stamped. `DemoSongIds` derives a project namespace from the standard DNS
namespace and the string `"karaokay.demo-songs"`, so `synth-twinkle-variations` always maps to the
same GUID on every machine and in both storage modes. That is what makes the fallback and the database
interchangeable for the three demo songs.

`DemoSongSeeder` iterates `MediaLibrary.GetLibrarySongs()`, computes the id, upserts title, artist,
duration, relative media paths, file size, and `IsSyntheticDemo = true`. It always calls
`SaveChangesAsync` because an existing row may have changed after a library edit. Two review items
live here: every scanned song is marked synthetic (minor #7), and there is no orphan cleanup plus one
query per song (minor #8).

The fallback stays because it earns its keep: bare `dotnet run` with no Postgres, the media endpoint
integration tests, the Playwright suite, and the Docker-less half of the .NET suite all run without a
container. With a connection string, the `Songs` table is authoritative and the scan is only the
seed source. The fallback is read-only; rooms and queues always use the database when one is
configured.

### 4.8 `KaraokayDbContext`, migrations, and startup

`KaraokayDbContext` exposes three `DbSet`s: `Songs`, `Rooms`, and `QueueItems`. The Slice 3
migration creates exactly those three tables. (`DownloadJob` appears in the blueprint's §4.2 entity
sketch for the Slice 5 YouTube pipeline, but there is no `DownloadJob` class, `DbSet`, or table in
this slice.)

The model configuration is ordinary EF Core except for one provider-specific fragment:

```csharp
song.HasIndex(entity => entity.VideoId)
    .IsUnique()
    .HasFilter(ProviderIndexFilters.VideoIdFilterFor(Database.ProviderName));
```

PostgreSQL quotes identifiers with double quotes; SQL Server uses brackets. `ProviderIndexFilters`
returns `"VideoId" IS NOT NULL` or `[VideoId] IS NOT NULL`. Because the model differs by provider,
`KaraokayModelCacheKeyFactory` includes `Database.ProviderName` in the EF model cache key. Without
that, a process that built models for both providers could reuse the wrong cached model.

Other constraints worth seeing once:

| Table | Constraint |
|---|---|
| `Songs` | `Title` required, max 512; path/URL columns max 1024/2048; indexes on `Source`, `IsSyntheticDemo`, filtered unique `VideoId`. |
| `Rooms` | `Code` required, max 4, **unique**; index on `CreatedAtUtc`; cascade delete to queue items. |
| `QueueItems` | `RequestedByName` required, max 128; index on `(RoomId, Position)` and `SongId`; song delete is `Restrict`. |

`Program.cs` applies the schema and seed only when a connection string exists:

```csharp
if (!string.IsNullOrWhiteSpace(connectionString))
{
    await ApplyDatabaseStartupAsync(app).ConfigureAwait(false);
}
```

`ApplyDatabaseStartupAsync` asks for pending migrations, logs the count, calls `MigrateAsync`, then
runs `DemoSongSeeder.SeedAsync`. This is a startup migration strategy: simple for a single-replica
self-hosted app, but it means startup is coupled to the database being reachable. The public demo's
cold-start strategy and retry behavior are Slice 6a concerns.

`Karaokay.AppHost` wires local development:

```csharp
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("karaokay-postgres-data");
var karaokayDatabase = postgres.AddDatabase("Karaokay");
builder.AddProject<Projects.Karaokay_Api>("api")
    .WithReference(karaokayDatabase)
    .WithHttpHealthCheck("/healthz");
```

The data volume makes local database rows survive an AppHost restart, but it does not rehydrate a live
room: the rows are there, the in-memory `RoomRegistry` is empty, and nothing loads them back yet (M4).

---

## 5. Contracts deep dive

### 5.1 The two interfaces

`IStageHub` is the client→server surface. It has 15 methods:

```text
JoinRoom, Ping, SearchYouTube,
EnqueueLibrarySong, EnqueueYouTubeSong, RemoveQueueItem, MoveQueueItem,
StartPlayback, PausePlayback, ResumePlayback, SkipSong, RestartSong,
ReportSongEnded, SetGuideVocalVolume, UpdateStageCapabilities
```

`IStageHubClient` is the server→client surface. It has 12 methods:

```text
JoinedRoom, ParticipantJoined, ParticipantLeft,
QueueChanged, QueueItemStateChanged, DownloadProgressChanged,
PlaybackChanged, CountdownTicked, LyricsLoaded, SearchCompleted,
RoomClosed, HubError
```

Several client methods are contract placeholders for later slices: `DownloadProgressChanged`,
`CountdownTicked`, `LyricsLoaded`, and `SearchCompleted` are declared but not emitted by Slice 3.
Declaring them now keeps the wire contract stable and the TypeScript mirror honest.

### 5.2 DTO shapes

The key records are small and explicit:

```csharp
public sealed record JoinRequest(string RoomCode, string DisplayName, string Role);
public sealed record JoinAck(bool Success, string? SessionToken, string? Reason);

public sealed record RoomSnapshot(
    string RoomCode, PlaybackStatus Status, PlaybackStateDto Playback,
    IReadOnlyList<QueueItemDto> Queue, int GuestCount, long QueueSequence);

public sealed record QueueDelta(
    IReadOnlyList<QueueItemDto> Added, IReadOnlyList<Guid> Removed,
    IReadOnlyList<QueueItemDto> Updated, long Sequence);

public sealed record PlaybackStateDto(
    PlaybackStatus Status, Guid? CurrentQueueItemId, SongDto? CurrentSong,
    long PositionMs, DateTimeOffset TimeAnchorUtc,
    double GuideVocalVolume, int? CountdownRemainingBeats);
```

`RoomSnapshot` is the full truth. `QueueDelta` is the incremental truth. `PlaybackStateDto` carries
both a position and the wall-clock instant at which that position was true, so a client can compute
`position + (serverNow - TimeAnchorUtc)` while playing. Slice 3 already fills `TimeAnchorUtc`; Slice 4
turns it into a clock-synchronized anchor with the `Ping`/`Pong` offset filter.

`HubErrorDto` is the structured error: `Code`, `Message`, optional `Details`. The code constants are
`ForbiddenRole`, `RoomNotFound`, `NotJoined`, `InvalidRequest`, `YouTubeDisabled`, `NotFound`, and
`NotImplemented`.

### 5.3 Wire enums versus domain enums

`Karaokay.Contracts` is a leaf project: it cannot reference `Karaokay.Domain`, because the
architecture tests forbid it (Contracts must stay consumable by tools and clients without dragging
EF Core or DryWetMidi). So the four enums are duplicated in `WireEnums.cs`:

| Contract enum | Domain enum | Values |
|---|---|---|
| `MediaSource` | `MediaSource` | `Multitrack=0`, `YouTube=1` |
| `QueueItemState` | `QueueItemState` | `Queued=0` … `Ended=5` |
| `PlaybackStatus` | `PlaybackStatus` | `Idle=0` … `SongEnded=5` |
| `DownloadStage` | `DownloadStage` | `Queued=0` … `Failed=5` |

The conversion is a numeric cast, not a name lookup:

```csharp
private static Karaokay.Contracts.Hubs.QueueItemState ToWireState(QueueItemState state) =>
    (Karaokay.Contracts.Hubs.QueueItemState)(int)state;
```

That works only while both enums have identical names and numeric values. `WireEnums.cs` says a
contract test *can* assert parity, but no such test exists yet (review minor #14). Until it does, a
renumbering in one file is a silent wire break. The TypeScript mirror has the same numbers a third
time in `protocol.ts`, which the method-name test does not compare numerically.

### 5.4 `RoomCommand` — reserved, not used

`RoomCommand` is a flat record with `type`, `roomCode`, `connectionId`, `queueItemId`, `songId`,
`newPosition`, `volume`, and `audioLatencyEstimateMs`. It exists for Slice 4's single-writer
`PlaybackAuthority`, which will consume a `Channel<RoomCommand>`. The TypeScript mirror is in
`protocol.ts`; `SignalRContractTests` reflects over the two hub interfaces' method names, serializes a
populated `RoomCommand` with both camel-case and default JSON options and compares the resulting
property-name set against the TypeScript interface, and compares the `RoomCommandTypes` constants. It
does **not** inspect the properties of the other DTO records (`RoomSnapshot`, `QueueDelta`, and so on),
so property drift there can still slip through — the review's nit. `RoomCommand` is deliberately
unused in Slice 3; document or remove it until the authority consumes it.

---

## 6. Client deep dive

### 6.1 `StageClient` — connect, join, reconnect

`StageClient` is plain TypeScript with no React. It owns one `HubConnection`:

```ts
new HubConnectionBuilder()
  .withUrl(STAGE_HUB_URL)                 // '/hubs/stage'
  .withAutomaticReconnect([0, 1_000, 3_000, 5_000, 10_000])
  .configureLogging(LogLevel.None)
  .build();
```

`join(roomCode, displayName, role)` normalizes the code to upper case and trims the name, invokes
`JoinRoom`, and remembers `joinOptions` only on success. It also stores `sessionToken`, but does not
reset `lastQueueSequence` after the ack. That is intentional and explained in a comment: the server
pushes `JoinedRoom` *before* the `JoinRoom` invocation completes, and the `JoinedRoom` handler seeds
the sequence. Clearing it after the ack would reopen the guard on the first post-resync delta.

The client method map mirrors every server method, including the Slice 4/5 placeholders. All calls go
through `invoke<T>`, which lazily starts the connection.

### 6.2 Delta folding and the sequence guard

`shouldAcceptDelta` is three lines of policy:

```ts
if (previousSequence === null) return true;
return nextSequence === previousSequence + 1;
```

`null` means "no snapshot or sequence observed yet," so the first delta is accepted. A gap, a
duplicate, or an out-of-order message is rejected. In `StageClient`, rejection triggers `resync()`:
it re-invokes `JoinRoom` with the remembered options, and the server's `JoinedRoom` push replaces all
client state. A `resyncing` flag prevents a burst of bad deltas from stacking rejoin calls.

On `onreconnecting`, status becomes `reconnecting`. On `onreconnected`, status becomes `connected`
and, if a room was joined, `resync()` runs. On `onclose`, status becomes `disconnected`; automatic
reconnect does not keep retrying forever, so the lobby offers a manual **Reconnect** button that
calls `restart()`.

### 6.3 `useStageClient` and the pure reducer

`useStageClient` is the React binding. It creates one `StageClient` in `useState`, wires handlers,
and maps each push to a pure state transition:

| Push | Reducer |
|---|---|
| `JoinedRoom(snapshot)` | `applySnapshot`: replace snapshot, playback, sorted queue, guest count, sequence. |
| `QueueChanged(delta)` | `applyDelta`: reject gaps; remove ids; add new ids; replace updated rows; sort; update sequence. |
| `PlaybackChanged(dto)` | `applyPlayback`: replace playback and mirror status into the snapshot. |
| `ParticipantJoined`/`Left` | `applyGuestCount`: update the displayed count. |
| `HubError` | store the error for a banner. |
| `RoomClosed` | reset to an empty state. |

Keeping the reducer pure in `lib/signalr/roomState.ts` is what makes the sequence and merge behavior
unit-testable without a browser or a hub. `applySnapshot` is idempotent; `applyDelta` is a no-op when
the sequence is not contiguous.

### 6.4 Phone remote components

`RemoteApp` is the route-level switch: no snapshot means `JoinScreen`; a snapshot means `LobbyView`.

- **`JoinScreen`** reads `?code=`, uppercases it, filters to the 23-letter alphabet, and truncates to
  four characters. Join is enabled only for a valid code and a non-empty name. The silent sanitizing
  is the review's nit: a mistyped character disappears without a validation message.
- **`LobbyView`** owns the header (room code, QR from `/api/rooms/{code}/qr`, guest count), the
  reconnect/disconnect banners, the error banner, `PlaybackControls`, and the Library/Queue tabs.
  Controls are disabled while reconnecting or disconnected.
- **`LibraryTab`** fetches `/api/songs` once, shows title/artist/duration, and only enables "Add to
  queue" for a GUID-shaped id. `isLibrarySongId` is the guard: the hub enqueues by GUID, while the
  filesystem fallback's public `id` is a slug and its `enqueueId` is the deterministic GUID.
- **`QueueList`** builds a title map from the library response, renders positions and state labels,
  and offers up/down/remove buttons. It uses `queueOrdering.ts` to disable moves for `Playing`/`Ended`
  rows and for the first movable row's up button, mirroring the server's clamp.
- **`PlaybackControls`** shows the status label and current song, enables Start only in `Idle` or
  `SongEnded`, Pause only while `Playing`, Resume only while `Paused`, and Skip only when there is a
  current queue item.

The guest flow never persists the room code or token in the browser. A page reload returns to the
join screen with the code prefilled from `?code=`, which is why `rooms.spec.ts` can prove snapshot
rejoin by reloading and joining again (review minor #3 wants `sessionStorage`).

### 6.5 Stage-side room creation and QR overlay

`useStageRoom` makes one quiet attempt at mount:

1. If `?code=XXXX` is valid, join that room as role `stage` with display name `"Stage"`.
2. Otherwise `POST /api/rooms`, validate the returned code, and join it.
3. On any failure, set status `offline` and leave the Slice 2 local demo flow untouched.

`StageRoomPanel` renders nothing unless the status is `live` and a snapshot exists. When live it
shows the join hint, room code, QR, guest count, and the first four queue rows (titles fetched from
`/api/songs`). `StageShell` includes it as a corner overlay. The stage still plays local
`DemoTrackPicker` audio; the room queue is a display, not the audio source. That is review major
**M5**, and it is why the phone's Start/Pause/Skip can look disconnected from what the TV is doing.

The panel is also why the existing Slice 2 Playwright specs stayed green: with no API running, the
hook fails fast, the panel returns `null`, and the local stage renders exactly as before.

---

## 7. Architecture decisions, with consequences

### 7.1 Full-snapshot rejoin instead of delta replay

**Context.** A phone can sleep, lose Wi-Fi, or reload. It needs the room state when it comes back.

**Options.** (a) Keep a server-side delta log and replay from the client's last sequence; (b) accept
the reconnect as a new `JoinRoom` and send the whole snapshot.

**Choice.** (b). Join and rejoin are the same path. The snapshot is cheap at party scale (tens of
rows), and there is no log to retain, expire, or corrupt.

**Consequences.** Reconnect logic is trivial and robust; a client that misses a delta only needs one
round trip. The cost is bandwidth and a full client-state replacement, neither of which matters here.
It also means the sequence guard is defensive: the normal recovery path is a snapshot, not a replay.

### 7.2 A singleton in-memory registry as the hot state

**Context.** Most reads are snapshots and most writes are queue mutations. A database round trip per
keystroke would be slow and would make the party feel laggy.

**Options.** (a) Read and write Postgres on every command; (b) keep live rooms in memory and persist
asynchronously or after mutation.

**Choice.** (b), with the DB written on every mutation. `RoomRegistry` is the authoritative hot
state; the rows are the cold-restart target.

**Consequences.** The design depends on one API replica owning a room. ADR-001 already chooses a
single Azure Container App replica with sticky sessions precisely because SignalR groups are
in-process. A second replica would not see another replica's rooms even though both see the same
database. Room GC and cold-start rehydration are the open work (M3/M4).

### 7.3 Persist-before-broadcast, with a memory-first caveat

**Context.** Clients must not see a queue change that the database rejected, and the database should
not contain a change no client saw.

**Choice.** Mutate memory, persist, then broadcast. The broadcast is strictly after persistence, so
no client ever sees an unpersisted state. The reverse is not guaranteed: a persistence failure leaves
memory ahead of the database (review minor #9).

**Consequences.** In the happy path, the order is right and simple. In a database incident, a room can
temporarily diverge; there is no dirty-room retry or compensation. Slice 4's authority is the natural
place to make the write ordering explicit.

### 7.4 Forward-only cursor over a new ordering token

**Context.** Reordering needs a stable notion of "before/after the playing song."

**Options.** (a) Keep dense `Position` integers and forbid moving cursor rows; (b) add a separate
monotonic ordering token that can be rewritten independently.

**Choice.** (a). The M1 fix rejects `Playing`/`Ended` moves, clamps an upcoming row's target to the
first upcoming slot, and adds a defensive `SelectNext` fallback.

**Consequences.** No migration, no wire change, no client protocol change. Positions stay dense and
easy to display. The rule is slightly opinionated: "move to the top" means "play next," not "put it
behind the song that is already playing." That is exactly what a party wants.

### 7.5 Sequence-numbered deltas

**Context.** Multiple clients need to converge without polling.

**Choice.** A per-room `long` sequence, incremented under the command lock, attached to every
`QueueChanged`. Clients accept only `previous + 1`; a gap triggers a snapshot rejoin.

**Consequences.** Cheap detection of lost messages, no per-delta acknowledgement protocol, and no
server-side replay buffer. A duplicate delta is rejected as firmly as a gap. No-op commands do not
consume a sequence, so "sequence 1" always means the first real queue mutation.

### 7.6 Startup migrations and demo seeding

**Context.** A fresh checkout should run without a manual SQL step.

**Choice.** On startup, if a connection string exists, apply pending migrations and upsert demo songs
with deterministic ids.

**Consequences.** Great local ergonomics and one code path for tests and production. Startup now
depends on the database being reachable, and the seeder runs N queries plus an unconditional save.
For a single-replica self-host that is an acceptable trade; Slice 6a will need cold-start and retry
attention.

### 7.7 Testcontainers with Docker-optional skip guards

**Context.** The queue's correctness depends on PostgreSQL (unique indexes, real FKs), but not every
developer machine has Docker.

**Choice.** `PostgresApiFixture` tries to start `postgres:16-alpine`. If it cannot, it records a skip
reason and all `[SkippableFact]` tests skip with an actionable message. CI runs on a Docker-capable
runner, so the real path is exercised there.

**Consequences.** Local laptops stay green, but a Docker-less local run does not prove persistence.
The review explicitly says CI must run the suite. The fixture uses long polling because
`WebApplicationFactory`'s test server has no real socket; that is a transport detail, not a product
choice.

### 7.8 Deliberately accepted debts

Slice 3 shipped with four majors scheduled, not silently ignored:

| # | Debt | Why it was scheduled rather than fixed |
|---|---|---|
| M2 | Stage role is self-declared. | Fixing it changes the join contract and needs an ADR (stage secret or one-stage-per-room). |
| M3 | Rooms are never collected. | Room GC is blueprint Slice 8 work; it needs idle expiry, a sweep, and a creation limit. |
| M4 | Persistence is write-only. | Rehydration needs the playback authority's anchors; Slice 4 is the natural owner. |
| M5 | Phones mutate playback the stage ignores. | Wiring stage audio is Slice 4's controller/authority work. |

Each is a scope decision, not an oversight. They do mean the Slice 3 demo is honest only if you read
the stage queue as "shared state" and the stage audio as "local demo," which is exactly what §10
explains.

### 7.9 Wire-enum duplication

**Context.** Contracts must stay a BCL-only leaf, so it cannot reference Domain.

**Choice.** Duplicate the four enums with identical numeric values and cast by `(int)`.

**Consequences.** Tools and clients can consume Contracts without EF Core or MIDI. The risk is silent
drift; the missing parity test (minor #14) is the debt that keeps the trade honest.

---

## 8. The review process and the M1 fix story

The Slice 3 review had two passes:

- **Pass 1 — static audit.** Verdict **SHIP-WITH-NITS, conditional on M1**. The boundaries held: the
  hub is the only client entry point, queue mutations are serialized and broadcast with a sequence,
  rejoin is a full snapshot, and stage-only methods are role-gated.
- **Pass 2 — execution matrix on `2d391de`.** Release build clean; **78 passed / 6 skipped** .NET
  tests; Vitest 60/60; Playwright 8/8 (one cold-start flake in `audio-gate.spec.ts`, nit 11). The six
  skipped were the Testcontainers hub tests on a Docker-less machine.

After the M1 fix: **86 passed / 9 skipped** .NET (8 new queue-service unit tests and 3 new hub
integration tests, which skip without Docker), Vitest **64/64**, Playwright **8/8**. The current tree
therefore has 95 .NET tests total: 46 unit, 38 integration, 11 architecture.

### 8.1 What M1 taught

M1 is a good example of a bug that is invisible if you only test the happy path. Each individual
operation was "correct": move renumbered densely, advance picked a greater position. The failure came
from their interaction. The fix moved the rule into a pure module and pinned both reachable failures:

- moving a future item above the playing row makes it play next;
- moving the playing row is rejected and advance order is unchanged;
- moving ended history is rejected;
- advance plays an earlier ready item instead of going idle;
- start/pause/resume/skip/idle transitions.

The integration suite repeats the two reorder regressions and the lifecycle against the real hub and
PostgreSQL, so the fix is verified at both the unit and the wire level.

### 8.2 The scheduled majors

| # | Finding | Owner / timing |
|---|---|---|
| M2 | Any guest can claim role `stage`. | Before the Slice 6a public demo; needs an ADR. |
| M3 | Every `/stage` load creates a room; no GC, close, or rate limit. | With room-GC work before Slice 6a. |
| M4 | Rooms/queue persist but never rehydrate on cold restart. | Slice 4 or earlier; before Slice 6a. |
| M5 | Phone controls change room state the stage does not render. | Slice 4, or hide/flag the controls until then. |

### 8.3 Minors the owner should know

These are not all equally urgent; the review orders them roughly by priority.

| # | Problem | Why it matters |
|---|---|---|
| 1 | After an advance, `QueueItemStateChanged` is emitted for the ended item, not the new playing item. | Clients infer the new state from `QueueChanged`; a future consumer expecting the event will be surprised. |
| 2 | Move/remove have no ownership check. | `SessionTokenId` is stored but unused; any guest can remove anyone's song. |
| 3 | No auto-rejoin after page reload. | The code and token are not persisted; every reload is a manual rejoin. |
| 4 | Snapshot/delta join race can force needless resync. | A delta arriving around the snapshot invocation may look like a gap. |
| 5 | Rejoin broadcasts a duplicate `ParticipantJoined`. | Presence counts can look odd around reconnects. |
| 7 | Seeder marks every scanned song synthetic. | Any future real library row is mislabeled. |
| 8 | No orphan cleanup; N queries per startup seed. | Deleted folders leave rows; large libraries seed slowly. |
| 9 | Memory-first persistence has no compensation. | A transient DB failure can leave memory and database divergent. |
| 10 | `POST /api/rooms` maps any `InvalidOperationException` to `503`. | Real failures masquerade as code-space exhaustion. |
| 11 | Every mutation rewrites the whole queue; no length cap. | A cheap DoS vector on a public deployment. |
| 14 | No Domain↔Contracts enum parity test. | Silent wire break if one enum is renumbered. |
| 15 | Rejoin mints a fresh session token. | Future identity/ownership cannot survive a reconnect. |

Notable nits: the QR URL trusts the request `Host` unless `PublicBaseUrl` is set; `POST /api/rooms`
returns `200` instead of `201`; `QueueList` removes without confirmation; ended history is numbered
alongside upcoming rows; `/api/songs` is fetched separately by stage and phone; the stage keeps
showing `live` after a hub disconnect; the queue panel shows only four rows without saying so;
`/healthz` changed from JSON to plain text; build-time OpenAPI execution couples build to startup
code; YouTube-disabled feedback is missing in the UI; and the `audio-gate` decode timeout should rise
from 1.5 s to about 5 s.

### 8.4 The governance gap

ADR-005 says contract or enforcement changes need a new ADR or a blueprint version bump. Slice 3
added no ADR even though it introduced the EF Core persistence path, the demo seed, the in-memory
registry as the hot path, and the hub's role/session model. The blueprint is also internally
inconsistent: §4.3's `JoinRequest` has no session token, while §5.2 says clients hold
`RoomCode + SessionToken` and rejoin with it. Today rejoin works only as a fresh join that mints a
new token. The review leaves this as a design decision to settle before Slice 4 relies on reconnect
identity.

---

## 9. How to run and see Slice 3 work end-to-end

### 9.1 Prerequisites

- .NET 10 SDK (`dotnet --version` reports 10.x).
- Node.js 24 with npm.
- Optional: Docker, for the real PostgreSQL path and the Testcontainers suite.
- FFmpeg is only needed for importer/generator work, not for the room loop.

### 9.2 With Aspire and PostgreSQL

From the repo root:

```bash
dotnet run --project src/apps/Karaokay.AppHost
```

The AppHost starts a PostgreSQL container with the `karaokay-postgres-data` volume and the API with a
`Karaokay` connection string. The API applies the migration and seeds the demo songs before serving.
In a second terminal:

```bash
cd src/clients/web
npm ci
npm run dev
```

Open `http://localhost:5173/stage`. The stage creates a room, shows the four-letter code and QR in the
corner, and joins the hub as `stage`.

### 9.3 Bare API, no database

```bash
dotnet run --project src/apps/Karaokay.Api
```

The Development profile serves on `http://localhost:5255` and points `MediaRoot` at
`../../clients/web/public/demo-tracks`, so `/api/songs` returns the three demo songs from the
filesystem. With no `ConnectionStrings:Karaokay`, rooms and queues are in-memory only, media resolves
through the Slice 2 whitelist, and the Playwright room spec can run without Docker.

Useful probes:

```bash
curl http://localhost:5255/healthz
curl http://localhost:5255/api/songs
curl -X POST http://localhost:5255/api/rooms
curl http://localhost:5255/api/rooms/ABCD
curl -o /tmp/room-qr.png http://localhost:5255/api/rooms/ABCD/qr
```

The `POST` returns `{ "code": "ABCD", "roomId": "..." }`. Replace the example code with the real one
for the snapshot and QR calls. The QR is an image, so open `/tmp/room-qr.png` rather than printing it
to the terminal.

### 9.4 The phone flow

1. Open `/stage`; wait for the corner panel's code.
2. On a phone-sized window or a second browser context, open `/remote?code=ABCD`.
3. Enter a display name and join. The lobby shows the same code, a QR, and the guest count.
4. Add a library song. The stage's queue panel updates within about a second.
5. Open the Queue tab; move a future song up. The stage reorders too.
6. Remove a row. Both surfaces drop it.
7. Reload the phone and join again. The full snapshot restores the remaining queue with no delta
   replay.

The stage audio still comes from `DemoTrackPicker`; do not expect phone Start/Pause to change what
the TV is playing (M5).

### 9.5 Tests

```bash
dotnet build
dotnet test                      # 86 passed / 9 skipped locally without Docker
cd src/clients/web
npm test                         # 64 Vitest tests
npm run test:e2e                 # builds and runs 8 Playwright tests
npm run test:e2e -- --grep rooms # or target the room spec
```

`rooms.spec.ts` is the proof of the party loop. It opens two browser contexts, waits for the stage's
code, asserts the 23-letter format, joins the phone by `?code=`, adds two songs, checks the stage
queue within 1 s, reorders and removes, then reloads and rejoins the phone to prove snapshot
convergence. `playwright.config.ts` starts the API on port 5255 in filesystem fallback mode (no
PostgreSQL, no seed) plus `vite preview` on 4173, so the spec is self-contained.

The integration suite is the other half: `PostgresApiFixture` starts PostgreSQL, boots the real host,
and the tests cover 500 unique room codes, snapshot join/rejoin, monotonic deltas, no-op sequences,
stage-role rejection, both M1 regressions, and the playback lifecycle. Without Docker those nine
tests skip; CI runs them.

---

## 10. Frequently confusing points

**Why are there two sources of truth for songs?** There are two *storage modes*, not two answers at
runtime. With a connection string, `SongCatalog` reads the `Songs` table. Without one, it reads the
media folders. Both modes expose the same `SongSummaryDto`, and demo songs use the same deterministic
UUIDv5 in both, so the hub's `EnqueueLibrarySong` accepts the id in either mode. The table is
authoritative when present; the scan is a read-only fallback and the seed source.

**Why does the stage still play local demo audio?** Because Slice 3 wired the room *state*, not the
room *playback*. `StageShell` still uses its Slice 2 `DemoTrackPicker`; `StageRoomPanel` only displays
the queue. Phone Start/Pause/Skip mutate `RoomState`, and the phones see `PlaybackChanged`, but the
stage does not react. That is M5, assigned to Slice 4.

**Why does rejoin send the whole snapshot?** Because there is no server-side delta log. A snapshot is
a few dozen rows at party scale, and replacing state is simpler and harder to get wrong than replaying
history. The sequence number exists to detect gaps defensively, not to support replay.

**Why can't the phone move the playing song?** The playing song is the cursor, not an ordinary row.
Moving it down can leave every remaining row before the cursor, so advance finds nothing and the room
goes `Idle` with songs still queued; moving another song above it makes advance skip that song. The
server rejects cursor moves and clamps "to the top" to the first upcoming slot. The UI disables the
same buttons so the user never sees a rejected request.

**Why does the first queue delta have sequence 1, not 0?** `RoomState.QueueSequence` starts at 0 and
`NextQueueSequence` increments before broadcasting. Zero is the snapshot's "no mutations yet" value.
No-op commands do not increment, so the first real mutation is always 1.

**Why is the hub object recreated for every call?** That is SignalR's lifetime model. Per-connection
state cannot live in a field; it lives in the singleton `RoomRegistry`, keyed by `Context.ConnectionId`.
This is also why the registry, not the hub, is the thing the queue service talks to.

**Why is there a session token if nothing checks it?** It is the seed for future identity: ownership
of a queued song, reconnect identity, and eventually the stage authentication in M2. Today it is
issued on every join, returned in `JoinAck`, stored in `RoomParticipant`, and persisted as
`SessionTokenId` on queue rows, but move/remove do not enforce it (minor #2), and rejoin mints a new
one (minor #15).

**What is the difference between a queue position and a queue sequence?** Position is *where* a row
sits in the dense order (0, 1, 2, …). Sequence is *when* a change happened (1, 2, 3, …). A move changes
positions and consumes one sequence. A no-op changes neither.

**Why does `QueueItemStateChanged` fire for the ended song, not the next one?** `AdvanceLocked`
returns the previous current entry after marking it `Ended`; `RunPlaybackCommandAsync` broadcasts that
returned id. Clients currently infer the new `Playing` row from the `QueueChanged` payload. Review
minor #1 tracks sending both ids.

**Why is the QR sometimes unreachable from a phone?** The QR encodes an absolute URL built from
`PublicBaseUrl` if set, otherwise the request's scheme and host. Behind a reverse proxy, the request
host may be an internal name. Set `PublicBaseUrl` (or wire forwarded headers) for deployments.

**Why is persistence "write-only"?** Every mutation is written, but startup never reloads rooms into
`RoomRegistry`. The database is a cold-restart *target*, not yet a cold-restart *source*. Rehydration
needs the queue, sequence, and playback anchors, which is why M4 is paired with Slice 4's authority.

**Why does `POST /api/rooms` return `200` instead of `201 Created`?** The endpoint uses `Results.Ok`
with a `{ code, roomId }` body. It works, but it does not advertise the new resource's `Location`
header. The review records this as a nit; changing it is a small future cleanup. Separately, the
endpoint catches *any* `InvalidOperationException` as "code space exhausted" (minor #10), which can
hide a different bug.

**Why do the Playwright specs pass without Postgres?** The room spec uses the filesystem fallback and
the deterministic demo `enqueueId`s. The stage hook treats an unreachable API as `offline` and renders
nothing, so the Slice 2 local stage is unchanged.

---

## 11. Glossary of Slice 3 terms

| Term | Meaning |
|---|---|
| **ADR** | Architecture Decision Record: a short, numbered note capturing a decision and its consequences. |
| **Aspire / AppHost** | .NET's local orchestrator for multi-service development; here it starts PostgreSQL and the API. |
| **Command lock** | The per-room `SemaphoreSlim` that serializes whole queue/playback commands. |
| **Cursor** | The playing row plus the ended history before it; only rows after it may be reordered. |
| **DbContext** | EF Core's unit of database work, tracking entities and saving changes. |
| **Delta** | An incremental update (`Added`, `Removed`, `Updated`) carrying a sequence number. |
| **Deterministic GUID / UUIDv5** | A GUID derived from a namespace and a name via SHA-1, so the same input always yields the same id. |
| **DI lifetime** | How long a container-created instance lives: singleton (process), scoped (request), transient (every resolution). |
| **EF Core** | Entity Framework Core, Microsoft's ORM for .NET. |
| **Group (SignalR)** | A named set of connections; Karaokay uses the room code. |
| **Hub** | A SignalR server endpoint clients invoke methods on. |
| **Idempotency** | Repeating an operation has the same effect as doing it once. |
| **Migration** | A checked-in schema change applied by EF Core at startup or via `dotnet ef`. |
| **Monotonic** | Only increasing; the queue sequence never goes backward. |
| **Npgsql** | The .NET driver/EF provider for PostgreSQL. |
| **ORM** | Object-Relational Mapper: maps rows to objects. |
| **Per-invocation lifetime** | SignalR creates a new hub object for each method call. |
| **Persistent connection** | A long-lived connection carrying many messages, unlike one request/response. |
| **QR join** | A QR-encoded absolute URL that opens the phone remote with a room code. |
| **Scoped** | One instance per request or hub invocation scope. |
| **Seeding** | Inserting starter rows (here, demo songs) into a fresh database. |
| **Sequence number** | A per-room counter attached to queue deltas; clients require contiguous values. |
| **Singleton** | One instance per process. |
| **Testcontainers** | A library that starts real Docker containers from tests. |
| **Typed hub** | `Hub<TClient>` where server-to-client calls are compile-checked against an interface. |
| **Unique index** | A database constraint rejecting duplicate values, used as the room-code backstop. |
| **WebSocket** | A browser protocol upgrading HTTP to a two-way byte stream; SignalR's preferred transport. |
| **Wire enum** | A BCL-only enum copy in Contracts that mirrors a Domain enum numerically. |

---

## 12. Further reading

- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) — the blueprint. Start with §4.3 (SignalR contract),
  §5.1–§5.2 (state machine and reconnection), §4.2 (entities), and §7's Slice 3 definition.
- [`../PROJECT_STATE.md`](../PROJECT_STATE.md) — the living snapshot, including what exists today and
  what Slice 4 inherits.
- [`../review/slice-3-review.md`](../review/slice-3-review.md) — the full review: verdicts, M1, the
  M2–M5 schedule, all minors, and the governance gap.
- [`slice-1-walkthrough.md`](slice-1-walkthrough.md) and
  [`slice-2-walkthrough.md`](slice-2-walkthrough.md) — how the content pipeline and browser stage got
  here.
- [`../../src/apps/Karaokay.Api/Hubs/StageHub.cs`](../../src/apps/Karaokay.Api/Hubs/StageHub.cs) — the
  real-time entry point.
- [`../../src/apps/Karaokay.Api/Features/QueueService.cs`](../../src/apps/Karaokay.Api/Features/QueueService.cs)
  and [`QueueOrdering.cs`](../../src/apps/Karaokay.Api/Features/QueueOrdering.cs) — the queue engine and
  the M1 rules.
- [`../../src/clients/web/src/lib/signalr/stageClient.ts`](../../src/clients/web/src/lib/signalr/stageClient.ts)
  and [`roomState.ts`](../../src/clients/web/src/lib/signalr/roomState.ts) — the client half of the
  protocol.
- [`../../src/tests/Karaokay.UnitTests/QueueServiceTests.cs`](../../src/tests/Karaokay.UnitTests/QueueServiceTests.cs)
  and [`../../src/tests/Karaokay.IntegrationTests/RoomHubIntegrationTests.cs`](../../src/tests/Karaokay.IntegrationTests/RoomHubIntegrationTests.cs)
  — the regressions that pin the fix.

A good next afternoon: run the AppHost and the web client, open `/stage`, join from a phone-sized
window, and queue two songs. Then open `StageHub.JoinRoom`, `QueueService.MoveQueueItemAsync`, and
`queueOrdering.ts` side by side. That path touches the hub lifetime, the singleton registry, the
command lock, the sequence, the delta reducer, and the UI gate — most of Slice 3 in one read.
