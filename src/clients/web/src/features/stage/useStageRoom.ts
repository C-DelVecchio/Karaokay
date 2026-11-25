import { useCallback, useEffect, useRef, useState } from 'react';
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
  const clientRef = useRef<StageClient | null>(null);

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
    });
    clientRef.current = client;

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
      clientRef.current = null;
      void client.dispose();
    };
  }, []);

  const reportSongEnded = useCallback(async (queueItemId: string): Promise<void> => {
    try {
      await clientRef.current?.reportSongEnded(queueItemId);
    } catch {
      // The ended watchdog/next snapshot will converge; never surface a transient hub fault to
      // the video surface.
    }
  }, []);

  return { status, room, reportSongEnded };
}
