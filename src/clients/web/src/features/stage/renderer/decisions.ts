/**
 * Pure decision layer for the stage renderer (docs/ARCHITECTURE.md §6.1 / ADR-003).
 *
 * This module is intentionally DOM-free and uses only erasable TypeScript syntax so Node 24 can run
 * it directly (`node scripts/generate-golden-fixtures.ts`) without a build step. The golden clock
 * fixtures pin these decisions for the Slice 8 SkiaSharp renderer.
 */

import type { LyricsDocument, PhraseGap, VocalPart } from '../lyrics';

export interface FlatSyllable {
  text: string;
  startMs: number;
  endMs: number;
  talky: boolean;
  connectsToNext: boolean;
  lineBreakAfter: boolean;
  flatIndex: number;
  lineIndex: number;
  syllableIndexInLine: number;
  /** Word-group start/end for connected chains (length > 1); null for standalone syllables. */
  wordGroupStartMs: number | null;
  wordGroupEndMs: number | null;
}

export interface FlatLine {
  lineIndex: number;
  firstStartMs: number;
  lastEndMs: number;
  syllables: FlatSyllable[];
}

export interface WordChainSyllable {
  startMs: number;
  endMs: number;
  connectsToNext: boolean;
}

export interface WordChain {
  startIndex: number;
  endIndex: number;
  startMs: number;
  endMs: number;
}

export interface DotState {
  gapIndex: number;
  countdownBeats: number;
  beatsRemaining: number;
  dotFills: number[];
}

export interface StageDecisions {
  activeLineIndex: number;
  syllableFills: number[];
  dotState: DotState | null;
}

export const SAMPLE_COUNT = 100;

export function clamp01(value: number): number {
  return Math.min(1, Math.max(0, value));
}

/** `easeOut(x) = 1 − (1 − x)²`. */
export function easeOut(x: number): number {
  return 1 - (1 - x) * (1 - x);
}

/** The default render part: exactly one part is flagged, with the first part as fallback. */
export function defaultPartOf(lyrics: LyricsDocument): VocalPart | undefined {
  return lyrics.vocalParts.find((part) => part.isDefaultRenderPart) ?? lyrics.vocalParts[0];
}

/**
 * Maximal `connectsToNext` chains. Every syllable belongs to exactly one chain; a standalone word is
 * a chain of length one. Callers treat length > 1 as a word-group for fill/layout decisions.
 */
export function groupWordChains(syllables: readonly WordChainSyllable[]): WordChain[] {
  const chains: WordChain[] = [];
  let startIndex = 0;

  for (let index = 0; index < syllables.length; index++) {
    const isLast = index === syllables.length - 1;
    if (isLast || !syllables[index].connectsToNext) {
      chains.push({
        startIndex,
        endIndex: index,
        startMs: syllables[startIndex].startMs,
        endMs: syllables[index].endMs,
      });
      startIndex = index + 1;
    }
  }

  return chains;
}

/** Flattens the default part's lines in document order, tagging word-group metadata. */
export function flattenLines(lyrics: LyricsDocument): FlatLine[] {
  const part = defaultPartOf(lyrics);
  if (!part) {
    return [];
  }

  const lines: FlatLine[] = [];
  let flatIndex = 0;
  let lineIndex = 0;

  for (const phrase of part.phrases) {
    for (const line of phrase.lines) {
      const syllables = line.syllables;
      const chains = groupWordChains(syllables);
      const chainByIndex = new Map<number, WordChain>();
      for (const chain of chains) {
        if (chain.endIndex > chain.startIndex) {
          for (let index = chain.startIndex; index <= chain.endIndex; index++) {
            chainByIndex.set(index, chain);
          }
        }
      }

      const flatSyllables: FlatSyllable[] = [];
      for (let syllableIndex = 0; syllableIndex < syllables.length; syllableIndex++) {
        const syllable = syllables[syllableIndex];
        const chain = chainByIndex.get(syllableIndex);
        flatSyllables.push({
          text: syllable.text,
          startMs: syllable.startMs,
          endMs: syllable.endMs,
          talky: syllable.talky,
          connectsToNext: syllable.connectsToNext,
          lineBreakAfter: syllable.lineBreakAfter,
          flatIndex,
          lineIndex,
          syllableIndexInLine: syllableIndex,
          wordGroupStartMs: chain ? chain.startMs : null,
          wordGroupEndMs: chain ? chain.endMs : null,
        });
        flatIndex++;
      }

      lines.push({
        lineIndex,
        firstStartMs: flatSyllables[0]?.startMs ?? 0,
        lastEndMs: flatSyllables[flatSyllables.length - 1]?.endMs ?? 0,
        syllables: flatSyllables,
      });
      lineIndex++;
    }
  }

  return lines;
}

/**
 * Active line at `T`: before the first line start the first line is active; otherwise the last
 * line whose first syllable started. During an inter-line gap the upcoming line is active (all
 * zero fills). Never past the last line.
 */
export function activeLineIndexAt(lines: readonly FlatLine[], tMs: number): number {
  if (lines.length === 0) {
    return 0;
  }

  if (tMs < lines[0].firstStartMs) {
    return 0;
  }

  let active = 0;
  for (let index = 0; index < lines.length; index++) {
    if (lines[index].firstStartMs <= tMs) {
      active = index;
    } else {
      break;
    }
  }

  if (active < lines.length - 1 && lines[active].lastEndMs < tMs) {
    active += 1;
  }

  return active;
}

