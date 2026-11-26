import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ClockSyncSampler,
  receiptAnchorOffsetMs,
  serverNowMs,
} from '../../lib/signalr/clockSync';
import {
  isValidRoomCode,
  type CreateRoomResponse,
  type PlaybackStateDto,
} from '../../lib/signalr/protocol';
import {
  applyCountdownTick,
  applyDelta,
  applyDownloadFailed,
  applyDownloadProgress,
  applyGuestCount,
  applyPlayback,
  applyQueueItemState,
  applySnapshot,
  applySongReady,
  createEmptyRoomState,
  type RemoteRoomState,
} from '../../lib/signalr/roomState';
import { StageClient } from '../../lib/signalr/stageClient';

declare global {
  interface Window {
    /**
     * Test-only: biases the estimated server clock. Playwright injects a small residual error to
     * model an 80 ms round trip without throttling the whole network stack.
     */
    __karaokayClockBiasMs?: number;
  }
}

export type StageRoomStatus = 'connecting' | 'live' | 'offline';

export interface StageRoomView {
  status: StageRoomStatus;
  room: RemoteRoomState;
  /** Best-of-window Ping/Pong offset in ms; 0 until at least two accepted samples exist. */
  clockOffsetMs: number;
  /** Local time corrected onto the server's wall clock (docs/ARCHITECTURE.md §5.3). */
  serverNow: () => number;
  /** Idempotent stage-only report; the server ignores duplicates for stale queue item ids. */
  reportSongEnded: (queueItemId: string) => Promise<void>;
  /** Stage-authoritative guide-vocal level; the server echoes it through `PlaybackChanged`. */
  setGuideVocalVolume: (volume: number) => Promise<void>;
}

function readRequestedCode(): string | null {
  if (typeof window === 'undefined') {
    return null;
  }

  const raw = new URLSearchParams(window.location.search).get('code') ?? '';
  const code = raw.trim().toUpperCase();
  return isValidRoomCode(code) ? code : null;
}

async function requestRoom(signal: AbortSignal): Promise<string | null> {
  const response = await fetch('/api/rooms', { method: 'POST', signal });
  if (!response.ok) {
    return null;
  }

  const body = (await response.json()) as CreateRoomResponse;
  return typeof body.code === 'string' && isValidRoomCode(body.code) ? body.code : null;
}

/**
 * Optional live-room companion for the browser stage. One quiet attempt at mount: join `?code=`
 * when present, otherwise create a room. Any failure (API absent, hub unreachable) resolves to
 * `offline` and the Slice 2 local demo flow continues untouched.
 */
