import type {
  PlaybackStateDto,
  QueueDelta,
  QueueItemDto,
  RoomSnapshot,
} from './protocol';
import { shouldAcceptDelta } from './sync';

/**
 * Pure client-side room state. `RoomSnapshot` is the single source of truth (join and rejoin take
 * the same path); deltas are applied only when their sequence is contiguous.
 */
export interface RemoteRoomState {
  snapshot: RoomSnapshot | null;
  playback: PlaybackStateDto | null;
  queue: QueueItemDto[];
  guestCount: number;
  queueSequence: number | null;
}

export function createEmptyRoomState(): RemoteRoomState {
  return {
    snapshot: null,
    playback: null,
    queue: [],
    guestCount: 0,
    queueSequence: null,
  };
}

function sortQueue(queue: readonly QueueItemDto[]): QueueItemDto[] {
  return [...queue].sort((left, right) =>
    left.position !== right.position
      ? left.position - right.position
      : left.id.localeCompare(right.id));
}

/** Applies a full snapshot. Idempotent: applying the same snapshot twice is a no-op in value. */
export function applySnapshot(
  _state: RemoteRoomState,
  snapshot: RoomSnapshot,
): RemoteRoomState {
  return {
    snapshot,
    playback: snapshot.playback,
    queue: sortQueue(snapshot.queue),
    guestCount: snapshot.guestCount,
    queueSequence: snapshot.queueSequence,
  };
}

/** Applies a contiguous delta; a gap/duplicate leaves the state untouched (caller re-syncs). */
export function applyDelta(state: RemoteRoomState, delta: QueueDelta): RemoteRoomState {
  if (state.queueSequence !== null) {
    if (!shouldAcceptDelta(state.queueSequence, delta.sequence)) {
      return state;
    }
  }

  const removed = new Set(delta.removed);
  let queue = state.queue.filter((item) => !removed.has(item.id));

  for (const added of delta.added) {
    if (!queue.some((item) => item.id === added.id)) {
      queue.push(added);
    }
  }

  for (const updated of delta.updated) {
    const index = queue.findIndex((item) => item.id === updated.id);
    if (index >= 0) {
      queue[index] = updated;
    } else {
      queue.push(updated);
    }
  }

  queue = sortQueue(queue);

  return {
    ...state,
    queue,
    queueSequence: delta.sequence,
    snapshot: state.snapshot
      ? { ...state.snapshot, queue, queueSequence: delta.sequence }
      : null,
  };
}

/** Applies an authoritative playback projection (position/status/volume/current song). */
export function applyPlayback(
  state: RemoteRoomState,
  playback: PlaybackStateDto,
): RemoteRoomState {
  return {
    ...state,
    playback,
    snapshot: state.snapshot
      ? { ...state.snapshot, status: playback.status, playback }
      : null,
  };
}

/** Applies the latest participant count from `ParticipantJoined`/`ParticipantLeft`. */
export function applyGuestCount(
  state: RemoteRoomState,
  guestCount: number,
): RemoteRoomState {
  if (state.guestCount === guestCount) {
    return state;
  }

  return {
    ...state,
    guestCount,
    snapshot: state.snapshot ? { ...state.snapshot, guestCount } : null,
  };
}
