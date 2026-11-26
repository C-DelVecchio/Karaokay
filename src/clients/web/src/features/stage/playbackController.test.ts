import { describe, expect, it } from 'vitest';
import { MediaSource, PlaybackStatus, type PlaybackStateDto } from '../../lib/signalr/protocol';
import {
  RESTART_AUDIO_MIN_MS,
  RESTART_POSITION_MAX_MS,
  authoritativePositionMs,
  decidePlayback,
  planAudioStart,
  type PlaybackDecisionInput,
} from './playbackController';

const ANCHOR = '2026-11-25T12:00:00.000Z';
const ANCHOR_MS = Date.parse(ANCHOR);

function playback(overrides: Partial<PlaybackStateDto> = {}): PlaybackStateDto {
  return {
    status: PlaybackStatus.Playing,
    currentQueueItemId: 'queue-1',
    currentSong: {
      id: 'song-1',
      title: 'Twinkle Variations',
      artist: 'Synthetic',
      durationMs: 30_000,
      source: MediaSource.Multitrack,
      thumbnailUrl: null,
    },
    positionMs: 0,
    timeAnchorUtc: ANCHOR,
    guideVocalVolume: 0.6,
    countdownRemainingBeats: null,
    ...overrides,
  };
}

function decisionInput(overrides: Partial<PlaybackDecisionInput> = {}): PlaybackDecisionInput {
  return {
    playback: playback(),
    nowMs: ANCHOR_MS,
    loaded: { queueItemId: 'queue-1', songId: 'song-1' },
    enginePlaying: false,
    enginePositionMs: 0,
    audioUnlocked: true,
    ...overrides,
  };
}

describe('authoritativePositionMs', () => {
  it('extrapolates a Playing anchor and folds paused positions', () => {
    expect(authoritativePositionMs(playback({ positionMs: 1_000 }), ANCHOR_MS + 2_500))
      .toBe(3_500);
    expect(authoritativePositionMs(
      playback({ status: PlaybackStatus.Paused, positionMs: 1_000 }),
      ANCHOR_MS + 2_500,
    )).toBe(1_000);
  });

  it('clamps negative elapsed time and invalid anchors to the folded position', () => {
    expect(authoritativePositionMs(playback({ positionMs: 500 }), ANCHOR_MS - 2_000)).toBe(500);
    expect(authoritativePositionMs(
      playback({ positionMs: 500, timeAnchorUtc: 'not-a-date' }),
      ANCHOR_MS + 2_000,
    )).toBe(500);
    expect(authoritativePositionMs(null, ANCHOR_MS)).toBe(0);
  });
});

describe('planAudioStart', () => {
  it('starts at zero for a fresh song', () => {
    expect(planAudioStart(playback(), ANCHOR_MS, 0, 30_000)).toEqual({
      expectedMs: 0,
      audioPositionMs: 0,
      seekOffsetMs: 0,
      startOffsetMs: 0,
    });
  });

  it('joins a song already in progress at the authoritative offset', () => {
    const plan = planAudioStart(playback(), ANCHOR_MS + 12_000, 0, 30_000);

    expect(plan.expectedMs).toBe(12_000);
    expect(plan.seekOffsetMs).toBe(12_000);
    expect(plan.startOffsetMs).toBe(12_000);
  });

  it('expresses a resume as the seek offset between the pause clock and the authority', () => {
    const resumedAt = ANCHOR_MS + 5_000;
    const processedAt = resumedAt + 40;
    const plan = planAudioStart(
      playback({ positionMs: 5_000, timeAnchorUtc: new Date(resumedAt).toISOString() }),
      processedAt,
      5_000,
      30_000,
    );

    expect(plan.expectedMs).toBe(5_040);
    expect(plan.seekOffsetMs).toBe(40);
    expect(plan.startOffsetMs).toBe(5_040);
  });

  it('keeps the seek target off the end of a known buffer', () => {
    const plan = planAudioStart(playback({ positionMs: 0 }), ANCHOR_MS + 31_000, 0, 30_000);

    expect(plan.startOffsetMs).toBe(29_950);
    expect(plan.seekOffsetMs).toBe(31_000);
  });
});

describe('planAudioStart under simulated latency', () => {
  it('stays within the ±150 ms budget with an 80 ms RTT residual', () => {
    // The stage's local clock is 500 ms behind the server. An 80 ms round trip leaves at most
    // RTT/2 = 40 ms of offset-estimate error after the best-of-window filter.
    const trueOffsetMs = 500;
    const estimatedErrorMs = 40;
    const localNowMs = ANCHOR_MS + 8_000 - trueOffsetMs;
    const stageServerNow = localNowMs + trueOffsetMs + estimatedErrorMs;

    const plan = planAudioStart(playback(), stageServerNow, 0, 30_000);
    const trueExpectedMs = 8_000;

    expect(Math.abs(plan.startOffsetMs - trueExpectedMs)).toBeLessThanOrEqual(150);
    expect(plan.seekOffsetMs).toBe(8_040);
  });

  it('does not accumulate correction when the stage joins mid-song', () => {
    const first = planAudioStart(playback(), ANCHOR_MS + 4_000, 0, 30_000);
    const second = planAudioStart(playback(), ANCHOR_MS + 9_000, first.startOffsetMs, 30_000);

    // A later reconciliation uses the fresh authority, not the first offset added twice.
    expect(second.startOffsetMs).toBe(9_000);
    expect(second.seekOffsetMs).toBe(5_000);
  });
});

