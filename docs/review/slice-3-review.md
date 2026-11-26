# Slice 3 Review — SignalR Room Hub, Queue Engine & Mobile Guest Remote

- **Date:** 2026-11-25
- **Scope:** Slice 3 diff in commit `2d391de` (typed `StageHub`, room registry and code generation,
  EF Core persistence + migrations, queue service and sequence-numbered deltas, stage/phone remote
  UI, QR join flow, Testcontainers hub tests), reviewed against
  [ARCHITECTURE.md](../ARCHITECTURE.md) v1.2, [ADR-001](../adr/001-single-replica-signalr-azure.md),
  and [ADR-005](../adr/005-slice-1-blueprint-reconciliations.md)
- **Follow-up:** correctness major **M1** is fixed in this change; majors M2–M5 are scheduled work
  and are **not** fixed here. The fix, its tests, and this review record are committed together
  with this document.

## a) Verdicts

**Pass 1 — static audit — SHIP-WITH-NITS, conditional on M1.** The slice keeps the documented
boundaries: the hub is the only client entry point, every queue mutation is serialized per room and
persisted before broadcast, rejoin is a full snapshot with no delta replay, and stage-only methods
are role-gated. The design debt is concentrated rather than structural: the stage role is
self-declared, rooms are never collected, persistence is write-only, and phone playback controls
mutate state the stage does not render yet. One correctness major (M1) was reachable from the phone
UI and had to be fixed before the slice could be called done.

**Pass 2 — execution matrix on commit `2d391de` — SHIP-WITH-NITS / proceed to Slice 4
conditional on M1:**

- `dotnet build karaokay.sln --configuration Release` — 0 warnings, 0 errors.
- `dotnet test karaokay.sln --configuration Release --no-build` — **78 passed / 6 skipped**
  (11 architecture + 38 unit + 29 integration passed; 6 Testcontainers hub tests skipped because
  Docker was unavailable locally, so CI must run that suite).
- Vitest — **60/60 passed**.
- Playwright — **8/8 passed**; one cold-start flake in `audio-gate.spec.ts` hit the 1.5 s decode
  timeout and passed on rerun (recommend raising the timeout to ~5 s, nit 11 below).
- Generated artifacts — `openapi.json`, `schema.d.ts`, and the three `stage-clock/*.json` fixtures
  are byte-stable.
- Slice 2 — no regressions; the smoke/audio-gate/canvas/gain specs all stayed green, and the new
  rooms spec passes.

**After the M1 fix (this change):** Release build 0 warnings; **86 .NET tests passed /
9 skipped** (8 new queue-service unit tests, 3 new hub integration tests which skip without
Docker); Vitest **64/64**; Playwright **8/8**.

## b) M1 — queue reorder could skip songs or end the room early (fixed)

Before the fix, `MoveQueueItemAsync` accepted any row and `RenumberLocked` rewrote every position,
while `AdvanceLocked` selected the next row only by `Position > current.Position`. Two reachable
failures followed:

- `A(playing, 0), B(1), C(2)`; a guest moves `C` to the top → `C(0), A(1), B(2)`. When `A` ends,
  only positions greater than 1 are considered, so `B` plays and `C` is skipped.
- A guest moves `A` itself down → every remaining row sits before the cursor and the room goes
  `Idle` with songs still queued.

`QueueList` enabled the move buttons for `Playing` and `Ended` rows, so both were reachable from a
phone.

**Fix chosen — forward-only cursor over the existing dense positions (review option b), with a
defensive advance fallback.** Rejected: migrating to a new ordering token, because positions are
persisted in `QueueItem.Position` and exposed in `QueueItemDto.Position`; changing both would touch
migrations, wire contracts, tests, and UI for a rule that fits the current model.

- `QueueOrdering.Move` (`src/apps/Karaokay.Api/Features/QueueOrdering.cs`) allows only upcoming rows
  to move. `Playing`/`Ended` rows return `NotMovable`; a target above the playing row is clamped to
  the first upcoming slot, so "put it on top" means "play next", not "strand it behind the cursor".
  Positions stay dense and the wire shape is unchanged.
- `QueueOrdering.SelectNext` advances to the first playable row after the cursor and falls back to
  the first playable row before it. The fallback keeps the room from going `Idle` while a queued
  song exists (also covering an item that finished downloading behind the cursor).
- `StageHub.MoveQueueItem` maps the new `QueueMoveResult` to `HubError(NotFound)` or
  `HubError(InvalidRequest)` ("Only upcoming songs can be reordered").
