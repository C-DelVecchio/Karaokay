# ADR-003: Dual-mode stage — Web (Web Audio + Canvas) and native Android (Oboe + SkiaSharp) share one renderer contract

- **Status:** Accepted
- **Date:** 2026-11-23
- **Deciders:** Architect
- **Context level:** Client architecture / Rendering
- **Related:** [ADR-001](001-single-replica-signalr-azure.md) (browser demo is the recruiter entry point), [ADR-002](002-android-oboe-hdmi-usb-drift.md) (the native audio stack), [ARCHITECTURE.md §6.1](../ARCHITECTURE.md#61-sweep-fill-karaoke-rendering-math-shared-by-canvas--skiasharp)

## Context

The platform needs **two stage clients** with very different jobs:

1. **Browser Stage** (React + TS): the recruiter's zero-install entry point. It must play the
   full dual-mode experience — Web Audio API (`AudioContext` + separate `GainNode` buses for
   `instrumental.opus` and `vocals.opus`) plus an HTML5 `<canvas>` sweep-fill renderer, and
   `<video>` playback for YouTube tracks — so the live Azure demo exercises the complete
   stage + phone-queue loop.
2. **Android TV Stage** (pure .NET for Android, `net10.0-android`, + C++): the flagship party
   experience — native `Activity`/views with SkiaSharp (`SKCanvasView`) rendering, an ExoPlayer
   video surface, and the Oboe real-time audio engine with live USB mic mixing (ADR-002).

The risk with two stage clients is **behavioral fork**: the syllable sweep-fill and
countdown-dot math is subtle (syllable interpolation, `connectsToNext` word seams, phrase gap
beats). If browser and TV drift apart, the demo and the flagship contradict each other —
the worst possible portfolio signal.

The other obvious options were: ship **only** a web stage (lose the native/C++ showcase), or
make the Android app **just a WebView** of the web stage (unusable audio latency for live mic
mixing, and it hollows out the C++ portfolio piece).

## Decision

Implement **two stage clients bound by one normative contract**, rather than one codebase or
two independent implementations:

1. **`lyrics.json` + the render equations in `ARCHITECTURE.md` §6.1 are the contract.** The
   fill math (`fill_i = clamp01((T − start_i) / (end_i − start_i))` with eased leading edge,
   word-granular sweep across `connectsToNext`, clip-rect partial fill inside the active
   syllable, and countdown dot beat math) is specified once.
2. **Golden fixtures are shared, not code.** A set of synthetic clock series (100 sampled
   timestamps per song) feeds:
   - `CanvasLyricsRenderer.ts` (Vitest, browser) and
   - `KaraokeRenderer.cs` (xunit with SkiaSharp, Android/CI),
   and both must produce **identical fill sequences, active-line indices, and dot states**.
   Byte-parity of *decisions*, not pixels — rendering cosmetics may differ.
3. **Media-clock ground truth is shared in shape**: browser uses
   `AudioContext.currentTime`/element `currentTime`; Android uses the Oboe frame counter
   (`engine_get_position_ms()`). Both calibrate once to the server wall-clock anchor
   (`ARCHITECTURE.md` §5.3) and never re-slave the audio clock.
4. **Feature capability is progressive enhancement, not divergence**: both render the sweep
   fill, phrase windows, and countdown dots identically, and the guide-vocal volume bus and
   YouTube video surface behave the same across stages. Audio backends differ (Web Audio vs
   Oboe) but both are bound by the same two-layer clock strategy (`ARCHITECTURE.md` §5.3).
   Real-time pitch detection was removed from scope (v1.1) precisely so the shared contract
   stays small enough to implement twice without divergence.

## Consequences

**Positive**

- The browser demo is always truthful about the product: what a recruiter sees in Chrome is
  what the TV does, and CI proves it continuously.
- Renderer regressions are caught **cross-platform** by the same fixtures — a change that
  improves one surface but breaks parity fails CI.
- Each implementation uses its platform's best tools (Canvas/Web Audio on web; SkiaSharp/Oboe
  on Android) with no lowest-common-denominator abstraction layer.
- The web stage doubles as the CI-deployable **reference implementation**, which is why it is
  built in Slice 2 — before rooms, phones, or any native code.

**Negative / Accepted risks**

- Two codebases must be kept behaviorally in sync — mitigated by shared fixtures (the sync is
  *tested*, not hoped for) and by the compact, fully-specified math (§6.1 is deliberately
  precise enough to implement twice unambiguously).
- SkiaSharp and Canvas have different text-measurement models; layout may differ slightly at
  the pixel level — accepted, since the contract pins *decisions* (fill values, active line,
  dot states), not glyph positions. Importer-side `lineBreakAfter` hints keep line wrapping
  stable across platforms.
- Maintenance cost is real but bounded: the renderer is a few hundred lines per platform.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| WebView-only Android stage | Live-mic mixing needs 10–40 ms round trip (ADR-002); WebView audio paths cannot deliver it. Also collapses the C++ portfolio showcase into a thin wrapper. |
| Web stage only (drop native) | Loses the flagship native piece — explicitly against the project's portfolio goals. |
| Shared cross-platform renderer code (e.g., Blazor, or a C++ renderer on both) | Web audio latency and tooling friction on the browser side; a C++ renderer in a browser is a maintenance dead end; the abstraction would constrain both platforms. |
| Single "canonical" implementation + screenshot-diff port | Pixel diffing across Skia/Canvas is flaky in CI (font rasterization, GPU differences); decision-level golden fixtures are deterministic. |

## Validation plan

- Golden fixture suite (`fixtures/golden/*.json` clock series) runs against both renderers in
  their respective CI jobs (Vitest + xunit) and must emit identical decision sequences.
- Slice 8's final Playwright run plus the on-device TV checklist verify end-to-end parity
  (transitions Countdown→Playing→SongEnded, guide-vocal slider response, corner QR overlay).