describe('decidePlayback', () => {
  it('is idle without a current room item and defers YouTube songs to the video surface', () => {
    expect(decidePlayback(decisionInput({ playback: null })).kind).toBe('idle');
    expect(decidePlayback(decisionInput({
      playback: playback({
        currentSong: {
          id: 'yt-1',
          title: 'Video',
          artist: null,
          durationMs: 60_000,
          source: MediaSource.YouTube,
          thumbnailUrl: null,
        },
      }),
    })).kind).toBe('video');
  });

  it('creates and decodes nothing before the audio gesture', () => {
    expect(decidePlayback(decisionInput({ audioUnlocked: false })).kind).toBe('await-unlock');
  });

  it('loads a not-yet-loaded library song, including paused items', () => {
    expect(decidePlayback(decisionInput({ loaded: null })).kind).toBe('load');
    expect(decidePlayback(decisionInput({
      loaded: null,
      playback: playback({ status: PlaybackStatus.Paused, positionMs: 2_000 }),
    })).kind).toBe('load');
  });

  it('waits through Countdown instead of starting early', () => {
    const decision = decidePlayback(decisionInput({
      playback: playback({ status: PlaybackStatus.Countdown, countdownRemainingBeats: 4 }),
    }));

    expect(decision).toMatchObject({
      kind: 'countdown',
      countdownBeats: 4,
    });
  });

  it('starts a loaded Playing item at the authoritative offset', () => {
    const decision = decidePlayback(decisionInput({ nowMs: ANCHOR_MS + 3_000 }));

    expect(decision.kind).toBe('start');
    if (decision.kind === 'start') {
      expect(decision.plan.startOffsetMs).toBe(3_000);
      expect(decision.restart).toBe(false);
    }
  });

  it('holds the audio clock while a Playing song is already aligned', () => {
    const decision = decidePlayback(decisionInput({
      nowMs: ANCHOR_MS + 3_000,
      enginePlaying: true,
      enginePositionMs: 2_980,
    }));

    expect(decision.kind).toBe('hold');
  });

  it('recognises an explicit restart at position zero without a periodic re-slave', () => {
    const restart = decidePlayback(decisionInput({
      playback: playback({ positionMs: 0, timeAnchorUtc: '2026-11-25T12:00:10.000Z' }),
      nowMs: ANCHOR_MS + 10_000,
      enginePlaying: true,
      enginePositionMs: 12_000,
    }));

    expect(restart.kind).toBe('start');
    if (restart.kind === 'start') {
      expect(restart.restart).toBe(true);
      expect(restart.plan.startOffsetMs).toBeLessThan(RESTART_POSITION_MAX_MS);
    }

    // Ordinary mid-song drift is never corrected on the audio path.
    const drifting = decidePlayback(decisionInput({
      nowMs: ANCHOR_MS + 9_000,
      enginePlaying: true,
      enginePositionMs: 8_000,
    }));
    expect(drifting.kind).toBe('hold');
    expect(RESTART_AUDIO_MIN_MS).toBe(1_000);
  });

  it('pauses the engine only when it is actually playing, then holds paused', () => {
    const paused = playback({ status: PlaybackStatus.Paused, positionMs: 5_000 });
    expect(decidePlayback(decisionInput({
      playback: paused,
      enginePlaying: true,
      enginePositionMs: 5_000,
    })).kind).toBe('pause');

    expect(decidePlayback(decisionInput({
      playback: paused,
      enginePlaying: false,
      enginePositionMs: 5_000,
    })).kind).toBe('hold');
  });

  it('ends and reports even when the stage never loaded the item', () => {
    const decision = decidePlayback(decisionInput({
      playback: playback({ status: PlaybackStatus.SongEnded }),
      loaded: null,
    }));

    expect(decision).toMatchObject({ kind: 'ended', queueItemId: 'queue-1' });
  });

  it('stops when the room leaves playback', () => {
    expect(decidePlayback(decisionInput({
      playback: playback({ status: PlaybackStatus.Idle, currentQueueItemId: null }),
      loaded: null,
    })).kind).toBe('idle');
    expect(decidePlayback(decisionInput({
      playback: playback({ status: PlaybackStatus.AwaitingDownload }),
    })).kind).toBe('stop');
  });
});
