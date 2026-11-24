import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import type { LyricsDocument } from '../lyrics';
import {
  buildGoldenSampleTimes,
  computeDecisionsAt,
  flattenLines,
} from './decisions';

const SONG_IDS = [
  'synth-twinkle-variations',
  'synth-spoken-telegram',
  'synth-countdown-canyon',
] as const;

interface GoldenSample {
  tMs: number;
  activeLineIndex: number;
  syllableFills: number[];
  dotState: {
    gapIndex: number;
    countdownBeats: number;
    beatsRemaining: number;
    dotFills: number[];
  } | null;
}

interface GoldenFixture {
  formatVersion: number;
  songId: string;
  sampleCount: number;
  samples: GoldenSample[];
}

function loadLyrics(songId: string): LyricsDocument {
  return JSON.parse(
    readFileSync(new URL(`../../../../public/demo-tracks/${songId}/lyrics.json`, import.meta.url), 'utf8'),
  ) as LyricsDocument;
}

function loadFixture(songId: string): GoldenFixture {
  return JSON.parse(
    readFileSync(new URL(`../../../../fixtures/golden/stage-clock/${songId}.json`, import.meta.url), 'utf8'),
  ) as GoldenFixture;
}

describe('fixture semantics: talky flat fill', () => {
  it('pins every spoken-telegram talky syllable to exactly 0 or 1', () => {
    const lyrics = loadLyrics('synth-spoken-telegram');
    const fixture = loadFixture('synth-spoken-telegram');
    const syllables = flattenLines(lyrics).flatMap((line) => line.syllables);
    const talkyIndexes = syllables
      .filter((syllable) => syllable.talky)
      .map((syllable) => syllable.flatIndex);

    expect(talkyIndexes.length).toBeGreaterThan(0);
    const seen = new Set<number>();

    for (const sample of fixture.samples) {
      for (const index of talkyIndexes) {
        const fill = sample.syllableFills[index];
        expect(fill === 0 || fill === 1).toBe(true);
        expect(fill).toBe(sample.tMs >= syllables[index].startMs ? 1 : 0);
        seen.add(fill);
      }
    }

    expect([...seen].sort()).toEqual([0, 1]);
  });
});

describe('fixture semantics: active line progression', () => {
  for (const songId of SONG_IDS) {
    it(`walks every line, including upcoming-line gaps, for ${songId}`, () => {
      const lyrics = loadLyrics(songId);
      const fixture = loadFixture(songId);
      const lines = flattenLines(lyrics);

      expect(fixture.samples[0].activeLineIndex).toBe(0);
      expect(fixture.samples[fixture.samples.length - 1].activeLineIndex).toBe(lines.length - 1);

      const seenLines = new Set(fixture.samples.map((sample) => sample.activeLineIndex));
      expect([...seenLines].sort((left, right) => left - right))
        .toEqual(lines.map((line) => line.lineIndex));

      const leadIn = fixture.samples[0];
      for (const syllable of lines[0].syllables) {
        expect(leadIn.syllableFills[syllable.flatIndex]).toBe(0);
      }

      const upcomingGapSamples = fixture.samples.filter((sample) =>
        sample.activeLineIndex > 0
        && sample.tMs < lines[sample.activeLineIndex].firstStartMs);
      expect(upcomingGapSamples.length).toBeGreaterThan(0);

      for (const sample of upcomingGapSamples) {
        for (const syllable of lines[sample.activeLineIndex].syllables) {
          expect(sample.syllableFills[syllable.flatIndex]).toBe(0);
        }
      }

      for (let index = 1; index < fixture.samples.length; index++) {
        const previous = fixture.samples[index - 1];
        const current = fixture.samples[index];
        for (const syllable of lines[previous.activeLineIndex].syllables) {
          expect(current.syllableFills[syllable.flatIndex])
            .toBeGreaterThanOrEqual(previous.syllableFills[syllable.flatIndex]);
        }
      }
    });
  }
});

