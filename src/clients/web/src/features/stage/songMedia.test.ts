import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  MediaSource,
  type SongDto,
  type SongSummaryDto,
} from '../../lib/signalr/protocol';
import {
  SONG_CATALOG_TTL_MS,
  buildTrackSource,
  mediaIdFromCatalog,
  resetSongCatalogCache,
  resolveTrackSource,
} from './songMedia';

const song = { id: '11111111-1111-4111-8111-111111111111' };

function hubSong(id: string): SongDto {
  return {
    id,
    title: 'Song',
    artist: 'Synthetic',
    durationMs: 30_000,
    source: MediaSource.Multitrack,
    thumbnailUrl: null,
  };
}

function jsonResponse(songs: readonly SongSummaryDto[]): Response {
  return {
    ok: true,
    status: 200,
    json: async () => songs,
  } as unknown as Response;
}

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

describe('resolveTrackSource catalog cache', () => {
  beforeEach(() => {
    resetSongCatalogCache();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
    resetSongCatalogCache();
  });

  it('serves repeated lookups from the cached catalog within the TTL', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse([
      summary({ id: 'synth-twinkle-variations', enqueueId: song.id }),
      summary({ id: 'synth-countdown-canyon', enqueueId: 'other' }),
    ]));
    vi.stubGlobal('fetch', fetchMock);

    await expect(resolveTrackSource(hubSong(song.id)))
      .resolves.toMatchObject({ mediaId: 'synth-twinkle-variations' });
    await expect(resolveTrackSource(hubSong('other')))
      .resolves.toMatchObject({ mediaId: 'synth-countdown-canyon' });

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does not treat a direct database-mode id match as a cache miss', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse([
      summary({ id: song.id, enqueueId: null }),
    ]));
    vi.stubGlobal('fetch', fetchMock);

    await expect(resolveTrackSource(hubSong(song.id)))
      .resolves.toMatchObject({ mediaId: song.id });
    await expect(resolveTrackSource(hubSong(song.id)))
      .resolves.toMatchObject({ mediaId: song.id });

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('refetches once when a song appears after the catalog was cached', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(jsonResponse([summary({ id: 'elsewhere', enqueueId: 'other' })]))
      .mockResolvedValueOnce(jsonResponse([summary({ id: 'synth-new', enqueueId: song.id })]));
    vi.stubGlobal('fetch', fetchMock);

    await expect(resolveTrackSource(hubSong('other')))
      .resolves.toMatchObject({ mediaId: 'elsewhere' });
    // A miss against the cached snapshot invalidates it and retries immediately.
    await expect(resolveTrackSource(hubSong(song.id)))
      .resolves.toMatchObject({ mediaId: 'synth-new' });

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('refreshes the catalog after the TTL expires', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-01-01T00:00:00.000Z'));
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(jsonResponse([summary({ id: 'synth-old', enqueueId: 'other' })]))
      .mockResolvedValueOnce(jsonResponse([summary({ id: 'synth-new', enqueueId: 'other' })]));
    vi.stubGlobal('fetch', fetchMock);

    await expect(resolveTrackSource(hubSong('other')))
      .resolves.toMatchObject({ mediaId: 'synth-old' });

    vi.setSystemTime(Date.now() + SONG_CATALOG_TTL_MS);
    await expect(resolveTrackSource(hubSong('other')))
      .resolves.toMatchObject({ mediaId: 'synth-new' });

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('falls back to the hub id after a catalog failure and recovers next time', async () => {
    const fetchMock = vi.fn()
      .mockRejectedValueOnce(new Error('catalog offline'))
      .mockResolvedValueOnce(jsonResponse([
        summary({ id: 'synth-twinkle-variations', enqueueId: song.id }),
      ]));
    vi.stubGlobal('fetch', fetchMock);

    await expect(resolveTrackSource(hubSong(song.id)))
      .resolves.toMatchObject({ mediaId: song.id });
    await expect(resolveTrackSource(hubSong(song.id)))
      .resolves.toMatchObject({ mediaId: 'synth-twinkle-variations' });

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
