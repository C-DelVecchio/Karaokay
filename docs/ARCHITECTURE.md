# Karaokay — Architectural Blueprint & Phased Implementation Plan

> **Status:** Accepted · **Version:** 1.2 · **Date:** 2026-11-24
>
> Karaokay is a self-hosted, multi-client, real-time party karaoke platform inspired by classic
> karaoke machines, Rock Band, and Jackbox Games. It is engineered as a GitHub portfolio showcase
> demonstrating senior-level C#/.NET backend architecture, modern React + TypeScript frontend
> engineering, low-level C++ Android audio DSP, and zero-cost cloud deployment.
>
> **Version history:** 1.0 — initial blueprint. 1.1 — scope & hardening revision: removed
> real-time pitch tracking (classic-karaoke focus); replaced .NET MAUI with pure .NET for Android
> (`net10.0-android`); specified NDK AMedia decoding and the ExoPlayer `BaseAudioProcessor` →
> JNI PCM routing; added HDMI slapback-safe vocal monitoring defaults; added Azure SQL
> auto-pause resilience; deterministic yt-dlp format selection + optional cookies mount;
> phrase-marker parsing and stem loudness normalization; AudioContext gesture unlock;
> `ReportSongEnded` idempotency + hub role gating; provider-specific filtered-index syntax;
> project renamed to **Karaokay**. 1.2 — Slice 1 reconciliation: room-code alphabet corrected to
> 23 letters / 279,841 codes (ADR-005); line-wrapping ownership moved to the renderer (ADR-006).
>
> Companion decision records: [ADR-001](adr/001-single-replica-signalr-azure.md) ·
> [ADR-002](adr/002-android-oboe-hdmi-usb-drift.md) ·
> [ADR-003](adr/003-dual-mode-stage-web-vs-native.md) ·
> [ADR-004](adr/004-dual-source-media-pipeline.md) ·
> [ADR-005](adr/005-slice-1-blueprint-reconciliations.md) ·
> [ADR-006](adr/006-line-wrapping-ownership.md) ·
> [ADR-007](adr/007-slice-4-5-deliverable-ordering.md) ·
> [ADR-008](adr/008-slice-4-playback-authority-decisions.md)

## Table of Contents

