import { describe, expect, it } from 'vitest';
import type { QueueDelta, QueueItemDto, RoomSnapshot } from './protocol';
import { PlaybackStatus, QueueItemState } from './protocol';
import {
  applyDelta,
  applyPlayback,
  applySnapshot,
  createEmptyRoomState,
} from './roomState';

const firstItem: QueueItemDto = {
  id: '11111111-1111-4111-8111-111111111111',
  songId: '22222222-2222-4222-8222-222222222222',
  position: 0,
  requestedByName: 'Ada',
  state: QueueItemState.Ready,
};

const secondItem: QueueItemDto = {
  id: '33333333-3333-4333-8333-333333333333',
  songId: '44444444-4444-4444-8444-444444444444',
  position: 1,
  requestedByName: 'Grace',
  state: QueueItemState.Ready,
};

const snapshot: RoomSnapshot = {
  roomCode: 'ABCD',
  status: PlaybackStatus.Idle,
  playback: {
    status: PlaybackStatus.Idle,
    currentQueueItemId: null,
    currentSong: null,
    positionMs: 0,
    timeAnchorUtc: '2026-01-01T00:00:00.000Z',
    guideVocalVolume: 0.6,
    countdownRemainingBeats: null,
  },
  queue: [firstItem, secondItem],
  guestCount: 2,
  queueSequence: 4,
};

function delta(overrides: Partial<QueueDelta> & Pick<QueueDelta, 'sequence'>): QueueDelta {
  return {
    added: [],
    removed: [],
    updated: [],
    ...overrides,
  };
}

describe('applySnapshot', () => {
  it('is idempotent', () => {
    const once = applySnapshot(createEmptyRoomState(), snapshot);
    const twice = applySnapshot(once, snapshot);

    expect(twice).toEqual(once);
    expect(twice.queue.map((item) => item.id)).toEqual([firstItem.id, secondItem.id]);
    expect(twice.queueSequence).toBe(4);
    expect(twice.guestCount).toBe(2);
  });

  it('sorts the queue densely by position', () => {
    const state = applySnapshot(createEmptyRoomState(), {
      ...snapshot,
      queue: [secondItem, firstItem],
    });

    expect(state.queue.map((item) => item.id)).toEqual([firstItem.id, secondItem.id]);
  });
});

describe('applyDelta', () => {
  it('accepts a contiguous delta and applies add/remove/update', () => {
    const initial = applySnapshot(createEmptyRoomState(), snapshot);
    let state = applyDelta(
      initial,
      delta({ sequence: 5, removed: [firstItem.id], updated: [{ ...secondItem, state: QueueItemState.Playing }] }),
    );
    expect(state.queue.map((item) => item.id)).toEqual([secondItem.id]);
    expect(state.queue[0]?.state).toBe(QueueItemState.Playing);
    expect(state.queueSequence).toBe(5);

    const added: QueueItemDto = {
      id: '55555555-5555-4555-8555-555555555555',
      songId: '66666666-6666-4666-8666-666666666666',
      position: 1,
      requestedByName: 'Linus',
      state: QueueItemState.Ready,
    };
    state = applyDelta(state, delta({ sequence: 6, added: [added] }));
    expect(state.queue.map((item) => item.id)).toEqual([secondItem.id, added.id]);
    expect(state.queueSequence).toBe(6);
  });

  it('ignores a delta with a sequence gap', () => {
    const initial = applySnapshot(createEmptyRoomState(), snapshot);
    const result = applyDelta(initial, delta({ sequence: 9, added: [firstItem] }));

    expect(result).toBe(initial);
  });

  it('ignores a duplicate delta', () => {
    const initial = applySnapshot(createEmptyRoomState(), snapshot);
    const result = applyDelta(initial, delta({ sequence: 4 }));

    expect(result).toBe(initial);
  });

  it('accepts the first delta when there is no snapshot yet', () => {
    const result = applyDelta(
      createEmptyRoomState(),
      delta({ sequence: 1, added: [firstItem] }),
    );

    expect(result.queue.map((item) => item.id)).toEqual([firstItem.id]);
    expect(result.queueSequence).toBe(1);
  });
});

describe('applyPlayback', () => {
  it('updates both the playback projection and the snapshot status', () => {
    const initial = applySnapshot(createEmptyRoomState(), snapshot);
    const playback = {
      ...snapshot.playback,
      status: PlaybackStatus.Playing,
      currentQueueItemId: firstItem.id,
      positionMs: 1_250,
    };

    const state = applyPlayback(initial, playback);

    expect(state.playback).toEqual(playback);
    expect(state.snapshot?.status).toBe(PlaybackStatus.Playing);
    expect(state.snapshot?.playback).toEqual(playback);
  });
});
