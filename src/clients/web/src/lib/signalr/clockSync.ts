import type { Pong } from './protocol';

/**
 * Wall-clock offset estimation over SignalR (docs/ARCHITECTURE.md §5.3, layer 1).
 *
 * The server is the wall-clock authority. Each sample follows the documented NTP-style exchange:
 *
 * ```
 * client sends Ping{t1} → server echoes Pong{t1, t2}
 * RTT = now4 − t1
 * clockOffset = t2 − (t1 + RTT / 2)
 * serverNow ≈ localNow + clockOffset
 * ```
 *
 * The median of the last 8 samples is the offset. Median filtering is what makes an occasional
 * slow/queued response harmless: one outlier cannot drag the estimate by more than half a window.
 */

/** Samples retained for the median (the documented 8-sample filter). */
export const CLOCK_SYNC_SAMPLE_COUNT = 8;

/** Steady-state sampling cadence once the startup burst has filled the window. */
export const CLOCK_SYNC_INTERVAL_MS = 5_000;

/**
 * Startup burst: the first few samples are taken close together so the filter becomes usable
 * (two samples) within the first second instead of after five. Sampling then settles to
 * {@link CLOCK_SYNC_INTERVAL_MS}.
 */
export const CLOCK_SYNC_WARMUP_SAMPLES = 4;
export const CLOCK_SYNC_WARMUP_SPACING_MS = 500;

/**
 * A response slower than this is almost certainly a stalled/aborted invocation rather than a
 * useful measurement, so its offset is discarded instead of polluting the median.
 */
export const CLOCK_SYNC_MAX_RTT_MS = 5_000;

export interface ClockSample {
  /** `t2 − (t1 + RTT / 2)`, i.e. how far the local clock is behind the server. */
  offsetMs: number;
  /** Round-trip time (`t4 − t1`) for this sample. */
  rttMs: number;
}

/**
 * Computes one offset sample from a Pong. Returns null for a mismatched echo, a non-finite value,
 * a negative RTT, or an implausibly slow response.
 */
export function computeClockSample(
  sentAtMs: number,
  pong: Pong,
  receivedAtMs: number,
): ClockSample | null {
  if (
    !Number.isFinite(sentAtMs)
    || !Number.isFinite(pong?.t1)
    || !Number.isFinite(pong?.t2)
    || !Number.isFinite(receivedAtMs)
  ) {
    return null;
  }

  // The echo must be the t1 this client sent, otherwise the sample belongs to another exchange.
  if (pong.t1 !== sentAtMs) {
    return null;
  }

  const rttMs = receivedAtMs - sentAtMs;
  if (rttMs < 0 || rttMs > CLOCK_SYNC_MAX_RTT_MS) {
    return null;
  }

  return {
    offsetMs: pong.t2 - (sentAtMs + rttMs / 2),
    rttMs,
  };
}

/** Median of a numeric list; empty input yields 0. */
export function median(values: readonly number[]): number {
  if (values.length === 0) {
    return 0;
  }

  const sorted = [...values].sort((left, right) => left - right);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 0
    ? (sorted[middle - 1] + sorted[middle]) / 2
    : sorted[middle];
}

/** Applies the median offset to a local timestamp. */
export function serverNowMs(localNowMs: number, offsetMs: number): number {
  return localNowMs + offsetMs;
}

/**
 * Rolling 8-sample median filter. Fewer than two samples reports an offset of 0, which degrades
 * cleanly to the historical local-wall-clock behavior until the exchange has actually worked.
 */
export class ClockOffsetFilter {
  private readonly samples: ClockSample[] = [];

  constructor(private readonly capacity: number = CLOCK_SYNC_SAMPLE_COUNT) {
    if (capacity < 1) {
      throw new RangeError('ClockOffsetFilter capacity must be at least 1.');
    }
  }

  /** Adds a sample (null is ignored) and trims the window to `capacity`. */
  add(sample: ClockSample | null): boolean {
    if (!sample) {
      return false;
    }

    this.samples.push(sample);
    if (this.samples.length > this.capacity) {
      this.samples.shift();
    }

    return true;
  }

  get count(): number {
    return this.samples.length;
  }

