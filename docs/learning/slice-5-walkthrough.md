# Slice 5 Code Walkthrough — zero-key YouTube search, downloads, and the stage video surface

This is a guided read of Slice 5: the work that let a party queue a YouTube karaoke video without a
YouTube API key, without a paid service, and without putting copyrighted media in the repository. It
is written for the repo owner as a junior-developer tour: every term is defined where it first
matters, the code claims are tied to real files, and the last sections collect the questions that are
easy to ask twice.

One fact has to come first because it confuses everything else: **this slice shipped early, in a
commit titled `feat(slice-4)`.** Commit `324da8a` is called "feat(slice-4): YouTube search, yt-dlp
download worker, stage video mode & guest YouTube UI", but its contents are the roadmap's Slice 5.
The documented Slice 4 — the single-writer playback authority, watchdog, and clock sync — landed
afterwards. [ADR-007](../adr/007-slice-4-5-deliverable-ordering.md) records the correction and keeps
the original roadmap intact. §1.4 tells that story in full, and the walkthrough keeps the roadmap's
numbering (this is Slice 5) rather than the commit's.

Read this beside the preserved snapshots of earlier work:

- [`slice-1-walkthrough.md`](slice-1-walkthrough.md) — MIDI, `lyrics.json`, FFmpeg, and the importer.
- [`slice-2-walkthrough.md`](slice-2-walkthrough.md) — media endpoints, Web Audio, and the Canvas
  renderer as the project stood after Slice 2.
- [`slice-3-walkthrough.md`](slice-3-walkthrough.md) — rooms, the queue engine, EF Core persistence,
  and the phone remote. This slice reuses all of it.
- [`slice-4-walkthrough.md`](slice-4-walkthrough.md) — the companion read for the playback-authority
  slice: `PlaybackAuthority`, the count-in, the watchdog, and Ping/Pong clock sync. Slice 5's stage
  video consumes Slice 4's `serverNow`.
- [`../PROJECT_STATE.md`](../PROJECT_STATE.md) — the living project snapshot. If this walkthrough and
  that snapshot disagree, the snapshot plus the code win, and one of them should be fixed.
- [`../adr/007-slice-4-5-deliverable-ordering.md`](../adr/007-slice-4-5-deliverable-ordering.md) —
  why a Slice 5 deliverable lives in a commit labelled Slice 4.
- [`../adr/004-dual-source-media-pipeline.md`](../adr/004-dual-source-media-pipeline.md) — the
  dual-source design: multitrack stems and YouTube videos behind one queue and one stage.
- [`../review/slice-4-review.md`](../review/slice-4-review.md) — the review of this work. Read it
  beside this document for a simple reason: the YouTube work was reviewed under the Slice 4 label,
  because that is what the commit claimed. The hardening majors (server-clock video sync,
  request-path backpressure, rate limiting, strict H.264/AAC enforcement, DTO parity) and the open
  minor backlog are all recorded there, not in a separate "slice-5-review.md".

Test counts are quoted from [`../PROJECT_STATE.md`](../PROJECT_STATE.md) rather than re-counted here:
the current tree reports **197 unit + 11 architecture + 31 integration passing** (239 passed of 267),
with **28 Docker-gated integration tests skipped locally**, and **Vitest 133 tests in 16 files**. The
Playwright lane — including `e2e/youtube.spec.ts` — is not asserted green by the backend change that
closed Slice 4; at the reviewed commit `324da8a` it reported 9 passing.

---

## 1. What Slice 5 set out to solve

### 1.1 The problem: parties want songs you do not own

Slices 1–3 built a legal self-hosted library and a shared queue. That is enough only if the host
already owns every song someone will request. Real parties do not work that way: the first guest asks
for a specific karaoke video that is on YouTube and not in the library.

The cheap, obvious answer — call the YouTube Data API for search and download the result — runs into
two hard project constraints:

- **C1: $0/month, permanently.** No paid API keys, no expiring trials, no paid media service. The
  YouTube Data API needs a key with a quota and a billing relationship. That is out.
- **C2: no copyrighted media in Git.** Downloaded videos are third-party commercial content. They may
  exist on the host's disk after a guest asks for them; they may never be committed.

Slice 5's answer is **zero-key ingestion**: search and download through the `yt-dlp` command-line
program, which needs no API key, keep the resulting file in a local cache that Git ignores, and fold
the finished file into the existing queue and stage as if it were a library song.

### 1.2 The pipeline in one picture

```text
  phone: YouTube tab
    │  GET /api/youtube/search?query=...          no API key, no paid quota
    ▼
  YtDlpClient ── yt-dlp "ytsearch10:<query>" --flat-playlist --dump-single-json --skip-download
    │  typed YouTubeSearchResultDto[]
    │  POST /api/youtube/enqueue                  (or the hub's EnqueueYouTubeSong)
    ▼
  YouTubeIngestionService
    ├── VideoIdValidator  ^[A-Za-z0-9_-]{11}$
    ├── Songs.VideoId lookup ── row + file exist ──▶ queue row Ready (no download)
    ├── Song row + DownloadJob row (Queued)
    └── DownloadJobChannel (bounded capacity 4, non-blocking TryWrite)
              │
              ▼
  MediaDownloadWorker (BackgroundService)
    └── yt-dlp → .staging/{videoId}-{guid}/ → ffprobe → File.Move → ytcache/{videoId}.mp4
              │                                             │
              │  DownloadProgressChanged / SongReady / DownloadFailed
              ▼                                             ▼
        room SignalR group                       <video src="/api/songs/{id}/video">
                                                 range-enabled, 206 Partial Content
```

There are two independent halves. The **ingestion half** turns a search result into
`{videoId}.mp4` on disk and a `Ready` queue row. The **playback half** serves that file over HTTP and
drives a single `<video>` element from authoritative room state. Everything between them is the
existing Slice 3 queue and the Slice 4 playback authority.

### 1.3 The constraints that shaped the design

| Constraint | What it forbids | How Slice 5 answers it |
|---|---|---|
| **C1** — $0/month | Paid API keys, paid search, paid transcode | `yt-dlp` for search and download; FFmpeg is already required |
| **C2** — no media in Git | Committing downloads | Cache under a git-ignored directory; `.gitignore` also guards `*.mp4`, `*.mkv`, `*.webm` |
| **C3** — two deployment modes | Ingestion on the public demo | `Features:EnableYouTubeIngestion`, default `false`, `true` only in Development |
| Party latency | A guest waiting on a 30 s download | Enqueue returns immediately; worker streams progress; queue row becomes `Ready` |
| Browser compatibility | Arbitrary codecs in the cache | yt-dlp format filters **and** an FFprobe assertion for H.264 video + AAC audio |
| Unauthenticated surface | One guest exhausting the host | Per-IP fixed-window limits, per-room and global pending caps, bounded worker queue |

### 1.4 The ordering story: why a "slice-4" commit is Slice 5

This is the single most confusing fact about the slice, so it is worth stating precisely.

- `docs/ARCHITECTURE.md` v1.2 defines Slice 4 as **playback authority, clock sync, and browser video
  mode**, and Slice 5 as **zero-key YouTube search and `yt-dlp` downloads**.
- Commit `324da8a` implements the Slice 5 scope: search, the download worker, the `Song`/`DownloadJob`
  persistence, the YouTube REST endpoints, the hub methods, the guest tab, and the stage `<video>`
  surface. It implements none of the Slice 4 deliverables (no `PlaybackAuthority`, no watchdog, no
  `Ping`/`Pong` offset filter, no rehydration, no duplicate-end protection against a watchdog).
- [ADR-007](../adr/007-slice-4-5-deliverable-ordering.md) is a **delivery-order correction, not new
  architecture**. It keeps the roadmap unchanged, credits the YouTube work as the Slice 5 deliverable
  "completed early", and marks Slice 4 as still owed.
- The documented Slice 4 was then completed separately. Its choices are in
  [ADR-008](../adr/008-slice-4-playback-authority-decisions.md), and its completion record is the
  follow-up sections of the [Slice 4 review](../review/slice-4-review.md).

Why not just renumber? ADR-007's rejected alternatives answer it: renumbering would invalidate every
Slice 4/5 reference in the blueprint, reviews, and learning guides, and would hide the missed
deliverable behind a label change. Reverting the commit would discard tested work for a cosmetic
reason. The owner chose to write the mistake down instead.

The practical consequence for a reader: when you see "Slice 4 review" discussing YouTube throttling
or codec enforcement, that is this work. When it discusses `PlaybackAuthority`, that is the slice that
landed afterwards. Both are in the same record because that is where the review trail put them.

---

## 2. New concepts, defined once

### 2.1 `yt-dlp`, and why it is not the YouTube Data API

**`yt-dlp`** is an open-source command-line program that resolves and downloads media from YouTube and
similar sites. It speaks the same public web endpoints a browser does; it does not need an API key.
That is what makes it compatible with C1.

The important architectural point is that it is an **external process**, not a library. Karaokay runs
it with a list of arguments and reads its stdout. The program can change, fail, be missing, or hang;
all of that has to be handled at the process boundary.

**Zero-key** therefore means: no YouTube API key exists anywhere in configuration, no quota is
consumed, and search results come from `yt-dlp`'s `ytsearch` pseudo-URL rather than an HTTP API.

### 2.2 CliWrap, and why not `Process.Start`

**CliWrap** is a small .NET library that wraps `System.Diagnostics.Process`. You hand it an executable
and an argument list, and it gives you typed results, stdout/stderr pipes, cancellation, and
validation.

Why not `Process.Start` directly? Because building a command line as one string is where command
injection lives. `YtDlpClient` puts every value in its own argument-list slot and always places a
`--` end-of-options marker before the caller-controlled input. A query containing `--flag`, `;`, or
quotes is then one opaque argument, not syntax. CliWrap makes that style the default and lets tests
assert the exact argument array without starting a process.

### 2.3 `--flat-playlist` and `--dump-single-json`

A `ytsearch10:query` invocation is conceptually a playlist of ten search results.

- **`--flat-playlist`** tells yt-dlp not to resolve every entry fully; it returns the shallow list
  (id, title, channel, duration, thumbnails) instead of fetching each video page. That is much faster
  and is enough for a search result grid.
- **`--dump-single-json`** emits the whole playlist as one JSON document instead of one JSON object
  per line.
- **`--skip-download`** guarantees the search is metadata-only.
- **`--no-playlist`** prevents a URL that points into a playlist from expanding it.

The result is a document with an `entries` array. `YtDlpResultMapper` handles both that shape and a
single-video object, so a future non-playlist invocation does not break parsing.

### 2.4 Video ids and why exactly eleven characters

A YouTube video id is eleven characters from the **URL-safe base64** alphabet: `A–Z`, `a–z`, `0–9`,
`-`, and `_`. The standard base64 alphabet's `+` and `/` are replaced by `-` and `_` so an id can sit
in a URL path without escaping.

The validator is deliberately exact. `^[A-Za-z0-9_-]{11}$` rejects spaces, semicolons, quotes, `..`,
Unicode look-alikes, and any length other than eleven. Since the id is later placed in a process
argument and a filename, this is the security boundary, not a formatting preference.

### 2.5 Single-flight (coalescing)

