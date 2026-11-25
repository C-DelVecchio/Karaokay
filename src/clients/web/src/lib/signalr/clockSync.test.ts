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

describe('median', () => {
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

    expect(filter.offsetMs).toBe(0);
    expect(filter.add({ offsetMs: 9_000, rttMs: 20 })).toBe(true);
    expect(filter.offsetMs).toBe(0);
    filter.add({ offsetMs: 9_100, rttMs: 20 });
    expect(filter.offsetMs).toBe(9_050);
  });

  it('ignores null samples', () => {
    const filter = new ClockOffsetFilter();
    filter.add({ offsetMs: 100, rttMs: 20 });

    expect(filter.add(null)).toBe(false);
    expect(filter.count).toBe(1);
    expect(filter.offsetMs).toBe(0);
  });

  it('keeps the last 8 samples and takes their median', () => {
    const filter = new ClockOffsetFilter();
    for (let index = 1; index <= 10; index += 1) {
      filter.add({ offsetMs: index, rttMs: 20 });
    }

    expect(filter.count).toBe(CLOCK_SYNC_SAMPLE_COUNT);
    expect(filter.window.map((sample) => sample.offsetMs)).toEqual([3, 4, 5, 6, 7, 8, 9, 10]);
    // Median of the even-sized window: (6 + 7) / 2.
    expect(filter.offsetMs).toBe(6.5);
  });

  it('tolerates a single outlier inside the window', () => {
    const filter = new ClockOffsetFilter();
    for (const offsetMs of [100, 101, 102, 103, 104, 105, 106]) {
      filter.add({ offsetMs, rttMs: 20 });
    }
    filter.add({ offsetMs: 10_000, rttMs: 20 });

    expect(filter.offsetMs).toBe(103.5);
  });
});

describe('ClockSyncSampler', () => {
  it('computes an offset sample from a Ping/Pong exchange', async () => {
    let now = 10_000;
    const pings: number[] = [];
    const sampler = new ClockSyncSampler({
      ping: async (t1) => {
        pings.push(t1);
        const rttMs = 40;
        now += rttMs;
        // Symmetric delay: the server is exactly 250 ms ahead of the local clock.
        return pong(t1, t1 + rttMs / 2 + 250);
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
      ping: async (t1) => {
        pings += 1;
        return pong(t1, t1 + 100);
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

  it('discards an in-flight sample after stop()', async () => {
    vi.useFakeTimers();
    let resolvePong: ((value: Pong) => void) | null = null;
    const onChange = vi.fn();
    const sampler = new ClockSyncSampler({
      ping: (t1) => new Promise<Pong>((resolve) => {
        resolvePong = (value) => resolve({ ...value, t1 });
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
      ping: (t1) => new Promise<Pong>((resolve) => {
        resolvers.push((value) => resolve({ ...value, t1 }));
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
