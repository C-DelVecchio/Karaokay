# ADR-010: Rock Band DLC decryption through the Onyx toolkit as an external subprocess

- **Status:** Proposed (draft for review)
- **Date:** 2026-11-27
- **Deciders:** Architect
- **Context level:** Media ingestion / Licensing boundary & external tooling
- **Related:** [ADR-004](004-dual-source-media-pipeline.md) (dual-source media pipeline, importer contract, constraint C2), [ADR-005](005-slice-1-blueprint-reconciliations.md) (blueprint governance), [ARCHITECTURE.md §1.2](../ARCHITECTURE.md#12-hard-constraints) (C2), [ARCHITECTURE.md §4.1](../ARCHITECTURE.md#41-lyricsjson--canonical-karaoke-timeline), [docs/specs/rb-dlc-onyx-prestage.md](../specs/rb-dlc-onyx-prestage.md) (full design)

## Context

The importer (`src/tools/Karaokay.Importer`) consumes an **already extracted** Rock Band folder:
`song.ini`, a chart MIDI (`notes.mid` and friends, via `MidiFileNameDiscovery`), and per-stem
`*.ogg`/`*.oga` audio mixed by `StemMixer`. This contract was set by
[ADR-004](004-dual-source-media-pipeline.md) and is what the Slice 1 tests lock down.

Real users do not start from that folder. They start from **what the console store delivered**:
an encrypted STFS package such as `media/rb-ingest/a-ha - Take on Me`, which the local checkout
already has in the (gitignored) `media/` directory. Inspecting it confirms a `CON ` header at byte 0
— a Rock Band Xbox 360 DLC container with `CON` signature. The second file, `Bob Marley and the Wailers -
Buffalo Soldi`, is the same shape, and both were unzipped from Windows archives, so each arrived
with a `…:Zone.Identifier` sidecar and **no filename extension at all**.

Today `import` on that folder stops at the `song.ini` existence check early in
`ImportCommand.ExecuteAsync`:

```text
error: song.ini not found in: media/rb-ingest
```

That message is true and useless: nothing in it says the input is Rock Band DLC, that the DLC is
encrypted, or that a separate tool can turn it into the folder the importer wants. The user's
reasonable next move — put the files in a folder named `rb-ingest` — does nothing either, because
that folder name is a convention, not a wired-up watch folder.

The technical question is therefore: **how does decryption get into the product?**

The candidate tools:

1. **Onyx Music Game Toolkit** (`https://github.com/mtolly/onyx`, author Michael Tolly; the
   onyxite.org manual documents version 20240120). It is the only maintained,
   cross-platform, CLI-capable toolkit that decrypts Rock Band DLC: RB1/2/3/Beatles Xbox 360
   STFS containers (CON or LIVE signature, single songs *and* multi-song packs), PS3 `.pkg`
   files (with user-supplied RAP licence files), `.ARK` archives, bare `.mogg`, RB4
   PS4 extracted files, and Magma RBA. It decrypts the encrypted MOGG audio itself, and it can
   export a **"Clone Hero & Phase Shift"** folder — `song.ini`, `notes.mid`, and PS/CH-named stems
   (`song.ogg`, `guitar.ogg`, `drums.ogg`, `vocals.ogg`, `crowd.ogg`, …) — which is *exactly* the
   input contract the importer already implements.
2. **A native C# port** of Onyx's decryption (or of the console container formats).
3. **C3 CON Tools** — Windows GUI applications.

Onyx is **GPL v3**. That is the fact that determines the architecture, because the rest of this
repository is distributed under its own license and C2 forbids committing commercial media.
Two very different things could be built here, and only one of them keeps our licensing story
simple.

## Decision

**The importer gains a pre-stage that decrypts Rock Band DLC by executing Onyx as a separate
operating-system process. No decryption, container parsing, key handling, or Onyx code is ported,
linked, vendored, or embedded into the C# codebase.**

Concretely:

1. **Process boundary, not a port.** `Karaokay.Importer` locates an `onyx` executable supplied by
   the user and invokes it as a subprocess (`CliWrap`, already the mechanism `StemMixer` uses for
   FFmpeg) to convert an encrypted input into a Phase Shift folder in a temporary directory. The
   existing `ImportCommand` pipeline then runs **unchanged** on each produced folder. The
   decryption program and the importer remain two independent programs that exchange files.
2. **Why the boundary matters legally.** GPL v3 obligations attach to *Onyx and its
   distribution*, not to an unrelated program that invokes it as a separate process and shares no
   address space, no data structures, and no code. We do not distribute Onyx — not in the repo,
   not in the container image — we document how to install it, and the user runs it. Porting
   Onyx's Python/Haskell decryption implementation (or transliterating its algorithms and tables)
   into our source would instead make the importer a **derivative work** of Onyx, which would put
   GPL v3 obligations on the importer itself. That is a one-way door we decline to walk through.
3. **Keys and circumvention logic stay at arm's length.** No key material, RAP handling, STFS
   signature logic, EDAT crypto, or console-format knowledge enters this repository. The importer
   knows only *"this file looks like an encrypted container; ask Onyx to convert it"*. The user
   supplies both the content and the tool; the project supplies orchestration. This is also why the
   pre-stage must degrade cleanly when Onyx is absent rather than shipping a fallback decryptor.
4. **Onyx is a user-installed runtime dependency**, like `ffmpeg`: resolve `--onyx <path>`, then
   `KARAOKAY_ONYX_PATH`, then `PATH`; if none resolves, fail fast with an actionable message
   modelled on the existing `FfmpegGuard` pattern. The single executable is enough — Magma/Wine is
   only needed to *build* Rock Band packages, never to decrypt or export them.
5. **CLI drift is contained to one class.** The Onyx manual states that command-line usage "is not
   documented here and is subject to change". Every invocation detail therefore lives in exactly
   one wrapper (`OnyxDlcExtractor`), and an implementation spike pins the real arguments on the
   target platform before the wrapper's argument builder is frozen. A future Onyx CLI change is a
   one-file fix, not a subsystem rewrite.
6. **Detection is content first; extensions only as a documented hint.** Real DLC arrives
   extensionless — both files in `media/rb-ingest` begin with the 4-byte STFS magic `CON `
   (`43 4F 4E 20`, verified with `xxd -l 16`) and have no filename extension at all — so the
   pre-stage sniffs headers (`CON `, `LIVE`, `PIRS` for STFS; the PS3 package and EDAT magics; the
   bare MOGG marker; ARK header files) and classifies the input as *plain song folder* (current
   path, untouched), *encrypted container(s)* (needs Onyx), or *unknown*. Filename extensions are
   used only where they are a documented hint (`.pkg`, `.edat`) and always paired with a magic
   check; an extension alone never spawns Onyx. A folder that already satisfies the existing
   importer contract always wins, so the pre-stage can never regress a working import.

The full component design — `DlcContainerDetector`, `OnyxLocator`, `OnyxDlcExtractor`, the
`ImportCommand`/`ScanCommand` integration, the error-message catalogue, milestones, and the test
plan — is in [docs/specs/rb-dlc-onyx-prestage.md](../specs/rb-dlc-onyx-prestage.md).

## Consequences

**Positive**

- **Licensing stays simple by construction.** The importer is not a derivative work of Onyx, and
  the repository ships zero decryption code and zero key material. The boundary is architectural,
  not a matter of vigilance.
- **The existing pipeline is reused verbatim.** Onyx's Phase Shift export is the importer's native
  input shape (`song.ini` + MIDI + per-stem audio), so `MidiLyricsParser`, `SongIniReader`,
  `StemMixer`, `SongId` resolution, and the three-file output all keep working — including loudness
  normalization to −16 LUFS and the temp-then-move write discipline. Today the importer accepts only
  `.ogg`/`.oga` stems (`ImportCommand.IsStemFile`); `.opus` becomes accepted alongside them as a
  small, tested addition (see spec §4.7) so Onyx's audio-format preference cannot break the
  pre-stage.
- **C2 remains intact.** No commercial audio or charts enter Git; extraction happens on the user's
  machine into `media/` (already gitignored) and only the encrypted source files' *existence* is
  ever tested, never their bytes.
- **The "confusing failure" becomes a diagnosis.** An encrypted folder is reported as encrypted,
  with the exact command or missing dependency named, instead of `song.ini not found`.
- **`scan` gets more honest**, which also closes the Slice 1 review complaint that `scan` reports
  folders as importable when `import` would refuse them (review item 6, and the duplicated stem
  helpers in item 14).

**Negative / Accepted risks**

- **Onyx's CLI is explicitly unstable and undocumented.** Accepted: mitigated by the wrapper
  isolation plus a pinned spike, and by version-recording in the docs rather than a hard gate.
- **Onyx is one more external tool users must install.** Accepted: it is optional and only needed
  for encrypted input; the error path is the feature, and it names the install URL.
- **PS3 input has a prerequisite we cannot satisfy for the user** — RAP licence files are not
  shipped and must be placed at `onyx-resources/raps/<CONTENTID>.RAP`. The error path must say so.
- **We inherit Onyx's format coverage**, including its gaps. Formats Onyx cannot read are out of
  scope here rather than reimplemented.
- **Subprocess orchestration has its own failure modes** (timeouts, partial multi-song packs,
  stdout/stderr volume). Accepted: bounded timeout, stderr tail capture, and per-song continuation
  are specified and tested.
- **Not legal advice.** The boundary keeps *our* distribution clean; whether a given user may
  decrypt a given package is between that user and their local law. The docs should say so once,
  plainly, and not pretend otherwise.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| **Native C# port** of Onyx's decryption (STFS/CON/LIVE, MOGG, EDAT, RAP) | Makes the importer a GPL v3 derivative work; embeds key material and circumvention logic in our source; commits us to tracking every console container revision forever. Large scope for zero product differentiation. |
| **Link/vendor Onyx** (bundle the AppImage or Windows build in the repo or container image) | Redistribution triggers GPL v3 obligations for the bundled binary and bloats the repo/image; C2's spirit is that media and media tooling stay on the user's machine. We document the install instead. |
| **C3 CON Tools** | Windows GUI-only applications with no supported CLI; cannot be automated or driven from a .NET CLI, and not cross-platform. |
| **Shell out to a thin Python wrapper around Onyx** | Adds a second runtime (Python) for no benefit; the `onyx` executable is already CLI-capable on all three platforms. |
| **Do nothing; document a manual Onyx step in the README** | Cheapest, but leaves the confusing `song.ini not found` failure and no `scan` classification; it also leaves `import <dir>` unable to do the one thing the user asked for. Kept as the Increment 1 stopping point, not as the end state. |
| **Add a watch folder / background ingest service now** | Different feature (lifecycle, debouncing, partial-copy detection, service hosting); the classification and extraction primitives are prerequisites either way, so it is deferred. |

## Validation plan

- Unit: `DlcContainerDetectorTests` (hosted in `Karaokay.IntegrationTests`, the only assembly with
  `InternalsVisibleTo` into the importer) over committed synthetic header fixtures (tens of bytes
  each: `CON `, `LIVE`, `PIRS`, PS3 package, EDAT, bare MOGG, ARK) plus the ignore rules
  (`…:Zone.Identifier`, `._*`, `.DS_Store`). No copyrighted content is committed, ever.
- Unit: `OnyxLocator` precedence tests (`--onyx` > `KARAOKAY_ONYX_PATH` > `PATH`) and the exact
  missing-Onyx message; `OnyxDlcExtractor` argument/exit-code/timeout interpretation tests against
  a deterministic fake `onyx` executable, so the Onyx-unstable surface is covered without Onyx.
- Integration: full decrypt → import round trip guarded by an `OnyxGuard` that **skips** when Onyx
  is absent (unlike `FfmpegGuard`, which fails, because Onyx is an optional tool). The test may
  point at `media/rb-ingest/` when those files exist locally but must never depend on them.
- Regression: the existing importer integration tests (`ImporterEndToEndTests`,
  `ImporterFailureTests`, `ImporterSongIdTests`, `StemSelectionTests`) must stay green, proving a
  plain extracted folder takes the pre-Onyx path byte-for-byte.
- Documentation: the onyxite.org manual documents 20240120, but that is a *doc* version, not a
  tested one — the release actually exercised is pinned by the Increment-2 spike and recorded in
  `docs/specs/rb-dlc-onyx-prestage.md` and in the importer usage text; `ARCHITECTURE.md` and
  `PROJECT_STATE.md` gain their importer updates **at implementation time** (listed as
  implementation deliverables in the spec, not edited by this ADR).
