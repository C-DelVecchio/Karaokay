import { useState } from 'react';
import type { HubErrorDto } from '../../lib/signalr/protocol';
import {
  ROOM_CODE_ALPHABET,
  ROOM_CODE_LENGTH,
  isValidRoomCode,
} from '../../lib/signalr/protocol';

/** Extracts and normalizes `?code=XXXX` from the QR join link. */
export function readRoomCodeFromSearch(search: string): string {
  const raw = new URLSearchParams(search).get('code') ?? '';
  return sanitizeRoomCode(raw);
}

function sanitizeRoomCode(value: string): string {
  return value
    .toUpperCase()
    .split('')
    .filter((character) => ROOM_CODE_ALPHABET.includes(character))
    .join('')
    .slice(0, ROOM_CODE_LENGTH);
}

interface JoinScreenProps {
  initialCode?: string;
  busy?: boolean;
  error: HubErrorDto | null;
  onJoin: (roomCode: string, displayName: string) => void | Promise<unknown>;
}

export function JoinScreen({ initialCode, busy = false, error, onJoin }: JoinScreenProps) {
  const [roomCode, setRoomCode] = useState(() =>
    initialCode
    ?? readRoomCodeFromSearch(typeof window === 'undefined' ? '' : window.location.search));
  const [displayName, setDisplayName] = useState('');
  const canJoin = isValidRoomCode(roomCode) && displayName.trim().length > 0 && !busy;

  return (
    <main className="remote">
      <section className="join-screen" aria-labelledby="join-heading">
        <h1 id="join-heading">Join a karaoke room</h1>
        <p className="remote-hint">Enter the four-letter code shown on the stage.</p>

        <form
          className="join-form"
          onSubmit={(event) => {
            event.preventDefault();
            if (!canJoin) {
              return;
            }

            void onJoin(roomCode, displayName.trim());
          }}
        >
          <label htmlFor="room-code">Room code</label>
          <input
            id="room-code"
            name="room-code"
            inputMode="text"
            autoComplete="off"
            autoCapitalize="characters"
            spellCheck={false}
            maxLength={ROOM_CODE_LENGTH}
            placeholder="ABCD"
            value={roomCode}
            onChange={(event) => setRoomCode(sanitizeRoomCode(event.currentTarget.value))}
          />

          <label htmlFor="display-name">Your name</label>
          <input
            id="display-name"
            name="display-name"
            autoComplete="nickname"
            maxLength={32}
            placeholder="Singer"
            value={displayName}
            onChange={(event) => setDisplayName(event.currentTarget.value)}
          />

          <button type="submit" className="remote-primary" disabled={!canJoin}>
            {busy ? 'Joining…' : 'Join room'}
          </button>
        </form>

        {error && (
          <p className="remote-error" role="alert">
            {error.message}
          </p>
        )}
      </section>
    </main>
  );
}