**Single-flight** means that concurrent requests for the same expensive operation share one execution.
Here the key is the video id. If two guests enqueue the same video while it is downloading, the first
request creates a `DownloadJobTicket`; the second attaches its queue row to that ticket instead of
starting a second `yt-dlp`.

The ticket is an in-memory handle: it holds the video id, the song id, the attached queue rows
("targets"), the latest progress sample, and a `TaskCompletionSource` that completes once. One download
can therefore serve many queue rows, in one room or several.

### 2.6 Bounded channels and backpressure

A **channel** is .NET's async producer/consumer queue (`System.Threading.Channels`). A **bounded**
channel has a fixed capacity.

**Backpressure** is what happens when the producer is faster than the consumer. There are three
choices: block the producer, drop work, or reject it. This slice rejects it:

- `DownloadJobChannel.Capacity = 4`.
- The options say `FullMode = BoundedChannelFullMode.Wait`, which describes what a blocking
  `WriteAsync` would do when full.
- The application never calls `WriteAsync`. It calls `TryEnqueue`, which is `TryWrite`: it returns
  `false` immediately when the queue is full.

That mismatch is intentional and is the fix for a review major. A REST request must not sit awaiting a
download queue; a full queue becomes a clean `QueueFull` error, and the guest retries later. The
`Wait` mode remains relevant only if a future caller adds a blocking write.

### 2.7 Staging directory and atomic move

A **staging directory** is a temporary workspace for an in-progress file. yt-dlp writes
`.staging/{videoId}-{guid}/...`; the worker never lets a partial file appear under the cache path.

After the download and codec check, `File.Move(stagedFile, cacheFile, overwrite: true)` publishes the
file as `ytcache/{videoId}.mp4`. On the same filesystem, rename is atomic: a reader either sees no
file or sees the complete file. No `.part` and no half-written MP4 can be served.

The caveat, recorded as a review minor: if staging and cache live on different volumes, `File.Move`
throws. The worker catches it and retries the download, which burns the retry budget. The design
assumes one volume for staging and cache.

### 2.8 Transcode versus remux

**Transcoding** decodes and re-encodes media, changing the codec. **Remuxing** copies the compressed
streams into a new container without re-encoding. yt-dlp's `--merge-output-format mp4` is usually a
remux: it takes the chosen video and audio streams and puts them in MP4.

The persisted `DownloadStage.Transcoding` name is slightly broader than what happens: the worker has
already asked yt-dlp for an MP4 merge, and it uses this stage as the "codec verified, publishing"
step. The label is the pipeline's, not a claim that FFmpeg re-encoded every frame.

### 2.9 H.264/AAC, and why browser-safe codecs matter

**H.264** (also called AVC) is the video codec browsers and hardware decoders are most likely to
support without a software fallback. **AAC** is the matching audio codec in an MP4 container. A VP9 or
AV1 file may be smaller, but it is not guaranteed to hardware-decode in Safari, older Android TV boxes,
or the eventual Android stage.

The slice enforces this twice:

1. yt-dlp is asked to prefer `avc1` video and `mp4a` audio, at most 1080p, merged to MP4.
2. After the download, **FFprobe** inspects the actual streams. If the file is not H.264 **and** AAC,
   the job fails with a reason instead of being published.

Step 2 exists because step 1 is a preference with fallbacks: a source may have no AVC rendition, and
yt-dlp will then produce something else.

### 2.10 Cache identity and dedup by `videoId`

**Cache identity** is the key that says "this is the same media." For YouTube it is the video id.
`Song.VideoId` has a **filtered unique index**: unique where not null, so multitrack songs (null
`VideoId`) are unaffected, and two rows cannot claim the same video.

The cache file is named `{videoId}.mp4`. `Song.CachePath` stores a path relative to the configured
cache directory (normally just `{videoId}.mp4`), because the cache may live outside the media root. A
cache hit is checked twice: the stored `CachePath`, then the canonical `{videoId}.mp4`. If either
resolves to an existing file, enqueue returns `Ready` immediately and no worker job is created.

### 2.11 Feature flags

A **feature flag** is configuration read at runtime that turns a capability on or off without a
rebuild. `FeatureOptions.EnableYouTubeIngestion` lives under `Features` and defaults to `false` in
`appsettings.json`; `appsettings.Development.json` turns it on.

The flag gates the **entry points**, not the worker's existence: search and enqueue are refused, REST
returns `403`, and the hub pushes `HubError(YouTubeDisabled)`. Cached videos that already exist can
still be served by the media endpoints, which is deliberate: the cloud demo's static songs keep
working while new ingestion is impossible.

### 2.12 Rate limiting: what this code actually does

ASP.NET Core's rate limiter is configured in `YouTubeRateLimiting`. The implementation is a
**fixed-window** limiter, not a token bucket:

- Search: **10 permits per client IP per minute**.
- Enqueue: **4 permits per client IP per minute**.
- Window: one minute, `QueueLimit = 0`, automatic replenishment.
- Partition key: `context.Connection.RemoteIpAddress`. No remote address (the in-process test server,
  a Unix socket) means **no limiter**, so unrelated callers do not share one bucket.
- Rejection: `429 Too Many Requests`, a `Retry-After` header when the lease exposes it, and a
  `ProblemDetails` body.

"Fixed window" means the counter resets on a clock boundary, so a burst at the end of one window and
the start of the next can briefly exceed the per-minute rate. That is acceptable for anti-abuse at
party scale, and the review notes the bucket is shared by a whole NAT'd party.

Only the two `/api/youtube` routes opt in. The hub's `SearchYouTube` is **not** covered by this
limiter; the review records that residual risk (new minor MIN-5).

### 2.13 `BackgroundService` and the worker loop

`BackgroundService` is the ASP.NET Core base class for a long-running hosted service. It starts with
the host, runs until shutdown, and gets a cancellation token.

`MediaDownloadWorker` implements `ExecuteAsync` as one `await foreach` over the channel reader. Each
job is processed to a terminal state before the next is read, so downloads are serialized. A crashed
job is logged and the loop continues: one bad video must not stop the party.

### 2.14 SignalR progress fan-out

The worker is not a hub and has no connection. It broadcasts through
`IHubContext<StageHub, IStageHubClient>`, the typed hub context a singleton can inject. A target is a
`(QueueItemId, RoomCode)` pair, so the worker can send to `Clients.Group(roomCode)` for each attached
queue row.

Three pushes matter in this slice:

- `DownloadProgressChanged(DownloadProgressDto)` — stage, percent, bytes/s, ETA.
- `SongReady(SongReadyDto)` — the file exists and the row is playable.
- `DownloadFailed(DownloadFailedDto)` — retries are exhausted (or a deterministic codec mismatch).

The queue row's lifecycle state is also updated through the existing `QueueChanged` /
`QueueItemStateChanged` path, so the badge and the queue stay consistent.

### 2.15 "Zero-key", one more time

"Zero-key" is not "no configuration". It means **no YouTube API credential**. The server still needs
`yt-dlp` on `PATH` (or an absolute `YtDlp:ExecutablePath`), FFmpeg/FFprobe for merging and probing,
and optionally a JavaScript runtime for yt-dlp's player challenge solving (`--js-runtimes`). None of
those are accounts, keys, or paid quotas.

---

## 3. File-by-file tour

Paths are relative to the repo root. "Why it exists" is the one-line reason you would keep the file.

### 3.1 Server, core, contracts, and persistence

| Path | Role | Why it exists |
|---|---|---|
| `src/apps/Karaokay.Api/Program.cs` | Host wiring | Registers `YtDlpClient`, the probe, the channel, the coordinator, the ingestion service, and `MediaDownloadWorker` as a hosted service. |
| `src/apps/Karaokay.Api/Features/YouTubeIngestionService.cs` | Shared ingestion core | Feature gate, validation, cache hit, dedup, queue insertion, admission caps, and ticket attach for both REST and hub. |
| `src/apps/Karaokay.Api/Features/MediaDownloadWorker.cs` | Download worker | Consumes the channel, drives yt-dlp, verifies codecs, publishes the cache file, broadcasts progress/ready/failure. |
| `src/apps/Karaokay.Api/Endpoints/YouTubeEndpoints.cs` | REST surface | `GET /api/youtube/search` and `POST /api/youtube/enqueue`, session-token resolution, problem mapping. |
| `src/apps/Karaokay.Api/Endpoints/YouTubeRateLimiting.cs` | Anti-abuse | The two fixed-window, per-IP policies and their `429` response. |
| `src/apps/Karaokay.Api/Endpoints/MediaEndpoints.cs` | Video delivery | Adds `GET /api/songs/{songId}/video` with ranges and ETags. |
| `src/apps/Karaokay.Api/Hubs/StageHub.cs` | Hub methods | Implements `SearchYouTube` and `EnqueueYouTubeSong` on top of the shared service. |
| `src/apps/Karaokay.Api/Features/FeatureOptions.cs` | Feature flag | `Features:EnableYouTubeIngestion`. |
| `src/apps/Karaokay.Api/Features/QueueService.cs` | Queue integration | `EnqueueSongAsync` accepts an explicit initial state; `UpdateQueueItemStateAsync` promotes `Downloading → Ready` or `Failed`. |
| `src/apps/Karaokay.Api/Features/SongCatalog.cs` | Media resolution | Resolves a cached MP4 through the cache root, and maps the deterministic YouTube song id back to `{videoId}.mp4` with no database. |
| `src/apps/Karaokay.Api/appsettings.json` | Defaults | Flag off; yt-dlp options (15 s search, 30 min download, 3 attempts, 2 s backoff). |
| `src/apps/Karaokay.Api/appsettings.Development.json` | Dev overrides | Flag on; tools from `PATH`; cache/staging under the ignored `media/` folder. |
| `src/core/Karaokay.Infrastructure/MediaIngest/YtDlpClient.cs` | Process adapter | `IYouTubeService` over CliWrap: search, download, timeouts, progress callback, no shell. |
| `src/core/Karaokay.Infrastructure/MediaIngest/YtDlpCommandBuilder.cs` | Pure arguments | Builds the exact argument arrays; extracted so tests never spawn a process. |
| `src/core/Karaokay.Infrastructure/MediaIngest/YtDlpOptions.cs` | Configuration | Executable, JS runtimes, FFmpeg path, staging/cache, timeouts, attempts, backoff. |
| `src/core/Karaokay.Infrastructure/MediaIngest/YtDlpProgressParser.cs` | Progress parser | Parses the numeric `karaokay-progress:` template into percent/speed/ETA. |
| `src/core/Karaokay.Infrastructure/MediaIngest/YtDlpResultMapper.cs` | JSON mapper | Maps the flat-playlist document into typed DTOs, dropping invalid ids. |
| `src/core/Karaokay.Infrastructure/MediaIngest/IYouTubeService.cs` | Adapter contract | Search/download interface, result/progress records, `YouTubeServiceException`. |
| `src/core/Karaokay.Infrastructure/MediaIngest/YouTubeCachePaths.cs` | Cache paths | One source of truth for `{videoId}.mp4`, stored relative paths, and traversal rejection. |
| `src/core/Karaokay.Infrastructure/MediaIngest/MediaCodecProbe.cs` | Codec assertion | FFprobe-backed H.264/AAC verification with a fail-soft "tool missing" outcome. |
| `src/core/Karaokay.Application/Downloads/DownloadJobChannel.cs` | Bounded queue | Capacity 4, single reader, non-blocking `TryEnqueue`. |
| `src/core/Karaokay.Application/Downloads/DownloadJobCoordinator.cs` | Single-flight registry | One ticket per video id; terminal tickets are recycled. |
| `src/core/Karaokay.Application/Downloads/DownloadJobTicket.cs` | Shared download handle | Targets, progress, completion, first-complete-wins semantics. |
| `src/core/Karaokay.Application/YouTubeSongIds.cs` | Deterministic ids | UUIDv5 from the video id for no-database mode. |
| `src/core/Karaokay.Contracts/VideoIdValidator.cs` | Validation gate | The eleven-character URL-safe base64 regex. |
| `src/core/Karaokay.Contracts/YouTubeHttpDtos.cs` | REST shapes | `YouTubeEnqueueRequest` and the session-token header name. |
| `src/core/Karaokay.Contracts/Hubs/Dtos.cs` | Wire records | Search DTO, progress/ready/failure DTOs, error codes, queue ack. |
| `src/core/Karaokay.Contracts/Hubs/WireEnums.cs` | Wire enums | BCL-only mirrors including `DownloadStage` and `MediaSource`. |
| `src/core/Karaokay.Contracts/Hubs/IStageHub.cs` | Client→server contract | Declares `SearchYouTube` and `EnqueueYouTubeSong`. |
| `src/core/Karaokay.Contracts/Hubs/IStageHubClient.cs` | Server→client contract | Declares the three YouTube pushes, plus older placeholders. |
| `src/core/Karaokay.Domain/DownloadJob.cs` | Job entity | Persisted stage, percent, attempts, failure reason, queue item link. |
| `src/core/Karaokay.Domain/DownloadStage.cs` | Job states | `Queued`, `Resolving`, `Downloading`, `Transcoding`, `Completed`, `Failed`. |
| `src/core/Karaokay.Domain/Song.cs` | Song entity | `VideoId`, `CachePath`, `FileSizeBytes`, `Channel`, `ThumbnailUrl`. |
| `src/core/Karaokay.Domain/MediaSource.cs` | Source discriminator | `Multitrack = 0`, `YouTube = 1`. |
| `src/core/Karaokay.Domain/QueueItemState.cs` | Row lifecycle | `Downloading = 1` is the new Slice 5 state. |
| `src/core/Karaokay.Infrastructure/Persistence/KaraokayDbContext.cs` | EF model | `DownloadJobs` set, entity config, filtered unique `Song.VideoId`. |
| `src/core/Karaokay.Infrastructure/Persistence/PostgresMigrations/20261125120000_AddDownloadJobs.cs` | Migration | Creates `DownloadJobs` with its three indexes and the `Songs` FK. |

