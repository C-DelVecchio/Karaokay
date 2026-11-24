# ADR-006: The renderer owns glyph-measured line wrapping; the importer stores only explicit breaks

- **Status:** Accepted
- **Date:** 2026-11-24
- **Deciders:** Architect
- **Context level:** Data contract / Rendering boundary
- **Related:** [ADR-003](003-dual-mode-stage-web-vs-native.md) (two renderers, one decision-level contract), [ADR-005](005-slice-1-blueprint-reconciliations.md) (the other Slice 1 reconciliation), [ARCHITECTURE.md §4.1](../ARCHITECTURE.md#41-lyricsjson--canonical-karaoke-timeline) (line-break rule), [ARCHITECTURE.md §6.1](../ARCHITECTURE.md#61-sweep-fill-karaoke-rendering-math-shared-by-canvas--skiasharp) (render math)

## Context

`ARCHITECTURE.md` §4.1 originally described line breaks as follows:

> A line wraps when cumulative syllable width at render font exceeds `MaxLineWidthEm` (stored
> per-line by importer using measured glyph widths at a reference font; renderer may re-flow but
> honors `lineBreakAfter`).

That wording gives the **importer** responsibility for glyph measurement — but the importer is a
headless .NET CLI built on DryWetMIDI and FFmpeg. It has **no font, no text-shaping stack, and no
GPU context**. A "reference font" measured by the importer is not the font either stage actually
renders with:

- the **web stage** renders with Canvas `measureText` and the browser's font stack;
- the **Android TV stage** renders with SkiaSharp and the device's font stack.

Any importer-side measurement would therefore be an approximation that each renderer either
trusts (and wraps badly) or ignores (and the stored breaks become dead data). Worse, the two
renderers would still need their own re-flow logic, so the measurement would be **duplicated in
three places** — exactly the kind of drift ADR-003's decision-level contract exists to prevent.

Slice 1 already settled the practical question: the committed golden fixtures encode **only**
`[line_end]`-derived breaks (`lineBreakAfter` on the syllable that ends the line). Charts without
`[line_end]` markers produce one line per phrase.

## Decision

**Glyph-measured line wrapping is owned by the renderer, not the importer.**

1. **Importer stores explicit breaks only.** `MidiLyricsParser` sets `lineBreakAfter: true` on the
   last syllable before each `[line_end]` text event. It performs no glyph measurement and stores
   no computed line widths.
2. **No markers → one line.** A chart without `[line_end]` markers yields a **single line per
   phrase**; the renderer re-flows it at render time.
3. **Renderer re-flows.** At render time the stage wraps a line when its own measured cumulative
   syllable width exceeds `MaxLineWidthEm` (a renderer layout parameter), and **always honors**
   importer-provided `lineBreakAfter` breaks. Forced breaks and measured breaks are both applied
   by the surface that knows its font.

`ARCHITECTURE.md` §4.1 is amended accordingly; the contract wording now matches this decision.

## Consequences

**Positive**

- Font metrics live where the font lives: Canvas metrics in the browser, Skia metrics on Android.
  Neither surface has to trust a measurement made by a tool that has never opened a font.
- The importer stays small and deterministic — DryWetMIDI + text events, no font dependencies in
  the CLI or its CI job.
- Re-importing a library is **not** required when a stage changes its font, size, or layout width;
  only rendering changes.
- The committed golden fixtures remain valid and unchanged: they assert `[line_end]`-only breaks,
  which is exactly what the importer is specified to produce.

**Negative / Accepted risks**

- The two renderers may wrap a long unmarked line at slightly different points because Canvas and
  Skia metrics differ. Accepted: ADR-003 pins *decisions* (fill values, active line, dot states),
  not pixel-identical glyph positions, and `lineBreakAfter` exists precisely to force parity where
  it matters.
- A chart with no `[line_end]` markers relies on renderer re-flow to look reasonable. Accepted:
  that is the only interpretation the importer can honestly produce without font context, and
  authored Rock Band charts normally carry line-end markers.
- `MaxLineWidthEm` becomes a renderer-side constant rather than data in `lyrics.json`. Accepted:
  it is a layout policy of the surface, not a property of the song.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Importer measures with a bundled reference font (the original wording) | Adds a font/shaping dependency to a headless CLI, and the measurement would not match either renderer's actual font — two wrong measurements instead of one honest contract. |
| Importer measures and renderers trust the widths | Renderers would be unable to honor their own typography, and any font change would require re-importing every song in the library. |
| A shared layout service/package used by both renderers | Still needs per-surface font metrics for the final measurement; the abstraction would be a thin wrapper around `measureText`/Skia at best, and a new cross-platform dependency at worst. |
| No wrapping at all (single long line) | Unreadable at TV/canvas width for any lyric line longer than a few words; the stage's sweep fill would run off-screen. |

## Validation plan

- `MidiLyricsParser` unit and golden tests assert `lineBreakAfter` is set exactly at `[line_end]`
  events and that marker-less fixtures produce one line per phrase.
- Committed golden fixtures and the web demo copy remain byte-identical (Slice 1 determinism and
  `TestPaths` drift checks).
- Slice 2's Canvas renderer tests and Slice 8's Skia renderer tests cover re-flow against
  `MaxLineWidthEm` and precedence of forced breaks over measured breaks.
- `docs/ARCHITECTURE.md` §4.1 states renderer-owned re-flow and importer-stored explicit breaks;
  the v1.2 history cites this ADR.
