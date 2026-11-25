# Slice 2 Review — Browser Stage & Media Serving

- **Date:** 2026-11-24
- **Scope:** Slice 2 diff in commit `188827e` (API media library/range endpoints, Web Audio engine,
  StageShell state machine, canvas sweep-fill renderer, shared golden clock fixtures, Vitest and
  Playwright suites, web CI lane), reviewed against [ARCHITECTURE.md](../ARCHITECTURE.md) v1.2 and
  [ADR-003](../adr/003-dual-mode-stage-web-vs-native.md)
- **Follow-up:** non-blocking nits recorded in section c; no Slice 2 code changes accompany this
  document.

## a) Verdicts

**Pass 1 — static audit — SHIP-WITH-NITS, conditional on execution evidence.** No structural
blockers: the API keeps media paths behind the scanned-id whitelist, the Web Audio engine honours
the gesture gate and audio-clock ground truth, the renderer consumes pinned decision values, and
the web/API layering remains inside the documented boundaries. Three minor defects (sticky unlock
failure, a doubled lead-in countdown, and per-request library rescans) plus two nits (count-in RAF
allocation and a duplicate CI web build) were raised; the slice was not shippable until an execution
matrix proved the fixes.

**Pass 2 — execution matrix — SHIP.** All gates green on the fixed tree:

- `dotnet build karaokay.sln` — 0 warnings, 0 errors.
- `dotnet test karaokay.sln` — 69 passed (11 architecture + 24 integration + 34 unit).
- Vitest — 40 passed across 7 files (including the golden-fixture guard and fixture semantics).
- Playwright — 7 e2e tests passed headless twice consecutively, without manual env vars.
- Golden fixtures — regeneration byte-stable; the three `stage-clock/*.json` files are unchanged.
- Slice 1 code — untouched by the Slice 2 commit.
- `docs/ARCHITECTURE.md` — diff is exactly the two ADR-005 reconciliation hunks; no version bump.

## b) Resolved during the slice

- **Sticky unlock failure.** A failed `contextFactory()`/`resume()` left `unlockPromise` cached with
  `unlocked` stuck true while the gate was already dismissed. Rollback now logs `unlock-failed`,
  clears the promise/context/buses, installs a fresh `unlockedSignal`, and keeps the gate visible
  until `unlock()` resolves — `src/clients/web/src/features/stage/WebAudioEngine.ts`,
  `StageShell.tsx`.
- **Double lead-in countdown.** The pre-roll wall-clock dots ran 0→2000 and audio then started at
  buffer position 0, re-rendering the same dots. `engine.start(leadIn.toMs)` now offsets both
  sources and the clock, so playback begins at the completed count-in — `WebAudioEngine.ts`,
  `StageShell.tsx`.
- **Per-request library re-scan.** Every media request re-enumerated the root and re-parsed every
  `lyrics.json`. A documented 1 s snapshot cache under a lock now serves bursts while still noticing
  library edits — `src/apps/Karaokay.Api/Features/MediaLibrary.cs`.
- **Count-in RAF closure.** The count-in loop allocated a `setCountdown` updater every frame; state
  is now set only when the beat count changes — `StageShell.tsx`.
- **CI duplicate web build.** CI built the web bundle twice (`npm run build`, then `test:e2e`
  rebuilding). CI now uses the no-rebuild `test:e2e:ci` lane — `src/clients/web/package.json`,
  `.github/workflows/ci.yml`.
- **Execution-evidence blocker.** The restricted reviewer session was unblocked with
  allow-list-compatible command forms (`npm --prefix …`) and an opportunistic
  `scripts/run-e2e.mjs` launcher that supplies locally extracted browser libraries on hosts missing
  system nss; it is a no-op on healthy hosts and CI.

## c) Minor / nit backlog

Ordered by rough priority; each item includes the relevant file and a one-line suggested fix.

| # | Finding | File | Suggested fix |
|---:|---|---|---|
| 1 | On unlock rollback, if `contextFactory()` succeeded but `resume()` rejected, the context is nulled without `context.close()`. A user retrying a persistently failing unlock could accumulate AudioContexts (browsers cap ~6/page). | `src/clients/web/src/features/stage/WebAudioEngine.ts:179-190` | Call `context.close()` in the rollback path when a context exists; assert in the retry unit tests that no context leaks. |
| 2 | The whole `Scan()` (directory enumeration + every `lyrics.json` parse/validate) runs while holding `_scanLock`, so a request burst on a cold cache serializes behind one scan. Correct as written; the 1 s TTL makes it rare. | `src/apps/Karaokay.Api/Features/MediaLibrary.cs:84-96` | Optional: scan outside the lock and swap the snapshot atomically, or leave documented. |
| 3 | Latent UI edge: the count-in drives the canvas with `countdownElapsed` starting at 0, but a `renderCountdown` lead-in gap with `fromMs > 0` expects absolute times `[fromMs, toMs)` — dots would not appear during the count-in. All committed demo fixtures use `fromMs = 0`, so behavior is correct today; matters once real MIDI intros (Slice 3+) produce non-zero lead-ins. | `src/clients/web/src/features/stage/StageShell.tsx:142` vs `:190` | Drive the count-in canvas at `gap.fromMs + elapsed`; add a fixture/test with a non-zero `fromMs` lead-in when Slice 3 introduces one. |
| 4 | CI .NET steps are minimal: `dotnet build` without `--no-restore` after the Restore step, and no TRX/coverage artifacts uploaded. Harmless on a free runner. | `.github/workflows/ci.yml` | Optional polish: add `--no-restore` to build/test and upload TRX/coverage artifacts if reporting is ever wanted. |

## d) Carry-forward

These nits are safe to fold into Slice 3 (rooms, hub, phones) or a later hardening pass. None
affects the Slice 2 definition of done or the shared golden-fixture contract; the generator and the
three `stage-clock/*.json` files remain the authoritative cross-platform decision surface.
