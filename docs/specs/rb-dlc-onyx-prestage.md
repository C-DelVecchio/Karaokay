# Spec: Rock Band DLC pre-stage via Onyx (encrypted container → Phase Shift folder → existing importer)

- **Status:** Draft for review (no code changes yet)
- **Date:** 2026-11-27
- **Owner:** Architect
- **Decision record:** [ADR-010](../adr/010-onyx-subprocess-dlc-extraction.md) — decryption runs as an
  external process; nothing from Onyx is ported, linked, or vendored.
- **Affects:** `src/tools/Karaokay.Importer` (CLI surface, `ImportCommand`, `ScanCommand`),
  optionally `src/core/Karaokay.Infrastructure/MediaIngest` (stem enumeration), and
  `src/tests/Karaokay.IntegrationTests` — the only test project that can see importer internals
  (see §7).
- **Documentation updated at implementation time (not by this spec):**
  `docs/ARCHITECTURE.md` (§3 importer row, §7 importer slice), `docs/PROJECT_STATE.md` (§2 slice
  table, §3.8 ADR table, §4.3 importer usage + exit-code text), and the ADR index lines in
  `ARCHITECTURE.md`'s header and `PROJECT_STATE.md` §3.8.

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

**Resolution precedence (highest first):** `--onyx <path>` → `KARAOKAY_ONYX_PATH` → `PATH`.
The `--onyx` flag is part of `import`'s argument parser (`--out` and `--onyx` are the only accepted
options; anything else stays a usage error). `--onyx` may be an executable path **or** a directory
containing the executable; a directory is normalised by appending the platform's expected name.

**Platform candidates** (checked in this order when a directory or `PATH` entry is resolved):

| OS | Names |
|---|---|
| Windows | `onyx.exe` (the special `command-line` release build is a console app — required, because the GUI build swallows stdout) |
| Linux | `onyx`, `onyx.AppImage` |
| macOS | `onyx`, `Onyx.app/Contents/MacOS/onyx` |

Linux note: an `onyx.AppImage` is executable directly; the docs also mention
`--appimage-extract` → `squashfs-root/usr/bin/onyx` for hosts without FUSE. The locator accepts
either, because both are just an executable path.

**Result model**

```csharp
internal sealed record OnyxLocation(string ExecutablePath, string Source); // Source: "--onyx" | "KARAOKAY_ONYX_PATH" | "PATH"
internal static bool TryResolve(string? cliPath, out OnyxLocation location, out string searchedSummary);
```

- `PATH` scanning reuses the `FfmpegGuard.Detect` shape (split on `Path.PathSeparator`,
  `RemoveEmptyEntries`, trim quotes, catch `ArgumentException` for malformed entries).
- Existence is not enough: on Unix the file must be executable (mode bits), otherwise the locator
  skips it and records `"<path> exists but is not executable"` in the searched summary so the error
  message can explain the near-miss.
- **Never** probes with a shell. The first real invocation happens in `OnyxDlcExtractor`.
- Optional diagnostics: a best-effort `onyx --version` captured into the `import` summary (timeout
  2 s, failure ignored) so bug reports can state the Onyx release. Not a gate — the manual warns the
  CLI may change.

### 4.3 `OnyxDlcExtractor`

**Location:** `src/tools/Karaokay.Importer/Dlc/OnyxDlcExtractor.cs` — **the only class in the
repository that knows how to spell an Onyx command line.** Everything about the invocation (argv
shape, output-format flag, working directory, timeout, exit-code interpretation, cleanup) lives
here; nothing above it passes a flag through.

```csharp
internal sealed class OnyxDlcExtractor
{
    public OnyxDlcExtractor(OnyxLocation onyx, TimeSpan timeout, Action<string>? log = null);

    // One invocation for all detected inputs (a pack is one input file that yields many songs).
    public Task<OnyxExtractionResult> ExtractAsync(
        IReadOnlyList<DlcContainer> inputs,
        CancellationToken cancellationToken);
}

internal sealed record OnyxExtractionResult(
    string TempDirectory,                  // caller owns deletion
    IReadOnlyList<string> SongFolders,     // dirs that contain song.ini, ordinal-sorted
    IReadOnlyList<OnyxInputFailure> Failures); // per-input nonzero exit / timeout

internal sealed record OnyxInputFailure(string InputPath, OnyxFailureKind Kind, int? ExitCode, string Diagnostic);
```

**Responsibilities**