- `QueueList` disables move buttons for `Playing`/`Ended` rows and for the first movable row's
  up-button, so the phone cannot request a rejected move.

Tests added: `QueueServiceTests` covers "move a future item above the current one, then advance"
(the moved item plays next), "moving the playing item is rejected and advance order is unchanged",
"moving ended history is rejected", "advance plays an earlier ready item instead of going idle",
plus Start / Pause / Resume / Skip / no-next-Idle transitions. `RoomHubIntegrationTests` repeats
the two reorder regressions and the playback lifecycle over the real hub + PostgreSQL (skipped
without Docker). Vitest covers the UI gating helper.

## c) Scheduled majors — not fixed in this change

| # | Finding | Owner / timing | Suggested direction |
|---:|---|---|---|
| M2 | **Stage role is client-self-declared.** `JoinRequest.Role` is trusted by `StageHub.RequireStageAsync`; any guest who knows the code can join as `"stage"` and invoke `ReportSongEnded`/`UpdateStageCapabilities`. | Fix before the Slice 6a public demo. | Bind the stage to a server-issued secret (room-creation response or one-time stage claim) or enforce one stage per room; record the choice as an ADR. |
| M3 | **Unbounded room creation.** Every `/stage` page load creates a room; there is no GC, close path, rate limit, or cap, and the finite 279,841-code space is consumed permanently. | Schedule with room-GC work (blueprint placed GC in Slice 8); must land before Slice 6a. | Add idle expiry/close + registry sweep, a per-source creation rate limit, and a documented capacity alarm. |
| M4 | **Write-only persistence.** Rooms/queue are written to Postgres but never rehydrated on a cold restart, contradicting ADR-001's intent that the DB is the cold-restart fallback. | Assign explicitly to Slice 4's `PlaybackAuthority` or schedule before Slice 6a. | Load rooms on startup (or lazily on first join) and rebuild `RoomState`, including sequence numbers and playback anchors. |
| M5 | **Phone playback controls mutate room playback the stage never renders.** The stage still plays its local `DemoTrackPicker`; pressing Start/Pause/Skip on a phone changes `RoomState` but not stage audio. | Hide controls behind a flag or wire the minimum stage playback path in Slice 4. | Slice 4's authority/controller is the natural owner; until then, label or disable the controls to avoid a demo that appears broken. |

## d) Minor backlog

Ordered by rough priority; each item includes the relevant file and a one-line suggested fix.

