# ADR-004: Unified dual-source media pipeline (Rock Band multitrack import + yt-dlp/Deno YouTube ingestion) with cloud isolation

- **Status:** Accepted
- **Date:** 2026-11-23
- **Deciders:** Architect
- **Context level:** Data architecture / Ingestion pipeline
- **Related:** [ADR-001](001-single-replica-signalr-azure.md) (cloud cost isolation), [ARCHITECTURE.md §4](../ARCHITECTURE.md#4-core-data-contracts), [ARCHITECTURE.md §5.1](../ARCHITECTURE.md#51-unified-state-machine-per-queue-item--room-playback)

## Context

The platform must serve two structurally different karaoke media sources through **one queue,
one room engine, and one stage**:

1. **Rock Band-style multitrack songs** (self-hosted library): extracted song folders with
   `song.ini`/`songs.dta`, `notes.mid`, and separated stems (`vocals*.ogg`, `drums.ogg`,
   `bass.ogg`, `guitar.ogg`, `keys.ogg`, `song.ogg`). These give the *classic karaoke machine*
   experience: a canvas sweep-fill lyric timeline rendered from `PART VOCALS`/`HARM1–3`, plus
   three independent audio buses (instrumental mix, guide vocals with live volume control, and
   the USB mic).
2. **YouTube karaoke videos** (on-demand): guests search YouTube from their phones — with **no
   YouTube Data API key** (constraint C1) — and queue a video that the server downloads
   asynchronously with `yt-dlp` and caches. These give a *synchronized video surface*
   experience with no lyrics timeline.

Hard external constraints:

- **Zero copyrighted assets in Git (C2).** Commercial Rock Band charts and YouTube downloads
  must never be committed. The library exists only on each user's own machine; the repository
  ships **2–3 deterministic synthetic public-domain demo songs** instead, so CI and recruiters
  get a fully working stack out of the box.
- **YouTube ingestion needs a JS runtime**: current `yt-dlp` releases rely on a JavaScript
  engine (Deno 2.3+ in our container image) to solve player challenges reliably.
- **Cloud demo isolation (C1/C3)**: the public Azure demo must be *incapable* of accruing
  storage/bandwidth costs — so YouTube ingestion is disabled there and demo songs are static
  frontend assets.

The temptation to model these as two subsystems (two queue types, two stage apps, two state
machines) would double the state machine, SignalR surface, and UI work, and would make the
portfolio harder to explain.

## Decision

**One media contract with a discriminator; two ingestion pipelines behind it; a feature flag
between them.**

1. **Unified domain**: `Song.MediaSource` (`Multitrack | YouTube`) discriminates render mode
   (canvas sweep-fill vs video surface) and audio graph (3-bus mixer vs single muxed bus).
   `QueueItem`, `PartyRoom`, the room state machine, and all SignalR contracts are **source-
   agnostic** — the stage switches surfaces, not protocols.
2. **Rock Band ingestion (self-host, CLI only)**: `Karaokay.Importer` parses the user's own
   extracted folders locally — `DryWetMidi` converts `PART VOCALS`/`HARM1–3` into the
   normalized `lyrics.json` timeline (tick→ms via the tempo map, **phrase boundaries preferring
   explicit Rock Band marker notes 105/106 with the `PhraseGapMs` heuristic as fallback**,
   syllable connectors, talky notes, countdown gaps), and FFmpeg (`amix`) mixes non-vocal
   stems — **excluding `crowd*.ogg`** — into `instrumental.opus` with **EBU R128 `loudnorm`**
   applied so multitrack stems match YouTube video loudness, and transcodes `vocals*.ogg`
   into `vocals.opus`. Nothing it produces is committed to Git except via the synthetic
   generator.
3. **Synthetic Demo Generator (committed)**: `Karaokay.SyntheticGen` deterministically
   (seeded) composes public-domain multitrack test songs — DryWetMIDI score + FFmpeg-baked
   Opus stems — producing byte-identical outputs across runs, so CI golden tests and the
   live demo have stable, legal content.
4. **YouTube ingestion (self-host, feature-flagged)**: zero-key search via
   `yt-dlp "ytsearch10:{query} karaoke" --dump-json --flat-playlist`; async downloads via a
   bounded `Channel<YouTubeDownloadJob>` consumed by a `BackgroundService` (1080p cap, cache
   lookup by `videoId` first, progress broadcast over SignalR, 2 retries, LRU cache quota),
   constrained to **H.264/AAC in MP4** via `-S "vcodec:h264,acodec:aac" --merge-output-format
   mp4` so cache files match `{videoId}.mp4` and hardware-decode on browsers and Android TV is
   guaranteed; server image carries `yt-dlp` + `ffmpeg` + `deno 2.3+`, and `docker-compose.yml`
   documents an **optional `cookies.txt` volume mount** for rate-limit resilience.
5. **Cloud isolation**: `Features:EnableYouTubeIngestion=false` in `appsettings.cloud.json`
   hard-disables search and enqueue (friendly `HubError`), and the demo songs ship as static
   frontend assets under `public/demo-tracks` — zero media egress through the API, zero
   storage growth, zero overage risk, forever.

## Consequences

**Positive**

- One queue/room/playback engine to build, test, and explain — the entire Slice 3–4 machinery
  is written once and inherited by both sources.
- **Legal safety by construction**: the repo contains only synthetic public-domain content;
   ingestion of commercial material happens exclusively on the user's machine.
- The unified `Song` model makes cache-hit-`Ready` instant queuing trivial (a previously
  downloaded YouTube song behaves exactly like a library song).
- Cloud demo risk is eliminated by a flag, not by vigilance.

**Negative / Accepted risks**

- **Dual storage layouts** (three multitrack files vs one cached MP4) inside one `Song` entity —
  nullable columns are the price; acceptable at this scale and hidden behind the enum.
- **Provider-agnostic persistence** (Npgsql for self-host PostgreSQL, Microsoft.EntityFrameworkCore.SqlServer
  for Azure SQL) adds a migration set and a provider-matrix test — bounded, tested cost for
  the dual deployment story.
- **Cloud demo exercises only the Multitrack path** for new content (YouTube search returns a
  disabled message). Accepted: the full YouTube path is demonstrable in the self-host compose
  demo, and the demo's purpose is the stage + queue loop.
- `yt-dlp`/YouTube internals are a moving target; CI never depends on them (shimmed-binary
  integration tests; real `yt-dlp` runs nightly/locally only, `[Category:RequiresYouTube]`).

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Two subsystems (two queue types / two stage apps) | Doubles state machine, hub surface, UI, and testing; makes the "unified party queue" portfolio story incoherent. |
| Commit sample Rock Band charts / downloaded videos to the repo | Copyright violation — a hard constraint (C2), independent of architecture taste. |
| YouTube Data API for search | Requires an API key with quota/cost — violates constraint C1; `yt-dlp` search needs none. |
| Enable yt-dlp on the cloud demo | Any visitor-triggered download risks egress/storage overage on free tiers — violates C1; also persists random third-party media on a portfolio tenant. |
| SQLite on the cloud demo instead of Azure SQL | Loses the "EF Core across two real providers" showcase and Azure SQL Free's auto-pause pairing with scale-to-zero (ADR-001). |

## Validation plan

- Unit: `lyrics.json` importer golden tests (multi-tempo, talky, connectors, countdown gaps,
  marker-based phrase boundaries); yt-dlp progress-line parser fixtures.
- Integration: shimmed-binary download worker lifecycle (Downloading → progress → `Ready`;
  failure → retries → `Failed`); feature-flag-off tests assert search/enqueue are rejected
  everywhere; provider-matrix tests run the same domain queries on PostgreSQL and SQL Server.
- Compliance: CI fails if any binary media >100 KB outside `public/demo-tracks` is committed;
  SyntheticGen determinism tests prove demo assets are regenerable, not sourced externally.
