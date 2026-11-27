# Spec: Rock Band DLC pre-stage via Onyx (encrypted container → Phase Shift folder → existing importer)

- **Status:** Implemented — Increments 1–3 shipped and merged (2026-11-27) against Onyx 20261111.
  §3.1 is a historical snapshot of the pre-implementation state, kept for context.
- **Date:** 2026-11-27
- **Owner:** Architect
- **Decision record:** [ADR-010](../adr/010-onyx-subprocess-dlc-extraction.md) — decryption runs as an
  external process; nothing from Onyx is ported, linked, or vendored.
- **Affects:** `src/tools/Karaokay.Importer` (CLI surface, `ImportCommand`, `ScanCommand`),
  optionally `src/core/Karaokay.Infrastructure/MediaIngest` (stem enumeration), and
  `src/tests/Karaokay.IntegrationTests` — the only test project that can see importer internals
  (see §7).
- **Documentation updated in Increment 3:**
  `docs/ARCHITECTURE.md` (§3 importer row, §7 importer slice, companion-ADR list),
  `docs/PROJECT_STATE.md` (§2 slice table, §2.1 importer bullet, §3.8 ADR table, §4.2/§4.3 importer
  text, §6.2/§6.2.1 dev workflow), and the ADR-010 status line.

---

## 1. Motivation

Two raw DLC containers are sitting in `media/rb-ingest/`:

```text
media/rb-ingest/
├── a-ha - Take on Me                                       (25 MB, no extension, starts "CON ")
├── a-ha - Take on Me:Zone.Identifier                       (242-byte NTFS sidecar)
├── Bob Marley and the Wailers - Buffalo Soldi              (23 MB, no extension, starts "CON ")
└── Bob Marley and the Wailers - Buffalo Soldi:Zone.Identifier
```

Distinct symptoms, one root cause:

1. **`import media/rb-ingest` fails confusingly.** `ImportCommand.ExecuteAsync` checks
   `Directory.Exists` first, then `File.Exists(Path.Combine(directory, "song.ini"))`, and returns
   `error: song.ini not found in: media/rb-ingest` with exit code 1. Every fact that matters is
   missing: the files are Rock Band DLC, they are encrypted, a tool exists that decrypts them, and
   that tool is not installed here.
2. **`import "<the file>"` is not accepted at all.** `ImportCommand` requires a directory; the user's
   case is two extensionless *files*.
3. **`scan media/rb-ingest` prints `no songs found`.** It is technically correct — the folder has no
   `song.ini`, no MIDI, no `*.ogg` — but it is indistinguishable from "empty folder", and the real
   condition ("this is encrypted DLC, extract it first") is never stated.
4. **Nothing triggers automatically, and nothing is *meant* to.** `media/rb-ingest` is a
   **conventional folder name only**: no code path in the repository references it, no watcher,
   no background service, no `Directory.Build` hook. Putting files there is a user habit, not a
   supported ingress. This spec does not add a watcher (§10), but it does make the manual command
   work from that exact layout.

The product gap is therefore a **pre-stage**, not a new pipeline: turn encrypted containers into
the folder shape the importer already understands, then let the existing code run unchanged.

---

## 2. Goals / Non-goals

### Goals

- **G1.** `import <dir>` detects encrypted Rock Band containers in that directory, transparently
  extracts them with Onyx when Onyx is available, and then imports each produced song folder —
  no manual intermediate step.
