/**
 * Regenerates the shared stage-clock decision fixtures (docs/ARCHITECTURE.md §6.1 / ADR-003).
 *
 * Run via `npm run fixtures:generate`. Intentional regeneration only: the committed fixtures are
 * the cross-platform contract for the Slice 8 SkiaSharp renderer.
 */

import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import {
  buildGoldenSampleTimes,
  computeDecisionsAt,
  flattenLines,
} from '../src/features/stage/renderer/decisions.ts';

const SONG_IDS = [
  'synth-twinkle-variations',
  'synth-spoken-telegram',
  'synth-countdown-canyon',
] as const;

const webRoot = path.resolve(import.meta.dirname, '..');
const lyricsRoot = path.join(webRoot, 'public', 'demo-tracks');
const outputRoot = path.join(webRoot, 'fixtures', 'golden', 'stage-clock');

mkdirSync(outputRoot, { recursive: true });

for (const songId of SONG_IDS) {
  const lyricsPath = path.join(lyricsRoot, songId, 'lyrics.json');
  const lyrics = JSON.parse(readFileSync(lyricsPath, 'utf8'));
  const times = buildGoldenSampleTimes(lyrics);
  const syllableCount = flattenLines(lyrics)
    .reduce((total, line) => total + line.syllables.length, 0);

  const samples = times.map((tMs) => {
    const decisions = computeDecisionsAt(lyrics, tMs);
    return {
      tMs,
      activeLineIndex: decisions.activeLineIndex,
      syllableFills: decisions.syllableFills,
      dotState: decisions.dotState,
    };
  });

  const fixture = {
    formatVersion: 1,
    songId,
    description:
      'Decision-level golden clock series per ARCHITECTURE.md §6.1 / ADR-003. Shared with the Slice 8 SkiaSharp renderer (decision parity, not pixel parity). Regenerate ONLY deliberately via `npm run fixtures:generate`.',
    sampleCount: samples.length,
    samples,
  };

  const outputPath = path.join(outputRoot, `${songId}.json`);
  writeFileSync(outputPath, `${JSON.stringify(fixture, null, 2)}\n`, 'utf8');
  process.stdout.write(`wrote ${outputPath} (${samples.length} samples, ${syllableCount} syllables)\n`);
}
