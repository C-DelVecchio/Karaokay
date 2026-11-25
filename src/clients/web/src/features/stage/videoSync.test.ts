import { beforeEach, describe, expect, it } from 'vitest';
import type { PlaybackStateDto } from '../../lib/signalr/protocol';
import { MediaSource, PlaybackStatus } from '../../lib/signalr/protocol';
import {
  HARD_NUDGE_MS,
  expectedPositionMs,
  isYouTubePlayback,
  videoCorrection,
  videoUrlForSong,
} from './videoSync';
import { stageTestClock } from './WebAudioEngine';

const anchor = '2026-01-01T00:00:10.000Z';

function playback(overrides: Partial<PlaybackStateDto> = {}): PlaybackStateDto {
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
    timeAnchorUtc: anchor,
    guideVocalVolume: 0.6,
    countdownRemainingBeats: null,
    ...overrides,
  };
}

beforeEach(() => {
  stageTestClock.clear();
});

describe('isYouTubePlayback', () => {
  it('selects video mode only for a current YouTube song', () => {
    expect(isYouTubePlayback(playback())).toBe(true);
    expect(isYouTubePlayback(playback({
      currentSong: { ...playback().currentSong!, source: MediaSource.Multitrack },
    }))).toBe(false);
    expect(isYouTubePlayback(playback({ currentQueueItemId: null }))).toBe(false);
    expect(isYouTubePlayback(null)).toBe(false);
  });

  it('builds the range-enabled media URL from the song id', () => {
    expect(videoUrlForSong('abc-123')).toBe('/api/songs/abc-123/video');
  });
});

describe('expectedPositionMs', () => {
  it('extrapolates from the server anchor while playing', () => {
    const anchorMs = Date.parse(anchor);
    expect(expectedPositionMs(playback(), anchorMs + 1_500)).toBe(6_500);
  });

  it('does not extrapolate while paused or idle', () => {
    const anchorMs = Date.parse(anchor);
    expect(expectedPositionMs(
      playback({ status: PlaybackStatus.Paused }),
      anchorMs + 4_000,
    )).toBe(5_000);
  });

  it('uses the installed test clock verbatim', () => {
    stageTestClock.set(42_500);
    expect(expectedPositionMs(playback(), Date.parse(anchor))).toBe(42_500);
  });
});

describe('videoCorrection', () => {
  it('hard-seeks when drift exceeds the 120 ms threshold', () => {
    const actual = 5_000 - HARD_NUDGE_MS - 100;
    const correction = videoCorrection(playback(), actual, Date.parse(anchor));

    expect(correction.expectedMs).toBe(5_000);
    expect(correction.driftMs).toBe(HARD_NUDGE_MS + 100);
    expect(correction.seekMs).toBe(5_000);
    expect(correction.rate).toBe(1);
  });

  it('soft-nudges playbackRate for sub-120 ms drift', () => {
    const correction = videoCorrection(playback(), 5_070, Date.parse(anchor));

    expect(correction.seekMs).toBeNull();
    // Video is 70 ms ahead of the authority, so it slows down slightly.
    expect(correction.rate).toBeLessThan(1);
    expect(correction.rate).toBeGreaterThan(0.99);
  });

  it('makes no correction inside the dead band', () => {
    const correction = videoCorrection(playback(), 5_020, Date.parse(anchor));

    expect(correction.seekMs).toBeNull();
    expect(correction.rate).toBe(1);
  });
});
