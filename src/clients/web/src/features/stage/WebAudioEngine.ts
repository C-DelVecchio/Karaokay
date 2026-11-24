/**
 * Framework-free two-stem Web Audio engine for the browser stage.
 *
 * Autoplay safety: the constructor never touches Web Audio. The `AudioContext` is created only
 * inside `unlock()`, which must be called synchronously from a user-gesture handler. Decoding
 * waits on that unlock even if stems were fetched earlier. Nothing here calls
 * `createMediaElementSource` — that path is reserved for Slice 4 video mode.
 */

export interface LoadedStageSong {
  id: string;
  instrumentalUrl: string;
  vocalsUrl: string;
}

export type AudioContextFactory = () => AudioContext;

/** Fetches a stem URL and resolves with its raw bytes. Injectable for tests. */
export type StemLoader = (url: string) => Promise<ArrayBuffer>;

export interface WebAudioEngineOptions {
  contextFactory?: AudioContextFactory;
  loader?: StemLoader;
}

export interface StageGains {
  instrumental: number;
  guide: number;
  master: number;
}

const MAX_AUDIO_LOG_ENTRIES = 64;
const GUIDE_CROSSFADE_SECONDS = 0.045;
const DEFAULT_MASTER_GAIN = 1;
const DEFAULT_INSTRUMENTAL_GAIN = 1;
const DEFAULT_GUIDE_GAIN = 0.6;

const audioLog: string[] = [];

function logAudio(entry: string): void {
  audioLog.push(entry);
  if (audioLog.length > MAX_AUDIO_LOG_ENTRIES) {
    audioLog.shift();
  }

  if (typeof window !== 'undefined') {
    window.__karaokayAudioLog = audioLog;
  }
}

/** Read-only debug log (also mirrored to `window.__karaokayAudioLog` in the browser). */
export function getAudioLog(): readonly string[] {
  return audioLog;
}

/** Test helper: clears the module-level instrumentation log. */
export function clearAudioLog(): void {
  audioLog.length = 0;
}

let testClockMs: number | null = null;

/**
 * Test-only media-clock override. When set, `positionMs()` returns the fixed value so Playwright
 * and unit tests can drive the renderer deterministically without real playback.
 */
export const stageTestClock = {
  set(ms: number): void {
    testClockMs = ms;
  },
  clear(): void {
    testClockMs = null;
  },
  isSet(): boolean {
    return testClockMs !== null;
  },
};

declare global {
  interface Window {
    __karaokayAudioLog?: string[];
    __karaokayAudio?: {
      readonly unlocked: boolean;
      positionMs: () => number;
      gains: () => StageGains;
      engine: WebAudioEngine;
    };
    __karaokayTestClock?: {
      set: (ms: number) => void;
      clear: () => void;
    };
  }
}

if (typeof window !== 'undefined') {
  window.__karaokayAudioLog = audioLog;
  window.__karaokayTestClock = {
    set: stageTestClock.set,
    clear: stageTestClock.clear,
  };
}

const defaultLoader: StemLoader = async (url) => {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`Failed to fetch ${url} (${response.status})`);
  }

  return response.arrayBuffer();
};

export class WebAudioEngine {
  private readonly contextFactory: AudioContextFactory;
  private readonly loader: StemLoader;

  private context: AudioContext | null = null;
  private unlockPromise: Promise<void> | null = null;
  private resolveUnlocked: (() => void) | null = null;
  private unlockedSignal: Promise<void>;

  private instrumentalBus: GainNode | null = null;
  private guideBus: GainNode | null = null;
  private instrumentalVolume = DEFAULT_INSTRUMENTAL_GAIN;
  private guideVolume = DEFAULT_GUIDE_GAIN;
  private masterVolume = DEFAULT_MASTER_GAIN;

  private instrumentalBuffer: AudioBuffer | null = null;
  private vocalsBuffer: AudioBuffer | null = null;
  private instrumentalSource: AudioBufferSourceNode | null = null;
  private vocalsSource: AudioBufferSourceNode | null = null;

