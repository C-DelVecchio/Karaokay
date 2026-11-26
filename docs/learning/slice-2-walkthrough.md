# Karaokay — Project State After Slice 2

> *This is a learning-walkthrough snapshot of the project as it stood after Slice 2 (2026-11-24),
> preserved from commit `ebc3e95`. Links have been adjusted for its new home in `docs/learning/`;
> the living project snapshot is [`../PROJECT_STATE.md`](../PROJECT_STATE.md).*

This is a guided tour of **what Karaokay is, what exists today, how the pieces fit together, and how to run it**. It assumes you can read a file and follow a command, but not that you have met terms like *Minimal API*, *Web Audio*, *ETag*, or *vertical slice*. Terms are defined where they matter for understanding that section, and there is a [Glossary](#9-glossary) near the end.

- **Snapshot date:** 2026-11-24
- **Completed:** Slice 1 and Slice 2
- **Next up:** Slice 3
- **Master blueprint:** [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md) v1.2
- **Slice 1 beginner guide:** [`docs/learning/slice-1-walkthrough.md`](slice-1-walkthrough.md)

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

**Karaokay** is a self-hosted, multi-client party karaoke web app. A browser "stage" shows lyrics, a server delivers songs, and (later) phones act as remotes. It turns MIDI charts plus separated audio tracks into playable songs and paints synchronized lyrics in the browser.

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
| 3 | SignalR rooms, queue, EF Core database, phone remote | ⏳ Next |
| 4 | Playback authority, clock sync, browser video mode | Planned |
| 5 | Zero-key YouTube search and `yt-dlp` downloads | Planned |
| 6 | Azure free-tier deployment and CI/CD | Planned |
| 7 | Android C++ Oboe audio engine | Planned |
| 8 | Android TV SkiaSharp stage and hardening | Planned |

Slices 1 and 2 are **complete**. Their tests pass locally and in CI.

### The blueprint and ADRs

[`docs/ARCHITECTURE.md`](../ARCHITECTURE.md) (v1.2) is the master blueprint: goals, contracts, state machines, rendering math, and the full roadmap. If this page and the blueprint disagree, the blueprint wins and one of them should be fixed.

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

The deep walkthrough of this slice is [`docs/learning/slice-1-walkthrough.md`](slice-1-walkthrough.md).

### 2.2 Slice 2 — media over HTTP and a browser stage

Slice 2 answered: *how does a browser fetch a song and paint synchronized lyrics?*

- **Media endpoints** on the API: `GET /api/songs`, and `GET /api/songs/{songId}/{instrumental|vocals|lyrics}`. They support **HTTP range requests** and send **ETag** validators plus `Cache-Control: public, max-age=86400, immutable`.
- **Browser stage** in **React 19 + TypeScript**, built with **Vite**: a landing page at `/`, the stage at `/stage`, a hand-written pathname router, a phase machine (`picking → countdown → playing → ended`), a Web Audio engine, and a Canvas lyrics renderer.
- **Tests:** 40 **Vitest** unit tests in 7 files; 7 **Playwright** end-to-end tests in 4 specs; the .NET suite grew from 45 to **69** (34 unit, 24 integration, 11 architecture) after Slice 2's media endpoint tests. Numbers measured 2026-11-24.
- **CI on GitHub Actions** builds and tests both stacks, runs Playwright headless, and checks that generated golden fixtures have not drifted.

### 2.3 What is NOT built yet

Do not look for these:

- **SignalR rooms, a phone remote, or any real-time connection.** No hub, lobby, or queue exists.
- **A database.** No EF Core `DbContext`, migrations, or SQL wiring. The API scans a folder.
- **Queue synchronization or playback authority.** Nothing coordinates clients.
- **YouTube ingestion.** No `yt-dlp` search or download worker.
- **Azure deployment.** No Bicep files, deploy workflow, or live URL.
- **Android anything.** No `net10.0-android` app, C++ Oboe engine, or SkiaSharp renderer.
- **A browser API client.** The Slice 2 stage loads demo songs from `public/demo-tracks/` as static files. The HTTP endpoints exist, are tested, and are ready for Slice 3, but the stage does not call them yet. This distinction matters.

`src/clients/web/src/features/remote/` is a placeholder folder containing only a README for the Slice 3 phone UI.

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
│   ├── PROJECT_STATE.md      # living project snapshot
│   ├── adr/
│   ├── learning/
│   │   ├── slice-1-walkthrough.md
│   │   ├── slice-2-walkthrough.md   ← this file
│   │   └── slice-3-walkthrough.md
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
| [001](../adr/001-single-replica-signalr-azure.md) | Run SignalR on one scale-to-zero Azure Container App replica with sticky sessions, avoiding paid SignalR/Redis. |
| [002](../adr/002-android-oboe-hdmi-usb-drift.md) | Build a full-duplex Oboe engine for USB mic → HDMI, with manual clock-drift compensation. |
| [003](../adr/003-dual-mode-stage-web-vs-native.md) | Ship web and native stages that share one decision-level renderer contract. |
| [004](../adr/004-dual-source-media-pipeline.md) | Support Rock Band multitracks and YouTube downloads through one queue/stage, with cloud ingestion disabled. |
| [005](../adr/005-slice-1-blueprint-reconciliations.md) | Correct the blueprint after Slice 1: 23-letter codes / 279,841 combinations, reflection-based rules, code-level validation. |
| [006](../adr/006-line-wrapping-ownership.md) | The renderer owns glyph-measured wrapping; the importer stores only explicit line breaks. |

- **`learning/slice-1-walkthrough.md`** — a long beginner guide to Slice 1: MIDI, tick math, lyric encoding, FFmpeg mixing, LUFS, and the tests.
- **`review/slice-1-review.md`** — the internal review of Slice 1 with verdicts and follow-ups.

---

## 4. The source tree under `src/`

```text
src/
├── apps/
│   ├── Karaokay.Api/            ← ASP.NET Core HTTP server
│   ├── Karaokay.AppHost/        ← .NET Aspire placeholder (Slice 3)
│   └── Karaokay.ServiceDefaults/← shared telemetry placeholder (Slice 3)
├── core/
│   ├── Karaokay.Contracts/      ← wire shapes + lyrics.json serializer
│   ├── Karaokay.Domain/         ← pure domain layer (marker only)
│   ├── Karaokay.Application/    ← use cases (room codes)
│   └── Karaokay.Infrastructure/ ← adapters (MIDI, FFmpeg, file names)
├── tools/
│   ├── Karaokay.Importer/       ← CLI for real song folders
│   └── Karaokay.SyntheticGen/   ← deterministic demo generator
├── clients/web/                 ← React 19 + TypeScript stage
└── tests/
    ├── Karaokay.UnitTests/
    ├── Karaokay.IntegrationTests/
    └── Karaokay.ArchitectureTests/
```

### 4.1 `src/apps/Karaokay.Api` — the HTTP server

**ASP.NET Core** is Microsoft's web framework for .NET; it handles sockets, routing, dependency injection, logging, and HTTP. This project writes only the interesting parts.

#### `Program.cs`

The entry point is short:

1. Create the builder.
2. Bind `MediaLibraryOptions` from configuration and register `MediaLibrary` as a singleton.
3. Map `/healthz` returning `{ "status": "ok" }`.
4. Map the song and media endpoint groups.
5. Run.

**Dependency injection** (DI) means a class asks for what it needs as constructor parameters; a container builds and shares instances. `AddSingleton` means one shared instance per process, which suits the library's scan cache.

A **Minimal API** is ASP.NET Core's low-ceremony style: lambdas mapped directly to routes instead of controller classes. The trailing `public partial class Program { }` lets integration tests boot the real host with `WebApplicationFactory<Program>`.

#### `Endpoints/SongsEndpoints.cs`

`GET /api/songs` asks DI for `MediaLibrary`, calls `GetSongs()`, and returns JSON. Items are `SongSummaryDto` records from `Karaokay.Contracts`: `id`, `title`, `artist`, `durationMs`, `source`, and `hasGuideVocals`. Slice 3 will swap the filesystem source for a table without changing this wire shape.

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

#### `Karaokay.AppHost` and `Karaokay.ServiceDefaults`

`.NET Aspire` orchestrates multi-service local development (API, database, sidecars). `AppHost` is currently a console app printing a placeholder; `ServiceDefaults` holds an empty class where shared OpenTelemetry, health checks, and HTTP resilience will live. They exist now so the solution graph and architecture tests include them from day one.

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

Why one serializer? The generator, importer, golden tests, and `verify` all compare bytes. Separate JSON options would create false failures and hide real ones.

#### `Karaokay.Domain`

Only `DomainAssemblyMarker`, an empty type that lets architecture tests find the assembly. Real entities arrive with Slice 3.

#### `Karaokay.Application`

`RoomCodeGenerator` uses `ABCDEFGHJKLMNPRSTUVWXYZ` (23 letters, no `I`/`O`/`Q`), builds four-character codes (`23⁴ = 279,841`), accepts an injectable `Random` for replayable tests, reserves each code with `HashSet.Add`, and retries at most 100 times before throwing. A database unique index will be the final collision backstop in Slice 3.

#### `Karaokay.Infrastructure/MediaIngest/` — adapters to real formats

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

### 4.4 `src/clients/web` — the browser stage

#### Tooling: `package.json`, Vite, TypeScript

- **`package.json`** lists dependencies and scripts. Runtime dependencies are only React 19 and React DOM; everything else is a dev dependency.
- **Vite** is the dev server and **bundler** — a tool that turns many TypeScript/CSS files into a few browser-ready files. Config: dev on port 5173, preview on 4173, Vitest tests in a Node environment because the renderer tests are pure math.
- **TypeScript** is JavaScript plus static types. A **transpiler** strips types and modern syntax; Vite does this during dev and build. `tsconfig.json` enables `strict`, `noUnusedLocals`, and `noUnusedParameters`, so unused code fails.
- **Vitest** is the unit test runner for Vite projects.

There is deliberately **no `react-router`**. `src/App.tsx` has a hand-rolled pathname router: `resolveRoute()` normalizes trailing slashes and maps `/` to landing, `/stage` to the stage, and everything else to 404. It listens for `popstate` so the back button works. Two routes do not justify a dependency.

#### `StageShell.tsx` — the phase machine

```text
picking ──pick──▶ countdown ──lead-in ends + audio ready──▶ playing ──source ends──▶ ended
   ▲                                                                                 │
   └──────────────────────────── pick another song ─────────────────────────────────┘
```

On a pick, `engine.loadSong()` fetches and decodes both stems while a countdown animation runs from the lead-in gap. `Promise.all` waits for both, then `engine.start(leadInOffsetMs)` starts both sources and seeks past the lead-in so audio and lyrics agree. When the source ends, the phase becomes `ended` and the picker returns.

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

`playwright.config.ts` serves the built `dist/` on port 4173 and launches with `--mute-audio` and `--autoplay-policy=user-gesture-required`, so the gate is enforced even in tests.

### 4.5 `src/tests/` — the safety net

- **`Karaokay.UnitTests` (34 tests).** A **unit test** checks one small piece in isolation: a pure function, parser, or validator. The project uses **xUnit**, where `[Fact]` is one test and `[Theory]` runs one test with several data rows. Coverage includes tempo math, talky/connector handling, phrase grouping by markers versus gaps, countdown math, golden `lyrics.json` byte comparisons, room codes, and generator determinism.
- **`Karaokay.IntegrationTests` (24 tests).** An **integration test** checks several real components working together, usually including I/O. Only the 7 media-endpoint tests boot the real API through `WebApplicationFactory`; they assert range responses (`206` with `Content-Range`/`Accept-Ranges`), ETag presence and strength, content types, 404 `ProblemDetails`, path-traversal 404s, invalid-folder skipping, and the library listing. The importer integration tests do not boot the API: they run against a temporary fake Rock Band folder with real FFmpeg-generated audio, assert stem selection and the filter graph, and check LUFS within ±1 LU. `FfmpegGuard` skips FFmpeg-dependent tests with a clear message when the tools are missing.
- **`Karaokay.ArchitectureTests` (11 tests).** These enforce layering. **Reflection** is .NET's ability to inspect assemblies and types at runtime. The tests use `Assembly.GetReferencedAssemblies()` to assert, for example, that `Contracts` never references Domain/Application/Infrastructure/DryWetMidi/CliWrap/EF Core, that `Application` never references `Infrastructure`, that CLI tools never reference the apps, and that `Domain` is a leaf. **Positive controls** assert the probe really sees a known reference, so a broken helper cannot make every rule pass vacuously. The source comment explains why reflection replaced `NetArchTest.Rules`, which misreported dependencies on .NET 10.

---

## 5. End-to-end data flow

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
          │ MediaLibrary scan    │
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
6. **Serve.** `MediaLibrary` scans, validates, caches for one second, and whitelists ids. `/api/songs` returns summaries; media routes return bytes with ranges, cache headers, and ETags.
7. **Play (Slice 2 path).** The stage fetches the same three files from its static `public/demo-tracks/` copies. `loadSong` fetches both stems, waits for unlock, decodes them, and starts two sources through the gain buses. `positionMs()` reads the audio clock.
8. **Draw.** `useStageClock` pushes positions over rAF. The renderer picks the active row, clips the filled color, draws the unfilled color, and paints countdown dots. No React re-render happens.

The key seam for Slice 3: server and browser already agree on file names and JSON shape. Replacing static URLs with API URLs and the local picker with a room queue requires no parser, contract, or renderer changes.

---

## 6. How to build and run everything

### 6.1 Prerequisites

**.NET 10 SDK** (`dotnet --version` reports 10.x), **Node.js 24** with npm, and **FFmpeg/ffprobe** on `PATH` for import, mixing, and audio tests (`sudo apt-get install ffmpeg` or `brew install ffmpeg`). Optional: `npx playwright install chromium` (CI uses `--with-deps` for system libraries).

### 6.2 .NET commands

From the repository root:

```bash
dotnet build          # restore + compile all twelve projects; warnings are errors
dotnet test           # run unit, integration, and architecture tests (~69 tests)
dotnet run --project src/apps/Karaokay.Api     # Development profile: http://localhost:5255
```

`dotnet build` reads `karaokay.sln`, restores NuGet packages, and compiles everything. Because warnings are errors, a green build is a strong signal. `dotnet test` builds if needed, then runs xUnit; FFmpeg-dependent integration tests are skipped by a guard if the tools are missing.

In Development, the API serves the web demo tracks because `MediaRoot` points there. Try:

```bash
curl http://localhost:5255/healthz
curl http://localhost:5255/api/songs
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
npm test                  # Vitest once (40 tests)
npm run build             # type-check with tsc, then bundle into dist/
npm run test:e2e          # build, serve dist/, run Playwright
npm run test:e2e:ci       # Playwright against an existing build
npm run fixtures:generate # regenerate the shared golden decision fixtures
```

- **`npm ci`** is `dotnet restore` for JavaScript: exact, reproducible install.
- **`npm run dev`** recompiles on save. Open `/` for landing and `/stage` for the stage.
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
7. `dotnet test --configuration Release --no-build` (tests cannot silently rebuild).
8. Set up Node 24 with npm caching keyed on `package-lock.json`.
9. `npm ci`.
10. `npm test` (Vitest).
11. `npm run build`.
12. Install Playwright Chromium with system dependencies.
13. `npm run test:e2e:ci`.
14. `npm run fixtures:generate`.
15. `git diff --exit-code -- src/clients/web/fixtures/golden/` — fail on drift.
16. Upload Playwright reports on failure.

There is no deployment job yet; Azure arrives in Slice 6a.

---

## 7. Where the project goes next

### Slice 3 — rooms, queue, database, phone remote

- **SignalR.** SignalR is ASP.NET Core's real-time library: instead of polling "anything new?", a persistent connection lets the server *push* updates (guest joined, song added, queue changed). It prefers WebSockets and falls back to other transports.
- **`StageHub`.** A hub is SignalR's server endpoint; clients call methods and receive broadcasts. Planned methods cover join/leave, enqueue/move/remove, and playback commands.
- **`QueueService` and `RoomRegistry`.** The singleton `RoomRegistry` tracks live rooms, while `QueueService` owns enqueue/move/remove and emits sequence-numbered `QueueDelta` updates; reconnecting clients get a snapshot and converge. The single-writer `PlaybackAuthority` that decides what actually plays next arrives in Slice 4.
- **EF Core.** **EF Core** is Microsoft's **ORM** — an Object-Relational Mapper, a library that lets C# work with tables as objects instead of hand-written SQL. Slice 3 adds `KaraokayDbContext`, entities, and migrations.
- **Phone remote.** Join screen with a four-letter code, lobby, library, and queue with reorder/remove. The placeholder is `src/clients/web/src/features/remote/`.
- **Aspire wiring.** `AppHost` and `ServiceDefaults` stop being placeholders and orchestrate the API plus a database locally.

The media endpoints and DTOs are already shaped for this: a database-backed song store can replace `MediaLibrary` without changing `SongSummaryDto` or the media URLs.

### Later slices

Slice 4 adds authoritative playback and clock sync. Slice 5 adds zero-key YouTube search and `yt-dlp`. Slice 6a deploys to Azure free tiers. Slices 7–8 build the Android TV stage: a C++ Oboe engine, then a SkiaSharp renderer consuming the same golden decision fixtures as the web.

For full file lists, tests, and Definition-of-Done prompts, read [`docs/ARCHITECTURE.md` §7](../ARCHITECTURE.md#7-phased-vertical-slice-implementation-roadmap). For the deep Slice 1 explanation, read [`docs/learning/slice-1-walkthrough.md`](slice-1-walkthrough.md).

---

## 8. Frequently confusing points

**Why two stems instead of one mixed track?** Karaoke needs a guide-vocal slider. Two files let the browser crossfade the vocal bus from 0 to 1 without re-encoding or restarting. A single mix would bake the vocal in permanently. The live microphone bus arrives in Slice 7.

**Why not just use `<audio src="song.opus">`?** An `<audio>` element gives one volume control and no sample-accurate multi-bus mixing. The stage needs independent instrumental and guide buses, a master bus, a testable clock, and — in Slice 4 — a `<video>` routed into the same graph. Web Audio provides that graph.

**What is an `.opus` file?** Opus is an open, royalty-free audio codec for speech and music over the internet. It gives good quality at low bitrates and is supported by modern browsers. These files are 48 kHz stereo, encoded by FFmpeg's `libopus`.

**Why range requests if the demo songs are small?** Real imported songs are minutes long. Ranges let playback start early, seeking avoid re-downloading, and dropped connections resume. Building it now keeps the API contract stable when real libraries arrive.

**Why ETag and `immutable` caching?** A song id always maps to the same bytes because imports are not edited in place. Caching avoids re-downloading megabytes on every visit. The ETag is the correctness backstop: if length or modification time changes, the fingerprint changes.

**Why does the stage not call `/api/songs` yet?** Slice 2's goal is the stage, not the party loop. Bundled static tracks let `npm run dev` work alone and let Playwright run without a second server. The API is fully integration-tested and ready to wire in when Slice 3 adds rooms and a queue.

**Why no `react-router`?** Two routes do not justify a dependency. `App.tsx` normalizes the pathname, switches, and listens for `popstate`. A router may become worthwhile when the remote adds screens.

**Why golden fixtures instead of screenshots?** Screenshots are brittle across fonts, anti-aliasing, and GPUs. The fixtures compare *decisions* — active line, fill values, dot states — at 100 instants, which are identical on any platform. Playwright separately checks that pixels are drawn and that frames differ over time, catching a blank canvas without depending on exact pixels.

**Why scan a folder instead of using a database?** A folder is the simplest possible library for a slice about serving media. The scan is cached, validated, and whitelisted, and is explicitly marked as a Slice 3 replacement. A database before a queue or room would add complexity without unlocking a feature.

**Why is `InvariantGlobalization` worth it?** Golden tests compare JSON bytes across machines. Locale-dependent number formatting or casing could produce different bytes in CI and locally. Invariant mode removes that variable at the cost of a future translated API needing an override.

---

## 9. Glossary

| Term | Meaning |
|---|---|
| **ADR** | Architecture Decision Record: a short, numbered note capturing one decision and its consequences. |
| **Aspire / AppHost** | .NET's local orchestrator for multi-service development; the AppHost describes services. Placeholder here. |
| **Bundler** | A tool that combines many source files into a few browser-ready files. Vite is the bundler. |
| **Canvas** | An HTML element with a JavaScript-controlled pixel drawing surface. |
| **CI** | Continuous Integration: automatically building and testing every change. |
| **Clean Architecture** | Layering pattern where business rules sit in the center and outer layers depend inward. |
| **CPM** | Central Package Management: one file declares all NuGet versions. |
| **Dependency injection** | Classes receive dependencies as parameters; a container builds and supplies them. |
| **EF Core** | Entity Framework Core, Microsoft's ORM for .NET. Planned for Slice 3. |
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

- [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md) — the blueprint, v1.2. Start with §7 for the roadmap or §4.1 for the `lyrics.json` contract.
- [`docs/learning/slice-1-walkthrough.md`](slice-1-walkthrough.md) — the deep beginner guide to MIDI, tempo maps, phrase markers, FFmpeg, LUFS, and the tests.
- [`docs/review/slice-1-review.md`](../review/slice-1-review.md) — how Slice 1 was reviewed and fixed.
- [`docs/adr/`](../adr/) — the six decision records summarized in [§3.8](#38-docs--the-written-memory-of-the-project).
- [`src/apps/Karaokay.Api/Program.cs`](../../src/apps/Karaokay.Api/Program.cs) — the smallest entry point; a good first read.
- [`src/clients/web/src/features/stage/StageShell.tsx`](../../src/clients/web/src/features/stage/StageShell.tsx) — the phase machine tying UI, audio, and renderer together.

A good first afternoon: run `dotnet build` and `dotnet test`, then `npm ci && npm test` in the web client, start both dev servers, open `/stage`, click the gate, and watch Twinkle Variations sweep by. Then trace one pick through `StageShell.tsx`. That path touches almost every idea in this document.
