import type { QueueItemDto, QueueItemState, SongSummaryDto } from '../../lib/signalr/protocol';

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
  disabled?: boolean;
  onMove: (queueItemId: string, newPosition: number) => void | Promise<void>;
  onRemove: (queueItemId: string) => void | Promise<void>;
}

export function QueueList({ queue, songs, disabled = false, onMove, onRemove }: QueueListProps) {
  const titles = new Map<string, string>();
  for (const song of songs) {
    const title = song.title ?? 'Untitled';
    if (song.id) {
      titles.set(song.id, title);
    }
    if (song.enqueueId) {
      titles.set(song.enqueueId, title);
    }
  }

  if (queue.length === 0) {
    return (
      <section className="remote-panel" aria-labelledby="queue-heading">
        <h2 id="queue-heading">Queue</h2>
        <p className="remote-hint">The queue is empty. Add a song from the library.</p>
      </section>
    );
  }

  return (
    <section className="remote-panel" aria-labelledby="queue-heading">
      <h2 id="queue-heading">Queue</h2>
      <ol className="queue-list">
        {queue.map((item, index) => {
          const label = titles.get(item.songId) ?? `Song ${item.songId.slice(0, 8)}`;
          return (
            <li key={item.id} className="queue-row" data-state={item.state}>
              <div className="queue-meta">
                <span className="queue-position">{item.position + 1}</span>
                <span className="queue-title">{label}</span>
                <span className="queue-subtitle">
                  {item.requestedByName || 'Guest'} · {STATE_LABELS[item.state]}
                </span>
              </div>
              <div className="queue-actions">
                <button
                  type="button"
                  className="remote-icon"
                  aria-label={`Move ${label} up`}
                  disabled={disabled || index === 0}
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
                  disabled={disabled || index === queue.length - 1}
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
