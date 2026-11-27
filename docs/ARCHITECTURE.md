# Karaokay — Architectural Blueprint & Phased Implementation Plan

> **Status:** Accepted · **Version:** 1.4 · **Date:** 2026-11-27
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
> 1.3 — Slice 6 split into 6a (Azure zero-cost demo) and 6b (self-hosted Proxmox LXC, systemd,
> Nginx Proxy Manager), with the self-host deployment topology recorded in ADR-009. 1.4 — Rock Band
> DLC pre-stage: content-based container detection and optional Onyx extraction/import documented
> (ADR-010, [`docs/specs/rb-dlc-onyx-prestage.md`](specs/rb-dlc-onyx-prestage.md)).
>
> Companion decision records: [ADR-001](adr/001-single-replica-signalr-azure.md) ·
> [ADR-002](adr/002-android-oboe-hdmi-usb-drift.md) ·
> [ADR-003](adr/003-dual-mode-stage-web-vs-native.md) ·
> [ADR-004](adr/004-dual-source-media-pipeline.md) ·
> [ADR-005](adr/005-slice-1-blueprint-reconciliations.md) ·
> [ADR-006](adr/006-line-wrapping-ownership.md) ·
> [ADR-007](adr/007-slice-4-5-deliverable-ordering.md) ·
> [ADR-008](adr/008-slice-4-playback-authority-decisions.md) ·
> [ADR-009](adr/009-self-hosted-lxc-behind-reverse-proxy.md) ·
> [ADR-010](adr/010-onyx-subprocess-dlc-extraction.md)

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
| C1 | **$0/month, permanently.** Every tool, library, CI runner, and cloud hosting target must operate at $0/month. No expiring 12-month free trials, no paid API keys, no paid Redis backplanes. | Azure SWA Free, ACA Consumption (`minReplicas: 0`), Azure SQL Free tier, GitHub free runners, zero-key `yt-dlp` search; self-host runs on the operator's own Proxmox hardware with free DuckDNS + Let's Encrypt. Cost/SKU tables in `docs/self-hosting.md` (self-host + Azure cross-check). |
| C2 | **Zero copyrighted assets in Git.** Never commit commercial Rock Band charts, audio stems, or downloaded YouTube videos. | Standalone `Karaokay.Importer` CLI processes users' own extracted folders locally — and, for encrypted store DLC, detects containers by content and optionally runs the GPL **Onyx** toolkit as an external process (never bundled; ADR-010); deterministic `Karaokay.SyntheticGen` tool generates 2–3 public-domain multitrack demo songs committed to the repo so CI and recruiters run the full stack out of the box. |
| C3 | **Two deployment modes from one codebase.** *Self-hosted* (unprivileged Proxmox Debian LXC, bare-metal systemd + Kestrel serving the built SPA, PostgreSQL, full `yt-dlp` ingestion, Nginx Proxy Manager TLS; Aspire is dev-time orchestration and the Docker Compose image specced for Slice 5 (not yet implemented) is the secondary path) and *Public portfolio demo* (Azure free tiers, Azure SQL, ingestion disabled via feature flag, demo songs bundled as static frontend assets). | `Features:EnableYouTubeIngestion` flag + provider-agnostic persistence (Npgsql / Microsoft.EntityFrameworkCore.SqlServer). |
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

