import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import type { PlaybackStateDto } from '../../lib/signalr/protocol';
import { PlaybackStatus } from '../../lib/signalr/protocol';
import type { WebAudioEngine } from './WebAudioEngine';
import {
  expectedPositionMs,
  isYouTubePlayback,
  playRejectionNotice,
  videoCorrection,
  videoFailureNotice,
  videoUrlForSong,
  type MediaElementSnapshot,
  type StageVideoNotice,
  type StageVideoNoticeKind,
} from './videoSync';

declare global {
  interface Window {
    __karaokayVideo?: {
      mode: 'video' | 'placeholder';
      songId: string | null;
      attached: boolean;
      notice: string | null;
      problem: StageVideoNoticeKind | null;
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
  /**
   * Local time corrected onto the server wall clock (docs/ARCHITECTURE.md §5.3 layer 1). Defaults
   * to the raw local clock so a stage without a live room keeps today's behavior.
   */
  serverNow?: () => number;
  onSongEnded: (queueItemId: string) => void;
}

export interface StageVideoHandle {
  videoRef: RefObject<HTMLVideoElement | null>;
  mode: StageVideoMode;
  songId: string | null;
  notice: StageVideoNotice | null;
  dismissNotice: () => void;
  /** Called from the unlock click so the muted element starts inside the gesture. */
  primeForUnlock: () => void;
  /** Retry after an autoplay block or a failed source load, also from a gesture. */
  retryPlayback: () => void;
}

const SYNC_INTERVAL_MS = 500;
const SOFT_NUDGE_RESET_MS = 5_000;

function snapshotOf(video: HTMLVideoElement, videoMode: boolean): MediaElementSnapshot {
  return {
    videoMode,
    hasSource: Boolean(video.getAttribute('src')),
    errorCode: video.error?.code ?? null,
    networkState: video.networkState,
    readyState: video.readyState,
  };
}

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
  serverNow,
  onSongEnded,
}: UseStageVideoOptions): StageVideoHandle {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const playbackRef = useRef<PlaybackStateDto | null>(playback);
  const serverNowRef = useRef<() => number>(serverNow ?? Date.now);
  const onSongEndedRef = useRef(onSongEnded);
  const modeRef = useRef<StageVideoMode>('placeholder');
  const songIdRef = useRef<string | null>(null);
  const noticeRef = useRef<StageVideoNotice | null>(null);
  const failureDismissedRef = useRef(false);
  const syncNowRef = useRef<() => void>(() => undefined);
  const reportedEndsRef = useRef(new Set<string>());
  const [notice, setNotice] = useState<StageVideoNotice | null>(null);

  const songId = playback?.currentSong?.id ?? null;
  const mode: StageVideoMode = isYouTubePlayback(playback) ? 'video' : 'placeholder';

  playbackRef.current = playback;
  serverNowRef.current = serverNow ?? Date.now;
  onSongEndedRef.current = onSongEnded;
  modeRef.current = mode;
  songIdRef.current = mode === 'video' ? songId : null;

  const updateNotice = useCallback((next: StageVideoNotice | null) => {
    const current = noticeRef.current;
    if (
      current === next
      || (current && next && current.kind === next.kind && current.message === next.message)
    ) {
      return;
    }

    // A dismissed unavailable banner stays dismissed for this song; the 500 ms reconciliation
    // pass would otherwise resurrect it on every tick.
    if (next?.kind === 'unavailable' && failureDismissedRef.current) {
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
      failureDismissedRef.current = false;
      releaseVideo(video);
      return;
    }

    const nextUrl = videoUrlForSong(songId);
    if (video.getAttribute('src') !== nextUrl) {
      // A new source deserves a fresh verdict: drop any failure carried over from the last song.
      failureDismissedRef.current = false;
      updateNotice(null);
      video.pause();
      video.muted = true;
      video.setAttribute('src', nextUrl);
      video.load();
    }
  }, [mode, songId, updateNotice]);

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

  // Playback reconciliation + the video's own lifecycle events. This only runs after the one-time
  // audio unlock; that is also where an error that fired while the gate was up is re-evaluated.
  useEffect(() => {
    const video = videoRef.current;
    if (!video || mode !== 'video' || !audioUnlocked) {
      return undefined;
    }

    let softResetTimer: number | null = null;

    const reportPlayRejection = (error: unknown) => {
      const next = playRejectionNotice(error instanceof Error ? error.name : null);
      if (next) {
        updateNotice(next);
      }
    };

    const sync = () => {
      const current = playbackRef.current;
      if (!current || modeRef.current !== 'video') {
        return;
      }

      // The error event can fire before the element has a listener (e.g. the room starts while
      // the audio gate is still up). The immediate `sync()` below runs right after the listeners
      // attach at unlock time, so the element's error state is recovered then; the 500 ms interval
      // keeps the classification current for errors that happen during playback.
      const failure = videoFailureNotice(snapshotOf(video, true));
      if (failure) {
        updateNotice(failure);
        return;
      }

      if (video.readyState < 1) {
        if (current.status === PlaybackStatus.Playing && video.paused) {
          void video.play().catch(reportPlayRejection);
        }
        return;
      }

      const actualMs = video.currentTime * 1000;
      const correction = videoCorrection(current, actualMs, serverNowRef.current());
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
          void video.play().catch(reportPlayRejection);
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
      const next = videoFailureNotice(snapshotOf(video, modeRef.current === 'video'));
      if (next) {
        updateNotice(next);
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

  // A leave-video transition must not carry a stale autoplay/failure banner.
  useEffect(() => {
    if (mode === 'placeholder') {
      failureDismissedRef.current = false;
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
        return noticeRef.current?.message ?? null;
      },
      get problem() {
        return noticeRef.current?.kind ?? null;
      },
      currentTime: () => videoRef.current?.currentTime ?? 0,
      expectedMs: () => expectedPositionMs(playbackRef.current, serverNowRef.current()),
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

    failureDismissedRef.current = false;
    if (noticeRef.current?.kind === 'unavailable') {
      // A failed fetch leaves the element in an error state; `load()` is the only way to ask the
      // same URL again after the backend has recovered.
      video.load();
    }

    void video.play()
      .then(() => updateNotice(null))
      .catch((error: unknown) => {
        const next = playRejectionNotice(error instanceof Error ? error.name : null);
        if (next) {
          updateNotice(next);
        }
      });
    syncNowRef.current();
  }, [audioUnlocked, engine, updateNotice]);

  const dismissNotice = useCallback(() => {
    if (noticeRef.current?.kind === 'unavailable') {
      failureDismissedRef.current = true;
    }

    updateNotice(null);
  }, [updateNotice]);

  return {
    videoRef,
    mode,
    songId: songIdRef.current,
    notice,
    dismissNotice,
    primeForUnlock,
    retryPlayback,
  };
}
