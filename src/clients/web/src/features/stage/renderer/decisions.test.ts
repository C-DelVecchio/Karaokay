import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import type { LyricsDocument } from '../lyrics';
import {
  activeLineIndexAt,
  buildGoldenSampleTimes,
  countdownAt,
  defaultPartOf,
  flattenLines,
  groupWordChains,
  syllableFillAt,
} from './decisions';

const SONG_IDS = [
  'synth-twinkle-variations',
  'synth-spoken-telegram',
  'synth-countdown-canyon',
] as const;

function loadLyrics(songId: string): LyricsDocument {
  return JSON.parse(
    readFileSync(new URL(`../../../../public/demo-tracks/${songId}/lyrics.json`, import.meta.url), 'utf8'),
  ) as LyricsDocument;
}

describe('talky syllables', () => {
  it('step from 0 to exactly 1 with no eased intermediate values', () => {
    const lyrics = loadLyrics('synth-spoken-telegram');
    const syllables = flattenLines(lyrics).flatMap((line) => line.syllables);
    const talky = syllables.filter((syllable) => syllable.talky);

    // The committed demo JSON contains 41 talky syllables (the task summary said 40).
    expect(talky).toHaveLength(41);

    for (const syllable of talky) {
      expect(syllableFillAt(syllable, syllable.startMs - 1)).toBe(0);
      expect(syllableFillAt(syllable, syllable.startMs)).toBe(1);
      expect(syllableFillAt(syllable, syllable.startMs + 1)).toBe(1);
      expect(syllableFillAt(syllable, syllable.endMs - 1)).toBe(1);
    }
  });
});

describe('countdown decisions', () => {
  it('decrements beats and fills dots from the last index backwards', () => {
    const lyrics = loadLyrics('synth-countdown-canyon');
    const part = defaultPartOf(lyrics);
    expect(part?.gaps.filter((gap) => gap.renderCountdown)).toHaveLength(3);

    expect(countdownAt(lyrics, 0)).toEqual({
      gapIndex: 0,
      countdownBeats: 4,
      beatsRemaining: 4,
      dotFills: [0, 0, 0, 0],
    });
    expect(countdownAt(lyrics, 500)).toEqual({
      gapIndex: 0,
      countdownBeats: 4,
      beatsRemaining: 3,
      dotFills: [0, 0, 0, 1],
    });
    expect(countdownAt(lyrics, 1000)).toEqual({
      gapIndex: 0,
      countdownBeats: 4,
      beatsRemaining: 2,
      dotFills: [0, 0, 1, 1],
    });
    expect(countdownAt(lyrics, 1500)).toEqual({
      gapIndex: 0,
      countdownBeats: 4,
      beatsRemaining: 1,
      dotFills: [0, 1, 1, 1],
    });

    const finalBeat = countdownAt(lyrics, 1999);
    expect(finalBeat?.beatsRemaining).toBe(1);
    expect(finalBeat?.dotFills[0]).toBeCloseTo(0.998, 12);
    expect(finalBeat?.dotFills.slice(1)).toEqual([1, 1, 1]);

    // The lead-in ends exactly at toMs; the next countdown gap starts at 14 000 ms.
    expect(countdownAt(lyrics, 2000)).toBeNull();
    expect(countdownAt(lyrics, 13999)).toBeNull();
    expect(countdownAt(lyrics, 14000)?.gapIndex).toBe(1);
  });
});

describe('word-granular sweep', () => {
  it('applies the group fill to every syllable in a connectsToNext chain', () => {
    const lyrics = loadLyrics('synth-twinkle-variations');
    const firstLine = flattenLines(lyrics)[0];
    const twin = firstLine.syllables[0];
    const kle = firstLine.syllables[1];

    expect(twin.text).toBe('Twin');
    expect(kle.text).toBe('kle');
    expect(twin.connectsToNext).toBe(true);
    expect(twin.wordGroupStartMs).toBe(2000);
    expect(twin.wordGroupEndMs).toBe(3000);
    expect(kle.wordGroupStartMs).toBe(2000);

    const tMs = 2250;
    const wordFill = 0.25; // (250 / 1000) * easeOut(1)
    expect(syllableFillAt(twin, tMs)).toBeCloseTo(wordFill, 12);
    expect(syllableFillAt(kle, tMs)).toBeCloseTo(wordFill, 12);

    // Per-syllable fill for "kle" would still be zero at 2250 ms (it starts at 2500).
    expect(tMs).toBeLessThan(kle.startMs);
  });

  it('reports the chains for the first twinkle line', () => {
    const lyrics = loadLyrics('synth-twinkle-variations');
    const firstLine = flattenLines(lyrics)[0];
    const chains = groupWordChains(firstLine.syllables);

    expect(chains[0]).toMatchObject({ startIndex: 0, endIndex: 1, startMs: 2000, endMs: 3000 });
    expect(chains[1]).toMatchObject({ startIndex: 2, endIndex: 3, startMs: 3000, endMs: 4000 });
  });
});

describe('active line', () => {
  const lyrics = loadLyrics('synth-twinkle-variations');
  const lines = flattenLines(lyrics);

  it('clamps before the first and after the last line', () => {
    expect(activeLineIndexAt(lines, -1)).toBe(0);
    expect(activeLineIndexAt(lines, 0)).toBe(0);
    expect(activeLineIndexAt(lines, 999_999)).toBe(lines.length - 1);
  });

  it('moves to the upcoming line during an inter-line gap', () => {
    expect(activeLineIndexAt(lines, 5500)).toBe(0);
    expect(activeLineIndexAt(lines, 5750)).toBe(1);
    expect(activeLineIndexAt(lines, 6000)).toBe(1);
  });
});

describe('golden sample times', () => {
  it('produces 100 unique ascending integer milliseconds for every demo', () => {
    for (const songId of SONG_IDS) {
      const lyrics = loadLyrics(songId);
      const times = buildGoldenSampleTimes(lyrics);

      expect(times).toHaveLength(100);
      expect(times[0]).toBe(0);
      expect(times[times.length - 1]).toBe(lyrics.durationMs);

      for (let index = 0; index < times.length; index++) {
        expect(Number.isInteger(times[index])).toBe(true);
        if (index > 0) {
          expect(times[index]).toBeGreaterThan(times[index - 1]);
        }
      }
    }
  });
});
