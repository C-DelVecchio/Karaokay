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

export function buildTrackSource(mediaId: string): LibraryTrackSource {
  const base = `/api/songs/${encodeURIComponent(mediaId)}`;
  return {
    mediaId,
    instrumentalUrl: `${base}/instrumental`,
    vocalsUrl: `${base}/vocals`,
    lyricsUrl: `${base}/lyrics`,
  };
}

let catalogPromise: Promise<SongSummaryDto[]> | null = null;

/** Test helper: forget the cached catalog between unit tests. */
export function resetSongCatalogCache(): void {
  catalogPromise = null;
}

async function fetchCatalog(): Promise<SongSummaryDto[]> {
  if (!catalogPromise) {
    catalogPromise = fetch('/api/songs')
      .then(async (response) => {
        if (!response.ok) {
          throw new Error(`Failed to fetch song catalog (${response.status})`);
        }

        return (await response.json()) as SongSummaryDto[];
      })
      .catch((error: unknown) => {
        // A transient catalog failure must not poison every later room item.
        catalogPromise = null;
        throw error;
      });
  }

  return catalogPromise;
}

/** Resolves the media endpoint id for a hub song, tolerating an unavailable catalog. */
export async function resolveTrackSource(song: SongDto): Promise<LibraryTrackSource> {
  let mediaId = song.id;
  try {
    mediaId = mediaIdFromCatalog(song, await fetchCatalog());
  } catch {
    // No catalog (stage opened without the API): the hub id is the best remaining guess.
  }

  return buildTrackSource(mediaId);
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
