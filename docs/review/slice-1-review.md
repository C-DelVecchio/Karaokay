# Slice 1 Review — Core Contracts & Importer Tooling

- **Date:** 2026-11-24
- **Scope:** Slice 1 (`lyrics.json` contract, MIDI parser, synthetic demo generator, importer CLI,
  architecture rules), reviewed against [ARCHITECTURE.md](../ARCHITECTURE.md) v1.1 and the
  [Slice 1 walkthrough](../learning/slice-1-walkthrough.md)
- **Follow-up:** majors addressed in the uncommitted follow-up code pass (working tree); process
  items recorded in [ADR-005](../adr/005-slice-1-blueprint-reconciliations.md) and
  [ADR-006](../adr/006-line-wrapping-ownership.md); blueprint bumped to v1.2.

## a) Verdicts

**Architecture review — READY FOR SLICE 2.** No structural blockers. Layering is clean
(Domain → Application → Infrastructure, Contracts free of infrastructure dependencies), the
`lyrics.json` contract matches §4.1, the parser/generator/importer live where §3 and §7 place
them, and demo assets resolve under the expected `public/demo-tracks` path. The deviations found
are documented and acceptable (see minor backlog #19).

**Correctness review — NOT READY until the majors below are fixed.** The defects are in the tools
Slice 2 depends on and fall into two classes: **silent corruption** (a bake that leaves a truncated
file `verify` accepts; two non-Latin titles colliding on the same song ID; an unmatched note-off
shifting another note's `endMs`) and **crash-on-bad-input** (malformed MIDI aborting the importer;
a missing `ffprobe` aborting an import instead of degrading). Both classes are unacceptable in a
pipeline the next slice builds on.

**Combined resolution — fix majors + record process items before Slice 2.** The majors have been
addressed in the follow-up code pass in the working tree (section b); the process items are
resolved by ADR-005/ADR-006 (section c). Slice 1 is ready to hand to Slice 2 once those changes
land and the full test suite is green.

## b) Majors — addressed in the follow-up code pass (working tree)

1. **CI lacked the ADR-004 binary-media >100 KB guard.** ADR-004's validation plan promises CI
   fails on any committed binary media over 100 KB outside `public/demo-tracks`, but the workflow
   never implemented it — the constraint was enforced by vigilance alone.
   `.github/workflows/ci.yml` now runs the guard. *Addressed.*

2. **Dead `Win32Exception` catch made a missing `ffprobe` abort imports instead of returning
   `null`.** The catch sat in the wrong scope, so process-start failure escaped
   `TryProbeDurationMsAsync`/`RunProcessAsync` in
   `src/core/Karaokay.Infrastructure/MediaIngest/StemMixer.cs`. This contradicted walkthrough §8.7
   ("missing ffprobe or unparseable output returns `null` instead of throwing") and turned an
   optional probe into a hard dependency. *Addressed: process-start failure is caught and the
   parser-derived duration stands.*

3. **Malformed MIDI crashed the importer with a stack trace.** DryWetMIDI exceptions were uncaught
   in `src/tools/Karaokay.Importer/Commands/ImportCommand.cs`, so a corrupt or truncated `.mid`
   produced an unhandled exception instead of a tool error. *Addressed: parse failures are caught
   and reported as a failed import with a non-zero exit code.*

4. **Bake outputs were written non-atomically.** A failed bake could leave a truncated `.opus`
   file that `verify` accepted, because `verify` only checks existence/non-emptiness.
   `ImportCommand.cs` / `StemMixer.BakeAsync` now stage output and publish it only after the bake
   succeeds. *Addressed.*

5. **ASCII-only `Slugify` collapsed non-Latin titles to `"song"`, causing silent overwrite
   collisions.** `Slugify`/`ResolveSongId` in `ImportCommand.cs` mapped every non-ASCII title to
   the same fallback ID, so importing two such songs overwrote the first. *Addressed: distinct
   non-Latin titles now resolve to distinct song IDs instead of silently colliding.*

6. **Committed demo `.opus` files were only existence-checked, never content-checked.**
   `src/tests/Karaokay.UnitTests/SyntheticGenDeterminismTests.cs` asserted the files existed and
   were non-empty, so a zero-filled or truncated artifact would pass. *Addressed: the test now
   checks the committed audio's content rather than its mere existence.* The originally requested
   −17…−15 LUFS band was unattainable without regenerating the committed demo assets: the synthetic
   stems are baked at fixed gain and never pass `loudnorm`, so they measure about −14.5 LUFS
   (instrumental) and −11.6 LUFS (vocals). The test therefore keeps the −16 LUFS band for the
   importer's bake pipeline only and asserts −17…−13 / −14…−9 LUFS content bands for these assets,
   which still reject silence and corruption while leaving the fixtures byte-identical.

7. **Unmatched note-off closed a different still-open note of the same pitch.**
   `MidiLyricsParser.cs` paired note-offs by pitch alone, so an unmatched off could close a
   different open note and silently shift its `endMs` (and therefore sweep timing). *Addressed:
   note-offs close the specific open note instance they belong to.*

## c) Process items — resolved by ADRs

- **ADR-005 — Slice 1 blueprint reconciliations.** Records the three places where the
  implementation corrected `ARCHITECTURE.md`: room-code alphabet (23 letters / 279,841 codes,
  commit `3f998e1`), reflection-based architecture rules with positive controls instead of
  NetArchTest, and code-level `LyricsJson.Validate` instead of a separate JSON Schema. The
  corrected values are now authoritative, and future blueprint changes must be recorded via an ADR
  or a version bump.
- **ADR-006 — line-wrapping ownership.** Glyph-measured wrapping belongs to the renderer, not the
  importer. The importer stores only `[line_end]`-derived breaks; charts without markers produce
  one line per phrase and the renderer re-flows at render time. §4.1 of the blueprint is amended to
  match.
- **Blueprint version bump.** `ARCHITECTURE.md` is now v1.2 (2026-11-24), with both ADRs cited in
  the version history and companion list.

## d) Minor / nit backlog

Ordered by rough priority; each item includes the relevant file and a one-line suggested fix.

| # | Finding | File | Suggested fix |
|---:|---|---|---|
| 1 | `GlobalOffsetMs` is applied unclamped; a negative `song.ini` delay yields negative `startMs`/`initialSilenceMs` that validation accepts. | `src/core/Karaokay.Infrastructure/MediaIngest/MidiLyricsParser.cs`; `src/core/Karaokay.Contracts/LyricsJson.cs` | Clamp the offset at parse time and add non-negative-time checks to `Validate`. |
| 2 | Lead-in hardcodes `CountdownBeats = 4` / `RenderCountdown = true` regardless of length, bypassing `ComputeGapCountdown`; `BuildGapsAlwaysEmitsAFullLeadIn` never tests a short lead-in. | `MidiLyricsParser.cs:332-343` | Decide and document the policy (full count-in always), then add a short-lead-in test asserting the chosen behavior. |
| 3 | `RoomCodeGenerator` is not thread-safe (unsynchronized `HashSet.Add`) and `MaxAttempts = 100` throws spuriously near saturation. | `src/core/Karaokay.Application/RoomCodeGenerator.cs` | Document singleton-unsafe or make it per-call; scale attempts to remaining code space. |
| 4 | Golden test feeds `Title`/`Artist`/`DurationMs` back from the fixture, making those fields and duration derivation tautologically verified. | `src/tests/Karaokay.UnitTests/MidiLyricsParserGoldenTests.cs:36-42` | Hard-code expected metadata and assert duration derivation independently. |
| 5 | `MidiFileNameDiscovery` enumerates unordered; case-differing filenames resolve nondeterministically. | `MidiFileNameDiscovery.cs:44-55` | Sort candidates ordinally before `FirstOrDefault`. |
| 6 | `scan` lists vocals-only folders as importable while `import` hard-fails on a missing instrumental. | `src/tools/Karaokay.Importer/Commands/ScanCommand.cs` vs `ImportCommand.cs` | Align the importability criteria in both commands. |
| 7 | No negative-path coverage: malformed/truncated MIDI, SMPTE division, missing vocals track, missing ffmpeg, missing `song.ini`, failed bake; `TryParseArguments`/`Slugify`/`ResolveSongId` have zero test callers despite `InternalsVisibleTo` comments. | Unit/integration tests | Add the named negative-path tests and direct tests for the internal helpers (or drop the exposure claim). |
| 8 | `LyricsJson.Validate` lacks syllable/gap ordering and containment checks; `DeserializeFile` catches only `JsonException` (ACL failures escape); `VerifyCommand`'s `File.ReadAllBytes` sits outside the `IOException` handler. | `LyricsJson.cs:218-285`; `VerifyCommand.cs:36-66` | Add ordering/containment validation, broaden the catch to I/O/auth exceptions, and move the read inside the handler. |
| 9 | CI floats: `ubuntu-latest` + `dotnet-version: '10.0.x'`; no NuGet cache, no TRX/coverage artifacts. | `.github/workflows/ci.yml` | Pin runner and SDK version, enable NuGet caching, upload TRX/coverage artifacts. |
| 10 | `AudioStemBaker` shares a fixed `%TEMP%/karaokay-synthgen` scratch dir (unsafe for concurrent runs); `WritePcm` writes dual-mono rather than true stereo; a missing ffmpeg escapes `Program.cs` as an unhandled `Win32Exception`. | `src/tools/Karaokay.SyntheticGen/AudioStemBaker.cs`; `Program.cs` | Use a per-run scratch dir with cleanup, write interleaved stereo, and catch process-start failure with a clean non-zero exit. |
| 11 | `ArchitectureRules` runs `Assembly.LoadFrom` in static field initializers — one missing DLL turns all 11 tests into `TypeInitializationException` with no message. | `src/tests/Karaokay.ArchitectureTests/ArchitectureRules.cs:36-40` | Load lazily per test with an explicit failure message naming the missing assembly. |
| 12 | `ArchitectureRules`: `ApplicationForbidden` forbids `Karaokay.Contracts` (not a blueprint rule; Slice 3 services may need contracts) and omits `Melanchall.DryWetMidi`/`CliWrap`. | `ArchitectureRules.cs` | Revisit the Application rule before Slice 3 and add the missing package exclusions where applicable. |
| 13 | Sanity tests are `Assert.True(true)` probes that never assert anything. | `SanityTests.cs` in all three test projects | Rename/comment them as discovery probes, or assert an environment fact so failure is meaningful. |
| 14 | Duplicated helpers: `IsAudioStem`/`IsStemFile`/`HasStemPrefix`/`JoinNames`/`FfmpegGuard`/`TryDeleteDirectory` are repeated across `StemMixer`, `ImportCommand`, `ScanCommand`, and test projects. | Multiple | Extract a shared internal media-ingest helper and reuse it everywhere. |
| 15 | `SyntheticSongs` determinism relies on the `new Random(int)` compatibility implementation (not contractually stable across .NET majors); `score.Chords` would index `-1` if the chord list were empty. | `src/tools/Karaokay.SyntheticGen/SyntheticSongs.cs:46-47` | Pin a note (or use an explicit PRNG) for determinism, and guard the empty-chord case. |
| 16 | `Directory.Build.props` sets a global `net10.0` `TargetFramework`, conflicting with the planned `net10.0-android` client. | `Directory.Build.props` | Plan the per-project TFM override now, before Slice 7. |
| 17 | `verify` takes a directory where blueprint §3 specifies `verify <songId>`. | `src/tools/Karaokay.Importer/Commands/ImporterCli.cs` | Either amend blueprint §3 or change the command to take a song ID. |
| 18 | `AppHost` is a plain console placeholder, `ServiceDefaults` is an empty shell the API never calls, and `src/clients/web` has no `package.json`. | `src/apps/Karaokay.AppHost`; `src/apps/Karaokay.ServiceDefaults`; `src/clients/web` | Track as slice entry criteria: AppHost/ServiceDefaults at Slice 3, web scaffold at Slice 2, README before Slice 6. |
| 19 | Accepted deviations: `RoomCodeGenerator` lives in Application while §3/§7 place it under `apps/Karaokay.Api/Features`; unit tests read committed web-client assets via `TestPaths` (coupling to client layout). | `src/core/Karaokay.Application/RoomCodeGenerator.cs`; `TestPaths.cs` | Keep as documented deviations for now; revisit if Application layering or client layout changes. |
