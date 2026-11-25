import { describe, expect, it } from 'vitest';
import type { QueueItemState } from '../../lib/signalr/protocol';
import { firstMovableIndex, isQueueItemMovable } from './queueOrdering';

const row = (state: QueueItemState) => ({ state });

describe('queue row movement', () => {
  it('treats the playing song and ended history as cursor rows', () => {
    expect(isQueueItemMovable(4)).toBe(false);
    expect(isQueueItemMovable(5)).toBe(false);
  });

  it('allows reordering queued, downloading, ready, and failed rows', () => {
    for (const state of [0, 1, 2, 3] as QueueItemState[]) {
      expect(isQueueItemMovable(state)).toBe(true);
    }
  });

  it('starts the movable region after the playing row', () => {
    expect(firstMovableIndex([row(5), row(4), row(2), row(2)])).toBe(2);
    expect(firstMovableIndex([row(4), row(2)])).toBe(1);
  });

  it('keeps idle rooms behind their ended-history prefix', () => {
    expect(firstMovableIndex([row(5), row(5), row(2)])).toBe(2);
    expect(firstMovableIndex([row(2), row(2)])).toBe(0);
    expect(firstMovableIndex([])).toBe(0);
    expect(firstMovableIndex([row(5)])).toBe(1);
  });
});
