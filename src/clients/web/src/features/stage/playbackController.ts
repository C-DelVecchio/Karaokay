import { useCallback, useEffect, useRef, useState } from 'react';
import {
  PlaybackStatus,
  type PlaybackStateDto,
  type SongDto,
} from '../../lib/signalr/protocol';
import type { LyricsDocument } from './lyrics';
import { loadLibraryTrack, type LoadedLibraryTrack } from './songMedia';
import { isYouTubePlayback } from './videoSync';
import type { WebAudioEngine } from './WebAudioEngine';

/**
 * Authoritative room playback for the browser stage (docs/ARCHITECTURE.md §5.3 layer 2 and §7
 * Slice 4).
 *
 * The server is the wall-clock and command authority. The stage derives the expected media
 * position from `PlaybackStateDto` once when it starts or joins a song, seeks its own stems to that
 * position, and then treats its audio clock as ground truth. Audio is **never** periodically
 * re-slaved to the wall clock: only explicit transitions (start, restart, resume, item change) may
 * move the audio. That is the layer-2 contract; the video element is the one surface that gets
 * periodic hard/soft nudges (see `useStageVideo`).
 */

export type RoomPlaybackPhase =
  | 'idle'
  | 'awaiting-unlock'
  | 'loading'
  | 'countdown'
  | 'playing'
  | 'paused'
  | 'ended'
  | 'error';

export interface LoadedRoomTrack {
  queueItemId: string;
  songId: string;
}

export interface RoomPlaybackStatus {
  /** True while a room queue item (library song) owns the stage. */
  active: boolean;
  phase: RoomPlaybackPhase;
  queueItemId: string | null;
  song: SongDto | null;
  lyrics: LyricsDocument | null;
  countdownBeats: number | null;
  error: string | null;
  /** One-time `expectedMs − audioPositionMs` measured when the stage joined the song. */
  anchorCorrectionMs: number | null;
}

export function emptyRoomPlaybackStatus(): RoomPlaybackStatus {
  return {
    active: false,
    phase: 'idle',
    queueItemId: null,
    song: null,
    lyrics: null,
    countdownBeats: null,
    error: null,
    anchorCorrectionMs: null,
  };
}

/** A restart while already playing is only recognised at the very top of a song. */
export const RESTART_POSITION_MAX_MS = 1_000;
export const RESTART_AUDIO_MIN_MS = 1_000;

/** Keep the seek target off the exact buffer end so the source never starts past its duration. */
const AUDIO_END_MARGIN_MS = 50;