1. **Self-Hosted Mode** — one unprivileged Debian 12/13 LXC on Proxmox VE runs the API as a
   bare-metal `systemd` service. Kestrel serves the built SPA from `wwwroot`, the REST API, and
   SignalR from a single origin; PostgreSQL listens on loopback; an existing Nginx Proxy Manager
   LXC terminates TLS for `https://karaokay.delvserv.duckdns.org` (free DuckDNS + Let's Encrypt)
   and proxies WebSockets. Full YouTube search/download (`yt-dlp`, FFmpeg, Deno 2.3+) is enabled.
   .NET Aspire remains dev-time orchestration, and the Docker Compose image specced for Slice 5
   (not yet implemented) is the secondary home-server path. See [ADR-009](adr/009-self-hosted-lxc-behind-reverse-proxy.md).
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
│   │   ├── 005-slice-1-blueprint-reconciliations.md
│   │   ├── 006-line-wrapping-ownership.md
│   │   ├── 007-slice-4-5-deliverable-ordering.md
│   │   ├── 008-slice-4-playback-authority-decisions.md
│   │   └── 009-self-hosted-lxc-behind-reverse-proxy.md
│   ├── data-contracts.md               # lyrics.json + SignalR reference for contributors
│   └── self-hosting.md                 # self-hosted LXC guide + $0 cost table + Azure SKU cross-check
│
├── src/
│   ├── apps/
│   │   ├── Karaokay.Api/              # ASP.NET Core host (Minimal APIs + SignalR)
│   │   │   ├── Hubs/StageHub.cs  Hubs/StageHubContracts.cs
│   │   │   ├── Endpoints/ (SongsEndpoints.cs, RoomsEndpoints.cs, MediaEndpoints.cs)
│   │   │   ├── Features/ (RoomService, QueueService, PlaybackAuthority, RoomCodeGenerator)
│   │   │   └── Program.cs  appsettings.json  appsettings.cloud.json  appsettings.selfhost.json
│   │   ├── Karaokay.AppHost/          # .NET Aspire orchestrator (dev-time self-host)
│   │   └── Karaokay.ServiceDefaults/  # shared telemetry/health/resilience config
│   │
│   ├── core/
│   │   ├── Karaokay.Domain/            # entities, enums, domain events
│   │   ├── Karaokay.Application/       # queue engine, playback authority, room state machine
│   │   ├── Karaokay.Infrastructure/
│   │   │   ├── Persistence/ (KaraokayDbContext, configurations, migrations)
│   │   │   └── MediaIngest/ (MidiLyricsParser, StemMixer, SongFolderProbe, MediaStore,
│   │   │                    YouTubeSearchService, YouTubeDownloadWorker, DownloadJobChannel)
│   │   └── Karaokay.Contracts/          # DTOs shared with clients, lyrics.json types
│   │
│   ├── tools/
│   │   ├── Karaokay.Importer/          # CLI: ingest extracted Rock Band folders → library + lyrics.json;
│   │   │   │                          #      optional Onyx pre-stage decrypts encrypted DLC (ADR-010)
│   │   │   ├── Commands/ (`scan <root>`, `import <dir|file>`, `verify <dir>`)
│   │   │   └── Dlc/ (DlcContainerDetector, OnyxLocator, OnyxDlcExtractor)
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
│   ├── aspire/ (dev-time self-host wiring)
│   ├── azure/ (main.bicep, containerapp.tmpl.yaml, swa.config.staticwebapp, deploy scripts)
│   └── mediastore/ (volume layout doc: /media/{library,ytcache})
│
├── scripts/
│   ├── proxmox/ (create-karaokay-lxc.sh, provision-karaokay.sh, karaokay-api.service)
│   └── deploy-selfhost.sh            # publish + web build + rsync + migrations + restart
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
    public long QueueSequence { get; set; }              // monotonic delta sequence; write-ahead minted
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

`Countdown` is **start-only**: `Start` (or the automatic start after an awaited download) enters
it; the advance path (song ended, skip) goes straight back to `Playing` with a fresh anchor and
never re-enters the count-in. A `Pause` during the count-in is supported: the room parks at
`Paused`/position 0, and `Resume` starts with a fresh anchor. The stage renders a lead-in for the
song itself from its local media; the room's count-in dots show the server's fixed four-beat cue
(see rule 5).

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
3. **Head-of-queue still downloading + user presses Start:** allowed → `AwaitingDownload`; when the
   item becomes `Ready` the authority starts the count-in itself (no second press needed). The
   transition is guarded on the room still awaiting that exact cursor item, so a concurrent
   Start/Skip/Remove wins cleanly; a failed awaited download returns the room to `Idle` so a later
   Start can pick the next playable item.
4. **Failure:** item → `Failed`, broadcast `HubError(DownloadFailed)` + toast on phones; a queue that
   is already advancing skips past it (stage interstitial shows failure for 4 s). If the failed item
   was the one an `AwaitingDownload` room was parked on, the room returns to `Idle` so a later Start
   can choose the next playable item.
5. **Countdown:** server-authoritative and fixed: four 500 ms beats (`CountdownBeats = 4`,
   `CountdownBeatMs = 500`), broadcast as `CountdownTicked(queueItemId, remainingBeats, beatMs)` with
   `remainingBeats` running 4 → 1. Phones render a mini ring from that contract and animate it
   locally per tick; in room mode the stage shows the fixed four-beat count-in carried by the Start
   broadcast (`StageShell` reads `countdownRemainingBeats` from that one message, so its dots do not
   decrement per beat). Only the database-less local `DemoTrackPicker` derives dots from
   `lyrics.json` gaps; room playback
   does not. The authority still begins audio pre-roll at Countdown start to guarantee decode
   readiness, and a hard restart mid-count-in resumes at the next due beat instead of replaying
   beats the previous process already sent.
6. **Single-writer + persistence:** `PlaybackAuthority` serializes commands via
   `Channel<RoomCommand>`; every durable state transition is written to the DB before broadcast
   (write-ahead, and playback + queue writes for one transition share a single `SaveChanges`), so a
   container cold-restart can rebuild `PlaybackStateDto` and guests reconnect transparently. The
   count-in tick is the one exception: it mutates derived in-memory remaining-beat progress and
   broadcasts `CountdownTicked` without a DB write (the durable step is the persisted transition
   into and out of `Countdown`).

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
Layer 1 — Wall-clock offset (NTP-style over SignalR, every 5 s, min-RTT best-of-window):
  client sends Ping{t1} → server echoes Pong{t1, t2}
  client: RTT = now4 − t1;  clockOffset = t2 − (t1 + RTT/2)
  serverNow ≈ localNow + clockOffset
  the last 8 exchanges form the window; samples whose RTT exceeds 250 ms are rejected and the
  offset comes from the lowest-RTT sample in the window (≥2 accepted samples before it is
  trusted, otherwise the stage falls back to the receipt-time anchor of the room-state message)

Layer 2 — Media clock alignment:
  PlaybackChanged carries (PositionMs, TimeAnchorUtc). Stage computes:
    expectedMs = (serverNow − TimeAnchorUtc) + PositionMs       // pauses folded into PositionMs
  Stage's local audio element is the master sample clock. One-time calibration at playback start:
    seekOffset = expectedMs − audio.currentTime*1000
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
T               = current media clock ms (audio.currentTime * 1000, minus seekOffset)
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

Nine entries (slices 1–8, with Slice 6 split into **6a** and **6b**); each ends in a demoable,
tested increment. **6a** is the Azure zero-cost demo, CI-green on free Ubuntu runners. **6b** is
the self-hosted Proxmox LXC path, validated by scripted provisioning plus a manual
phone-off-network acceptance checklist. Slices 1–6a are CI-green on free Ubuntu runners throughout.

| Order | Slice | Unlocks | Biggest risk & built-in mitigation |
|---|---|---|---|
| 1 | Generator + parser | everything (content without copyright) | MIDI tick math errors → golden files lock it down first |
| 2 | Browser stage | the visual centerpiece | Renderer perf debt later → no-per-frame-allocation rule from day one |
| 3 | Rooms + phones | the party mechanic | SignalR state races → single-writer authority + sequence numbers |
| 4 | Sync | correctness under real networks | drift bugs → audio-as-ground-truth + self-correcting nudges |
| 5 | yt-dlp worker | the wow feature | YouTube flakiness → shimmed-binary integration tests; real yt-dlp only nightly/local |
| 6a | Azure deploy | the recruiter URL | two-layer cold start (app + DB) → reconnect UX + retry strategy (ADR-001) |
| 6b | Self-hosted LXC | the real home-server install | reverse-proxy host/proto leakage → ForwardedHeaders + PublicBaseUrl precedence tests |
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

**Post-Slice-1 extension — Rock Band DLC pre-stage (ADR-010).** The importer now also accepts
encrypted store DLC. `DlcContainerDetector` classifies *by content* (STFS `CON `/`LIVE`/`PIRS`
magic, PS3 `.pkg`/encrypted `.edat`, bare MOGG, unpacked ARK markers); extensionless filenames are
handled by content, and OS sidecars (`:Zone.Identifier`, `._*`, `.DS_Store`) are ignored. A directory
that already satisfies the extracted-folder contract always takes the normal path. When containers
are found, `OnyxLocator` resolves the user's Onyx executable (`--onyx` > `KARAOKAY_ONYX_PATH` >
`PATH` > repository `.tools/`) and `OnyxDlcExtractor` runs Onyx's two-step CLI — `onyx import
<container> --to <temp project>`, then an injected `ps` target (`game: ps`, `audio-format: ogg`) and
`onyx build <project>/song.yml --target ps --to <temp out>` — after which each produced Phase Shift
folder goes through the unchanged import pipeline. Onyx is optional and never bundled (GPL boundary), and
`SongFolderProbe` is now the shared stem/importability rule (`.ogg`/`.oga`/`.opus`). Full design,
messages, and tests: [`docs/specs/rb-dlc-onyx-prestage.md`](specs/rb-dlc-onyx-prestage.md).

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
- Web: `lib/signalr/clockSync.ts` (Ping/Pong sampler with the 8-sample min-RTT best-of-window
  offset filter, 250 ms RTT rejection and receipt-anchor fallback; the
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

### Slice 6a — Azure zero-cost deployment, cloud isolation & CI/CD

**Files**

- `infra/azure/main.bicep` (SWA free, ContainerApp consumption `minReplicas:0 maxReplicas:1` +
  sticky session affinity, Azure SQL free tier with auto-pause, log analytics free),
  `swa.config.staticwebapp` (routes, demo-asset caching), `appsettings.cloud.json` (SqlServer
  provider, `EnableYouTubeIngestion=false`, demo library = static assets list), EF Core SQL
  Server provider configured with **`EnableRetryOnFailure` tuned for the 30–60 s auto-pause wake
  window (transient errors `40613`/`40197`)**, SqlServer migration set, web client
  **"Waking database…"** state alongside the "waking the stage…" cold-start state,
  `.github/workflows/deploy-demo.yml` (build → GHCR → bicep deploy → SWA), `web` build step
  copying `public/demo-tracks`, README live-demo section + architecture
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

> "Implement Slice 6a per docs/ARCHITECTURE.md §7. Definition of Done: (1) one GitHub Actions
> workflow deploys the web client to Azure Static Web Apps (free) and the API to a single-replica
> scale-to-zero Container App with HTTP session affinity, with Azure SQL free tier persistence;
> (2) the live demo URL in the README boots from idle in <90 s across both cold-start layers
> (app + database) and a weekly scheduled Playwright smoke test keeps a green badge; (3) cloud
> mode provably has YouTube ingestion disabled (integration test) and serves demo songs purely
> as static frontend assets — no media egress through the API; (4) identical domain queries pass
> on both Npgsql and SqlServer via the provider-matrix test, including the provider-specific
> `HasFilter` filtered-index syntax; (5) the EF Core retry strategy absorbs simulated
> 40613/40197 transients after idle and the web client shows a branded 'Waking database…'
> state during the wake window; (6) the whole stack costs $0/month verified by documented
> Azure SKU choices in `infra/azure/main.bicep` and the README live-demo section (the
> self-host guide now belongs to Slice 6b)."

---

### Slice 6b — Self-hosted deployment: Proxmox LXC, bare-metal systemd, reverse-proxied public access

**Files**

- `scripts/proxmox/create-karaokay-lxc.sh` (community-helper-script style, run on the Proxmox
  host): prompts for CTID / hostname / LXC IP + gateway / DNS, downloads the Debian 12/13 template,
  creates an **unprivileged** LXC, then copies the provisioner (path supplied as an argument) into
  it with `pct push` and runs it with `pct exec`; safe to re-run against an existing CT.
- `scripts/proxmox/provision-karaokay.sh` (in-LXC, idempotent): apt installs FFmpeg, PostgreSQL
  15+, Python, and ufw; adds Microsoft's package repo and installs `aspnetcore-runtime-10.0`;
  installs Deno 2.3+; creates the `karaokay` system user; creates
  `/opt/karaokay/{app,media,ytdlp-cache,yt-dlp}`; creates the yt-dlp venv as the `karaokay` user
  (no `pip` as root) and installs `yt-dlp` into it; writes `/etc/karaokay/karaokay.env` (mode 0640,
  root:karaokay) with `ASPNETCORE_URLS=http://0.0.0.0:5255`, `ASPNETCORE_ENVIRONMENT=selfhost`,
  `MediaRoot`, `YtDlp__CacheDirectory`, `ConnectionStrings__Karaokay`, a **generated** Postgres
  password, `PublicBaseUrl`, and `Cors__AllowedOrigins__*`; creates the `karaokay` database/role;
  installs and enables `karaokay-api.service`. The generated password is one half of the production
  fix for carried-forward review item **M6**; the other half is the AppHost change below.
