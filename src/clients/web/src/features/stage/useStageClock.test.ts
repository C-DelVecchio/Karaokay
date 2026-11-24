import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { WebAudioEngine, stageTestClock } from './WebAudioEngine';
import { createStageClock } from './useStageClock';

let scheduledFrames: Map<number, FrameRequestCallback>;
let cancelledIds: number[];
let nextFrameId: number;

beforeEach(() => {
  scheduledFrames = new Map();
  cancelledIds = [];
  nextFrameId = 1;
  stageTestClock.clear();

  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
    const id = nextFrameId++;
    scheduledFrames.set(id, callback);
    return id;
  });
  vi.stubGlobal('cancelAnimationFrame', (id: number) => {
    cancelledIds.push(id);
    scheduledFrames.delete(id);
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  stageTestClock.clear();
});

function runFrames(): void {
  const pending = [...scheduledFrames.values()];
  scheduledFrames.clear();
  for (const callback of pending) {
    callback(0);
  }
}

describe('createStageClock', () => {
  it('delivers the engine position to subscribers once per frame', () => {
    let position = 0;
    const clock = createStageClock({ positionMs: () => position });
    const seen: number[] = [];
    const unsubscribe = clock.subscribe((value) => seen.push(value));

    position = 120;
    runFrames();
    position = 240;
    runFrames();

    expect(seen).toEqual([120, 240]);
    unsubscribe();
  });

  it('ticks with the test-clock override', () => {
    stageTestClock.set(4200);
    const clock = createStageClock(new WebAudioEngine());
    const seen: number[] = [];
    const unsubscribe = clock.subscribe((value) => seen.push(value));

    runFrames();

    expect(seen).toEqual([4200]);
    unsubscribe();
  });

  it('cancels the RAF when the last subscriber unsubscribes', () => {
    const clock = createStageClock({ positionMs: () => 0 });
    const unsubscribeFirst = clock.subscribe(() => {});
    const unsubscribeSecond = clock.subscribe(() => {});

    expect(scheduledFrames.size).toBe(1);

    unsubscribeFirst();
    expect(cancelledIds).toHaveLength(0);

    unsubscribeSecond();
    expect(cancelledIds).toEqual([1]);
    expect(scheduledFrames.size).toBe(0);
  });

  it('stops the loop on dispose', () => {
    const clock = createStageClock({ positionMs: () => 0 });
    clock.subscribe(() => {});

    clock.dispose();

    expect(cancelledIds).toEqual([1]);
    expect(scheduledFrames.size).toBe(0);
  });
});
