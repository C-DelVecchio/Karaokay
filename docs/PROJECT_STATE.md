# Karaokay — Project State After Slice 3

This is a guided tour of **what Karaokay is, what exists today, how the pieces fit together, and how to run it**. It assumes you can read a file and follow a command, but not that you have met terms like *Minimal API*, *Web Audio*, *ETag*, or *vertical slice*. Terms are defined where they matter for understanding that section, and there is a [Glossary](#9-glossary) near the end.

- **Snapshot date:** 2026-11-25
- **Completed:** Slice 1, Slice 2, and Slice 3
- **Slice 3 verdict:** SHIP-WITH-NITS; M1 (queue reorder skipping songs) is fixed in this change,
  and majors M2–M5 are scheduled before the Slice 6 public demo
- **Next up:** Slice 4 — playback authority, clock sync, and browser video mode
- **Master blueprint:** [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) v1.2
- **Slice 1 beginner guide:** [`docs/learning/slice-1-walkthrough.md`](learning/slice-1-walkthrough.md)
- **Reviews:** [`slice-1`](review/slice-1-review.md) · [`slice-2`](review/slice-2-review.md) · [`slice-3`](review/slice-3-review.md)

---

## Table of Contents

1. [What is Karaokay?](#1-what-is-karaokay)
2. [Current state: what exists, what does not](#2-current-state-what-exists-what-does-not)
3. [Folder-by-folder tour of the repository root](#3-folder-by-folder-tour-of-the-repository-root)
4. [The source tree under `src/`](#4-the-source-tree-under-src)
5. [End-to-end data flow](#5-end-to-end-data-flow)
6. [How to build and run everything](#6-how-to-build-and-run-everything)
7. [Where the project goes next](#7-where-the-project-goes-next)
8. [Frequently confusing points](#8-frequently-confusing-points)
9. [Glossary](#9-glossary)
10. [Further reading](#10-further-reading)

---

## 1. What is Karaokay?

**Karaokay** is a self-hosted, multi-client party karaoke web app. A browser "stage" shows lyrics, a server delivers songs, and phones act as remotes. It turns MIDI charts plus separated audio tracks into playable songs and paints synchronized lyrics in the browser.

Three rules shape everything:

1. **$0/month, permanently.** Every tool, library, runner, and host must fit a free tier or a home server. No expiring trials, no paid APIs. The architecture document calls this constraint **C1**.
2. **No copyrighted media in Git.** A generator produces public-domain demo songs; an importer lets users process their own files locally. That is **C2**.
3. **Two clients, one contract.** The browser stage and the future Android TV stage must agree on lyric timing. They share a JSON timeline and a set of reference "decision" fixtures.

### What "vertical slice" means

A **vertical slice** is a small, complete feature that cuts through every layer — data, server, and UI — rather than finishing one layer at a time. Each slice ends with something you can run, demo, and test. The roadmap has eight:

| Slice | Theme | Status |
|---:|---|---|
| 1 | MIDI → `lyrics.json`, synthetic demos, importer CLI, room codes | ✅ Complete |
| 2 | HTTP media endpoints + browser stage with Web Audio and Canvas | ✅ Complete |
| 3 | SignalR rooms, queue, EF Core database, phone remote | ✅ Complete (SHIP-WITH-NITS; M1 fixed) |
| 4 | Playback authority, clock sync, browser video mode | ⏳ Next |
| 5 | Zero-key YouTube search and `yt-dlp` downloads | Planned |
| 6 | Azure free-tier deployment and CI/CD | Planned |
| 7 | Android C++ Oboe audio engine | Planned |
| 8 | Android TV SkiaSharp stage and hardening | Planned |

Slices 1–3 are **complete**. The unit and Vitest lanes pass locally; the Testcontainers integration
tests are exercised in CI, and skip locally when Docker is unavailable. The Slice 3 review left four
majors scheduled as later work (stage-role trust, room GC, cold-restart rehydration, and stage
playback wiring); none blocks Slice 4, but M2/M3 must close before the public demo.

### The blueprint and ADRs

[`docs/ARCHITECTURE.md`](ARCHITECTURE.md) (v1.2) is the master blueprint: goals, contracts, state machines, rendering math, and the full roadmap. If this page and the blueprint disagree, the blueprint wins and one of them should be fixed.

An **ADR** (Architecture Decision Record) is a short numbered note capturing one important decision: context, options, choice, consequences. ADRs are never rewritten; a later one may supersede an earlier one.

---

## 2. Current state: what exists, what does not

### 2.1 Slice 1 — content without copyright

Slice 1 answered: *where do songs come from before there is a database or a user?*

- **MIDI parser** (`MidiLyricsParser`) converts a Rock Band-style vocal chart into a `lyrics.json` timeline. A **MIDI file** is not audio; it is a list of events (note on/off, tempo, text). The parser turns those events into millisecond timings.
- **Synthetic generator** (`Karaokay.SyntheticGen`) composes three deterministic demo songs from a seed: MIDI, instrumental and vocal stems, and `lyrics.json`.
- **Importer CLI** (`Karaokay.Importer`) with `scan`, `import`, and `verify`, accepting extracted Rock Band folders (`song.ini`, `notes.mid`, `*.ogg` stems).
- **Room-code generator** (`RoomCodeGenerator`): four letters from a 23-letter alphabet, so **23⁴ = 279,841** codes. `I`, `O`, and `Q` are omitted because they are easy to misread.
- **45 .NET tests** at the end of the slice: 29 unit, 5 integration, 11 architecture.

The deep walkthrough of this slice is [`docs/learning/slice-1-walkthrough.md`](learning/slice-1-walkthrough.md).

### 2.2 Slice 2 — media over HTTP and a browser stage

Slice 2 answered: *how does a browser fetch a song and paint synchronized lyrics?*

- **Media endpoints** on the API: `GET /api/songs`, and `GET /api/songs/{songId}/{instrumental|vocals|lyrics}`. They support **HTTP range requests** and send **ETag** validators plus `Cache-Control: public, max-age=86400, immutable`.
- **Browser stage** in **React 19 + TypeScript**, built with **Vite**: a landing page at `/`, the stage at `/stage`, a hand-written pathname router, a phase machine (`picking → countdown → playing → ended`), a Web Audio engine, and a Canvas lyrics renderer.
- **Tests:** 40 **Vitest** unit tests in 7 files; 7 **Playwright** end-to-end tests in 4 specs; the .NET suite grew from 45 to **69** (34 unit, 24 integration, 11 architecture) after Slice 2's media endpoint tests. Numbers measured 2026-11-24.
- **CI on GitHub Actions** builds and tests both stacks, runs Playwright headless, and checks that generated golden fixtures have not drifted.

### 2.3 Slice 3 — rooms, a real queue, and phones

Slice 3 answered: *how does a party join one room and change a shared queue in real time?*

- **Real-time hub.** `StageHub` at `/hubs/stage` implements every method in the §4.3 contract:
  join, ping, enqueue (library and the disabled YouTube path), move, remove, start/pause/resume,
  skip, restart, report-ended, guide-vocal volume, and stage capabilities. Clients receive
  `JoinedRoom` snapshots, sequence-numbered `QueueChanged` deltas, `PlaybackChanged`,
  `QueueItemStateChanged`, and structured `HubError` messages.
- **Rooms.** `POST /api/rooms` reserves a four-letter code in the singleton `RoomRegistry` and
  writes a `PartyRoom` row; the database unique index is the collision backstop.
  `GET /api/rooms/{code}` returns a full snapshot and `GET /api/rooms/{code}/qr` renders the phone
  join URL as a PNG.
- **Persistence.** EF Core entities (`Song`, `PartyRoom`, `QueueItem`, `DownloadJob`) and Npgsql
  migrations. When a connection string is configured the API migrates and seeds the demo songs at
  startup; when it is absent, the Slice 2 filesystem scan remains a graceful fallback for tests and
  a bare `dotnet run`.
- **Queue engine.** `QueueService` serializes mutations per room with a command lock, persists
  before broadcasting, renumbers dense positions, and emits one monotonic sequence per change.
  Playback is a deliberately small subset of §5.1: start, pause, resume, skip, restart, and an
  idempotent stage end report. Reordering is forward-only — the playing song and ended history are
  the cursor and cannot be moved.
- **Phone remote.** `/remote?code=XXXX` (also `/remote` with a manual code) has a join screen, a
  lobby with the room code, a library tab, a queue tab with reorder/remove, and playback controls.
  The stage at `/stage` now creates a room, shows its code and QR in a corner overlay, and renders
  the shared queue. Its audio still comes from the local `DemoTrackPicker`.
- **Tests.** The .NET suite had **84 tests** at the reviewed commit: 11 architecture, 38 unit, and
  35 integration (29 passed + 6 Testcontainers tests skipped without Docker), for 78 passed / 6
  skipped. The M1 fix adds 8 unit and 3 integration tests, so the current tree runs **95 tests**
  (86 passed / 9 skipped locally). Vitest is at **64 tests** (60 at review, plus 4 UI-gating tests)
  and Playwright at **8**. Numbers measured 2026-11-25.

The Slice 3 review is [`docs/review/slice-3-review.md`](review/slice-3-review.md). Its M1 finding —
a guest reorder could skip a song or end the room early — is fixed in this change; M2–M5 are
recorded there as scheduled work.

### 2.4 What is NOT built yet

Do not look for these:

- **Authoritative playback and clock sync.** The room has a small playback subset, but there is no
  single-writer `PlaybackAuthority`, no watchdog, and no `Ping/Pong` clock offset filter yet. The
  stage still plays local demo audio.
- **Stage playback controlled by phones.** Phone Start/Pause/Skip changes room state; the stage does
  not render it yet (review M5).
- **Room lifecycle.** Rooms are never closed or garbage-collected, and cold restarts do not
  rehydrate them from Postgres (review M3/M4).
- **Stage authentication.** The stage role is self-declared in `JoinRequest` (review M2).
- **YouTube ingestion.** No `yt-dlp` search or download worker; the hub stubs the methods.
- **Azure deployment.** No Bicep files, deploy workflow, or live URL.
- **Android anything.** No `net10.0-android` app, C++ Oboe engine, or SkiaSharp renderer.
- **A browser API client for stage audio.** The phone remote calls the API and hub, and the stage
  uses the hub, but stage audio still loads `public/demo-tracks/` as static files.

`src/clients/web/src/features/remote/` is no longer a placeholder: it is the phone remote.

---

## 3. Folder-by-folder tour of the repository root

Generated folders (`bin/`, `obj/`, `node_modules/`, `dist/`) are omitted:

```text
karaokay/
├── .editorconfig / .gitignore
├── .github/
│   ├── scripts/check-binary-media.sh
│   └── workflows/ci.yml
├── Directory.Build.props
├── Directory.Packages.props
├── karaokay.sln
├── opencode.json
├── docs/
│   ├── ARCHITECTURE.md
│   ├── PROJECT_STATE.md      ← this file
│   ├── adr/
│   ├── learning/slice-1-walkthrough.md
│   └── review/{slice-1,slice-2,slice-3}-review.md
└── src/
    ├── apps/  core/  tools/  clients/web/  tests/
```

### 3.1 `.github/`

- **`.github/workflows/ci.yml`** is the **CI** (Continuous Integration) recipe GitHub runs on every push and pull request to `main`: install FFmpeg and .NET, build, run .NET tests, install Node, run Vitest, build the web app, install Chromium, run Playwright, regenerate golden fixtures, and fail if they changed.
- **`.github/scripts/check-binary-media.sh`** enforces C2. It inspects tracked files only (`git ls-files`) and their committed sizes (`git cat-file -s`). Any audio, video, or image file larger than **100 KiB** outside `public/demo-tracks/` fails CI. The demo tracks are allowed because they are generated public-domain content.

### 3.2 `.editorconfig`

**EditorConfig** tells editors how to format files: UTF-8, LF line endings, final newline, trimmed trailing whitespace, 4-space C# and 2-space JSON/YAML/project files. These are style suggestions, not build failures: `EnforceCodeStyleInBuild` is off, so only compiler/analyzer settings in `Directory.Build.props` gate CI.

### 3.3 `.gitignore`

The standard .NET ignore list plus `node_modules/`, `dist/`, `playwright-report/`, `test-results/`, `media/` (local libraries), and `*.mp4`/`*.mkv`/`*.webm` as a second guard against committing downloads.

### 3.4 `Directory.Build.props` — one build configuration for all projects

**MSBuild** is the build engine behind `dotnet build`. A `Directory.Build.props` file is imported automatically by every project beneath it, so shared settings live in one place.

| Setting | Meaning |
|---|---|
| `TargetFramework=net10.0` | Compile against **.NET 10**; a target framework (TFM) is the version/flavour of .NET a project runs on. |
| `LangVersion=latest` | Use the newest C# features — for example collection expressions, introduced in C# 12 — compiled as C# 14 because `latest` on .NET 10 selects it. |
| `Nullable=enable` | Nullable reference types on; the compiler warns when something may be `null`, making "absent" explicit. |
| `ImplicitUsings=enable` | Common `using` lines are added automatically. |
| `AnalysisLevel=latest-recommended` | Enable the recommended code-quality analyzer rules (`CAxxxx`). |
| `TreatWarningsAsErrors=true` | Any warning fails the build; green means zero warnings. |
| `InvariantGlobalization=true` | Skip culture-specific data (ICU/NLS) so casing, sorting, and number formatting do not depend on the machine's locale. Essential for byte-exact fixtures. |

Invariant globalization is a one-way door: a future translated UI would need to override it.

### 3.5 `Directory.Packages.props` — central package management

**Central Package Management** (CPM) declares every NuGet version once here, and projects reference packages without a version (`<PackageReference Include="CliWrap" />`). **NuGet** is the .NET package manager; a package is a downloadable library. Why CPM? "Which version are we on?" becomes a one-file question, and two projects cannot silently use different versions. Pinned here: xUnit, the test SDK, `Microsoft.AspNetCore.Mvc.Testing`, `Melanchall.DryWetMidi` (MIDI), and `CliWrap` (running external processes). There is no lock file; exact pins are the reproducibility mechanism.

### 3.6 `karaokay.sln` — the solution file

A **solution file** groups related projects so tools can build and test them together. It is a manifest, not code. This one contains twelve projects: apps, core libraries, tools, and tests. A **project** (`.csproj`) is one compilable unit producing a `.dll` or an executable. `dotnet build` at the root builds the solution; `dotnet test` discovers the three test projects.

### 3.7 `opencode.json` — AI agent routing

This project was partly built with an AI coding assistant, **OpenCode**. This file routes work: `orchestrator` plans and verifies but cannot edit files or run arbitrary shell commands; `coder` implements .NET code; `native-coder` handles Android/C++; `reviewer` audits and runs builds; `explore` searches. It does not affect the app at runtime, but it explains why some docs mention agent roles.

### 3.8 `docs/` — the written memory of the project

- **`ARCHITECTURE.md`** — the master blueprint: constraints, system diagram, folder layout, the `lyrics.json` contract, EF Core entities, SignalR contracts, the room state machine, sweep-fill math, the Android audio plan, and all eight slices with Definition-of-Done prompts.
- **`adr/`** — six Architecture Decision Records:

| ADR | Decision in one line |
|---|---|
| [001](adr/001-single-replica-signalr-azure.md) | Run SignalR on one scale-to-zero Azure Container App replica with sticky sessions, avoiding paid SignalR/Redis. |
| [002](adr/002-android-oboe-hdmi-usb-drift.md) | Build a full-duplex Oboe engine for USB mic → HDMI, with manual clock-drift compensation. |
| [003](adr/003-dual-mode-stage-web-vs-native.md) | Ship web and native stages that share one decision-level renderer contract. |
| [004](adr/004-dual-source-media-pipeline.md) | Support Rock Band multitracks and YouTube downloads through one queue/stage, with cloud ingestion disabled. |
| [005](adr/005-slice-1-blueprint-reconciliations.md) | Correct the blueprint after Slice 1: 23-letter codes / 279,841 combinations, reflection-based rules, code-level validation. |
| [006](adr/006-line-wrapping-ownership.md) | The renderer owns glyph-measured wrapping; the importer stores only explicit line breaks. |

- **`learning/slice-1-walkthrough.md`** — a long beginner guide to Slice 1: MIDI, tick math, lyric encoding, FFmpeg mixing, LUFS, and the tests.
- **`review/slice-1-review.md`**, **`review/slice-2-review.md`**, **`review/slice-3-review.md`** — internal reviews with verdicts, fixed defects, and carry-forward backlogs. The Slice 3 record is the current planning input: it fixes M1 and schedules M2–M5.

---

## 4. The source tree under `src/`

```text
src/
├── apps/
│   ├── Karaokay.Api/            ← ASP.NET Core HTTP server + SignalR hub
│   ├── Karaokay.AppHost/        ← .NET Aspire orchestrator (API + PostgreSQL)
│   └── Karaokay.ServiceDefaults/← telemetry, health checks, resilience
├── core/
│   ├── Karaokay.Contracts/      ← wire shapes + lyrics.json serializer
│   ├── Karaokay.Domain/         ← entities and enums (PartyRoom, QueueItem, Song)
│   ├── Karaokay.Application/    ← use cases (room codes)
│   └── Karaokay.Infrastructure/ ← adapters (MIDI, FFmpeg, EF Core, file names)
├── tools/
│   ├── Karaokay.Importer/       ← CLI for real song folders
│   └── Karaokay.SyntheticGen/   ← deterministic demo generator
├── clients/web/                 ← React 19 + TypeScript stage and phone remote
└── tests/
    ├── Karaokay.UnitTests/
    ├── Karaokay.IntegrationTests/
    └── Karaokay.ArchitectureTests/
```

### 4.1 `src/apps/Karaokay.Api` — the HTTP and SignalR server

**ASP.NET Core** is Microsoft's web framework for .NET; it handles sockets, routing, dependency injection, logging, and HTTP. This project writes only the interesting parts.

#### `Program.cs`

The entry point wires the slice together:

1. Register the media library, song catalog, room registry, room service, queue service, SignalR,
   OpenAPI, CORS, and — when `ConnectionStrings:Karaokay` exists — EF Core with Npgsql.
2. Map `/healthz` through `MapHealthChecks` (plain-text probe output; the Slice 2 JSON body was
   replaced in Slice 3 — a review nit), `/openapi`, the song/media/room endpoints, and
   `/hubs/stage`.
3. When a database is configured, apply migrations and seed the demo songs before serving.

**Dependency injection** (DI) means a class asks for what it needs as constructor parameters; a container builds and shares instances. `AddSingleton` means one shared instance per process, which suits the room registry, queue service, and library scan cache.

A **Minimal API** is ASP.NET Core's low-ceremony style: lambdas mapped directly to routes instead of controller classes. The trailing `public partial class Program { }` lets integration tests boot the real host with `WebApplicationFactory<Program>`.

#### `Endpoints/SongsEndpoints.cs`

`GET /api/songs` asks DI for `SongCatalog`, which reads the `Songs` table when a database is configured and falls back to the Slice 2 filesystem scan otherwise. Items are `SongSummaryDto` records from `Karaokay.Contracts`: `id`, `title`, `artist`, `durationMs`, `source`, `hasGuideVocals`, and `enqueueId`. The same wire shape survived the move from folder scan to table.

#### `Endpoints/MediaEndpoints.cs`

Three routes under `/api/songs/{songId}` serve `instrumental`, `vocals`, and `lyrics` via `Results.File(..., enableRangeProcessing: true)`.

A **range request** asks for part of a file: the client sends `Range: bytes=1048576-` and a range-capable server replies `206 Partial Content` with just those bytes plus `Accept-Ranges: bytes`. A music player needs this to start before the whole file arrives, to seek without re-downloading, and to resume after a dropped connection.

The endpoint sets `Cache-Control: public, max-age=86400, immutable` (the bytes for a song id never change, so a browser may keep them for a day), `Accept-Ranges: bytes`, and a strong **ETag** built from file length plus last-write time.

An **ETag** (entity tag) is an opaque fingerprint for one version of a resource. Clients return it in `If-None-Match`; a match can yield `304 Not Modified` with no body. "Strong" means the bytes are guaranteed identical, not merely equivalent. Unknown ids and missing files return structured 404 `ProblemDetails`.

#### `Features/MediaLibrary.cs`

A "song" is an immediate subdirectory containing `instrumental.opus` and a `lyrics.json` that both deserializes and passes `LyricsJson.Validate`. `vocals.opus` is optional and sets `hasGuideVocals`. Invalid folders are skipped with a warning instead of crashing the API.

Two details matter:

- **One-second scan cache.** Range reads arrive in bursts; re-parsing every `lyrics.json` per chunk would be wasteful. A 1,000 ms snapshot keeps reads cheap while noticing edits within a second.
- **Song-id whitelist and path-traversal protection.** The URL's `{songId}` is never concatenated into a path. The library records directory names as ids and looks the input up with an exact ordinal comparison; unknown ids are 404s.

**Path traversal** is an attack where input like `../../etc/passwd` escapes an intended folder. If code did `Path.Combine(root, songId)`, that input could read files outside the library. The whitelist makes traversal impossible by construction.

#### `Features/MediaLibraryOptions.cs`

One property, `MediaRoot`. `appsettings.json` uses `media/library`; `appsettings.Development.json` points at `../../clients/web/public/demo-tracks`, so Development serves the demo songs with no copying.

#### `Endpoints/RoomsEndpoints.cs`

`POST /api/rooms` reserves a code and persists a `PartyRoom`; `GET /api/rooms/{code}` returns the
same `RoomSnapshot` shape the hub sends; `GET /api/rooms/{code}/qr` renders the phone join URL as a
PNG with `QRCoder`. The QR URL prefers a configured `PublicBaseUrl` and otherwise trusts the
request scheme/host — a known reverse-proxy caveat recorded in the review.

#### `Hubs/StageHub.cs`

`StageHub` is a **typed hub**: `Hub<IStageHubClient>` implements `IStageHub`, so client and server
method names are compile-checked C# instead of strings. A hub instance exists only per invocation,
so connection→room membership lives in the singleton `RoomRegistry`. `JoinRoom` always returns a
fresh session token and sends a full `JoinedRoom` snapshot; `OnDisconnectedAsync` broadcasts
`ParticipantLeft`. `RequireRoomAsync` rejects commands before a join, and `RequireStageAsync` gates
`ReportSongEnded`/`UpdateStageCapabilities` on `Role == "stage"`.

That stage gate is only as strong as the role the client claims in `JoinRequest` (review M2, to be
fixed before the public demo). `ReportSongEnded` is idempotent: anything but the current queue item
is ignored, so a duplicate stage report cannot double-advance.

#### `Features/RoomRegistry.cs` and `RoomService.cs`

`RoomState` is the hot in-memory room: a command semaphore (single-writer shape), a sync root for
snapshot reads, playback fields, participants by connection, and a monotonic queue sequence.
`RoomRegistry` holds live rooms and connection memberships in concurrent dictionaries.
`RoomService.CreateRoomAsync` retries code generation against both the registry and the database
unique index, releasing the in-memory reservation if persistence collides.

#### `Features/QueueService.cs` and `QueueOrdering.cs`

Every queue mutation takes the room command lock, mutates memory, persists via a fresh EF Core
scope, then broadcasts a `QueueChanged` delta with the next sequence. Tracked properties are dense
positions plus states; `RenumberLocked` keeps zero-based ordering after removes.

`QueueOrdering` holds the pure cursor rules fixed in the Slice 3 review: only upcoming rows may
move, a target above the playing row clamps to the first upcoming slot, and advance selects the
next playable row after the cursor with a fallback to a playable row behind it. This prevents a
guest reorder from skipping a song or forcing `Idle` while songs remain. The service also supports
start/pause/resume/skip/restart and an idempotent end report; the full `PlaybackAuthority`,
watchdog, and clock sync are Slice 4.

#### `Features/SongCatalog.cs` and EF Core persistence

`SongCatalog` is the read side used by both HTTP and the hub. With a connection string it queries
`KaraokayDbContext`; without one it maps deterministic demo GUIDs back to the filesystem scan.
Entities (`Song`, `PartyRoom`, `QueueItem`, `DownloadJob`) and Npgsql migrations live in
`Karaokay.Infrastructure/Persistence`. `DemoSongSeeder` inserts the scanned demo songs at startup
when the database is empty.

Persistence is currently write-only: the database is updated, but a cold restart does not rehydrate
rooms into `RoomRegistry` (review M4, assigned to Slice 4 or earlier).

#### `Karaokay.AppHost` and `Karaokay.ServiceDefaults`

`.NET Aspire` orchestrates multi-service local development. `AppHost` now starts a PostgreSQL
container with a persisted data volume and the API with a `Karaokay` connection string; the API
applies migrations and seeds demo songs on startup. `ServiceDefaults` supplies shared telemetry,
health checks (`/healthz`), and HTTP resilience. Both are real dependencies of the Slice 3
experience, not placeholders.

### 4.2 `src/core/` — the domain layers

These four projects follow **Clean Architecture**: business rules in the center, with outer layers (HTTP, files, FFmpeg) depending inward, never the reverse. The direction is enforced by tests, not just convention.

```text
Karaokay.Domain        (leaf: no project references)
Karaokay.Contracts     (leaf: wire shapes + lyrics serializer)
        ▲
Karaokay.Application ──▶ Domain
Karaokay.Infrastructure ──▶ Application, Contracts
```

#### `Karaokay.Contracts`

- **`LyricsSchema.cs`** — plain C# classes mirroring `lyrics.json`: document, tempo entries, vocal parts, phrases, lines, syllables, gaps. `[JsonPropertyName]` and `[JsonPropertyOrder]` pin names and order.
- **`LyricsJson.cs`** — the **single deterministic serializer**. Same document, same bytes: two-space indentation, LF newlines, UTF-8 without BOM, relaxed escaping, fixed property order. `Validate()` is a hand-written structural check returning one message per violation.
- **`SongDtos.cs`** — HTTP response shapes such as `SongSummaryDto`.
- **`Hubs/`** — the SignalR contract: `IStageHub` (client→server), `IStageHubClient`
  (server→client), shared DTO records (`RoomSnapshot`, `QueueDelta`, `PlaybackStateDto`,
  `HubErrorDto`), the `QueueItemState`/`PlaybackStatus` wire enums, and `RoomCommand` reserved for
  Slice 4's authority.

Why one serializer? The generator, importer, golden tests, and `verify` all compare bytes. Separate JSON options would create false failures and hide real ones.

#### `Karaokay.Domain`

The persistence entities and enums: `Song`, `PartyRoom`, `QueueItem`, `DownloadJob`, plus
`MediaSource`, `QueueItemState`, `PlaybackStatus`, and `DownloadStage`. The wire mirrors live in
Contracts because Contracts cannot reference Domain; a parity test is still missing (review minor
#14).

#### `Karaokay.Application`

`RoomCodeGenerator` uses `ABCDEFGHJKLMNPRSTUVWXYZ` (23 letters, no `I`/`O`/`Q`), builds four-character codes (`23⁴ = 279,841`), accepts an injectable `Random` for replayable tests, reserves each code with `HashSet.Add`, and retries at most 100 times before throwing. The database unique index on `PartyRoom.Code` is now the collision backstop used by `RoomService`.

#### `Karaokay.Infrastructure/` — adapters to real formats and a database

`Persistence/KaraokayDbContext.cs`, entity configurations, and the Npgsql migrations implement the
Slice 3 storage path. The provider-specific filtered unique index on `Song.VideoId` is codified in
the model; the DB is the cold-restart target even though rehydration is not wired yet (M4).

`MediaIngest/` contains the Slice 1 adapters:

- **`MidiLyricsParser.cs`** — finds `PART VOCALS`/`HARM1–3`, walks events once, follows the tempo map, groups phrases (explicit marker notes 105/106 first, 1,500 ms gap fallback second), splits lines, derives countdown gaps, and applies the global offset.
- **`MidiFileNameDiscovery.cs`** — checks `notes.mid`, `notes.midi`, `song.mid`, `song.midi`, `chart.mid` case-insensitively, which matters on Linux.
- **`SongIniReader.cs`** — forgiving metadata reader for `name`, `artist`, `delay`, `charter`, `song_id`: case-insensitive keys, comments, optional `[song]` header, unparseable values fall back to zero.
- **`StemMixer.cs`** — shells out to **FFmpeg** via `CliWrap`: selects instrumental stems (everything except `crowd*` and `vocals*`), mixes with `amix`, and normalizes to **−16 LUFS**. It selects `vocals*` stems or writes one second of silence when no guide vocal exists.

**LUFS** (Loudness Units relative to Full Scale) estimates *perceived* loudness, not peaks, using ITU-R BS.1770 / EBU R128. Targeting −16 LUFS makes a synthetic demo and a real recording feel equally loud beside a YouTube video. The mixer uses two-pass `loudnorm`: measure first, then apply one constant gain, which avoids the pumping of single-pass adaptive normalization.

### 4.3 `src/tools/` — two console applications

#### `Karaokay.Importer`

A CLI (command-line interface) with three commands:

```bash
dotnet run --project src/tools/Karaokay.Importer -- scan /path/to/library
dotnet run --project src/tools/Karaokay.Importer -- import /path/to/song --out /path/to/library
dotnet run --project src/tools/Karaokay.Importer -- verify /path/to/library/song-id
```

`scan` lists candidate folders. `import` parses the MIDI, reads `song.ini`, bakes stems, probes the encoded duration, writes `lyrics.json`, and prints sizes plus measured LUFS. `verify` checks the three files exist, that `lyrics.json` deserializes and validates, and that re-serializing reproduces the bytes exactly. Exit codes: **0** success, **1** failure, **2** usage error.

#### `Karaokay.SyntheticGen`

Generates three demo songs from a seed (default 42) into `demo-tracks` (default). Each song gets `song.mid`, `instrumental.opus`, `vocals.opus`, and `lyrics.json`. Timing, layout, and lyrics do not depend on the seed; only pitch and vocal detune do, using independent RNG streams per song. It prints each file's size and SHA-256, the quickest way to see whether two runs agree.

**Why synthetic data matters for a $0 project:** real karaoke recordings are copyrighted and cannot be committed, but CI, demos, and recruiters need something that plays. Synthesized public-domain songs are legally distributable, small, reproducible, and exercise the full pipeline. They also keep tests honest: a golden test can re-parse a committed MIDI rather than comparing hand-written JSON to hand-written JSON.

### 4.4 `src/clients/web` — the browser stage and phone remote

#### Tooling: `package.json`, Vite, TypeScript

- **`package.json`** lists dependencies and scripts. Runtime dependencies are React 19, React DOM, and the SignalR client (`@microsoft/signalr`); everything else is a dev dependency.
- **Vite** is the dev server and **bundler** — a tool that turns many TypeScript/CSS files into a few browser-ready files. Config: dev on port 5173, preview on 4173, Vitest tests in a Node environment because the renderer tests are pure math. `/api` and `/hubs` proxy to the API on port 5255.
- **TypeScript** is JavaScript plus static types. A **transpiler** strips types and modern syntax; Vite does this during dev and build. `tsconfig.json` enables `strict`, `noUnusedLocals`, and `noUnusedParameters`, so unused code fails.
- **Vitest** is the unit test runner for Vite projects.

There is deliberately **no `react-router`**. `src/App.tsx` has a hand-rolled pathname router: `resolveRoute()` normalizes trailing slashes and maps `/` to landing, `/stage` to the stage, `/remote` to the phone remote, and everything else to 404. It listens for `popstate` so the back button works. Three routes still do not justify a dependency.

#### `features/remote/` — the phone remote

`RemoteApp` owns the `useStageClient` hook: it builds the SignalR connection, joins with the room
code from `?code=`, applies the `RoomSnapshot`, then folds `QueueChanged` deltas by sequence and
rejoins for a full snapshot when a gap appears. `JoinScreen` collects code and display name;
`LobbyView` hosts the code, library, queue, and controls; `LibraryTab` enqueues library songs;
`QueueList` reorders/removes; `PlaybackControls` calls start/pause/resume/skip/restart.

`QueueList` uses `queueOrdering.ts` to disable move buttons for `Playing`/`Ended` rows and for the
first upcoming row's up-button. That mirrors the server rule (review M1): the playing song and
ended history are the cursor and cannot be reordered.

The room code is not persisted client-side yet, so a page reload requires joining again (review
minor #3). Playback controls change room state that the stage does not render yet (review M5).

#### `StageShell.tsx` — the phase machine

```text
picking ──pick──▶ countdown ──lead-in ends + audio ready──▶ playing ──source ends──▶ ended
   ▲                                                                                 │
   └──────────────────────────── pick another song ─────────────────────────────────┘
```

On a pick, `engine.loadSong()` fetches and decodes both stems while a countdown animation runs from the lead-in gap. `Promise.all` waits for both, then `engine.start(leadInOffsetMs)` starts both sources and seeks past the lead-in so audio and lyrics agree. When the source ends, the phase becomes `ended` and the picker returns.

Separately from audio, the stage now calls `POST /api/rooms` on load, shows the returned code and QR
in a corner overlay, connects to the hub as `"stage"`, and renders the shared queue. The stage's
local picker still drives audio; wiring that audio to room playback is Slice 4 (review M5).

**Why the gesture gate?** Browsers block audio until the user interacts, so a page cannot surprise a visitor with sound. An `AudioContext` created before a click starts suspended. The stage shows **"Click to Enable Stage Audio"**; only that click calls `engine.unlock()`. Nothing in the playback path — no source creation, decode, or media-element routing — happens before it. The Playwright audio-gate test asserts exactly that through `window.__karaokayAudioLog`.

#### `WebAudioEngine.ts` — two stems, three buses

The **Web Audio API** is the browser's audio graph: create nodes, connect them, and the browser mixes in real time.

```text
instrumental buffer ──▶ instrumental GainNode ──┐
                                                 ├──▶ master GainNode ──▶ speakers
vocals buffer ────────▶ guide GainNode ─────────┘
```

A **GainNode** is a volume control. Three buses allow independent instrumental, guide-vocal, and master volume. `setGuideVocalVolume()` ramps the guide bus over 45 ms, so there is no click or dropout and sources are never recreated.

`unlock()` is idempotent and must be called from a user gesture; on failure it rolls back so the next click retries. A **test clock** (`stageTestClock`) overrides `positionMs()`, letting Playwright pin an exact millisecond and screenshot the canvas deterministically. `positionMs()` otherwise reads `AudioContext.currentTime` — the audio hardware clock — because what the user hears is the ground truth.

Slice 2 does **not** use `createMediaElementSource` or `<audio>`. That is reserved for Slice 4 video mode, and tests assert it is never called.

#### `useStageClock.ts` — animation without React re-renders

**`requestAnimationFrame`** (rAF) calls a function before the next repaint, typically ~60 times per second, and pauses when the tab is hidden. The stage clock runs one rAF loop and notifies subscribers with the current audio position. It does **not** call `setState` per frame; that would re-render the whole component tree 60 times per second. The canvas subscribes directly and draws imperatively. React re-renders only when a phase or control changes. The loop starts with the first subscriber and stops with the last.

#### `renderer/CanvasLyricsRenderer.ts` — drawing without allocations

**Canvas rendering** means drawing pixels through a 2D context. The stage is 1280×720 logical pixels, scaled by device pixel ratio.

The rule: **`load()` may measure and allocate; `render(T)` may not.** `load()` measures every syllable once, caches widths, lays out rows, builds fill "units" (a syllable or a connected word group), and precomputes dot centers in `Float64Array`s. `render(T)` only reads numbers, mutates scratch fields, sets canvas state, and draws. No `measureText`, array copies, or object literals per frame — that is what keeps it at 60 fps.

The sweep works by drawing the row twice: the filled color inside a clip rectangle whose width is the fill progress, then the unfilled color over the whole row. Connected syllables fill as one word; **talky** (spoken) syllables switch instantly. The active row pulses on the current syllable, and the previous row fades over 300 ms.

#### `renderer/layout.ts` — glyph-measured line wrapping

`layoutLines()` takes lines plus a `measure(text)` function and produces rows up to a maximum width (default 30 em). An **em** is the current font size; at 48 px, 30 em is 1,440 px, which exceeds both the 1,280 px stage and its 1,120 px usable width after the 80 px side margins. The 30-em cap is therefore an upper bound rather than a target: in practice glyph-measured wrapping and the short generated lines keep rows inside the usable width. Spacing encodes pronunciation: no gap inside a `connectsToNext` chain, two character-widths after a chain, one between standalone words. Overflow flushes the row; `lineBreakAfter` forces a break.

This is ADR-006: the importer has no fonts and cannot measure glyphs, so the **renderer** owns wrapping. JSON stores only explicit semantic breaks.

#### `renderer/decisions.ts` — the pure layer and golden fixtures

This module has no DOM or React. Given `lyrics.json` and a time `T`, it computes the active line, the 0-to-1 fill of every syllable, and countdown-dot states.

It also builds a deterministic set of **100 sample timestamps** per song: an even grid snapped to nearby decision boundaries (syllable edges, gap edges, dot beats), deduplicated, then split until exactly 100 unique integer milliseconds remain.

A **golden fixture** is a file produced by the real implementation, committed, and compared exactly. "Golden" means it is the reference: intentional changes regenerate it and review the diff. Here `fixtures/golden/stage-clock/*.json` records those 100 decisions per song. It serves two purposes:

1. Vitest checks the current TypeScript decisions still match the committed file.
2. The future **Slice 8 SkiaSharp renderer** will be tested against the same JSON, proving *decision parity* (same active line, same fill values) without requiring pixel-identical output across two graphics stacks.

`scripts/generate-golden-fixtures.ts` regenerates them. CI runs it and then `git diff --exit-code`, so accidental drift fails the build.

#### `public/demo-tracks/` — the bundled songs

Three directories (`synth-countdown-canyon`, `synth-spoken-telegram`, `synth-twinkle-variations`), each with `instrumental.opus`, `vocals.opus`, `song.mid`, and `lyrics.json`. `DemoTrackPicker.tsx` fetches each `lyrics.json` on load and shows title, artist, and duration. Audio URLs are `/demo-tracks/<slug>/instrumental.opus` and `/demo-tracks/<slug>/vocals.opus`.

The three songs are a test harness, not a library: one has explicit phrase markers and tempo changes, one is entirely spoken-word, and one has long instrumental breaks with countdowns.

#### `scripts/` and `e2e/`

**`scripts/generate-golden-fixtures.ts`** regenerates the fixtures. Node 24 runs it directly because the decision module uses only erasable TypeScript syntax. **`scripts/run-e2e.mjs`** is a thin Playwright launcher: if fallback browser libraries were extracted to a known temp path, it prepends them to `LD_LIBRARY_PATH` for the child process; it is a no-op on healthy machines and CI, and passes arguments through (`npm run test:e2e -- --grep gate`).

**Playwright** drives a real Chromium browser. **End-to-end** (E2E) means the test starts at the UI and exercises the built app.

| Spec | What it proves |
|---|---|
| `smoke.spec.ts` | The landing page boots with the right title/heading/link; unknown routes show 404. |
| `audio-gate.spec.ts` | No playback path runs before the unlock click; decode happens during countdown and `start` only after the lead-in; the unlock survives across songs; media-element routing is never used. |
| `canvas-pixels.spec.ts` | At 0, 1,250, and 9,000 ms the canvas produces stable, distinct frames with visible glyph pixels, driven by the test clock. |
| `gain-slider.spec.ts` | Moving guide vocals to 0 and 1 changes the gain bus without recreating sources; crossfades are ≤ 50 ms. |
| `rooms.spec.ts` | Two browser contexts (stage + phone): the stage auto-creates a room and shows a valid code, the phone joins, enqueue/reorder/remove propagate to both, and a phone reload converges from a fresh snapshot. |

`playwright.config.ts` serves the built `dist/` on port 4173 and launches with `--mute-audio` and `--autoplay-policy=user-gesture-required`, so the gate is enforced even in tests.

### 4.5 `src/tests/` — the safety net

- **`Karaokay.UnitTests` (46 tests).** A **unit test** checks one small piece in isolation: a pure function, parser, or validator. The project uses **xUnit**, where `[Fact]` is one test and `[Theory]` runs one test with several data rows. Coverage includes tempo math, talky/connector handling, phrase grouping by markers versus gaps, countdown math, golden `lyrics.json` byte comparisons, room codes, generator determinism, SignalR method-name parity, and the Slice 3 queue-service rules. `QueueServiceTests` runs the real service against in-memory rooms with a hub recorder and no database: reorder clamping/rejection, advance ordering, and Start/Pause/Resume/Skip/Idle transitions.
- **`Karaokay.IntegrationTests` (38 tests, 9 skipped without Docker).** An **integration test** checks several real components working together, usually including I/O. The 7 media-endpoint tests boot the real API through `WebApplicationFactory`; they assert range responses (`206` with `Content-Range`/`Accept-Ranges`), ETag presence and strength, content types, 404 `ProblemDetails`, path-traversal 404s, invalid-folder skipping, and the library listing. The room-hub tests also boot the real host, but against a **Testcontainers PostgreSQL** container: room-code uniqueness, snapshot join/rejoin, monotonic deltas, reorder/advance regressions, the playback lifecycle, and stage-role rejection. `PostgresApiFixture` records a skip reason when Docker is unavailable, so laptops stay green while CI runs the real persistence path. The importer integration tests do not boot the API: they run against a temporary fake Rock Band folder with real FFmpeg-generated audio, assert stem selection and the filter graph, and check LUFS within ±1 LU. `FfmpegGuard` skips FFmpeg-dependent tests with a clear message when the tools are missing.
- **`Karaokay.ArchitectureTests` (11 tests).** These enforce layering. **Reflection** is .NET's ability to inspect assemblies and types at runtime. The tests use `Assembly.GetReferencedAssemblies()` to assert, for example, that `Contracts` never references Domain/Application/Infrastructure/DryWetMidi/CliWrap/EF Core, that `Application` never references `Infrastructure`, that CLI tools never reference the apps, and that `Domain` is a leaf. **Positive controls** assert the probe really sees a known reference, so a broken helper cannot make every rule pass vacuously. The source comment explains why reflection replaced `NetArchTest.Rules`, which misreported dependencies on .NET 10.

---

## 5. End-to-end data flow

### 5.1 Content pipeline (Slices 1–2)

```text
  SOURCE FILES                 TOOLS                        LIBRARY ON DISK
  song.ini  notes.mid  *.ogg
       │         │        │
       ▼         ▼        ▼
   SongIni    MidiLyrics  StemMixer
   Reader     Parser      (FFmpeg)
       │         │            │
       └────┬────┘            │
            ▼                 ▼
       lyrics.json    instrumental.opus
                      vocals.opus
            │                 │
            └────────┬────────┘
                     ▼
            <library>/<song-id>/
                     │
                     ▼
          ┌──────────────────────┐
          │ Karaokay.Api         │
          │ MediaLibrary /       │
          │ SongCatalog          │
          │ /api/songs           │
          │ /api/songs/{id}/...  │ ← range + ETag
          └──────────┬───────────┘
                     │ HTTP
                     ▼
          ┌──────────────────────┐
          │ Browser stage        │
          │ fetch stems + JSON   │
          │ Web Audio playback   │
          │ rAF clock → Canvas   │
          └──────────────────────┘
```

1. **Input.** Run `SyntheticGen` for three generated songs, or point the Importer at an extracted Rock Band folder you legally own.
2. **Parse.** `SongIniReader` reads metadata and the `delay` offset. `MidiLyricsParser` follows the tempo map and flattens phrases, lines, syllables, and gaps into milliseconds.
3. **Prepare audio.** `StemMixer` mixes non-vocal stems with `amix`, normalizes to −16 LUFS, encodes the vocal stem, and writes one second of silence if no vocal exists.
4. **Write the contract.** `LyricsJson.Serialize` writes deterministic JSON; `ffprobe` records the real encoded duration.
5. **Library layout.** One directory per song, named by a stable id, with exactly the expected files.
6. **Serve.** `SongCatalog` reads the songs table when a database is configured, or falls back to the `MediaLibrary` scan. `/api/songs` returns summaries; media routes return bytes with ranges, cache headers, and ETags.
7. **Play (Slice 2 path).** The stage fetches the same three files from its static `public/demo-tracks/` copies. `loadSong` fetches both stems, waits for unlock, decodes them, and starts two sources through the gain buses. `positionMs()` reads the audio clock.
8. **Draw.** `useStageClock` pushes positions over rAF. The renderer picks the active row, clips the filled color, draws the unfilled color, and paints countdown dots. No React re-render happens.

### 5.2 Party loop (Slice 3)

```text
  PHONE (/remote?code=ABCD)          API + HUB                     STAGE (/stage)
        │                               │                              │
        │ JoinRoom {code,name,role} ───▶ RoomRegistry                  │
        │◀──── JoinedRoom(snapshot) ────┤                              │
        │                               │◀──── JoinRoom {role:stage} ──┤
        │                               │──── JoinedRoom(snapshot) ───▶│
        │ EnqueueLibrarySong(songId) ──▶ QueueService                 │
        │                               │  lock → mutate → persist     │
        │                               │  EF Core ──▶ PostgreSQL      │
        │◀──── QueueChanged {seq} ──────┤──── QueueChanged ───────────▶│
        │ Start / Pause / Skip ────────▶│  playback subset (Slice 3)   │
        │◀──── PlaybackChanged ─────────┤──── PlaybackChanged ────────▶│
```

1. **Join.** A phone or stage calls `JoinRoom`; the hub binds the connection to a `RoomState` in the singleton registry, adds it to the SignalR group, and returns a full snapshot plus a session token. Rejoin uses the same path.
2. **Queue.** Enqueue/move/remove take the room command lock, mutate the in-memory queue, persist through EF Core (when configured), then broadcast a `QueueChanged` delta carrying the next monotonic sequence. A client that sees a gap re-joins for a snapshot rather than replaying deltas.
3. **Playback subset.** Start/Pause/Resume/Skip/Restart plus the idempotent `ReportSongEnded` update room state and broadcast `PlaybackChanged`; the full single-writer `PlaybackAuthority`, watchdog, and clock sync are Slice 4. Reordering is forward-only: `Playing`/`Ended` rows are the cursor and cannot be moved, which is the M1 fix from the Slice 3 review.
4. **Not connected yet.** The stage renders the room queue but still plays local demo audio; phone controls do not drive stage sound (review M5). Persistence is write-only until rooms are rehydrated (review M4).

---

## 6. How to build and run everything

### 6.1 Prerequisites

**.NET 10 SDK** (`dotnet --version` reports 10.x), **Node.js 24** with npm, and **FFmpeg/ffprobe** on `PATH` for import, mixing, and audio tests (`sudo apt-get install ffmpeg` or `brew install ffmpeg`). Optional: `npx playwright install chromium` (CI uses `--with-deps` for system libraries).

### 6.2 .NET commands

From the repository root:

```bash
dotnet build          # restore + compile all twelve projects; warnings are errors
dotnet test           # run unit, integration, and architecture tests (~95 tests)
dotnet run --project src/apps/Karaokay.Api     # Development profile: http://localhost:5255
dotnet run --project src/apps/Karaokay.AppHost # optional: API + PostgreSQL via Aspire
```

`dotnet build` reads `karaokay.sln`, restores NuGet packages, and compiles everything. Because warnings are errors, a green build is a strong signal. `dotnet test` builds if needed, then runs xUnit; FFmpeg-dependent integration tests are skipped by a guard if the tools are missing, and the Testcontainers PostgreSQL suite skips with an actionable message when Docker is unavailable. The current tree is **86 passed / 9 skipped** on a Docker-less machine; CI runs those nine against a real container.

The `AppHost` profile starts PostgreSQL with a persisted data volume and passes a `Karaokay`
connection string to the API, which applies migrations and seeds the demo songs. Without a
connection string the API runs the filesystem fallback used by bare `dotnet run` and most tests.

In Development, the API serves the web demo tracks because `MediaRoot` points there. Try:

```bash
curl http://localhost:5255/healthz
curl http://localhost:5255/api/songs
curl -X POST http://localhost:5255/api/rooms
curl -I http://localhost:5255/api/songs/synth-twinkle-variations/instrumental
curl -H "Range: bytes=0-1023" \
     http://localhost:5255/api/songs/synth-twinkle-variations/instrumental -o /tmp/chunk.opus
```

The `-I` call shows the ETag and cache headers; the ranged call should return `206 Partial Content` with exactly 1,024 bytes.

### 6.3 Web commands

From `src/clients/web`:

```bash
npm ci                    # install exact versions from package-lock.json
npm run dev               # Vite dev server with hot reload at http://localhost:5173
npm test                  # Vitest once (64 tests)
npm run build             # type-check with tsc, then bundle into dist/
npm run test:e2e          # build, serve dist/, run Playwright
npm run test:e2e:ci       # Playwright against an existing build
npm run fixtures:generate # regenerate the shared golden decision fixtures
```

- **`npm ci`** is `dotnet restore` for JavaScript: exact, reproducible install.
- **`npm run dev`** recompiles on save. Open `/` for landing, `/stage` for the stage, and `/remote` for the phone remote.
- **`npm test`** runs Vitest once; `npm run test:watch` keeps it running.
- **`npm run build`** runs `tsc --noEmit` (types only) and `vite build` (bundle). Type errors fail even though types do not exist at runtime.
- **`npm run test:e2e`** builds first, then `scripts/run-e2e.mjs` starts Playwright, which launches `vite preview` on 4173 itself.
- **`npm run fixtures:generate`** runs the TypeScript generator directly on Node 24. Only regenerate when a decision change is intentional, and review the diff.

### 6.4 Generate the demo tracks

```bash
dotnet run --project src/tools/Karaokay.SyntheticGen -- --seed 42 --out demo-tracks
```

This writes three song folders with four files each and prints every file's size and SHA-256. Run it twice to check determinism: the pure-.NET artifacts (`song.mid`, `lyrics.json`) are identical, and under the same FFmpeg/libopus build the Opus bytes are too. Different FFmpeg versions may legitimately produce different lossy bytes; tests compare only what is portable.

To refresh the web copies, generate into a temp directory and copy the three folders into `src/clients/web/public/demo-tracks/`. Never commit copyrighted audio; CI's 100 KiB guard enforces it.

### 6.5 What CI runs

`.github/workflows/ci.yml` runs one job on `ubuntu-latest` for pushes and pull requests to `main`:

1. Checkout.
2. Run the binary-media guard.
3. Install FFmpeg.
4. Set up .NET 10.
5. `dotnet restore`.
6. `dotnet build --configuration Release --no-restore`.
7. `dotnet test --configuration Release --no-build` (tests cannot silently rebuild). GitHub's
   ubuntu runner has Docker, so the Testcontainers PostgreSQL suite runs here instead of skipping.
8. Set up Node 24 with npm caching keyed on `package-lock.json`.
9. `npm ci`.
10. `npm run gen:api` — regenerate `schema.d.ts` from the `openapi.json` produced by the .NET build.
11. `git diff --exit-code -- src/clients/web/openapi.json src/clients/web/src/lib/api/schema.d.ts` —
    fail if generated OpenAPI artifacts drifted.
12. `npm test` (Vitest).
13. `npm run build`.
14. Install Playwright Chromium with system dependencies.
15. `npm run test:e2e:ci`.
16. `npm run fixtures:generate`.
17. `git diff --exit-code -- src/clients/web/fixtures/golden/` — fail on drift.
18. Upload Playwright reports on failure.

There is no deployment job yet; Azure arrives in Slice 6.

---

## 7. Where the project goes next

### Slice 4 — playback authority, clock sync, and browser video mode

Slice 3 has a queue and a small playback state machine. Slice 4 makes playback authoritative and
synchronized, and closes the gap between what phones control and what the stage renders.

- **`PlaybackAuthority`.** A single-writer hosted service consuming `Channel<RoomCommand>` (the type
  already exists in Contracts), owning every playback transition, persisting before broadcast, and
  running the duration watchdog. `ReportSongEnded` stays idempotent against both a stage report and
  the watchdog.
- **Clock sync.** `Ping/Pong` over the hub, an 8-sample median offset filter on clients, and
  `PlaybackChanged(PositionMs, TimeAnchorUtc)` anchors. The stage calibrates once against its audio
  clock; video mode uses the hard/soft nudge thresholds from §5.3.
- **Browser video mode.** A `<video>` element routed through `AudioContext.createMediaElementSource`
  into the same master and mic buses, plus the AwaitingDownload/SongEnded/Idle interstitials.
- **Stage playback wiring (review M5).** The stage must render room playback instead of its local
  `DemoTrackPicker`, or the phone controls should be hidden behind a flag until it does.
- **Cold-start rehydration (review M4).** Rebuild `RoomState` from Postgres on startup or first join,
  including `QueueSequence` and playback anchors. This is the natural owner because the authority
  already needs the persisted anchor; if deferred, schedule it before the Slice 6 demo.
- **Testing.** Integration: Start → `PlaybackChanged(Playing, 0, now)`, pause/resume within ±30 ms,
  skip advances, the watchdog fires after `duration + 5 s`, and a duplicate end report cannot
  double-advance. Playwright: synthetic clocks prove audio alignment within ±150 ms.

### Carried forward from the Slice 3 review

These are scheduled, not optional polish:

- **M2 — stage role is client-declared.** Any guest with the code can join as `"stage"`; fix before
  the Slice 6 public demo via a stage secret/one-time claim or one-stage-per-room binding. Needs an
  ADR because it changes the join contract.
- **M3 — rooms are never collected.** Every `/stage` load creates a room with no GC, close path, or
  rate limit, consuming the finite 279,841-code space. Pair with room-GC work (blueprint placed it
  in Slice 8) and land before Slice 6.
- **M4 — persistence is write-only.** Covered above; assign it explicitly to Slice 4 so it does not
  drift to Slice 8.
- **M5 — phones mutate playback the stage ignores.** Covered above; either wire the stage or flag
  the controls.
- **Minor backlog.** The 15 minors and 11 nits in
  [`docs/review/slice-3-review.md`](review/slice-3-review.md) (missing `QueueItemStateChanged` for
  the new playing item, no move/remove ownership checks, no reload auto-rejoin, duplicate
  `ParticipantJoined`, `IsSyntheticDemo` seeding, whole-queue persistence, `/healthz` format, enum
  parity, fresh token per rejoin, and the rest) should be folded into Slice 4 planning.
- **ADR-005 governance gap.** No Slice 3 ADR was written, and §4.3/§5.2 still disagree on whether
  `JoinRequest` carries a session token. Settle the token/reconnect contract (and record the
  Slice 3 decisions) before building identity-dependent features.

### Later slices

Slice 5 adds zero-key YouTube search and `yt-dlp`. Slice 6 deploys to Azure free tiers. Slices 7–8
build the Android TV stage: a C++ Oboe engine, then a SkiaSharp renderer consuming the same golden
decision fixtures as the web.

For full file lists, tests, and Definition-of-Done prompts, read [`docs/ARCHITECTURE.md` §7](ARCHITECTURE.md#7-phased-vertical-slice-implementation-roadmap). For the deep Slice 1 explanation, read [`docs/learning/slice-1-walkthrough.md`](learning/slice-1-walkthrough.md).

---

## 8. Frequently confusing points

**Why two stems instead of one mixed track?** Karaoke needs a guide-vocal slider. Two files let the browser crossfade the vocal bus from 0 to 1 without re-encoding or restarting. A single mix would bake the vocal in permanently. The live microphone bus arrives in Slice 7.

**Why not just use `<audio src="song.opus">`?** An `<audio>` element gives one volume control and no sample-accurate multi-bus mixing. The stage needs independent instrumental and guide buses, a master bus, a testable clock, and — in Slice 4 — a `<video>` routed into the same graph. Web Audio provides that graph.

**What is an `.opus` file?** Opus is an open, royalty-free audio codec for speech and music over the internet. It gives good quality at low bitrates and is supported by modern browsers. These files are 48 kHz stereo, encoded by FFmpeg's `libopus`.

**Why range requests if the demo songs are small?** Real imported songs are minutes long. Ranges let playback start early, seeking avoid re-downloading, and dropped connections resume. Building it now keeps the API contract stable when real libraries arrive.

**Why ETag and `immutable` caching?** A song id always maps to the same bytes because imports are not edited in place. Caching avoids re-downloading megabytes on every visit. The ETag is the correctness backstop: if length or modification time changes, the fingerprint changes.

**Why does the stage not call `/api/songs` yet?** The phone remote does call the API and hub, but
stage *audio* still loads the bundled `public/demo-tracks/` copies. That keeps `npm run dev` usable
with one server and lets Playwright run without a database or seeded library. The stage also
connects to the hub and renders the room queue; Slice 4 replaces the local picker with authoritative
room playback and then the stage can load media through the API like the phone does.

**Why no `react-router`?** Three routes do not justify a dependency. `App.tsx` normalizes the pathname, switches, and listens for `popstate`. A router may become worthwhile when the remote adds more screens.

**Why golden fixtures instead of screenshots?** Screenshots are brittle across fonts, anti-aliasing, and GPUs. The fixtures compare *decisions* — active line, fill values, dot states — at 100 instants, which are identical on any platform. Playwright separately checks that pixels are drawn and that frames differ over time, catching a blank canvas without depending on exact pixels.

**Why is there still a folder-scan fallback when there is a database?** Slice 2 needed a hermetic
library without SQL, and that fallback still earns its keep: bare `dotnet run`, media endpoint
tests, and the Docker-less half of the suite work with no container. With a connection string, the
songs table is authoritative and the scan is only the demo-seed source. The fallback is read-only;
the queue and rooms always use the database when one is configured.

**Why can't I move the playing song?** The playing song and the ended history are the playback
cursor, not ordinary queue rows. Allowing them to move made "put this song on top" land above the
playing row, so the next advance started after the cursor and skipped songs — or moved the cursor
past everything and ended the room early. The server now rejects moves of `Playing`/`Ended` rows and
clamps an upcoming item's target to the first slot after the current one, so "on top" means "plays
next". The phone UI disables the buttons for the same reason; the regression is pinned by
`QueueServiceTests` and the hub integration suite.

**Why is `InvariantGlobalization` worth it?** Golden tests compare JSON bytes across machines. Locale-dependent number formatting or casing could produce different bytes in CI and locally. Invariant mode removes that variable at the cost of a future translated API needing an override.

---

## 9. Glossary

| Term | Meaning |
|---|---|
| **ADR** | Architecture Decision Record: a short, numbered note capturing one decision and its consequences. |
| **Aspire / AppHost** | .NET's local orchestrator for multi-service development; the AppHost starts PostgreSQL and the API together. |
| **Bundler** | A tool that combines many source files into a few browser-ready files. Vite is the bundler. |
| **Canvas** | An HTML element with a JavaScript-controlled pixel drawing surface. |
| **CI** | Continuous Integration: automatically building and testing every change. |
| **Clean Architecture** | Layering pattern where business rules sit in the center and outer layers depend inward. |
| **CPM** | Central Package Management: one file declares all NuGet versions. |
| **Dependency injection** | Classes receive dependencies as parameters; a container builds and supplies them. |
| **EF Core** | Entity Framework Core, Microsoft's ORM for .NET. Used since Slice 3 for songs, rooms, queues, and download jobs. |
| **ETag** | An opaque fingerprint of a resource version, used for conditional HTTP requests and caching. |
| **FFmpeg / ffprobe** | Command-line tools for encoding, decoding, mixing, and inspecting media. |
| **Golden fixture** | A reference output produced by the real implementation, committed, and compared exactly. |
| **Hub** | A SignalR server endpoint clients connect to for real-time messages. |
| **Integration test** | A test exercising several real components together, often including I/O. |
| **Invariant globalization** | .NET mode avoiding locale data so behavior is identical everywhere. |
| **LUFS** | Loudness Units relative to Full Scale; a perceived-loudness measure (ITU-R BS.1770 / EBU R128). |
| **MIDI** | A file of musical events (notes, tempo, text), not audio samples. |
| **Minimal API** | ASP.NET Core style mapping lambdas directly to routes instead of controllers. |
| **MSBuild** | The build engine behind `dotnet build`; `.props` and `.csproj` configure it. |
| **NuGet** | The .NET package manager. |
| **ORM** | Object-Relational Mapper: maps database rows to language objects. |
| **Opus** | An open, royalty-free audio codec used for the stems. |
| **Path traversal** | An attack using `..` in input to escape an intended directory. |
| **Playwright** | A browser automation framework used for end-to-end tests. |
| **Range request** | An HTTP request for part of a file (`Range: bytes=...`), answered with `206 Partial Content`. |
| **rAF** | `requestAnimationFrame`, a browser API running a callback before each repaint (~60 fps). |
| **Reflection** | .NET's ability to inspect assemblies and types at runtime. |
| **SignalR** | ASP.NET Core's library for pushing real-time messages over persistent connections. |
| **Slice (vertical)** | A small end-to-end feature cutting through all layers, ending in something demoable. |
| **Solution file** | A `.sln` manifest grouping related .NET projects for build/test tools. |
| **Stem** | One isolated audio track from a multitrack recording (e.g. instrumental, vocals). |
| **Transpiler** | A tool converting source from one language/version to another, e.g. TypeScript to JavaScript. |
| **TypeScript** | JavaScript with static types, checked at build time and erased before running. |
| **Unit test** | A fast, isolated test of one small piece of logic. |
| **Vite** | A JavaScript dev server and bundler; also hosts Vitest. |
| **Vitest** | A unit test runner designed for Vite projects. |
| **Web Audio API** | The browser API for building and mixing an audio graph (`AudioContext`, `GainNode`, etc.). |
| **xUnit** | A .NET test framework using `[Fact]` and `[Theory]`. |

---

## 10. Further reading

- [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) — the blueprint, v1.2. Start with §7 for the roadmap or §4.1 for the `lyrics.json` contract.
- [`docs/learning/slice-1-walkthrough.md`](learning/slice-1-walkthrough.md) — the deep beginner guide to MIDI, tempo maps, phrase markers, FFmpeg, LUFS, and the tests.
- [`docs/review/slice-1-review.md`](review/slice-1-review.md) — how Slice 1 was reviewed and fixed.
- [`docs/review/slice-2-review.md`](review/slice-2-review.md) — the browser-stage review and its nit backlog.
- [`docs/review/slice-3-review.md`](review/slice-3-review.md) — the rooms/queue/remote review, the M1 fix, and the M2–M5 schedule.
- [`docs/adr/`](adr/) — the six decision records summarized in [§3.8](#38-docs--the-written-memory-of-the-project).
- [`src/apps/Karaokay.Api/Program.cs`](../src/apps/Karaokay.Api/Program.cs) — the smallest entry point; a good first read.
- [`src/apps/Karaokay.Api/Hubs/StageHub.cs`](../src/apps/Karaokay.Api/Hubs/StageHub.cs) — the real-time entry point; read beside the TypeScript `useStageClient`.
- [`src/clients/web/src/features/stage/StageShell.tsx`](../src/clients/web/src/features/stage/StageShell.tsx) — the phase machine tying UI, audio, and renderer together.

A good first afternoon: run `dotnet build` and `dotnet test`, then `npm ci && npm test` in the web client. Start the API and the web dev server, open `/stage`, click the gate, and watch Twinkle Variations sweep by. Then open `/remote` with the stage's room code in a phone-sized window, queue a song, and watch both surfaces update. Trace one pick through `StageShell.tsx`; that path touches almost every idea in this document, and the queue path touches the rest.