1. [Project Goals & Constraints](#1-project-goals--constraints)
2. [System Overview](#2-system-overview)
3. [Monorepo Directory Layout](#3-monorepo-directory-layout)
4. [Core Data Contracts](#4-core-data-contracts)
5. [Party Room State Machine, Download Lifecycle & Clock Sync](#5-party-room-state-machine-download-lifecycle--clock-sync)
6. [Dual-Mode Karaoke Stage Renderer & Android C++ Audio Blueprint](#6-dual-mode-karaoke-stage-renderer--android-c-audio-blueprint)
7. [Phased Vertical-Slice Implementation Roadmap](#7-phased-vertical-slice-implementation-roadmap)

---

## 1. Project Goals & Constraints

### 1.1 Primary Purpose

A real-time party karaoke platform:

- **TV Stage** (Android TV, targeting an Onn 4K Pro → HDMI → TV, USB mic → Onn 4K Pro) renders a
  60fps classic karaoke machine display or a synchronized YouTube karaoke video, mixing the live
  USB microphone signal into the music in real time.
- **Phone Remotes** (mobile web) join a 4-letter room code and control the queue.
- **Browser Stage** provides the identical dual-mode stage experience in a desktop browser so
  recruiters can evaluate the live cloud demo with zero install.
- **Classic-karaoke scope (v1.1):** the stage renders sweep-fill syllables, phrase windows, and
  countdown dots only. Real-time pitch detection and Rock Band-style pitch-bar overlays are
  deliberately out of scope — the feature would have tripled the complexity of all three clients
  for a toggleable nicety, and `lyrics.json` stays a pure timeline contract.

### 1.2 Hard Constraints

| # | Constraint | Enforcement |
|---|---|---|
| C1 | **$0/month, permanently.** Every tool, library, CI runner, and cloud hosting target must operate at $0/month. No expiring 12-month free trials, no paid API keys, no paid Redis backplanes. | Azure SWA Free, ACA Consumption (`minReplicas: 0`), Azure SQL Free tier, GitHub free runners, zero-key `yt-dlp` search. Documented SKU table in `docs/self-hosting.md`. |
| C2 | **Zero copyrighted assets in Git.** Never commit commercial Rock Band charts, audio stems, or downloaded YouTube videos. | Standalone `Karaokay.Importer` CLI processes users' own extracted folders locally; deterministic `Karaokay.SyntheticGen` tool generates 2–3 public-domain multitrack demo songs committed to the repo so CI and recruiters run the full stack out of the box. |
| C3 | **Two deployment modes from one codebase.** *Self-hosted* (Linux home server, Aspire/Docker Compose, PostgreSQL, full `yt-dlp` ingestion) and *Public portfolio demo* (Azure free tiers, Azure SQL, ingestion disabled via feature flag, demo songs bundled as static frontend assets). | `Features:EnableYouTubeIngestion` flag + provider-agnostic persistence (Npgsql / Microsoft.EntityFrameworkCore.SqlServer). |
| C4 | **Type safety across the wire.** OpenAPI → `openapi-typescript` generated clients; SignalR method-name parity enforced by unit tests reflecting C# interfaces against TS protocol constants. | CI contract tests (`SignalRContractTests`). |

---

## 2. System Overview

```
                        ┌───────────────────────────── Server (.NET 10 / ASP.NET Core) ─────────────────────────────┐
                        │  Minimal API ──► Songs/Media/Rooms endpoints (range-enabled static media serving)          │
   Guests (phones) ──►  │  SignalR StageHub ──► RoomRegistry (singleton) ──► PlaybackAuthority (single writer)       │
   Stage (TV/browser) ► │       ▲                     QueueService │ RoomCodeGenerator (4-letter, collision-free)     │
                        │       │  QueueDelta / PlaybackChanged / DownloadProgressChanged                            │
                        │  YouTubeDownloadWorker ── Channel<YouTubeDownloadJob> (bounded) ── yt-dlp + FFmpeg (+Deno)   │
                        │  MediaIngest: MidiLyricsParser (DryWetMidi) │ StemMixer (FFmpeg amix) │ MediaStore         │
                        │  EF Core ──► PostgreSQL (self-host) / Azure SQL (cloud)                                  │
                        └──────────────────────────────────────────────────────────────────────────────────────────┘
                             │ Multitrack: lyrics.json + instrumental.opus + vocals.opus
                             │ YouTube:   cached mp4 (video surface mode)
        ┌────────────────────┴───────────────────┐
   Android TV Stage (.NET for Android)       Browser Stage (React + TS)
   SkiaSharp SKCanvasView renderer            Canvas sweep-fill renderer (identical math)
   ExoPlayer video (PCM → engine ring)        <video> element routed into Web Audio
   C++ Oboe engine: USB mic → SPSC →          Web Audio API: AudioContext, GainNode buses
   3-bus mixer + Schroeder reverb → HDMI      (instrumental / guide / mic)
   AMedia opus decode, drift compensation     AudioContext gesture unlock
```

**Deployment topologies (one codebase):**

1. **Self-Hosted Mode** — .NET Aspire orchestrates the API + PostgreSQL with local persistent media
   storage and full YouTube search/download (`yt-dlp`, FFmpeg, Deno 2.3+ in the image).
2. **Public Portfolio Demo Mode** — Azure Static Web Apps (Free) hosts the web client;
   Azure Container Apps (Consumption, `minReplicas: 0`, `maxReplicas: 1`, HTTP session affinity)
   hosts the API so SignalR works on a single replica without Redis or Azure SignalR Service;
   Azure SQL Database (Free tier, auto-pause) persists rooms/queue/library. The 2–3 synthetic
   demo song stems are bundled as static frontend assets and `Features:EnableYouTubeIngestion=false`
   eliminates all storage/bandwidth overage risk. See [ADR-001](adr/001-single-replica-signalr-azure.md).

---

## 3. Monorepo Directory Layout

```
karaokay/
├── karaokay.sln                       # .NET 10 solution (server + tools + tests)
├── .editorconfig / .gitignore / Directory.Build.props / Directory.Packages.props (central package mgmt)
├── README.md                          # portfolio landing: GIF demo, live Azure URL, architecture diagram
├── docs/
│   ├── ARCHITECTURE.md                # this document, versioned
│   ├── adr/
│   │   ├── 001-single-replica-signalr-azure.md
│   │   ├── 002-android-oboe-hdmi-usb-drift.md
│   │   ├── 003-dual-mode-stage-web-vs-native.md
│   │   ├── 004-dual-source-media-pipeline.md
│   │   └── template.md
│   ├── data-contracts.md               # lyrics.json + SignalR reference for contributors
│   └── self-hosting.md                 # home-server setup guide + Azure SKU cost table
│
├── src/
│   ├── apps/
│   │   ├── Karaokay.Api/              # ASP.NET Core host (Minimal APIs + SignalR)
│   │   │   ├── Hubs/StageHub.cs  Hubs/StageHubContracts.cs
│   │   │   ├── Endpoints/ (SongsEndpoints.cs, RoomsEndpoints.cs, MediaEndpoints.cs)
│   │   │   ├── Features/ (RoomService, QueueService, PlaybackAuthority, RoomCodeGenerator)
│   │   │   └── Program.cs  appsettings.json  appsettings.cloud.json
│   │   ├── Karaokay.AppHost/          # .NET Aspire orchestrator (self-host mode)
│   │   └── Karaokay.ServiceDefaults/  # shared telemetry/health/resilience config
│   │
│   ├── core/
│   │   ├── Karaokay.Domain/            # entities, enums, domain events
│   │   ├── Karaokay.Application/       # queue engine, playback authority, room state machine
│   │   ├── Karaokay.Infrastructure/
│   │   │   ├── Persistence/ (KaraokayDbContext, configurations, migrations)
│   │   │   └── MediaIngest/ (MidiLyricsParser, StemMixer, MediaStore, YouTubeSearchService,
│   │   │                    YouTubeDownloadWorker, DownloadJobChannel)
│   │   └── Karaokay.Contracts/          # DTOs shared with clients, lyrics.json types
│   │
│   ├── tools/
│   │   ├── Karaokay.Importer/          # CLI: ingest extracted Rock Band folders → library + lyrics.json
│   │   │   └── (Commands: `scan <root>`, `import <dir>`, `verify <songId>`)
│   │   └── Karaokay.SyntheticGen/      # deterministic demo song generator (DryWetMidi + FFmpeg)
│   │
│   ├── clients/
│   │   ├── web/                         # React + TS + Vite (guest remote + browser stage)
│   │   │   ├── src/features/remote/ (JoinScreen, LibraryTab, YouTubeTab, QueueList)
│   │   │   ├── src/features/stage/ (StageShell, CanvasLyricsRenderer, WebAudioEngine,
│   │   │   │                    YouTubeVideoSurface, QrOverlay)
│   │   │   ├── src/lib/api/ (generated by openapi-typescript)  src/lib/signalr/ (typed client)
│   │   │   ├── e2e/ (Playwright)
│   │   │   └── public/demo-tracks/       # 2–3 synthetic demo songs (stems + lyrics.json)
│   │   │
│   │   └── stage-android/               # Pure .NET for Android (net10.0-android) — no MAUI/XAML
│   │       ├── Platforms/Android/ (MainActivity, Leanback/TV setup, UsbMicPermission)
│   │       ├── Stage/ (StageActivity, KaraokeRenderer.cs — SKCanvasView port of canvas math,
│   │       │            VideoSurfaceView, LobbyActivity, DriftStatsOverlay)
│   │       ├── Audio/ (AudioEngineInterop.cs — [LibraryImport] P/Invoke)
│   │       └── native/karaoke-audio/    # C++ (CMake + Android NDK)
│   │           ├── engine/ (oboe_engine.cpp, spsc_ring.h, decoder_amedia.cpp,
│   │           │           youtube_pcm_jni.cpp, mixer.cpp, schroeder.cpp,
│   │           │           drift_compensator.cpp, jni_bridge.cpp)
│   │           └── tests/ (googletest: ring/mixer/reverb/decoder unit tests)
│   │
│   └── tests/
│       ├── Karaokay.UnitTests/          # domain, midi parser (golden-file tests), room codes
│       ├── Karaokay.IntegrationTests/   # WebApplicationFactory + Testcontainers (Postgres),
│       │                                 # SignalR hub integration, yt-dlp worker (mocked binary)
│       └── Karaokay.ArchitectureTests/  # hand-written reflection dependency rules
│                                         # (Assembly.GetReferencedAssemblies) + positive controls
│
├── infra/
│   ├── docker/ (server.Dockerfile [aspnet + yt-dlp + ffmpeg + deno], docker-compose.yml
│   │             with optional cookies.txt volume mount)
│   ├── aspire/ (self-host wiring)
│   ├── azure/ (main.bicep, containerapp.tmpl.yaml, swa.config.staticwebapp, deploy scripts)
│   └── mediastore/ (volume layout doc: /media/{library,ytcache})
│
└── .github/workflows/
    ├── ci.yml            # build + test matrix (ubuntu, free runners)
    ├── e2e.yml           # Playwright against `dotnet run` + synthetic tracks
    └── deploy-demo.yml   # SWA + ACA + Azure SQL free tier (OIDC-federated GH auth)
```

**Key structural decisions**

- **One sln, one npm workspace.** Server and tool CLIs share `core/`; clients consume generated
  contracts only — never project references to server code.
- **`Contracts` is the single source of truth** for SignalR method names and DTO shapes; the TS
  client imports generated OpenAPI types plus a hand-maintained `signalr/protocol.ts` that is
  compile-checked against C# via `SignalRContractTests`.
- **`native/karaoke-audio` is a standalone CMake project** with gtest — testable on desktop Linux
  (fakes for Oboe and MediaCodec) and compiled for Android via NDK from the `net10.0-android`
  csproj.
- **The Android TV client is pure .NET for Android** (`<TargetFramework>net10.0-android</TargetFramework>`,
  `dotnet workload install android`) — native `Activity`/views, `SkiaSharp.Views.Android`
  (`SKCanvasView`) for the stage canvas, and `[LibraryImport]` P/Invoke to
  `libkaraoke-audio.so`. No XAML or MAUI abstraction layer.
- **Dual EF Core provider migrations** (`Persistence/PostgresMigrations`, `Persistence/SqlServerMigrations`)
  keep self-host and cloud on one domain model.

---

## 4. Core Data Contracts

### 4.1 `lyrics.json` — canonical karaoke timeline

Designed for: sweep-fill syllable rendering, phrase grouping, and countdown dots after
instrumental gaps. Deliberately excludes pitch data (§1.1 classic-karaoke scope). All times are
**milliseconds relative to the start of `instrumental.opus` audio**, after applying the importer's
global offset. Syllables are the atomic render unit; phrases carry the gap metadata for countdown
dots.

```jsonc
{
  "version": 1,                        // schema version; renderer rejects unknown major versions
  "songId": "synth-twinkle-2026",
  "source": "Synthetic",                // "Synthetic" | "RockBand"
  "title": "Twinkle Variations",
  "artist": "Public Domain Arr. K. Okay",
  "durationMs": 182400,
  "ticksPerQuarterNote": 480,
  "tempoMap": [                          // informational; ms values are already authoritative
    { "tick": 0, "bpm": 120.0, "beatMs": 500.0 }
  ],
  "initialSilenceMs": 2000,              // time before first audio (from MIDI 'real' offset)
  "vocalParts": [
    {
      "part": "vocals",                  // "vocals" | "harm1" | "harm2" | "harm3"
      "isDefaultRenderPart": true,
      "phrases": [
        {
          "startMs": 8250,
          "endMs": 12000,
          "lines": [
            {
              "syllables": [
                { "text": "Twin",  "startMs": 8250, "endMs": 8571, "talky": false,
                  "connectsToNext": false, "lineBreakAfter": false },
                { "text": "kle",  "startMs": 8571, "endMs": 8928, "talky": false,
                  "connectsToNext": true,  "lineBreakAfter": false },
                { "text": "twin", "startMs": 8928, "endMs": 9300, "talky": false,
                  "connectsToNext": false, "lineBreakAfter": false }
              ]
            }
          ]
        }
      ],
      // gaps BETWEEN phrases, used for lead-in/countdown rendering
      "gaps": [
        { "fromMs": 41000, "toMs": 44600,
          "countdownBeats": 4, "beatMs": 500.0,     // derived from tempo map at fromMs
          "renderCountdown": true }                  // false if gap < 2 beats or first phrase
      ]
    }
  ]
}
```

**Importer semantics encoded in the schema**

| MIDI concept | Contract rule |
|---|---|
| Tick → ms | Exact conversion via `SetTempo` map (DryWetMidi `TempoMap`; piecewise-constant BPM). All fields stored pre-flattened to ms. |
| Phrase grouping | **Prefer explicit Rock Band phrase-boundary marker notes (`105`/`106` on `PART VOCALS`/`HARM*`) when present.** Fallback when markers are absent: a phrase ends when the next note begins > `PhraseGapMs` (default 1,500 ms) after the previous note's end, or at end-of-track marker events. |
| Line breaks | The importer stores only **explicit** breaks derived from `[line_end]` text events (`lineBreakAfter: true` on the syllable that ends the line); a chart without `[line_end]` markers yields one line per phrase. The **renderer owns re-flow**: it wraps a line when cumulative syllable width at the render font exceeds `MaxLineWidthEm`, measured against its own font metrics, and always honors importer-provided `lineBreakAfter` breaks. |
| Syllable connectors | Lyric text ending in `-` or `+` → strip marker, set `connectsToNext: true` (word continues; sweep does not reset). |
| Talky / spoken-word | Lyric text starting `#` (or `^` in HARM tracks) → `talky: true`; a render style hint — flat spoken-word fill with no syllable sweep pulse. |
| Countdown dots | Any inter-phrase gap ≥ `MinCountdownGapMs` (default 2,000 ms) gets `renderCountdown: true`, `countdownBeats = min(4, floor(gapMs / beatMs))`; dots fill beat-by-beat counting *down* to `toMs`. |
| Lead-in | First phrase always preceded by `initialSilenceMs` + a synthetic gap with 4 countdown dots. |

### 4.2 EF Core entity models

Provider-agnostic: Npgsql (self-host PostgreSQL) / Microsoft.EntityFrameworkCore.SqlServer
(Azure SQL free tier). Lyrics JSON is *never* queried in SQL (only served as a file), so
`NVARCHAR(MAX)` vs `jsonb` divergence is irrelevant. One provider divergence is codified in
`KaraokayDbContext`: the filtered unique index on `Song.VideoId` requires provider-specific raw
SQL in `HasFilter(...)` — `"VideoId" IS NOT NULL` for Npgsql vs `[VideoId] IS NOT NULL` for SQL
Server — selected at model-configuration time so the same entity graph migrates cleanly on both
providers. Sessions are **in-memory + cookie/token only** (no table, no Redis): a room lives in a
singleton `RoomRegistry` with the DB as cold-restart fallback for the queue.

```csharp
public enum MediaSource { Multitrack = 0, YouTube = 1 }
public enum QueueItemState { Queued = 0, Downloading = 1, Ready = 2, Failed = 3, Playing = 4, Ended = 5 }
public enum PlaybackStatus { Idle = 0, AwaitingDownload = 1, Countdown = 2, Playing = 3, Paused = 4, SongEnded = 5 }
public enum DownloadStage { Queued = 0, Resolving = 1, Downloading = 2, Transcoding = 3, Completed = 4, Failed = 5 }

public class Song {
    public Guid Id { get; set; }
    public MediaSource Source { get; set; }
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public long DurationMs { get; set; }
    public string? ThumbnailUrl { get; set; }        // YouTube channel art / generated art
    public string? VideoId { get; set; }            // YouTube source only; filtered unique index where not null
    public string? Channel { get; set; }
    // Multitrack storage (relative to MediaRoot; null for YouTube source)
    public string? InstrumentalPath { get; set; }    // ".../instrumental.opus"
    public string? VocalsPath { get; set; }          // ".../vocals.opus"
    public string? LyricsPath { get; set; }          // ".../lyrics.json"
    // YouTube storage (null for Multitrack)
    public string? CachePath { get; set; }           // ".../ytcache/{videoId}.mp4"
    public long? FileSizeBytes { get; set; }
    public bool IsSyntheticDemo { get; set; }        // bundled demo tracks (cloud mode asset list)
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastPlayedAtUtc { get; set; }
}

public class DownloadJob {
    public Guid Id { get; set; }
    public Guid SongId { get; set; }                 // FK: Song created at enqueue time
    public string VideoId { get; set; } = "";
    public DownloadStage Stage { get; set; }
    public double ProgressPercent { get; set; }       // 0..100, updated from yt-dlp --newline
    public string? FailureReason { get; set; }
    public int AttemptCount { get; set; }            // retry up to 2 with backoff
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public Guid? QueueItemId { get; set; }           // link for progress broadcast targeting
    public Song Song { get; set; } = null!;
}

public class PartyRoom {
    public Guid Id { get; set; }
    public string Code { get; set; } = "";           // 4 chars, unique index
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }       // soft-close; rooms GC after 12h idle
    // authoritative playback state (single writer: PlaybackAuthority)
    public PlaybackStatus Status { get; set; }
    public Guid? CurrentQueueItemId { get; set; }
    public DateTime? PlaybackStartedAtUtc { get; set; }  // wall clock when Playing began (position 0)
    public long PausedAtPositionMs { get; set; }          // position when Paused; 0 otherwise
    public double GuideVocalVolume { get; set; } = 0.6;  // 0..1, Multitrack songs only
    public int CountdownRemainingBeats { get; set; }
    public List<QueueItem> Queue { get; set; } = new();
}

public class QueueItem {
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid SongId { get; set; }
    public int Position { get; set; }                // dense ordering within room; reorder = swap+renumber
    public string RequestedByName { get; set; } = "";
    public QueueItemState State { get; set; }        // Queued/Downloading/Ready/Failed/Playing/Ended
    public Guid? SessionTokenId { get; set; }        // who queued it (soft ownership for remove rights)
    public DateTime RequestedAtUtc { get; set; }
    public PartyRoom Room { get; set; } = null!;
    public Song Song { get; set; } = null!;
}
```

**Room code alphabet:** `ABCDEFGHJKLMNPRSTUVWXYZ` — 23 letters (no `I O Q`) → 279,841 codes.
The `RoomCodeGenerator` generates against an in-memory active-set plus the DB unique index as the
collision backstop.

### 4.3 SignalR contracts (typed hub, single hub: `/hubs/stage`)

```csharp
// ---- Client → Server --------------------------------------------------
public interface IStageHub {
    // roles: "stage" (TV/browser stage) | "guest" (phone). Guests get a session token back.
    Task<JoinAck> JoinRoom(JoinRequest request);
    Task<Pong> Ping(PingPayload payload);                       // clock sync, client supplies t1
    Task<IReadOnlyList<YouTubeSearchResultDto>> SearchYouTube(string query); // invocation return (no API key)
    Task<QueueAck> EnqueueLibrarySong(Guid songId);
    Task<QueueAck> EnqueueYouTubeSong(YouTubeQueueRequest request); // {videoId,title,channel,durationSeconds,thumbnailUrl}
    Task RemoveQueueItem(Guid queueItemId);
    Task MoveQueueItem(Guid queueItemId, int newPosition);
    Task StartPlayback();            // stage or any guest; authority validates
    Task PausePlayback();
    Task ResumePlayback();
    Task SkipSong();
    Task RestartSong();
    Task ReportSongEnded(Guid queueItemId);     // stage-only; idempotent vs the watchdog (§5.1)
    Task SetGuideVocalVolume(double volume);       // 0..1
    Task UpdateStageCapabilities(StageCapabilities caps); // stage-only; reports audio latencyEstimateMs
}

// ---- Server → Client --------------------------------------------------
public interface IStageHubClient {
    Task JoinedRoom(RoomSnapshot snapshot);          // full authoritative state (rejoin = same path)
    Task ParticipantJoined(string displayName, int guestCount);
    Task ParticipantLeft(string displayName, int guestCount);
    Task QueueChanged(QueueDelta delta);            // items added/removed/moved/state-changed
    Task QueueItemStateChanged(Guid queueItemId, QueueItemState state);
    Task DownloadProgressChanged(DownloadProgressDto dto); // {queueItemId, songId, stage, percent, bytesPerSec, etaSec}
    Task PlaybackChanged(PlaybackStateDto dto);     // see §5.3 for full shape
    Task CountdownTicked(CountdownDto dto);        // {queueItemId, remainingBeats, beatMs}
    Task LyricsLoaded(Guid songId, LyricsDto lyricsRef); // url + sha256 of lyrics.json
    Task SearchCompleted(string correlationId, IReadOnlyList<YouTubeSearchResultDto> results); // optional push variant
    Task RoomClosed(string reason);
    Task HubError(HubErrorDto error);               // {code, message, details?}
}

// ---- Key DTO shapes (C#; TS generated) --------------------------------
public record JoinRequest(string RoomCode, string DisplayName, string Role);
public record JoinAck(bool Success, string? SessionToken, string? Reason);
public record PlaybackStateDto(
    PlaybackStatus Status, Guid? CurrentQueueItemId, SongDto? CurrentSong,
    long PositionMs,                 // authoritative position AT TimeAnchorUtc
    DateTimeOffset TimeAnchorUtc,    // wall clock when PositionMs was true
    double GuideVocalVolume,
    int? CountdownRemainingBeats);
public record QueueDelta(
    IReadOnlyList<QueueItemDto> Added, IReadOnlyList<Guid> Removed,
    IReadOnlyList<QueueItemDto> Updated, long Sequence);  // monotonic per room
```

**Role gating:** `ReportSongEnded` and `UpdateStageCapabilities` are accepted only from
connections that joined with `Role == "stage"`; guest-role invocations receive
`HubError(ForbiddenRole)`. This prevents a guest from spoofing stage reports or ending songs
for everyone.

**Method-name parity** is enforced by `Karaokay.UnitTests/SignalRContractTests`, which reflects
over `IStageHub`/`IStageHubClient` and compares against `web/src/lib/signalr/protocol.ts` constants —
the compile-time bridge between C# and TS.

---

## 5. Party Room State Machine, Download Lifecycle & Clock Sync

### 5.1 Unified state machine (per queue item × room playback)

```
                    ┌──────────── guest enqueues YouTube result ────────────┐
                    │  (cache hit by videoId? → Ready immediately)           │
 QueueItem:  Queued ───────────► Downloading ───────► Ready ──────► Playing ───────► Ended
                 │                    │                 ▲            │
                 │ (Multitrack lib     │ (fail after 2   │            │
                 │  song = instantly   │  retries)       │            │
                 │  Ready)             ▼                 │            │
                 │                  Failed ─────────────┘(skip removes item)
                 ▼
 Room playback:  Idle ► AwaitingDownload ► Countdown ► Playing ⇄ Paused ► SongEnded ► (advance)

                     AwaitingDownload = head-of-queue item is Downloading
                     Countdown = fixed 4-beat/lead-in + pre-roll; stage buffers audio/video
```

**Rules (all enforced by the single-writer `PlaybackAuthority` hosted service — no other component
mutates room playback state):**

1. **Advance logic:** when a song ends the authority pops the next `Ready` item. The stage
   invokes `ReportSongEnded(queueItemId)` — **idempotent**: reports are ignored unless
   `queueItemId == CurrentQueueItemId`, so a duplicate stage report and the server's
   `startedAt + duration` watchdog (which fires if the stage goes silent) can never
   double-advance. If the head item is `Downloading`, room enters `AwaitingDownload` and
   broadcasts its progress; the stage shows a "Next song: downloading…" interstitial with the
   progress bar.
2. **Download completes while playing:** item moves to `Ready` silently; queue list updates.
3. **Head-of-queue still downloading + user presses Start:** allowed → `AwaitingDownload`; on the
   download-complete event the authority auto-transitions to `Countdown` (no second press needed).
4. **Failure:** item → `Failed`, broadcast `HubError(DownloadFailed)` + toast on phones; queue
   auto-advances past it (stage interstitial shows failure for 4 s).
5. **Countdown:** computed on the stage from `beatMs` (from `gaps[]`/`initialSilenceMs`), but the
   authority still broadcasts `CountdownTicked` so phones can show a mini progress ring. Stage
   begins audio pre-roll at Countdown start to guarantee decode readiness.
6. **Single-writer + persistence:** `PlaybackAuthority` serializes commands via
   `Channel<RoomCommand>`; every state transition is written to the DB before broadcast
   (write-ahead) so a container cold-restart can rebuild `PlaybackStateDto` and guests reconnect
   transparently.

### 5.2 Reconnection handling

- Guests/stages hold `RoomCode + SessionToken`; `HubConnection.onreconnect` → re-`JoinRoom` →
  server sends `JoinedRoom(RoomSnapshot)` (idempotent full-state resync — **no delta replay needed**).
- `QueueDelta.Sequence` is monotonic; a client whose last sequence has a gap ignores deltas and
  invokes a `RequestSnapshot` fallback (defensive only, since full snapshots are cheap at this scale).
- **Two-layer cold start on Azure:** with `minReplicas: 0` the first SignalR connect may take
  ~5–30 s after idle; the web client shows a branded **"waking the stage…"** state and the demo
  README tells recruiters the first load may pause briefly. **Azure SQL auto-pause is the second
  cold layer:** the free tier database pauses after idle and takes 30–60 s to wake on the first
  query, surfacing transient errors (`40613`, `40197`). EF Core `EnableRetryOnFailure` (SQL
  Server provider) absorbs the wake window; the client shows a branded **"waking database…"**
  state alongside the app cold-start state — an idle demo wakes into a loading experience, never
  an error. Client pings during *any* active session count as traffic and prevent scale-to-zero
  mid-demo (see [ADR-001](adr/001-single-replica-signalr-azure.md)).

### 5.3 Clock sync (frame-accurate lyrics/video without WebRTC)

**Server is the wall-clock authority; the audio hardware is the media-clock authority.** These are
reconciled with a two-layer scheme:

```
Layer 1 — Wall-clock offset (NTP-style over SignalR, every 5 s, 8-sample median):
  client sends Ping{t1} → server echoes Pong{t1, t2}
  client: RTT = now4 − t1;  clockOffset = t2 − (t1 + RTT/2)
  serverNow ≈ localNow + clockOffset

Layer 2 — Media clock alignment:
  PlaybackChanged carries (PositionMs, TimeAnchorUtc). Stage computes:
    expectedMs = (serverNow − TimeAnchorUtc) + PositionMs       // pauses folded into PositionMs
  Stage's local audio element is the master sample clock. One-time calibration at playback start:
    anchorCorrection = expectedMs − audio.currentTime*1000
  applied as a constant; audio.currentTime is never re-slaved afterwards (audio is ground truth).
  For <video> (YouTube mode): drift = expectedMs − video.currentTime*1000
    if |drift| > 120 ms once  → video.currentTime = expectedMs/1000  (hard nudge)
    if 40 < |drift| < 120 ms  → video.playbackRate = 1 ± 0.005 for 5 s (soft nudge, inaudible)
  Lyric sweep always renders against audio.currentTime (Web Audio / Oboe / video), so it is
  frame-accurate by construction; wall-clock sync only steers start alignment and phone UI rings.
```

The Android native stage uses the same two layers, except `audio.currentTime` comes from an Oboe
frame counter (`framesConsumed / 48000 * 1000`) exposed through JNI — the C++ mixer callback is the
ground-truth clock there.

**Browser autoplay policy:** `AudioContext` starts suspended until a user gesture. `StageShell`
therefore shows a one-time **"Click to Enable Stage Audio"** overlay before any Web Audio
playback (including `createMediaElementSource` routing for video mode); the click doubles as the
count-in gesture for the first song. Once unlocked, the context stays runnable across songs and
reconnects within the same page session.

---

## 6. Dual-Mode Karaoke Stage Renderer & Android C++ Audio Blueprint

### 6.1 Sweep-fill karaoke rendering math (shared by Canvas & SkiaSharp)

```
T               = current media clock ms (audio.currentTime * 1000, minus anchorCorrection)
view window     = 3–4 lines centered vertically; line advance when T passes the 3rd line's last endMs

Per syllable i (timestamps from lyrics.json):
  fill_i = clamp01( (T − start_i) / max(1, end_i − start_i) )
  Smooth leading edge over ~50 ms: easedFill = fill_i * easeOut( min(1, fillWindow/50ms ) )
  For connectsToNext words: sweep continues across syllables without visual seam
    (compute fill at word granularity: cumulative duration from word start).
  Talky syllables: flat fill (no eased sweep, no active-syllable pulse).

Draw pass per line:
 1. Measure each syllable width w_i at render font (Canvas: ctx.measureText cache per song load).
 2. Layout with word spacing (2ch gap between connect groups, 1ch between words).
 3. Sung portion: ctx.save(); ctx.beginPath(); ctx.rect(lineX, 0, lineX + partialWidth, lineH);
    ctx.clip(); drawText(filledColor); ctx.restore(); then drawText(unfilledColor) underneath —
    partialWidth interpolates INSIDE the active syllable:
      activeWidth = w_i * fill_i  (plus full widths of all earlier syllables in the word/line).
 4. Active syllable gets a subtle 1.15× horizontal scale pulse (Rock Band feel), driven by fill_i.
 5. Next upcoming line: dim unfilled style + smaller font (0.85×); previous line fades out over 300 ms.

Countdown dots after a gap (gaps[].renderCountdown):
  beatsRemaining = ceil( (gap.toMs − T) / beatMs )   // clamp to countdownBeats
  Draw N hollow circles top-center; circle k is filled when
      T >= gap.toMs − (k+1) * beatMs  → fill sweeps inside one beat (same clip-rect math, radial).
```

`CanvasLyricsRenderer.ts` (browser) and `KaraokeRenderer.cs` (SkiaSharp `SKCanvasView` on
Android) implement identical math; golden-file unit tests feed a synthetic clock to both test
harnesses and assert identical fill sequences — this is [ADR-003](adr/003-dual-mode-stage-web-vs-native.md)'s
"one renderer contract, two surfaces" guarantee.

### 6.2 Android C++ audio engine (pure .NET for Android + Oboe)

The Android TV client is pure .NET for Android — native `Activity` + `FrameLayout`/`SurfaceView`
views, `SkiaSharp.Views.Android` (`SKCanvasView`) for the stage canvas, and `[LibraryImport]`
P/Invoke into `libkaraoke-audio.so`.

**Threading model**

```
USB mic input stream (Oboe AudioInputStream)        HDMI/TV output stream (Oboe AudioOutputStream)
  PerformanceMode::LowLatency                        PerformanceMode::LowLatency
  SharingMode::Exclusive (fallback Shared)            Usage::Game, ContentType::Music
  format: Float, mono, 48 kHz (device native)        format: Float, stereo, 48 kHz
  callback: producer ──writes──► SPSC ring ──reads──► callback: consumer = full mix
                                                     callback does: read mic ring
                                                     → 3-bus mixer → reverb wet → drift check
NO locks. NO allocations. NO JNI calls in either callback.
Control plane (C# via JNI) → engine parameters via std::atomic<float>:
  musicGain, guideGain, micGain, dryMicGain, reverbWet
```

**Media decoding (inside `native/karaoke-audio`)**

- **Multitrack songs:** NDK `AMediaExtractor` + `AMediaCodec` decode `instrumental.opus` and
  `vocals.opus` into 48 kHz float PCM playback rings consumed by the mixer callback (Opus decode
  via MediaCodec is supported on Android 10+; the Onn 4K Pro runs Android 12+). The mixer
  callback never touches compressed audio.
- **YouTube songs:** ExoPlayer renders video to the `SurfaceView`; a custom ExoPlayer
  `BaseAudioProcessor` captures decoded audio PCM and pushes it via JNI into a dedicated SPSC
  ring in `oboe_engine.cpp`, replacing ExoPlayer's own `AudioTrack` sink (no double playback).
  Video pacing stays on ExoPlayer's clock, nudged against `engine_get_position_ms()` with the
  same hard/soft nudge strategy as the browser (§5.3), so video, PCM ring, and lyric clock stay
  frame-accurate.

**Vocal monitoring on HDMI (slapback prevention)**

The USB-in → HDMI-out path includes TV audio DSP latency (often 30–100 ms+). A full-level dry
replica of the singer's voice is speech-jamming at those delays, so `dryMicGain` defaults to
**0.0** and live vocals are monitored primarily through the Schroeder reverb bus (wet ≈ 0.25) —
room ambience without a distinct echo. `dryMicGain` remains a stage toggle for low-latency
displays or headphone setups.

**Stream configuration** (per [ADR-002](adr/002-android-oboe-hdmi-usb-drift.md)): output
`setBufferSizeInFrames(stream->getFramesPerBurst() * 2)`; input burst-sized reads. Both streams
opened `unordered`/concurrent if the device supports it; engine verifies `getTimestamp()`
availability.

**SPSC ring buffer sizing**

```
48 kHz, bursts of 96–256 frames typical on Android TV HALs.
Ring capacity: 8192 frames (170.7 ms) — large enough to absorb USB scheduling jitter
(USB full-speed 1 ms frames) and HAL burst bursts; small enough that total mic latency
stays under ~200 ms. Power-of-two, single allocation, head/tail as relaxed atomics +
acquire/release semantics; producer never blocks (overrun drops oldest sample, counted).
```

**USB-to-HDMI crystal drift compensation (consumer-side, in the output callback)**

```
Target fill  = N/2 (4096 frames)
Low mark     = 0.35 N (underrun imminent)
High mark    = 0.65 N (overrun imminent)

Every output callback (after reading the burst):
  f = ring.fillLevel()
  if f < lowMark  → read the last sample again (duplicate 1 sample: inserts time,
                    slowing the mic signal relative to the faster HDMI clock)
  if f > highMark → skip/drop 1 sample (speeds the mic signal up)
  Rate-limit: at most ONE compensation sample per 100 ms window (≤ 0.1% speed error
              absorbed — inaudible, prevents oscillation).
  Fill level + compensation events exported as atomics for telemetry/UI overlay
  (portfolio gold: a live "clock drift" graph on the stage debug page).
Rationale: HDMI DAC and USB ADC run on independent crystals with ppm-level offset
(~50 ppm → ~2.4 samples/sec at 48 kHz). Android provides no cross-device resampler
for arbitrary USB devices, so the app owns drift correction.
```

**Real-time mixer + Schroeder reverb**

```
out[t] = musicGain*musicBus[t] + guideGain*guideBus[t]
       + micGain*(dryMicGain*dryMic[t] + reverbWet*schroeder(mic[t]))
// for YouTube songs, the ExoPlayer PCM ring replaces musicBus/guideBus

Schroeder (Freeverb-class), float at 48 kHz:
  8 parallel comb filters, delays 1557, 1617, 1491, 1422, 1277, 1356, 1188, 1116 samples
  (scaled ×48/44.1 from classic values), feedback 0.84, damping (lowpass in comb) 0.2
  4 serial allpass: 225, 556, 441, 341 samples, gain 0.5
  Default wet: 0.25 (the primary vocal monitoring path given dryMicGain = 0)
All state in preallocated static per-channel arrays; stereo output = two decorrelated
runs (comb delays offset ±11 samples).
```

**P/Invoke surface (JNI bridge):** `engine_start(bufferSize)`, `engine_stop()`,
`engine_load_track(path, offsetMs)` (backed by the AMedia decoder), `engine_load_guide(path)`,
`engine_set_gains(music, guide, mic, dry, wet)`, `engine_get_position_ms()` (ground-truth media
clock for the renderer), `engine_get_drift_stats()`. `AudioEngineInterop.cs` wraps these with
`[LibraryImport]`; no managed code ever touches an audio callback.

---

## 7. Phased Vertical-Slice Implementation Roadmap

8 slices; each ends in a demoable, tested increment. Slices 1–6 are CI-green on free Ubuntu
runners throughout.

| Order | Slice | Unlocks | Biggest risk & built-in mitigation |
|---|---|---|---|
| 1 | Generator + parser | everything (content without copyright) | MIDI tick math errors → golden files lock it down first |
| 2 | Browser stage | the visual centerpiece | Renderer perf debt later → no-per-frame-allocation rule from day one |
| 3 | Rooms + phones | the party mechanic | SignalR state races → single-writer authority + sequence numbers |
| 4 | Sync | correctness under real networks | drift bugs → audio-as-ground-truth + self-correcting nudges |
| 5 | yt-dlp worker | the wow feature | YouTube flakiness → shimmed-binary integration tests; real yt-dlp only nightly/local |
| 6 | Azure deploy | the recruiter URL | two-layer cold start (app + DB) → reconnect UX + retry strategy (ADR-001) |
| 7 | Oboe engine | the C++ showpiece | device quirks → fake-Oboe/MediaCodec CI harness + telemetry to prove behavior |
| 8 | TV stage + hardening | flagship demo | two-renderer divergence → shared golden fixtures |

Each slice below specifies files to create, required tests, and a copy-paste
**Definition of Done** prompt for an execution agent.

---

### Slice 1 — Foundation, Synthetic Demo Generator & MIDI→lyrics.json parser

**Files to create**

- Solution scaffold: `Directory.Build.props`, `Directory.Packages.props`, `src/apps/*` (empty
  shells), `src/core/{Domain,Application,Infrastructure,Contracts}`, `src/tools/Karaokay.Importer`,
  `src/tools/Karaokay.SyntheticGen`, `tests/*`, `.github/workflows/ci.yml`.
- `SyntheticGen`: `Program.cs` (deterministic seed flag), `MidiScoreComposer.cs` (DryWetMidi:
  writes `PART VOCALS` track with phrase-marker notes 105/106, tempo map, 4/4 beat grid,
  instrumental gaps), `AudioStemBaker.cs` (CliWrap→FFmpeg: sine/chirp-based instrumental & vocal
  stems at 48 kHz stereo Opus, `amix` for instrumental), `LyricsTimelineWriter.cs`.
- `Infrastructure/MediaIngest/MidiLyricsParser.cs` (DryWetMidi `TempoMap` tick→ms; phrase
  boundaries **preferring explicit Rock Band marker notes 105/106** with the
  `PhraseGapMs > 1500 ms` gap heuristic as fallback; connector/talky handling; countdown gap
  derivation), `LyricsSchema.cs` (in Contracts).
- Importer CLI: `scan`/`import`/`verify` commands; `SongIniReader.cs` (name, artist, delay,
  charter flags), `StemMixer.cs` (FFmpeg `amix` of non-vocal stems — **excluding `crowd*.ogg`** —
  into `instrumental.opus`, then EBU R128 `loudnorm` targeting −16 LUFS so multitrack stems
  match YouTube video loudness; `vocals*.ogg` → `vocals.opus`), `MidiFileNameDiscovery.cs`.
- `tests/UnitTests/MidiLyricsParserGoldenTests.cs` + `fixtures/golden/*.json`;
  `SyntheticGenDeterminismTests` (same seed → byte-identical output hashes);
  `ArchitectureTests` (hand-written `Assembly.GetReferencedAssemblies()` reflection rules with
  positive controls — see ADR-005).

**Tests required**

- Unit: tick→ms conversion across multi-tempo maps (golden files); connector/talky
  classification; phrase grouping via marker notes (marker fixture) and via gap fallback
  (no-marker fixture); phrase gap + countdown beat math; 4-letter code generator
  alphabet/collision.
- Integration: SyntheticGen runs in CI (FFmpeg via apt in workflow), produces 3 songs, importer
  `verify` validates every `lyrics.json` via `LyricsJson.Validate` + a byte-identical round-trip parse.

**Definition of Done**

> "Implement Slice 1 per docs/ARCHITECTURE.md §7. Definition of Done: (1) `dotnet run --project
> Karaokay.SyntheticGen -- --seed 42 --out demo-tracks` produces 3 deterministic songs each with
> `instrumental.opus`, `vocals.opus`, `lyrics.json`, and identical SHA-256 across two runs; (2) the
> DryWetMidi parser converts those same MIDIs to golden `lyrics.json` files that byte-match
> committed fixtures, including one multi-tempo song, one talky-phrase song, one marker-delimited
> phrase fixture (105/106 boundaries take precedence over the gap heuristic), and one ≥4-beat
> instrumental gap with `renderCountdown:true`; (3) importer CLI `import <extracted-rb-folder>`
> (test fixture with synthetic stand-in stems) produces the same three-file layout, excludes
> `crowd*.ogg` from the instrumental `amix` (asserted on the generated filter graph), and
> normalizes `instrumental.opus` to within ±1 LU of −16 LUFS (verified via `loudnorm` print
> stats); (4) CI workflow builds, runs all unit + golden + architecture tests on ubuntu-latest
> with zero paid dependencies; (5) no binary media over 100 KB is committed except the generated
> demo stems, which are public-domain synthetic."

---

### Slice 2 — Media serving + Browser Stage: Web Audio engine & Canvas sweep-fill renderer

**Files**

- API: `SongsEndpoints.cs` (library list DTOs), `MediaEndpoints.cs` (range-enabled static file
  serving of `instrumental.opus`/`vocals.opus`/`lyrics.json` under `MediaRoot`), cache headers.
- Web: `src/features/stage/WebAudioEngine.ts` (two `AudioBufferSourceNode`s → `GainNode` buses +
  master, `currentTime` clock, pre-buffer promise before countdown ends),
  `CanvasLyricsRenderer.ts` (§6.1 math: line layout, clip-rect sweep, countdown dots, talky
  flat-fill), `StageShell.tsx` (video-mode placeholder div + a one-time **"Click to Enable Stage
  Audio"** overlay that unlocks the suspended `AudioContext` before any Web Audio playback),
  `useStageClock.ts`, `DemoTrackPicker.tsx` (local-only, no hub yet — picks one of the
  bundled demo songs).

**Tests**

- Unit (Vitest): renderer fill math against golden fixture clock series (100 sampled timestamps →
  exact fill values + active line index); line-break layout; countdown dot states; talky
  flat-fill behavior.
- Playwright (`e2e/`): loads demo stage, asserts canvas pixels at T=0 vs T=mid-phrase via
  screenshot diffing with a synthetic deterministic song; asserts `instrumental` and `vocals`
  `GainNode` gains respond to slider changes; asserts no playback path is exercised before the
  unlock click and audio starts only after it plus the completed countdown (mocked AudioContext
  clock).

**Definition of Done**

> "Implement Slice 2 per docs/ARCHITECTURE.md §7. Definition of Done: (1) browser stage at /stage
> plays a bundled demo song with both stems via Web Audio, countdown dots then sweep-fill
> syllables that finish exactly at each syllable's endMs (golden Vitest fixtures for fill math,
> Playwright pixel test at 3 sampled times); (2) the 'Click to Enable Stage Audio' gesture unlocks
> the suspended `AudioContext` on first interaction and no playback path (including
> `createMediaElementSource` routing) is attempted before it; (3) guide-vocal slider crossfades
> the vocals bus 0→1 in <50 ms with no audio dropout; (4) renderer never allocates per-frame (no
> measureText in the RAF loop — cached widths); (5) all e2e green headless on CI with a fake
> audio device."

---

### Slice 3 — SignalR Room Hub, queue engine & mobile guest remote

**Files**

- API: `Hubs/StageHub.cs` (+ contracts from §4.3), `RoomCodeGenerator.cs`, `RoomRegistry.cs`
  (singleton), `QueueService.cs`, `Features/RoomsEndpoints.cs`; `PartyRoom/QueueItem/Song` EF
  entities + `KaraokayDbContext` + migrations (Npgsql first, with the provider-specific
  `HasFilter` syntax switch for the `Song.VideoId` filtered unique index); Aspire `AppHost`
  wiring Postgres; OpenAPI + `openapi-typescript` build step.
- Web: `src/lib/signalr/protocol.ts` + `stageClient.ts` (typed hub proxy, auto-reconnect +
  snapshot rejoin), `src/features/remote/` (JoinScreen with code input, LobbyView showing room
  code + QR (QRCoder server-side PNG endpoint), LibraryTab, QueueList with reorder/remove,
  playback controls).

**Tests**

- Integration (Testcontainers Postgres + `WebApplicationFactory` + SignalR client): create room →
  code format asserts (no I/O/Q/0/1, uniqueness over 500 sequential rooms); guest join →
  snapshot received; enqueue/move/remove → `QueueChanged` deltas with monotonic sequence; second
  connection rejoining gets identical snapshot.
- Security: `ReportSongEnded` and `UpdateStageCapabilities` are rejected with
  `HubError(ForbiddenRole)` for connections joined with `Role == "guest"`.
- Unit: `RoomCommand` serialization contract parity C#↔TS (`SignalRContractTests`).
- Playwright: two browser contexts (phone + stage) — phone enqueues, stage view queue updates
  within 1 s; disconnect phone, reconnect, state restored from snapshot.

**Definition of Done**

> "Implement Slice 3 per docs/ARCHITECTURE.md §7. Definition of Done: (1) stage can create a
> room, sees its 4-letter code + QR (served as PNG), phone joins by code and both see the same
> queue in real time; (2) enqueue/reorder/remove propagate as sequence-numbered deltas and a
> reconnecting client converges via full snapshot; (3) room codes are collision-free under DB
> unique index + retry generation; (4) TS API types are generated from the OpenAPI document in
> the build (no hand-written DTO duplication) and `SignalRContractTests` proves C#/TS
> method-name parity; (5) integration tests run against real Postgres via Testcontainers in CI;
> (6) stage-only hub methods (`ReportSongEnded`, `UpdateStageCapabilities`) reject guest-role
> connections while working for the stage."

---

### Slice 4 — Playback authority, clock sync & YouTube video mode in the browser stage

**Files**

- API: `PlaybackAuthority.cs` (command channel, single writer, watchdog, `ReportSongEnded`
  idempotency guard), `PlaybackStateDto` broadcasts, `Ping/Pong` clock endpoint in hub,
  `SetGuideVocalVolume`.
- Web: `lib/signalr/clockSync.ts` (Ping/Pong sampler with the 8-sample median offset filter; the
  planned `useServerClock.ts` name never existed — see
  [ADR-008](adr/008-slice-4-playback-authority-decisions.md)), stage `playbackController.ts`
  (audio-as-ground-truth calibration, one-time server-anchor alignment, hard/soft video nudges per
  §5.3), and `useStageVideo.ts` with `StageShell.tsx` hosting the `<video>` element (routed through
  `AudioContext.createMediaElementSource` into the same master + mic bus so guest voice still
  mixes; the planned `YouTubeVideoSurface.tsx` never existed), stage interstitials
  (AwaitingDownload, SongEnded, Idle with QR corner overlay).

**Tests**

- Integration: full round-trip — Start → `PlaybackChanged(Countdown, PositionMs=0,
  TimeAnchorUtc=countdown anchor)` → `CountdownTicked` beats → `PlaybackChanged(Playing,
  PositionMs=0, fresh TimeAnchorUtc)` (two-phase Start; see
  [ADR-008](adr/008-slice-4-playback-authority-decisions.md)); pause/resume preserves position
  within ±30 ms of authority's `PositionMs`;
  skip advances; ended watchdog fires when stage silent for `duration+5s`; `GuideVocalVolume`
  broadcast reaches all clients; **double-advance protection — a second `ReportSongEnded` (or the
  watchdog firing after the stage report) is ignored because `queueItemId != CurrentQueueItemId`
  after advance**.
- Playwright (synthetic clocks): stage audio start time aligned to server anchor within ±150 ms
  across simulated 80 ms RTT; pause at t=5 s, resume, lyrics continue from correct position;
  YouTube video mode (`<video>` with a bundled sample file) drift-corrects a +300 ms injected
  offset via hard nudge.

**Definition of Done**

> "Implement Slice 4 per docs/ARCHITECTURE.md §7. Definition of Done: (1) any phone can
> Start/Pause/Resume/Skip and all clients plus the stage converge on the authoritative position;
> (2) the stage renders lyrics/video from its own audio element as ground truth after one-time
> server-anchor calibration, and a test injecting ±300 ms desync on the video path self-corrects
> within 2 s without audible artifacts; (3) the clock offset estimator reports RTT and offset and
> tolerates reconnection mid-song; (4) room-level watchdog ends stalled playback; (5) a
> duplicate `ReportSongEnded` racing the watchdog never double-advances the queue (idempotency
> guard `queueItemId == CurrentQueueItemId`); (6) Playwright passes with simulated latency via
> CDP network throttling."

---

### Slice 5 — YouTube zero-key search, yt-dlp download worker & progress streaming

**Files**

- `Infrastructure/MediaIngest/YouTubeSearchService.cs` (CliWrap:
  `yt-dlp "ytsearch10:{q} karaoke" --dump-json --flat-playlist --no-warnings`, sanitize query,
  15 s timeout, result DTO mapping), `DownloadJobChannel.cs`
  (`Channel<YouTubeDownloadJob>` bounded capacity 4, `BoundedChannelFullMode.Wait`),
  `YouTubeDownloadWorker.cs` (`BackgroundService`: cache-hit check by `videoId`, downloads
  constrained to **H.264/AAC in MP4** via `yt-dlp -S "vcodec:h264,acodec:aac"
  --merge-output-format mp4 --newline --progress-template`, progress regex →
  `DownloadProgressChanged` broadcast, 2 retries, failure → `Failed` + toast), `DownloadJob`
  entity + cache LRU eviction quota job, `Features:EnableYouTubeIngestion` flag gating
  search+enqueue (cloud: `HubError(YouTubeDisabled)`), `server.Dockerfile` with `yt-dlp` +
  `ffmpeg` + `deno 2.3` layers; `docker-compose.yml` documents an **optional `cookies.txt`
  volume mount** for rate-limit resilience.
- Web: `YouTubeTab.tsx` (debounced search → results grid), queue rows show live progress bars,
  cached-badge.

**Tests**

- Unit: progress line parser (fixtures of yt-dlp `--newline` output), result JSON mapper,
  cache-hit path short-circuits channel push, generated command line contains the format
  selector flags.
- Integration: worker against a **shimmed yt-dlp binary** (recorded transcript) — enqueue
  YouTube result → `QueueItem` appears `Downloading` → progress events at 25/50/75/100 → file
  exists at `CachePath` (`{videoId}.mp4`) and `ffprobe` asserts H.264 video + AAC audio in MP4
  container → `Ready`; failure shim → retry then `Failed`; feature flag off → search/queue
  rejected with `HubError(YouTubeDisabled)`.
- E2E (self-host compose only): real `yt-dlp` smoke test, marked `[Category:RequiresYouTube]`,
  runs nightly/locally, **skipped in CI** (network-flaky, still $0).

**Definition of Done**

> "Implement Slice 5 per docs/ARCHITECTURE.md §7. Definition of Done: (1) guest search needs no
> API key and returns typed DTOs within 15 s or a clean timeout error; (2) enqueueing a result
> creates the Song + DownloadJob, immediately sets the queue item Downloading with live
> percentage events (verified via shimmed-binary integration tests at 25/50/75/100%); (3) cache
> hit by videoId returns Ready instantly with no download; (4) bounded channel serializes
> downloads, failure retries twice then marks Failed and the room auto-advances; (5)
> `Features:EnableYouTubeIngestion=false` disables search and enqueue everywhere with a friendly
> client message; (6) downloads are constrained to H.264/AAC in MP4 (asserted via `ffprobe` on
> shimmed output so cache files match `{videoId}.mp4` and hardware decode on browsers and
> Android TV is guaranteed), the Docker image builds with yt-dlp, FFmpeg, and Deno 2.3, and
> `docker compose up` (with the optional `cookies.txt` mount documented in the compose file)
> yields a fully working home-server stack."

---

### Slice 6 — Azure zero-cost deployment, cloud isolation & CI/CD

**Files**

- `infra/azure/main.bicep` (SWA free, ContainerApp consumption `minReplicas:0 maxReplicas:1` +
  sticky session affinity, Azure SQL free tier with auto-pause, log analytics free),
  `swa.config.staticwebapp` (routes, demo-asset caching), `appsettings.cloud.json` (SqlServer
  provider, `EnableYouTubeIngestion=false`, demo library = static assets list), EF Core SQL
  Server provider configured with **`EnableRetryOnFailure` tuned for the 30–60 s auto-pause wake
  window (transient errors `40613`/`40197`)**, SqlServer migration set, web client
  **"Waking database…"** state alongside the "waking the stage…" cold-start state,
  `.github/workflows/deploy-demo.yml` (build → GHCR → bicep deploy → SWA), `web` build step
  copying `public/demo-tracks`, `docs/self-hosting.md` + README live-demo section + architecture
  diagram (`docs/diagram.drawio` → exported PNG).

**Tests**

- Deploy workflow smoke: scheduled (weekly, free runner) Playwright run against the live demo —
  join room, enqueue demo song, stage plays, lyrics render; alerts via workflow failure badge.
- Integration: provider-agnostic migration test matrix (Testcontainers Postgres + SqlServer Docker
  container) — same seed/query results on both providers, including the `HasFilter` filtered
  unique index syntax; a fault-injecting EF interceptor simulates `40613`/`40197` transients and
  asserts the first query after "pause" succeeds within the retry window while the client
  surfaces the "Waking database…" state.

**Definition of Done**

> "Implement Slice 6 per docs/ARCHITECTURE.md §7. Definition of Done: (1) one GitHub Actions
> workflow deploys the web client to Azure Static Web Apps (free) and the API to a single-replica
> scale-to-zero Container App with HTTP session affinity, with Azure SQL free tier persistence;
> (2) the live demo URL in the README boots from idle in <90 s across both cold-start layers
> (app + database) and a weekly scheduled Playwright smoke test keeps a green badge; (3) cloud
> mode provably has YouTube ingestion disabled (integration test) and serves demo songs purely
> as static frontend assets — no media egress through the API; (4) identical domain queries pass
> on both Npgsql and SqlServer via the provider-matrix test, including the provider-specific
> `HasFilter` filtered-index syntax; (5) the EF Core retry strategy absorbs simulated
> 40613/40197 transients after idle and the web client shows a branded 'Waking database…'
> state during the wake window; (6) the whole stack costs $0/month verified by documented SKU
> choices in docs/self-hosting.md."

---

### Slice 7 — Android C++ Oboe audio engine (native library + P/Invoke + drift telemetry)

**Files**

- `clients/stage-android/native/karaoke-audio/`: `CMakeLists.txt`, `oboe_engine.cpp`
  (open/close streams, callback graph), `spsc_ring.h`, `decoder_amedia.cpp`
  (`AMediaExtractor` + `AMediaCodec`: Opus stems → 48 kHz float PCM playback rings),
  `youtube_pcm_jni.cpp` (JNI SPSC ring receiving ExoPlayer `BaseAudioProcessor` PCM, with
  `engine_get_position_ms()`-aligned pacing), `mixer.cpp` (3-bus, no alloc),
  `schroeder.cpp` (Freeverb-class params §6.2), `drift_compensator.cpp` (±1 sample, 100 ms rate
  limit, stats atomics), `jni_bridge.cpp`, `gtest` suite (`ring_tests`, `drift_tests` with
  simulated ±80 ppm clocks, `decoder_tests` against FFmpeg reference decodes, `reverb_tests`
  against reference impulse responses, `mixer_tests` with `dryMicGain = 0` default routing).
- App side: `AudioEngineInterop.cs` (`[LibraryImport]` wrappers), `DriftStatsOverlay.cs`
  (debug View), csproj (`net10.0-android`) NDK/CMake integration, USB-audio permission wiring.

**Tests**

- Native gtest (runs on Linux desktop with a fake Oboe + fake MediaCodec layer in CI): SPSC
  correctness under 1M random burst sizes; drift compensator drives simulated fill level back to
  target under ±80 ppm and never compensates more than once per 100 ms; `decoder_amedia` output
  matches an FFmpeg reference decode of the same demo Opus stems; mixer output equals per-bus
  reference mix within 1e-6 with the `dryMicGain = 0`, reverb-primary vocal routing.
- On-device smoke checklist (documented, manual): Onn 4K Pro + USB mic → HDMI, round-trip
  latency screenshot via drum-test method, drift graph stable ±0.05% over 10 min.

**Definition of Done**

> "Implement Slice 7 per docs/ARCHITECTURE.md §7. Definition of Done: (1) `libkaraoke-audio.so`
> builds via CMake/NDK from the `net10.0-android` csproj and also compiles+tests natively on
> ubuntu CI with a fake-Oboe/fake-MediaCodec harness; (2) all gtests pass: SPSC under random
> bursts, drift compensator recovers from simulated ±80 ppm clock mismatch within 5 s and stays
> within 35–65% fill thereafter with ≤1 correction per 100 ms, the AMedia decoder matches the
> FFmpeg reference decode of the demo stems, and mixer + Schroeder match reference outputs with
> the dry-mic-default routing (`dryMicGain = 0`, vocals monitored via the reverb bus); (3) a
> debug overlay shows live ring fill and corrections/sec while a USB mic is connected, proving
> full-duplex USB-in → HDMI-out; (4) no heap allocation or lock acquisition occurs in either
> audio callback (enforced by a debug-only allocation guard test hook)."

---

### Slice 8 — Android dual-mode Stage UI: SkiaSharp renderer, ExoPlayer video mode, lobby & hardening

**Files**

- `LobbyActivity.cs` (room code + QR), `KaraokeRenderer.cs` (SkiaSharp `SKCanvasView` port of
  §6.1, golden fixtures shared from Slice 2), `CountdownDots.cs`, `VideoSurfaceView.cs`
  (ExoPlayer video; a custom `BaseAudioProcessor` pushes decoded PCM via JNI into the engine's
  YouTube SPSC ring — no `AudioTrack` output — with video pacing nudged against
  `engine_get_position_ms()` per §5.3), `StageActivity.cs`, persistent corner QR overlay,
  `RoomConnectionService.cs` (typed SignalR + snapshot rejoin), `settings.json` for server URL.
- Cross-cutting hardening everywhere: reconnect storm tests, room GC after 12 h, queue
  pagination guard, README GIFs, `docs/ARCHITECTURE.md` final pass, portfolio polish (CI badges,
  live demo link, ADR links from README).

**Tests**

- Renderer: same golden clock fixtures run through SkiaSharp in an xunit harness comparing
  fill/active-line/dot sequences to the TS implementation's exported expected JSON — byte-parity
  of *decisions*, not pixels.
- Playwright E2E finale (browser demo): full recruiter journey — open live demo, join from
  emulated phone, enqueue two synthetic songs, watch stage transition Countdown→Playing→
  SongEnded→next song, guide-vocal slider live.
- Manual checklist for the Android TV path (device-only) committed as `docs/tv-checklist.md`.

**Definition of Done**

> "Implement Slice 8 per docs/ARCHITECTURE.md §7. Definition of Done: (1) the Android TV app
> joins a room, displays lobby code + QR, and plays a multitrack song with SkiaSharp sweep-fill
> syllables and countdown dots driven by the Oboe engine's frame-accurate position — behavioral
> parity with the web renderer proven by the shared golden fixture suite; (2) YouTube songs
> switch the viewport to ExoPlayer video with audio routed through the `BaseAudioProcessor` →
> JNI SPSC ring so the live mic still mixes over the video, and video pacing stays locked to
> the engine clock; (3) room code + QR persists as a corner overlay during songs; (4)
> disconnect/reconnect of the TV mid-song restores exact playback position from the authority
> snapshot; (5) the final Playwright run passes end-to-end against the deployed Azure demo and
> the README shows green CI badges, the live URL, and an architecture diagram — the portfolio
> is complete."
