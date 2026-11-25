import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import type { PlaybackStateDto } from '../../lib/signalr/protocol';
import { PlaybackStatus } from '../../lib/signalr/protocol';
import type { WebAudioEngine } from './WebAudioEngine';
import {
  expectedPositionMs,
  isYouTubePlayback,
  videoCorrection,
  videoUrlForSong,
} from './videoSync';

declare global {
  interface Window {
    __karaokayVideo?: {
      mode: 'video' | 'placeholder';
      songId: string | null;
      attached: boolean;
      notice: string | null;
      currentTime: () => number;
      expectedMs: () => number;
      paused: () => boolean;
      /** Test hook: move the element away from the authoritative position by `ms`. */
      injectDrift: (ms: number) => void;
      /** Test hook: force one reconciliation pass. */
      syncNow: () => void;
    };
  }
}

export type StageVideoMode = 'video' | 'placeholder';

interface UseStageVideoOptions {
  playback: PlaybackStateDto | null;
  audioUnlocked: boolean;
  engine: WebAudioEngine;
  onSongEnded: (queueItemId: string) => void;
}

export interface StageVideoHandle {
  videoRef: RefObject<HTMLVideoElement | null>;
  mode: StageVideoMode;
  songId: string | null;
  notice: string | null;
  dismissNotice: () => void;
  /** Called from the unlock click so the muted element starts inside the gesture. */
  primeForUnlock: () => void;
  /** Retry after an autoplay block, also from a gesture. */
  retryPlayback: () => void;
}

const SYNC_INTERVAL_MS = 500;
const SOFT_NUDGE_RESET_MS = 5_000;

function clampSeekSeconds(video: HTMLVideoElement, seconds: number): number {
  const duration = video.duration;
  const upper = Number.isFinite(duration) && duration > 0 ? Math.max(0, duration - 0.05) : seconds;
  return Math.min(upper, Math.max(0, seconds));
}

function releaseVideo(video: HTMLVideoElement): void {
  video.pause();
  video.muted = true;
  video.playbackRate = 1;
  video.removeAttribute('src');
  video.load();
}

/**
 * Owns the stage's single persistent `<video>`. The element never unmounts, so the
 * `MediaElementAudioSourceNode` created on unlock stays valid across every YouTube song and every
 * audio↔video transition; transitions only pause, mute, and clear `src`.
 */
