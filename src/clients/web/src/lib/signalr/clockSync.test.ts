import { afterEach, describe, expect, it, vi } from 'vitest';
import type { PlaybackStateDto, Pong } from './protocol';
import { MediaSource, PlaybackStatus } from './protocol';
import {
  CLOCK_SYNC_INTERVAL_MS,
  CLOCK_SYNC_SAMPLE_COUNT,
  CLOCK_SYNC_WARMUP_SAMPLES,
  CLOCK_SYNC_WARMUP_SPACING_MS,
  ClockOffsetFilter,
  ClockSyncSampler,
  computeClockSample,
  median,
  receiptAnchorOffsetMs,
  serverNowMs,
} from './clockSync';
import { videoCorrection } from '../../features/stage/videoSync';

function pong(t1: number, t2: number): Pong {
  return { t1, t2 };
}

afterEach(() => {
  vi.useRealTimers();
});

describe('computeClockSample', () => {
  it('computes RTT and offset from the documented formula', () => {
    // Sent at local 1 000, response arrived at local 1 100 (RTT 100). The server clock is
    // 4 050 ms ahead: t2 = 5 100 = 1 000 + 50 (half RTT) + 4 050.
    const sample = computeClockSample(1_000, pong(1_000, 5_100), 1_100);

    expect(sample).toEqual({ rttMs: 100, offsetMs: 4_050 });
  });

  it('rejects a mismatched echo, non-finite values, and implausible RTTs', () => {
    expect(computeClockSample(1_000, pong(999, 5_100), 1_100)).toBeNull();
    expect(computeClockSample(1_000, pong(1_000, Number.NaN), 1_100)).toBeNull();
    expect(computeClockSample(1_000, pong(1_000, 5_100), 900)).toBeNull();
    expect(computeClockSample(1_000, pong(1_000, 5_100), 100_000)).toBeNull();
  });
});

describe('median (test-only helper)', () => {
  it('averages the two middle values for an even sample count', () => {
    expect(median([0, 10, 20, 30, 40, 50, 60, 70])).toBe(35);
  });

  it('returns the middle value for an odd sample count and 0 for an empty list', () => {
    expect(median([5, 1, 9])).toBe(5);
    expect(median([])).toBe(0);
  });
});

describe('ClockOffsetFilter', () => {
  it('reports 0 until at least two samples have been collected', () => {
    const filter = new ClockOffsetFilter();

    expect(filter.confident).toBe(false);
    expect(filter.offsetMs).toBe(0);
    expect(filter.add({ offsetMs: 9_000, rttMs: 20 })).toBe(true);
    expect(filter.confident).toBe(false);
    expect(filter.offsetMs).toBe(0);
    filter.add({ offsetMs: 9_100, rttMs: 20 });
    expect(filter.confident).toBe(true);
    expect(filter.offsetMs).toBe(9_100);
  });

  it('ignores null and stalled samples', () => {
    const filter = new ClockOffsetFilter();
    filter.add({ offsetMs: 100, rttMs: 20 });

    expect(filter.add(null)).toBe(false);
    // A stall-biased exchange looks like a 3.5 s RTT and must never enter the alignment window.
    expect(filter.add({ offsetMs: 1_750, rttMs: 3_500 })).toBe(false);
    expect(filter.count).toBe(1);
    expect(filter.offsetMs).toBe(0);
  });

  it('keeps the last 8 samples and selects the lowest-RTT one', () => {
    const filter = new ClockOffsetFilter();
    for (let index = 1; index <= 10; index += 1) {
      filter.add({ offsetMs: index, rttMs: index === 8 ? 5 : 20 });
    }

    expect(filter.count).toBe(CLOCK_SYNC_SAMPLE_COUNT);
    expect(filter.window.map((sample) => sample.offsetMs)).toEqual([3, 4, 5, 6, 7, 8, 9, 10]);
    // The offset 8 sample has the lowest RTT; the newest equal-RTT sample would be 10 otherwise.
    expect(filter.offsetMs).toBe(8);
  });

  it('prefers the minimum-RTT sample over a fast-but-noisy window', () => {
    const filter = new ClockOffsetFilter();
    filter.add({ offsetMs: 1_000, rttMs: 120 });
    filter.add({ offsetMs: 40, rttMs: 6 });
    filter.add({ offsetMs: 2_000, rttMs: 200 });

    expect(filter.offsetMs).toBe(40);
  });

  it('rejects a stall-biased outlier instead of letting it poison the window', () => {
    const filter = new ClockOffsetFilter();
    for (const offsetMs of [100, 101, 102, 103, 104, 105, 106]) {
      filter.add({ offsetMs, rttMs: 20 });
    }

    expect(filter.add({ offsetMs: 10_000, rttMs: 3_500 })).toBe(false);
    expect(filter.count).toBe(7);
    expect(filter.offsetMs).toBe(106);
  });
});

