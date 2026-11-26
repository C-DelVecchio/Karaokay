import { useEffect, useState } from 'react';
import type { SongSummaryDto } from '../../lib/signalr/protocol';
import type { RemoteRoomState } from '../../lib/signalr/roomState';
import { queueTitlesFromSongs, resolveQueueItemTitle } from '../remote/queueTitles';
import type { StageRoomStatus } from './useStageRoom';

interface StageRoomPanelProps {
  status: StageRoomStatus;
  room: RemoteRoomState;
}

/**
 * Small live-room overlay for the TV/browser stage. Renders nothing unless a room was created or
 * joined successfully, which keeps the offline Slice 2 demo (and its e2e specs) byte-for-byte
 * unchanged when no API is running.
 *
 * The room connection itself is owned by `StageShell` (which needs playback for video mode) and
 * passed in, so there is exactly one stage hub connection per page.
 */
export function StageRoomPanel({ status, room }: StageRoomPanelProps) {
  const snapshot = room.snapshot;
  const [titles, setTitles] = useState<Map<string, string>>(() => new Map());
  const roomCode = snapshot?.roomCode;

  useEffect(() => {
    if (!roomCode) {
      return undefined;
    }

    let cancelled = false;
    const loadTitles = async () => {
      try {
        const response = await fetch('/api/songs');
        if (!response.ok) {
          return;
        }

        const songs = (await response.json()) as SongSummaryDto[];
        if (cancelled) {
          return;
        }

        setTitles(queueTitlesFromSongs(songs));
      } catch {
        // The queue falls back to requester names if the library endpoint is unavailable.
      }
    };

    void loadTitles();
    return () => {
      cancelled = true;
    };
  }, [roomCode]);

  if (status !== 'live' || !snapshot) {
    return null;
  }

  return (
    <aside className="stage-room-panel" aria-label="Live room">
      <p className="stage-room-label">Join at /remote</p>
      <p className="stage-room-code">{snapshot.roomCode}</p>
      <img
        className="stage-room-qr"
        src={`/api/rooms/${snapshot.roomCode}/qr`}
        alt={`Scan to join room ${snapshot.roomCode}`}
        width={128}
        height={128}
      />
      <p className="stage-room-guests">
        {room.guestCount} {room.guestCount === 1 ? 'guest' : 'guests'}
      </p>
      {room.queue.length > 0 && (
        <ol className="stage-room-queue" aria-label="Up next">
          {room.queue.slice(0, 4).map((item) => (
            <li key={item.id}>
              {resolveQueueItemTitle(item, titles)} · {item.requestedByName || 'Guest'}
            </li>
          ))}
        </ol>
      )}
    </aside>
  );
}
