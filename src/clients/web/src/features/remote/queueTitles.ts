/** Legacy fallback used when neither the queue DTO nor the songs snapshot knows the title. */
export function queueTitlePlaceholder(songId: string): string {
  return `Song ${songId.slice(0, 8)}`;
}

/**
 * Blank titles and the legacy `Song {id-prefix}` placeholder carry no information, so a better
 * source may still win. The server now sends the real title on every queue item.
 */
function meaningfulTitle(value: string | null | undefined): string | null {
  const trimmed = value?.trim();
  if (!trimmed || /^Song [0-9a-z-]{8}$/i.test(trimmed)) {
    return null;
  }

  return trimmed;
}

export interface QueueTitleSource {
  songId: string;
  title?: string | null;
}

/**
 * Resolution order: the DTO title (authoritative for newly enqueued YouTube songs that are not in
 * the `/api/songs` snapshot yet), then the songs-snapshot join, then the legacy placeholder.
 */
export function resolveQueueItemTitle(
  item: QueueTitleSource,
  titles: ReadonlyMap<string, string>,
): string {
  return meaningfulTitle(item.title)
    ?? meaningfulTitle(titles.get(item.songId))
    ?? queueTitlePlaceholder(item.songId);
}

/** Builds the songs-snapshot join used as the fallback title source. */
export function queueTitlesFromSongs(
  songs: ReadonlyArray<{ id?: string; enqueueId?: string | null; title?: string | null }>,
): Map<string, string> {
  const titles = new Map<string, string>();
  for (const song of songs) {
    const title = song.title ?? 'Untitled';
    if (song.id) {
      titles.set(song.id, title);
    }
    if (song.enqueueId) {
      titles.set(song.enqueueId, title);
    }
  }

  return titles;
}