1. **Temp working directory** — `Path.Combine(Path.GetTempPath(), "karaokay-onyx-" + Guid.NewGuid():N)`.
   Deliberately **not** under the media root: `Karaokay.Api`'s `MediaLibrary.Scan()` walks every
   immediate subdirectory of `MediaRoot` and *silently* `continue`s past one missing
   `instrumental.opus`/`lyrics.json` (`Features/MediaLibrary.cs:135-138`). A half-extracted folder
   there would therefore be a phantom entry that never appears in `/api/songs` and never explains
   itself, and an in-progress extraction would race every media-root scan. Keeping the temp tree in
   the OS temp directory means extraction is invisible to the API until each song is fully baked and
   moved into the library.
2. **Invocation** — `Cli.Wrap(location.ExecutablePath).WithArguments(argv)` with
   `CommandResultValidation.None`, `WorkingDirectory = tempDir`, stdout+stderr captured separately
   and capped (keep the last 64 KiB of each, plus a total line cap for messages).
3. **Output discovery** — after a run, enumerate subdirectories of `tempDir` (bounded depth 3, in
   practice depth 1–2 because Onyx's export template decides the nesting) and keep those containing
   `song.ini`. Zero folders after a zero exit is itself a failure (`OnyxProducedNothing`), reported
   with a stderr tail — that combination means CLI drift or a preference problem, and it must not be
   silently "0 songs imported".
4. **Exit-code interpretation** — `0` = success; non-zero = `OnyxFailureKind.NonZeroExit` carrying
   the code; killed by timeout/cancellation = `Timeout`. Never parse Onyx's human output for
   semantics; it is diagnostic text only.
5. **Timeout** — default **15 minutes per invocation** (a large pack decrypt + transcode is slow),
   overridable via `KARAOKAY_ONYX_TIMEOUT` (`TimeSpan.Parse`, invariant culture; documented values
   like `00:30:00`). On expiry the process **tree** is killed, then the extractor returns a
   `Timeout` failure; a stuck child must not hang the CLI.
6. **Cleanup** — on any failure the extractor deletes `TempDirectory` before returning (best effort,
   swallowing `IOException`/`UnauthorizedAccessException`, mirroring `ImportCommand.TryDeleteFile`).
   On success the caller deletes it after importing; if the process dies mid-import the OS temp
   cleaner reclaims it.
7. **Multi-input policy** — all detected containers in a directory go into **one** invocation (that
   is how packs and multi-file PS3 DLC are shaped). If the spike shows one-input-per-run is more
   reliable on some platform, the fallback is a sequential loop inside this class only; the caller
   never learns the difference. Per-input failures in that loop are collected, not thrown.

**Security hygiene** — arguments are passed as an array (no shell, no quoting bugs, no injection via
file names such as `a-ha - Take on Me`); stderr/stdout are treated as untrusted text (strip ANSI
escapes and control characters, truncate to the last ~20 meaningful lines) before printing.

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
   `ImportSongFolderAsync(directory, outRoot, ct)` so the multi-song loop can reuse it). No Onyx
   lookup, no behaviour change, no extra output.
3. **`Unknown`** → the §6.4 message, exit 1.
4. **`EncryptedContainers`** → resolve Onyx (`OnyxLocator`); not found → §6.1 message (PS3 caveat
   appended when any detected container `IsPs3`), exit 1. Found → `OnyxDlcExtractor.ExtractAsync`,
   then:
   - iterate `SongFolders` in ordinal order, importing each via the same private method;
   - catch per-song failures and **continue** — the existing import path already prints that song's
     own `error:` line, so the pre-stage adds no second per-song failure format (§6.6);
   - after the loop print an aggregate (`imported N of M songs from '<input>'`), and return
     `ExitFailure` if any song failed (or extraction failed outright), else `ExitSuccess`;
   - delete the temp directory in `finally`.

**Default out root.** Unchanged for directories (`<dir>/karaokay-out`). For a **file** input, the
default is `<parent of file>/karaokay-out`, so `import "media/rb-ingest/a-ha - Take on Me"` writes
next to the input rather than into a non-existent "file directory".

**Song ids** still come from each **extracted** folder's `song.ini` via `ResolveSongId`
(`song_id` → slug of `name` → slug of directory name), with the existing collision handling. Onyx's
export folder names are irrelevant to the final library id — a useful property, because Onyx's
folder-name template may change (see §9).

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

### 4.7 Audio format decision: accept `.opus` stems

**Problem.** Onyx's Phase Shift export can write Ogg Vorbis or Opus, and the choice follows Onyx's
persisted preferences, not a documented flag we can rely on (the CLI is explicitly undocumented and
may change). Making the pre-stage depend on Onyx preference state would create a configuration
coupling we cannot test.

