import { useCallback, useEffect, useRef, useState } from 'react';
import { ClockSyncSampler, serverNowMs } from '../../lib/signalr/clockSync';
import {
  isValidRoomCode,
  type CreateRoomResponse,
} from '../../lib/signalr/protocol';
import {
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

export type StageRoomStatus = 'connecting' | 'live' | 'offline';

export interface StageRoomView {
  status: StageRoomStatus;
  room: RemoteRoomState;
  /** Median Ping/Pong offset in ms; 0 until at least two samples exist. */
  clockOffsetMs: number;
  /** Local time corrected onto the server's wall clock (docs/ARCHITECTURE.md §5.3). */
  serverNow: () => number;
  /** Idempotent stage-only report; the server ignores duplicates for stale queue item ids. */
  reportSongEnded: (queueItemId: string) => Promise<void>;
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
  const joinedRef = useRef(false);

  useEffect(() => {
    let disposed = false;
    const controller = new AbortController();
    const client = new StageClient({
      onJoinedRoom: (snapshot) => {
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
        if (!disposed) {
          setRoom((current) => applyPlayback(current, playback));
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
      ping: (t1) => client.ping(t1),
      onChange: (offsetMs) => {
        clockOffsetRef.current = offsetMs;
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

  const serverNow = useCallback(
    () => serverNowMs(Date.now(), clockOffsetRef.current),
    [],
  );

  const reportSongEnded = useCallback(async (queueItemId: string): Promise<void> => {
    try {
      await clientRef.current?.reportSongEnded(queueItemId);
    } catch {
      // The ended watchdog/next snapshot will converge; never surface a transient hub fault to
      // the video surface.
    }
  }, []);

  return { status, room, clockOffsetMs, serverNow, reportSongEnded };
}
