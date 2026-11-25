import { useEffect, useState } from 'react';
import {
  searchYouTube,
  youtubeErrorMessage,
  type YouTubeSearchResult,
} from '../../lib/api/youtube';
import type { QueueAck } from '../../lib/signalr/protocol';

type AddState = 'pending' | 'added' | 'error';

interface YouTubeTabProps {
  disabled?: boolean;
  onEnqueue: (result: YouTubeSearchResult) => Promise<QueueAck>;
}

const SEARCH_DEBOUNCE_MS = 300;
const MAX_RESULTS = 10;

function formatDuration(durationSeconds: number): string {
  const totalSeconds = Math.max(0, Math.round(durationSeconds));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

/**
 * Zero-key YouTube search tab. Debounces typing, speaks the REST API's typed result shape, and
 * surfaces the feature-flag/timeout cases as friendly copy instead of raw problem details.
 */
export function YouTubeTab({ disabled = false, onEnqueue }: YouTubeTabProps) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<YouTubeSearchResult[]>([]);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [addState, setAddState] = useState<Record<string, AddState>>({});
  const [addError, setAddError] = useState<Record<string, string>>({});

  useEffect(() => {
    const trimmed = query.trim();
    if (trimmed.length === 0) {
      setResults([]);
      setSearching(false);
      setSearchError(null);
      return undefined;
    }

    let cancelled = false;
    const timer = window.setTimeout(() => {
      setSearching(true);
      setSearchError(null);

      void searchYouTube(trimmed, MAX_RESULTS)
        .then((payload) => {
          if (cancelled) {
            return;
          }

          setResults(payload);
          setSearching(false);
        })
        .catch((error: unknown) => {
          if (cancelled) {
            return;
          }

          setResults([]);
          setSearching(false);
          setSearchError(youtubeErrorMessage(error));
        });
    }, SEARCH_DEBOUNCE_MS);

    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [query]);

  const handleAdd = async (result: YouTubeSearchResult) => {
    const videoId = result.videoId;
    setAddState((current) => ({ ...current, [videoId]: 'pending' }));
    setAddError((current) => {
      const next = { ...current };
      delete next[videoId];
      return next;
    });

    try {
      const ack = await onEnqueue(result);
      setAddState((current) => ({
        ...current,
        [videoId]: ack.success ? 'added' : 'error',
      }));
      if (!ack.success) {
        setAddError((current) => ({
          ...current,
          [videoId]: ack.reason === 'YouTubeDisabled'
            ? 'YouTube requests are turned off for this stage.'
            : 'That video could not be added to the queue.',
        }));
      }
    } catch (error) {
      setAddState((current) => ({ ...current, [videoId]: 'error' }));
      setAddError((current) => ({ ...current, [videoId]: youtubeErrorMessage(error) }));
    }
  };

  return (
    <section className="remote-panel" aria-labelledby="youtube-heading">
      <h2 id="youtube-heading">YouTube</h2>

      <label className="youtube-search-label" htmlFor="youtube-search">
        Search karaoke videos
      </label>
      <input
        id="youtube-search"
        className="youtube-search-input"
        type="search"
        autoComplete="off"
        placeholder="Song, artist, or video title"
        value={query}
        onChange={(event) => setQuery(event.currentTarget.value)}
      />

      {searching && <p className="remote-hint">Searching YouTube…</p>}
      {searchError && (
        <p className="remote-error" role="alert">
          {searchError}
        </p>
      )}

      {!searching && !searchError && query.trim().length > 0 && results.length === 0 && (
        <p className="remote-hint">No videos matched that search.</p>
      )}

      {results.length > 0 && (
        <ul className="youtube-list" aria-live="polite">
          {results.map((result) => {
            const state = addState[result.videoId];
            const rowError = addError[result.videoId];
            const thumbnail = result.thumbnailUrl ?? '';

            return (
              <li
                key={result.videoId}
                className="youtube-row"
                data-video-id={result.videoId}
              >
                {thumbnail ? (
                  <img
                    className="youtube-thumb"
                    src={thumbnail}
                    alt=""
                    loading="lazy"
                    width={96}
                    height={54}
                  />
                ) : (
                  <span className="youtube-thumb youtube-thumb-empty" aria-hidden="true" />
                )}
                <div className="youtube-meta">
                  <span className="youtube-title">{result.title}</span>
                  <span className="youtube-subtitle">
                    {result.channel || 'Unknown channel'} · {formatDuration(Number(result.durationSeconds) || 0)}
                  </span>
                  {rowError && (
                    <span className="remote-error" role="alert">
                      {rowError}
                    </span>
                  )}
                </div>
                <button
                  type="button"
                  className="remote-secondary"
                  disabled={disabled || state === 'pending' || state === 'added'}
                  onClick={() => {
                    void handleAdd(result);
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
      )}
    </section>
  );
}