- `src/apps/Karaokay.AppHost/Program.cs`: replace the hardcoded `postgres-password` literal with a
  user-secrets-backed parameter (`AddParameter("postgres-password", secret: true)` with no literal
  value; the dev value comes from `dotnet user-secrets` / Aspire input, while the production LXC
  path ignores AppHost and reads `ConnectionStrings__Karaokay` from the env file); reinitialize the
  persisted `karaokay-postgres-data` volume after the change. This is the second half of **M6**.
- `scripts/proxmox/karaokay-api.service`: installed to `/etc/systemd/system/`; `User=karaokay`,
  `Group=karaokay`, `WorkingDirectory=/opt/karaokay/app`,
  `EnvironmentFile=/etc/karaokay/karaokay.env`,
  `ExecStart=/usr/bin/dotnet /opt/karaokay/app/Karaokay.Api.dll`, `Restart=always`,
  `RestartSec=5`; hardening `NoNewPrivileges=true`, `PrivateTmp=true`, `ProtectSystem=strict`,
  `ProtectHome=true`, `ReadWritePaths=/opt/karaokay/media /opt/karaokay/ytdlp-cache`,
  `ProtectKernelTunables=true`, `ProtectKernelModules=true`, `ProtectControlGroups=true`,
  `RestrictSUIDSGID=true`, `LockPersonality=true`; `systemd-analyze verify` clean.
