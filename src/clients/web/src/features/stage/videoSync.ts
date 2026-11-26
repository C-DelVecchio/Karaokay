import {
  MediaSource,
  PlaybackStatus,
  type PlaybackStateDto,
} from '../../lib/signalr/protocol';
import { stageTestClock } from './WebAudioEngine';

/**
 * Pure video-mode decisions for the stage (docs/ARCHITECTURE.md §5.3).
 *
 * `MediaSource.YouTube` (1) on the authoritative `PlaybackStateDto.currentSong` is the single
 * signal that chooses `<video>` over the Web Audio stem mixer. Multitrack items never attach a
 * media element, so the demo-track flow stays byte-for-byte unchanged.
 */

export const HARD_NUDGE_MS = 120;
export const SOFT_NUDGE_MS = 40;
export const SOFT_NUDGE_RATE = 0.005;

/** `HTMLMediaElement.NETWORK_NO_SOURCE`: resource selection finished with no usable source. */
export const NETWORK_NO_SOURCE = 3;

/** `MediaError.MEDIA_ERR_ABORTED`: a load superseded by a source change, not a failure. */
export const MEDIA_ERR_ABORTED = 1;

export type StageVideoNoticeKind = 'unavailable' | 'autoplay-blocked';

export interface StageVideoNotice {
  kind: StageVideoNoticeKind;
  message: string;
}

/** Deliberately explicit: a failed cache download must never read as a silent black stage. */
export const VIDEO_UNAVAILABLE_MESSAGE = "Video unavailable — this song's download failed.";
export const VIDEO_AUTOPLAY_MESSAGE = 'Tap the stage to start the video.';

export interface MediaElementSnapshot {
  /** True while the element is the surface for an active YouTube playback item. */
  videoMode: boolean;
  /** `src` is only ever set for an active YouTube item (see `useStageVideo`). */
  hasSource: boolean;
  /** `video.error.code`, or null when the element reports no `MediaError`. */
  errorCode: number | null;
  networkState: number;
  readyState: number;
}

/**
 * Detects a source that cannot play at all. A 404 from `/api/songs/{id}/video` surfaces as a
 * `MediaError` (usually `MEDIA_ERR_SRC_NOT_SUPPORTED`) and/or `NETWORK_NO_SOURCE` with an empty
 * ready state. Codes 2–4 are permanent for this `src`, unlike buffering (`NETWORK_LOADING`);
 * `MEDIA_ERR_ABORTED` (1) only means a newer load superseded this one and is ignored.
 */
export function videoFailureNotice(snapshot: MediaElementSnapshot): StageVideoNotice | null {
  if (!snapshot.videoMode || !snapshot.hasSource) {
    return null;
  }

  if (snapshot.errorCode !== null) {
    return snapshot.errorCode === MEDIA_ERR_ABORTED
      ? null
      : { kind: 'unavailable', message: VIDEO_UNAVAILABLE_MESSAGE };
  }

  const failed = snapshot.networkState === NETWORK_NO_SOURCE && snapshot.readyState === 0;
  return failed ? { kind: 'unavailable', message: VIDEO_UNAVAILABLE_MESSAGE } : null;
}

/**
 * Maps a rejected `HTMLMediaElement.play()` onto the two user-visible outcomes. Autoplay policy
 * (`NotAllowedError`) is recoverable with a tap; a missing/undecodable source is not.
 */
export function playRejectionNotice(
  errorName: string | null | undefined,
): StageVideoNotice | null {
  if (errorName === 'NotAllowedError') {
    return { kind: 'autoplay-blocked', message: VIDEO_AUTOPLAY_MESSAGE };
  }

  if (errorName === 'NotSupportedError' || errorName === 'NotReadableError') {
    return { kind: 'unavailable', message: VIDEO_UNAVAILABLE_MESSAGE };
  }

  // AbortError and friends come from a superseded load/play and are not user-visible failures.
  return null;
}

export interface VideoCorrection {
  /** Absolute media position the video should be at, in ms. */
  expectedMs: number;
  /** Signed drift: expected − actual. Positive means the video is behind. */
  driftMs: number;
  /** Hard seek target, or null when a seek is not needed. */
  seekMs: number | null;
  /** Playback-rate multiplier to apply for a gentle nudge, or 1. */
  rate: number;
}

export function isYouTubePlayback(
  playback: PlaybackStateDto | null | undefined,
): boolean {
  return Boolean(
    playback?.currentQueueItemId
    && playback.currentSong
    && playback.currentSong.source === MediaSource.YouTube,
  );
}

export function videoUrlForSong(songId: string): string {
  return `/api/songs/${encodeURIComponent(songId)}/video`;
}

/**
 * Authoritative position extrapolation: `PositionMs + (now − TimeAnchorUtc)` while Playing;
 * paused/idle positions are already folded into `PositionMs`.
 *
 * The stage test clock, when installed, is the expected media position directly so Playwright can
 * drive deterministic video checks exactly like it drives the lyric renderer.
 */
export function expectedPositionMs(
  playback: PlaybackStateDto | null | undefined,
  nowMs: number,
): number {
  const testClock = stageTestClock.positionMs();
  if (testClock !== null) {
    return Math.max(0, testClock);
  }

  if (!playback) {
    return 0;
  }

  const base = Math.max(0, Number(playback.positionMs) || 0);
  if (playback.status !== PlaybackStatus.Playing) {
    return base;
  }

  const anchorMs = Date.parse(playback.timeAnchorUtc);
  if (!Number.isFinite(anchorMs)) {
    return base;
  }

  return Math.max(0, base + Math.max(0, nowMs - anchorMs));
}

/** Computes the doc's hard-seek / soft-nudge correction for one sample. */
export function videoCorrection(
  playback: PlaybackStateDto | null | undefined,
  actualMs: number,
  nowMs: number,
): VideoCorrection {
  const expectedMs = expectedPositionMs(playback, nowMs);
  const driftMs = expectedMs - actualMs;

  if (Math.abs(driftMs) > HARD_NUDGE_MS) {
    return { expectedMs, driftMs, seekMs: expectedMs, rate: 1 };
  }

  if (Math.abs(driftMs) > SOFT_NUDGE_MS) {
    return {
      expectedMs,
      driftMs,
      seekMs: null,
      rate: driftMs > 0 ? 1 + SOFT_NUDGE_RATE : 1 - SOFT_NUDGE_RATE,
    };
  }

  return { expectedMs, driftMs, seekMs: null, rate: 1 };
}