export function useStageVideo({
  playback,
  audioUnlocked,
  engine,
  onSongEnded,
}: UseStageVideoOptions): StageVideoHandle {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const playbackRef = useRef<PlaybackStateDto | null>(playback);
  const onSongEndedRef = useRef(onSongEnded);
  const modeRef = useRef<StageVideoMode>('placeholder');
  const songIdRef = useRef<string | null>(null);
  const noticeRef = useRef<string | null>(null);
  const syncNowRef = useRef<() => void>(() => undefined);
  const reportedEndsRef = useRef(new Set<string>());
  const [notice, setNotice] = useState<string | null>(null);

  const songId = playback?.currentSong?.id ?? null;
  const mode: StageVideoMode = isYouTubePlayback(playback) ? 'video' : 'placeholder';

  playbackRef.current = playback;
  onSongEndedRef.current = onSongEnded;
  modeRef.current = mode;
  songIdRef.current = mode === 'video' ? songId : null;

  const updateNotice = useCallback((next: string | null) => {
    if (noticeRef.current === next) {
      return;
    }

    noticeRef.current = next;
    setNotice(next);
  }, []);

  // Source selection and cleanup. `src` is only ever changed on this one element.
  useEffect(() => {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    if (mode !== 'video' || !songId) {
      releaseVideo(video);
      return;
    }

    const nextUrl = videoUrlForSong(songId);
    if (video.getAttribute('src') !== nextUrl) {
      video.pause();
      video.muted = true;
      video.setAttribute('src', nextUrl);
      video.load();
    }
  }, [mode, songId]);

  // Route the element after the one-time gesture. Idempotent on every re-render.
  useEffect(() => {
    const video = videoRef.current;
    if (!video || mode !== 'video' || !audioUnlocked) {
      return;
    }

    if (engine.attachMediaElement(video)) {
      video.muted = false;
    }
  }, [audioUnlocked, engine, mode, songId]);

  // Playback reconciliation + the video's own lifecycle events.
  useEffect(() => {
    const video = videoRef.current;
    if (!video || mode !== 'video' || !audioUnlocked) {
      return undefined;
    }

    let softResetTimer: number | null = null;

    const sync = () => {
      const current = playbackRef.current;
      if (!current || modeRef.current !== 'video') {
        return;
      }

      if (video.readyState < 1) {
        if (current.status === PlaybackStatus.Playing && video.paused) {
          void video.play().catch(() => undefined);
        }
        return;
      }

      const actualMs = video.currentTime * 1000;
      const correction = videoCorrection(current, actualMs, Date.now());
      const shouldPlay = current.status === PlaybackStatus.Playing;

      if (correction.seekMs !== null) {
        video.currentTime = clampSeekSeconds(video, correction.seekMs / 1000);
        video.playbackRate = 1;
      } else if (shouldPlay && correction.rate !== 1) {
        video.playbackRate = correction.rate;
        if (softResetTimer !== null) {
          window.clearTimeout(softResetTimer);
        }
        softResetTimer = window.setTimeout(() => {
          video.playbackRate = 1;
          softResetTimer = null;
        }, SOFT_NUDGE_RESET_MS);
      } else {
        video.playbackRate = 1;
      }

      if (shouldPlay) {
        if (video.paused) {
          void video.play().catch((error: unknown) => {
            if (error instanceof Error && error.name === 'NotAllowedError') {
              updateNotice('Tap the stage to start the video.');
            }
          });
        }
      } else if (!video.paused) {
        video.pause();
      }

      if (current.status === PlaybackStatus.Paused && correction.seekMs !== null) {
        video.currentTime = clampSeekSeconds(video, correction.seekMs / 1000);
      }
    };

    const handleEnded = () => {
      const current = playbackRef.current;
      const queueItemId = current?.currentQueueItemId;
      if (!queueItemId || !isYouTubePlayback(current) || reportedEndsRef.current.has(queueItemId)) {
        return;
      }

      reportedEndsRef.current.add(queueItemId);
      onSongEndedRef.current(queueItemId);
    };

    const handleError = () => {
      if (modeRef.current === 'video' && video.getAttribute('src')) {
        updateNotice('The video could not be played.');
      }
    };

    syncNowRef.current = sync;
    sync();
    const interval = window.setInterval(sync, SYNC_INTERVAL_MS);
    video.addEventListener('loadedmetadata', sync);
    video.addEventListener('ended', handleEnded);
    video.addEventListener('error', handleError);

    return () => {
      window.clearInterval(interval);
      if (softResetTimer !== null) {
        window.clearTimeout(softResetTimer);
      }
      video.playbackRate = 1;
      video.removeEventListener('loadedmetadata', sync);
      video.removeEventListener('ended', handleEnded);
      video.removeEventListener('error', handleError);
      syncNowRef.current = () => undefined;
    };
  }, [audioUnlocked, engine, mode, updateNotice]);

  // Keep playbackRef fresh for the interval/event handlers and apply pause/resume/seek promptly.
  useEffect(() => {
    playbackRef.current = playback;
    syncNowRef.current();
  }, [playback]);

  // A leave-video transition must not carry a stale autoplay error banner.
  useEffect(() => {
    if (mode === 'placeholder') {
      updateNotice(null);
    }
  }, [mode, updateNotice]);

  // Debug/test surface (mirrors `__karaokayAudio`/`__karaokayCanvas`).
  useEffect(() => {
    window.__karaokayVideo = {
      get mode() {
        return modeRef.current;
      },
      get songId() {
        return songIdRef.current;
      },
      get attached() {
        const video = videoRef.current;
        return Boolean(video && engine.hasMediaElementSource(video));
      },
      get notice() {
        return noticeRef.current;
      },
      currentTime: () => videoRef.current?.currentTime ?? 0,
      expectedMs: () => expectedPositionMs(playbackRef.current, Date.now()),
      paused: () => videoRef.current?.paused ?? true,
      injectDrift: (ms: number) => {
        const video = videoRef.current;
        if (video) {
          video.currentTime = Math.max(0, video.currentTime + ms / 1000);
        }
      },
      syncNow: () => syncNowRef.current(),
    };

    return () => {
      delete window.__karaokayVideo;
    };
  }, [engine]);

  const primeForUnlock = useCallback(() => {
    const video = videoRef.current;
    if (!video || modeRef.current !== 'video' || !video.getAttribute('src')) {
      return;
    }

    // Muted playback is always autoplay-safe; the source node is attached immediately after the
    // AudioContext unlock resolves, then the element is unmuted into the master bus.
    video.muted = true;
    void video.play().catch(() => undefined);
  }, []);

  const retryPlayback = useCallback(() => {
    const video = videoRef.current;
    if (!video || modeRef.current !== 'video') {
      return;
    }

    if (audioUnlocked && engine.attachMediaElement(video)) {
      video.muted = false;
    }

    void video.play()
      .then(() => updateNotice(null))
      .catch(() => updateNotice('The video could not be played.'));
    syncNowRef.current();
  }, [audioUnlocked, engine, updateNotice]);

  return {
    videoRef,
    mode,
    songId: songIdRef.current,
    notice,
    dismissNotice: () => updateNotice(null),
    primeForUnlock,
    retryPlayback,
  };
}