  /** Snapshot copy, oldest first. */
  get window(): readonly ClockSample[] {
    return [...this.samples];
  }

  get offsetMs(): number {
    if (this.samples.length < 2) {
      return 0;
    }

    return median(this.samples.map((sample) => sample.offsetMs));
  }

  reset(): void {
    this.samples.length = 0;
  }
}

export interface ClockSyncSamplerOptions {
  /** One Ping invocation (normally `stageClient.ping`). */
  ping: (t1: number) => Promise<Pong>;
  /** Local monotonic-ish millisecond clock; injectable for tests. Defaults to `Date.now`. */
  now?: () => number;
  intervalMs?: number;
  warmupSamples?: number;
  warmupSpacingMs?: number;
  /** Called after every accepted sample with the current median offset. */
  onChange?: (offsetMs: number) => void;
}

/**
 * Drives the Ping/Pong exchange on a timer and owns the offset filter.
 *
 * Lifecycle is explicit: `start()` after a successful room join and `stop()` on
 * reconnect/disconnect/leave. `stop()` bumps a generation counter so any Ping already in flight is
 * discarded when it resolves — an aborted exchange can never install a stale offset. A tick never
 * overlaps the previous one, and a failed invocation is swallowed and retried on the next cadence;
 * the sampler itself never retries in a tight loop.
 */
export class ClockSyncSampler {
  readonly filter = new ClockOffsetFilter();

  private readonly ping: (t1: number) => Promise<Pong>;
  private readonly now: () => number;
  private readonly intervalMs: number;
  private readonly warmupSamples: number;
  private readonly warmupSpacingMs: number;
  private readonly onChange: ((offsetMs: number) => void) | undefined;

  private timer: ReturnType<typeof setTimeout> | null = null;
  private generation = 0;
  private inFlightGeneration: number | null = null;
  private active = false;

  constructor(options: ClockSyncSamplerOptions) {
    this.ping = options.ping;
    this.now = options.now ?? Date.now;
    this.intervalMs = options.intervalMs ?? CLOCK_SYNC_INTERVAL_MS;
    this.warmupSamples = options.warmupSamples ?? CLOCK_SYNC_WARMUP_SAMPLES;
    this.warmupSpacingMs = options.warmupSpacingMs ?? CLOCK_SYNC_WARMUP_SPACING_MS;
    this.onChange = options.onChange;
  }

  get running(): boolean {
    return this.active;
  }

  get offsetMs(): number {
    return this.filter.offsetMs;
  }

  start(): void {
    if (this.active) {
      return;
    }

    this.active = true;
    this.generation += 1;
    this.schedule(0);
  }

  stop(): void {
    this.active = false;
    this.generation += 1;
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  /** Performs exactly one Ping/Pong exchange without touching the timer or the filter. */
  async sampleOnce(): Promise<ClockSample | null> {
    const sentAtMs = this.now();
    const pong = await this.ping(sentAtMs);
    return computeClockSample(sentAtMs, pong, this.now());
  }

  private schedule(delayMs: number): void {
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.tick();
    }, delayMs);
  }

  private async tick(): Promise<void> {
    if (!this.active) {
      return;
    }

    const generation = this.generation;
    if (this.inFlightGeneration === generation) {
      return;
    }

    this.inFlightGeneration = generation;

    let sample: ClockSample | null = null;
    try {
      sample = await this.sampleOnce();
    } catch {
      // A dropped invocation is not an error the stage can act on; the next tick retries.
      sample = null;
    } finally {
      // Only the exchange that owns this generation may clear the marker: a stop()/start() pair
      // while a Ping is in flight starts a new generation whose result must still be processed.
      if (this.inFlightGeneration === generation) {
        this.inFlightGeneration = null;
      }
    }

    if (!this.active || generation !== this.generation) {
      return;
    }

    if (this.filter.add(sample)) {
      this.onChange?.(this.filter.offsetMs);
    }

    // Only a successful sample advances the startup burst. A failed invocation backs off to the
    // steady-state cadence so an unreachable connection is never pinged in a tight loop.
    const nextDelay = sample !== null && this.filter.count < this.warmupSamples
      ? this.warmupSpacingMs
      : this.intervalMs;
    this.schedule(nextDelay);
  }
}