- **G2.** `import <file>` accepts a single container file (the user's actual case) as sugar for the
  same flow.
- **G3.** Missing Onyx produces an **actionable, self-explanatory failure**: what was detected, what
  is missing, where it looked, the install URL, the three ways to point at it, and the PS3 RAP
  caveat when the detected input is PS3. Exit code 1, never a stack trace.
- **G4.** `scan <root>` classifies encrypted-container input explicitly
  (`requires Onyx — N encrypted container(s), STFS CON x2` and similar) instead of printing
  `no songs found` or silently listing/rejecting it. This also fixes the Slice 1 review mismatch
  where `scan` advertises folders that `import` refuses.
- **G5.** Multi-song packs are imported song-by-song; one bad song does not abort the rest, and the
  aggregate exit code is 1 if anything failed.
- **G6.** Zero regression for plain extracted folders: the pre-stage is a classifier in front of the
  current path, and a folder satisfying today's contract never touches Onyx.
- **G7.** Zero GPL exposure: no Onyx code, tables, keys, or container-format implementations in this
  repository; the only artifact is a process invocation and an install instruction.

### Non-goals (explicit)

- **N1. Watch-folder / background auto-ingest.** No `FileSystemWatcher`, no hosted service, no
  "drop a file in `media/rb-ingest` and it appears on the stage". Possible future slice (§10);
  the primitives built here are its prerequisites.
- **N2. Native in-process decryption.** Explicitly rejected in ADR-010 (GPL derivative, embedded
  keys, permanent console-format maintenance).
- **N3. C3 CON Tools integration.** Windows GUI-only, not automatable.
- **N4. Formats Onyx itself does not support** (e.g. arbitrary protected containers, disc images,
  DRM'd downloads from other stores). We report "unrecognized", we do not reimplement.
- **N5. Building Rock Band packages** (Magma/Wine), RB4 PS4 *extraction* from a jailbroken console,
  or RAP licence *acquisition*. The user supplies inputs and licences.
- **N6. Emitting `.opus` stems *by preference*** — see §4.7: we accept `.opus` in the importer
  instead of relying on Onyx preference state.
- **N7. A GUI.** The importer stays a CLI.

---

## 3. Current behavior (verified in the tree)

### 3.1 Command surface

`src/tools/Karaokay.Importer/Program.cs` is a 6-line file (a `using` for `Karaokay.Importer.Commands`,
three comment lines, then `return await ImporterCli.RunAsync(args).ConfigureAwait(false);`).
`ImporterCli` is hand-rolled dispatch (no parser dependency):

| Command | Implementation | Argument rule |
|---|---|---|
| `scan <root>` | `ScanCommand.Execute(string root)` | exactly one arg |
| `import <dir> [--out <outRoot>]` | `ImportCommand.ExecuteAsync(string[] args, CancellationToken)` | one positional + optional `--out`; unknown `--x` is a usage error |
| `verify <dir>` | `VerifyCommand.Execute(string directory)` | exactly one arg |
| `--help` / `-h` / `help` | `ImporterCli.PrintUsage()` | — |

Exit codes are constants on `ImporterCli`: `ExitSuccess = 0`, `ExitFailure = 1`, `ExitUsage = 2`.
`ImportCommand` reports failures through `Fail(message)` → `Console.Error.WriteLine("error: " + message)`
→ `ExitFailure`; usage errors go through `UsageError`/`TryParseArguments` → `ExitUsage`.

Documented usage (`PROJECT_STATE.md` §4.3) matches the code:

```bash
dotnet run --project src/tools/Karaokay.Importer -- scan /path/to/library
dotnet run --project src/tools/Karaokay.Importer -- import /path/to/song --out /path/to/library
dotnet run --project src/tools/Karaokay.Importer -- verify /path/to/library/song-id
```

### 3.2 `import` pipeline, step by step

`ImportCommand.ExecuteAsync` currently does, in order:

1. **Parse** via `TryParseArguments` — `directory`, optional `outRoot`; rejects unknown options and
   a second positional ("import expects exactly one song directory.").
2. **`Directory.Exists(directory)`** → `error: song directory not found: <dir>`.
3. **`song.ini` must exist** → `error: song.ini not found in: <dir>` *(this is where encrypted DLC
   dies today)*.
4. **`SongIniReader.Read(iniPath)`** — tolerant INI reader for `name`, `artist`, `delay`, `charter`,
   `song_id`; case-insensitive keys, `;`/`#` comments, optional `[song]` header; unparseable
   `delay` falls back to 0. `IOException`/`UnauthorizedAccessException` → `could not read '<ini>'`.
5. **`MidiFileNameDiscovery.Discover(directory)`** — tries `notes.mid`, `notes.midi`, `song.mid`,
   `song.midi`, `chart.mid`, case-insensitively (matters on Linux); missing →
   `No chart MIDI found in '<dir>'. Looked for: …`.
6. **Target root** — `--out` value, else `<dir>/karaokay-out`.
7. **Song-id resolution** — `ImportCommand.ResolveSongId`: `song_id` if it is a safe directory name,
   else `Slugify(name)`, else slug of the directory name; collision-safe against `outRoot` with
   `-2 … -999` suffixes (or an empty existing directory) and a short-hash fallback. A re-import of
   the same title/artist refreshes the same directory instead of creating `-2` (`HoldsSameSong`
   compares `lyrics.json` title/artist, case-insensitively).
8. **MIDI → lyrics** — `MidiLyricsParser.Parse(midi, songId, ini.Name, ini.Artist, "RockBand",
   new MidiLyricsParserOptions(GlobalOffsetMs: ini.DelayMs))`. Errors are wrapped:
   `MIDI parse failed for '<path>'`, `invalid MIDI file '<path>'`.
9. **Stem enumeration** — `Directory.EnumerateFiles(directory).Where(IsStemFile)`; `IsStemFile`
   accepts only `.ogg` and `.oga` (case-insensitive). This predicate is duplicated in
   `StemMixer.IsAudioStem` and `ScanCommand.IsStemFile`.
10. **Instrumental selection** — `StemMixer.SelectInstrumentalStems`: `.ogg`/`.oga` excluding
    `crowd*` and `vocals*`, ordinal-ignore-case sorted. Empty →
    `no instrumental stems (*.ogg/*.oga excluding crowd*/vocals*) found in: <dir>`.
11. **Vocal selection** — `StemMixer.SelectVocalStems` (`vocals*`); empty means a warning plus a
    synthesized 1-second silent `vocals.opus`, so the three-file layout always exists.
12. **Bake** into sibling temp paths (`.<name>.<guid>.tmp<ext>`), so a half-written song directory is
    never visible: `StemMixer.BakeInstrumentalAsync` / `BakeVocalsAsync` run **two-pass FFmpeg
    `loudnorm`** (`LoudnessTargetLufs = -16`, `TP=-1.5`, `LRA=11`) and encode `libopus`, 48 kHz,
    stereo, 96 kbps. FFmpeg failure → `StemMixerException` → `error: <message>`.
13. **Duration** — `StemMixer.TryProbeDurationMsAsync` via `ffprobe`; a usable value overwrites
    `lyrics.DurationMs`, a missing `ffprobe` silently costs only the probe.
14. **`lyrics.json`** written with `LyricsJson.Serialize` (UTF-8, no BOM) to a temp path.
15. **Promote** the three temp files into `instrumental.opus`, `vocals.opus`, `lyrics.json` with
    `File.Move(..., overwrite: true)`; temp files are deleted in `finally`.
16. **Summary** on stdout: `imported '<songId>' -> <full path>`, byte counts and source stems per
    file, and measured instrumental LUFS (`warning:` if measurement fails).

### 3.3 `scan` behavior

`ScanCommand.Execute(root)` enumerates directories recursively (ordered by ordinal path), and for
each one requires `song.ini`, `MidiFileNameDiscovery.TryDiscover`, and
`SelectInstrumentalStems(...) + SelectVocalStems(...) > 0` — note the **`Count > 0` union**, which is
the Slice 1 review item 6 mismatch: a vocals-only folder passes `scan` and then fails `import` with
"no instrumental stems". Matching folders print one line:

```text
<relative dir> | <name> | <artist> | delay=<n> ms | midi=<file> | instrumental=[…] | vocals=[…] [| crowd=[…]]
```

`BuildLine` puts `Path.GetRelativePath(root, directory)` first (with `/` separators and `.` when the
folder *is* the root), so `song.ini` is never the first field. An empty result prints exactly
`no songs found` with exit code 0 (always 0, except `root directory not found` → 1). Folders without
`song.ini` are skipped **silently** — which is why `media/rb-ingest` looks empty today. The
recursive enumeration only visits subdirectories, so **loose container files directly in the scanned
root are never examined at all** (see §4.5).

### 3.4 External-tool pattern already in the codebase

`StemMixer.RunProcessAsync` uses `Cli.Wrap(fileName).WithArguments(list)` (no shell), captures
stdout/stderr into `StringBuilder`s, sets `CommandResultValidation.None`, and translates a
`Win32Exception` into `StemMixerProcessStartException("Could not start '<tool>'. Is it installed on
PATH?")`. Tests use `FfmpegGuard`: a `Lazy<bool>` that splits `PATH`, applies the `ffmpeg.exe`
Windows suffix, and `Require()`s with an actionable `InvalidOperationException`.

The Onyx pre-stage follows **both** patterns: `CliWrap` for execution, a guard class for tests.

---

## 4. Design

### 4.0 Where the pre-stage sits

```text
import <path> [--out R] [--onyx P]
   │
   ├─ path is a FILE ─────────────┐
   └─ path is a DIRECTORY ─┐      │
                           ▼      ▼
                   DlcContainerDetector.Classify(path)
                           │
        ┌──────────────────┼─────────────────────────┐
        │                  │                         │
  PlainSongFolder    EncryptedContainers         Unknown
        │                  │                         │
        │                  ▼                         ▼
        │          OnyxLocator.Resolve()      "unrecognized input" (exit 1)
        │            │            │
        │        found        not found → actionable error (exit 1)
        │            ▼
        │    OnyxDlcExtractor.ExtractAsync()  → temp dir with N song folders
        │            │
        └────────────┴─► existing import per folder (SongIni → MIDI → StemMixer → lyrics.json)
                                     │
                                     ▼
                         per-song report + aggregate exit code
```

The pre-stage runs **after** argument parsing and path existence checks, but **only when `song.ini`
is absent**. A directory that has `song.ini` skips classification entirely and takes the existing
path with its existing errors, byte for byte — deliberate behaviour preservation, so a hybrid folder
(`song.ini` plus a stray container) is still handled by the old validation rather than by the
pre-stage. Nothing about the baking pipeline moves.

### 4.1 `DlcContainerDetector`

**Location:** `src/tools/Karaokay.Importer/Dlc/DlcContainerDetector.cs` (importer-owned, because it
is about command ingress, not media semantics). Pure, synchronous, no I/O beyond reads of the first
few hundred bytes. It is `internal`, and its tests live in `Karaokay.IntegrationTests` because that
is the only assembly the importer grants `InternalsVisibleTo` (see §7).

**Output model**

```csharp
internal enum DlcInputKind
{
    PlainSongFolder,      // satisfies (or plausibly satisfies) the existing import contract
    EncryptedContainers,  // one or more Onyx-convertible inputs
    Unknown,              // neither
}

internal sealed record DlcDetection(
    DlcInputKind Kind,
    IReadOnlyList<DlcContainer> Containers);   // empty unless EncryptedContainers

internal sealed record DlcContainer(string Path, DlcContainerFormat Format, bool IsPs3);
```

There is deliberately no free-text `Detail` payload on `DlcDetection`: callers build their messages
from the kind plus the container list (`DescribeCount`/`FormatKinds`), so there is one description
path rather than a second one that can drift.

**Content sniffing rules** — **content first; filename extensions only as a documented hint** (the
`.pkg` and `.edat` rows below). A file is only handed to Onyx when the content check passes; an
extension alone never spawns the extractor. The table is the contract, and the constants are pinned
by the committed fixtures in Increment 1.

**Verified header bytes.** Both files in `media/rb-ingest` were byte-inspected for this spec.
`xxd -l 16` on each returns the identical prefix:

```text
43 4F 4E 20 01 A8 09 12 BA 26 E3 58 38 30 33 33   CON .....&.X8033
```

That is the 4-byte ASCII magic `CON ` (`0x43 0x4F 0x4E 0x20`) **including the trailing space**,
followed by the STFS header fields. The extensionless filenames are irrelevant to detection.

| Evidence | Format | Notes |
|---|---|---|
| bytes 0–3 = `43 4F 4E 20` = ASCII `CON ` (**trailing space**) | STFS, Xbox 360 (CON signature) | **Verified** against both `media/rb-ingest` files. Single song **or** multi-song pack. |
| bytes 0–3 = `4C 49 56 45` = ASCII `LIVE` (4 bytes, **no** trailing space) | STFS, Xbox 360 (LIVE signature) | Same container family, different signature block. |
| bytes 0–3 = `50 49 52 53` = ASCII `PIRS` (4 bytes, **no** trailing space) | STFS, Xbox 360 (PIRS signature) | Accepted for completeness; same handling as CON/LIVE. |
| `.pkg` whose header begins `7F 43 4E 54` (`0x7F` + ASCII `CNT`) | PS3 package | **PS3** → RAP prerequisite; contains `.mid.edat` charts. Extension-hinted: the `.pkg` name selects the PS3 magic check. |
| `.edat`/`.mid.edat` whose header begins `4E 50 44 00` (ASCII `NPD\0`) | PS3 encrypted data (chart/audio) | **PS3** → RAP prerequisite. Extension-hinted like `.pkg`; pinned by its fixture before being trusted on real input. |
| `OggS` at byte 0 **and** the `mogg` vendor marker inside a **512-byte read window** | bare MOGG (encrypted multitrack audio) | Distinguishes a bare MOGG from an ordinary `.ogg` stem, which must **not** be treated as a container. In a normal Ogg stream the vendor string sits in the comment header of the *second* page, so "the first ~64 bytes" is the wrong model: read a 512-byte window and search it, with the exact pinned offset fixed by the Increment-2 spike/fixture. |
| directory contains `MAIN.HDR`, `main_xbox.hdr`, or `main_ps3.hdr` (case-insensitive) | `.ARK` archive (unpacked) | Directory-shaped input. The three marker names are the ARK header files documented by the Onyx manual. |

RBN `.ctx` packages are **not** listed in the Onyx manual's supported inputs, so the detector makes
no claim about them (see §10). If support is confirmed later, the rule must content-verify before
invoking Onyx — an extension-only match is not enough.

**PIRS caveat (spike finding).** Onyx 20261111's own file recogniser accepts `CON `, `LIVE`, and the
PS3 `.pkg` magic, but **not `PIRS`**. The detector deliberately keeps `PIRS` (it is a valid STFS
signature and the platform may see it), so a PIRS input is still classified correctly here and then
surfaces Onyx's own clean `Couldn't find a song at location: …` error (exit 1) through the §6.2
path. That is preferable to silently dropping a container the user handed us.

**Directory classification algorithm**

1. **Plain wins.** If the directory already has `song.ini` **and** a discoverable MIDI **and** at
   least one instrument stem (the shared probe from §4.6), return `PlainSongFolder` **immediately**,
   even if stray container files also sit there. This is the no-regression guarantee.
2. **ARK markers before loose files.** If the directory contains any ARK header file (`MAIN.HDR`,
   `main_xbox.hdr`, `main_ps3.hdr`, case-insensitive), return a single `EncryptedContainers` entry
   for the directory and stop. A directory that holds both an ARK marker and loose containers
   therefore reports **only** the ARK: the marker is a directory-level archive identity, so the
   directory is the container (§4.1.1).
3. Otherwise enumerate **top-level regular files** (not recursive) and sniff each one; collect
   recognised containers in ordinal-name order for deterministic messages and test assertions.
4. If at least one container was recognised → `EncryptedContainers` with all of them (a folder of
   two DLC files, a pack file, and a folder holding one `.pkg` all land here).
5. Else → `Unknown`. There is no detail string to inspect; callers choose the message from the kind
   (and, for `EncryptedContainers`, the container list).

**File classification (`import <file>`)** — the same sniffer on one file. A file that is not a
container is `Unknown` and produces the "unrecognized input" message (§6.4); `import` never tries to
treat a bare file as a song folder.

**Ignored sidecar files** — must be skipped before sniffing and never counted as "the folder has
content", because they are exactly what the user's Windows unzip produced:

| Pattern | Origin |
|---|---|
| name ends `:Zone.Identifier` (case-insensitive) | NTFS alternate data stream stored as a sibling file on Linux/macOS |
| name ends `.Zone.Identifier` (case-insensitive) | same, when a tool mangles the colon |
| name starts `._` | macOS AppleDouble |
| name is `.DS_Store`, `Thumbs.db`, `desktop.ini` (case-insensitive) | OS metadata |

**Guard rails**

- Files shorter than 4 bytes are never containers.
- Sniffing opens with `FileShare.ReadWrite` and `FileOptions.SequentialScan`, reads at most 512 bytes,
  and swallows `IOException`/`UnauthorizedAccessException` into `Unknown` (a permission error on one
  file must not crash `scan` of a whole library).
- No recursion into subdirectories for container detection: a directory that *contains* song folders
  is a library, not a pack. `scan` is what walks libraries.

#### 4.1.1 `.ARK` and directory-shaped inputs

An unpacked ARK (any of `MAIN.HDR`, `main_xbox.hdr`, `main_ps3.hdr`, case-insensitive) is passed to
Onyx as a directory path, and `IsPs3 = false`. Nothing else in the detector treats a directory as a
container; this is the one exception and it is tested with an empty placeholder `MAIN.HDR` plus a
lower-case `main_ps3.hdr` variant.

### 4.2 `OnyxLocator`

**Location:** `src/tools/Karaokay.Importer/Dlc/OnyxLocator.cs` (its tests live in
`Karaokay.IntegrationTests`, per the placement rule in §7).

**Resolution precedence (highest first):**

1. `--onyx <path>` (explicit; authoritative — if given and it does not resolve, the command fails
   rather than silently falling back),
2. `KARAOKAY_ONYX_PATH`,
3. `PATH`,
4. repo-local conventions, for zero-setup developer machines:
   `.tools/squashfs-root/usr/bin/onyx` (the extracted AppImage used by this repository's spike),
   then `.tools/onyx.AppImage`.

The `--onyx` flag is part of `import`'s argument parser (`--out` and `--onyx` are the only accepted
options; anything else stays a usage error). `--onyx` may be an executable path **or** a directory
containing the executable; a directory is normalised by appending the platform's expected name.

**Platform candidates** (checked in this order when a directory or `PATH` entry is resolved):

| OS | Names |
|---|---|
| Windows | `onyx.exe` (the special `command-line` release build is a console app — required, because the GUI build swallows stdout) |
| Linux | `onyx`, `onyx.AppImage` |
| macOS | `onyx`, `Onyx.app/Contents/MacOS/onyx` |

**Linux environment prerequisites (spike-verified).** The AppImage runs directly only when
`libfuse2` is installed. Without FUSE, extract it with `--appimage-extract` and use
`squashfs-root/usr/bin/onyx`; the extracted binary may additionally need the host libraries
`libpangoxft-1.0.so.0` and `libXft.so.2` copied into `squashfs-root/usr/lib/` (the binary's RUNPATH
is `$ORIGIN/../lib`, so no wrapper is needed afterwards). This repository's `.tools/` already has
that fixed-up tree, which is why the `.tools` fallback exists.

**Result model**

```csharp
internal sealed record OnyxLocation(string ExecutablePath, string Source);
// Source: "--onyx" | "KARAOKAY_ONYX_PATH" | "PATH" | ".tools"

internal static bool TryResolve(string? cliPath, out OnyxLocation location, out string searchedSummary);
```

- Validation is **exists + executable** (Unix execute bits; on Windows `.exe` existence). There is no
  `--version` probe: Onyx 20261111 rejects `--version` with exit 1, and the manual warns the CLI may
  change. The real availability check is running the import step.
- An explicit `--onyx`/`KARAOKAY_ONYX_PATH` path is accepted **as-is** when it is an existing
  executable file: no suffix is required, and on Windows any existing executable file is accepted
  (`.exe` is not mandatory). Directories are expanded with the platform candidate names above.
- Known test gaps (accepted): `KARAOKAY_ONYX_TIMEOUT` parsing is not covered because it needs
  process-wide environment mutation, and CliWrap's child-process kill is not asserted — the timeout
  tests assert the returned `Timeout` result, not OS process state.
- `PATH` scanning reuses the `FfmpegGuard.Detect` shape (split on `Path.PathSeparator`,
  `RemoveEmptyEntries`, trim quotes, catch `ArgumentException` for malformed entries).
- Existence is not enough: on Unix a non-executable candidate is skipped and recorded as
  `"<path> exists but is not executable"` in the searched summary so the message explains the
  near-miss.
- **Never** probes with a shell. The first real invocation happens in `OnyxDlcExtractor`.
- Tests inject the candidate list/environment (no process-wide `Environment` mutation), so the
  precedence table above is exercised deterministically.

### 4.3 `OnyxDlcExtractor`

**Location:** `src/tools/Karaokay.Importer/Dlc/OnyxDlcExtractor.cs` — **the only class in the
repository that knows how to spell an Onyx command line or touch Onyx's project YAML.** Everything
about the invocation (argv shape, target injection, working directory, timeout, exit-code
interpretation, cleanup) lives here; nothing above it passes a flag through.

**Two-step orchestration (spike-pinned, 20261111).** Onyx's CLI has no one-shot convert command; a
Phase Shift export is an `import` into Onyx's project format followed by a target `build`:

```text
onyx import "<input>" --to "<work>/<project>"                                  # step 1
# inject under `targets:` in "<work>/<project>/song.yml":
#   ps:
#     game: ps
#     audio-format: ogg
onyx build "<work>/<project>/song.yml" --target ps --to "<work>/out/<name>"    # step 2
```

Rules that come from the spike and are non-negotiable:

- **`--to` is always passed to `import`.** Without it Onyx writes `<input>_import` next to the user's
  copyrighted source file. Forbidden.
- **`--to` for `build` has no extension.** Onyx picks the output shape from the extension: none →
  folder (what we need), `.zip` → zip, `.sng` → SNG.
- `audio-format: ogg` is written explicitly even though it is Onyx's default; it pins the codec so a
  user's Onyx preferences cannot change the output (§4.7).
- The target is injected by appending an indented block under the project's `targets:` key. The
  imported project ends with `targets:`; the extractor verifies that invariant (only indented/blank
  lines after it) and fails that input with `LayoutDrift` if a future Onyx changes the layout,
  instead of writing possibly-invalid YAML.
- The argv is identical on Windows/macOS/Linux; only the executable path differs (the locator owns
  that).
- **Input paths are resolved to absolute paths before invoking Onyx.** The extractor runs Onyx with
  the temp work directory as its CWD, so a relative `import media/rb-ingest/...` argument would
  resolve against the temp directory and fail with `Couldn't find a song` (caught during the first
  real CLI run; the unit test now covers it).

```csharp
internal sealed class OnyxDlcExtractor
{
    public OnyxDlcExtractor(OnyxLocation onyx, TimeSpan? timeout = null, Action<string>? log = null);

    // The caller supplies the work directory; the extractor writes project/ and out/ inside it.
    public Task<OnyxExtractionResult> ExtractAsync(
        IReadOnlyList<DlcContainer> inputs,
        string workDirectory,
        CancellationToken cancellationToken);
}

internal sealed record OnyxExtractionResult(
    IReadOnlyList<string> SongFolders,           // dirs that contain song.ini, ordinal-sorted
    IReadOnlyList<OnyxInputFailure> Failures);   // per-input nonzero exit / timeout / produced nothing

internal sealed record OnyxInputFailure(
    string InputPath,
    OnyxFailureKind Kind,                        // NonZeroExit | Timeout | ProducedNothing | LayoutDrift
    int? ExitCode,
    string Diagnostic);
```

**Responsibilities**

1. **Work directory (caller-owned).** `ImportCommand` creates
   `Path.Combine(Path.GetTempPath(), "karaokay-onyx-" + Guid.NewGuid():N)` and deletes the **whole**
   tree in `finally` — success *and* failure — because Onyx leaves both the project (including its
   `gen-<timestamp>-<timestamp>/` build trees) and `out/` inside it. Deliberately **not** under the
   media root: `MediaLibrary.Scan()` silently `continue`s past directory entries without
   `instrumental.opus`/`lyrics.json`, so a half-extracted folder there would be a phantom that never
   appears in `/api/songs` and never explains itself, and an in-progress extraction would race every
   media-root scan.
2. **Per-input sequence.** One import + YAML injection + one build per input, sequentially (a
   discovered directory may hold several containers). Each step writes into its own project tree;
   inputs are processed in the detector's ordinal order so failures and discovery are deterministic.
3. **Process execution** — `Cli.Wrap(location.ExecutablePath).WithArguments(argv)` with
   `CommandResultValidation.None`, `WorkingDirectory = workDirectory`, stdout+stderr captured
   separately and capped (last 64 KiB of each, ANSI-stripped, last ~20 lines printed). Arguments are
   passed as an array (no shell, no quoting bugs for names like `a-ha - Take on Me`).
4. **Exit codes are the only success signal.** `0` = success; non-zero =
   `OnyxFailureKind.NonZeroExit` carrying the code. Onyx prints
   `Warning: An external program was not found on your PATH … checking if Wine is installed` on every
   run in a Wine-less environment; that warning is **informational** and never treated as failure.
   Magma/Wine is only required to *build* Rock Band packages, not Phase Shift folders.
5. **Output discovery.** After a successful build, enumerate subdirectories of `<work>/out` (bounded
   depth 3; Onyx writes the folder directly at the `--to` path) and keep those containing
   `song.ini`. Zero folders after a zero exit is `OnyxFailureKind.ProducedNothing` (§6.8), reported
   with a stderr tail — never a silent “0 songs imported”.
6. **Timeout** — default **15 minutes per input, covering both steps** (the spike measured ~35 s per
   build), overridable via `KARAOKAY_ONYX_TIMEOUT` (`TimeSpan.Parse`, invariant culture; values such
   as `00:30:00`). On expiry the process **tree** is killed and that input yields
   `OnyxFailureKind.Timeout`; a stuck child must not hang the CLI.
7. **Failure continuation.** A failed input is recorded and the next input still runs. The extractor
   never imports anything itself; it returns folders and failures, and `ImportCommand` owns the
   user-facing per-song loop (§4.4).
8. **Pack caveat (unverified).** Both spike inputs were single-song CONs. `onyx import` on a
   multi-song pack may produce a multi-song project whose `build` layout differs; discovery is the
   best-effort “any subdirectory containing `song.ini`” rule, and an unexpected layout becomes
   `ProducedNothing`/`LayoutDrift` for that input rather than a guess (§5, §8).

**Security hygiene** — stderr/stdout are treated as untrusted text (strip ANSI escapes and control
characters, truncate the last ~20 meaningful lines before printing); the work directory is the only
place Onyx writes.

### 4.4 `ImportCommand` integration

**Parsing.** `TryParseArguments` grows one option:

```text
import <dir|file> [--out <outRoot>] [--onyx <path>]
```

`--onyx` without a value is a usage error (`--onyx requires a value.`), matching the existing
`--out` handling. Everything else about parsing is unchanged.

**Flow.** After the `Directory.Exists` check is generalised into an
`File.Exists(path) || Directory.Exists(path)` check:

1. **Branch on `song.ini`.** A directory that has `song.ini` skips classification entirely and runs
   the existing body unchanged (§4.0). Otherwise — and for any **file** input — **classify** with
   `DlcContainerDetector` (`ClassifyDirectory`/`ClassifyFile`).
2. **`PlainSongFolder`** → run the existing body exactly as today (extract it into a private
   `ImportDirectoryAsync(directory, outRoot, ct)` so the multi-song loop can reuse it). No Onyx
   lookup, no behaviour change, no extra output.
3. **`Unknown`** → the §6.4 message, exit 1.
4. **`EncryptedContainers`** → resolve Onyx (`OnyxLocator`). Not found → §6.1 message (PS3 caveat
   appended when any detected container `IsPs3`), exit 1. Found → create the temp work directory, run
   `OnyxDlcExtractor.ExtractAsync`, then:
   - print one §6.2 / §6.3 / §6.8 block per `OnyxInputFailure` (naming the input and its stderr tail);
   - import each returned `SongFolders` entry in ordinal order through the same private method;
   - per-song failures use the existing `error:` line and **continue** (§6.6);
   - print `import summary: imported N of M songs from '<input>'` (M = folders returned); return
     `ExitSuccess` only if every input extracted and every song imported, else `ExitFailure`;
   - delete the **whole** work directory in `finally` (success and failure).

**Default out root.** Unchanged for directories (`<dir>/karaokay-out`). For a **file** input, the
default is `<parent of file>/karaokay-out`, so `import "media/rb-ingest/a-ha - Take on Me"` writes
next to the input rather than into a non-existent "file directory".

**Song ids** still come from each **extracted** folder's `song.ini` via `ResolveSongId`
(`song_id` → slug of `name` → slug of directory name), with the existing collision handling. Onyx's
export folder names are irrelevant to the final library id — a useful property, because Onyx's
folder-name template may change (see §9).

**Onyx's `song.ini` (spike fact).** It contains `[song]`, `name`, `artist`, `album`, `year`, `genre`,
the `diff_*` keys, `song_length`, preview times, and `track` — but **no `delay`, `charter`, or
`song_id`**. `SongIniReader` already tolerates all three absences (`DelayMs = 0`, `Charter`/`SongId`
null), so ids resolve from the slug of `name` (`take-on-me`, `buffalo-soldier` in the spike) and the
lyric offset stays 0. No code change is required; this is recorded so nobody “fixes” the missing keys.

**Output discipline worth preserving.** Each imported song is still baked to temp files and moved
into place only when all three outputs exist. A pack where song 3 fails leaves songs 1–2 complete and
song 3 absent, never a partial song directory that `MediaLibrary.Scan()` would silently skip as a
phantom entry.

### 4.5 `ScanCommand` integration

`ScanCommand` gains one extra pass, using the **same** detector so the two commands cannot disagree.
Two parts, both Increment 1:

1. **Loose files in the scanned root.** Today `ScanCommand.Execute` only enumerates *directories*
   (`Directory.EnumerateDirectories(root, "*", enumerationOptions)`), so a root like
   `media/rb-ingest` — two container files and no subdirectories — never reaches classification at
   all. `scan` must therefore classify the root's own loose files with the detector as well. This is
   a required `ScanCommand` change, not an incidental one.
2. **One line per classified input.** For each classified input — a directory that is not an
   importable song folder, or the root's own loose container files (part 1 above) — print a single
   line carrying the **count and kinds** of containers found in it. The root and each subdirectory
   are separate inputs, so a subdirectory holding one container is its own row:

   ```text
   . | requires Onyx — 2 encrypted containers (STFS CON x2)
   Take on Me (pack) | requires Onyx — 1 encrypted container (STFS CON)
   ```

   The first field stays `Path.GetRelativePath(root, …)` with `.` for the root, matching
   `BuildLine`. Never one line per container: an input is one row.

   **Row order.** Importable song rows print first, in the existing directory-enumeration order.
   Classified-container rows are buffered and printed afterwards, sorted by relative path — so a
   mixed root prints its `.` container row last. The scanner passes its already-computed
   `IsImportable` result into the classifier so a non-song directory is probed once, not twice.

- Track a separate `encrypted` counter, but use it **only** to decide whether the existing
  `no songs found` fallback is suppressed. Current behaviour prints that fallback exactly when
  `found == 0` (`ScanCommand.cs:77-80`); keep the same shape: if `found == 0 && encrypted == 0` →
  `no songs found` (unchanged text); if `found == 0 && encrypted > 0` → print **nothing extra** —
  the `requires Onyx …` lines are the whole output, matching acceptance criterion 1 and §7.2. There
  is no "no importable songs found" summary line. Exit 0 either way.
- Containers formerly printed as plain matches must not double-report: a directory with a valid
  `song.ini` is a song folder (plain wins, §4.1), so it never also gets a `requires Onyx` line.
- Fix the review item 6 mismatch while here: replace the `instrumental + vocals > 0` union with the
  shared probe's "instrumental ≥ 1" rule, so `scan` stops advertising vocals-only folders that
  `import` refuses. This is a *behaviour* change and must be covered by a test (`ScanCommand` output
  on a vocals-only fixture).

### 4.6 `SongFolderProbe` (shared importability rule)

Slice 1 review item 14 records the duplication: `IsStemFile`, `IsAudioStem`, `HasStemPrefix`,
`JoinNames`, and the importability criteria are copied across `StemMixer`, `ImportCommand`, and
`ScanCommand`. The pre-stage makes the divergence actively harmful (three copies of "what is a
stem" would each need the `.opus` change), so Increment 1 introduces one shared helper. This
deliberately narrows item 14 to the **stem/importability helpers**; deduplicating `FfmpegGuard` and
`TryDeleteDirectory` across the test/tool projects stays out of scope.

`ScanCommand` and the importer share one helper, `SongFolderProbe`, which is **public** in
`src/core/Karaokay.Infrastructure/MediaIngest/SongFolderProbe.cs`. It sits beside the already-public
`StemMixer` and `MidiFileNameDiscovery` in the same assembly, because `StemMixer` delegates to it
and both tool commands consume it; making it `internal` would require granting
`InternalsVisibleTo` from `Karaokay.Infrastructure` to the importer tool, which is the worse
direction for a general media-ingest rule.

```csharp
public static class SongFolderProbe
{
    public static bool IsAudioStemFile(string path);      // Increment 1: .ogg | .oga; Increment 2 adds .opus
    public static bool HasStemPrefix(string path, string prefix);
    public static StemSet Discover(string directory);      // instrumental + vocal + crowd lists
    public static bool IsImportable(string directory, out string reason);
}
```

`StemMixer` keeps its public `SelectInstrumentalStems`/`SelectVocalStems` signatures (tests use them)
but delegates the extension and prefix predicates. In Increment 1 this is a pure refactor: behaviour
is unchanged (`.ogg`/`.oga` only) and the only functional delta is the `scan` instrumental rule
(§4.5). `.opus` acceptance arrives in Increment 2 (§4.7).

### 4.7 Audio format: Ogg Vorbis pinned, `.opus` accepted as future-proofing

**Spike result.** Onyx 20261111's Phase Shift target defaults to **Ogg Vorbis** (`audio-format` key,
default `"ogg"`) and emitted 44.1 kHz stereo `.ogg` stems for both real-file conversions. The
extractor writes `audio-format: ogg` explicitly, so the pre-stage does not depend on Onyx
preferences at all.

**Decision.** `.opus` stem acceptance still lands in Increment 2 as approved future-proofing (Onyx
and Clone Hero both support Opus output), but it is **not load-bearing** for this feature: the Onyx
path emits Vorbis. `SongFolderProbe.IsAudioStemFile` accepts `.ogg`, `.oga`, and `.opus`.

- **Duplicate protection:** a folder containing both `song.ogg` and `song.opus` must not mix the same
  stem twice. Selection groups by basename and prefers `.ogg`/`.oga` over `.opus`; the loser is
  ignored.
- Cost: one predicate change plus the dedupe rule, both unit-tested without Onyx.
- Consequence: extracted stems feed `StemMixer` unchanged, which re-encodes everything to Opus at
  −16 LUFS anyway — the intermediate codec only affects decode compatibility, and `libopus`/
  `libvorbis` are both in the FFmpeg builds the project already requires.
- Why keep it: a future Onyx default change, or a user's own Opus PS/CH folder passed to `import`,
  works without another importer change.

**Not chosen:** relying on Onyx preferences for the codec. Rejected because it is invisible in a
headless run; the explicit `audio-format` key removes the coupling entirely.

---

## 5. Onyx invocation contract

### 5.1 The contract we now implement

Onyx's CLI does **not** expose a one-shot converter (spike, 20261111). The contract is two commands
plus a small, validated project-YAML edit:

```text
onyx import <input> --to <work>/<project>
  ⇒ <project>/song.yml, audio.mogg, notes.mid, …
(edit under `targets:`:  ps: { game: ps, audio-format: ogg })
onyx build <project>/song.yml --target ps --to <work>/out/<name>    # no extension ⇒ folder
  ⇒ <work>/out/<name>/song.ini, notes.mid, album.png,
    song.ogg, guitar.ogg, rhythm.ogg, drums.ogg | drums_1.ogg + drums_2.ogg, keys.ogg, vocals.ogg
```

Requirements, with the spike verdict:

1. ✅ Accept an STFS `CON `/`LIVE` file, a PS3 `.pkg`, a bare `.mogg`, or an unpacked `.ARK`, and
   decrypt the MOGG itself. (`PIRS` is not in Onyx's recogniser — §4.1.)
2. ✅ Write a **flat** Phase Shift folder: `song.ini` + `notes.mid` + per-stem Ogg Vorbis
   (`song`, `guitar`, `rhythm`, `drums` or `drums_1`/`drums_2`, `keys`, `vocals`) + `album.png`.
   No `crowd.ogg` appeared in the spike; the importer's stem rules already handle it if present.
3. ✅ Write into a caller-specified path (`--to`), never next to the input.
4. ⚠️ Multi-song packs: **unverified**. Both spike inputs were single-song CONs; layout drift becomes
   `ProducedNothing`/`LayoutDrift` for that input rather than a guess (§4.3, §8).
5. ✅ Exit `0` on success; non-zero with a human-readable reason on failure (e.g. `Couldn't find a
   song at location: …` for unrecognised input).
6. ✅ No Magma/Wine required for this path; the Wine warning on stderr is informational.

### 5.2 Spike results (pinned, 20261111)

Run against `onyx-20261111-linux-x64.AppImage` (sha256-verified) on the two real files in
`media/rb-ingest/`. These are now the implementation's pinned facts:

| Question | Result |
|---|---|
| Top-level verb for export | none — `import` + `build --target ps` |
| `import` output path | `--to` (without it: `<input>_import` beside the input — forbidden) |
| Target YAML needed | `ps: { game: ps, audio-format: ogg }` under `targets:` |
| `build --to` shape | no extension ⇒ folder; `.zip` ⇒ zip; `.sng` ⇒ SNG |
| Audio format | Ogg Vorbis, 44.1 kHz, stereo (`codec_name=vorbis`) |
| Output layout | flat folder; stems `song/guitar/rhythm/drums{,_1,_2}/keys/vocals` + `notes.mid`, `song.ini`, `album.png` |
| Durations | import ≈ 1.0–1.4 s; build ≈ 33.6–34.5 s; whole import of one song ≈ 37–42 s |
| Exit codes | 0 on success; 1 with a clean error for unrecognised input |
| Wine/Magma | Wine warning on stderr every run; neither is needed for PS export |
| Multi-song pack | **not observed** (both inputs single-song) |
| `onyx --version` | unsupported (exit 1) — never used for validation |
| PS3 RAP lookup | not exercised in the spike |

**Isolation requirement (repeat, because it is the mitigation):** the pinned recipe (both argv
shapes plus the YAML injection) exists **only** inside `OnyxDlcExtractor` (plus its unit tests). No
other file may contain an Onyx flag, and no flag may be passed in from configuration. CLI drift is
then a one-file, one-test fix.

### 5.3 Release pinning

The onyxite.org manual documents **20240120**; the spike tested **20261111**
(`onyx-20261111-linux-x64.AppImage`). That tested release is recorded here, in the implementation
note, and in the locator's missing-Onyx message; a mention in the importer **usage text** belongs to
Increment 3's documentation pass. Mismatches are **reported, not enforced**: an older release may
lack the same YAML target keys, and a newer one may change flags, which is exactly why the recipe
lives in one class.

---

## 6. Error handling & UX

Principles: every message names (a) the input, (b) the condition, (c) the next action; no stack
traces; nothing printed to stdout that a scripter would mistake for success; exit codes stay
**0 = success, 1 = failure, 2 = usage error**. All failures go to stderr through the existing
`Fail(...)` convention.

### 6.1 Onyx missing

```text
error: encrypted Rock Band DLC detected in 'media/rb-ingest' but Onyx was not found.
       Detected: 2 containers (STFS CON x2).
       Searched: --onyx (not given), KARAOKAY_ONYX_PATH (unset), PATH (7 entries), .tools (not found).
       Onyx decrypts Rock Band CON/LIVE (Xbox 360) and PS3 packages (with RAP files).
       Install a current release (the
       onyxite.org manual documents 20240120; the tested release is 20261111) from
       https://github.com/mtolly/onyx/releases, then choose one:
         - add the onyx executable to PATH,
         - set KARAOKAY_ONYX_PATH=/path/to/onyx,
         - pass --onyx /path/to/onyx.
       macOS: Onyx.app/Contents/MacOS/onyx. Linux: onyx.AppImage (or --appimage-extract).
       Windows: use the 'command-line' release build, not the GUI build.
       Linux notes: the AppImage needs libfuse2; without FUSE run squashfs-root/usr/bin/onyx,
       and if needed copy libpangoxft-1.0.so.0/libXft.so.2 into squashfs-root/usr/lib/.
```

The `Searched:` line depends on what the user supplied. When an explicit `--onyx` or
`KARAOKAY_ONYX_PATH` value was present but did not resolve, the message lists **that level only**
(the rejected path is the actionable fact); otherwise it lists all four precedence levels, including
the repo-local `.tools` convention (`.tools/squashfs-root/usr/bin/onyx`, then
`.tools/onyx.AppImage`) so local developers know the zero-setup option.

With a PS3 input, append one indented block:

```text
       PS3 .pkg input also needs your RAP licence files at
       onyx-resources/raps/<CONTENTID>.RAP next to the Onyx executable.
```

If a candidate was found but not executable, say so instead of pretending nothing existed:

```text
       Note: '/home/me/onyx.AppImage' exists but is not executable (chmod +x it).
```

### 6.2 Onyx exits non-zero

```text
error: Onyx failed to extract 'media/rb-ingest/a-ha - Take on Me' (exit code 1) after 12.4 s.
       Onyx said (last lines of stderr):
         | <verbatim stderr tail, ANSI-stripped, indented, max ~20 lines>
       Onyx output was discarded; nothing was imported. Re-run with `onyx --help` to check this
       build's command line if the message looks like a usage error.
```

PS3 inputs additionally get the RAP hint appended when stderr mentions a licence/key/decrypt
failure (case-insensitive match on `rap`, `license`, `licence`, `decrypt`, `key`) — the hint is
*additive*, never a replacement for Onyx's own text.

Onyx prints `Warning: An external program was not found on your PATH … checking if Wine is
installed` on stderr in every Wine-less environment. That warning is **informational**: the exit code
is the only success signal, and the extractor never fails on stderr content alone.

### 6.3 Onyx timeout

```text
error: Onyx did not finish extracting 'media/rb-ingest/Bob Marley and the Wailers - Buffalo Soldi'
       within 15 minutes and was stopped.
       Large multi-song packs can take longer. Raise the limit with
       KARAOKAY_ONYX_TIMEOUT=00:30:00 and re-run. Onyx output was discarded.
```

The timeout covers **both** steps (`import` + `build`) for one input; the spike measured ~35 s per
build, so 15 minutes is generous headroom for large packs.

### 6.4 Unsupported / unrecognized input

```text
error: 'media/rb-ingest/whatever.txt' is not a song folder and not a container Karaokay recognizes.
       Expected one of:
         - an extracted song folder: song.ini + notes.mid (or song.mid) + *.ogg/*.oga/*.opus stems
         - an STFS package (Xbox 360 Rock Band DLC, CON/LIVE/PIRS)
         - a PS3 .pkg or encrypted .mid.edat
         - a bare .mogg or an unpacked .ARK (MAIN.HDR / main_xbox.hdr / main_ps3.hdr)
       If this is encrypted Rock Band content, decrypt it with Onyx first — see
       docs/specs/rb-dlc-onyx-prestage.md.
```

Increment 2 lists `*.opus` alongside `*.ogg`/`*.oga`, matching `SongFolderProbe` (§4.7).

Empty directory keeps today's phrasing plus the hint (it is `Unknown`, not encrypted):

```text
error: no song folder or encrypted Rock Band container found in: media/empty
```

### 6.5 PS3 package without RAP files

This is a **sub-case of 6.2** (Onyx runs, then refuses), so the primary text is Onyx's. The appended
hint is explicit because the failure is otherwise opaque:

```text
       Hint: PS3 packages are encrypted with a per-content RAP licence. Put the matching
       onyx-resources/raps/<CONTENTID>.RAP next to the Onyx executable and re-run.
```

If the RAP directory is absent entirely, the hint says `onyx-resources/raps/ was not found next to
'<onyx path>'` — a check `OnyxDlcExtractor` can perform before invoking, purely to sharpen the
message (it still runs Onyx; it never guesses that decryption will fail).

### 6.6 Pack where only some songs succeed

No manifest is introduced. Extraction yields folders; each folder then runs the **unchanged**
`ImportCommand` body, so a failing song prints its **own existing** error line (the one from the
relevant `Fail(...)` site, which always names the offending path), and the pre-stage adds only the
summary line and the aggregate failure:

```text
imported 'take-on-me' -> /home/me/media/library/take-on-me
  instrumental.opus: 4123456 bytes (mixed from 5 stem(s): …)
  vocals.opus:       812345 bytes (source: vocals.ogg)
  lyrics.json:       23456 bytes
  instrumental loudness: -16.0 LUFS (target -16 LUFS)
error: MIDI parse failed for '/tmp/karaokay-onyx-1f3c…/buffalo-soldier/notes.mid': <parser message>
import summary: imported 1 of 2 songs from 'media/rb-ingest'
error: 1 of 2 songs failed; the successful songs were kept.
```

Rules: the per-song `error:` text is produced by the existing pipeline, not by the new code, and its
path is what identifies the failed folder (`No chart MIDI found in '<folder>'`, `invalid MIDI file
'<path>'`, `no instrumental stems … found in: <folder>`, `could not read '<path>'`, and so on).
The pre-stage owns exactly two lines: `import summary: imported N of M songs from '<input>'` and the
final aggregate `error:`. Successful songs are **kept** (never rolled back — re-running is cheap and
idempotent thanks to `ResolveSongId`'s same-song refresh); exit code 1. If *all* songs fail, the
per-song lines still print, then `import summary: imported 0 of M songs from '<input>'` and
`error: 0 of M songs imported.`

**Zero folders extracted is not this case.** If Onyx exits 0 but produces no folder containing
`song.ini`, no per-song import runs at all; that is an invocation-level failure of the Onyx step
(§4.3 responsibility 5) and reports through §6.8's `ProducedNothing` path, never as a `0 of 0`
summary.

When **extraction itself** fails for some inputs while other inputs still produce songs, each failed
input gets its §6.2/§6.3/§6.8 block and, if the remaining songs all import, one final line:
`error: N of M inputs failed to extract; the successful songs were kept.` This is an
extraction-level aggregate only; partial song imports inside successful inputs use the song-level
aggregate above. Exit code is 1 either way.

### 6.7 Exit-code table

| Situation | Exit |
|---|---|
| Plain folder or encrypted folder fully imported | 0 |
| `scan` with no matches (plain or encrypted) | 0 |
| Onyx missing / Onyx failed / timeout / produced nothing / unknown input / any song failed | 1 |
| Bad arguments (`--onyx` without value, two positionals, unknown flag) | 2 |

### 6.8 Onyx produced no song folder

Onyx exited 0 but `<work>/out` contained no directory with `song.ini` (CLI drift, an unexpected
pack layout, or a preference problem):

```text
error: Onyx reported success for 'media/rb-ingest/a-ha - Take on Me' but produced no song folder.
       Expected a folder containing song.ini under the build output. Onyx said (last lines):
         | <stderr tail>
       Nothing was imported from this input.
```

Exit 1 for that input; other inputs still run (§4.3).

---

## 7. Testing plan

**Placement rule.** Every test that touches importer internals (`DlcContainerDetector`,
`OnyxLocator`, `OnyxDlcExtractor`, `SongFolderProbe`, `ScanCommand` classification) lives in
`src/tests/Karaokay.IntegrationTests`. The reason is mechanical:
`src/tools/Karaokay.Importer/Karaokay.Importer.csproj` grants `InternalsVisibleTo` **only** to
`Karaokay.IntegrationTests` (line 13), and `Karaokay.UnitTests` neither references the importer nor
references `Xunit.SkippableFact`. Putting them there requires **no csproj change** and gets the
skip support these tests need (§7.3). They are still unit-level tests — no FFmpeg, no database, no
API host — so the assembly name does not make the suite slow.

### 7.1 Unit-level — detector (no external tools)

`src/tests/Karaokay.IntegrationTests/Dlc/DlcContainerDetectorTests.cs`, fixtures under
`src/tests/Karaokay.IntegrationTests/fixtures/dlc-headers/`. Every fixture is **synthetic and
tiny** — 16–512 bytes with the magic followed by zero padding. No copyrighted bytes, no real
container, and well under `.github/scripts/check-binary-media.sh`'s 100 KiB gate (that script also
only inspects audio/video/image extensions, so `.bin` fixtures are doubly safe). The `CON ` fixture
repeats only the 4-byte magic; it does not copy any further bytes from the user's files.

| Fixture | Bytes | Asserts |
|---|---|---|
| `stfs-con.bin` | `43 4F 4E 20` (`CON `, trailing space) + 60 zeros | STFS CON detected, `IsPs3 == false` |
| `stfs-live.bin` | `4C 49 56 45` (`LIVE`, no trailing space) + 60 zeros | STFS LIVE detected |
| `stfs-pirs.bin` | `50 49 52 53` (`PIRS`, no trailing space) + 60 zeros | STFS PIRS detected |
| `ps3-pkg.bin` | `7F 43 4E 54` + zeros | PS3 package, `IsPs3 == true` |
| `ps3-edat.bin` | `4E 50 44 00` (`NPD\0`) + zeros | PS3 EDAT, `IsPs3 == true` |
| `bare-mogg.bin` | `OggS` page + `mogg` marker inside the 512-byte window | bare MOGG detected |
| `plain-ogg.bin` | `OggS` page **without** `mogg` | **not** a container |
| `short.bin` | 3 bytes | not a container |

The PS3 fixtures are committed as `.bin` so no OS maps them to a handler, but PS3 detection is
**extension-hinted** (§4.1): each test copies `ps3-pkg.bin` → `*.pkg` and `ps3-edat.bin` →
`*.mid.edat` before sniffing, and separately asserts that the same bytes under an extensionless
name are **not** detected.

Directory-level cases: extensionless files (name-only irrelevant, the user's exact shape);
`:Zone.Identifier` and its dot-mangled `.Zone.Identifier` variant, plus `._*`/`.DS_Store`, ignored;
plain folder wins over stray container; empty dir → `Unknown`; `.ARK` dir with empty `MAIN.HDR` and a
second with lower-case `main_ps3.hdr`; two containers → ordinal order; unreadable file → `Unknown`,
no throw.

### 7.2 Unit-level — locator, argv, exit codes (fake `onyx`)

- `OnyxLocatorTests`: precedence `--onyx` > `KARAOKAY_ONYX_PATH` > `PATH` > `.tools` (extracted
  binary, then AppImage); directory form (`--onyx /opt/onyx` finds `/opt/onyx/onyx`); a
  non-executable candidate is skipped with an `exists but is not executable` note; the missing-Onyx
  message contains the install URL, all four mechanisms, the Linux FUSE/lib hints, and (for PS3
  input) the RAP caveat. Tests inject the candidate environment (no process-wide `Environment`
  mutation), so precedence is deterministic.
- `OnyxDlcExtractorTests` (POSIX fake `onyx` generated by the test, skipped on Windows):
  - step 1 argv is exactly `import <input> --to <work>/<project>` and **always** contains `--to`;
  - step 2 argv is exactly `build <project>/song.yml --target ps --to <work>/out/<name>`, with **no
    extension** on the final `--to`;
  - `song.yml` is verified by the fake to contain `ps:`, `game: ps`, `audio-format: ogg` under
    `targets:` (the test captures the file from the fake's second step);
  - non-zero exit → `NonZeroExit` with a capped, ANSI-stripped stderr tail;
  - a fake that ignores the build and writes nothing → `ProducedNothing`;
  - a fake that sleeps (0.3 s per step under a shared 450 ms per-input budget, or a hang against a
    300 ms budget) → `Timeout`; the test asserts the `TimedOut` outcome, not the OS-level
    child-process kill (see §4.2);
  - a fake that writes an unexpected `targets:` layout → `LayoutDrift`;
  - the work directory is entirely caller-owned/cleaned by `ImportCommand` (asserted in the
    ImportCommand test by counting `karaokay-onyx-*` directories before/after a failed run).
- `ScanCommandTests`: a root with two loose containers (the `media/rb-ingest` shape) prints exactly
  one `. | requires Onyx — 2 encrypted containers (STFS CON x2)` line; a directory holding a single
  container prints one line with `1 encrypted container`; container rows are not counted as
  importable songs; vocals-only folders are no longer advertised as importable (§4.5); plain-folder
  output is byte-identical to today's expected line for the existing fixture.
- `SongFolderProbeTests`/`StemSelectionTests`: `.opus` stems are selected; a folder with both
  `song.ogg` and `song.opus` mixes that stem once (`.ogg` wins); `IsImportable` accepts an
  Opus-only instrumental.

### 7.3 Integration — `OnyxGuard` (skip, don't fail)

`src/tests/Karaokay.IntegrationTests/OnyxGuard.cs`. Onyx is optional, so the guard **skips** rather
than fails. `FfmpegGuard.Require()` throws because every importer e2e test needs FFmpeg; a missing
Onyx must not turn a normal `dotnet test` red.

```csharp
internal static class OnyxGuard
{
    public static bool IsAvailable { get; }          // Lazy; OnyxLocator.TryResolve with the real environment
    public static string? ExecutablePath { get; }
    public static void SkipIfUnavailable();           // Skip.If(!IsAvailable, "…") — called from a [SkippableFact]
}
```

The guard resolves through the **same `OnyxLocator` precedence** as the product, so on this machine
it finds `.tools/squashfs-root/usr/bin/onyx` with no environment setup, while honouring
`KARAOKAY_ONYX_PATH`/`PATH` on a developer box. Test code does not carry its own PATH-scanning copy.

The skip mechanism is the repo's existing one, not `Assert.Skip`: xUnit 2.9.3 has no
`Assert.Skip`, so these tests follow the `Xunit.SkippableFact` convention already used by
`RoomHubIntegrationTests` / `YouTubeRateLimitIntegrationTests`:

```csharp
[SkippableFact]
public async Task ImportingARealContainerProducesTheThreeFileLayout()
{
    OnyxGuard.SkipIfUnavailable();   // Skip.If(...) inside the skippable fact
    …
}
```

`Skip.If` may live inside the guard helper because the guard is called from a `[SkippableFact]`
body; it throws the skip exception the runner understands. `Karaokay.IntegrationTests` already
references `Xunit.SkippableFact`, so no package change is needed.

Test: `OnyxDlcExtractorEndToEndTests` — `[SkippableFact]`, skipped when Onyx is absent **or** the two
real files are absent from `media/rb-ingest/` (the folder is gitignored and machine-specific). It
runs `import media/rb-ingest --out /tmp/opencode/onyx-e2e`, asserts exit 0, the console line
`import summary: imported 2 of 2 songs from 'media/rb-ingest'`, and that
`take-on-me/` and `buffalo-soldier/` each contain `instrumental.opus`, `vocals.opus`, and
`lyrics.json` (slug ids from Onyx's `song.ini`, which has `name`/`artist` but no `song_id`). Output
stays under `/tmp/opencode` and is never committed.

**Fixture policy (hard rule, C2):**

- **No copyrighted DLC bytes are ever committed.** Not truncated, not "just the header of a real
  file", not in LFS. The detector fixtures are hand-built magic + zeros.
- The round-trip test is **skip-guarded**: it must never fail when `media/rb-ingest/` or the two
  known files are absent, and it never runs in CI without them. When the known two files are present
  locally, the test may assert their observed names/slugs (`take-on-me`, `buffalo-soldier`) and the
  `2 of 2` count — that is the point of the local-only test. It must not assert on the source files'
  bytes/content, and it must not assume that a *different* user file dropped into that gitignored
  folder will match those names.
- Optional, explicitly flagged: **generating** a synthetic encrypted container with Onyx itself
  (Magma/Wine required to *build*) for local verification only. Not in CI, not a committed fixture,
  not a dependency of any test. If it is ever built, the generator script lives outside the test
  project and the output stays in `media/`.

### 7.4 Regression

The existing importer integration tests — `ImporterEndToEndTests` (synthetic folder, real FFmpeg,
`FfmpegGuard.Require()`), `ImporterFailureTests`, `ImporterSongIdTests`, and `StemSelectionTests` —
must pass unchanged. (There are currently no dedicated unit tests for `SongIniReader` or
`MidiFileNameDiscovery`; adding them is a separate slice-1 review item, not part of this work.)
If a plain folder ever needs Onyx to import, the design has failed.

---

## 8. Milestones

### Increment 1 — Detect, classify, explain (no Onyx dependency at all)

Deliverables:

- `DlcContainerDetector` + `DlcContainer`/`DlcInputKind` (§4.1), with its unit-level tests and
  fixtures in `Karaokay.IntegrationTests` (§7).
- `SongFolderProbe` shared importability helper (`.ogg`/`.oga` only), with
  `StemMixer`/`ImportCommand`/`ScanCommand` delegating (§4.6) — no `.opus` acceptance yet, to keep
  this increment a reviewable refactor.
- `ImportCommand`: accept a file **path** for classification purposes, but only classify when
  `song.ini` is absent (§4.0); `Unknown` and `EncryptedContainers` produce the §6.4 / "manual Onyx
  step" messages. On encrypted input the message names Onyx, the Phase Shift recipe and the runnable
  command, and is explicit that automatic extraction is not in this build:

  ```text
  error: 'media/rb-ingest' contains 2 encrypted Rock Band containers (STFS CON x2).
         Convert them with Onyx first (Phase Shift export), then run:
           dotnet run --project src/tools/Karaokay.Importer -- import <exported-folder> --out <library>
         Install Onyx: https://github.com/mtolly/onyx/releases
         Automatic extraction lands with the Onyx pre-stage; see
         docs/specs/rb-dlc-onyx-prestage.md and docs/adr/010-onyx-subprocess-dlc-extraction.md.
  ```

  A single **file** input uses the natural singular headline instead —
  `error: '<path>' is an encrypted Rock Band container (STFS CON).` — with the same continuation
  block.

- `ScanCommand`: classify the scanned root's **own loose files** as well as its directories (§4.5),
  print one `requires Onyx — N encrypted container(s) (…)` line per classified input, track the
  encrypted counter, and fix the vocals-only advertisement.
- `ImporterCli.PrintUsage()` mentions file-or-directory input and the Onyx requirement.

Acceptance criteria:

1. `scan media/rb-ingest` prints exactly **one** line —
   `. | requires Onyx — 2 encrypted containers (STFS CON x2)` — and does **not** print
   `no songs found` and does not emit one line per container.
2. `scan` on the existing synthetic/plain fixtures is unchanged (golden string assertion).
3. `import media/rb-ingest` exits 1 with the encrypted-container message — no `song.ini not found`.
4. `import <plain folder>` on the e2e fixture is byte-identical to today's behaviour; full .NET suite
   green; no Onyx lookup occurs (assert via a test seam/spy).
5. Detector unit-level tests cover every row of the §4.1 table and every ignore pattern, using only
   committed ≤512-byte fixtures.

### Increment 2 — Locate, extract, import

Deliverables:

- Spike results recorded (§5.2) and the tested release pinned (**20261111**, §5.3).
- `OnyxLocator` with precedence `--onyx` > `KARAOKAY_ONYX_PATH` > `PATH` > `.tools` (extracted
  binary, then AppImage), exists+executable validation, near-miss reporting, and the §6.1 message
  including FUSE/lib hints.
- `OnyxDlcExtractor`: two-step `import`/`build` orchestration, validated `ps` target injection
  (`game: ps`, `audio-format: ogg`), `--to` always present, caller-owned work directory cleaned on
  success **and** failure, 15-minute per-input timeout with `KARAOKAY_ONYX_TIMEOUT`, capped stderr,
  per-input failure continuation, `ProducedNothing`/`LayoutDrift` handling.
- `ImportCommand` pre-stage: locate → extract → per-folder existing import path, continue-on-failure,
  `import summary: imported N of M songs from '<input>'`, aggregate exit code, work-dir cleanup in
  `finally`.
- `.opus` stem acceptance + duplicate-basename dedupe (§4.7); `*.opus` in the §6.4 expected-inputs
  text; `--onyx <path>` in the usage text.
- `OnyxGuard` + the real two-file round trip; fake-Onyx locator/extractor/ImportCommand tests — all
  in `Karaokay.IntegrationTests` (§7).

Acceptance criteria:

1. With the pinned Onyx (`.tools/` or `--onyx`), `import media/rb-ingest --out /tmp/opencode/onyx-e2e`
   imports **2 of 2** songs into `take-on-me/` and `buffalo-soldier/`, each with
   `instrumental.opus`, `vocals.opus`, `lyrics.json` and −16 LUFS instrumental (±1 LU, measured as in
   `ImporterEndToEndTests`); `import "<one file>"` does the same for one song.
2. Without Onyx, the §6.1 text is printed (unit test asserts the install URL, the four mechanisms,
   the FUSE/lib notes, and the PS3 caveat), exit 1, and nothing is written to `--out`.
3. An explicit `--onyx` that does not resolve fails with the missing message rather than falling back
   to env/PATH; `KARAOKAY_ONYX_PATH` and `.tools` each resolve a non-`PATH` install.
4. Fake-Onyx tests pin both argv shapes (including `--to` in both steps and no extension on the
   build output), the YAML target injection, nonzero-exit stderr surfacing, timeout kill,
   `ProducedNothing`, and work-directory cleanup on failure.
5. `.opus` stems are selected, and a duplicate `song.ogg`/`song.opus` pair is mixed once.
6. The full suite skips (not fails) when Onyx is absent; locator, argv, YAML, and detector tests run
   everywhere.
7. **Pack behaviour remains unverified** (both spike inputs were single-song CONs); an unexpected
   multi-song build layout fails that input with `ProducedNothing`/`LayoutDrift` rather than
   guessing, and the iteration code is covered by the continue-on-failure song tests.

### Increment 3 — Documentation and polish (implementation-time)

- `ARCHITECTURE.md`: §3 importer row gains the pre-stage; §7's importer slice notes the Onyx
  dependency and its optionality; the companion-ADR list gains ADR-010.
- `PROJECT_STATE.md`: slice table row for the importer, ADR table row for 010, §4.3 usage text
  (`import <dir|file>`, `--onyx`, exit codes unchanged) and the recording of the tested Onyx
  release.
- Importer usage text: the two new options, the Onyx requirement, and the RAP caveat.

Acceptance: docs describe exactly what the CLI prints, and `onyx --help` drift is documented as a
known, contained risk.

---

## 9. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| **Onyx CLI is explicitly undocumented and subject to change** | A new release breaks the two-step invocation or the YAML target keys | Single wrapper (`OnyxDlcExtractor`) + unit-level tests pinning both argv shapes and the injected YAML + the §5.2 spike; a broken release is a one-file fix. Validation is exists+executable; no `--version` probe (unsupported). |
| **PS3 RAP prerequisite** | `.pkg`/`.mid.edat` fails for users who lack licences, with an opaque Onyx error | Detect PS3 input, append the RAP path hint (§6.1/6.5); check for `onyx-resources/raps/` to sharpen the message; state it in the usage text and spec. Never attempt to obtain RAP files. |
| **Multi-song pack layout is unverified** | A pack import may produce a different `build` layout or folder nesting | Discovery is “any subdirectory containing `song.ini`”; an unexpected layout fails that input with `ProducedNothing`/`LayoutDrift` (§6.8) instead of guessing. A follow-up pack spike is required before promising pack support. |
| **Audio may be Opus instead of Vorbis** | A future Onyx default, or a user's own Opus folder, would hit “no instrumental stems” | `.opus` is accepted (§4.7) with duplicate-basename dedupe; the Onyx path itself pins `audio-format: ogg`, so Vorbis is what we currently receive. |
| **PIRS is not in Onyx's recogniser** | A PIRS-signed STFS input is detected by us but rejected cleanly by Onyx | Documented in §4.1; the failure surfaces Onyx's `Couldn't find a song` through §6.2 rather than being silently skipped. |
| **Linux runtime libs (FUSE/pango/Xft)** | The AppImage cannot start on a minimal host | Documented prerequisites and the extracted-tree fallback in §4.2/§6.1; this repository's `.tools/` tree is fixed up and found by the locator's last precedence level. |
| **Decryption + transcode runtime for large packs** | A 20-song pack can run for many minutes; users think it hung | 15-minute default timeout, progress line before invoking (`extracting with Onyx…`), per-song summary as each finishes, `KARAOKAY_ONYX_TIMEOUT` escape hatch. |
| **Onyx missing on most machines** | Feature appears broken to new users | It is optional and only reached for encrypted input; the error names the install URL, the three resolution mechanisms, and the platform artifact names; plain-folder imports never mention Onyx. |
| **Wrong blob detected as a container** (false positive) | Onyx spawned on an unrelated file, confusing error | Magic-only detection with a 4-byte minimum; `.ogg` explicitly excluded from MOGG detection; plain-folder precedence; unknown → no Onyx spawn. |
| **Temp-directory leakage** | Disk fills after repeated failures | `finally`-based cleanup in the extractor **and** the command; OS temp location (self-cleaning); temp deliberately outside `MediaRoot` so the API never sees it. |
| **License drift** (someone decides to vendor Onyx or port a table "just for tests") | Turns the importer into a GPL derivative | ADR-010 states the boundary and the reason; the spec forbids key/format code in the repo; review checklist item: no new Onyx-derived code, no bundled binary. |
| **Version skew across platforms** (Windows `command-line` build vs AppImage vs bundle) | Different flags/behavior per OS | Spike pinned per platform (§5.2 table); locator knows each artifact name; error message tells Windows users to use the `command-line` build. |

---

## 10. Out of scope / future work

1. **Watch folder for `media/rb-ingest`.** Listing a directory, debouncing, waiting for copy
   completion (a 25 MB file still growing), and hosting it as a service/`import --watch` loop.
   Deferred: it needs the detector and extractor built here, and it raises lifecycle questions
   (partial files, retries, logging, cancellation) that deserve their own spec.
2. **`verify` command doc mismatch — resolved in Increment 3.** The blueprint's importer row
   previously named a song-id argument while `ImporterCli` has always taken a directory; the row now
   matches the CLI (`verify <dir>`). ADR-005's governance rule was followed.
3. **Media-library/API awareness of encrypted input.** `Karaokay.Api`'s `MediaLibrary` scans
   immediate subdirectories of `MediaRoot` for `instrumental.opus` + `lyrics.json`. An `rb-ingest`
   directory placed *inside* the media root is therefore invisible today. Either document "keep
   `rb-ingest` outside `MediaRoot`" or add a skip-list; not part of this feature.
4. **PS3 RAP management UX** (a configured `raps/` directory, validation, clear per-content
   messages beyond the hint).
5. **RBN `.ctx` packages.** The Onyx manual's supported-input list does not mention them, so the
   detector deliberately makes no claim. If a future Onyx release documents RBN support, add a rule
   that content-verifies the package before invoking Onyx — an extension-only match must never spawn
   the extractor (§4.1).
6. **RB4 PS4 extracted folders**, Magma RBA, and any other Onyx input that needs a different
   ingestion shape than "Phase Shift folder".
7. **Bundled/portable Onyx install** (a `scripts/` helper that downloads the pinned release into a
   tool directory). Would be convenient, but distributing the binary is a GPL question ADR-010
   deliberately answers "don't".
8. **Progress reporting to the API/UI** — a pre-stage that streams progress over SignalR is only
   meaningful once ingestion is a hosted service (§10.1).
9. **Extracting to a user-visible intermediate directory** (`--keep-extracted <dir>`) for debugging;
   today's design discards the temp tree, which is the right default.
