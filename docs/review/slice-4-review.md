# Slice 4 Review — YouTube Ingestion Landed Early; Playback Authority Still Owed

- **Date:** 2026-11-25
- **Scope:** Commit `324da8a` ("feat(slice-4): YouTube search, yt-dlp download worker, stage
  video mode & guest YouTube UI"), reviewed against
  [ARCHITECTURE.md](../ARCHITECTURE.md) v1.2 §5.1, §5.3, and §7 (Slice 4 and Slice 5),
  [ADR-001](../adr/001-single-replica-signalr-azure.md),
  [ADR-007](../adr/007-slice-4-5-deliverable-ordering.md), and the
  [slice-3 review](slice-3-review.md) (M4/M5)
- **Follow-up:** two changes landed after the reviewed commit: this documentation record (the
  review, ADR-007, and the PROJECT_STATE correction) and a separate code follow-up. The code
  follow-up landed majors **M3–M5**, strict H.264/AAC enforcement, the development-path cleanup,
  the CI requires-Docker gate, and DTO field-parity contract tests; the remaining minor/nit backlog
  stays open.

## a) Verdicts

**Pass 1 — static audit — SHIP-WITH-MAJORS.** The delivered code is a coherent, well-tested
implementation of the documented **Slice 5** (zero-key YouTube search, `yt-dlp` worker, cache,
progress streaming, guest UI, and stage video surface). It is not the documented **Slice 4**.
At `324da8a`, `PlaybackAuthority`, the watchdog, `Ping`/`Pong` clock offset filtering,
authoritative stage playback, cold-start rehydration, and the duplicate-end race the blueprint
describes were absent. (The clock-offset filter has since landed in the follow-up; see sections b
and c.) The commit title mislabels the deliverable, and the state documents were left describing a
different shipped feature set.

**Pass 2 — execution matrix on `324da8a` — SHIP-WITH-MAJORS.** The delivered Slice 5 surface is
mergeable subject to the hardening that has since landed in section b. The Slice 4 definition of
done is **not met**; see section c.

Verified-good outcomes measured at `324da8a`:

- `dotnet build karaokay.sln` — 0 warnings, 0 errors.
- `dotnet test` — **131 unit + 11 architecture + 29 integration passing**; the remaining **22
  Docker-gated integration tests skipped** locally.
- Vitest — **78 passed**.
- Playwright — **9 passed**.
- Generated OpenAPI artifacts — regenerated and **byte-identical**; no drift.
- SignalR parity — **exact**: 15 client→server methods, 14 server→client methods, with enum names
  and values matching.
- Binary-media guard — clean.

## b) Follow-up fixes (landed after this documentation change)

These landed in the follow-up change. They stay recorded here so the review trail shows what was
owed and where each fix lives.

| # | Fix | Evidence | Status |
|---:|---|---|---|
| M3 | Video sync must use a server-clock offset (Slice 4 `Ping`/`Pong` + min-RTT best-of-window filter) instead of the local wall clock, or state clearly that it is unsynchronized. | `src/clients/web/src/features/stage/videoSync.ts:52-76`; `src/clients/web/src/features/stage/useStageVideo.ts:161-196` | **Landed.** |
| M4 | Download-channel backpressure must not block HTTP/hub request paths; decouple admission from the bounded `Channel` and surface a clean busy/queue response. | `src/apps/Karaokay.Api/Features/YouTubeIngestionService.cs:167-175,230-244,333-358,399-405`; `src/apps/Karaokay.Api/Endpoints/YouTubeRateLimiting.cs` | **Landed.** |
| M5 | `/api/youtube/search` and `/api/youtube/enqueue` need authentication/authorization, rate limiting, and a process cap; the REST enqueue currently passes `sessionTokenId: null`. | `src/apps/Karaokay.Api/Endpoints/YouTubeEndpoints.cs:98-118,141-166`; `src/apps/Karaokay.Api/Endpoints/YouTubeRateLimiting.cs:21-25,52-80` | **Landed.** |
| Format | Enforce H.264/AAC with strict format filters and an `ffprobe` assertion; `-S vcodec:h264,acodec:aac` is currently a sort preference with fallbacks. | `src/core/Karaokay.Infrastructure/MediaIngest/YtDlpCommandBuilder.cs:69-73`; `src/core/Karaokay.Infrastructure/MediaIngest/YtDlpClient.cs:167-186` | **Landed.** |
| Paths | Remove machine-specific absolute `yt-dlp`/Node/FFmpeg paths from development settings. | `src/apps/Karaokay.Api/appsettings.Development.json:10-16` | **Landed.** |
| CI gate | Add a require-Docker switch so the Docker-gated integration tests fail instead of silently skipping when CI's container runtime is unavailable. | Integration fixtures | **Landed.** |
| Contract test | Extend `SignalRContractTests` from method/enum parity to DTO field-shape parity. | `src/tests/Karaokay.UnitTests/SignalRContractTests.cs` | **Landed.** |

## c) Majors — verdict and disposition

| # | Finding | Status | Direction |
|---:|---|---|---|
| M1 | **Scope mislabel.** Commit `324da8a` says Slice 4 but implements documented Slice 5. | **Resolved by this documentation change + [ADR-007](../adr/007-slice-4-5-deliverable-ordering.md).** | Keep the original spec; credit YouTube as early Slice 5; mark Slice 4 partial/open. |
| M2 | **Slice 4 definition of done not met.** At `324da8a`: no `PlaybackAuthority`/watchdog, no `Ping`/`Pong` + offset filter, no authoritative stage playback, no cold-start rehydration, no duplicate-end idempotency against the watchdog. The offset filter has since landed (M3); the rest stays open. Slice 3 review **M4** and **M5** remain open. | **Open — clock sync landed; remainder planned next.** | Implement the unchanged Slice 4 scope; do not call the slice complete. |
| M3 | **Video sync depends on unsynchronized wall clocks.** `expectedPositionMs` extrapolates `PositionMs + (Date.now() − TimeAnchorUtc)` with no server-offset correction; `useStageVideo` feeds it `Date.now()`. On a client whose clock is off by seconds, the hard-nudge path chases a permanently wrong target. | **Landed in follow-up.** | Consume the Slice 4 `Ping`/`Pong` offset and anchor calculation, or disable nudge corrections when no offset is known. `videoSync.ts:52-76`, `useStageVideo.ts:161-196`. |
| M4 | **Bounded download channel blocks request paths.** Capacity 4 with `BoundedChannelFullMode.Wait` means `EnqueueAsync` can await indefinitely from a REST request or hub invocation. | **Landed in follow-up.** | Use `TryWrite`/timeout with a clean busy response, or an unbounded admission queue with a separate bounded worker. `YouTubeIngestionService.cs:286`, `YouTubeEndpoints.cs:94`, `StageHub.cs:182-189`. |
| M5 | **Unauthenticated, unthrottled, uncapped `yt-dlp` spawning.** REST search and enqueue are open; each enqueue can start a download process; the REST path records `sessionTokenId: null`. | **Landed in follow-up.** | Require identity or a feature gate, rate-limit per source, cap concurrent/total jobs, and bind the queue row to the caller's token. `YouTubeEndpoints.cs:100`. |
| Format | **H.264/AAC is a preference, not an enforced contract.** `-S` sorts codecs but allows fallbacks; no `ffprobe` verification of the produced MP4. | **Landed in follow-up.** | Add strict format filters and assert H.264 video + AAC audio in MP4 before promoting the job to `Ready`. `YtDlpCommandBuilder.cs:69-73`. |

## d) Minor backlog — not being fixed in this change

Ordered by rough priority. Item 1 landed in the follow-up change; items 2–16 remain open or
partially landed.

| # | Finding | File | Status / suggested fix |
|---:|---|---|---|
| 1 | Machine-specific absolute paths in development settings (`yt-dlp`, Node, FFmpeg). | `src/apps/Karaokay.Api/appsettings.Development.json:10-16` | **Landed in follow-up.** Use `PATH` lookup or documented environment overrides. |
| 2 | Hub invocations pass `CancellationToken.None`, so a disconnecting client cannot cancel queue/playback work. | `src/apps/Karaokay.Api/Hubs/StageHub.cs:141,210,222,244,251,258,265,272,282,289` | **Partially landed.** `SearchYouTube` and `EnqueueYouTubeSong` now pass `Context.ConnectionAborted`; the listed queue/playback calls still pass `CancellationToken.None`. |
| 3 | `MediaDownloadWorker.ExecuteAsync` rethrows on host stop without completing the in-flight ticket, leaving `Downloading` rows/targets behind. | `src/apps/Karaokay.Api/Features/MediaDownloadWorker.cs:76-78,151-153` | **Open.** Complete or fail the ticket in a cancellation/finally path before rethrowing. |
| 4 | Unbounded progress channel(s) can grow if the pump falls behind a fast producer. | `src/apps/Karaokay.Api/Features/MediaDownloadWorker.cs:183,532` | **Open.** Bound the channel and drop/coalesce stale progress samples. |
| 5 | The `finally` block can mask the original `yt-dlp` exception when the progress pump also faults. | `src/apps/Karaokay.Api/Features/MediaDownloadWorker.cs:203-207` | **Open.** Await the pump without replacing the primary exception, or aggregate. |
| 6 | No playability verification of the downloaded file; `FindDownloadedFile` accepts a non-MP4 largest candidate. | `src/core/Karaokay.Infrastructure/MediaIngest/YtDlpClient.cs:167-186` | **Open.** The format fix adds a targeted `ffprobe` assertion but not a general playability probe; full probe remains backlog. |
| 7 | `File.Move` assumes the cache and staging directories are on the same volume. | `src/apps/Karaokay.Api/Features/MediaDownloadWorker.cs:141` | **Open.** Fall back to copy-then-delete when `IOException` indicates cross-volume. |
| 8 | Partial success can leave some queue rows `Downloading` when a multi-target completion loop throws partway. | `src/apps/Karaokay.Api/Features/MediaDownloadWorker.cs:269-325` | **Open.** Persist per-target state independently and fail remaining targets on error. |
| 9 | `ResolveSongAsync` persists the `Song` before queue admission; a rejected enqueue leaves an orphan song row. | `src/apps/Karaokay.Api/Features/YouTubeIngestionService.cs:124,162-165` (song written at `:313-330`) | **Open.** Create the song inside the same transaction as the queue row, or delete it on rejection. |
| 10 | REST enqueue trusts caller-supplied `Title`, `Channel`, `DurationSeconds`, and `ThumbnailUrl`. | `src/core/Karaokay.Contracts/YouTubeHttpDtos.cs:12-15` | **Open.** Re-resolve metadata from the video id or validate against the search result. |
| 11 | Stale roadmap/state claims (YouTube unbuilt, `DownloadJob` planned, Slice 4 next, old test counts). | `docs/PROJECT_STATE.md` | **Resolved by this documentation change.** |
| 12 | **MIN-1 — the global 16 pending-download cap is unreachable.** `DownloadJobChannel.Capacity` is 4, so the channel rejects before `MaxPendingDownloadsGlobal` can bind; channel capacity is the effective global bound. | `src/apps/Karaokay.Api/Features/YouTubeIngestionService.cs:43,357-379`; `src/core/Karaokay.Application/Downloads/DownloadJobChannel.cs:14` | **Open.** Raise `DownloadJobChannel.Capacity` or document the effective global bound. |
| 13 | **MIN-2 — an ffprobe process failure burns the retry budget as a non-retryable mismatch.** `ExitCode != 0` or unparseable output maps to `Mismatch`, which the worker treats as deterministic. | `src/core/Karaokay.Infrastructure/MediaIngest/MediaCodecProbe.cs:97-102`; `src/apps/Karaokay.Api/Features/MediaDownloadWorker.cs:160-165` | **Open.** Add a distinct retryable `ProbeFailed` outcome. |
| 14 | **MIN-3 — the probe resolves ffprobe from `PATH` only.** `YtDlp.FfmpegLocation` is ignored, so hosts with a custom FFmpeg location silently lose codec enforcement. | `src/core/Karaokay.Infrastructure/MediaIngest/MediaCodecProbe.cs:51`; `src/apps/Karaokay.Api/Program.cs:30` | **Open.** Prefer `YtDlp.FfmpegLocation` when resolving ffprobe. |
| 15 | **MIN-4 — the enqueue bucket is one 4/min bucket per IP for a whole NAT'd party.** `QueueFull` rejections still consume permits. | `src/apps/Karaokay.Api/Endpoints/YouTubeRateLimiting.cs:25` | **Open.** Consider 20/min and/or partitioning by session token as well. |
| 16 | **MIN-5 — hub `SearchYouTube` remains unthrottled yt-dlp spawning.** The REST rate limiter covers only `/api/youtube`. | `src/apps/Karaokay.Api/Hubs/StageHub.cs:108-135` | **Open.** Add a per-room/connection search budget or document the residual risk. |

## e) Nits — backlog

- Double seek in the paused hard-nudge path: `useStageVideo.ts:193-195` seeks again after the earlier
  `:165-167` branch already did.
- Unbounded `reportedEndsRef`, `youtubeSongIds`, and `rememberedRequests` growth in the stage/remote
  reducers; prune by queue item id when history is removed.
- `retryYouTubeSong` leaves dead `Failed` rows behind, and per-item retry state never resets.
- `SearchYouTube` maps upstream failures to `InvalidRequest` (`StageHub.cs:126-130`); use a distinct
  upstream/download-unavailable code.
- `YouTubeEndpoints` returns `YouTubeServiceException.Message`, including the `yt-dlp` stderr tail
  (`:55`, `:143`); log the detail server-side and return a generic client message.
- `SongCatalog` accepts an optional `IOptions<YtDlpOptions>? = null` constructor parameter; make the
  dependency explicit or remove the null branch.
- `YtDlpClient` accumulates stdout/stderr in unbounded `StringBuilder`s (`:121-138`); cap the
  retained tail.
- Playwright `runStamp` temp directories are never removed, and `reuseExistingServer: !CI` can reuse
  a stale server across runs.
- `RoomRegistry.FindByQueueItemId` scans every room; index by queue item if the hot path grows.
- `DownloadJob.VideoId` is `varchar(64)` for an 11-character id; tighten to 11 or document the
  headroom.
- `YouTubeCachePaths.IsUnder` uses `StringComparison.Ordinal`; Windows path checks should be
  case-insensitive.
- Redundant job-row delete: `TryQueueJobAsync` already deletes on a rejected write
  (`YouTubeIngestionService.cs:428`), and the queue-full branch deletes again (`:240`). Keep one
  best-effort delete.
- `searchYouTube`'s `sessionToken` parameter has no caller, and `GET /api/youtube/search` never
  reads the header (`src/clients/web/src/lib/api/youtube.ts:170-187`). Wire it deliberately or drop
  it.
- `YouTubeEndpoints` creates an `ILogger` per request via `loggerFactory.CreateLogger`
  (`YouTubeEndpoints.cs:73,101`); inject `ILogger<YouTubeEndpoints>` instead.
- `clockOffsetMs` state re-renders `StageShell` on every clock sample even though no consumer reads
  it (`useStageRoom.ts:62,134-136`); keep it in a ref or remove it.
- `clockSync.ts:239-241` can return from `tick` without rescheduling when a same-generation exchange
  is already in flight; confirm the in-flight path always schedules the next tick.
- `PROJECT_STATE.md:179` still describes the stage video sync as local-clock-driven in the present
  tense even though the follow-up landed the §5.3 offset filter; update or scope that sentence.
- `PROJECT_STATE.md` §2.4 does not mention the new per-IP rate limits or the admission caps.
- Cached files bypass the codec assertion (`MediaDownloadWorker.cs:105-116`), so a VP9 cache entry
  written before format enforcement remains servable; consider re-probing or invalidating the cache.
- The rate limiter's partition dictionary grows per distinct IP with no eviction
  (`YouTubeRateLimiting.cs:72-80`); acceptable now, but note it for the Slice 6a public demo.

## f) Verification blind spots

- **The Docker gate materialized as a real CI regression.** CI run #11 was already red on `324da8a`
  (before the M3–M5 follow-ups), and run #12 reproduced the same two failures on the current tree:
  `HubSearchFailureSurfacesAHubErrorInsteadOfFaulting` and
  `EnqueueRejectsAnInvalidVideoIdBeforeSchedulingADownload` failed the first time CI actually executed
  them. Both had always been Docker-skipped locally. The causes were test-harness bugs, not product
  regressions: the fake search passed `maxResults: 0` into a mapper that returns early for a
  non-positive limit, so the malformed JSON was never parsed and no `HubError` was pushed; and the
  "invalid" video id was eleven hyphens, which `VideoIdValidator` correctly accepts as URL-safe
  base64. The fake now mirrors the real client's positive-limit clamp, the enqueue test uses a
  genuinely invalid id (`abc;defghij`), and both tests now run locally against a DB-less
  `WebApplicationFactory` host because neither path needs PostgreSQL.
- **Docker-gated tests can no longer skip silently in CI.** 23 integration tests still require
  Docker and skip locally without it; the fixtures record a skip reason rather than failing. The
  require-Docker switch landed in section b, so a missing container runtime now fails the CI job.
- **Admission-control unit tests use the no-database seam.** `YouTubeIngestionServiceTests` runs
  without a `KaraokayDbContext`, so its "no DownloadJobs row persisted" assertion means "no
  database seam and no live ticket", not a literal row-count check against Postgres. The
  Docker-gated integration suite covers the persisted queue-row side.
- **SignalR contract tests now cover DTO field shapes.** The follow-up extended them beyond
  method-name and enum parity, so a renamed or missing DTO property fails the contract test.
- **Three dead contract entries.** `CountdownTicked`, `LyricsLoaded`, and `SearchCompleted` are
  declared on `IStageHubClient` but are never sent by the server or handled by the clients. They
  imply features that do not exist.
- **Guest YouTube flow is REST-only.** `YouTubeTab` calls `/api/youtube`, so the hub
  `SearchYouTube`/`EnqueueYouTubeSong` methods are covered only by integration tests; the primary
  guest path does not exercise them end-to-end.

## g) Carry-forward

The remaining Slice 4 work is not optional and is planned next: `PlaybackAuthority` + watchdog,
authoritative stage playback (M5), cold-start rehydration (M4), and duplicate-end idempotency. The
`Ping`/`Pong` + min-RTT best-of-window offset filter has landed for the web stage video path (M3), but the
authoritative stage audio calibration and phone countdown rings in §5.3 still depend on that
remaining work. Slice 3 review M4/M5 stay open until those land.

The follow-up majors (M3–M5, strict format enforcement, development paths, CI Docker gating, DTO
parity) landed with tests. The minor/nit backlog in sections d and e is carried forward rather than
rediscovered; none of it blocks the documentation correction in this change.

## h) Follow-up #2 — Slice 4 completion (2026-11-25)

The backlog §g called "not optional and planned next" has landed. This section records the
completion and supersedes §g's open-status statements; the earlier sections stay as written so the
review trail shows what was owed.

| Previously open item | Where it landed | Status |
|---|---|---|
| `PlaybackAuthority` + duration watchdog | `src/apps/Karaokay.Api/Features/PlaybackAuthority.cs`, `Features/PlaybackTiming.cs`; registered as a singleton hosted service in `Program.cs` | **Landed.** Single writer over a bounded channel (256, wait, single reader) of `RoomCommand` + `TaskCompletionSource`; transitions persist before broadcast under the room lock; 100 ms watchdog ends a `Playing` room at `PlaybackStartedAtUtc + DurationMs + 5 s`. |
| Duplicate-end idempotency against the watchdog race | `PlaybackAuthority.cs` (shared `queueItemId == CurrentQueueItemId` guard under the command lock) | **Landed.** A duplicate stage report and a post-report watchdog firing cannot double-advance. |
| Authority count-in and phone countdown ring (`CountdownTicked`) | `Features/PlaybackTiming.cs`, `PlaybackAuthority.cs`; `src/clients/web/src/lib/signalr/roomState.ts` (`applyCountdownTick`), `features/remote/PlaybackControls.tsx` (SVG ring) | **Landed.** Four 500 ms beats, 4 → 1, then `Playing` with a fresh anchor. |
| Authoritative stage playback wiring (Slice 3 review **M5**) | `src/clients/web/src/features/stage/playbackController.ts`, `songMedia.ts`, `StageShell.tsx`; `WebAudioEngine.pause()` | **Landed.** Room playback takes over library audio; one-time `seekOffset = expectedMs − audioPositionMs`, audio as ground truth; `DemoTrackPicker` is an idle fallback only. M5 is closed. |
| Cold-start rehydration (Slice 3 review **M4**) | `Features/RoomRehydrator.cs`, `Features/RoomStateMapper.cs`, `Program.ApplyDatabaseStartupAsync`; migration `20261125160000_AddRoomQueueSequence`; `RoomRegistry.RestoreQueueSequence` | **Landed.** Open rooms, ordered queue, anchors, and a write-ahead-minted `PartyRoom.QueueSequence` restored; dangling cursor → `Idle`; stale duplicate `Playing` rows demoted. M4 is closed. |
| Clock sync used by authoritative playback | `src/clients/web/src/lib/signalr/clockSync.ts` | **Already landed in the first follow-up**; now consumed by `playbackController.ts` for start alignment and mid-song joins. |
| Guide-vocal slider through the hub | `StageHub.SetGuideVocalVolume` → authority; `useStageRoom.setGuideVocalVolume` | **Landed.** |

**Final verification on the completed tree.** `dotnet build` Debug and Release: **0 warnings**.
.NET after the post-review backend fixes: **197 unit + 11 architecture + 31 integration passing**
(239 passed of 267), with **28 Docker-gated integration tests skipped locally** (Docker is
unavailable on this machine; CI runs them). The five Slice 4 integration tests cover the two-phase
Start broadcast and tick contract, ±30 ms pause/resume, the watchdog, duplicate-end, and
cold-restart rehydration. Web: Vitest **133 tests / 16 files** passing. The Playwright lane —
`e2e/playback-authority.spec.ts` and the `e2e/youtube.spec.ts` drift assertion included — is owned
by the parallel web-client clock-sync alignment fix; it is **not claimed green by this backend
change**, and these backend fixes plus that alignment fix are what make the lane reproducible.

**Still open (not claimed by this follow-up).**

- Slice 3 review **M2** — the stage role is client-declared; needs an ADR before the Slice 6a public
  demo.
- Slice 3 review **M3** — rooms are never garbage-collected.
- The `clockSync.ts:239-241` nit from §e. Checked on the current tree: `tick()` still returns when
  `inFlightGeneration === generation` without rescheduling, so the nit **remains open**. (It is
  hard to reach in practice because only one tick is scheduled at a time, but the described path is
  unchanged.)
- The minor/nit backlog in sections d and e, except for the corrected blind-spot item below.
- §f's "three dead contract entries" has shrunk to two: `CountdownTicked` is now sent by the
  server and handled by both the stage and the phone; `LyricsLoaded` and `SearchCompleted` remain
  declared but unsent.
- Slices 6–8.

The implementation choices behind these files are recorded in
[ADR-008](../adr/008-slice-4-playback-authority-decisions.md); the blueprint's Slice 4 file list now
points at `lib/signalr/clockSync.ts` and `useStageVideo.ts` as well.

**Addendum (2026-11-25, post-audit).** Two fixes landed after the table above was written:

1. **Removing the current item now persists the playback reset before it broadcasts.**
   `QueueService.RemoveQueueItemAsync` clears the cursor and returns the room to `Idle` when the
   removed entry is current — a playback mutation — so that branch mints the queue sequence and
   writes the playback reset and the renumbered queue **in one `SaveChanges` transaction** before
   fanning out `QueueChanged` and the playback broadcast, matching the write-ahead ordering in §5.1
   rule 6. (The first version called `PersistRoomAsync` then `PersistQueueAsync`; the later backend
   review combined them into `PersistRoomStateAsync` so a crash between the two is impossible.)
   Pinned by `RemovingTheCurrentItemPersistsTheIdlePlaybackReset` in
   `src/tests/Karaokay.UnitTests/QueueServiceTests.cs`; it runs on the EF InMemory harness double
   and re-reads the row through a fresh `AsNoTracking` query, so the assertions prove `SaveChanges`
   durability rather than in-memory state.
2. **`RemoveQueueItem` on the hub routes through the authority.** `StageHub.RemoveQueueItem` still
   does the membership/role check itself, but dispatches `RoomCommandTypes.RemoveQueueItem` over the
   `PlaybackAuthority` command channel instead of calling `QueueService` directly, so the single
   writer owns the removal and any reset it triggers (§5.1 rule 6). A missing item still surfaces
   the `NotFound` hub error, now derived from the command result.

**Addendum #2 (2026-11-25, backend review fixes).** A second review pass over the Slice 4 commit
found backend and documentation gaps; this change closes them:

| Finding | Fix | Status |
|---|---|---|
| `StartPlayback` on a `Downloading` head yielded `Idle`, contradicting §5.1 rule 3 and the ADR/PROJECT_STATE claims. | `StartPlaybackAsync` now parks in `AwaitingDownload`; `PlaybackAuthority.TickRoomAsync` begins the count-in when the exact cursor item becomes `Ready` (guarded on status + item), and a failed awaited download returns to `Idle`. Pinned by new `PlaybackAuthorityTests` and `QueueServiceTests` cases. | **Landed.** |
| Removal persisted playback (`PersistRoomAsync`) and queue (`PersistQueueAsync`) in two separate `SaveChanges`, leaving a crash window with the room `Idle` while the row was still queued. | Both methods were replaced by one `PersistRoomStateAsync(playback, queue)` that uses one scope and one `SaveChangesAsync`; removal, Start/advance, and other transitions that touch both slices now commit atomically. | **Landed.** |
| `CountdownTicked` was awaited inside `CommandLock` at a 500 ms cadence, so a slow client blocked Pause/Skip for the count-in. | Beats and the in-memory projection are computed under the lock; the fan-out runs after `Release()`. | **Landed.** |
| Removing the current item during `Countdown` reset status/anchor/position but left `CountdownRemainingBeats` (and the restore flag) stale. | The removal branch resets both. Pinned by `RemovingTheCountingInItemResetsTheCountdownState`. | **Landed.** |
| A cold restart mid-count-in started a fresh cursor at beat 4, re-emitting beats phones already saw. | `RoomState.CountdownAwaitingResume` marks a restored count-in; the authority resumes at the next due beat (derived from the persisted anchor), and `RoomStateMapper` derives the live remaining-beat value at startup. Pinned by `ColdRestartMidCountInDoesNotReplayAlreadyBroadcastBeats` and the mapper test. | **Landed.** |
| `PlaybackAuthority.EnqueueAsync` awaited `WriteAsync` with `CancellationToken.None`, so a consumed-stalled (full) channel could hang the hub invocation. | A 2 s `CommandWriteTimeout` (injectable internal seam) fails the invocation with `TimeoutException`. Pinned by `EnqueueFailsFastWhenTheCommandChannelIsFull`. | **Landed.** |
| `PlaybackLifecycleStartsPausesResumesSkipsAndIdles` read a snapshot immediately after Start and required `Countdown`; on slow CI the 2 s count-in could already be `Playing`. | It now waits on the `PlaybackCollector` `WaitForAsync(Countdown)` broadcast, mirroring the other Slice 4 test. | **Landed.** |

The E2E lane is **not** part of this verification: the web client's clock-sync alignment fix is
landing in parallel (owned by the frontend change), and only that fix plus these backend fixes make
`e2e/playback-authority.spec.ts` and the `e2e/youtube.spec.ts` drift assertion reproducible. This
review does not claim Playwright green.

Local verification precondition: `src/clients/web/playwright.config.ts` now retries once locally
(`retries: process.env.CI ? 2 : 1`) rather than not at all. The first cold run under 8 parallel
workers starves the four timing-sensitive specs — the decode poll, the two position-progression
polls, and the YouTube drift `beforeMs` assertion — and with zero local retries that baseline flake
failed the lane instead of recovering on a warm second attempt. CI's value of 2 is unchanged; the
specs and their assertions are untouched.