/** Word-granular fill for a connected group; the leading edge eases in over 50 ms. */
export function wordFillAt(startMs: number, endMs: number, tMs: number): number {
  const progress = clamp01((tMs - startMs) / Math.max(1, endMs - startMs));
  const ease = easeOut(Math.min(1, Math.max(0, (tMs - startMs) / 50)));
  return progress * ease;
}

/** The fixture "fill value" for one syllable, per §6.1. */
export function syllableFillAt(syllable: FlatSyllable, tMs: number): number {
  if (syllable.talky) {
    return tMs >= syllable.startMs ? 1 : 0;
  }

  if (syllable.wordGroupStartMs !== null && syllable.wordGroupEndMs !== null) {
    return wordFillAt(syllable.wordGroupStartMs, syllable.wordGroupEndMs, tMs);
  }

  const progress = clamp01((tMs - syllable.startMs) / Math.max(1, syllable.endMs - syllable.startMs));
  const ease = easeOut(Math.min(1, Math.max(0, (tMs - syllable.startMs) / 50)));
  return progress * ease;
}

export function syllableIsTalky(syllable: FlatSyllable): boolean {
  return syllable.talky;
}

/** Index into the full gaps array of the active render-countdown gap, or −1. */
export function findActiveGapIndex(gaps: readonly PhraseGap[], tMs: number): number {
  for (let index = 0; index < gaps.length; index++) {
    const gap = gaps[index];
    if (gap.renderCountdown && gap.fromMs <= tMs && tMs < gap.toMs) {
      return index;
    }
  }

  return -1;
}

/** Countdown decision at `T`; allocates dot fills (fixtures/tests only — not the render loop). */
export function countdownAt(lyrics: LyricsDocument, tMs: number): DotState | null {
  const part = defaultPartOf(lyrics);
  if (!part) {
    return null;
  }

  const gaps = part.gaps;
  const gapIndex = findActiveGapIndex(gaps, tMs);
  if (gapIndex < 0) {
    return null;
  }

  const gap = gaps[gapIndex];
  const beatsRemaining = Math.min(
    gap.countdownBeats,
    Math.max(0, Math.ceil((gap.toMs - tMs) / gap.beatMs)),
  );
  const dotFills: number[] = [];

  for (let k = 0; k < gap.countdownBeats; k++) {
    const boundary = gap.toMs - (k + 1) * gap.beatMs;
    dotFills.push(clamp01((tMs - boundary) / gap.beatMs));
  }

  return {
    gapIndex,
    countdownBeats: gap.countdownBeats,
    beatsRemaining,
    dotFills,
  };
}

/** All decisions for one instant; used by fixtures, guards, and Node tooling. */
export function computeDecisionsAt(lyrics: LyricsDocument, tMs: number): StageDecisions {
  const lines = flattenLines(lyrics);
  const syllableFills: number[] = [];

  for (const line of lines) {
    for (const syllable of line.syllables) {
      syllableFills.push(syllableFillAt(syllable, tMs));
    }
  }

  return {
    activeLineIndex: activeLineIndexAt(lines, tMs),
    syllableFills,
    dotState: countdownAt(lyrics, tMs),
  };
}

/** Every decision boundary: syllable edges, gap edges, and per-dot beat boundaries. */
export function collectCriticalTimes(lyrics: LyricsDocument): number[] {
  const times = new Set<number>();
  times.add(0);
  times.add(lyrics.durationMs);

  for (const line of flattenLines(lyrics)) {
    for (const syllable of line.syllables) {
      times.add(syllable.startMs);
      times.add(syllable.endMs);
    }
  }

  const part = defaultPartOf(lyrics);
  if (part) {
    for (const gap of part.gaps) {
      times.add(gap.fromMs);
      times.add(gap.toMs);
      if (gap.renderCountdown) {
        for (let k = 1; k <= gap.countdownBeats; k++) {
          times.add(Math.round(gap.toMs - k * gap.beatMs));
        }
      }
    }
  }

  return [...times]
    .filter((time) => time >= 0 && time <= lyrics.durationMs)
    .sort((left, right) => left - right);
}

function snapToCritical(critical: readonly number[], value: number): number {
  let best = value;
  let bestDistance = 11;

  for (const candidate of critical) {
    const distance = Math.abs(candidate - value);
    if (distance < bestDistance || (distance === bestDistance && candidate < best)) {
      best = candidate;
      bestDistance = distance;
    }
  }

  return best;
}

/**
 * Deterministic 100-point series: snap the even grid to nearby decision boundaries, dedupe, then
 * repeatedly split the largest remaining gap until exactly 100 unique integer milliseconds remain.
 */
export function buildGoldenSampleTimes(lyrics: LyricsDocument): number[] {
  const durationMs = lyrics.durationMs;
  const critical = collectCriticalTimes(lyrics);
  const snapped = new Set<number>();

  for (let index = 0; index < SAMPLE_COUNT; index++) {
    const gridTime = Math.round((index * durationMs) / (SAMPLE_COUNT - 1));
    snapped.add(snapToCritical(critical, gridTime));
  }

  const times = [...snapped].sort((left, right) => left - right);

  while (times.length < SAMPLE_COUNT) {
    let splitIndex = -1;
    let largestGap = 1;

    for (let index = 0; index < times.length - 1; index++) {
      const gap = times[index + 1] - times[index];
      if (gap > largestGap) {
        largestGap = gap;
        splitIndex = index;
      }
    }

    if (splitIndex < 0) {
      break;
    }

    times.splice(splitIndex + 1, 0, Math.round((times[splitIndex] + times[splitIndex + 1]) / 2));
  }

  if (times.length !== SAMPLE_COUNT) {
    throw new Error(`Could not build ${SAMPLE_COUNT} unique sample times for ${lyrics.songId}`);
  }

  return times;
}