**Decision (recommended).** Teach the importer to accept `.opus` stems, and treat "prefer Vorbis in
Onyx" as a nicety to set when the spike finds a flag, not as a requirement.

- `SongFolderProbe.IsAudioStemFile` gains `.opus` alongside `.ogg`/`.oga` (Increment 2; Increment 1
  ships the helper with the current two extensions so the refactor can be reviewed on its own).
- **Duplicate protection:** a folder containing both `song.ogg` and `song.opus` must not mix the same
  stem twice. `StemSet.Discover` groups by basename and prefers `.ogg`/`.oga` over `.opus`; the
  loser is ignored (and noted in `scan` output only if it is the sole copy of a distinct stem).
- Cost: one predicate change plus the dedupe rule; both are unit-testable without Onyx.
- Consequence: the extracted folder's stems feed `StemMixer` unchanged, which re-encodes everything
  to Opus at −16 LUFS anyway — the intermediate codec only affects decode compatibility, and
  `libopus`/`libvorbis` are both in the FFmpeg builds the project already requires (`FfmpegGuard`,
  CI `apt install ffmpeg`).

**Rejected alternative:** require Onyx preferences to be set to Ogg Vorbis. Rejected because it is
untestable in CI, invisible in a headless run, and exactly the kind of undocumented-CLI dependency
ADR-010 is trying to contain.

---

## 5. Onyx invocation contract

### 5.1 What we need from the CLI

One operation, stated as a contract rather than a command line:

```text
onyx <convert/decode verb> <input container>... --output <directory> --format "Phase Shift"
  ⇒ <directory>/<template>/song.ini
    <directory>/<template>/notes.mid
    <directory>/<template>/song.ogg|opus, guitar.*, bass.*, drums.*, keys.*, vocals.*, crowd.*
```

Requirements:

1. Accept an STFS file (CON/LIVE/PIRS signature), a PS3 `.pkg`, a bare `.mogg`, or an `.ARK`
   directory as input; decrypt MOGG audio itself.
2. Write a **Phase Shift / Clone Hero** folder: `song.ini` + `notes.mid` + per-stem audio, with
   PS/CH stem basenames (`song`, `guitar`, `bass`, `drums`, `keys`, `vocals`, `crowd`).
3. Write **into a caller-specified directory** (not next to the input, not into a global library).
4. Emit **multiple song folders** for a multi-song pack, without interaction.
5. Exit `0` on success; non-zero on failure with a human-readable reason on stderr.
6. Never require Magma/Wine for this path (only package *building* needs them).

### 5.2 The spike (Increment 2, first task)

The manual says command-line usage "is not documented here and is subject to change", so the exact
argv is **pinned empirically before the wrapper is frozen**. Deliverable: a short note in the
implementation PR (and a comment block above `BuildArguments`) recording, per platform:

| Question | How to answer |
|---|---|
| Top-level verb and its options | `onyx --help` |
| Convert/export verb spelling and output flag | `onyx <verb> --help`, iterate |
| Export-format name for Phase Shift/Clone Hero | `onyx <verb> --help`; try the documented GUI export names |
| Output directory semantics (one dir vs per-song subdir) | Run on one CON file into an empty dir; list the tree |
| Audio format default and whether a flag overrides it | Resulting extensions; if no flag exists, rely on §4.7 |
| Multi-input / pack behaviour | Run with two CON files and with one pack file |
| Exit codes for "unsupported input" vs "corrupt input" | Deliberately feed a truncated container |
| Windows console build path and Linux AppImage invocation | Run each platform's artifact |
| `onyx-resources/raps/` lookup location | PS3 `.pkg` run without/with a RAP file |

**Isolation requirement (repeat, because it is the mitigation):** the pinned recipe exists **only**
inside `OnyxDlcExtractor.BuildArguments` (plus its unit test). No other file may contain an Onyx
flag, and no flag may be passed in from configuration. CLI drift is then a one-file, one-test fix.

### 5.3 Release pinning

The onyxite.org manual documents **20240120**, and several newer releases exist. That is a *doc*
version, not a tested one: the **actual tested release is pinned by the Increment-2 spike** and
recorded in the implementation note, in `OnyxLocator`'s best-effort version log, and in the importer
usage text. Mismatches are **reported, not enforced**: an older release may work; a newer one may
change flags, which is exactly why the recipe lives in one class.

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
       Searched: --onyx (not given), KARAOKAY_ONYX_PATH (unset), PATH (7 entries).
       Onyx decrypts Rock Band CON/LIVE/PIRS and PS3 packages. Install a current release (the
       onyxite.org manual documents 20240120) from https://github.com/mtolly/onyx/releases,
       then choose one:
         - add the onyx executable to PATH,
         - set KARAOKAY_ONYX_PATH=/path/to/onyx,
         - pass --onyx /path/to/onyx.
       macOS: Onyx.app/Contents/MacOS/onyx. Linux: onyx.AppImage (or --appimage-extract).
       Windows: use the 'command-line' release build, not the GUI build.