  private startCtxTime = 0;
  private startOffsetMs = 0;
  private isPlaying = false;
  private stoppedByUs = false;
  private loadGeneration = 0;

  public onEnded: (() => void) | null = null;

  constructor(options: WebAudioEngineOptions = {}) {
    this.contextFactory = options.contextFactory ?? (() => new AudioContext());
    this.loader = options.loader ?? defaultLoader;
    this.unlockedSignal = new Promise<void>((resolve) => {
      this.resolveUnlocked = resolve;
    });

    if (typeof window !== 'undefined') {
      const engine = this;
      window.__karaokayAudio = {
        get unlocked() {
          return engine.unlocked;
        },
        positionMs: () => engine.positionMs(),
        gains: () => engine.gains(),
        engine,
      };
    }
  }

  /** True once `unlock()` has been called (the context may still be resuming). */
  get unlocked(): boolean {
    return this.unlockPromise !== null;
  }

  get playing(): boolean {
    return this.isPlaying;
  }

  /**
   * Creates and resumes the AudioContext. Must be called from a user gesture. Idempotent: later
   * calls share the first promise and never create a second context. A failed attempt is rolled
   * back so the caller can retry with a fresh context.
   */
  unlock(): Promise<void> {
    if (this.unlockPromise) {
      return this.unlockPromise;
    }

    this.unlockPromise = this.createContext().catch((error: unknown) => {
      logAudio('unlock-failed');
      this.unlockPromise = null;
      this.context = null;
      this.instrumentalBus = null;
      this.guideBus = null;
      this.resolveUnlocked = null;
      this.unlockedSignal = new Promise<void>((resolve) => {
        this.resolveUnlocked = resolve;
      });
      throw error;
    });

    return this.unlockPromise;
  }

  /**
   * Fetches and decodes both stems. Fetching starts immediately, but decoding waits for `unlock()`.
   * Resolves only after BOTH buffers are decoded and stored, ready for `start()`.
   */
  async loadSong(song: LoadedStageSong): Promise<void> {
    const generation = ++this.loadGeneration;
    this.stopSources();
    this.isPlaying = false;
    this.instrumentalBuffer = null;
    this.vocalsBuffer = null;

    const [instrumentalData, vocalsData] = await Promise.all([
      this.loader(song.instrumentalUrl),
      this.loader(song.vocalsUrl),
    ]);

    await this.unlockedSignal;

    if (generation !== this.loadGeneration) {
      throw new Error(`loadSong(${song.id}) was superseded by a newer song`);
    }

    const context = this.context;
    if (!context) {
      throw new Error('AudioContext is not available after unlock');
    }

    const [instrumentalBuffer, vocalsBuffer] = await Promise.all([
      context.decodeAudioData(instrumentalData),
      context.decodeAudioData(vocalsData),
    ]);

    if (generation !== this.loadGeneration) {
      throw new Error(`loadSong(${song.id}) was superseded by a newer song`);
    }

    this.instrumentalBuffer = instrumentalBuffer;
    this.vocalsBuffer = vocalsBuffer;
    logAudio(`decodeAudioData:${song.id}`);
  }

