import type { StageConnectionStatus } from './protocol';

/**
 * Per-room `QueueDelta.sequence` guard (docs/ARCHITECTURE.md §5.2). The server sequence starts at
 * 1 and increases by one per queue mutation. A gap means the client missed a delta; the caller
 * must ignore it and re-join for a full snapshot rather than replaying deltas.
 *
 * `previousSequence === null` means "no snapshot/sequence observed yet", so the first delta is
 * always accepted.
 */
export function shouldAcceptDelta(
  previousSequence: number | null,
  nextSequence: number,
): boolean {
  if (previousSequence === null) {
    return true;
  }

  return nextSequence === previousSequence + 1;
}

export type ReconnectAction = 'rejoin' | 'none';

/**
 * Reconnection decision: once a connection has successfully joined a room, returning to the
 * connected state after automatic reconnect requires an idempotent `JoinRoom` re-sync. A bare
 * connection that never joined, or one that is still reconnecting, needs no action.
 */
export function decideReconnectAction(
  hasJoinedRoom: boolean,
  status: StageConnectionStatus,
): ReconnectAction {
  return hasJoinedRoom && status === 'connected' ? 'rejoin' : 'none';
}