| # | Finding | File | Suggested fix |
|---:|---|---|---|
| 1 | `QueueItemStateChanged` is not emitted for the **new** playing item after an advance; only the ended item is broadcast. | `src/apps/Karaokay.Api/Features/QueueService.cs` (`RunPlaybackCommandAsync`/`AdvanceUnderLockAsync`) | Broadcast the state change for the next item (or send both ids) so clients need not infer it from `QueueChanged`. |
| 2 | Move/remove have no ownership check despite `SessionTokenId` being persisted. | `QueueService.cs`; `Hubs/StageHub.cs` | Decide guest-vs-stage rights and enforce them (`HubError(ForbiddenRole)`/`NotFound`); keep the soft token for reconnect identity. |
| 3 | No auto-rejoin after page reload; the session token and room code are not persisted client-side. | `src/clients/web/src/features/remote/useStageClient.ts` | Store `{roomCode, sessionToken, displayName}` in `sessionStorage` and rejoin on mount. |
| 4 | Snapshot/delta join race can force needless full resyncs: a delta may arrive after the snapshot invocation but before its sequence is applied. | `useStageClient.ts`; `RoomHubProtocol` | Buffer deltas until the snapshot is applied, or attach the applied sequence to the snapshot ack. |
| 5 | Rejoining broadcasts a duplicate `ParticipantJoined` with no preceding `ParticipantLeft`. | `Hubs/StageHub.cs` (`JoinRoom`) | Treat same-connection rejoin as a no-op presence change, or emit the corresponding leave first. |
| 6 | `/healthz` changed from JSON `{"status":"ok"}` to `MapHealthChecks` plain text. | `src/apps/Karaokay.Api/Program.cs` | Keep the plain-text probe or add a JSON writer; update PROJECT_STATE/README and any consumer. |
| 7 | `DemoSongSeeder` marks every scanned song `IsSyntheticDemo = true` unconditionally. | `src/apps/Karaokay.Api/Features/DemoSongSeeder.cs` | Detect the synthetic demo folders (or seed only `public/demo-tracks`) instead of claiming every library row is demo content. |
| 8 | No DB cleanup when a media folder disappears; startup runs N per-song queries. | `DemoSongSeeder.cs` | Reconcile in one query and delete/mark orphaned rows; batch the seed lookups. |
| 9 | A transient DB failure leaves in-memory state divergent from persisted state; there is no compensation or retry marker. | `QueueService.cs` (`PersistQueueAsync`/`PersistRoomAsync`) | Log and mark the room dirty, retry the write, or fail the command before mutating memory (write-ahead ordering is currently memory-first). |
| 10 | `POST /api/rooms` maps **any** `InvalidOperationException` to `503` "code space exhausted". | `src/apps/Karaokay.Api/Endpoints/RoomsEndpoints.cs` | Throw/catch a dedicated code-space-exhausted exception; let other failures surface as 500. |
| 11 | Every mutation persists the whole queue row set; there is no queue-length cap, so a public deployment has a cheap DoS vector. | `QueueService.cs`; endpoint/hub validation | Cap queue length per room and persist only changed rows (or one round-trip upsert). |
| 12 | `MapHealthChecks`/OpenAPI generation runs the app assembly at build time, coupling build to startup code. | `Program.cs`; `Karaokay.Api.csproj` | Keep build-time OpenAPI but ensure it is side-effect free, or generate from a design-time host. |
| 13 | YouTube stubs ack an empty list (and the enqueue path is disabled) with no disabled-state feedback in the UI. | `StageHub.SearchYouTube`; `LibraryTab`/`YouTubeTab` | Return/display an explicit "YouTube ingestion is disabled" state so the tab is not mistaken for a broken search. |
| 14 | No Domain↔Contracts enum parity test despite the `WireEnums.cs:5` comment claiming one can exist. | `src/core/Karaokay.Contracts/Hubs/WireEnums.cs`; `SignalRContractTests` | Add a reflection test asserting identical names and numeric values for all four mirrored enums. |
| 15 | Rejoin generates a fresh `SessionToken` every time, so future identity/ownership features cannot survive a reconnect. | `Hubs/StageHub.cs` (`JoinRoom`) | Accept a prior token on join, validate it against the room, and rotate only when absent/invalid (ties into minor #2). |

## e) Nits

- Unused `RoomCommand` channel/type remains in Contracts as Slice 4 preparation; document or remove
  until the authority consumes it.
- `SignalRContractTests` matches method names by regex and `RoomCommand` property names only; it
  does not catch DTO property drift on the remaining records.
- `JoinScreen` silently strips invalid characters from the code input instead of rejecting them;
  show a validation message so a mistyped code does not look accepted.
- `QueueList` removes an item immediately with no confirmation; add a confirm step or undo.
- `Ended` history is rendered and numbered alongside upcoming rows with no visual distinction;
  dim/group history or collapse it.
- `/api/songs` is fetched separately by the stage and the phone; cache or share it per page.
- The stage stays `live` after the hub disconnect with no reconnect banner; surface connection state.
- The queue panel shows only the first four rows without disclosing that more exist.
- The QR join URL trusts the request `Host` header; deployments behind a reverse proxy need
  `ForwardedHeaders` or `PublicBaseUrl` set.
- `POST /api/rooms` returns `200` rather than `201 Created` + `Location`.
- `audio-gate.spec.ts` uses a 1.5 s decode timeout that flakes on cold start under 8 workers; raise
  it to ~5 s.

## f) Governance gap (ADR-005)

ADR-005 requires contract/count/enforcement changes to be recorded as a new ADR or a blueprint
version bump. Slice 3 added **no ADR** even though it introduced authoritative decisions not
described by v1.2: EF Core persistence and the demo-seed path, the in-memory room registry as the
hot path with the DB as fallback, and the hub's role/session model. The blueprint also remains
internally inconsistent: **§4.3's `JoinRequest` carries no token, while §5.2 says clients hold
`RoomCode + SessionToken` and rejoin with it.** Rejoin today works only as a new join that mints a
fresh token (minor #15), which is not what §5.2 describes.

This is flagged for a design decision rather than resolved here: write a Slice 3 reconciliation ADR
(or bump the blueprint and fix §5.2) before Slice 4 relies on reconnect identity. The M1 fix does
not change a documented contract — §5.1 already says advance pops the next ready item; the
implementation now follows it.

## g) Carry-forward

M2 and M3 must close before the Slice 6a public demo; M4 belongs with Slice 4's `PlaybackAuthority`
unless scheduled earlier; M5 is natural Slice 4 scope. The minor backlog should be folded into
Slice 4 planning rather than rediscovered, and the ADR/session-token question should be settled
before any identity-dependent feature is built on reconnect.
