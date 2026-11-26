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
 * Alignment uses the best-of-window sample: the lowest-RTT sample inside the last 8. A sample whose
 * RTT was inflated by a main-thread stall (stem decode, garbage collection) is rejected outright,
 * because a stall on either side of the exchange shifts the computed offset by roughly half the
 * stall. Taking the minimum RTT is the standard NTP robustness trick and is what keeps the startup
 * burst from poisoning the estimate while the stage is decoding its stems.
 *
 * Fewer than {@link CLOCK_SYNC_MIN_ACCEPTED_SAMPLES} accepted samples is not enough evidence to
 * shift a playback start, so the filter reports no offset and callers fall back to the room-state
 * anchor measured at receipt (see {@link receiptAnchorOffsetMs}).
 */

/** Samples retained for the best-of-window selection (the documented 8-sample filter). */
export const CLOCK_SYNC_SAMPLE_COUNT = 8;

/** At least two independent exchanges must agree before the offset is trusted for alignment. */
export const CLOCK_SYNC_MIN_ACCEPTED_SAMPLES = 2;

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
 * useful measurement, so its offset is discarded instead of polluting the window. This is the
 * second, looser gate: {@link CLOCK_SYNC_MAX_ALIGNMENT_RTT_MS} is the tight one applied to
 * alignment samples.
 */
export const CLOCK_SYNC_MAX_RTT_MS = 5_000;

/**
 * Samples slower than this are not usable for alignment. RTTs on a healthy link are a few ms; an
 * RTT in the hundreds of ms means a main-thread stall (stem decode, GC, layout) swallowed part of
 * the exchange, and halving that stall into the offset would misalign the start by hundreds of ms.
 */
export const CLOCK_SYNC_MAX_ALIGNMENT_RTT_MS = 250;

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

/**
 * Median of a numeric list; empty input yields 0.
 *
 * Test-only: production offset selection is `ClockOffsetFilter.bestSample`, a minimum-RTT
 * best-of-window pick. This helper is retained (and exercised) only by the clock-sync unit tests.
 */
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

/** Applies the estimated offset to a local timestamp. */
export function serverNowMs(localNowMs: number, offsetMs: number): number {
  return localNowMs + offsetMs;
}

/**
 * Offset implied by a room-state playback message received at `receivedAtLocalMs`.
 *
 * `anchorServerMs` is the server timestamp (`timeAnchorUtc`) at which the message's `positionMs`
 * was measured. Using `anchorServerMs − receivedAtLocalMs` as the clock offset makes the playback
 * position extrapolate from the *local receipt instant* instead of the server anchor:
 *
 * ```
 * position = positionMs + (serverNow − anchorServerMs)
 *          = positionMs + (localNow − receivedAtLocalMs)
 * ```
 *
 * The unknown clock offset cancels, leaving only the one-way transit delay as error. That is the
 * fallback used until the Ping/Pong filter is confident enough to trust its estimate.
 */
export function receiptAnchorOffsetMs(
  anchorServerMs: number,
  receivedAtLocalMs: number,
): number | null {
  if (!Number.isFinite(anchorServerMs) || !Number.isFinite(receivedAtLocalMs)) {
    return null;
  }

  return anchorServerMs - receivedAtLocalMs;
}

/**
 * Rolling 8-sample best-of-window filter.
 *
 * Samples with an RTT above {@link CLOCK_SYNC_MAX_ALIGNMENT_RTT_MS} are rejected, and the offset is
 * taken from the lowest-RTT sample in the window (ties prefer the newest sample so a window of
 * equally fast samples still tracks the current estimate). Fewer than
 * {@link CLOCK_SYNC_MIN_ACCEPTED_SAMPLES} accepted samples reports an offset of 0, which degrades
 * cleanly to the receipt-anchor fallback until the exchange has actually worked.
 */
export class ClockOffsetFilter {
  private readonly samples: ClockSample[] = [];

  constructor(private readonly capacity: number = CLOCK_SYNC_SAMPLE_COUNT) {
    if (capacity < 1) {
      throw new RangeError('ClockOffsetFilter capacity must be at least 1.');
    }
  }

  /** Adds a sample (null and stall-biased samples are rejected) and trims the window. */
  add(sample: ClockSample | null): boolean {
    if (!sample || !Number.isFinite(sample.rttMs) || sample.rttMs < 0) {
      return false;
    }

    if (sample.rttMs > CLOCK_SYNC_MAX_ALIGNMENT_RTT_MS) {
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

  /** True once enough independent exchanges have landed to trust the offset for alignment. */
  get confident(): boolean {
    return this.samples.length >= CLOCK_SYNC_MIN_ACCEPTED_SAMPLES;
  }

  /** Lowest-RTT sample in the window (newest wins ties), or null when empty. */
  get bestSample(): ClockSample | null {
    let best: ClockSample | null = null;
    for (const sample of this.samples) {
      if (best === null || sample.rttMs <= best.rttMs) {
        best = sample;
      }
    }

    return best;
  }

  /** Snapshot copy, oldest first. */
  get window(): readonly ClockSample[] {
    return [...this.samples];
  }

  get offsetMs(): number {
    return this.confident ? this.bestSample?.offsetMs ?? 0 : 0;
  }

  reset(): void {
    this.samples.length = 0;
  }
}

export interface ClockSyncSamplerOptions {
  /**
   * One Ping invocation. The transport must stamp `t1` as late as possible — immediately before
   * flushing the frame — and return the echoed {@link Pong}. The sampler deliberately does not
   * stamp the send itself: a timestamp taken before an await would include any main-thread stall
   * that happens before the frame actually leaves the browser.
   */
  ping: () => Promise<Pong>;
  /** Local monotonic-ish millisecond clock; injectable for tests. Defaults to `Date.now`. */
  now?: () => number;
  intervalMs?: number;
  warmupSamples?: number;
  warmupSpacingMs?: number;
  /** Called after every accepted sample with the current best-of-window offset. */
  onChange?: (offsetMs: number, confident: boolean) => void;
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

  private readonly ping: () => Promise<Pong>;
  private readonly now: () => number;
  private readonly intervalMs: number;
  private readonly warmupSamples: number;
  private readonly warmupSpacingMs: number;
  private readonly onChange: ((offsetMs: number, confident: boolean) => void) | undefined;

  private timer: ReturnType<typeof setTimeout> | null = null;
  private generation = 0;
  private inFlightGeneration: number | null = null;
  private active = false;
  private exchanges = 0;

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

  /** True once the filter has enough accepted samples to trust for playback alignment. */
  get confident(): boolean {
    return this.filter.confident;
  }

  start(): void {
    if (this.active) {
      return;
    }

    this.active = true;
    this.generation += 1;
    this.exchanges = 0;
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
    const pong = await this.ping();
    // `pong.t1` is the timestamp the transport recorded immediately before the flush; computing
    // the RTT from it excludes any stall that happened before the frame was actually sent.
    return computeClockSample(pong?.t1, pong, this.now());
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
      this.onChange?.(this.filter.offsetMs, this.filter.confident);
    }

    if (sample !== null) {
      this.exchanges += 1;
    }

    // Only a successful exchange advances the startup burst. A failed invocation backs off to the
    // steady-state cadence so an unreachable connection is never pinged in a tight loop. Rejected
    // (stall-biased) samples still count as exchanges so a persistently slow link cannot pin the
    // sampler to the 500 ms burst forever.
    const nextDelay = sample !== null && this.exchanges < this.warmupSamples
      ? this.warmupSpacingMs
      : this.intervalMs;
    this.schedule(nextDelay);
  }
}
