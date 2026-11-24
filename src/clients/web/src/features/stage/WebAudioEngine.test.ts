import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  WebAudioEngine,
  clearAudioLog,
  getAudioLog,
  stageTestClock,
  type LoadedStageSong,
  type StemLoader,
} from './WebAudioEngine';

interface ParamCall {
  method: string;
  args: number[];
}

class MockAudioParam {
  readonly calls: ParamCall[] = [];

  constructor(public value: number) {}

  cancelScheduledValues(time: number): void {
    this.calls.push({ method: 'cancelScheduledValues', args: [time] });
  }

  setValueAtTime(value: number, time: number): void {
    this.calls.push({ method: 'setValueAtTime', args: [value, time] });
    this.value = value;
  }

  linearRampToValueAtTime(value: number, time: number): void {
    this.calls.push({ method: 'linearRampToValueAtTime', args: [value, time] });
    this.value = value;
  }
}

class MockGainNode {
  readonly gain: MockAudioParam;

  constructor(value: number) {
    this.gain = new MockAudioParam(value);
  }

  connect(_node: unknown): void {
    // Graph wiring is irrelevant to these unit tests.
  }

  disconnect(): void {
    // No-op.
  }
}

interface StartCall {
  when: number;
  offset: number;
}

class MockBufferSource {
  buffer: AudioBuffer | null = null;
  onended: (() => void) | null = null;
  readonly startCalls: StartCall[] = [];
  stopCalls = 0;

  constructor(private readonly context: MockAudioContext) {}

  connect(_node: unknown): void {
    // No-op.
  }

  start(when = 0, offset = 0): void {
    this.startCalls.push({ when, offset });
    this.context.calls.push('start');
  }

  stop(): void {
    this.stopCalls += 1;
    this.context.calls.push('stop');
  }

  disconnect(): void {
    this.context.calls.push('disconnect');
  }

  finish(): void {
    this.onended?.();
  }
}

function createMockBuffer(): AudioBuffer {
  return { duration: 10 } as AudioBuffer;
}

class MockAudioContext {
  currentTime = 0;
  readonly destination = {};
  readonly calls: string[] = [];
  readonly gainNodes: MockGainNode[] = [];
  readonly sources: MockBufferSource[] = [];
  readonly pendingDecodes: Array<(buffer: AudioBuffer) => void> = [];
  deferDecode = false;
  decodeCalls = 0;
  resumeCalls = 0;
  resumeFailures = 0;

  createGain(): MockGainNode {
    const node = new MockGainNode(1);
    this.gainNodes.push(node);
    this.calls.push('createGain');
    return node;
  }

  createBufferSource(): MockBufferSource {
    const source = new MockBufferSource(this);
    this.sources.push(source);
    this.calls.push('createBufferSource');
    return source;
  }

  decodeAudioData(_data: ArrayBuffer): Promise<AudioBuffer> {
    this.decodeCalls += 1;
    this.calls.push('decodeAudioData');
    if (this.deferDecode) {
      return new Promise<AudioBuffer>((resolve) => {
        this.pendingDecodes.push(resolve);
      });
    }

    return Promise.resolve(createMockBuffer());
  }

  resume(): Promise<void> {
    this.resumeCalls += 1;
    this.calls.push('resume');
    if (this.resumeFailures > 0) {
      this.resumeFailures -= 1;
      return Promise.reject(new Error('resume failed'));
    }

    return Promise.resolve();
  }
}

const song: LoadedStageSong = {
  id: 'test-song',
  instrumentalUrl: '/demo-tracks/test-song/instrumental.opus',
  vocalsUrl: '/demo-tracks/test-song/vocals.opus',
};

function createHarness(loader: StemLoader = async () => new ArrayBuffer(8)) {
  const context = new MockAudioContext();
  const factory = vi.fn(() => context as unknown as AudioContext);
  const engine = new WebAudioEngine({ contextFactory: factory, loader });

  return { engine, context, factory };
}