export function useStageRoom(): StageRoomView {
  const [room, setRoom] = useState<RemoteRoomState>(createEmptyRoomState);
  const [status, setStatus] = useState<StageRoomStatus>('connecting');
  const [clockOffsetMs, setClockOffsetMs] = useState(0);
  const clientRef = useRef<StageClient | null>(null);
  const samplerRef = useRef<ClockSyncSampler | null>(null);
  const clockOffsetRef = useRef(0);
  const clockConfidentRef = useRef(false);
  /**
   * Receipt-anchored fallback for the first moments of a join: `anchorServerMs` is the server
   * timestamp of the last playback projection and `receivedAtLocalMs` is when this client saw it.
   * The difference is a one-way-delay-bounded clock offset that does not depend on the Ping/Pong
   * filter (see `receiptAnchorOffsetMs`).
   */
  const receiptAnchorRef = useRef<{
    anchorServerMs: number;
    receivedAtLocalMs: number;
  } | null>(null);
  const joinedRef = useRef(false);

  useEffect(() => {
    let disposed = false;
    const controller = new AbortController();

    const notePlaybackAnchor = (playback: PlaybackStateDto | null | undefined) => {
      const anchorServerMs = Date.parse(playback?.timeAnchorUtc ?? '');
      if (!Number.isFinite(anchorServerMs)) {
        return;
      }

      receiptAnchorRef.current = { anchorServerMs, receivedAtLocalMs: Date.now() };
    };

    const client = new StageClient({
      onJoinedRoom: (snapshot) => {
        notePlaybackAnchor(snapshot.playback);
        if (!disposed) {
          setRoom((current) => applySnapshot(current, snapshot));
        }
      },
      onQueueChanged: (delta) => {
        if (!disposed) {
          setRoom((current) => applyDelta(current, delta));
        }
      },
      onQueueItemStateChanged: (queueItemId, itemState) => {
        if (!disposed) {
          setRoom((current) => applyQueueItemState(current, queueItemId, itemState));
        }
      },
      onDownloadProgressChanged: (progress) => {
        if (!disposed) {
          setRoom((current) => applyDownloadProgress(current, progress));
        }
      },
      onSongReady: (ready) => {
        if (!disposed) {
          setRoom((current) => applySongReady(current, ready));
        }
      },
      onDownloadFailed: (failed) => {
        if (!disposed) {
          setRoom((current) => applyDownloadFailed(current, failed));
        }
      },
      onPlaybackChanged: (playback) => {
        notePlaybackAnchor(playback);
        if (!disposed) {
          setRoom((current) => applyPlayback(current, playback));
        }
      },
      onCountdownTicked: (tick) => {
        if (!disposed) {
          setRoom((current) => applyCountdownTick(current, tick));
        }
      },
      onParticipantJoined: (_displayName, guestCount) => {
        if (!disposed) {
          setRoom((current) => applyGuestCount(current, guestCount));
        }
      },
      onParticipantLeft: (_displayName, guestCount) => {
        if (!disposed) {
          setRoom((current) => applyGuestCount(current, guestCount));
        }
      },
      // Clock sync is only useful on a live connection: stop the sampler while SignalR is
      // reconnecting/disconnected, and resume once a room membership is known again.
      onStatusChanged: (nextStatus) => {
        if (nextStatus === 'connected' && joinedRef.current) {
          samplerRef.current?.start();
        } else {
          samplerRef.current?.stop();
        }
      },
    });
    clientRef.current = client;

    const sampler = new ClockSyncSampler({
      ping: () => client.ping(),
      onChange: (offsetMs, confident) => {
        clockOffsetRef.current = offsetMs;
        clockConfidentRef.current = confident;
        if (!disposed) {
          setClockOffsetMs(offsetMs);
        }
      },
    });
    samplerRef.current = sampler;

    const connect = async () => {
      try {
        const code = readRequestedCode() ?? (await requestRoom(controller.signal));
        if (!code) {
          throw new Error('No room available');
        }

        const ack = await client.join(code, 'Stage', 'stage');
        if (!ack.success) {
          throw new Error(ack.reason ?? 'Join failed');
        }

        joinedRef.current = true;
        sampler.start();

        if (!disposed) {
          setStatus('live');
        }
      } catch {
        if (!disposed) {
          setStatus('offline');
        }
      }
    };

    void connect();

    return () => {
      disposed = true;
      controller.abort();
      joinedRef.current = false;
      sampler.stop();
      samplerRef.current = null;
      clientRef.current = null;
      void client.dispose();
    };
  }, []);

  const serverNow = useCallback(() => {
    const bias = typeof window !== 'undefined' && Number.isFinite(window.__karaokayClockBiasMs)
      ? window.__karaokayClockBiasMs ?? 0
      : 0;
    const localNow = Date.now();
    const anchor = receiptAnchorRef.current;
    const fallbackOffsetMs = anchor
      ? receiptAnchorOffsetMs(anchor.anchorServerMs, anchor.receivedAtLocalMs)
      : null;
    // Before the Ping/Pong filter is confident, extrapolate from the room-state anchor at the
    // local receipt instant instead of trusting a possibly stall-biased offset.
    const offsetMs = !clockConfidentRef.current && fallbackOffsetMs !== null
      ? fallbackOffsetMs
      : clockOffsetRef.current;
    return serverNowMs(localNow, offsetMs) + bias;
  }, []);

  const reportSongEnded = useCallback(async (queueItemId: string): Promise<void> => {
    try {
      await clientRef.current?.reportSongEnded(queueItemId);
    } catch {
      // The ended watchdog/next snapshot will converge; never surface a transient hub fault to
      // the video surface.
    }
  }, []);

  const setGuideVocalVolume = useCallback(async (volume: number): Promise<void> => {
    try {
      await clientRef.current?.setGuideVocalVolume(volume);
    } catch {
      // The next playback broadcast or snapshot carries the authoritative level.
    }
  }, []);

  return {
    status,
    room,
    clockOffsetMs,
    serverNow,
    reportSongEnded,
    setGuideVocalVolume,
  };
}