### 3.2 Web client, end-to-end tests, and .NET tests

| Path | Role | Why it exists |
|---|---|---|
| `src/clients/web/src/lib/api/youtube.ts` | REST client | Typed search/enqueue, error classification, timeouts, retry memory. |
| `src/clients/web/src/features/remote/YouTubeTab.tsx` | Guest search UI | Debounced search, per-result add state, friendly errors. |
| `src/clients/web/src/features/remote/useStageClient.ts` | Remote wiring | Enqueues through REST with the session token; retries a failed row from memory. |
| `src/clients/web/src/features/remote/QueueList.tsx` | Queue rows | Renders download badges, percentage, failure reason, Retry. |
| `src/clients/web/src/lib/signalr/roomState.ts` | Pure reducers | Folds progress/ready/failed/queue-state pushes into one badge record. |
| `src/clients/web/src/features/stage/useStageVideo.ts` | Video surface hook | One persistent `<video>`, source selection, sync loop, ended reporting, test hooks. |
| `src/clients/web/src/features/stage/videoSync.ts` | Pure sync math | Mode selection, URL, expected position, hard/soft correction. |
| `src/clients/web/src/features/stage/WebAudioEngine.ts` | Audio graph | `attachMediaElement` routes the video into the master bus exactly once. |
| `src/clients/web/src/features/stage/StageShell.tsx` | Stage composition | Chooses video mode versus stem mode, mounts the single `<video>`, exposes data attributes. |
| `src/clients/web/src/features/stage/useStageRoom.ts` | Room + clock hook | Applies YouTube pushes and provides `serverNow` for video sync. |
| `src/clients/web/e2e/youtube.spec.ts` | E2E proof | Search → enqueue → progress → Ready → stage video → drift correction. |
| `src/clients/web/e2e/fixtures/yt-dlp-shim.mjs` | Deterministic binary | Replaces yt-dlp for Playwright; no network, real tiny MP4, real progress lines. |
| `src/clients/web/playwright.config.ts` | E2E host config | Points the API at the shim and a temp cache/staging directory. |
| `src/tests/Karaokay.UnitTests/VideoIdValidatorTests.cs` | Validator tests | Injection, traversal, length, alphabet, Unicode. |
| `src/tests/Karaokay.UnitTests/YtDlpParsingTests.cs` | Adapter tests | JSON mapping, argument arrays, sanitization, early rejection. |
| `src/tests/Karaokay.UnitTests/YtDlpProgressParserTests.cs` | Parser tests | Percent math, missing fields, junk lines, defensive numbers. |
| `src/tests/Karaokay.UnitTests/DownloadJobCoordinatorTests.cs` | Coalescing tests | Concurrent enqueues share one ticket; terminal tickets recycle. |
| `src/tests/Karaokay.UnitTests/MediaDownloadWorkerTests.cs` | Worker tests | Happy path, retries, cancellation cleanup, codec mismatch, no-DB degradation. |
| `src/tests/Karaokay.UnitTests/YouTubeIngestionServiceTests.cs` | Admission tests | Channel cap, per-room cap, coalescing bypass, cancellation cleanup, token resolution. |
| `src/tests/Karaokay.UnitTests/YouTubeCachePathsTests.cs` | Path tests | Outside-media-root cache, legacy path, traversal rejection. |
| `src/tests/Karaokay.IntegrationTests/YouTubeIntegrationTests.cs` | API integration | Search, dedup, hub progress/ready/failure, range serving, token binding. |
| `src/tests/Karaokay.IntegrationTests/YouTubeApiFixture.cs` | Container fixture | PostgreSQL + real host + `FakeYouTubeService` + simulated remote IP. |
| `src/tests/Karaokay.IntegrationTests/YouTubeNoDatabaseApiFixture.cs` | DB-less fixture | Runs contract edges without Docker. |
| `src/tests/Karaokay.IntegrationTests/YouTubeDisabledIntegrationTests.cs` | Flag-off tests | REST `403` and hub `YouTubeDisabled` everywhere. |
| `src/tests/Karaokay.IntegrationTests/YouTubeRateLimitIntegrationTests.cs` | Limit test | The fifth enqueue in a window returns `429` with `Retry-After`. |
| `src/tests/fixtures/youtube-sample.mp4.b64` | Test media | A tiny real H.264/AAC MP4 stored as text, so the binary-media guard stays green. |

---

## 4. Server deep dive

### 4.1 `YtDlpClient`, `YtDlpCommandBuilder`, and the actual argument array

`YtDlpClient` is the only implementation of `IYouTubeService`. Its class comment states the rule
plainly: "No shell, no `Process.Start`, no string interpolation into a command line", and the input
URL is placed after `--` so a hostile value cannot be parsed as an option.

#### Search

`SearchAsync` trims the query, returns `[]` for an empty query, sanitizes control characters, clamps
the result limit, builds arguments, runs the process, and maps JSON.

The limit clamp is:

```csharp
var limit = Math.Clamp(
    maxResults <= 0 ? _options.DefaultMaxSearchResults : maxResults,
    MinSearchResults,   // 1
    MaxSearchResults);  // 25
```

A non-positive limit means "use the configured default" (10). That detail matters in tests: the fake
adapter mirrors it because `YtDlpResultMapper` returns early for a non-positive limit.

The argument array from `BuildSearchArguments` is, in order:

```text
--flat-playlist
--dump-single-json
--skip-download
--no-playlist
--no-warnings
[--js-runtimes <runtime>] ...          one pair per configured runtime
--
ytsearch{limit}:{sanitized-query}
```

The query is one element, not split on spaces. `SanitizeQuery` removes every `char.IsControl`
character (newline, tab, NUL). Control characters matter because a newline in an argument could look
like a second line of input to a downstream parser.

The timeout is `Math.Max(1, SearchTimeoutSeconds)` seconds (default 15). It is implemented with a
linked `CancellationTokenSource` and `CancelAfter`, and a timeout becomes a
`YouTubeServiceException` with a message like "yt-dlp timed out after 15 seconds."

#### Download

`DownloadAsync` rejects an invalid id **before creating the staging directory or starting a process**.
Then it builds:

```text
--no-playlist
--no-warnings
--newline
--progress
--progress-template
download:karaokay-progress:%(progress.downloaded_bytes)s|%(progress.total_bytes)s|...|%(progress.speed)s|%(progress.eta)s
-f
bestvideo[vcodec^=avc1][height<=1080][ext=mp4]+bestaudio[acodec^=mp4a][ext=m4a]/best[ext=mp4]/best
-S
vcodec:h264,acodec:aac
--merge-output-format
mp4
--restrict-filenames
-o
{stagingDirectory}/{videoId}.%(ext)s
[--ffmpeg-location <path>]
[--js-runtimes <runtime>] ...
--
https://www.youtube.com/watch?v={videoId}
```

Three things are worth understanding:

1. **`-f` is a format selector with fallbacks.** It first tries AVC1 video at most 1080p plus MP4A
   audio. If YouTube has no such pair, it falls back to any MP4, then anything.
2. **`-S` sorts the remaining choices** toward H.264/AAC. It is a preference, not a hard filter, which
   is exactly why the FFprobe assertion exists.
3. **`--restrict-filenames` and `-o .../%(ext)s`** keep the filename predictable. The worker looks for a
   file whose basename equals the video id.

Progress lines are delivered through `onStandardOutputLine`. The client does not parse them itself;
it forwards each line to `YtDlpProgressParser.TryParse` and reports the resulting sample through
`IProgress<DownloadProgressInfo>`.

After the process exits zero, `FindDownloadedFile` enumerates the staging directory, ignores `.part`
files, keeps only files whose name-without-extension equals the video id, prefers `.mp4`, and
otherwise takes the largest candidate. If nothing is there, the client throws rather than returning a
path that does not exist.

#### Failure modes

| Situation | Result |
|---|---|
| Timeout | `YouTubeServiceException` with the timeout message |
| Executable missing | `YouTubeServiceException` wrapping `Win32Exception` ("Is yt-dlp installed…?") |
| Non-zero exit | `YouTubeServiceException` including a **600-character tail** of stderr |
| Invalid id | `ArgumentException` before any process starts |
| No output file | `YouTubeServiceException` ("produced no media file") |
| Malformed search JSON | `YouTubeServiceException` from the mapper |

The 600-character stderr tail is deliberate: enough to diagnose, not enough to log a megabyte. The
review still flags the unbounded stdout/stderr `StringBuilder`s as a nit.

