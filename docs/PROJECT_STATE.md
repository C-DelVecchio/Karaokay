# Karaokay — Project State After Slice 4 and Early Slice 5

This is a guided tour of **what Karaokay is, what exists today, how the pieces fit together, and how to run it**. It assumes you can read a file and follow a command, but not that you have met terms like *Minimal API*, *Web Audio*, *ETag*, or *vertical slice*. Terms are defined where they matter for understanding that section, and there is a [Glossary](#9-glossary) near the end.

- **Snapshot date:** 2026-11-25
- **Completed:** Slice 1, Slice 2, Slice 3, Slice 4, and the documented Slice 5 YouTube ingestion —
  which landed early in the mislabeled Slice 4 commit `324da8a`
- **Slice 3 verdict:** SHIP-WITH-NITS; M1 (queue reorder skipping songs) is fixed. **M4**
  (cold-start rehydration) and **M5** (stage playback wiring) were closed by Slice 4; **M2**
  (client-declared stage role) and **M3** (rooms never collected) remain open before the Slice 6
  public demo.
- **Slice 4 status:** **COMPLETE.** `PlaybackAuthority` + duration watchdog, the authority-owned
  count-in, duplicate-end idempotency, authoritative stage playback, cold-start rehydration, and
  the §5.3 clock offset are all live. Slice 3 review M4/M5 are closed; M2/M3 stay open. See
  [ADR-008](adr/008-slice-4-playback-authority-decisions.md).
- **Next up:** Slice 6 (Azure free-tier deployment and CI/CD)
- **Master blueprint:** [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) v1.2
- **Slice 1 beginner guide:** [`docs/learning/slice-1-walkthrough.md`](learning/slice-1-walkthrough.md)
- **Slice 2 learning snapshot:**
  [`docs/learning/slice-2-walkthrough.md`](learning/slice-2-walkthrough.md)
- **Slice 3 deep guide:** [`docs/learning/slice-3-walkthrough.md`](learning/slice-3-walkthrough.md)
- **Slice 4 deep guide:** [`docs/learning/slice-4-walkthrough.md`](learning/slice-4-walkthrough.md)
- **Slice 5 deep guide:** [`docs/learning/slice-5-walkthrough.md`](learning/slice-5-walkthrough.md)
- **Reviews:** [`slice-1`](review/slice-1-review.md) · [`slice-2`](review/slice-2-review.md) · [`slice-3`](review/slice-3-review.md) · [`slice-4`](review/slice-4-review.md)

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
| 4 | Playback authority, clock sync, browser video mode | ✅ Complete |
| 5 | Zero-key YouTube search and `yt-dlp` downloads | ✅ Complete early (in `324da8a`); hardening landed in follow-up |
| 6 | Azure free-tier deployment and CI/CD | Planned |
| 7 | Android C++ Oboe audio engine | Planned |
| 8 | Android TV SkiaSharp stage and hardening | Planned |

Slices 1–5 are **complete**, with Slice 5 delivered early out of order. Commit `324da8a` is titled
"slice-4" but implemented the Slice 5 YouTube pipeline, so the number in the commit title does not
describe its contents; [ADR-007](adr/007-slice-4-5-deliverable-ordering.md) kept the original
roadmap unchanged, and the documented Slice 4 was completed afterwards. The unit and Vitest lanes
pass locally; the Testcontainers PostgreSQL integration tests are exercised in CI and skip locally
when Docker is unavailable, while the YouTube hub/REST contract edges that need no database run
against a DB-less host everywhere. The Slice 3 review's M4 (cold-start rehydration) and M5 (stage
playback wiring) were closed by Slice 4; **M2 (stage-role trust) and M3 (room GC)** must close
before the public demo. The Slice 4 review's hardening majors and its completion backlog have
**landed** in follow-up changes.

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
  join, ping, enqueue (library and YouTube), move, remove, start/pause/resume,
  skip, restart, report-ended, guide-vocal volume, and stage capabilities. Clients receive
  `JoinedRoom` snapshots, sequence-numbered `QueueChanged` deltas, `PlaybackChanged`,
  `QueueItemStateChanged`, and structured `HubError` messages.
- **Rooms.** `POST /api/rooms` reserves a four-letter code in the singleton `RoomRegistry` and,
  when a database is configured, persists a `PartyRoom` row; the database unique index is the
  collision backstop. Without a connection string the room is in-memory only.
  `GET /api/rooms/{code}` returns a full snapshot and `GET /api/rooms/{code}/qr` renders the phone
  join URL as a PNG.
- **Persistence.** EF Core entities (`Song`, `PartyRoom`, `QueueItem`) and Npgsql migrations.
  (`DownloadJob` and its table arrived early with the Slice 5 work in `324da8a`; see §2.4.)
  When a connection string is configured the API migrates and syncs the demo songs at startup; when
  it is absent, the Slice 2 filesystem scan remains a graceful fallback for tests and a bare
  `dotnet run`.