- Web client production serving: the API enables default/static files over `wwwroot` plus an SPA
  fallback (`MapFallbackToFile("index.html")`, excluding `/api` and `/hubs`), so one NPM upstream
  `http://<lxc-ip>:5255` serves `/stage`, `/remote`, `/api`, and `/hubs` same-origin; SignalR
  WebSockets traverse NPM with websocket support enabled. `scripts/deploy-selfhost.sh`:
  `dotnet publish` (Release) → `npm ci && npm run build` → rsync the publish output and `dist/`
  into `/opt/karaokay/app` and `/opt/karaokay/app/wwwroot` → apply EF migrations →
  `systemctl restart karaokay-api`.
- `src/apps/Karaokay.Api/appsettings.selfhost.json` (loaded because
  `ASPNETCORE_ENVIRONMENT=selfhost`; the env file remains the source of secrets and pins
  non-secret defaults): Npgsql provider, `Features:EnableYouTubeIngestion=true`,
  `PublicBaseUrl=https://karaokay.delvserv.duckdns.org`, `Cors:AllowedOrigins` including that
  https origin, `MediaRoot=/opt/karaokay/media/library`,
  `YtDlp:CacheDirectory=/opt/karaokay/ytdlp-cache`, and `YtDlp:ExecutablePath` pointing at the
  provisioned venv shim.