```

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

### 6.3 Onyx timeout

```text
error: Onyx did not finish extracting 'media/rb-ingest/Bob Marley and the Wailers - Buffalo Soldi'
       within 15 minutes and was stopped.
       Large multi-song packs can take longer. Raise the limit with
       KARAOKAY_ONYX_TIMEOUT=00:30:00 and re-run. Onyx output was discarded.
```

### 6.4 Unsupported / unrecognized input

```text
error: 'media/rb-ingest/whatever.txt' is not a song folder and not a container Karaokay recognizes.
       Expected one of:
         - an extracted song folder: song.ini + notes.mid (or song.mid) + *.ogg/*.oga stems
         - an STFS package (Xbox 360 Rock Band DLC, CON/LIVE/PIRS)
         - a PS3 .pkg or encrypted .mid.edat
         - a bare .mogg or an unpacked .ARK (MAIN.HDR / main_xbox.hdr / main_ps3.hdr)
       If this is encrypted Rock Band content, decrypt it with Onyx first — see
       docs/specs/rb-dlc-onyx-prestage.md.
```

Increment 1 lists `*.ogg`/`*.oga` only, matching what the importer accepts today; `.opus` joins the
expected-inputs text in Increment 2 together with §4.7.

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
(§4.3 responsibility 3) and reports through §6.2's `OnyxProducedNothing` path, never as a
`0 of 0` summary.

### 6.7 Exit-code table

| Situation | Exit |
|---|---|
| Plain folder or encrypted folder fully imported | 0 |
| `scan` with no matches (plain or encrypted) | 0 |
| Onyx missing / Onyx failed / timeout / unknown input / any song failed | 1 |
| Bad arguments (`--onyx` without value, two positionals, unknown flag) | 2 |

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

- `OnyxLocatorTests`: precedence `--onyx` > `KARAOKAY_ONYX_PATH` > `PATH`; directory form
  (`--onyx /opt/onyx` finds `/opt/onyx/onyx`); non-executable candidate skipped with a note; the
  missing-Onyx message contains the install URL, all three mechanisms, and (for PS3 input) the RAP
  caveat. Env-var mutation is confined to a dedicated xUnit collection (`[Collection]`) so it cannot
  race other tests; the locator takes an injectable environment accessor rather than reading
  `Environment` directly.
- `OnyxDlcExtractorTests`: argv built by `BuildArguments` matches the pinned recipe exactly (this
  test is the drift alarm); non-zero exit → `NonZeroExit` with stderr tail; a fake executable that
  exits 0 but writes no `song.ini` → `OnyxProducedNothing`; a fake that sleeps past a 200 ms timeout
  → `Timeout` and the temp directory is gone; ANSI escapes stripped; 10 MB of stderr truncated.
  Fakes: a committed `fake-onyx.sh` (POSIX, `chmod +x` in the repo) for Linux/CI, and a
  `fake-onyx.cmd` for Windows; tests that need the shim are `[Trait("Category", "RequiresPosixShim")]`
  or use a generated script so Windows CI is not wedged. Exit-code interpretation itself is tested
  by a C# fake process only where a shim is unavailable.
- `ScanCommandTests`: a root with two loose containers (the `media/rb-ingest` shape) prints exactly
  one `. | requires Onyx — 2 encrypted containers (STFS CON x2)` line; a directory holding a single
  container prints one line with `1 encrypted container`; container rows are not counted as
  importable songs; vocals-only folders are no longer advertised as importable (§4.5); plain-folder
  output is byte-identical to today's expected line for the existing fixture.

### 7.3 Integration — `OnyxGuard` (skip, don't fail)

`src/tests/Karaokay.IntegrationTests/OnyxGuard.cs`, mirroring `FfmpegGuard`'s structure
(`Lazy<bool>`, `PATH` split, platform suffix `onyx`/`onyx.exe`) with **one deliberate difference**:
Onyx is optional, so the guard **skips** rather than fails. `FfmpegGuard.Require()` throws because
every importer e2e test needs FFmpeg; a missing Onyx must not turn a normal `dotnet test` red.

```csharp
internal static class OnyxGuard
{
    public static bool IsAvailable { get; }          // Lazy, detects onyx[/.exe] on PATH
    public static string? ExecutablePath { get; }
    public static void SkipIfUnavailable();           // Skip.If(!IsAvailable, "…") — called from a [SkippableFact]
}
```

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

Test: `OnyxDlcExtractorEndToEndTests` — `import` on a directory containing one real container, or on
`import "<file>"`, asserts the three-file layout via `VerifyCommand.Execute`, non-zero exit + exact
message when `KARAOKAY_ONYX_PATH` points at a non-executable path, and the multi-song path when a
pack is available.

**Fixture policy (hard rule, C2):**

- **No copyrighted DLC bytes are ever committed.** Not truncated, not "just the header of a real
  file", not in LFS. The detector fixtures are hand-built magic + zeros.
- The round-trip test may look for `media/rb-ingest/` **when it exists locally** (developer machines
  — this is exactly how the two current files get exercised) and must `Skip` cleanly when it does
  not. It must never assert on file names, counts, or content of that folder, because the folder is
  gitignored and machine-specific.
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

- Onyx usage spike (§5.2) with its findings recorded.
- `OnyxLocator` + `--onyx` flag + `KARAOKAY_ONYX_PATH`.
- `OnyxDlcExtractor` with temp dir, single invocation point, timeout, capped stderr, cleanup,
  multi-input policy.
- `ImportCommand` pre-stage: extraction, per-song loop, aggregate summary, pack continuation.
- `.opus` stem acceptance + duplicate-stem dedupe (§4.7) and the corresponding `StemSet` tests.
- `OnyxGuard` + guarded e2e test; fake-Onyx unit-level tests — all in `Karaokay.IntegrationTests`
  (§7).

Acceptance criteria:

1. With the spike-pinned Onyx release on `PATH`, `import "media/rb-ingest/a-ha - Take on Me" --out
   /tmp/out` and `import media/rb-ingest --out /tmp/out` both produce one `verify`-passing folder per
   song (`instrumental.opus`, `vocals.opus`, `lyrics.json`), with `song.ini`-derived ids and −16 LUFS
   instrumental (±1 LU, measured as in `ImporterEndToEndTests`).
2. Without Onyx, the §6.1 text is printed verbatim (a unit test asserts the string, so the install
   URL and the three mechanisms cannot rot silently), exit 1, and nothing is written to `--out`.
3. `KARAOKAY_ONYX_PATH` and `--onyx` each resolve a non-`PATH` install; `--onyx` wins over
   `KARAOKAY_ONYX_PATH`.
4. A fake Onyx that exits non-zero surfaces its stderr tail and leaves no temp directory; a fake that
   hangs is killed at the timeout with the §6.3 message.
5. A two-song fixture pack where one exported folder fails the **unchanged** import path (e.g. a
   malformed `notes.mid`) imports the good song, prints that song's existing error line, prints
   `import summary: imported 1 of 2 songs from …`, and exits 1 (§6.6).
6. The full suite skips (not fails) when Onyx is absent; the detector/locator/argv tests run
   everywhere.

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
| **Onyx CLI is explicitly undocumented and subject to change** | A new release breaks the invocation | Single wrapper (`OnyxDlcExtractor.BuildArguments`) + a unit-level test pinning the argv recipe + the §5.2 spike; a broken release is a one-file fix. `--version` is logged, never enforced. |
| **PS3 RAP prerequisite** | `.pkg`/`.mid.edat` fails for users who lack licences, with an opaque Onyx error | Detect PS3 input, append the RAP path hint (§6.1/6.5); check for `onyx-resources/raps/` to sharpen the message; state it in the usage text and spec. Never attempt to obtain RAP files. |
| **Multi-song pack folder naming comes from Onyx templates** | Folder names change between releases; ids/paths shift | Import ids never derive from Onyx folder names — they derive from the extracted `song.ini` via `ResolveSongId`. Discovery is "any subdirectory containing `song.ini`", not a fixed template. |
| **Audio may be Opus instead of Vorbis** | Today's importer only sees `.ogg`/`.oga` → "no instrumental stems" | Accept `.opus` (§4.7) with duplicate-stem dedupe; do not depend on Onyx preference state. |
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
2. **`verify` command doc mismatch.** `ARCHITECTURE.md` §3 lists `verify <songId>` while
   `ImporterCli` implements `verify <dir>`. Fix the blueprint at implementation time (Increment 3)
   rather than silently changing the command; ADR-005's governance rule applies.
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