beforeEach(() => {
  clearAudioLog();
  stageTestClock.clear();
});

describe('WebAudioEngine gesture gating', () => {
  it('creates no context and runs no playback path before unlock', async () => {
    const { engine, context, factory } = createHarness();

    expect(engine.unlocked).toBe(false);

    const loadPromise = engine.loadSong(song);
    expect(engine.start()).toBe(false);

    expect(factory).not.toHaveBeenCalled();
    expect(context.decodeCalls).toBe(0);
    expect(context.sources).toHaveLength(0);
    expect(context.resumeCalls).toBe(0);
    expect(getAudioLog()).not.toContain('AudioContext:created');
    expect(getAudioLog()).not.toContain('createMediaElementSource');
    expect(getAudioLog().some((entry) => entry.startsWith('decodeAudioData:'))).toBe(false);

    await engine.unlock();
    await Promise.all([engine.unlock(), loadPromise]);

    expect(factory).toHaveBeenCalledTimes(1);
    expect(context.resumeCalls).toBe(1);
    expect(context.decodeCalls).toBe(2);
    expect(getAudioLog()).toContain('AudioContext:created');
    expect(getAudioLog()).toContain('decodeAudioData:test-song');
    expect(getAudioLog()).not.toContain('createMediaElementSource');

    expect(engine.start()).toBe(true);
    expect(context.sources).toHaveLength(2);
    expect(getAudioLog()).toContain('createBufferSource');
    expect(getAudioLog()).toContain('start');
  });
});

describe('WebAudioEngine unlock retry', () => {
  it('rolls back a failed context factory and succeeds on the next click', async () => {
    const context = new MockAudioContext();
    let attempts = 0;
    const factory = vi.fn(() => {
      attempts += 1;
      if (attempts === 1) {
        throw new Error('context blocked');
      }
      return context as unknown as AudioContext;
    });
    const engine = new WebAudioEngine({
      contextFactory: factory,
      loader: async () => new ArrayBuffer(8),
    });

    await expect(engine.unlock()).rejects.toThrow('context blocked');
    expect(engine.unlocked).toBe(false);
    expect(getAudioLog().filter((entry) => entry === 'unlock-failed')).toHaveLength(1);

    await expect(engine.unlock()).resolves.toBeUndefined();
    expect(engine.unlocked).toBe(true);
    expect(factory).toHaveBeenCalledTimes(2);

    await engine.loadSong(song);
    expect(engine.start()).toBe(true);
  });

  it('rolls back a rejected resume and succeeds on the next click', async () => {
    const { engine, context, factory } = createHarness();
    context.resumeFailures = 1;

    await expect(engine.unlock()).rejects.toThrow('resume failed');
    expect(engine.unlocked).toBe(false);
    expect(getAudioLog().filter((entry) => entry === 'unlock-failed')).toHaveLength(1);

    await expect(engine.unlock()).resolves.toBeUndefined();
    expect(engine.unlocked).toBe(true);
    expect(factory).toHaveBeenCalledTimes(2);

    await engine.loadSong(song);
    expect(engine.start()).toBe(true);
  });
});