describe('receiptAnchorOffsetMs', () => {
  it('derives an offset that survives a main-thread stall after the anchor was received', () => {
    // The server anchored 5 s of playback at 12:00:10 and this client received it at local 12:00:10
    // (same clock). Even after a 3.5 s stall, extrapolating from the local receipt instant keeps
    // the position within one-way delay instead of half the stall.
    const anchorServerMs = Date.parse('2026-11-25T12:00:10.000Z');
    const receivedAtLocalMs = anchorServerMs;

    expect(receiptAnchorOffsetMs(anchorServerMs, receivedAtLocalMs)).toBe(0);
    expect(receiptAnchorOffsetMs(anchorServerMs, receivedAtLocalMs - 250)).toBe(250);
    expect(receiptAnchorOffsetMs(Number.NaN, receivedAtLocalMs)).toBeNull();
  });
});

describe('ClockSyncSampler', () => {
  it('computes an offset sample from a Ping/Pong exchange', async () => {
    let now = 10_000;
    const pings: number[] = [];
    const sentAtMs = 10_000;
    const sampler = new ClockSyncSampler({
      ping: async () => {
        // The transport owns t1 and stamps it immediately before the send.
        pings.push(sentAtMs);
        const rttMs = 40;
        now += rttMs;
        // Symmetric delay: the server is exactly 250 ms ahead of the local clock.
        return pong(sentAtMs, sentAtMs + rttMs / 2 + 250);
      },
      now: () => now,
    });

    const sample = await sampler.sampleOnce();

    expect(pings).toEqual([10_000]);
    expect(sample).toEqual({ offsetMs: 250, rttMs: 40 });
  });

  it('runs a startup burst then settles to the 5 s cadence', async () => {
    vi.useFakeTimers();
    let pings = 0;
    const sampler = new ClockSyncSampler({
      ping: async () => {
        pings += 1;
        return pong(1_000, 1_100);
      },
      now: () => 1_000,
    });

    sampler.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(pings).toBe(1);

    await vi.advanceTimersByTimeAsync(CLOCK_SYNC_WARMUP_SPACING_MS - 1);
    expect(pings).toBe(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(pings).toBe(2);

    // Fill the startup burst: samples at 0, 500, 1000, 1500 ms.
    await vi.advanceTimersByTimeAsync(
      CLOCK_SYNC_WARMUP_SPACING_MS * (CLOCK_SYNC_WARMUP_SAMPLES - 2),
    );
    expect(pings).toBe(CLOCK_SYNC_WARMUP_SAMPLES);
    expect(sampler.offsetMs).toBe(100);

    await vi.advanceTimersByTimeAsync(CLOCK_SYNC_INTERVAL_MS - 1);
    expect(pings).toBe(CLOCK_SYNC_WARMUP_SAMPLES);
    await vi.advanceTimersByTimeAsync(1);
    expect(pings).toBe(CLOCK_SYNC_WARMUP_SAMPLES + 1);

    sampler.stop();
  });

  it('keeps the offset untrusted while stall-biased samples are rejected', async () => {
    vi.useFakeTimers();
    let now = 10_000;
    let pings = 0;
    const onChange = vi.fn();
    const sampler = new ClockSyncSampler({
      ping: async () => {
        pings += 1;
        const t1 = now;
        if (pings === 1) {
          // A 3.5 s main-thread stall between send and receive shifts the computed offset by
          // ~1.75 s; the sample must be rejected rather than align playback from it.
          now += 3_500;
          return pong(t1, t1 + 1_750);
        }

        now += 20;
        return pong(t1, t1 + 10 + 40);
      },
      now: () => now,
      onChange,
    });

    sampler.start();
    await vi.advanceTimersByTimeAsync(0);

    expect(sampler.filter.count).toBe(0);
    expect(sampler.confident).toBe(false);
    expect(sampler.offsetMs).toBe(0);
    expect(onChange).not.toHaveBeenCalled();

    // The next fast exchange is usable and restores confidence.
    await vi.advanceTimersByTimeAsync(CLOCK_SYNC_WARMUP_SPACING_MS);
    expect(sampler.filter.count).toBe(1);
    expect(sampler.confident).toBe(false);

    await vi.advanceTimersByTimeAsync(CLOCK_SYNC_WARMUP_SPACING_MS);
    expect(sampler.filter.count).toBe(2);
    expect(sampler.confident).toBe(true);
    expect(sampler.offsetMs).toBe(40);

    sampler.stop();
  });

  it('discards an in-flight sample after stop()', async () => {
    vi.useFakeTimers();
    let resolvePong: ((value: Pong) => void) | null = null;
    const onChange = vi.fn();
    const sampler = new ClockSyncSampler({
      ping: () => new Promise<Pong>((resolve) => {
        resolvePong = resolve;
      }),
      now: () => 1_000,
      onChange,
    });

    sampler.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(resolvePong).not.toBeNull();

    sampler.stop();
    resolvePong!(pong(1_000, 9_000));
    await vi.advanceTimersByTimeAsync(0);

    expect(sampler.filter.count).toBe(0);
    expect(onChange).not.toHaveBeenCalled();
  });

  it('resumes sampling when restarted while a Ping is still in flight', async () => {
    vi.useFakeTimers();
    const resolvers: Array<(value: Pong) => void> = [];
    const sampler = new ClockSyncSampler({
      ping: () => new Promise<Pong>((resolve) => {
        resolvers.push(resolve);
      }),
      now: () => 1_000,
    });

    sampler.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(resolvers).toHaveLength(1);

    // A reconnect status flap stops and immediately restarts the sampler.
    sampler.stop();
    sampler.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(resolvers).toHaveLength(2);

    // The pre-stop exchange is stale and must not be installed.
    resolvers[0](pong(1_000, 9_000));
    await vi.advanceTimersByTimeAsync(0);
    expect(sampler.filter.count).toBe(0);

    // The post-restart exchange lands normally.
    resolvers[1](pong(1_000, 1_100));
    await vi.advanceTimersByTimeAsync(0);
    expect(sampler.filter.count).toBe(1);

    sampler.stop();
  });

  it('backs off to the steady cadence after a failed invocation', async () => {
    vi.useFakeTimers();
    const ping = vi.fn(async () => {
      throw new Error('connection down');
    });
    const sampler = new ClockSyncSampler({ ping, now: () => 1_000 });

    sampler.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(ping).toHaveBeenCalledTimes(1);

    // A failure must not retrigger the 500 ms startup burst.
    await vi.advanceTimersByTimeAsync(CLOCK_SYNC_WARMUP_SPACING_MS);
    expect(ping).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(CLOCK_SYNC_INTERVAL_MS - CLOCK_SYNC_WARMUP_SPACING_MS);
    expect(ping).toHaveBeenCalledTimes(2);

    sampler.stop();
  });
});

describe('server-now wiring into video correction', () => {
  function playback(): PlaybackStateDto {
    return {
      status: PlaybackStatus.Playing,
      currentQueueItemId: 'item-1',
      currentSong: {
        id: 'song-1',
        title: 'Video song',
        artist: 'Channel',
        durationMs: 180_000,
        source: MediaSource.YouTube,
        thumbnailUrl: null,
      },
      positionMs: 5_000,
      timeAnchorUtc: '2026-01-01T00:00:10.000Z',
      guideVocalVolume: 0.6,
      countdownRemainingBeats: null,
    };
  }

  it('passes server-now into correction instead of a skewed local clock', () => {
    const anchorMs = Date.parse('2026-01-01T00:00:10.000Z');
    // The server has played 5 s past the anchor; this client's clock is 3 s behind.
    const localNowMs = anchorMs + 2_000;
    const clockOffsetMs = 3_000;
    const actualVideoMs = 10_000;

    const corrected = videoCorrection(
      playback(),
      actualVideoMs,
      serverNowMs(localNowMs, clockOffsetMs),
    );

    expect(corrected.expectedMs).toBe(10_000);
    expect(corrected.seekMs).toBeNull();

    // Without the offset the stage would chase a target 3 s in the past.
    const skewed = videoCorrection(playback(), actualVideoMs, localNowMs);
    expect(skewed.expectedMs).toBe(7_000);
    expect(skewed.seekMs).toBe(7_000);
  });
});
