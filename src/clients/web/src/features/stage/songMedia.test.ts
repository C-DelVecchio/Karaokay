import { describe, expect, it } from 'vitest';
import type { SongSummaryDto } from '../../lib/signalr/protocol';
import { buildTrackSource, mediaIdFromCatalog } from './songMedia';

const song = { id: '11111111-1111-4111-8111-111111111111' };

function summary(overrides: Partial<SongSummaryDto>): SongSummaryDto {
  return {
    id: '11111111-1111-4111-8111-111111111111',
    title: 'Song',
    artist: 'Synthetic',
    durationMs: 30_000,
    source: 'Multitrack',
    hasGuideVocals: true,
    enqueueId: null,
    ...overrides,
  };
}

describe('mediaIdFromCatalog', () => {
  it('matches the hub id directly in database mode', () => {
    expect(mediaIdFromCatalog(song, [summary({})])).toBe(song.id);
  });

  it('bridges the deterministic demo GUID to the fallback directory slug', () => {
    const catalog = [
      summary({ id: 'synth-twinkle-variations', enqueueId: song.id }),
      summary({ id: 'synth-countdown-canyon', enqueueId: 'other' }),
    ];

    expect(mediaIdFromCatalog(song, catalog)).toBe('synth-twinkle-variations');
  });

  it('falls back to the hub id when the catalog has no match', () => {
    expect(mediaIdFromCatalog(song, [summary({ id: 'elsewhere', enqueueId: 'other' })])).toBe(song.id);
    expect(mediaIdFromCatalog(song, [])).toBe(song.id);
  });
});

describe('buildTrackSource', () => {
  it('builds the three media endpoints with an encoded id', () => {
    expect(buildTrackSource('song/with space')).toEqual({
      mediaId: 'song/with space',
      instrumentalUrl: '/api/songs/song%2Fwith%20space/instrumental',
      vocalsUrl: '/api/songs/song%2Fwith%20space/vocals',
      lyricsUrl: '/api/songs/song%2Fwith%20space/lyrics',
    });
  });
});
