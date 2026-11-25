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
