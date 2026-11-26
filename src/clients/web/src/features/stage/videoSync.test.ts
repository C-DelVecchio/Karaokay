import { beforeEach, describe, expect, it } from 'vitest';
import type { PlaybackStateDto } from '../../lib/signalr/protocol';
import { MediaSource, PlaybackStatus } from '../../lib/signalr/protocol';
import {
  HARD_NUDGE_MS,
  MEDIA_ERR_ABORTED,
  NETWORK_NO_SOURCE,
  NO_SOURCE_FAILURE_MS,
  VIDEO_AUTOPLAY_MESSAGE,
  VIDEO_UNAVAILABLE_MESSAGE,
  expectedPositionMs,
  isNoSourceStall,
  isYouTubePlayback,
  playRejectionNotice,
  videoCorrection,
  videoFailureNotice,
  videoLoadRecovered,
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

describe('video failure notices', () => {
  function snapshot(overrides: Partial<Parameters<typeof videoFailureNotice>[0]> = {}) {
    return {
      videoMode: true,
      hasSource: true,
      errorCode: null,
      networkState: 2, // NETWORK_LOADING
      readyState: 0,
      ...overrides,
    };
  }

  it('presents the unavailable state for a media error while a YouTube song is Playing', () => {
    const playing = playback({ status: PlaybackStatus.Playing });
    const notice = videoFailureNotice(snapshot({
      videoMode: isYouTubePlayback(playing),
      errorCode: 4, // MEDIA_ERR_SRC_NOT_SUPPORTED
      networkState: NETWORK_NO_SOURCE,
    }));

    expect(notice).toEqual({ kind: 'unavailable', message: VIDEO_UNAVAILABLE_MESSAGE });
    expect(notice?.message).toContain('download failed');
  });

  it.each([2, 3, 4])('presents unavailable for MediaError code %i', (code) => {
    expect(videoFailureNotice(snapshot({ errorCode: code }))?.kind).toBe('unavailable');
  });

  it('ignores MEDIA_ERR_ABORTED, which is a superseded load rather than a failure', () => {
    expect(videoFailureNotice(snapshot({
      errorCode: MEDIA_ERR_ABORTED,
      networkState: NETWORK_NO_SOURCE,
      readyState: 0,
    }))).toBeNull();
  });

  it('does not classify the pre-load window right after src assignment', () => {
    // Chromium reports NETWORK_NO_SOURCE/readyState 0 synchronously after `src` + `load()`,
    // before the fetch starts. One or two observations of that state are not a failure.
    const preload = snapshot({ networkState: NETWORK_NO_SOURCE, readyState: 0 });

    expect(isNoSourceStall(preload)).toBe(true);
    expect(videoFailureNotice(preload)).toBeNull();
    expect(videoFailureNotice(preload, NO_SOURCE_FAILURE_MS - 1)).toBeNull();
  });

  it('maps a durable 404-style no-source stall without a MediaError code', () => {
    const stalled = snapshot({ networkState: NETWORK_NO_SOURCE, readyState: 0 });

    expect(videoFailureNotice(stalled, NO_SOURCE_FAILURE_MS)?.kind).toBe('unavailable');
  });

  it('treats a decoded source as recovery from an unavailable verdict', () => {
    const playing = snapshot({ networkState: 2, readyState: 4 });

    expect(videoLoadRecovered(playing)).toBe(true);
    expect(videoLoadRecovered(snapshot({ readyState: 0 }))).toBe(false);
    expect(videoLoadRecovered(snapshot({ errorCode: 4, readyState: 4 }))).toBe(false);
  });

  it('recovers a transient no-source verdict once playback starts', () => {
    const stalled = snapshot({ networkState: NETWORK_NO_SOURCE, readyState: 0 });
    expect(videoFailureNotice(stalled, NO_SOURCE_FAILURE_MS)?.kind).toBe('unavailable');

    // The same element once metadata/playback arrives must no longer report a failure and is
    // flagged as recovered so the hook clears the outstanding card.
    const playing = snapshot({ networkState: 1, readyState: 4 });
    expect(videoFailureNotice(playing)).toBeNull();
    expect(videoLoadRecovered(playing)).toBe(true);
  });

  it('does not call a still-loading element a failure', () => {
    expect(videoFailureNotice(snapshot({ networkState: 2, readyState: 0 }))).toBeNull();
  });

  it('only reports failures for an active video-mode element with a source', () => {
    expect(videoFailureNotice(snapshot({ videoMode: false, errorCode: 4 }))).toBeNull();
    expect(videoFailureNotice(snapshot({ hasSource: false, errorCode: 4 }))).toBeNull();
  });

  it('keeps autoplay blocks separate from load failures', () => {
    expect(playRejectionNotice('NotAllowedError')).toEqual({
      kind: 'autoplay-blocked',
      message: VIDEO_AUTOPLAY_MESSAGE,
    });
    // The tap-to-start path is not a source failure, so the reconciliation net must stay quiet.
    expect(videoFailureNotice(snapshot({ networkState: 1, readyState: 0 }))).toBeNull();
  });

  it('maps NotSupportedError play rejections to unavailable and ignores aborts', () => {
    expect(playRejectionNotice('NotSupportedError')?.kind).toBe('unavailable');
    expect(playRejectionNotice('NotReadableError')?.kind).toBe('unavailable');
    expect(playRejectionNotice('AbortError')).toBeNull();
    expect(playRejectionNotice(null)).toBeNull();
  });
});