  /**
   * Starts both stems at the same context time, optionally seeking into the buffers (the lead-in
   * consumed by the count-in). No-op returning false until unlocked and loaded.
   */
  start(offsetMs = 0): boolean {
    const context = this.context;
    const instrumentalBus = this.instrumentalBus;
    const guideBus = this.guideBus;
    if (
      !context
      || !instrumentalBus
      || !guideBus
      || !this.instrumentalBuffer
      || !this.vocalsBuffer
      || this.isPlaying
    ) {
      return false;
    }

    const instrumentalSource = context.createBufferSource();
    const vocalsSource = context.createBufferSource();
    logAudio('createBufferSource');

    instrumentalSource.buffer = this.instrumentalBuffer;
    vocalsSource.buffer = this.vocalsBuffer;
    instrumentalSource.connect(instrumentalBus);
    vocalsSource.connect(guideBus);

    const offset = Math.max(0, offsetMs);
    const offsetSeconds = offset / 1000;
    const startTime = context.currentTime;
    instrumentalSource.start(startTime, offsetSeconds);
    vocalsSource.start(startTime, offsetSeconds);
    logAudio('start');

    this.instrumentalSource = instrumentalSource;
    this.vocalsSource = vocalsSource;
    this.stoppedByUs = false;
    this.startCtxTime = startTime;
    this.startOffsetMs = offset;
    this.isPlaying = true;

    instrumentalSource.onended = () => {
      // Ignore manual stops and callbacks from sources replaced by a newer start.
      if (this.stoppedByUs || this.instrumentalSource !== instrumentalSource) {
        return;
      }

      this.isPlaying = false;
      this.instrumentalSource = null;
      this.vocalsSource = null;
      this.onEnded?.();
    };

    return true;
  }

  /** Stops and disconnects the current sources; never fires `onEnded`. */
  stop(): void {
    this.stoppedByUs = true;
    this.stopSources();
    this.isPlaying = false;
    this.startCtxTime = 0;
    this.startOffsetMs = 0;
  }

  /**
   * Media-clock ground truth. Uses `AudioContext.currentTime` only; the test-clock override wins
   * when a deterministic value has been installed.
   */
  positionMs(): number {
    if (testClockMs !== null) {
      return testClockMs;
    }

    if (!this.context || !this.isPlaying) {
      return this.startOffsetMs;
    }

    return (this.context.currentTime - this.startCtxTime) * 1000 + this.startOffsetMs;
  }

  /** Current mix values (deterministic targets, not AudioParam scheduling internals). */
  gains(): StageGains {
    return {
      instrumental: this.instrumentalVolume,
      guide: this.guideVolume,
      master: this.masterVolume,
    };
  }

  /**
   * Crossfades the guide-vocal bus in 45 ms without stopping or recreating any source and without
   * touching the instrumental bus.
   */
  setGuideVocalVolume(value: number): void {
    const target = Number.isFinite(value) ? Math.min(1, Math.max(0, value)) : 0;
    this.guideVolume = target;

    const context = this.context;
    const guideBus = this.guideBus;
    if (!context || !guideBus) {
      return;
    }

    const now = context.currentTime;
    const gain = guideBus.gain;
    gain.cancelScheduledValues(now);
    gain.setValueAtTime(gain.value, now);
    gain.linearRampToValueAtTime(target, now + GUIDE_CROSSFADE_SECONDS);
    logAudio(`gain:crossfade:${Math.round(GUIDE_CROSSFADE_SECONDS * 1000)}`);
  }

  private async createContext(): Promise<void> {
    try {
      const context = this.contextFactory();
      this.context = context;
      logAudio('AudioContext:created');

      const master = context.createGain();
      const instrumentalBus = context.createGain();
      const guideBus = context.createGain();
      master.gain.value = this.masterVolume;
      instrumentalBus.gain.value = this.instrumentalVolume;
      guideBus.gain.value = this.guideVolume;

      instrumentalBus.connect(master);
      guideBus.connect(master);
      master.connect(context.destination);

      this.instrumentalBus = instrumentalBus;
      this.guideBus = guideBus;

      await context.resume();
      logAudio('resume');
    } finally {
      // Release any waiting decode (which then fails cleanly if no context exists).
      this.resolveUnlocked?.();
      this.resolveUnlocked = null;
    }
  }

  private stopSources(): void {
    const sources = [this.instrumentalSource, this.vocalsSource];
    for (const source of sources) {
      if (!source) {
        continue;
      }

      source.onended = null;
      try {
        source.stop();
      } catch {
        // Already stopped: nothing to do.
      }
      source.disconnect();
    }

    this.instrumentalSource = null;
    this.vocalsSource = null;
  }
}