### 4.2 Progress and result parsing

#### `YtDlpProgressParser`

The template is numeric on purpose, so tests can feed recorded lines without a terminal:

```text
karaokay-progress:<downloaded>|<total>|<estimate>|<speed>|<eta>
```

`TryParse`:

1. Finds the marker anywhere in the line (yt-dlp may prefix status text).
2. Splits the payload on `|`; fewer than five fields is not a progress line.
3. Parses `downloaded`; uses `total`, falling back to `estimate` when `total <= 0`.
4. Clamps percent to `0..100`; a zero total yields `0`, not a division by zero.
5. Returns `DownloadProgressInfo(percent, speed, eta)`.

`ParseNumber` treats `NA`, `N/A`, `None`, `null`, blank, `NaN`, `Infinity`, and unparseable text as
zero, and parses with `InvariantCulture`. That is what keeps a German or Turkish server locale from
changing a decimal point into a thousands separator.

#### `YtDlpResultMapper`

The mapper accepts either:

- an object with an `entries` array (the `--flat-playlist` shape), or
- a single video object.

For each entry it requires a valid video id; invalid entries are dropped silently. Then:

| Field | Source, with fallback |
|---|---|
| `VideoId` | `id` (validated) |
| `Title` | `title`, else the id |
| `Channel` | `channel`, else `uploader`, else `creator`, else `""` |
| `DurationSeconds` | `duration`, numeric or numeric-string, else `0` |
| `ThumbnailUrl` | last non-empty `thumbnails[].url`, else `https://i.ytimg.com/vi/{id}/mqdefault.jpg` |

Malformed JSON is converted into `YouTubeServiceException` so the REST endpoint can return `502` and
the hub can push a `HubError` instead of faulting the invocation with a raw `JsonException`.

### 4.3 `YouTubeIngestionService` — the enqueue path

This class is the shared core behind both the hub method and the REST endpoint. It owns the feature
gate, validation, dedup, cache hit, queue insertion, admission caps, and single-flight attach.

The happy path in `EnqueueAsync` is:

1. **Feature gate.** Flag off → `QueueAck(false, Guid.Empty, YouTubeDisabled)`.
2. **Validate.** `VideoIdValidator.IsValid`; invalid → `InvalidRequest`.
3. **Normalize metadata.** Title clamped to 512 (fallback `"YouTube video"`), channel to 512,
   thumbnail URL to 2048, duration seconds converted to milliseconds with NaN/Infinity/negative
   handled.
4. **Resolve the song row.** With a database:
   - look up `Songs` by `VideoId`;
   - if absent, insert a new `Song` with `Source = YouTube`, `IsSyntheticDemo = false`;
   - if the insert loses the filtered unique index to a concurrent request, clear the change tracker
     and re-read the winner.
   Without a database, use `YouTubeSongIds.ForVideo(videoId)` (a UUIDv5).
5. **Build the wire `SongDto`** with `MediaSource.YouTube`.
6. **Cache hit?** `TryResolveCachedFile` tries the stored `CachePath` (traversal-safe) and then the
   canonical `{videoId}.mp4`. If a file exists, enqueue the row as `Ready` with no queue item id and
   return; no download happens.
7. **Admission check.** `HasPendingDownloadCapacity` returns true when a live ticket already exists
   for this video (a coalesced request adds no work). Otherwise it counts pending rows in this room
   (`MaxPendingDownloadsPerRoom = 4`) and across all live rooms (`MaxPendingDownloadsGlobal = 16`).
   A full cap → `QueueAck(false, Guid.Empty, QueueFull)`.
8. **Create the queue row** with a pre-minted `queueItemId` and state `Downloading`.
9. **Attach to a ticket.** `GetOrCreate(videoId, songId, out created)`; `TryAddTarget` attaches this
   queue row. If the ticket completed between the cache check and the attach, the method checks the
   cache again, promotes the row to `Ready` if the file appeared, and otherwise retries up to five
   times with 50 ms spacing.
10. **Queue the job only once.** The request that got `created == true` persists a `DownloadJob`
    (`Stage = Queued`, `QueueItemId`, `CreatedAtUtc`) and calls `TryQueueJobAsync`, which uses
    `TryEnqueue`. If the channel is full, the row is deleted again, the ticket is completed as failed,
    every already-attached target is failed, and the caller gets `QueueFull`.
11. **Return the ack.** Coalesced requests return the same `QueueItemId` they created, with no new job.

Two subtleties are worth calling out:

- **The row is created before the job is admitted**, because the phone must see a `Downloading` row
  immediately. If admission fails, the code path has to fail the row and broadcast `DownloadFailed`,
  which it does.
- **The song row may be created even if the enqueue is later rejected.** That is review minor #9: an
  orphan `Song` row can survive a `QueueFull`. It is harmless for display but it is real.

There is a second safeguard: if all attach retries are exhausted and no cache file appears, the row is
marked failed with a readable reason and the ack is `DownloadUnavailable` (mapped to HTTP `409`).

### 4.4 `DownloadJobChannel`, `DownloadJobCoordinator`, and `DownloadJobTicket`

#### Channel

```csharp
public const int Capacity = 4;

_channel = Channel.CreateBounded<DownloadJob>(
    new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
```

`Reader` is consumed by the worker; `TryEnqueue` is `Writer.TryWrite`. Single reader is safe because
only `MediaDownloadWorker` reads. Multiple writers are allowed because several HTTP requests and hub
invocations can enqueue concurrently; the coordinator plus `TryWrite` makes that safe.

One consequence deserves a note: the global cap of 16 cannot normally bind, because the channel itself
holds only 4 jobs. The review records this as MIN-1. The effective global bound is "4 in the channel
plus 1 being processed".

#### Coordinator

`DownloadJobCoordinator` is a `ConcurrentDictionary<string, DownloadJobTicket>` with ordinal keys.

`GetOrCreate` loops:

- If a ticket exists and is **not terminal**, return it with `created = false`.
- If it exists and **is terminal**, remove that exact instance and try to install a fresh one.
- Install with `TryAdd`; if another thread won, loop and read theirs.

`TryRemove(ticket)` removes only if the stored value is the same object, so a stale completion cannot
delete a newer download's ticket.

#### Ticket

A `DownloadJobTicket` is the in-memory shared handle:

| Member | Meaning |
|---|---|
| `VideoId`, `SongId` | Identity of the one download. |
| `Completion` | `TaskCompletionSource<DownloadJobResult>`; every attached request can await it. |
| `Result` | Null while running; terminal result afterwards. |
| `Progress` | Latest `DownloadProgressSnapshot` for late joiners. |
| `Targets` | Snapshot of attached `(QueueItemId, RoomCode)` rows. |
| `TryAddTarget` | Fails once the ticket is terminal. |
| `TryRemoveTarget` | Undo an attach that could not be persisted. |
| `Report` | Records progress while non-terminal. |
| `Complete` | First caller wins; returns the targets, clears them, completes the TCS; later calls return `[]`. |

"First caller wins" is what makes duplicate completion harmless: a retry path or a double broadcast
cannot produce two terminal events.

### 4.5 `MediaDownloadWorker` — the state machine

`ExecuteAsync` is a serialized loop:

```csharp
await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
{
    try { await ProcessJobAsync(job, stoppingToken); }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
    catch (Exception exception) { LogJobCrashed(_logger, job.VideoId, exception); }
}
```

`ProcessJobAsync` first resolves the ticket (or creates one if the ticket vanished). Then it checks
the cache: if `{videoId}.mp4` already exists, it skips the download and completes the job as a success.
That is the "second request raced the first download, and the first finished" path.

For each attempt, `1..MaxAttempts` (default 3 = one initial try plus two retries):

1. Create a unique staging directory: `{StagingRoot}/{videoId}-{guid:N}`.
2. Set `Stage = Resolving`, persist, broadcast.
3. Call `DownloadWithProgressAsync`, which starts a progress pump and then calls
   `IYouTubeService.DownloadAsync`.
4. Verify codecs with `EnsureCodecsAsync`.
5. Set `Stage = Transcoding`, persist, broadcast.
6. Ensure the cache directory exists and `File.Move(staged, cacheFile, overwrite: true)`.
7. Complete success and return.

The stages and who sets them:

| Stage | Set by | Persisted? | Pushed to the room |
|---|---|---|---|
| `Queued` | `TryQueueJobAsync` at enqueue | Yes | No (the queue row itself just appeared) |
| `Resolving` | worker at attempt start | Yes | `DownloadProgressChanged` |
| `Downloading` | progress pump on the first accepted sample | Yes | `DownloadProgressChanged` |
| `Transcoding` | worker after the codec check | Yes | `DownloadProgressChanged` |
| `Completed` | `CompleteSuccessAsync` | Yes | `DownloadProgressChanged(100)` + `SongReady` |
| `Failed` | `CompleteFailureAsync` | Yes | `DownloadFailed` |

Note the real enum: there is no `Processing` stage. Earlier project-state prose used the shorthand
"Queued → Downloading → Processing → Transcoding"; that shorthand has since been corrected, and the
enum's middle state is `Resolving` (`src/core/Karaokay.Domain/DownloadStage.cs`).

#### Retries

- A normal exception sets `failureReason`, logs the attempt, and waits `RetryBackoffSeconds` (default
  2) before the next attempt.
- A `CodecMismatchException` is **deterministic**: retrying the same source will produce the same
  codecs, so the loop breaks immediately and the job fails without spending the retry budget.
- `OperationCanceledException` when the host is stopping is rethrown; the `finally` still deletes the
  staging directory.

The review records an open edge: on host shutdown the in-flight ticket is **not completed**, so a row
can be left `Downloading` until restart. That is minor #3.

#### Progress pump

`DownloadWithProgressAsync` creates an **unbounded** `Channel<DownloadProgressInfo>`, starts
`PumpProgressAsync`, and bridges yt-dlp's synchronous `Report` callback into it with a tiny
`ChannelProgress` adapter. The pump decides what is worth broadcasting:

```csharp
var shouldBroadcast = sample.Percent >= 100
    || Math.Abs(sample.Percent - lastPercent) >= 1
    || now - lastBroadcast >= TimeSpan.FromMilliseconds(250);
```

