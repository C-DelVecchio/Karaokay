import type { QueueItemState } from '../../lib/signalr/protocol';

const PLAYING: QueueItemState = 4;
const ENDED: QueueItemState = 5;

/**
 * The playing song and the ended history form the playback cursor. The server rejects moves for
 * those rows, so the phone UI must not offer them (docs/ARCHITECTURE.md §5.1).
 */
export function isQueueItemMovable(state: QueueItemState): boolean {
  return state !== PLAYING && state !== ENDED;
}

/**
 * Index of the first row a guest may move into. Rows before it are the cursor: the playing row
 * (when present) followed by nothing, or — while idle — the ended-history prefix.
 */
export function firstMovableIndex(queue: ReadonlyArray<{ state: QueueItemState }>): number {
  const playingIndex = queue.findIndex((item) => item.state === PLAYING);
  if (playingIndex >= 0) {
    return playingIndex + 1;
  }

  const firstUpcoming = queue.findIndex((item) => item.state !== ENDED);
  return firstUpcoming < 0 ? queue.length : firstUpcoming;
}