describe('WebAudioEngine crossfade', () => {
  it('ramps the guide bus in under 50 ms without touching sources', async () => {
    const { engine, context } = createHarness();
    await engine.unlock();
    await engine.loadSong(song);
    context.currentTime = 0.25;
    expect(engine.start()).toBe(true);

    const sourceCount = context.sources.length;
    const stopCount = context.calls.filter((call) => call === 'stop').length;
    const instrumentalParam = context.gainNodes[1].gain;
    const guideParam = context.gainNodes[2].gain;

    context.currentTime = 2;
    engine.setGuideVocalVolume(1);

    const ramps = guideParam.calls.filter((call) => call.method === 'linearRampToValueAtTime');
    expect(ramps).toHaveLength(1);
    expect(ramps[0].args[0]).toBe(1);
    expect(ramps[0].args[1]).toBeLessThanOrEqual(2 + 0.05);
    expect(guideParam.calls.some((call) => call.method === 'cancelScheduledValues')).toBe(true);
    expect(guideParam.calls.some((call) => call.method === 'setValueAtTime')).toBe(true);

    expect(instrumentalParam.calls).toHaveLength(0);
    expect(context.sources).toHaveLength(sourceCount);
    expect(context.calls.filter((call) => call === 'stop')).toHaveLength(stopCount);
    expect(engine.gains().guide).toBe(1);
    expect(getAudioLog()).toContain('gain:crossfade:45');
  });
});

describe('WebAudioEngine pre-buffer', () => {
  it('resolves loadSong only after both decode promises settle', async () => {
    const { engine, context } = createHarness();
    context.deferDecode = true;

    let resolved = false;
    const loadPromise = engine.loadSong(song).then(() => {
      resolved = true;
    });

    expect(engine.start()).toBe(false);

    await engine.unlock();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(context.pendingDecodes).toHaveLength(2);
    expect(resolved).toBe(false);

    const [firstDecode, secondDecode] = context.pendingDecodes;
    firstDecode(createMockBuffer());
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(resolved).toBe(false);

    secondDecode(createMockBuffer());
    await loadPromise;

    expect(resolved).toBe(true);
    expect(engine.start()).toBe(true);
  });
});

describe('WebAudioEngine clock', () => {
  it('derives position from the audio clock and honours the test-clock override', async () => {
    const { engine, context } = createHarness();
    await engine.unlock();
    await engine.loadSong(song);

    context.currentTime = 0.25;
    expect(engine.start()).toBe(true);

    context.currentTime = 3.25;
    expect(engine.positionMs()).toBeCloseTo(3000, 5);

    stageTestClock.set(1234);
    expect(engine.positionMs()).toBe(1234);

    stageTestClock.clear();
    expect(engine.positionMs()).toBeCloseTo(3000, 5);
  });

  it('seeks both sources to the offset and includes it in the position clock', async () => {
    const { engine, context } = createHarness();
    await engine.unlock();
    await engine.loadSong(song);

    context.currentTime = 0.25;
    expect(engine.start(2_000)).toBe(true);

    expect(context.sources[0].startCalls[0]).toEqual({ when: 0.25, offset: 2 });
    expect(context.sources[1].startCalls[0]).toEqual({ when: 0.25, offset: 2 });
    expect(engine.positionMs()).toBeCloseTo(2_000, 5);

    context.currentTime = 3.25;
    expect(engine.positionMs()).toBeCloseTo(5_000, 5);
  });

  it('defaults to a zero offset', async () => {
    const { engine, context } = createHarness();
    await engine.unlock();
    await engine.loadSong(song);

    context.currentTime = 0.5;
    expect(engine.start()).toBe(true);

    expect(context.sources[0].startCalls[0]).toEqual({ when: 0.5, offset: 0 });
    expect(context.sources[1].startCalls[0]).toEqual({ when: 0.5, offset: 0 });
    expect(engine.positionMs()).toBeCloseTo(0, 5);
  });
});

describe('WebAudioEngine completion', () => {
  it('fires onEnded on natural completion but not after a manual stop', async () => {
    const { engine, context } = createHarness();
    await engine.unlock();
    await engine.loadSong(song);

    let ended = 0;
    engine.onEnded = () => {
      ended += 1;
    };

    expect(engine.start()).toBe(true);
    context.sources[0].finish();
    expect(ended).toBe(1);

    // A second callback from the already-ended source is ignored.
    context.sources[0].finish();
    expect(ended).toBe(1);

    expect(engine.start()).toBe(true);
    engine.stop();
    context.sources[2].finish();
    expect(ended).toBe(1);
  });
});
