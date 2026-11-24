import { useCallback, useEffect, useMemo } from 'react';

/**
 * RAF-driven stage clock. Readers (Package 3's canvas renderer, debug overlays) subscribe without
 * React state, so playback never re-renders the component tree once per frame.
 */

export interface StageClockSource {
  positionMs: () => number;
}

export type StageClockListener = (positionMs: number) => void;
export type SubscribeStageClock = (listener: StageClockListener) => () => void;

export interface StageClock {
  subscribe: SubscribeStageClock;
  dispose: () => void;
}

export function createStageClock(source: StageClockSource): StageClock {
  const listeners = new Set<StageClockListener>();
  let rafId: number | null = null;
  let framePositionMs = 0;

  // One stable notifier keeps the RAF path free of per-frame closures/array copies.
  const notify = (listener: StageClockListener) => {
    listener(framePositionMs);
  };

  const frame = () => {
    framePositionMs = source.positionMs();
    listeners.forEach(notify);
    rafId = listeners.size > 0 ? requestAnimationFrame(frame) : null;
  };

  const subscribe: SubscribeStageClock = (listener) => {
    listeners.add(listener);
    if (rafId === null) {
      rafId = requestAnimationFrame(frame);
    }

    return () => {
      listeners.delete(listener);
      if (listeners.size === 0 && rafId !== null) {
        cancelAnimationFrame(rafId);
        rafId = null;
      }
    };
  };

  return {
    subscribe,
    dispose() {
      listeners.clear();
      if (rafId !== null) {
        cancelAnimationFrame(rafId);
        rafId = null;
      }
    },
  };
}

/**
 * Returns a stable `subscribeStageClock(cb)` for the current engine. The RAF loop runs only while
 * at least one subscriber is attached and is canceled on unmount.
 */
export function useStageClock(source: StageClockSource | null): SubscribeStageClock {
  const clock = useMemo(() => (source ? createStageClock(source) : null), [source]);

  useEffect(() => () => {
    clock?.dispose();
  }, [clock]);

  return useCallback<SubscribeStageClock>(
    (listener) => (clock ? clock.subscribe(listener) : () => {}),
    [clock],
  );
}
