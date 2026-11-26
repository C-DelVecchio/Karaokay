import type { SongDto, SongSummaryDto } from '../../lib/signalr/protocol';
import type { LyricsDocument } from './lyrics';

/**
 * Library-song media resolution for authoritative room playback (docs/ARCHITECTURE.md §5.3).
 *
 * `PlaybackStateDto.currentSong` is the hub projection of a library song, so its `id` is what the
 * `/api/songs/{songId}/...` endpoints expect in database mode. The no-database fallback used by
 * `npm run dev` and Playwright keeps the Slice 2 directory slug as its REST id while the hub uses
 * the deterministic demo GUID. The catalog (`GET /api/songs`) exposes both, so one lookup bridges
 * the two representations without duplicating backend knowledge here.
 */

export interface LibraryTrackSource {
  /** Id accepted by the `/api/songs/{songId}/...` media endpoints. */
  mediaId: string;
  instrumentalUrl: string;
  vocalsUrl: string;
  lyricsUrl: string;
}

/** Picks the REST id for a hub song from the catalog, falling back to the hub id. */
export function mediaIdFromCatalog(
  song: Pick<SongDto, 'id'>,
  songs: readonly SongSummaryDto[],
): string {
  const match = songs.find(
    (entry) => entry.id === song.id || (entry.enqueueId != null && entry.enqueueId === song.id),
  );
  const mediaId = typeof match?.id === 'string' ? match.id.trim() : '';
  return mediaId.length > 0 ? mediaId : song.id;
}

/** True when the catalog knows this hub song (either representation). */
function catalogHasSong(
  song: Pick<SongDto, 'id'>,
  songs: readonly SongSummaryDto[],
): boolean {
  return songs.some(
    (entry) => entry.id === song.id || (entry.enqueueId != null && entry.enqueueId === song.id),
  );
}

export function buildTrackSource(mediaId: string): LibraryTrackSource {
  const base = `/api/songs/${encodeURIComponent(mediaId)}`;
  return {
    mediaId,
    instrumentalUrl: `${base}/instrumental`,
    vocalsUrl: `${base}/vocals`,
    lyricsUrl: `${base}/lyrics`,
  };
}

/**
 * Catalog entries can appear after the stage's first load (a library song added or downloaded
 * while the room is live), so the cache cannot live forever. It is refreshed after this TTL, and
 * a lookup miss invalidates it immediately and retries once.
 */
export const SONG_CATALOG_TTL_MS = 30_000;

interface CatalogCache {
  promise: Promise<SongSummaryDto[]>;
  /** When the request was started; a slow response cannot keep the entry fresh forever. */
  fetchedAtMs: number;
}

let catalogCache: CatalogCache | null = null;

/** Test helper: forget the cached catalog between unit tests. */
export function resetSongCatalogCache(): void {
  catalogCache = null;
}

async function fetchCatalog(
  force: boolean,
): Promise<{ songs: SongSummaryDto[]; fromCache: boolean }> {
  const now = Date.now();
  if (!force && catalogCache && now - catalogCache.fetchedAtMs < SONG_CATALOG_TTL_MS) {
    return { songs: await catalogCache.promise, fromCache: true };
  }

  const promise = fetch('/api/songs')
    .then(async (response) => {
      if (!response.ok) {
        throw new Error(`Failed to fetch song catalog (${response.status})`);
      }

      return (await response.json()) as SongSummaryDto[];
    })
    .catch((error: unknown) => {
      // A transient catalog failure must not poison every later room item.
      if (catalogCache?.promise === promise) {
        catalogCache = null;
      }

      throw error;
    });

  const entry: CatalogCache = { promise, fetchedAtMs: now };
  catalogCache = entry;
  return { songs: await promise, fromCache: false };
}

/** Resolves the media endpoint id for a hub song, tolerating an unavailable catalog. */
export async function resolveTrackSource(song: SongDto): Promise<LibraryTrackSource> {
  let catalog: { songs: SongSummaryDto[]; fromCache: boolean };
  try {
    catalog = await fetchCatalog(false);
  } catch {
    // No catalog (stage opened without the API): the hub id is the best remaining guess.
    return buildTrackSource(song.id);
  }

  if (!catalogHasSong(song, catalog.songs) && catalog.fromCache) {
    // The cached snapshot predates this song. Invalidate and ask once; a song added since the
    // first load would otherwise keep resolving to the hub id forever.
    try {
      catalog = await fetchCatalog(true);
    } catch {
      // Keep the stale snapshot (and the hub-id fallback) when the refresh fails.
    }
  }

  return buildTrackSource(mediaIdFromCatalog(song, catalog.songs));
}

export async function loadLyricsDocument(source: LibraryTrackSource): Promise<LyricsDocument> {
  const response = await fetch(source.lyricsUrl);
  if (!response.ok) {
    throw new Error(`Failed to fetch lyrics (${response.status})`);
  }

  return (await response.json()) as LyricsDocument;
}

export interface LoadedLibraryTrack {
  source: LibraryTrackSource;
  lyrics: LyricsDocument;
}

export async function loadLibraryTrack(song: SongDto): Promise<LoadedLibraryTrack> {
  const source = await resolveTrackSource(song);
  const lyrics = await loadLyricsDocument(source);
  return { source, lyrics };
}
