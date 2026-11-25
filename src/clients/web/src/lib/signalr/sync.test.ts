import { describe, expect, it } from 'vitest';
import { decideReconnectAction, shouldAcceptDelta } from './sync';

describe('shouldAcceptDelta', () => {
  it('accepts the first delta when no snapshot sequence is known', () => {
    expect(shouldAcceptDelta(null, 1)).toBe(true);
    expect(shouldAcceptDelta(null, 42)).toBe(true);
  });

  it('accepts a contiguous sequence', () => {
    expect(shouldAcceptDelta(0, 1)).toBe(true);
    expect(shouldAcceptDelta(5, 6)).toBe(true);
    expect(shouldAcceptDelta(999, 1000)).toBe(true);
  });

  it('rejects a gap so the caller re-syncs from a snapshot', () => {
    expect(shouldAcceptDelta(5, 7)).toBe(false);
    expect(shouldAcceptDelta(5, 100)).toBe(false);
  });

  it('rejects duplicates and out-of-order deltas', () => {
    expect(shouldAcceptDelta(5, 5)).toBe(false);
    expect(shouldAcceptDelta(5, 4)).toBe(false);
  });
});

describe('decideReconnectAction', () => {
  it('re-joins after automatic reconnect when a room was joined', () => {
    expect(decideReconnectAction(true, 'connected')).toBe('rejoin');
  });

  it('does nothing without a joined room', () => {
    expect(decideReconnectAction(false, 'connected')).toBe('none');
  });

  it('waits for the connected state before re-joining', () => {
    expect(decideReconnectAction(true, 'reconnecting')).toBe('none');
    expect(decideReconnectAction(true, 'connecting')).toBe('none');
    expect(decideReconnectAction(true, 'disconnected')).toBe('none');
    expect(decideReconnectAction(true, 'idle')).toBe('none');
  });
});