/** `PositionMs + (serverNow − TimeAnchorUtc)` while Playing; otherwise `PositionMs` is already folded. */
export function authoritativePositionMs(
  playback: PlaybackStateDto | null | undefined,
  nowMs: number,
): number {
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

export interface AudioStartPlan {
  /** Server-authoritative position at the calibration instant. */
  expectedMs: number;
  /** Local audio clock reading used for the one-time correction. */
  audioPositionMs: number;
  /** `expectedMs − audioPositionMs`, the one-time calibration correction. */
  anchorCorrectionMs: number;
  /** Buffer offset handed to `WebAudioEngine.start()`. */
  startOffsetMs: number;
}

/**
 * Plans a one-time audio (re)start at the authoritative position. Passing the current audio
 * position lets a resume express its correction explicitly; a fresh start passes 0.
 */
export function planAudioStart(
  playback: PlaybackStateDto | null | undefined,
  nowMs: number,
  audioPositionMs = 0,
  durationMs?: number | null,
): AudioStartPlan {
  const expectedMs = Math.max(0, authoritativePositionMs(playback, nowMs));
  const safeAudioPositionMs = Math.max(0, Number.isFinite(audioPositionMs) ? audioPositionMs : 0);
  const anchorCorrectionMs = expectedMs - safeAudioPositionMs;
  const duration = typeof durationMs === 'number' && Number.isFinite(durationMs) && durationMs > 0
    ? durationMs
    : null;
  const maxOffsetMs = duration !== null ? Math.max(0, duration - AUDIO_END_MARGIN_MS) : Number.POSITIVE_INFINITY;
  const startOffsetMs = Math.min(maxOffsetMs, Math.max(0, safeAudioPositionMs + anchorCorrectionMs));

  return { expectedMs, audioPositionMs: safeAudioPositionMs, anchorCorrectionMs, startOffsetMs };
}

export interface PlaybackDecisionInput {
  playback: PlaybackStateDto | null;
  nowMs: number;
  loaded: LoadedRoomTrack | null;
  enginePlaying: boolean;
  enginePositionMs: number;
  audioUnlocked: boolean;
}

export type PlaybackDecision =
  | { kind: 'idle' }
  | { kind: 'video' }
  | { kind: 'await-unlock'; song: SongDto }
  | { kind: 'load'; song: SongDto }
  | { kind: 'countdown'; song: SongDto; countdownBeats: number | null }
  | {
      kind: 'start';
      song: SongDto;
      restart: boolean;
      plan: AudioStartPlan;
    }
  | { kind: 'pause'; song: SongDto }
  | { kind: 'ended'; queueItemId: string; song: SongDto }
  | { kind: 'hold'; phase: 'playing' | 'paused'; song: SongDto }
  | { kind: 'stop' };

function songDurationMs(song: SongDto): number | null {
  const duration = Number(song.durationMs);
  return Number.isFinite(duration) && duration > 0 ? duration : null;
}

/** Pure transition table shared by the controller and its unit tests. */
export function decidePlayback(input: PlaybackDecisionInput): PlaybackDecision {
  const { playback } = input;
  if (!playback?.currentQueueItemId || !playback.currentSong) {
    return { kind: 'idle' };
  }

  const song = playback.currentSong;
  if (isYouTubePlayback(playback)) {
    return { kind: 'video' };
  }

  if (!input.audioUnlocked) {
    return { kind: 'await-unlock', song };
  }

  // A terminal status must stop/report even when the stage never finished loading the song.
  if (playback.status === PlaybackStatus.SongEnded) {
    return { kind: 'ended', queueItemId: playback.currentQueueItemId, song };
  }

  const loadedMatches = input.loaded?.queueItemId === playback.currentQueueItemId
    && input.loaded.songId === song.id;
  if (!loadedMatches) {
    return { kind: 'load', song };
  }

  switch (playback.status) {
    case PlaybackStatus.Countdown:
      return {
        kind: 'countdown',
        song,
        countdownBeats: playback.countdownRemainingBeats ?? null,
      };

    case PlaybackStatus.Playing: {
      const plan = planAudioStart(
        playback,
        input.nowMs,
        input.enginePlaying ? input.enginePositionMs : 0,
        songDurationMs(song),
      );
      const restart = input.enginePlaying
        && plan.expectedMs < RESTART_POSITION_MAX_MS
        && input.enginePositionMs > RESTART_AUDIO_MIN_MS;
      if (!input.enginePlaying || restart) {
        return { kind: 'start', song, restart, plan };
      }

      return { kind: 'hold', phase: 'playing', song };
    }

    case PlaybackStatus.Paused:
      return input.enginePlaying
        ? { kind: 'pause', song }
        : { kind: 'hold', phase: 'paused', song };

    default:
      // Idle / AwaitingDownload: no current audio should be running.
      return { kind: 'stop' };
  }
}

export interface PlaybackAnchor {
  queueItemId: string;
  songId: string | null;
  status: PlaybackStatus;
  positionMs: number;
  timeAnchorMs: number;
}

interface StagePlaybackControllerOptions {
  engine: WebAudioEngine;
  serverNow: () => number;
  reportSongEnded: (queueItemId: string) => void;
  onChange: (status: RoomPlaybackStatus) => void;
  onLyrics?: (lyrics: LyricsDocument) => void;
  loadTrack?: (song: SongDto) => Promise<LoadedLibraryTrack>;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Failed to load the song.';
}

function sameStatus(left: RoomPlaybackStatus, right: RoomPlaybackStatus): boolean {
  return left.active === right.active
    && left.phase === right.phase
    && left.queueItemId === right.queueItemId
    && left.song?.id === right.song?.id
    && left.song?.title === right.song?.title
    && left.lyrics === right.lyrics
    && left.countdownBeats === right.countdownBeats
    && left.error === right.error
    && left.anchorCorrectionMs === right.anchorCorrectionMs;
}

/**
 * Framework-free controller: consumes `PlaybackStateDto` transitions and drives the stem engine.
 * React binds to it through {@link useStagePlayback}.
 */
export class StagePlaybackController {
  private readonly deps: StagePlaybackControllerOptions;
  private playback: PlaybackStateDto | null = null;
  private audioUnlocked = false;
  private loaded: LoadedRoomTrack | null = null;
  private pending: { queueItemId: string; songId: string; token: number } | null = null;
  private token = 0;
  private reportedEnds = new Set<string>();
  private anchorCorrectionMs: number | null = null;
  private status: RoomPlaybackStatus = emptyRoomPlaybackStatus();
  private startRetryTimer: ReturnType<typeof setTimeout> | null = null;
  private cancelled = false;

  constructor(options: StagePlaybackControllerOptions) {
    this.deps = options;
  }

  getStatus(): RoomPlaybackStatus {
    return this.status;
  }

  get anchorCorrection(): number | null {
    return this.anchorCorrectionMs;
  }

  anchor(): PlaybackAnchor | null {
    const playback = this.playback;
    if (!playback?.currentQueueItemId) {
      return null;
    }

    return {
      queueItemId: playback.currentQueueItemId,
      songId: playback.currentSong?.id ?? null,
      status: playback.status,
      positionMs: Math.max(0, Number(playback.positionMs) || 0),
      timeAnchorMs: Date.parse(playback.timeAnchorUtc),
    };
  }

  /** Authoritative position at the current moment, in the stage's corrected server clock. */
  expectedMs(): number {
    return authoritativePositionMs(this.playback, this.deps.serverNow());
  }

  update(playback: PlaybackStateDto | null, audioUnlocked: boolean): void {
    this.cancelled = false;
    this.playback = playback;
    this.audioUnlocked = audioUnlocked;
    this.reconcile();
  }

  /**
   * Cancels an in-flight load without retiring the controller. Used by the hook's effect cleanup
   * so a StrictMode remount re-reconciles from the latest playback instead of decoding a stale item.
   */
  cancel(): void {
    this.cancelled = true;
    this.cancelPending();
    if (this.loaded) {
      this.deps.engine.stop();
      this.loaded = null;
    }
  }

  /** True when the engine's natural end belonged to a room item (and was handled). */
  handleEngineEnded(): boolean {
    const loaded = this.loaded;
    if (!loaded) {
      return false;
    }

    this.loaded = null;
    this.reportEnd(loaded.queueItemId);
    const playback = this.playback;
    const stillCurrent = playback?.currentQueueItemId === loaded.queueItemId;
    this.publish({
      active: stillCurrent,
      phase: 'ended',
      queueItemId: loaded.queueItemId,
      song: stillCurrent ? playback?.currentSong ?? null : null,
      countdownBeats: null,
      error: null,
    });
    return true;
  }

  private reconcile(): void {
    if (this.cancelled) {
      return;
    }

    const decision = decidePlayback({
      playback: this.playback,
      nowMs: this.deps.serverNow(),
      loaded: this.loaded,
      enginePlaying: this.deps.engine.playing,
      enginePositionMs: this.deps.engine.positionMs(),
      audioUnlocked: this.audioUnlocked,
    });
    const playback = this.playback;

    switch (decision.kind) {
      case 'idle':
        this.cancelPending();
        if (this.loaded) {
          this.deps.engine.stop();
          this.loaded = null;
        }
        this.anchorCorrectionMs = null;
        this.publishIdle();
        break;

      case 'video':
        this.cancelPending();
        if (this.loaded) {
          this.deps.engine.stop();
          this.loaded = null;
        }
        this.anchorCorrectionMs = null;
        this.publishIdle();
        break;

      case 'await-unlock':
        this.cancelPending();
        this.publish({
          active: true,
          phase: 'awaiting-unlock',
          queueItemId: playback?.currentQueueItemId ?? null,
          song: decision.song,
          countdownBeats: null,
          error: null,
        });
        break;

      case 'load':
        this.beginLoad(playback!, decision.song);
        break;

      case 'countdown':
        this.publish({
          active: true,
          phase: 'countdown',
          queueItemId: playback!.currentQueueItemId,
          song: decision.song,
          countdownBeats: decision.countdownBeats,
          error: null,
        });
        break;

      case 'start': {
        this.anchorCorrectionMs = decision.plan.anchorCorrectionMs;
        if (decision.restart) {
          this.deps.engine.stop();
        }

        let started = false;
        try {
          started = this.deps.engine.start(decision.plan.startOffsetMs);
        } catch (error) {
          this.publish({ phase: 'error', error: errorMessage(error) });
          break;
        }

        if (!started) {
          this.scheduleStartRetry();
          break;
        }

        this.publish({
          active: true,
          phase: 'playing',
          queueItemId: playback!.currentQueueItemId,
          song: decision.song,
          countdownBeats: null,
          error: null,
        });
        break;
      }

      case 'pause':
        this.deps.engine.pause();
        this.publish({
          active: true,
          phase: 'paused',
          queueItemId: playback!.currentQueueItemId,
          song: decision.song,
          countdownBeats: null,
          error: null,
        });
        break;

      case 'ended':
        this.deps.engine.stop();
        this.reportEnd(decision.queueItemId);
        this.publish({
          active: true,
          phase: 'ended',
          queueItemId: decision.queueItemId,
          song: decision.song,
          countdownBeats: null,
          error: null,
        });
        break;

      case 'hold':
        this.publish({
          active: true,
          phase: decision.phase,
          queueItemId: playback!.currentQueueItemId,
          song: decision.song,
          countdownBeats: null,
          error: null,
        });
        break;

      case 'stop':
        this.cancelPending();
        if (this.loaded) {
          this.deps.engine.stop();
          this.loaded = null;
        }
        this.anchorCorrectionMs = null;
        this.publishIdle();
        break;
    }
  }

  private beginLoad(playback: PlaybackStateDto, song: SongDto): void {
    const queueItemId = playback.currentQueueItemId;
    if (!queueItemId) {
      return;
    }

    if (
      this.pending
      && this.pending.queueItemId === queueItemId
      && this.pending.songId === song.id
    ) {
      return;
    }

    this.cancelPending();
    const token = ++this.token;
    this.pending = { queueItemId, songId: song.id, token };
    this.loaded = null;
    this.deps.engine.stop();
    this.publish({
      active: true,
      phase: playback.status === PlaybackStatus.Countdown ? 'countdown' : 'loading',
      queueItemId,
      song,
      lyrics: null,
      countdownBeats: playback.countdownRemainingBeats ?? null,
      error: null,
      anchorCorrectionMs: null,
    });

    void (async () => {
      try {
        const track = await (this.deps.loadTrack ?? loadLibraryTrack)(song);
        if (this.cancelled || token !== this.token) {
          return;
        }

        await this.deps.engine.loadSong({
          id: track.source.mediaId,
          instrumentalUrl: track.source.instrumentalUrl,
          vocalsUrl: track.source.vocalsUrl,
        });
        if (this.cancelled || token !== this.token) {
          return;
        }

        this.pending = null;
        this.loaded = { queueItemId, songId: song.id };
        this.deps.onLyrics?.(track.lyrics);
        this.reconcile();
      } catch (error) {
        if (this.cancelled || token !== this.token) {
          return;
        }

        this.pending = null;
        this.loaded = null;
        this.publish({
          active: true,
          phase: 'error',
          queueItemId,
          song,
          lyrics: null,
          countdownBeats: null,
          error: errorMessage(error),
        });
      }
    })();
  }

  private cancelPending(): void {
    this.token += 1;
    this.pending = null;
    if (this.startRetryTimer !== null) {
      clearTimeout(this.startRetryTimer);
      this.startRetryTimer = null;
    }
  }

  private scheduleStartRetry(): void {
    if (this.startRetryTimer !== null || this.cancelled) {
      return;
    }

    this.startRetryTimer = setTimeout(() => {
      this.startRetryTimer = null;
      this.reconcile();
    }, 100);
  }

  private reportEnd(queueItemId: string): void {
    if (this.reportedEnds.has(queueItemId)) {
      return;
    }

    this.reportedEnds.add(queueItemId);
    this.deps.reportSongEnded(queueItemId);
  }

  private publishIdle(): void {
    this.publish(emptyRoomPlaybackStatus());
  }

  private publish(partial: Partial<RoomPlaybackStatus>): void {
    const next: RoomPlaybackStatus = { ...this.status, ...partial };
    if (sameStatus(this.status, next)) {
      return;
    }

    this.status = next;
    this.deps.onChange(next);
  }
}

declare global {
  interface Window {
    /** Stage playback debug surface (mirrors `__karaokayVideo`). */
    __karaokayPlayback?: {
      readonly active: boolean;
      readonly phase: RoomPlaybackPhase;
      readonly songId: string | null;
      readonly queueItemId: string | null;
      readonly anchorCorrectionMs: number | null;
      expectedMs: () => number;
      audioPositionMs: () => number;
      driftMs: () => number;
      serverNow: () => number;
      anchor: () => PlaybackAnchor | null;
    };
  }
}

interface UseStagePlaybackOptions {
  engine: WebAudioEngine;
  playback: PlaybackStateDto | null;
  audioUnlocked: boolean;
  serverNow: () => number;
  onSongEnded: (queueItemId: string) => void;
  onLyrics?: (lyrics: LyricsDocument) => void;
}

export interface StagePlaybackHandle {
  status: RoomPlaybackStatus;
  /** Called by the shell's single `engine.onEnded` handler. */
  handleEngineEnded: () => boolean;
}

/** React binding for {@link StagePlaybackController}. */
export function useStagePlayback({
  engine,
  playback,
  audioUnlocked,
  serverNow,
  onSongEnded,
  onLyrics,
}: UseStagePlaybackOptions): StagePlaybackHandle {
  const [status, setStatus] = useState<RoomPlaybackStatus>(emptyRoomPlaybackStatus);
  const callbacksRef = useRef({ serverNow, onSongEnded, onLyrics });
  const mountedRef = useRef(true);
  callbacksRef.current = { serverNow, onSongEnded, onLyrics };

  const controllerRef = useRef<StagePlaybackController | null>(null);
  if (!controllerRef.current) {
    controllerRef.current = new StagePlaybackController({
      engine,
      serverNow: () => callbacksRef.current.serverNow(),
      reportSongEnded: (queueItemId) => {
        void callbacksRef.current.onSongEnded(queueItemId);
      },
      onChange: (next) => {
        if (mountedRef.current) {
          setStatus(next);
        }
      },
      onLyrics: (lyrics) => callbacksRef.current.onLyrics?.(lyrics),
    });
  }

  const controller = controllerRef.current;

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    controller.update(playback, audioUnlocked);
  }, [controller, playback, audioUnlocked]);

  // StrictMode-safe: a fake unmount cancels only the in-flight work; the remount's update effect
  // reconciles from the latest playback again.
  useEffect(() => () => controller.cancel(), [controller]);

  useEffect(() => {
    window.__karaokayPlayback = {
      get active() {
        return controller.getStatus().active;
      },
      get phase() {
        return controller.getStatus().phase;
      },
      get songId() {
        return controller.getStatus().song?.id ?? null;
      },
      get queueItemId() {
        return controller.getStatus().queueItemId;
      },
      get anchorCorrectionMs() {
        return controller.anchorCorrection;
      },
      expectedMs: () => controller.expectedMs(),
      audioPositionMs: () => engine.positionMs(),
      driftMs: () => controller.expectedMs() - engine.positionMs(),
      serverNow: () => callbacksRef.current.serverNow(),
      anchor: () => controller.anchor(),
    };

    return () => {
      delete window.__karaokayPlayback;
    };
  }, [controller, engine]);

  const handleEngineEnded = useCallback(
    () => controllerRef.current?.handleEngineEnded() ?? false,
    [],
  );

  return { status, handleEngineEnded };
}