- **Queue engine.** `QueueService` serializes mutations per room with a command lock, persists
  before broadcasting (memory mutates first, so a persistence failure can leave memory ahead of the
  database — review minor #9), renumbers dense positions, and emits one monotonic sequence per
  change.
  Playback at the end of Slice 3 was a deliberately small subset of §5.1: start, pause, resume,
  skip, restart, and an idempotent stage end report; Slice 4 later moved these transitions behind
  `PlaybackAuthority` and added the count-in, watchdog, and rehydration (§2.5). Reordering is
  forward-only — the playing song and ended history are the cursor and cannot be moved.
- **Phone remote.** `/remote?code=XXXX` (also `/remote` with a manual code) has a join screen, a
  lobby with the room code, a library tab, a queue tab with reorder/remove, and playback controls.
  The stage at `/stage` creates a room, shows its code and QR in a corner overlay, and renders the
  shared queue. At the end of Slice 3 its library audio still came from the local `DemoTrackPicker`;
  Slice 4 replaced that with authoritative room playback (§2.5).
- **Tests.** The .NET suite had **84 tests** at the reviewed commit: 11 architecture, 38 unit, and
  35 integration (29 passed + 6 Testcontainers tests skipped without Docker), for 78 passed / 6
  skipped. The M1 fix added 8 unit and 3 integration tests. After the early Slice 5 work, the
  follow-up hardening, Slice 4, the post-review backend fixes, and the restart-guard fix, the current
  tree has **197 unit + 11 architecture + 31 integration passing** (239 passed of 267 total), with
  **28 Docker-gated integration tests skipped locally**; Vitest is at **133 tests in 16 files**. The
  Playwright lane
  is not counted here: it depends on the parallel web-client clock-sync alignment fix and is not
  asserted green by this backend change. Numbers measured 2026-11-25.

The Slice 3 review is [`docs/review/slice-3-review.md`](review/slice-3-review.md). Its M1 finding —
a guest reorder could skip a song or end the room early — is fixed; M2 and M3 are recorded there as
scheduled work for the Slice 6 demo, while **M4 and M5 were closed by the Slice 4 work**.

### 2.4 Slice 5 — YouTube ingestion (completed early)

Commit `324da8a` is labeled `feat(slice-4)`, but its contents implement the roadmap's **Slice 5**
scope. The project owner accepted that work as the Slice 5 deliverable and recorded the ordering
in [ADR-007](adr/007-slice-4-5-deliverable-ordering.md); the Slice 4 specification was not
rewritten.

What landed:

- **Search.** `IYouTubeService` and a CliWrap-only `YtDlpClient` search with
  `ytsearch{N}:` / `--flat-playlist --dump-single-json --skip-download --no-playlist`, sanitize the
  query, and enforce a timeout. Results map to typed DTOs.
- **Validation and cache identity.** `VideoIdValidator` enforces `^[A-Za-z0-9_-]{11}$` at every
  entry point. A filtered unique index on `Song.VideoId` deduplicates songs, and a cache hit by
  `videoId` short-circuits the download and returns `Ready`.
- **Download worker.** A bounded `Channel<DownloadJob>` (capacity 4, `BoundedChannelFullMode.Wait`)
  feeds `MediaDownloadWorker`, which moves jobs through `Queued → Resolving → Downloading →
  Transcoding → Completed/Failed`, coalesces single-flight downloads per `videoId`, retries twice
  with backoff, writes into a staging directory, and atomically moves the result under the trusted
  cache root. Progress is broadcast as `DownloadProgressChanged`; `SongReady`/`DownloadFailed`
  notify the room.
- **API and hub.** `GET/POST /api/youtube` and a range-enabled `GET /api/songs/{id}/video` exist.
  The hub implements `SearchYouTube` and `EnqueueYouTubeSong`; the contracts carry 15 client→server
  and 14 server→client methods.
- **Persistence.** `DownloadJob` and its `AddDownloadJobs` migration are live; `DownloadStage` now
  describes persisted job rows rather than existing on its own.
- **Guest UI.** `YouTubeTab` provides debounced search and enqueue; queue rows show live badges
  (`Queued`, percentage, `Ready`/cached, `Failed` + retry) driven by the room-state reducers.
- **Stage video surface.** `StageShell` renders a `<video>` element routed through the existing
  `WebAudioEngine` via one post-unlock `createMediaElementSource`, with clean audio↔video
  transitions. It consumes `PlaybackStateDto` and derives its expected position from the §5.3
  server-synchronized `serverNow` (Ping/Pong min-RTT best-of-window offset), not the raw local wall clock. Slice 4
  then made this part of the authoritative path: room playback drives the stage's library stems and
  the video surface, and the stage calibrates once against its own audio clock.
- **Stage video failure surface (2026-11-26).** A YouTube song whose cached MP4 never arrived no
  longer renders as a silent black stage: `useStageVideo` classifies media-source and play errors,
  and `StageShell` shows a visible "Video unavailable" card with Retry/Dismiss. Covered by
  `videoSync` unit tests and the `youtube-video-failure` Playwright spec.
- **Tests.** Unit coverage for video-id validation, yt-dlp JSON/progress parsing, argument arrays,
  coalescing, worker transitions, and temp cleanup; integration coverage with a deterministic
  `FakeYouTubeService` (search, dedup, SignalR progress/`SongReady`, 206 range serving); Vitest for
  reducers; and a deterministic yt-dlp-shim Playwright spec.
- **FFmpeg merge fix (2026-11-26).** yt-dlp treats `--ffmpeg-location` as a literal path, so the
  development value `ffmpeg` made it warn that ffmpeg did not exist and skip the merge, leaving
  `{videoId}.fNNN.*` fragments that `FindDownloadedFile` correctly refused to promote. Bare names
  are now resolved through `PATH` (or the flag is omitted so yt-dlp does its own lookup) before the
  invocation. When a download still produces nothing, the failure message now carries the staged
  filenames plus truncated yt-dlp stderr/stdout tails, with a distinct message when only `.part`
  files remain.

The feature is complete in scope, with review majors **landed** in a follow-up change:
unsynchronized wall-clock video sync (Ping/Pong offset filter), bounded-channel backpressure on
request paths, unauthenticated/unthrottled `yt-dlp` spawning, strict H.264/AAC enforcement,
machine-specific development paths, the CI requires-Docker gate, and DTO field-parity contract
tests. The verdict and open backlog are in
[`docs/review/slice-4-review.md`](review/slice-4-review.md). The deep walkthrough is
[`docs/learning/slice-5-walkthrough.md`](learning/slice-5-walkthrough.md).

### 2.5 Slice 4 — playback authority, clock sync, authoritative stage playback

Slice 4 answered: *who is allowed to move playback, and how does every screen converge on the same position?*

- **Single writer.** `PlaybackAuthority` (`Features/PlaybackAuthority.cs`) is a `BackgroundService`
  and the only component that drives room playback. It owns a bounded `Channel` of capacity **256**
  (`BoundedChannelFullMode.Wait`, single reader) whose envelope is a `RoomCommand` plus a
  `TaskCompletionSource`. Hub methods — `StartPlayback`, `PausePlayback`, `ResumePlayback`,
  `SkipSong`, `RestartSong`, `ReportSongEnded`, and `SetGuideVocalVolume` — await that completion,
  so a phone observes the persisted-then-broadcast state on return (**read-your-writes**). The
  consume loop applies every durable transition under the room `CommandLock` and persists before
  broadcasting (§5.1 rule 6); playback + queue writes for one transition share a single
  `SaveChanges` transaction. The count-in tick is the one non-persisting broadcast: it sends
  `CountdownTicked` after releasing the command lock so a slow client can never block Pause/Skip.
  Enqueue/move stay on their direct path; removal is authority-owned because deleting the current
  item also resets playback.
- **Authority-owned count-in.** Start puts the room in `Countdown` with an anchor of "now" and four
  remaining beats. The authority broadcasts `CountdownTicked(queueItemId, remainingBeats, beatMs:
  500)` per beat (4 → 1) over a 2 s window, then auto-transitions to `Playing` with a **fresh**
  `PlaybackStartedAtUtc` anchor. `Features/PlaybackTiming.cs` pins `CountdownBeats = 4` and
  `CountdownBeatMs = 500`. Pause during the count-in is supported and parks at `Paused`/position 0
  (the phone remote enables the Pause button during the count-in in parallel); Resume starts a fresh
  anchor; Skip cancels and advances; Restart re-enters the count-in; a duplicate Start is ignored.
  The count-in is **start-only**: the advance path (song ended / skip) goes straight to `Playing`
  with a fresh anchor and never re-enters `Countdown`. The beat is deliberately fixed and
  server-authoritative: `CountdownTicked` drives the phone ring, which animates locally per tick. In
  room mode `StageShell` shows the fixed four-beat count-in carried by the Start broadcast; it does
  not decrement its dots per beat. Only the database-less local `DemoTrackPicker` derives dots from
  `lyrics.json` gaps. A hard restart mid-count-in resumes at the next due beat instead of
  replaying beats the previous process already broadcast.
- **AwaitingDownload auto-start.** Starting the room while the only candidate is still
  `Downloading` parks it in `AwaitingDownload` (§5.1 rule 3). The authority's tick loop watches the
  cursor item and begins the count-in by itself when it becomes `Ready`, guarded on the room still
  awaiting that exact item; a failed awaited download returns the room to `Idle`.
- **Duration watchdog.** A 100 ms tick considers only `Playing` rooms and fires at
  `PlaybackStartedAtUtc + Song.DurationMs + 5 s`, advancing through the **same guarded end path** as
  a stage report.
- **Duplicate-end idempotency.** One shared guard under `CommandLock` — `queueItemId ==
  CurrentQueueItemId` — means a duplicate stage report, or the watchdog firing after that report,
  can never double-advance the queue.
- **Clock sync.** `StageHub.Ping` echoes `t1` with `t2` stamped at the server, and
  `lib/signalr/clockSync.ts` keeps the last **8 samples** and publishes the **min-RTT
  best-of-window** offset plus `serverNow`. Samples slower than 250 ms are rejected before they
  enter the window; the offset is only trusted after **two** accepted samples, otherwise callers
  fall back to the receipt-time anchor. The stage uses that estimate for video position and for
  joining mid-song.
- **Authoritative stage playback.** `features/stage/playbackController.ts` (`StagePlaybackController`
  + `useStagePlayback`) subscribes to `PlaybackChanged`, loads library stems and lyrics through
  `songMedia.ts` (`/api/songs/{id}/instrumental|vocals|lyrics`, with GUID→slug catalog mapping for
  the no-DB fallback), and performs the §5.3 Layer-2 start alignment **once**:
  `seekOffset = expectedMs − audioPositionMs`. Audio is ground truth and is never re-slaved
  mid-song; a stage joining mid-song aligns to the authoritative position. Pause/resume/skip/restart
  and ended are steered from the room, with deduped `reportSongEnded`.
- **Stage UI.** Room playback takes over `StageShell`'s library mode; the local `DemoTrackPicker`
  remains an explicit fallback when room playback is idle or absent, so `npm run dev` and
  database-less Playwright still work. The guide-vocal slider routes through the hub
  (`SetGuideVocalVolume`) when a room is present. New test attributes `data-room`, `data-song-id`,
  `data-queue-item-id`, and a unified `data-phase` (including `paused`) make the takeover
  assertable. `WebAudioEngine.pause()` preserves the media position, reuses decoded buffers, and
  does not fire `onEnded`.
- **Phone remote.** `CountdownTicked` feeds `roomState.applyCountdownTick`, which renders an SVG
  countdown ring in `PlaybackControls.tsx` (§5.1 rule 5 mini progress ring). `useStageRoom` exposes
  `setGuideVocalVolume` and a test-only `window.__karaokayClockBiasMs` bias.
- **Cold-start rehydration.** `Features/RoomRehydrator.cs` plus the pure
  `Features/RoomStateMapper.cs` run in `Program.ApplyDatabaseStartupAsync` after demo seeding when a
  database is configured. One `AsNoTracking` query loads open rooms with their ordered queue and
  joined songs; positions are re-densified, playback fields and anchors restored (a `Playing` room
  keeps flowing), a dangling cursor resets to `Idle`, and stale duplicate `Playing` rows are
  demoted. `PartyRoom.QueueSequence` is now persisted with **write-ahead minting** (the sequence is
  minted before the row is written) through migration `20261125160000_AddRoomQueueSequence`
  (`Rooms.QueueSequence bigint NOT NULL DEFAULT 0`) and restored via
  `RoomRegistry.RestoreQueueSequence`. Without a connection string rehydration is a no-op, so
  no-database mode is unchanged.
- **Testability.** `QueueService` takes an injectable `TimeProvider` so tests can drive anchors;
  `PlaybackAuthority` has the same internal seam plus a tick-interval override. `QueueOrdering` also
  protects the count-in cursor item from moves.
- **Tests.** The final tree has **197 unit + 11 architecture + 31 integration passing** (239 passed
  of 267), with **28 Docker-gated integration tests skipped locally** (CI runs them). The Slice 4
  coverage includes the five Docker-gated integration tests (two-phase Start broadcast and tick
  contract, ±30 ms pause/resume, the watchdog, duplicate-end, cold-restart rehydration) plus new
  authority unit tests for `AwaitingDownload` auto-start, the `AwaitingDownload` restart guard,
  cold-restart count-in resume, and the
  command-channel write timeout. Vitest is **133 tests in 16 files**. The Playwright lane is owned
  by the parallel web-client change that lands the clock-sync alignment fix; its specs (including
  `e2e/playback-authority.spec.ts` for phone-driven Start/Pause/Resume/Skip convergence and the
  +300 ms `injectDrift` hard-nudge in `e2e/youtube.spec.ts`) are **not asserted green by this
  backend change**. These backend fixes plus that alignment fix are what make the E2E lane
  reproducible.

The implementation choices and the two blueprint filename corrections are recorded in
[ADR-008](adr/008-slice-4-playback-authority-decisions.md); the completion follow-up is in the
[Slice 4 review](review/slice-4-review.md). The deep walkthrough is
[`docs/learning/slice-4-walkthrough.md`](learning/slice-4-walkthrough.md).

### 2.6 What is NOT built yet

Do not look for these:

- **Room lifecycle and garbage collection.** Rooms are never closed or garbage-collected (Slice 3
  review M3), so every `/stage` load consumes a code from the finite 279,841-code space. Cold-start
  rehydration now exists (Slice 4), but room GC does not.
- **Stage authentication.** The stage role is self-declared in `JoinRequest` (Slice 3 review M2);
  any guest with the code can claim it until a stage secret or one-stage-per-room claim lands.
- **Azure deployment.** No Bicep files, deploy workflow, or live URL.
- **Android anything.** No `net10.0-android` app, C++ Oboe engine, or SkiaSharp renderer.

Authoritative playback, phone-driven stage library audio, cold-start rehydration, and the browser
API client for library audio are no longer on this list — see §2.5. YouTube ingestion is no longer
on it either — see §2.4.

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
│   ├── PROJECT_STATE.md      # living snapshot (this file)
│   ├── adr/
│   ├── learning/
│   │   ├── slice-1-walkthrough.md
│   │   ├── slice-2-walkthrough.md
│   │   ├── slice-3-walkthrough.md
│   │   ├── slice-4-walkthrough.md
│   │   └── slice-5-walkthrough.md
│   └── review/{slice-1,slice-2,slice-3,slice-4}-review.md
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
- **`adr/`** — eight Architecture Decision Records:

| ADR | Decision in one line |
|---|---|
| [001](adr/001-single-replica-signalr-azure.md) | Run SignalR on one scale-to-zero Azure Container App replica with sticky sessions, avoiding paid SignalR/Redis. |
| [002](adr/002-android-oboe-hdmi-usb-drift.md) | Build a full-duplex Oboe engine for USB mic → HDMI, with manual clock-drift compensation. |
| [003](adr/003-dual-mode-stage-web-vs-native.md) | Ship web and native stages that share one decision-level renderer contract. |
| [004](adr/004-dual-source-media-pipeline.md) | Support Rock Band multitracks and YouTube downloads through one queue/stage, with cloud ingestion disabled. |
| [005](adr/005-slice-1-blueprint-reconciliations.md) | Correct the blueprint after Slice 1: 23-letter codes / 279,841 combinations, reflection-based rules, code-level validation. |
| [006](adr/006-line-wrapping-ownership.md) | The renderer owns glyph-measured wrapping; the importer stores only explicit line breaks. |
| [007](adr/007-slice-4-5-deliverable-ordering.md) | Slice 5 YouTube ingestion landed early in a commit labeled Slice 4; the original Slice 4 scope stays open. |
| [008](adr/008-slice-4-playback-authority-decisions.md) | Slice 4 completion: bounded command-channel authority, fixed 500 ms count-in beat, persisted queue sequence, injectable clock, and blueprint filename reconciliation. |

- **`learning/slice-1-walkthrough.md`** — a long beginner guide to Slice 1: MIDI, tick math, lyric encoding, FFmpeg mixing, LUFS, and the tests.
- **`learning/slice-2-walkthrough.md`** — the preserved "Project State After Slice 2" snapshot (commit `ebc3e95`), kept as a learning walkthrough with links fixed for its new home.
- **`learning/slice-3-walkthrough.md`** — the deep Slice 3 guide: SignalR hubs and groups, the room registry and queue engine, EF Core persistence, the M1 reorder bug, the phone remote, and the review backlog.
- **`learning/slice-4-walkthrough.md`** — the deep Slice 4 guide: the single-writer `PlaybackAuthority`, the count-in and duration watchdog, Ping/Pong clock sync, authoritative stage playback, and cold-start rehydration.
- **`learning/slice-5-walkthrough.md`** — the deep Slice 5 guide: zero-key `yt-dlp` search, the bounded download worker and cache, the guest `YouTubeTab`, and the stage `<video>` surface.
- **`review/slice-1-review.md`**, **`review/slice-2-review.md`**, **`review/slice-3-review.md`**,
  **`review/slice-4-review.md`** — internal reviews with verdicts, fixed defects, and carry-forward
  backlogs. The Slice 3 record fixes M1 and schedules M2–M5 (M4/M5 since closed by Slice 4); the
  Slice 4 record resolves the scope mislabel, records the hardening that landed in its follow-ups,
  and records the completion of the authority, watchdog, rehydration, and stage wiring.

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

1. Register the media library, song catalog, room registry, room service, queue service, YouTube
   ingestion, the `PlaybackAuthority` hosted service, the room rehydrator, SignalR, OpenAPI, CORS,
   and — when `ConnectionStrings:Karaokay` exists — EF Core with Npgsql.
2. Map `/healthz` through `MapHealthChecks` (plain-text probe output; the Slice 2 JSON body was
   replaced in Slice 3 — a review nit), `/openapi`, the song/media/room endpoints, and
   `/hubs/stage`.
3. When a database is configured, apply migrations, seed the demo songs, and rehydrate open rooms
   into the registry before serving.

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

`POST /api/rooms` reserves a code and, when a database is configured, persists a `PartyRoom`; without
one the room is in-memory only. `GET /api/rooms/{code}` returns the same `RoomSnapshot` shape the hub
sends; `GET /api/rooms/{code}/qr` renders the phone join URL as a PNG with `QRCoder`. The QR URL
prefers a configured `PublicBaseUrl` and otherwise trusts the request scheme/host — a known
reverse-proxy caveat recorded in the review.

#### `Hubs/StageHub.cs`

`StageHub` is a **typed hub**: `Hub<IStageHubClient>` implements `IStageHub`, so client and server
method names are compile-checked C# instead of strings. A hub instance exists only per invocation,
so connection→room membership lives in the singleton `RoomRegistry`. `JoinRoom` always returns a
fresh session token and sends a full `JoinedRoom` snapshot; `OnDisconnectedAsync` broadcasts
`ParticipantLeft`. `RequireRoomAsync` rejects commands before a join, and `RequireStageAsync` gates
`ReportSongEnded`/`UpdateStageCapabilities` on `Role == "stage"`.

That stage gate is only as strong as the role the client claims in `JoinRequest` (review M2, to be
fixed before the public demo). The playback methods do not mutate room state directly: each builds a
`RoomCommand` and awaits `PlaybackAuthority`, which persists and broadcasts before completing the
caller. `ReportSongEnded` is idempotent in the authority: anything but the current queue item is
ignored under the command lock, and the watchdog shares the same guard, so a duplicate stage report
cannot double-advance.

#### `Features/RoomRegistry.cs` and `RoomService.cs`

`RoomState` is the hot in-memory room: a command semaphore (single-writer shape), a sync root for
snapshot reads, playback fields, participants by connection, and a monotonic queue sequence.
`RoomRegistry` holds live rooms and connection memberships in concurrent dictionaries.
`RoomService.CreateRoomAsync` retries code generation against both the registry and the database
unique index, releasing the in-memory reservation if persistence collides.

#### `Features/QueueService.cs` and `QueueOrdering.cs`

Every queue mutation takes the room command lock, mutates memory, persists via a fresh EF Core
scope, then broadcasts a `QueueChanged` delta with the next sequence. The mutation is memory-first:
a persistence failure can leave memory ahead of the database (review minor #9). Tracked properties
are dense positions plus states; `RenumberLocked` keeps zero-based ordering after removes.

`QueueOrdering` holds the pure cursor rules fixed in the Slice 3 review: only upcoming rows may
move, a target above the playing row clamps to the first upcoming slot, and advance selects the
next playable row after the cursor with a fallback to a playable row behind it. This prevents a
guest reorder from skipping a song or forcing `Idle` while songs remain. It also protects the
count-in cursor item. Playback transitions no longer live here: they are applied by
`PlaybackAuthority` through the command channel, while `QueueService` supplies the shared
persist-then-broadcast transition bodies and an injectable `TimeProvider` for position anchors.

#### `Features/PlaybackAuthority.cs` and `PlaybackTiming.cs`

The single writer for room playback. Hub playback calls enqueue a `RoomCommand` with a
`TaskCompletionSource` on a bounded channel (256, wait, single reader); the consume loop takes the
room command lock, persists, broadcasts, and completes the caller. The same service owns the 2 s
authority count-in (`PlaybackTiming`, four 500 ms beats with `CountdownTicked`) and the 100 ms
duration watchdog, which ends a stalled `Playing` room at `PlaybackStartedAtUtc + DurationMs + 5 s`
through the same guarded advance path as a stage report. One `queueItemId == CurrentQueueItemId`
guard makes stage reports and watchdog firings idempotent together.

#### `Features/SongCatalog.cs` and EF Core persistence

`SongCatalog` is the read side used by both HTTP and the hub. With a connection string it queries
`KaraokayDbContext`; without one it maps deterministic demo GUIDs back to the filesystem scan.
Entities (`Song`, `PartyRoom`, `QueueItem`) and Npgsql migrations live in
`Karaokay.Infrastructure/Persistence`; `DownloadJob` arrived with the early Slice 5 work.
`DemoSongSeeder` syncs the scanned demo library into the songs table at startup (insert missing
rows, update existing ones, then save), rather than seeding only an empty database.

Persistence is no longer write-only: `RoomRehydrator` rebuilds open rooms from Postgres at startup
(one `AsNoTracking` query with the ordered queue and joined songs), and `RoomStateMapper` restores
positions, playback anchors, guide-vocal level, and the persisted `PartyRoom.QueueSequence` that
write-ahead minting keeps monotonic across restarts. A dangling cursor becomes `Idle`, and stale
duplicate `Playing` rows are demoted. Migration `AddRoomQueueSequence` adds the stored sequence
column; without a connection string rehydration is a no-op.

#### `Karaokay.AppHost` and `Karaokay.ServiceDefaults`

`.NET Aspire` orchestrates multi-service local development. `AppHost` now starts a PostgreSQL
container with a persisted data volume and the API with a `Karaokay` connection string; the API
applies migrations and syncs demo songs on startup. `ServiceDefaults` supplies shared telemetry,
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
  `HubErrorDto`), the `QueueItemState`/`PlaybackStatus` wire enums, and `RoomCommand` — now the
  payload carried on `PlaybackAuthority`'s command channel.

Why one serializer? The generator, importer, golden tests, and `verify` all compare bytes. Separate JSON options would create false failures and hide real ones.

#### `Karaokay.Domain`

The persistence entities and enums: `Song`, `PartyRoom`, `QueueItem`, `DownloadJob`, plus
`MediaSource`, `QueueItemState`, `PlaybackStatus`, and `DownloadStage`. The wire mirrors live in
Contracts because Contracts cannot reference Domain. `SignalRContractTests` checks the hub method
names, the `RoomCommand`/`HubErrorCodes` constants, and each mirrored DTO's field count and camelCase
name set; enum names/values are not compared yet (slice-3 review minor #14), so renumbering one copy
is still a silent wire break. Name-only parity remains for field types and optionality.

#### `Karaokay.Application`

`RoomCodeGenerator` uses `ABCDEFGHJKLMNPRSTUVWXYZ` (23 letters, no `I`/`O`/`Q`), builds four-character codes (`23⁴ = 279,841`), accepts an injectable `Random` for replayable tests, reserves each code with `HashSet.Add`, and retries at most 100 times before throwing. The database unique index on `PartyRoom.Code` is now the collision backstop used by `RoomService`.

#### `Karaokay.Infrastructure/` — adapters to real formats and a database

`Persistence/KaraokayDbContext.cs`, entity configurations, and the Npgsql migrations implement the
Slice 3 storage path. The provider-specific filtered unique index on `Song.VideoId` is codified in
the model, and the DB is now the cold-restart target: Slice 4's rehydrator rebuilds rooms and their
queue sequence from it at startup.

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
minor #3). Playback controls change authoritative room state, and the stage now renders it: the
phone's Start/Pause/Resume/Skip reach the same audio the stage plays. During the authority count-in,
`PlaybackControls` renders the SVG countdown ring fed by `CountdownTicked`; the guide-vocal slider
routes through `SetGuideVocalVolume` when a room is present.

#### `StageShell.tsx` — the phase machine

```text
picking ──pick──▶ countdown ──lead-in ends + audio ready──▶ playing ──source ends──▶ ended
   ▲                                                                                 │
   └──────────────────────────── pick another song ─────────────────────────────────┘
```

On a pick, `engine.loadSong()` fetches and decodes both stems while a countdown animation runs from the lead-in gap. `Promise.all` waits for both, then `engine.start(leadInOffsetMs)` starts both sources and seeks past the lead-in so audio and lyrics agree. When the source ends, the phase becomes `ended` and the picker returns.

Separately from audio, the stage calls `POST /api/rooms` on load, shows the returned code and QR in a
corner overlay, connects to the hub as `"stage"`, and renders the shared queue. When a room is
present, **room playback takes over**: `useStagePlayback` loads the current library song through the
media API, aligns once to the authoritative anchor, and steers pause/resume/skip/restart from the
hub. The local `DemoTrackPicker` remains an explicit fallback when room playback is idle or absent,
which keeps the database-less dev and Playwright paths working. The unified `data-phase` attribute
covers the room phases, including `paused`. Guide-vocal volume routes through the hub when a room is
present. The authoritative layer is `playbackController.ts` (next section).

**Why the gesture gate?** Browsers block audio until the user interacts, so a page cannot surprise a visitor with sound. An `AudioContext` created before a click starts suspended. The stage shows **"Click to Enable Stage Audio"**; only that click calls `engine.unlock()`. Nothing in the playback path — no source creation, decode, or media-element routing — happens before it. The Playwright audio-gate test asserts exactly that through `window.__karaokayAudioLog`.

#### `features/stage/playbackController.ts` and `songMedia.ts` — authoritative playback on the stage

`StagePlaybackController` (bound with `useStagePlayback`) subscribes to `PlaybackChanged` and turns
room state into stage audio. It loads the song's stems and `lyrics.json` through `songMedia.ts`
(`/api/songs/{id}/instrumental|vocals|lyrics`), mapping demo GUIDs back to slugs for the no-DB
catalog. Loading stays gesture-gated: nothing is fetched or decoded before the audio unlock.

The §5.3 Layer-2 calibration happens **once** per song:
`seekOffsetMs = expectedMs − audioPositionMs`, where `expectedMs` comes from the playback
anchor plus the synchronized server clock. Audio is ground truth: the stage does not re-slave the
audio to the server mid-song. A stage that joins mid-song applies the same one-time alignment to
the authoritative position, so it starts at the right lyric line. Pause/resume/skip/restart and
song-ended are steered from `PlaybackChanged`; `reportSongEnded` is deduped per queue item. The
`<video>` surface remains the video-mode path, while this controller owns library audio.

#### `WebAudioEngine.ts` — two stems, three buses

The **Web Audio API** is the browser's audio graph: create nodes, connect them, and the browser mixes in real time.

```text
instrumental buffer ──▶ instrumental GainNode ──┐
                                                 ├──▶ master GainNode ──▶ speakers
vocals buffer ────────▶ guide GainNode ─────────┘
```

A **GainNode** is a volume control. Three buses allow independent instrumental, guide-vocal, and master volume. `setGuideVocalVolume()` ramps the guide bus over 45 ms, so there is no click or dropout and sources are never recreated.

`unlock()` is idempotent and must be called from a user gesture; on failure it rolls back so the next click retries. `pause()` stops both sources while preserving the media position, reuses the decoded buffers, and does **not** fire `onEnded`, so a room pause is not mistaken for a song end. A **test clock** (`stageTestClock`) overrides `positionMs()`, letting Playwright pin an exact millisecond and screenshot the canvas deterministically. `positionMs()` otherwise reads `AudioContext.currentTime` — the audio hardware clock — because what the user hears is the ground truth.

Slice 2 does **not** use `createMediaElementSource` or `<audio>`. The early Slice 5 video surface
adds one post-unlock `createMediaElementSource` path for `<video>`; tests still assert that the
Slice 2 audio path never calls it.

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
| `playback-authority.spec.ts` | Phone Start/Pause/Resume/Skip converge on the stage's audio; a mid-song stage join aligns under a +40 ms clock bias standing in for RTT/2. |
| `youtube.spec.ts` | The deterministic yt-dlp shim drives search/download/progress into the queue, and a +300 ms `injectDrift` on the video surface is hard-nudged back within the §5.3 threshold. |

`playwright.config.ts` serves the built `dist/` on port 4173 and launches with `--mute-audio` and `--autoplay-policy=user-gesture-required`, so the gate is enforced even in tests.

### 4.5 `src/tests/` — the safety net

- **`Karaokay.UnitTests` (197 tests).** A **unit test** checks one small piece in isolation: a pure function, parser, or validator. The project uses **xUnit**, where `[Fact]` is one test and `[Theory]` runs one test with several data rows. Coverage includes tempo math, talky/connector handling, phrase grouping by markers versus gaps, countdown math, golden `lyrics.json` byte comparisons, room codes, generator determinism, SignalR method-name parity, the Slice 3 queue-service rules, the early Slice 5 video-id, yt-dlp parsing, coordinator, and worker paths, and the Slice 4 authority. `PlaybackAuthorityTests` drives an injected clock through the two-phase Start/tick contract, pause/resume position preservation (the ±30 ms bound itself is asserted by the Docker-gated integration suite), watchdog, duplicate-end protection, the count-in interactions, `AwaitingDownload` auto-start, cold-restart count-in resume, and the command-channel write timeout; `RoomStateMapperTests` and `RoomRehydrationQueryTests` cover cold-start mapping and the query shape via `RoomTestHarness`. `YouTubeIngestionServiceTests` covers channel and per-room admission caps, the coalescing bypass, queue-full cleanup of coalesced targets, and session-token resolution against an in-memory harness with no database. `QueueServiceTests` runs the real service against in-memory rooms with a hub recorder and no database: reorder clamping/rejection, advance ordering, the countdown reset on removal, and Start/Pause/Resume/Skip/Idle transitions.
- **`Karaokay.IntegrationTests` (31 passing; 28 Docker-gated tests skip locally).** An **integration test** checks several real components working together, usually including I/O. The media-endpoint tests boot the real API through `WebApplicationFactory`; they assert range responses (`206` with `Content-Range`/`Accept-Ranges`), ETag presence and strength, content types, 404 `ProblemDetails`, path-traversal 404s, invalid-folder skipping, and the library listing. The room-hub tests also boot the real host, but against a **Testcontainers PostgreSQL** container: room-code uniqueness, snapshot join/rejoin, monotonic deltas, reorder/advance regressions, the playback lifecycle, stage-role rejection, and five Slice 4 tests (two-phase Start broadcast and tick contract, ±30 ms pause/resume, watchdog, duplicate-end, cold-restart rehydration). The early Slice 5 suite adds deterministic `FakeYouTubeService` coverage for search, dedup, SignalR progress/`SongReady`, and video range serving, plus session-token binding, the anonymous malformed-token fallback, and a per-IP enqueue rate-limit `429` with `Retry-After` (the fixture assigns a simulated remote IP). `PostgresApiFixture` records a skip reason when Docker is unavailable, so laptops stay green while CI runs the real persistence path. The importer integration tests do not boot the API: they run against a temporary fake Rock Band folder with real FFmpeg-generated audio, assert stem selection and the filter graph, and check LUFS within ±1 LU. `FfmpegGuard` skips FFmpeg-dependent tests with a clear message when the tools are missing.
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
        │ Start / Pause / Skip ────────▶│  PlaybackAuthority channel  │
        │                               │  lock → persist → broadcast  │
        │◀──── PlaybackChanged ─────────┤──── PlaybackChanged ────────▶│
```

1. **Join.** A phone or stage calls `JoinRoom`; the hub binds the connection to a `RoomState` in the singleton registry, adds it to the SignalR group, and returns a full snapshot plus a session token. Rejoin uses the same path.
2. **Queue.** Enqueue/move/remove take the room command lock, mutate the in-memory queue, persist through EF Core (when configured), then broadcast a `QueueChanged` delta carrying the next monotonic sequence. A client that sees a gap re-joins for a snapshot rather than replaying deltas.
3. **Playback.** Start/Pause/Resume/Skip/Restart/`ReportSongEnded`/`SetGuideVocalVolume` enqueue a
   `RoomCommand` on the authority's bounded channel and await completion; the single-writer
   `PlaybackAuthority` applies each durable transition under the room lock, persists, and broadcasts
   `PlaybackChanged`. During the count-in it also broadcasts `CountdownTicked` per beat without a DB
   write, and it sends those ticks after releasing the command lock. The watchdog ends a stalled
   `Playing` room through the same guarded path. Reordering remains forward-only: `Playing`/`Ended`
   rows are the cursor and cannot be moved, which is the M1 fix from the Slice 3 review.
4. **Stage convergence.** The stage loads the current song's stems/lyrics and aligns once to the
   authoritative anchor, then keeps its own audio clock as ground truth; a mid-song join aligns to
   the authoritative position. Phone Start/Pause/Resume/Skip therefore drive stage sound. A room
   started on a still-downloading head parks in `AwaitingDownload` and the authority starts the
   count-in by itself when the item becomes `Ready` (§5.1 rule 3). The stage role remains
   client-declared (review M2).
5. **YouTube path.** A guest searches and enqueues through `/api/youtube` (the guest UI is REST-only);
   the worker downloads, broadcasts progress, and marks the queue row `Ready`. The stage renders the
   resulting video through its `<video>` surface, steered by the §5.3 server-clock offset, and the
   same `PlaybackAuthority` owns the room's playback state for video songs too.

---

## 6. How to build and run everything

### 6.1 Prerequisites

**.NET 10 SDK** (`dotnet --version` reports 10.x), **Node.js 24** with npm, and **FFmpeg/ffprobe** on `PATH` for import, mixing, and audio tests (`sudo apt-get install ffmpeg` or `brew install ffmpeg`). Optional: `npx playwright install chromium` (CI uses `--with-deps` for system libraries).

### 6.2 .NET commands

From the repository root:

```bash
dotnet build          # restore + compile all twelve projects; warnings are errors
dotnet test           # run unit, integration, and architecture tests (248 passing; 29 Docker-gated skips locally; 277 with Docker)
dotnet run --project src/apps/Karaokay.Api     # Development profile: http://localhost:5255
dotnet run --project src/apps/Karaokay.AppHost # optional: API + PostgreSQL via Aspire
```

`dotnet build` reads `karaokay.sln`, restores NuGet packages, and compiles everything. Because warnings are errors, a green build is a strong signal. `dotnet test` builds if needed, then runs xUnit; FFmpeg-dependent integration tests are skipped by a guard if the tools are missing, and the Testcontainers PostgreSQL suite skips with an actionable message when Docker is unavailable. The current tree is **248 passed / 29 skipped** on a Docker-less machine (206 unit, 11 architecture, 31 integration); with Docker available the full suite is **277 passed / 0 skipped**, and CI runs the Docker-gated tests against a real container.

The `AppHost` profile starts PostgreSQL with a persisted data volume and passes a `Karaokay`
connection string to the API, which applies migrations and syncs the demo songs. Without a
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
npm test                  # Vitest once (133 tests)
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

**Status: COMPLETE (2026-11-25).** Commit `324da8a` was labeled "slice-4" but delivered the
documented Slice 5; [ADR-007](adr/007-slice-4-5-deliverable-ordering.md) kept the original Slice 4
scope open, and the documented work landed afterwards. The implementation choices are recorded in
[ADR-008](adr/008-slice-4-playback-authority-decisions.md).

**What shipped**

- **Single writer.** `Features/PlaybackAuthority.cs` is a `BackgroundService` consuming a bounded
  `Channel` (capacity 256, wait, single reader) of `RoomCommand` + `TaskCompletionSource` envelopes.
  Hub playback methods await persist→broadcast completion (read-your-writes); every durable
  transition runs under the room command lock. A full/stalled channel fails the invocation after a
  2 s write timeout instead of hanging it.
- **Count-in.** `Features/PlaybackTiming.cs` pins four 500 ms beats; `CountdownTicked` runs 4 → 1,
  then `Playing` starts with a fresh anchor. It is start-only: advance/skip go straight to `Playing`.
  Pause during the count-in is supported (parked at position 0; the phone button is enabled in
  parallel); Skip cancels; Restart re-enters; duplicate Start is ignored. Room mode shows the fixed
  four-beat count-in from the Start broadcast in `StageShell`; only the no-DB picker uses
  `lyrics.json` gaps. A hard restart
  mid-count-in resumes at the next due beat instead of replaying beats already sent.
- **AwaitingDownload.** Start on a still-`Downloading` head parks the room instead of reporting
  `Idle`; the authority starts the count-in by itself when that exact cursor item becomes `Ready`
  (§5.1 rule 3), and a failed awaited download returns the room to `Idle`.
- **Watchdog and idempotency.** A 100 ms tick ends a `Playing` room at `PlaybackStartedAtUtc +
  DurationMs + 5 s` through the same guarded end path as a stage report. One
  `queueItemId == CurrentQueueItemId` guard makes duplicate reports and watchdog firings
  non-double-advancing.
- **Clock sync.** `StageHub.Ping` plus `lib/signalr/clockSync.ts`: 8-sample min-RTT
  best-of-window offset (250 ms RTT rejection, ≥2-sample confidence gate, receipt-anchor fallback)
  and `serverNow`.
- **Authoritative stage playback.** `features/stage/playbackController.ts` + `songMedia.ts` load
  stems/lyrics, align once with `seekOffset = expectedMs − audioPositionMs`, keep audio as
  ground truth, and steer pause/resume/skip/restart/ended. Room playback takes over `StageShell`;
  the local picker is an explicit idle fallback. `WebAudioEngine.pause()` preserves position.
- **Phone count-in ring.** `CountdownTicked` feeds `roomState.applyCountdownTick` and the SVG ring in
  `PlaybackControls.tsx`; the guide-vocal slider routes through `SetGuideVocalVolume`.
- **Cold-start rehydration.** `Features/RoomRehydrator.cs` + `Features/RoomStateMapper.cs` restore
  open rooms, re-densify positions, reset a dangling cursor to `Idle`, and demote stale duplicate
  `Playing` rows. `PartyRoom.QueueSequence` is persisted with write-ahead minting
  (`20261125160000_AddRoomQueueSequence`) and restored via `RoomRegistry.RestoreQueueSequence`.
  No database means no-op, so no-DB mode is unchanged.

**Definition-of-done checklist**

- [x] `PlaybackAuthority` single-writer hosted service + duration watchdog. **Landed in
      `Features/PlaybackAuthority.cs`; the 100 ms watchdog shares the guarded end path.**
- [x] Authoritative `AwaitingDownload` start (§5.1 rule 3). **Landed: `StartPlaybackAsync` parks on
      a downloading head and the authority's tick begins the count-in when the cursor item becomes
      `Ready`; a failed awaited download returns to `Idle`.**
- [x] `Ping`/`Pong` clock sync and the 8-sample min-RTT best-of-window client offset filter (250 ms
      RTT rejection, ≥2-sample confidence gate, receipt-anchor fallback), applied to the web
      stage video path (`StageHub.Ping` stamps `t2` at entry; `useStageVideo` passes `serverNow` into
      `expectedPositionMs`). **Landed; the authoritative stage audio start alignment and the phone
      countdown ring (`CountdownTicked`) also landed in `playbackController.ts` and
      `PlaybackControls.tsx`.**
- [x] Authoritative stage playback wiring (Slice 3 review M5); room playback now drives stage
      library audio, with `DemoTrackPicker` only as an idle fallback. **Landed in
      `playbackController.ts` / `StageShell.tsx`; M5 closed.**
- [x] Cold-start rehydration of playing rooms from Postgres (Slice 3 review M4). **Landed in
      `RoomRehydrator.cs` + `RoomStateMapper.cs`, invoked from `ApplyDatabaseStartupAsync`; M4
      closed.**
- [x] Duplicate `ReportSongEnded` idempotency against the watchdog race. **Landed: one shared
      `queueItemId == CurrentQueueItemId` guard under the command lock.**
- [x] The Slice 4 integration/Playwright matrix below (authoritative position, ±300 ms
      self-correction, watchdog, duplicate-end protection). **Backend matrix landed: five Docker-gated
      integration tests plus the authority unit tests. The Playwright specs
      (`e2e/playback-authority.spec.ts` and the `e2e/youtube.spec.ts` drift assertion) are owned by
      the parallel web-lane clock-sync alignment fix and are not asserted green by this backend
      change.**

**Original scope, for the record**

Slice 3 had a queue and a small playback state machine. Slice 4 made playback authoritative and
synchronized, and closed the gap between what phones control and what the stage renders:
single-writer transitions persisted before broadcast; `Ping`/`Pong` with a min-RTT best-of-window
offset; one-time audio-anchor calibration; a duration watchdog; a duplicate-end guard; and rehydration of rooms,
queue sequence, and anchors from Postgres. What remains from the original planning is not Slice 4
scope but the Slice 3 review's M2/M3 and the ADR-005 governance gap — see the next section.

### Carried forward from the Slice 3 review

These are scheduled, not optional polish:

- **M2 — stage role is client-declared.** Any guest with the code can join as `"stage"`; fix before
  the Slice 6 public demo via a stage secret/one-time claim or one-stage-per-room binding. Needs an
  ADR because it changes the join contract.
- **M3 — rooms are never collected.** Every `/stage` load creates a room with no GC, close path, or
  rate limit, consuming the finite 279,841-code space. Pair with room-GC work (blueprint placed it
  in Slice 8) and land before Slice 6.
- **M4 — persistence is write-only.** **Status: CLOSED by Slice 4.**
  `RoomRehydrator`/`RoomStateMapper` restore open rooms, playback anchors, and the persisted queue
  sequence at startup when a database is configured.
- **M5 — phones mutate playback the stage ignores.** **Status: CLOSED by Slice 4.** Room playback
  takes over the stage: `playbackController.ts` loads library stems through the media API and
  follows the authority; `DemoTrackPicker` is only an idle fallback.
- **Minor backlog.** The 15 minors and 11 nits in
  [`docs/review/slice-3-review.md`](review/slice-3-review.md) (missing `QueueItemStateChanged` for
  the new playing item, no move/remove ownership checks, no reload auto-rejoin, duplicate
  `ParticipantJoined`, `IsSyntheticDemo` seeding, whole-queue persistence, `/healthz` format, enum
  parity, fresh token per rejoin, and the rest) remain carried forward; Slice 4 addressed the
  playback-related ones (`QueueItemStateChanged` now fires on the authority-driven current item, and
  whole-room state is persisted for rehydration), while the rest stay in the review backlog.
- **ADR-005 governance gap.** No Slice 3 ADR was written, and §4.3/§5.2 still disagree on whether
  `JoinRequest` carries a session token. Settle the token/reconnect contract (and record the
  Slice 3 decisions) before building identity-dependent features.
- **M6 — Postgres dev password is a committed literal.** `src/apps/Karaokay.AppHost/Program.cs`
  pins `postgres-password` to a hardcoded dev value so the persisted `karaokay-postgres-data`
  volume's credentials survive restarts (without this, AddPostgres regenerates a random password
  each run and the api's startup migrations fail auth — the "api Finished in Aspire" bug).
  Fine for localhost-only self-host dev; before Postgres is ever exposed beyond localhost
  (notably the Slice 6 Azure deploy), switch to a user-secrets-backed parameter instead
  (plain `AddParameter(name, secret: true)` with no literal value, value supplied via
  `dotnet user-secrets set` in the AppHost) and reinitialize the data volume with the new
  credentials.

### Slice 5 — zero-key YouTube search and `yt-dlp` downloads (completed early)

**Status: COMPLETE IN SCOPE, landed early in `324da8a`.** Search, VideoId validation, the bounded
download worker, cache hits, retries, progress streaming, persistence, REST/hub enqueue, the guest
`YouTubeTab`, and the stage video surface are present. Review majors M3–M5 and strict H.264/AAC
enforcement, the development-path cleanup, the CI requires-Docker gate, and DTO field-parity
contract tests have **landed** in a follow-up change. See §2.4 and the
[Slice 4 review](review/slice-4-review.md).

### Later slices

Slice 6 deploys to Azure free tiers. Slices 7–8 build the Android TV stage: a C++ Oboe engine, then
a SkiaSharp renderer consuming the same golden
decision fixtures as the web.

For full file lists, tests, and Definition-of-Done prompts, read [`docs/ARCHITECTURE.md` §7](ARCHITECTURE.md#7-phased-vertical-slice-implementation-roadmap). For the deep Slice 1 explanation, read [`docs/learning/slice-1-walkthrough.md`](learning/slice-1-walkthrough.md); for Slice 3, read [`docs/learning/slice-3-walkthrough.md`](learning/slice-3-walkthrough.md).

---

## 8. Frequently confusing points

**Why two stems instead of one mixed track?** Karaoke needs a guide-vocal slider. Two files let the browser crossfade the vocal bus from 0 to 1 without re-encoding or restarting. A single mix would bake the vocal in permanently. The live microphone bus arrives in Slice 7.

**Why not just use `<audio src="song.opus">`?** An `<audio>` element gives one volume control and no sample-accurate multi-bus mixing. The stage needs independent instrumental and guide buses, a master bus, a testable clock, and a `<video>` routed into the same graph (now provided by the early Slice 5 video surface). Web Audio provides that graph.

**What is an `.opus` file?** Opus is an open, royalty-free audio codec for speech and music over the internet. It gives good quality at low bitrates and is supported by modern browsers. These files are 48 kHz stereo, encoded by FFmpeg's `libopus`.

**Why range requests if the demo songs are small?** Real imported songs are minutes long. Ranges let playback start early, seeking avoid re-downloading, and dropped connections resume. Building it now keeps the API contract stable when real libraries arrive.

**Why ETag and `immutable` caching?** A song id always maps to the same bytes because imports are not edited in place. Caching avoids re-downloading megabytes on every visit. The ETag is the correctness backstop: if length or modification time changes, the fingerprint changes.

**Why does the stage still bundle demo tracks?** Room playback does call the API: when a room is
present, `playbackController.ts` loads library stems and lyrics through `songMedia.ts`
(`/api/songs/{id}/instrumental|vocals|lyrics`). The bundled `public/demo-tracks/` copies remain for
the idle `DemoTrackPicker` fallback, which keeps `npm run dev` usable with one server and lets the
database-less Playwright specs run without a seeded library. Room playback is authoritative; the
local picker is explicitly what happens when there is no room playback to follow.

**Why was Slice 4 completed after a `slice-4` commit had already landed?** Commit `324da8a` is
titled "slice-4" but contains the documented Slice 5 YouTube pipeline. [ADR-007](adr/007-slice-4-5-deliverable-ordering.md)
kept the original roadmap unchanged and credited YouTube as completed early, leaving the documented
Slice 4 open. The authority, watchdog, authoritative stage playback, cold-start rehydration, and
duplicate-end idempotency then landed, with the implementation choices in
[ADR-008](adr/008-slice-4-playback-authority-decisions.md). Slice 4 is now complete; Slice 3 review
M2 (client-declared stage role) and M3 (no room GC) remain open for the Slice 6 demo.

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
| **EF Core** | Entity Framework Core, Microsoft's ORM for .NET. Used since Slice 3 for songs, rooms, and queues; download jobs arrived with the early Slice 5 work. |
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
- [`docs/learning/slice-2-walkthrough.md`](learning/slice-2-walkthrough.md) — the preserved post-Slice-2 project tour, now a learning snapshot with relocated links.
- [`docs/learning/slice-3-walkthrough.md`](learning/slice-3-walkthrough.md) — the deep Slice 3 guide: rooms, the queue engine, EF Core, the M1 story, and the phone remote.
- [`docs/learning/slice-4-walkthrough.md`](learning/slice-4-walkthrough.md) — the deep Slice 4 guide: the playback authority, count-in and watchdog, clock sync, authoritative stage playback, and rehydration.
- [`docs/learning/slice-5-walkthrough.md`](learning/slice-5-walkthrough.md) — the deep Slice 5 guide: zero-key yt-dlp search, the download worker and cache, the guest YouTube tab, and the stage video surface.
- [`docs/review/slice-1-review.md`](review/slice-1-review.md) — how Slice 1 was reviewed and fixed.
- [`docs/review/slice-2-review.md`](review/slice-2-review.md) — the browser-stage review and its nit backlog.
- [`docs/review/slice-3-review.md`](review/slice-3-review.md) — the rooms/queue/remote review, the M1 fix, and the M2–M5 schedule.
- [`docs/review/slice-4-review.md`](review/slice-4-review.md) — the early-YouTube/partial-Slice-4 review, the fixes that landed in its follow-ups, and the Slice 4 completion record.
- [`docs/adr/`](adr/) — the eight decision records summarized in [§3.8](#38-docs--the-written-memory-of-the-project).
- [`src/apps/Karaokay.Api/Program.cs`](../src/apps/Karaokay.Api/Program.cs) — the smallest entry point; a good first read.
- [`src/apps/Karaokay.Api/Hubs/StageHub.cs`](../src/apps/Karaokay.Api/Hubs/StageHub.cs) — the real-time entry point; read beside the TypeScript `useStageClient`.
- [`src/clients/web/src/features/stage/StageShell.tsx`](../src/clients/web/src/features/stage/StageShell.tsx) — the phase machine tying UI, audio, and renderer together.

A good first afternoon: run `dotnet build` and `dotnet test`, then `npm ci && npm test` in the web client. Start the API and the web dev server, open `/stage`, click the gate, and watch Twinkle Variations sweep by. Then open `/remote` with the stage's room code in a phone-sized window, queue a song, and watch both surfaces update. Trace one pick through `StageShell.tsx`; that path touches almost every idea in this document, and the queue path touches the rest.
