import { useEffect, useState } from 'react';
import type { QueueAck, SongSummaryDto } from '../../lib/signalr/protocol';

type AddState = 'pending' | 'added' | 'error';

interface LibraryTabProps {
  disabled?: boolean;
  onEnqueue: (songId: string) => Promise<QueueAck>;
  onSongsLoaded?: (songs: SongSummaryDto[]) => void;
}

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The hub enqueues library songs by GUID; the offline filesystem fallback uses slugs. */
export function isLibrarySongId(value: string): boolean {
  return GUID_PATTERN.test(value);
}

function formatDuration(durationMs: number): string {
  const totalSeconds = Math.round(durationMs / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

export function LibraryTab({ disabled = false, onEnqueue, onSongsLoaded }: LibraryTabProps) {
  const [songs, setSongs] = useState<SongSummaryDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [addState, setAddState] = useState<Record<string, AddState>>({});

  useEffect(() => {
    let cancelled = false;

    const load = async () => {
      try {
        const response = await fetch('/api/songs');
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }

        const payload = (await response.json()) as SongSummaryDto[];
        if (cancelled) {
          return;
        }

        setSongs(payload);
        setLoadError(null);
        onSongsLoaded?.(payload);
      } catch {
        if (!cancelled) {
          setLoadError('The song library is unavailable right now.');
          onSongsLoaded?.([]);
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    };

    void load();
    return () => {
      cancelled = true;
    };
  }, [onSongsLoaded]);

  const handleAdd = async (songId: string) => {
    setAddState((current) => ({ ...current, [songId]: 'pending' }));
    const ack = await onEnqueue(songId);
    setAddState((current) => ({
      ...current,
      [songId]: ack.success ? 'added' : 'error',
    }));
  };

  return (
    <section className="remote-panel" aria-labelledby="library-heading">
      <h2 id="library-heading">Library</h2>

      {loading && <p className="remote-hint">Loading songs…</p>}
      {loadError && (
        <p className="remote-error" role="alert">
          {loadError}
        </p>
      )}

      {!loading && !loadError && songs.length === 0 && (
        <p className="remote-hint">No songs are available yet.</p>
      )}

      <ul className="library-list">
        {songs.map((song, index) => {
          const songId = song.enqueueId ?? song.id ?? '';
          const state = addState[songId];
          const canAdd = isLibrarySongId(songId) && !disabled && state !== 'pending';

          return (
            <li key={songId || `song-${index}`} className="library-row">
              <div className="library-meta">
                <span className="library-title">{song.title ?? 'Untitled'}</span>
                <span className="library-subtitle">
                  {song.artist ?? 'Unknown artist'} · {formatDuration(Number(song.durationMs ?? 0))}
                </span>
              </div>
              <button
                type="button"
                className="remote-secondary"
                disabled={!canAdd || state === 'added'}
                title={isLibrarySongId(songId) ? undefined : 'This song id is not enqueueable.'}
                onClick={() => {
                  void handleAdd(songId);
                }}
              >
                {state === 'pending' && 'Adding…'}
                {state === 'added' && 'Added ✓'}
                {state === 'error' && 'Retry'}
                {!state && 'Add to queue'}
              </button>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