- API code: `UseForwardedHeaders` in `src/apps/Karaokay.Api/Program.cs` — call
  `ClearForwardedHeaders()` first, apply `XForwardedFor | XForwardedProto | XForwardedHost`, and
  limit `ForwardedHeadersOptions.KnownProxies` to the NPM LXC IP (configured via
  `ForwardedHeaders:KnownProxies:0`, never `KnownNetworks = 0.0.0.0/0`), so `Request.Scheme` /
  `Host` / remote IP are the public ones behind NPM; CORS stays the existing env-configurable
  `Cors:AllowedOrigins` allow-list.
- `src/tests/Karaokay.IntegrationTests/QrJoinUrlTests.cs`: add a middleware-level case
  (`WebApplicationFactory`/`TestClient` sends `X-Forwarded-Host:
  karaokay.delvserv.duckdns.org`, `X-Forwarded-Proto: https`) asserting the generated QR join URL
  is `https://karaokay.delvserv.duckdns.org/remote?code=...`, and that a non-empty `PublicBaseUrl`
  still wins when both are present.
- `src/clients/web/e2e/selfhost-production.spec.ts`: Playwright smoke against the API serving a
  production `wwwroot` bundle (no Vite dev proxy) with `PublicBaseUrl` set.
- Dev-mode LAN phone flow: `src/clients/web/package.json` gains
  `"dev:lan": "vite --host 0.0.0.0"` (the default `"dev": "vite"` stays loopback-bound; the
  preview port/binding and existing Playwright outcomes are unchanged because no spec asserts QR
  payloads, though preview's proxied `Host` also changes); `src/clients/web/vite.config.ts` flips
  the shared `apiProxy` entries for `/api` and `/hubs` from `changeOrigin: true` to
  `changeOrigin: false` (the same proxy object feeds `server` and `preview`), so the API receives
  the browser's original `Host` and `BuildJoinUrl`'s request-host fallback encodes
  `http://<dev-LAN-IP>:5173/remote?code=…` with no `PublicBaseUrl` in dev. `ws: true` stays on
  `/hubs`; phone traffic remains same-origin through the Vite proxy, so the API stays
  loopback-bound and the loopback-only CORS allow-list is unchanged. `PublicBaseUrl` still wins
  when set (production/self-host path). Caveats documented in `docs/self-hosting.md`: DHCP LAN-IP
  changes, OS firewall prompting for Node, trusted home LAN only (never port-forward), and Vite
  `allowedHosts` if a LAN hostname is used instead of the raw IP (the raw IP needs no config).
- `docs/self-hosting.md`: the full operator guide (architecture diagram, LXC creation, NPM proxy
  host + Let's Encrypt/DuckDNS, configuration, update/backup/troubleshooting, $0 cost table,
  secondary Docker Compose path, and the first-class developer `dev:lan` LAN-testing flow).
- `docs/learning/slice-3-walkthrough.md`: additive dated note that Slice 6b plans the `dev:lan` +
  `changeOrigin: false` dev-mode fix (no historical prose rewritten).
- Firewall: ufw in the LXC allows `5255/tcp` **only** from the NPM LXC; an optional Proxmox-host
  rule for local health checks is documented as optional everywhere. PostgreSQL binds
  `127.0.0.1:5432` only; optionally enforce the same rule at the Proxmox firewall level. No
  inbound SSH is exposed through NPM.

**Tests**

- Integration: forwarded-headers join-URL case as above (`X-Forwarded-Host` / `X-Forwarded-Proto`
  → public https URL), plus the existing `PublicBaseUrl` precedence assertions; a CORS preflight
  from the public origin succeeds and from a foreign origin fails.
- AppHost config/integration: assert the Postgres password parameter has no committed literal and
  is sourced from user secrets/Aspire input, and that the API still starts and migrates against
  PostgreSQL with the supplied value after the data volume is reinitialized.
- Dev-mode LAN: `QrJoinUrlTests` already pins the empty/whitespace `PublicBaseUrl` →
  `Request.Scheme`/`Request.Host` fallback (including a `127.0.0.1:5255` case), so no new unit
  assertion is required; the `changeOrigin: false` flow is covered by the manual checklist item
  below, and the existing Playwright preview suite must stay green unchanged.
- Playwright: load `/stage` from the locally served production bundle, create a room, decode the
  QR and assert it encodes the configured absolute `https://…/remote?code=...` URL, then join
  `/remote?code=...` from a second browser context and queue a demo song.
- Static checks: `shellcheck` clean on both Proxmox scripts and `deploy-selfhost.sh`;
  `systemd-analyze verify karaokay-api.service` clean; re-running `provision-karaokay.sh` is
  idempotent (no duplicate users/databases/env lines, service still healthy).
- Manual acceptance checklist (documented in `docs/self-hosting.md`, like Slice 7's on-device
  checklist): from a clean Proxmox host one command creates the LXC; an NPM proxy host issues a
  Let's Encrypt DuckDNS certificate and has websockets ON; a phone on the LAN scans the stage QR
  and joins; a phone **off-network (cellular)** scans the same QR, joins, queues, and sings
  end-to-end. Dev-mode item: `npm run dev:lan` on the dev machine is reachable from a phone on the
  same LAN; the stage QR decodes to `http://<dev-LAN-IP>:5173/remote?code=…` and the join-sing flow
  completes; the default `npm run dev` remains loopback-bound.

**Definition of Done**

> "Implement Slice 6b per docs/ARCHITECTURE.md §7. Definition of Done: (1) `scripts/proxmox/create-karaokay-lxc.sh`
> and `provision-karaokay.sh` stand up a fully working unprivileged Debian LXC from a clean
> Proxmox VE host with one command, and re-running the provisioner is idempotent; (2)
> `karaokay-api.service` is active and hardened (`systemd-analyze verify` clean) as the `karaokay`
> user, running with `ASPNETCORE_ENVIRONMENT=selfhost` so `appsettings.selfhost.json` is loaded, and
> serving the built SPA from `wwwroot` with SPA fallback alongside `/api` and `/hubs`; (3)
> behind the operator's existing Nginx Proxy Manager with a Let's Encrypt DuckDNS certificate and
> websocket support, the public https URL serves stage, remote, `/api`, and `/hubs`, and a phone
> entirely outside the local network scans the stage QR and completes the join-sing flow
> end-to-end; (4) the forwarded-headers integration test and the `PublicBaseUrl` precedence test
> pass, so the QR always encodes the public URL; (5) PostgreSQL uses a generated password stored
> only in `/etc/karaokay/karaokay.env`, listens on loopback, **and** the AppHost
> `postgres-password` literal is replaced by a user-secrets-backed parameter with the dev data
> volume reinitialized — together closing carried-forward review item M6; (6) the firewall allows
> `5255/tcp` only from the NPM LXC (with an optional Proxmox-host rule for local health checks
> documented as optional) and blocks direct database access; (7) `docs/self-hosting.md` documents
> the complete guide — LXC creation, NPM proxy-host
> configuration, configuration, update procedure, `pg_dump` + media-directory backups,
> troubleshooting, the secondary Docker Compose alternative, and a cost table proving $0/month;
> (8) `npm run dev:lan` on the dev machine is reachable from a phone on the same LAN; with the
> stage loaded via `http://<dev-LAN-IP>:5173/stage`, the QR encodes
> `http://<dev-LAN-IP>:5173/remote?code=…`, and the join-sing flow completes end-to-end with the
> API and CORS unchanged (loopback-only); under plain `npm run dev` the QR correctly derives
> `localhost:5173`, and existing Playwright/preview outcomes are unaffected."

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
