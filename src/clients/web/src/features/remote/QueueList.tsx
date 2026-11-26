import { useState } from 'react';
import {
  isKnownYouTubeQueueItem,
  youtubeErrorMessage,
} from '../../lib/api/youtube';
import type { QueueDownloadState } from '../../lib/signalr/roomState';
import { QueueItemState } from '../../lib/signalr/protocol';
import type {
  QueueAck,
  QueueItemDto,
  SongSummaryDto,
} from '../../lib/signalr/protocol';
import { firstMovableIndex, isQueueItemMovable } from './queueOrdering';
import { queueTitlesFromSongs, resolveQueueItemTitle } from './queueTitles';

const STATE_LABELS: Record<QueueItemState, string> = {
  0: 'Queued',
  1: 'Downloading',
  2: 'Ready',
  3: 'Failed',
  4: 'Playing',
  5: 'Ended',
};

interface QueueListProps {
  queue: QueueItemDto[];
  songs: SongSummaryDto[];
  downloads: Record<string, QueueDownloadState>;
  disabled?: boolean;
  onMove: (queueItemId: string, newPosition: number) => void | Promise<void>;
  onRemove: (queueItemId: string) => void | Promise<void>;
  onRetry: (queueItemId: string) => Promise<QueueAck>;
}

function badgeLabel(
  item: QueueItemDto,
  download: QueueDownloadState | undefined,
  isYouTube: boolean,
): string {
  if (item.state === QueueItemState.Failed || download?.failureReason) {
    return 'Failed';
  }

  if (item.state === QueueItemState.Ready || download?.ready) {
    // A YouTube row that is Ready before any progress event exists was a cache hit.
    return download?.cached || (isYouTube && !download) ? 'Ready · cached' : 'Ready';
  }

  if (item.state === QueueItemState.Playing) {
    return 'Playing';
  }

  if (item.state === QueueItemState.Ended) {
    return 'Ended';
  }

  if (item.state === QueueItemState.Downloading) {
    if (download && Number.isFinite(download.percent)) {
      return `${Math.round(Math.min(100, Math.max(0, download.percent)))}%`;
    }

    return 'Downloading';
  }

  return 'Queued';
}

export function QueueList({
  queue,
  songs,
  downloads,
  disabled = false,
  onMove,
  onRemove,
  onRetry,
}: QueueListProps) {
  const titles = queueTitlesFromSongs(songs);
  const youtubeSongIds = new Set<string>();
  for (const song of songs) {
    if (song.id && song.source?.toLowerCase() === 'youtube') {
      youtubeSongIds.add(song.id);
    }
  }

  const [retrying, setRetrying] = useState<Record<string, boolean>>({});
  const [retried, setRetried] = useState<Record<string, boolean>>({});
  const [retryErrors, setRetryErrors] = useState<Record<string, string>>({});

  if (queue.length === 0) {
    return (
      <section className="remote-panel" aria-labelledby="queue-heading">
        <h2 id="queue-heading">Queue</h2>
        <p className="remote-hint">The queue is empty. Add a song from the library.</p>
      </section>
    );
  }

  const movableFrom = firstMovableIndex(queue);

  const handleRetry = async (queueItemId: string) => {
    setRetrying((current) => ({ ...current, [queueItemId]: true }));
    setRetryErrors((current) => {
      const next = { ...current };
      delete next[queueItemId];
      return next;
    });

    try {
      await onRetry(queueItemId);
      setRetried((current) => ({ ...current, [queueItemId]: true }));
    } catch (error) {
      setRetryErrors((current) => ({
        ...current,
        [queueItemId]: youtubeErrorMessage(error),
      }));
    } finally {
      setRetrying((current) => {
        const next = { ...current };
        delete next[queueItemId];
        return next;
      });
    }
  };

  return (
    <section className="remote-panel" aria-labelledby="queue-heading">
      <h2 id="queue-heading">Queue</h2>
      <ol className="queue-list">
        {queue.map((item, index) => {
          const label = resolveQueueItemTitle(item, titles);
          const movable = isQueueItemMovable(item.state);
          const download = downloads[item.id];
          const isYouTube = Boolean(download)
            || isKnownYouTubeQueueItem(item.id)
            || youtubeSongIds.has(item.songId);
          const failed = item.state === QueueItemState.Failed || Boolean(download?.failureReason);
          const percent = Math.min(100, Math.max(0, download?.percent ?? 0));
          const retryError = retryErrors[item.id];

          return (
            <li
              key={item.id}
              className="queue-row"
              data-state={item.state}
              data-youtube={isYouTube ? 'true' : undefined}
            >
              <div className="queue-meta">
                <span className="queue-position">{item.position + 1}</span>
                <span className="queue-title">{label}</span>
                <span className="queue-subtitle">
                  {item.requestedByName || 'Guest'} · {STATE_LABELS[item.state]}
                </span>
                {isYouTube && (
                  <span
                    className="queue-badge"
                    data-download-state={failed ? 'failed' : download?.stage ?? 'queued'}
                    title={failed ? download?.failureReason ?? 'Download failed' : undefined}
                  >
                    {badgeLabel(item, download, isYouTube)}
                  </span>
                )}
                {isYouTube
                  && item.state === QueueItemState.Downloading
                  && !failed
                  && (
                    <span
                      className="queue-progress"
                      role="progressbar"
                      aria-label={`${label} download progress`}
                      aria-valuemin={0}
                      aria-valuemax={100}
                      aria-valuenow={Math.round(percent)}
                    >
                      <span className="queue-progress-fill" style={{ width: `${percent}%` }} />
                    </span>
                  )}
                {failed && download?.failureReason && (
                  <span className="queue-error">{download.failureReason}</span>
                )}
                {retryError && (
                  <span className="queue-error" role="alert">
                    {retryError}
                  </span>
                )}
              </div>
              <div className="queue-actions">
                {failed && isYouTube && (
                  <button
                    type="button"
                    className="remote-secondary queue-retry"
                    disabled={disabled || retrying[item.id] === true || retried[item.id] === true}
                    onClick={() => {
                      void handleRetry(item.id);
                    }}
                  >
                    {retrying[item.id] ? 'Retrying…' : retried[item.id] ? 'Retried ✓' : 'Retry'}
                  </button>
                )}
                <button
                  type="button"
                  className="remote-icon"
                  aria-label={`Move ${label} up`}
                  disabled={disabled || !movable || index <= movableFrom}
                  onClick={() => {
                    void onMove(item.id, item.position - 1);
                  }}
                >
                  ↑
                </button>
                <button
                  type="button"
                  className="remote-icon"
                  aria-label={`Move ${label} down`}
                  disabled={disabled || !movable || index === queue.length - 1}
                  onClick={() => {
                    void onMove(item.id, item.position + 1);
                  }}
                >
                  ↓
                </button>
                <button
                  type="button"
                  className="remote-icon remote-icon-danger"
                  aria-label={`Remove ${label}`}
                  disabled={disabled}
                  onClick={() => {
                    void onRemove(item.id);
                  }}
                >
                  ✕
                </button>
              </div>
            </li>
          );
        })}
      </ol>
    </section>
  );
}