describe('fixture semantics: countdown dot states', () => {
  const gapExpectations: Array<[songId: string, gapCount: number]> = [
    ['synth-countdown-canyon', 3],
    ['synth-spoken-telegram', 6],
  ];

  for (const [songId, gapCount] of gapExpectations) {
    it(`decrements beats and fills dots right-to-left across all ${gapCount} gaps of ${songId}`, () => {
      const fixture = loadFixture(songId);
      const byGap = new Map<number, GoldenSample[]>();

      for (const sample of fixture.samples) {
        if (!sample.dotState) {
          continue;
        }

        const group = byGap.get(sample.dotState.gapIndex) ?? [];
        group.push(sample);
        byGap.set(sample.dotState.gapIndex, group);
      }

      expect([...byGap.keys()].sort((left, right) => left - right))
        .toEqual(Array.from({ length: gapCount }, (_, index) => index));

      for (const samples of byGap.values()) {
        const first = samples[0].dotState;
        expect(first).not.toBeNull();
        if (!first) {
          continue;
        }

        expect(samples[0].dotState?.beatsRemaining).toBe(first.countdownBeats);

        for (let index = 0; index < samples.length; index++) {
          const state = samples[index].dotState;
          expect(state).not.toBeNull();
          if (!state) {
            continue;
          }

          expect(state.countdownBeats).toBe(first.countdownBeats);
          expect(state.beatsRemaining).toBeGreaterThanOrEqual(1);
          expect(state.beatsRemaining).toBeLessThanOrEqual(first.countdownBeats);
          expect(state.dotFills).toHaveLength(first.countdownBeats);

          for (let dot = 0; dot < state.dotFills.length; dot++) {
            expect(state.dotFills[dot]).toBeGreaterThanOrEqual(0);
            expect(state.dotFills[dot]).toBeLessThanOrEqual(1);
            if (dot > 0) {
              expect(state.dotFills[dot]).toBeGreaterThanOrEqual(state.dotFills[dot - 1]);
            }
            if (index > 0) {
              expect(state.dotFills[dot])
                .toBeGreaterThanOrEqual(samples[index - 1].dotState?.dotFills[dot] ?? 0);
            }
          }

          if (index > 0) {
            const previousBeats = samples[index - 1].dotState?.beatsRemaining ?? 0;
            expect(previousBeats - state.beatsRemaining).toBeGreaterThanOrEqual(0);
            expect(previousBeats - state.beatsRemaining).toBeLessThanOrEqual(1);
          }
        }
      }
    });
  }
});

describe('stage clock golden fixtures', () => {
  for (const songId of SONG_IDS) {
    it(`recomputes every pinned decision for ${songId}`, () => {
      const lyrics = loadLyrics(songId);
      const fixture = loadFixture(songId);
      const syllableCount = flattenLines(lyrics)
        .reduce((total, line) => total + line.syllables.length, 0);

      expect(fixture.formatVersion).toBe(1);
      expect(fixture.songId).toBe(songId);
      expect(fixture.sampleCount).toBe(100);
      expect(fixture.samples).toHaveLength(100);
      expect(fixture.samples.map((sample) => sample.tMs)).toEqual(buildGoldenSampleTimes(lyrics));

      for (let index = 0; index < fixture.samples.length; index++) {
        const sample = fixture.samples[index];
        expect(Number.isInteger(sample.tMs)).toBe(true);
        if (index > 0) {
          expect(sample.tMs).toBeGreaterThan(fixture.samples[index - 1].tMs);
        }

        const actual = computeDecisionsAt(lyrics, sample.tMs);
        expect(sample.syllableFills).toHaveLength(syllableCount);
        expect(actual.activeLineIndex).toBe(sample.activeLineIndex);

        for (let syllableIndex = 0; syllableIndex < sample.syllableFills.length; syllableIndex++) {
          expect(actual.syllableFills[syllableIndex]).toBe(sample.syllableFills[syllableIndex]);
        }

        expect(actual.dotState).toEqual(sample.dotState);
      }
    });
  }
});
