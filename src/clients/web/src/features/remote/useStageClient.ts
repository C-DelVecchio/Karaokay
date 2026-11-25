import { useCallback, useEffect, useState } from 'react';
import type {
  HubErrorDto,
  JoinAck,
  QueueAck,
  StageConnectionStatus,
} from '../../lib/signalr/protocol';
import {
  applyDelta,
  applyGuestCount,
  applyPlayback,
  applySnapshot,
  createEmptyRoomState,
  type RemoteRoomState,
} from '../../lib/signalr/roomState';
import { StageClient } from '../../lib/signalr/stageClient';

export interface StageClientApi {
  state: RemoteRoomState;
  status: StageConnectionStatus;
  error: HubErrorDto | null;
  clearError: () => void;
  join: (roomCode: string, displayName: string) => Promise<JoinAck>;
  restart: () => Promise<void>;
  enqueueLibrarySong: (songId: string) => Promise<QueueAck>;
  removeQueueItem: (queueItemId: string) => Promise<void>;
  moveQueueItem: (queueItemId: string, newPosition: number) => Promise<void>;
  startPlayback: () => Promise<void>;
  pausePlayback: () => Promise<void>;
  resumePlayback: () => Promise<void>;
  skipSong: () => Promise<void>;
}

const failedAck: JoinAck = {
  success: false,
  sessionToken: null,
  reason: 'ConnectionFailed',
};

function toHubError(error: unknown): HubErrorDto {
  return {
    code: 'ConnectionFailed',
    message: error instanceof Error
      ? error.message
      : 'The stage server is unreachable.',
  };
}

/**
 * React binding for {@link StageClient}. The full `RoomSnapshot` is the source of truth; deltas
 * and playback pushes are merged through the pure reducer in `lib/signalr/roomState`.
 */
export function useStageClient(): StageClientApi {
  const [client] = useState(() => new StageClient());
  const [state, setState] = useState<RemoteRoomState>(createEmptyRoomState);
  const [status, setStatus] = useState<StageConnectionStatus>('idle');
  const [error, setError] = useState<HubErrorDto | null>(null);

  useEffect(() => {
    client.setHandlers({
      onJoinedRoom: (snapshot) => setState((current) => applySnapshot(current, snapshot)),
      onQueueChanged: (delta) => setState((current) => applyDelta(current, delta)),
      onPlaybackChanged: (playback) => setState((current) => applyPlayback(current, playback)),
      onParticipantJoined: (_displayName, guestCount) =>
        setState((current) => applyGuestCount(current, guestCount)),
      onParticipantLeft: (_displayName, guestCount) =>
        setState((current) => applyGuestCount(current, guestCount)),
      onHubError: setError,
      onStatusChanged: setStatus,
      onRoomClosed: () => setState(createEmptyRoomState()),
    });

    return () => {
      client.setHandlers({});
      void client.dispose();
    };
  }, [client]);

  const join = useCallback(
    async (roomCode: string, displayName: string): Promise<JoinAck> => {
      setError(null);
      try {
        const ack = await client.join(roomCode, displayName, 'guest');
        if (!ack.success) {
          setError({
            code: ack.reason ?? 'JoinFailed',
            message: ack.reason === 'RoomNotFound'
              ? 'That room code is not live. Ask the stage for the current code.'
              : 'Could not join the room.',
          });
        }

        return ack;
      } catch (joinError) {
        const hubError = toHubError(joinError);
        setError(hubError);
        return failedAck;
      }
    },
    [client],
  );

  const enqueueLibrarySong = useCallback(
    async (songId: string): Promise<QueueAck> => {
      setError(null);
      try {
        const ack = await client.enqueueLibrarySong(songId);
        if (!ack.success) {
          setError({
            code: ack.reason ?? 'NotFound',
            message: 'That song could not be added to the queue.',
          });
        }

        return ack;
      } catch (enqueueError) {
        setError(toHubError(enqueueError));
        return { success: false, queueItemId: '', reason: 'ConnectionFailed' };
      }
    },
    [client],
  );

  const run = useCallback(async (action: () => Promise<void>): Promise<void> => {
    setError(null);
    try {
      await action();
    } catch (actionError) {
      setError(toHubError(actionError));
    }
  }, []);

  const removeQueueItem = useCallback(
    (queueItemId: string) => run(() => client.removeQueueItem(queueItemId)),
    [client, run],
  );

  const moveQueueItem = useCallback(
    (queueItemId: string, newPosition: number) =>
      run(() => client.moveQueueItem(queueItemId, newPosition)),
    [client, run],
  );

  const startPlayback = useCallback(() => run(() => client.startPlayback()), [client, run]);
  const pausePlayback = useCallback(() => run(() => client.pausePlayback()), [client, run]);
  const resumePlayback = useCallback(() => run(() => client.resumePlayback()), [client, run]);
  const skipSong = useCallback(() => run(() => client.skipSong()), [client, run]);
  const restart = useCallback(() => run(() => client.restart()), [client, run]);

  return {
    state,
    status,
    error,
    clearError: () => setError(null),
    join,
    restart,
    enqueueLibrarySong,
    removeQueueItem,
    moveQueueItem,
    startPlayback,
    pausePlayback,
    resumePlayback,
    skipSong,
  };
}