So a fast download produces at most roughly four samples per second plus every whole-percent change,
not one SignalR message per yt-dlp line. The unbounded channel is a known nit (#4): a producer far
ahead of the pump could grow it.

On the first accepted sample while the stage is `Queued` or `Resolving`, the pump moves the job to
`Downloading`, persists, and reports the sample to the ticket. Then, for every target (or the
fallback target), it sends `DownloadProgressChanged` to that room's group.

#### Success and failure fan-out

`CompleteSuccessAsync`:

1. Computes the stored cache path relative to the cache directory (`{videoId}.mp4`).
2. Sets `Stage = Completed`, `ProgressPercent = 100`, clears the failure reason, stamps
   `CompletedAtUtc`.
3. Persists the job and then the song (`Source = YouTube`, `VideoId`, `CachePath`, `FileSizeBytes`).
4. `ticket.Complete(success)` and removes the ticket from the coordinator.
5. For each target: send a final `DownloadProgressChanged(Completed, 100)`, call
   `QueueService.UpdateQueueItemStateAsync(..., Ready)`, and send `SongReady`.

`CompleteFailureAsync` mirrors it: `Stage = Failed`, failure reason truncated to 1024 characters,
persist, complete the ticket, update each row to `Failed`, send `DownloadFailed` with the reason.

If the ticket has no targets (for example after a process restart), `FallbackTargets` finds the room
by queue item id, then falls back to scanning active rooms. That is why the job row carries
`QueueItemId`: it is the durable link between a downloaded file and the row that should become ready.

### 4.6 REST endpoints and rate limiting

`MapYouTubeEndpoints` registers two routes under `/api/youtube`.

#### `GET /api/youtube/search`

| Condition | Response |
|---|---|
| Flag off | `403` "YouTube ingestion is disabled" |
| Blank query | `400` "A search query is required" |
| `YouTubeServiceException` | `502` "YouTube search failed" with the exception message |
| Success | `200` with `YouTubeSearchResultDto[]` |

The endpoint passes `maxResults ?? 0`; the client layer clamps it. It requires the
`youtube-search` rate-limit policy.

#### `POST /api/youtube/enqueue`

The body is `YouTubeEnqueueRequest`: room code, requester name, video id, title, channel, duration,
thumbnail.

| Condition | Response |
|---|---|
| Flag off | `403` |
| Invalid video id | `400`, before any service call |
| Unknown room | `404` |
| Success | `200` with `QueueAck` |
| `QueueFull` | `429` "YouTube queue is full" |
| `DownloadUnavailable` | `409` "YouTube download unavailable" |
| Other failure | `409` with the ack reason |

The session token is optional. `ResolveSessionTokenId` reads the `X-Karaokay-Session-Token` header,
parses a GUID, and looks for a participant holding it in the room. Missing, malformed, or stale
tokens fall back to `null` (anonymous) and are logged at debug level. That is why a guest who joined
the hub can have the queue row attributed to them, while a plain REST caller still works.

The endpoint requires the `youtube-enqueue` policy. The limits and partitioning are described in
§2.12. Because the in-process test server has no remote IP, the integration fixture installs a
test-only middleware that copies `X-Test-Remote-Ip` into `Connection.RemoteIpAddress`; production
traffic is partitioned by the real address.

One accuracy note: the rate limiter is attached at the endpoint, so the hub's `SearchYouTube` is not
throttled by it. The hub still requires a room membership, and the pending-download caps still bound
enqueue work, but a joined guest can trigger searches in a loop. The review records this as a
residual risk rather than a closed item.

### 4.7 `GET /api/songs/{songId}/video`

`MediaEndpoints` already served instrumental, vocals, and lyrics. Slice 5 adds one route:

```csharp
songs.MapGet("/{songId}/video", ... ServeAsync(songId, MediaLibrary.VideoFile, Mp4ContentType, ...));
```

`MediaLibrary.VideoFile` is `"video.mp4"`, but the actual resolution is not a filename join. It goes
through `SongCatalog.ResolveMediaFileAsync`, which:

- in database mode, loads the song row and resolves `CachePath` against the trusted **cache** root
  (not the media root), rejecting traversal;
- in no-database mode, maps the deterministic UUID back to `{videoId}.mp4` by scanning the cache and
  comparing `YouTubeSongIds.ForVideo(videoId) == songId`.

The response uses the same discipline as the stem endpoints: `Cache-Control: public, max-age=86400,
immutable`, `Accept-Ranges: bytes`, a strong ETag from length + last-write ticks, and
`enableRangeProcessing: true`. A `<video>` element needs byte ranges to seek and to start before the
whole MP4 arrives; the integration test pins `206` plus `Content-Range: bytes 0-99/<size>`, and `200`
for a full read.

The endpoint is **not** gated by the feature flag. Existing cached videos remain playable on a
deployment that has ingestion turned off, which is the intended cloud behavior.

### 4.8 `VideoIdValidator`

```csharp
public const int Length = 11;

public static bool IsValid(string? videoId) =>
    videoId is { Length: Length } && VideoIdPattern().IsMatch(videoId);

[GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
private static partial Regex VideoIdPattern();
```

Why this shape:

- **Exactly eleven** removes any buffer or truncation ambiguity.
- **`A–Z`, `a–z`, `0–9`** cover the alphanumeric part.
- **`_` and `-`** are the URL-safe replacements for base64's `+` and `/`; YouTube uses the URL-safe
  alphabet so ids can live in paths.
- **Anchors** mean no prefix or suffix is allowed.
- **`CultureInvariant`** avoids locale-specific case or character folding.
- **`string?` and the length pattern** make null and wrong-length inputs false, not exceptions.

Every entry point calls it: the hub enqueue, the REST enqueue, `YouTubeIngestionService`, the command
builder, and `DownloadAsync`. The duplication is deliberate defense in depth; the outermost check
gives a clean 400, the innermost guarantees no process ever sees a bad id.

### 4.9 Persistence: `DownloadJob` and `AddDownloadJobs`

`DownloadJob` is a plain entity:

| Column | Type | Meaning |
|---|---|---|
| `Id` | `uuid` PK | Job identity |
| `SongId` | `uuid` FK → `Songs.Id`, `Restrict` | The song row created at enqueue |
| `VideoId` | `varchar(64)` | Validated video id (review nit: 64 for an 11-char value) |
| `Stage` | `int` | `DownloadStage` |
| `ProgressPercent` | `double precision` | 0..100 |
| `FailureReason` | `varchar(1024)` nullable | Terminal reason |
| `AttemptCount` | `int` | Attempts made |
| `CreatedAtUtc` | `timestamptz` | Creation |
| `CompletedAtUtc` | `timestamptz` nullable | Terminal time |
| `QueueItemId` | `uuid` nullable | Durable link for progress targeting |

The migration creates the table, the FK, and indexes on `QueueItemId`, `SongId`, and `VideoId`. The
model also gives `Song.VideoId` a **filtered unique index**:

```csharp
song.HasIndex(entity => entity.VideoId)
    .IsUnique()
    .HasFilter(ProviderIndexFilters.VideoIdFilterFor(Database.ProviderName));
```

PostgreSQL gets `"VideoId" IS NOT NULL`; SQL Server gets `[VideoId] IS NOT NULL`. That is the dedup
guarantee: a concurrent second enqueue cannot create a second song row for the same video, and the
service handles the unique-violation race by re-reading the winner.

Two honest notes:

- **The database is optional.** Every persistence call resolves `KaraokayDbContext` with
  `GetService` and returns when it is null. A bare `dotnet run` still ingests and plays; it just
  loses job history on restart.
- **`DownloadJob` rows are not cleaned up.** A completed row stays. There is no retention policy or
  LRU cache eviction in this slice, even though the blueprint mentions an LRU quota. The cache grows
  until an operator removes files.

### 4.10 `FeatureOptions` and how the flag gates the hub

`FeatureOptions` has one boolean, bound from the `Features` section. `Program.cs` registers it with
`Configure<FeatureOptions>`; the hub, the ingestion service, and the REST endpoints inject
`IOptions<FeatureOptions>`.

The gate exists at three layers:

| Layer | Flag off behavior |
|---|---|
| REST search/enqueue | `403 Forbidden` with a `ProblemDetails` body |
| `YouTubeIngestionService.SearchAsync` | Returns `[]` (defense in depth) |
| `YouTubeIngestionService.EnqueueAsync` | `QueueAck(false, Guid.Empty, YouTubeDisabled)` |
| `StageHub.SearchYouTube` | Pushes `HubError(YouTubeDisabled)`, returns `[]` |
| `StageHub.EnqueueYouTubeSong` | Pushes `HubError(YouTubeDisabled)`, returns failed ack |

The worker is still registered. With no way to enqueue, it simply has nothing to read. This is
deliberate: the flag is a policy switch, not a DI graph rewrite.

---

## 5. Contracts deep dive

### 5.1 The hub surface

`IStageHub` has 15 methods. Slice 5 adds two of them:

```csharp
Task<IReadOnlyList<YouTubeSearchResultDto>> SearchYouTube(string query);
Task<QueueAck> EnqueueYouTubeSong(YouTubeQueueRequest request);
```

Both require a room (`RequireRoomAsync`), so the hub is not an anonymous search proxy.

`SearchYouTube`:

- flag off → `HubError(YouTubeDisabled)` and an empty list;
- `YouTubeServiceException` → `HubError(InvalidRequest, message)` and an empty list;
- success → the result list as the invocation return value.

It does **not** call `SearchCompleted`. That push exists on the client interface but is never sent;
the direct return value is the contract. The review's "three dead entries" note has shrunk to two:
`SearchCompleted` and `LyricsLoaded` remain declared but unsent.

`EnqueueYouTubeSong`:

- null request → `HubError(InvalidRequest)` plus a thrown `HubException`;
- flag off → `HubError(YouTubeDisabled)` plus a failed ack;
- invalid id → `HubError(InvalidRequest)` plus a failed ack;
- service failure → `HubError(ack.Reason)`; `QueueFull` gets a friendlier message;
- success → the `QueueAck` with the new queue item id.

The hub path passes `Context.ConnectionAborted`, so a disconnecting client can cancel the enqueue
work. The older queue/playback methods still pass `CancellationToken.None` (review minor #2).

### 5.2 The live client pushes

`IStageHubClient` has 14 methods. Slice 5 makes three of them live:

| Push | DTO | When |
|---|---|---|
| `DownloadProgressChanged` | `DownloadProgressDto(QueueItemId, SongId, Stage, Percent, BytesPerSec, EtaSec)` | Every accepted progress sample and at terminal stages. |
| `SongReady` | `SongReadyDto(QueueItemId, SongId)` | After the file is moved and the row is `Ready`. |
| `DownloadFailed` | `DownloadFailedDto(QueueItemId, SongId, Reason)` | After retries are exhausted or a codec mismatch. |

The DTO field names are camelCase on the wire because SignalR's default JSON policy camelCases
properties. The TypeScript mirror in `protocol.ts` uses the same names.

`SearchCompleted` remains a declared-but-unsent optional push. `CountdownTicked` is live, but it is
Slice 4's count-in, not part of this slice.

### 5.3 `DownloadStage`: the wire enum versus the domain enum

There are two enums with the same name, one in `Karaokay.Domain` and one in `Karaokay.Contracts.Hubs`.
Both are persisted as integers and must stay numerically identical, because the conversion is a cast.
The TypeScript mirror repeats the numbers a third time in `protocol.ts`. No current test compares the
two C# enums: `SignalRContractTests` checks method names, constants, and DTO field names, not enum
parity. A renumbering in one file is therefore a silent wire break; the slice-3 review recorded that
gap as minor #14.

| Value | Domain and wire name | Meaning |
|---:|---|---|
| 0 | `Queued` | Waiting for a worker slot |
| 1 | `Resolving` | Resolving the best stream with yt-dlp |
| 2 | `Downloading` | Transferring media |
| 3 | `Transcoding` | Remux/merge into the MP4 cache file |
| 4 | `Completed` | File is ready |
| 5 | `Failed` | Terminal failure |

The conversion in the worker is a numeric cast:

```csharp
private static Karaokay.Contracts.Hubs.DownloadStage ToWireStage(DownloadStage stage) =>
    (Karaokay.Contracts.Hubs.DownloadStage)(int)stage;
```

That works only because the numeric values match; no current test compares the two enums, so a
renumbering is still a silent wire break (§5.5).

Earlier project-state prose called the middle step "Processing"; it has since been corrected, and the
code calls it `Resolving`. The enum is authoritative. Phones do not distinguish the pre-download
stages: the client reducer maps every non-terminal, non-queued stage to the `Downloading` queue state
and shows the percentage badge.

### 5.4 `MediaSource` for YouTube rows

There is no `SongSource` type in the code. The discriminator is `MediaSource`:

| Value | Domain and wire name | Meaning |
|---:|---|---|
| 0 | `Multitrack` | Local stems (`instrumental.opus` + `vocals.opus`) |
| 1 | `YouTube` | A cached MP4 served by `/api/songs/{id}/video` |

The ingestion service creates the song with `MediaSource.YouTube`; the worker reasserts it when it
writes `CachePath`; `SongDto.Source` carries it to the client. `isYouTubePlayback` in `videoSync.ts`
is the single client-side switch: a `PlaybackStateDto` with a current item whose song source is
`YouTube` enters video mode; anything else uses the stem mixer.

### 5.5 DTO field-parity contract tests

The earlier `SignalRContractTests` checked method names, the `RoomCommand`/`HubErrorCodes` constants,
and `RoomCommand`'s field shape — not enum values (slice-3 review minor #14). The Slice 4 follow-up
extended it to **DTO field shapes**:

- `HubDtoRecordNames` lists 19 DTO records, including `YouTubeSearchResultDto`,
  `DownloadProgressDto`, `SongReadyDto`, `DownloadFailedDto`, and `CountdownDto`.
- `HubDtoRecordSetIsComplete` asserts the list length, so adding a record without adding it to the
  theory fails the suite.
- `HubDtoFieldsMatchTheTypeScriptInterfaces` reflects over each C# record's properties, camelCases
  them, parses the matching TypeScript interface out of `protocol.ts`, and asserts the same count and
  the same name set.
- `RoomCommand` gets a separate 8-field check against its TypeScript interface.

This catches a renamed or missing property, which is the drift that actually breaks a client. It does
not catch a changed type or a changed optionality; the review says name-only parity remains.

---

## 6. Client deep dive

### 6.1 `lib/api/youtube.ts` — the REST client

This module is the only place the guest UI talks HTTP for YouTube.

- **Types come from the generated OpenAPI schema** (`components['schemas'][...]`), not hand-written
  duplicates.
- **Errors are classified** into `YouTubeErrorKind`: `disabled`, `invalid`, `not-found`, `timeout`,
  `network`, `search-failed`, `failed`. `classify` maps `403 → disabled`, `400 → invalid`,
  `404 → not-found`, and `502/504` on a search to `search-failed`.
- **Both calls have a 15 s timeout** implemented with `AbortController` plus `setTimeout`; an abort
  is reported as `timeout`, and a pre-response failure as `network`.
- **`friendlyMessage`** turns those kinds into party-appropriate copy ("YouTube search and requests
  are turned off for this stage.") rather than raw problem details.
- **`rememberedRequests`** is a session-scoped `Map<queueItemId, YouTubeEnqueueRequest>`. On a
  successful enqueue it remembers the request, so a later `Failed` row can offer Retry with the same
  video. `getYouTubeRequest`, `isKnownYouTubeQueueItem`, and `forgetYouTubeRequest` expose it.
- **`enqueueYouTube`** sends the `X-Karaokay-Session-Token` header when the client has one, and throws
  `YouTubeApiError('failed', ack.reason)` when the ack itself is unsuccessful.

The search call accepts an optional session token and sends the header, but `YouTubeTab` never passes
one; the Slice 4 review lists that as an open nit: either wire it deliberately or drop the parameter.

### 6.2 `YouTubeTab.tsx` — debounced search and enqueue

The tab is a controlled search box plus a result list.

- **Debounce:** every keystroke resets a 300 ms timer (`SEARCH_DEBOUNCE_MS`). Only the last query in a
  burst hits the network.
- **Cancellation:** the effect's cleanup sets `cancelled = true` and clears the timer, so a stale
  response cannot overwrite a newer query's results.
- **Empty query:** clears results, searching state, and error immediately without a request.
- **Result limit:** `MAX_RESULTS = 10`.
- **Per-result add state:** `pending → added` or `pending → error`. The button label follows:
  "Adding…", "Added ✓", "Retry", or "Add to queue".
- **Disabled:** the `disabled` prop (the remote passes its connection-disabled flag) disables every
  add button.
- **Errors:** search failures use `youtubeErrorMessage`; an enqueue failure with reason
  `YouTubeDisabled` gets specific copy.
- **Test hooks:** each row has `data-video-id`, which the Playwright spec uses to target the fixture
  result.

The tab does not own the enqueue call. `LobbyView` passes `api.enqueueYouTubeSong`, which lives in
`useStageClient.ts` and knows the room code, display name, and session token. That split keeps the
component presentational and puts the wire concern in the hook.

A subtle behavior worth knowing: `retryYouTubeSong` enqueues a **new** queue row for the same video.
The old `Failed` row stays visible on purpose, as a record of the failed attempt and as the anchor for
its badge state. The review nits this (`retried` state never resets, dead rows accumulate).

### 6.3 Queue badges: `QueueList.tsx` and `roomState.ts`

The badge has one source of truth, `QueueDownloadState`:

```ts
export interface QueueDownloadState {
  stage: DownloadStage;
  percent: number;
  bytesPerSec: number;
  etaSec: number;
  ready: boolean;
  cached: boolean;        // enqueued Ready with no progress events
  failureReason: string | null;
}
```

Which push drives which field:

| Push / event | Reducer | Effect |
|---|---|---|
| `DownloadProgressChanged` | `applyDownloadProgress` | Sets stage/percent/speed/ETA; adds the song id; promotes `Queued`/`Downloading` rows to the stage's queue state. |
| `SongReady` | `applySongReady` | Sets `Completed`/100/ready, clears the failure, and marks the row `Ready`. |
| `DownloadFailed` | `applyDownloadFailed` | Sets `Failed`, keeps the reason, and marks the row `Failed`. |
| `QueueItemStateChanged` | `applyQueueItemState` | Syncs the row state; a `Ready` with no prior download record is treated as a **cache hit** (`cached: true`). |
| `QueueChanged` | `applyDelta` | Removes/updates rows and prunes download records for rows that left the queue. |

`QueueList.badgeLabel` then renders:

| Condition | Badge |
|---|---|
| Row `Failed` or a failure reason exists | `Failed` (with the reason as a title/tooltip and an inline error) |
| Row `Ready` or `download.ready` | `Ready` — or `Ready · cached` when the row was already ready with no progress |
| `Playing` | `Playing` |
| `Ended` | `Ended` |
| `Downloading` | `NN%` when a finite percent exists, else `Downloading` |
| Anything else | `Queued` |

While downloading, the row also renders a `role="progressbar"` bar with the clamped percentage. The
`data-download-state` attribute is `failed`, the numeric stage, or `queued`, which gives Playwright a
stable selector.

The retry button appears only for failed YouTube rows. It calls `onRetry(queueItemId)`, which asks
`useStageClient` for the remembered request and re-enqueues it. `isYouTube` is inferred from three
signals: a download record exists, the request is remembered, or the library song's `source` is
`youtube`.

Two reducer behaviors are easy to miss:

- **A progress push must not downgrade a `Playing` row.** `applyDownloadProgress` only rewrites the
  queue state when the row is `Queued`/`Downloading` or the stage is terminal.
- **Download records survive a rejoin.** They are derived from transient pushes, not from the
  snapshot, so `applySnapshot` prunes only records whose queue rows disappeared; it does not wipe the
  map.

### 6.4 `useStageVideo.ts` — one persistent `<video>`, one source node

The hook owns a single `<video>` element that is rendered once by `StageShell` and never unmounted.
That is the central design decision, and the reason is a browser constraint:

> `createMediaElementSource(element)` may be called **once per element**. Calling it again throws.

Because the stage wants video audio to go through the same master bus and unlock gesture as the
stems, it must route the element into the `AudioContext`. If the element were
recreated per song, every new element would need a new source node, and the hook would have to manage
disconnect/reconnect on every transition. Keeping one element for the page session makes the routing
one-time and idempotent.

The flow:

1. **Source selection.** When `isYouTubePlayback(playback)` is true, the hook sets
   `src="/api/songs/{songId}/video"`, pauses, mutes, and loads. When it leaves video mode, it pauses,
   mutes, removes `src`, and calls `load()` again. The element is never removed from the DOM.
2. **Attach after unlock.** The effect runs only when `audioUnlocked` is true. It calls
   `engine.attachMediaElement(video)`; on success it unmutes. `attachMediaElement` returns early for
   the same element and same source, so re-renders are no-ops.
3. **Reconciliation.** A 500 ms interval (`SYNC_INTERVAL_MS`) plus a `loadedmetadata` listener call
   `sync()`. `sync` computes the authoritative position, applies a seek or a playback-rate nudge, and
   starts/stops the element to match `Playing`.
4. **Ended.** The `ended` listener reports the current queue item once, guarded by a
   `reportedEndsRef` set. The authority/watchdog path is idempotent too; this guard just avoids
   redundant invocations.
5. **Autoplay failure.** A `NotAllowedError` from `play()` sets the "Tap the stage to start the video."
   notice. `StageShell` renders it with a retry button that calls `retryPlayback()` from a real
   gesture.
6. **Test surface.** `window.__karaokayVideo` exposes `mode`, `songId`, `attached`, `notice`,
   `currentTime()`, `expectedMs()`, `paused()`, `injectDrift(ms)`, and `syncNow()`. The E2E spec uses
   exactly these.

`attachMediaElement` in `WebAudioEngine` has one defensive branch for a remounted stage: if a
*different* element appears, it disconnects the old source and creates one for the new element. The
normal path never reaches it because `StageShell` renders the video once.

### 6.5 `videoSync.ts` — server-clock expected position and the drift thresholds

`expectedPositionMs(playback, nowMs)`:

- If the stage test clock is installed, return it verbatim. That is what lets Playwright pin a
  deterministic video position, exactly as it does for the canvas renderer.
- Otherwise, start from `PlaybackStateDto.positionMs`.
- If the status is not `Playing`, return the base: pauses are already folded into `positionMs`.
- If playing, parse `timeAnchorUtc` and return `base + max(0, nowMs - anchorMs)`.

The crucial input is `nowMs`. `useStageVideo` passes `serverNowRef.current`, which is
`useStageRoom.serverNow()` — a local clock corrected by Slice 4's Ping/Pong offset (best-of-window,
minimum-RTT sample), with a receipt-time fallback until the filter is confident. It is **not**
`Date.now()` unless there is no room. That is the review's M3 fix: a wall clock off by seconds no
longer chases a permanently wrong target.

`videoCorrection(playback, actualMs, nowMs)`:

```text
drift = expectedMs - actualMs

|drift| > 120 ms  -> hard seek to expectedMs, playbackRate = 1
40 < |drift| <= 120 -> soft nudge: playbackRate = 1 ± 0.005 (5 s, then reset)
else                -> no correction
```

The constants are `HARD_NUDGE_MS = 120`, `SOFT_NUDGE_MS = 40`, `SOFT_NUDGE_RATE = 0.005`. They match
`docs/ARCHITECTURE.md` §5.3. The E2E test injects **+300 ms**, which is well past the hard threshold,
and asserts the next pass lands under 150 ms of error. The +300 ms is the *injected drift*, not the
threshold; the threshold is 120 ms.

Why a soft nudge at all? A hard seek every 500 ms would produce audible clicks and visible jumps. A
0.5 % rate change is inaudible and converges a small drift without seeking.

`videoUrlForSong` builds `/api/songs/{encoded id}/video`, which is the range-enabled endpoint from
§4.7.

### 6.6 How `StageShell` chooses between stem mode and video mode

`StageShell` composes three sources of truth:

1. **Local demo mode** (`DemoTrackPicker` + `useStagePlayback`'s idle fallback) for a database-less
   `npm run dev` or Playwright run.
2. **Room library mode** (`useStagePlayback`) when the room's current song is a multitrack.
3. **Video mode** (`useStageVideo`) when the room's current song is `YouTube`.

The switch is:

```tsx
const videoMode = video.mode === 'video';
const roomActive = roomPlayback.status.active && !videoMode;
const effectivePhase = roomActive ? stagePhaseForRoom(roomPlayback.status.phase) : phase;
```

Consequences:

- **Video mode owns the stage.** An effect stops the demo engine, clears the selection and countdown,
  and clears the canvas. This prevents a room YouTube item from overlapping a local demo stem.
- **Video mode renders no guide-vocal slider.** There is one muxed audio track; there is nothing to
  crossfade. The `<video>` is routed into the master bus, so master volume still applies.
- **The canvas stays mounted** behind the video. `StageShell` keeps `#stage-canvas` visible, and the
  E2E spec asserts it. That preserves the "same stage, different surface" idea and keeps the
  canvas/test hooks valid.
- **Data attributes make the mode assertable:** `data-phase` (including `paused`), `data-video`,
  `data-room`, `data-song-id`, and `data-queue-item-id`. The YouTube spec waits for
  `data-video="true"` and reads the video's `data-mode`/`data-song-id`/`src`.

The audio gate is still the first interaction. `handleEnableAudio` calls `video.primeForUnlock()`
first (muted playback is autoplay-safe inside the gesture), then `engine.unlock()`. After the unlock,
the source node is attached and the element is unmuted into the bus. Nothing in the video path —
source creation, decode, routing — happens before that click.

---

## 7. Architecture decisions, with consequences

### 7.1 Zero-key `yt-dlp` instead of the YouTube Data API

**Context.** Guests need search. A search API normally means a key, a quota, and a billing
relationship.

**Options.** (a) YouTube Data API with a key; (b) scrape YouTube's HTML/JSON directly; (c) shell out
to `yt-dlp`.

**Choice.** (c). `yt-dlp` already solves the hard parts (player challenges, signature deciphering,
format selection) and needs no key.

**Consequences.** C1 is satisfied, and the same tool downloads the media, so there is one external
dependency instead of two. The cost is an external process with a moving target: YouTube changes can
break yt-dlp until it is updated, and a JS runtime may be required. CI never depends on the real
binary; a shim and a fake adapter cover the pipeline. Real yt-dlp runs are local/nightly work.

### 7.2 A background worker and a bounded channel instead of inline download

**Context.** A download takes seconds to minutes. An HTTP request or hub invocation cannot hold the
connection that long, and N concurrent downloads would exhaust disk, CPU, and YouTube's patience.

**Options.** (a) Download inline in the request; (b) fire-and-forget `Task.Run` per request; (c) a
bounded channel feeding one hosted worker.

**Choice.** (c). The request creates the row and the job, tries a non-blocking enqueue, and returns.
The worker serializes downloads.

**Consequences.** The guest gets an immediate `Downloading` row and progress events. The host has a
predictable upper bound on concurrent work. The price is a queue-full path, a ticket/coordinator
layer for coalescing, and a persisted job state machine. Those are the bulk of this slice's
complexity, and they are worth it: the alternative either blocks the party or melts the host.

### 7.3 Cache identity by `videoId`, with dedup and single-flight

**Context.** The same popular karaoke video will be requested repeatedly, sometimes by two guests at
once.

**Options.** (a) Key the cache by song row id; (b) key by a hash of the URL; (c) key by the validated
`videoId`.

**Choice.** (c), enforced by a filtered unique `Songs.VideoId` index and a `{videoId}.mp4` filename.

**Consequences.** A second request maps to the same song row and the same file. If the file exists,
the row is `Ready` instantly with no worker job. If a download is in flight, the second request
attaches to the ticket and shares it. The cache is self-describing: an operator can look at
`ytcache/dQw4w9WgXcQ.mp4` and know exactly what it is. The cost is that a corrupt or wrong-codec file
is served forever until deleted; the review notes cached files bypass the codec assertion.

### 7.4 Staging plus an atomic move

**Context.** A served file must never be a partial MP4, and a crashed download must not leave garbage
in the cache.

**Options.** (a) Download directly to the final cache path; (b) download to a temp file and rename;
(c) download to a temp directory and copy.

**Choice.** (b)/(c): a per-attempt staging directory, then `File.Move` to the cache. The `finally`
always deletes the staging directory.

**Consequences.** A reader sees either no file or a complete file. A failed attempt leaves no partial
cache entry, and `.part` files never reach the media endpoint. The design assumes staging and cache
share a filesystem; a cross-volume move throws and consumes a retry. The review records that
cross-volume fallback as open work.

### 7.5 Strict H.264/AAC enforcement

**Context.** The stage is a browser today and an Android TV app later. Both need codecs that
hardware-decode reliably, and both need a predictable MP4 container.

**Options.** (a) Trust yt-dlp's `-S` preference; (b) reject anything that is not H.264/AAC; (c)
transcode everything to H.264/AAC.

**Choice.** (b), with (a) as the first line of defense. The download selector prefers AVC1/MP4A, and
FFprobe asserts the result before publication.

**Consequences.** The cache is guaranteed playable on the target clients, and a bad rendition fails
with a clear reason instead of producing a video that silently will not play. The cost is that some
videos become unavailable when no AVC1 rendition exists, and a missing FFprobe degrades to "accept
without verification" rather than failing every job. That fail-soft choice matches the existing
`StemMixer` policy.

### 7.6 Rate limiting and the feature flag as two different defenses

**Context.** The REST surface is unauthenticated by design (a guest joins with a room code), and every
search spawns a process.

**Options.** (a) Require accounts; (b) trust the party; (c) per-IP limits plus caps plus a deployment
flag.

**Choice.** (c). Fixed-window per-IP limits on the REST routes, per-room and global pending caps in
the admission service, a bounded worker channel, and `EnableYouTubeIngestion` for the public demo.

**Consequences.** A single abusive client cannot spawn unlimited searches within a minute, and a
whole party cannot queue unlimited downloads. The limits are deliberately generous (10 searches, 4
enqueues per minute per IP). The known gaps: a NAT'd party shares one bucket, the hub search path is
not covered by the REST limiter, and the partition dictionary grows per IP without eviction. All
three are recorded rather than hidden.

### 7.7 Recording the delivery-order mistake instead of rewriting history

**Context.** A commit labelled Slice 4 shipped Slice 5, and the docs described a different shipped
state.

**Options.** (a) Renumber the work as Slice 4 and move the authority to Slice 5; (b) leave the docs
stale; (c) revert and re-land; (d) keep the roadmap, credit the early delivery, and mark Slice 4 open.

**Choice.** (d), in ADR-007.

**Consequences.** The roadmap, the reviews, and the state page agree on what shipped. The Slice 4
definition of done keeps its full strength, so "complete" cannot be claimed without the authority,
watchdog, rehydration, and duplicate-end tests. The cost is a git history that still contains a
misleading commit title, and a period where a later slice was complete before an earlier one. Both
are accepted explicitly. The alternative — a label change — would have hidden a missed deliverable.

---

## 8. Testing the slice

### 8.1 Unit tests

| File | What it pins |
|---|---|
| `VideoIdValidatorTests` | Six accepted ids (including all `_` and all `-`); 22 rejected values (flags, shell metacharacters, quotes, traversal, wrong lengths, Unicode); null; the `Length` constant. |
| `YtDlpParsingTests` | Flat-playlist mapping and fallbacks; limit honoring; single-video JSON; malformed JSON → `YouTubeServiceException`; control-character sanitization; search cannot start when sanitizing leaves nothing; exact search and download argument arrays; the `--` marker; invalid-id rejection before a process starts. |
| `YtDlpProgressParserTests` | 25 %/100 % samples; estimate fallback; clamping; junk/incomplete lines; marker inside a prefixed line; `NA`/`None`/`NaN`/`Infinity` handling. |
| `DownloadJobCoordinatorTests` | Two and 32 concurrent enqueues share one ticket; distinct ids get distinct tickets; terminal tickets recycle; `TryRemove` only removes the same instance; `Find` tolerates null/empty/unknown. |
| `MediaDownloadWorkerTests` | The default `MaxAttempts = 3` means two retries; cache path is relative to the cache directory; happy path moves the file and broadcasts 25/50/75/100 + `SongReady` + `Ready`; failure retries twice then marks job and row `Failed`; a transient failure succeeds on attempt 2; cancellation cleans staging; a codec mismatch fails **without** retrying; a missing probe degrades to `Ready`. |
| `YouTubeIngestionServiceTests` | A full channel rejects and fails the row; the fifth pending download in a room is rejected while another room still enqueues; a coalesced request bypasses the room cap; queue-full cleanup fails already-attached targets; session-token lookup and malformed/unknown-token fallback. |
| `YouTubeCachePathsTests` | A cache outside the media root resolves; the legacy media-root-relative form still resolves; rooted paths and `..` are rejected. |
| `SignalRContractTests` | 15/14 hub method parity; `RoomCommand` and `HubErrorCodes` constants; 19 DTO records with matching camelCase field sets; `RoomCommand`'s 8 fields. |

A note on the worker tests: `MediaDownloadWorkerTests` uses a real `DownloadJobChannel`, a real
`MediaDownloadWorker`, a real `QueueService`, and a real in-memory `RoomState`, but a **fake**
`IYouTubeService`, a stub probe, and a scope factory that resolves no `DbContext`. That is the same
no-database degradation the API uses for a bare `dotnet run`, which is why the tests run everywhere.

### 8.2 Integration tests

`YouTubeIntegrationTests` is the heart of the server-side verification. It has two fixtures:

- `YouTubeApiFixture` starts `postgres:16-alpine` with Testcontainers, boots the real host, configures
  a temp media root and a **sibling** cache directory (mirroring development topology), and replaces
  `IYouTubeService` with `FakeYouTubeService`.
- `YouTubeNoDatabaseApiFixture` boots the same host with an empty connection string, for the two
  contract edges that never touch Postgres.

`FakeYouTubeService` is deterministic and offline:

- Search returns two scripted results; `FailSearch` throws; `MalformedSearchJson` feeds invalid JSON
  through the real `YtDlpResultMapper` (with the same positive-limit clamp the real client applies).
- Download writes a `.part` file first, emits 25/50/75 % progress with a delay, writes the committed
  base64 MP4, emits 100 %, and returns its size.
- `FailVideoId` always throws.
- It counts calls per video, which is how the dedup test proves one download.

What the tests prove:

| Test | Assertion |
|---|---|
| `SearchReturnsTheTypedFakeResults` | REST search returns the typed list and the query reached the adapter. |
| `SearchWithABlankQueryIsRejected` | `400`. |
| `SearchFailureMapsTo502` | `502`. |
| `HubSearchFailureSurfacesAHubErrorInsteadOfFaulting` | A malformed JSON search pushes `HubError(InvalidRequest)` and returns `[]`, on the DB-less host. |
| `EnqueueRejectsAnInvalidVideoIdBeforeSchedulingADownload` | `abc;defghij` is `400` and the adapter's download count does not move. |
| `EnqueueWithTheGuestSessionTokenBindsTheQueueRowToTheParticipant` | The persisted `SessionTokenId` equals the join token. |
| `EnqueueWithAMalformedSessionTokenFallsBackToAnonymous` | A bad header still enqueues, with a null token id. |
| `EnqueueingTheSameVideoTwiceUsesOneSongOneJobAndOneDownload` | Two `Ready` rows share one `SongId`; one `Songs` row; one `DownloadJobs` row; one download; `CachePath = {videoId}.mp4`. |
| `HubEnqueueBroadcastsProgressThenSongReady` | A real SignalR client sees 25/50/75/100 and `SongReady`, and the snapshot row is `Ready`. |
| `HubFailureBroadcastsDownloadFailedAndMarksTheRowFailed` | `DownloadFailed` carries the reason and the row is `Failed`. |
| `RangeRequestServesExactlyHundredBytesAndFullRequestServesTheWholeFile` | `206` with `Content-Range: bytes 0-99/<size>`, 100 bytes, `video/mp4`; full read is `200` with `Accept-Ranges`. |
| `VideoEndpointReturns404ForASongWithoutACachedVideo` | A multitrack id has no `video` media. |

`YouTubeDisabledIntegrationTests` runs against the ordinary `PostgresApiFixture`, which boots with the
flag off: REST search and enqueue are `403`, and the hub returns an empty search plus
`YouTubeDisabled` on both search and enqueue.

`YouTubeRateLimitIntegrationTests` uses the fixture's `X-Test-Remote-Ip` hook to simulate one client.
It sends `EnqueuePermitsPerMinute + 1` requests: the first four are `200`, the fifth is `429`, and the
response carries `Retry-After` and the "Too many YouTube requests" body.

The Docker gate is real. Both Postgres fixtures check `CI=true`: locally, a missing Docker records a
skip reason and the tests skip with an actionable message; in CI, a missing container runtime **fails
the job** instead of silently skipping. That was the "CI requires-Docker gate" follow-up fix.

### 8.3 Vitest

The reducer suite (`roomState.test.ts`, 17 `it` blocks) covers:

- `applySnapshot` idempotence and dense sorting;
- contiguous deltas, gaps, duplicates, and a first delta with no snapshot;
- live progress promoting a row to `Downloading`;
- `SongReady` at 100 %;
- a snapshot `Ready` with no progress treated as a cache hit;
- failure reason preservation and retry creating a fresh row;
- pruning download records when a row leaves the queue;
- playback projection;
- count-in ring seeding and 4 → 1 ticks (Slice 4, but adjacent).

`videoSync.test.ts` (8 `it` blocks) pins mode selection, the URL, extrapolation while playing, no
extrapolation while paused, the test-clock override, hard seek over 120 ms, soft rate under 120 ms,
and the dead band at 40 ms.

### 8.4 The Playwright spec and the yt-dlp shim

`e2e/youtube.spec.ts` is a two-context test: a stage and a phone.

Why a shim? The real `yt-dlp` needs the network, is non-deterministic, and can change weekly. The
Playwright config starts the API with:

```text
YtDlp__ExecutablePath = e2e/fixtures/yt-dlp-shim.mjs
YtDlp__CacheDirectory = <temp dir>
YtDlp__StagingRoot    = <temp dir>
YtDlp__RetryBackoffSeconds = 0
YtDlp__MaxAttempts = 1
Features__EnableYouTubeIngestion = true
```

The shim is a Node script, so it runs anywhere Node runs. It implements exactly the two invocations
the client makes:

- if `--dump-single-json` is present, print one playlist document with video id `e2eTestVid1`, title
  "E2E Karaoke Fixture", channel "Karaokay CI", duration 120 s, and a data-URL thumbnail;
- otherwise, find `-o`, replace `%(ext)s` with `mp4`, write the committed base64 MP4, and emit
  `karaokay-progress:` lines at 25/50/75/100 with 400 ms spacing.

The test flow:

1. Open `/stage`, read the room code, click the audio gate.
2. Join from `/remote?code=...` as "Ada".
3. Search "karaoke fixture"; assert the one fixture row.
4. Add it; assert "Added".
5. Open the Queue tab; assert a percentage badge, then `data-state="2"` and a "Ready" badge.
6. Press Start; assert `data-video="true"`, the video's `data-mode="video"`, a non-empty
   `data-song-id`, and an `/api/songs/.../video` `src`.
7. Poll `currentTime() > 0.2` and `paused() === false`.
8. `injectDrift(300)`, record `beforeMs`, call `syncNow()`, read `afterMs`. Assert `beforeMs > 200`
   (the injection really left the dead band) and `afterMs < 150` (the hard seek corrected it).
9. Assert the canvas is still mounted, the now-playing title is the fixture, and a canvas frame still
   produces pixels.

Because `MaxAttempts = 1`, a shim failure would fail fast instead of retrying. Because the API runs
with no Postgres, the cached video is resolved through `YouTubeSongIds.ForVideo`. No live network, no
pixel comparison, no real binary.

---

## 9. Frequently confusing points

**Why is this work in a commit titled `slice-4`?** Because the commit author labelled it with the
next slice number while implementing the roadmap's Slice 5 scope. The owner chose to keep the
roadmap and record the early delivery rather than renumber. [ADR-007](../adr/007-slice-4-5-deliverable-ordering.md)
is the authoritative explanation, and `324da8a`'s own commit message accurately describes the YouTube
packages. Slice 4 (playback authority) was completed afterwards.

**Where do downloaded files live, and are they in Git?** Under `YtDlp:CacheDirectory`. The production
default is `media/library/ytcache` (resolved under the media root); Development uses
`../../../../../media/ytcache`, the repo-root `media/` folder. `media/` is git-ignored, and
`.gitignore` also blocks `*.mp4`, `*.mkv`, and `*.webm` as a second guard. CI's binary-media script
enforces the same rule on tracked files. A downloaded video is never committed.

**What happens if two guests enqueue the same video?** The first creates one `Song` row, one
`DownloadJob`, and a ticket. The second finds the same `Song` row by `VideoId` (or loses the unique
index race and re-reads the winner), creates its own queue row, and attaches it as a second target on
the same ticket. One download, one progress stream, two `Ready` rows. If the file already exists, both
rows are `Ready` immediately and no job is created.

**Why H.264/AAC only?** It is the intersection of what browsers, hardware decoders, and the future
Android TV stage can play without a software fallback. VP9/AV1 may be smaller, but they are not a safe
default for a party machine. The download is asked for AVC1/MP4A, and FFprobe verifies it before the
file is published.

**What exactly does the feature flag disable?** Search and enqueue. REST returns `403`; the hub pushes
`HubError(YouTubeDisabled)` and returns an empty/failed result. It does not delete the worker from DI,
and it does **not** stop `/api/songs/{id}/video` from serving an already-cached file. The cloud demo
therefore cannot ingest new media, but a pre-existing cache would still play. That is intentional.

**Is downloading from YouTube legal or safe to run publicly?** That is a policy and jurisdiction
question, not a code question. The repository takes the conservative position: ingestion is off by
default, it is meant for a self-hosted machine where the operator controls what is downloaded, and
C2 forbids committing the results. Exposing an unauthenticated ingestion endpoint to the public
internet would invite abuse and legal risk; the rate limits and caps reduce accidental load, they are
not a rights-management system. The public Azure demo is designed to have ingestion disabled.

**Why does the queue badge say "Ready · cached" sometimes?** A cache hit enqueues the row directly as
`Ready`, so no `DownloadProgressChanged` event ever arrives. The reducer sees a `Ready` queue state
with no download record and marks it `cached: true`. A normal download reaches `Ready` through
`SongReady` or a `QueueChanged` update and has a record, so it renders plain "Ready".

**Why is the client channel non-blocking when the options say `Wait`, and why is the global cap of 16
effectively unreachable?** `BoundedChannelFullMode.Wait` only affects a blocking `WriteAsync`. The app
uses `TryWrite` through `TryEnqueue`, so a full channel returns `false` immediately and the request
path maps it to `QueueFull`; that was the fix for the review major "bounded download channel blocks
request paths." The global cap is a second bound: because the channel holds only 4 jobs, admission
rejects before the global count can reach 16. The review records this as MIN-1 — the effective global
bound is "4 queued plus 1 in flight". Raising the channel capacity or documenting the real bound is
open work.

**Why does the stage keep a `<video>` element mounted even in stem mode?** Because
`createMediaElementSource` can only be called once per element. Keeping one element for the page
session means exactly one source node, one routing setup, and no reconnect dance when switching
between a multitrack song and a YouTube video. Leaving video mode pauses, mutes, and clears `src`; it
does not remove the element.

**Why is a hard video seek 120 ms and not 300 ms?** 120 ms is the threshold in
`docs/ARCHITECTURE.md` §5.3 and `videoSync.ts`. The E2E test injects 300 ms of drift to prove the
hard path fires; it then asserts the residual error is under 150 ms. A seek below the threshold would
be audible and visually jumpy, so the code soft-nudges the playback rate instead.

**Why does the hub have `SearchYouTube` if the guest UI uses REST?** The hub method is part of the
Slice 3 contract and is exercised by integration tests, but `YouTubeTab` calls `/api/youtube`. The
review records this as a blind spot: the primary guest path is REST-only, so the hub methods are not
covered end-to-end by the browser suite. Both paths share `YouTubeIngestionService`, so the core
logic is the same.

---

## 10. Where this leaves the project

Slice 5 turns the queue from "songs you own" into "songs the party asks for", while keeping the two
hard constraints intact: no paid API and no media in Git. It also demonstrates the project's most
useful architectural property — the YouTube path is not a second app. It is the same `Song` entity,
the same queue rows, the same SignalR group, and the same stage; only the ingestion and the rendering
surface differ.

The next slice is **Slice 6: Azure free-tier deployment and CI/CD**. It has to solve the problems
this slice deliberately leaves to the operator: disable ingestion in the cloud, keep media out of the
image, make the container start without a warm database, and keep the Docker-gated integration suite
honest. The open review backlog — the unbounded progress channel, the incomplete ticket on shutdown,
the cross-volume move, the NAT-shared enqueue bucket, and the unthrottled hub search — is the natural
input to that work, along with the still-open Slice 3 items M2 (client-declared stage role) and M3
(no room garbage collection).
