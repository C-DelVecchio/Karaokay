import { useState } from 'react';
import type { SongSummaryDto } from '../../lib/signalr/protocol';
import { LibraryTab } from './LibraryTab';
import { PlaybackControls } from './PlaybackControls';
import { QueueList } from './QueueList';
import { YouTubeTab } from './YouTubeTab';
import type { StageClientApi } from './useStageClient';

type LobbyTab = 'library' | 'youtube' | 'queue';

interface LobbyViewProps {
  api: StageClientApi;
}

export function LobbyView({ api }: LobbyViewProps) {
  const [tab, setTab] = useState<LobbyTab>('library');
  const [songs, setSongs] = useState<SongSummaryDto[]>([]);
  const { state, status, error } = api;
  const snapshot = state.snapshot;

  if (!snapshot) {
    return null;
  }

  const disabled = status === 'reconnecting' || status === 'disconnected';

  return (
    <main className="remote">
      <header className="lobby-header">
        <div>
          <p className="remote-eyebrow">Room</p>
          <p className="lobby-code" aria-label={`Room code ${snapshot.roomCode}`}>
            {snapshot.roomCode}
          </p>
          <p className="remote-hint">
            {state.guestCount} {state.guestCount === 1 ? 'guest' : 'guests'} connected
          </p>
        </div>
        <img
          className="lobby-qr"
          src={`/api/rooms/${snapshot.roomCode}/qr`}
          alt={`QR code to join room ${snapshot.roomCode}`}
          width={132}
          height={132}
        />
      </header>

      {status === 'reconnecting' && (
        <p className="remote-banner" role="status">
          Reconnecting to the stage…
        </p>
      )}

      {status === 'disconnected' && (
        <div className="remote-banner remote-banner-error" role="alert">
          <span>Disconnected from the stage.</span>
          <button
            type="button"
            className="remote-link"
            onClick={() => {
              void api.restart();
            }}
          >
            Reconnect
          </button>
        </div>
      )}

      {error && (
        <div className="remote-banner remote-banner-error" role="alert">
          <span>{error.message}</span>
          <button type="button" className="remote-link" onClick={api.clearError}>
            Dismiss
          </button>
        </div>
      )}

      <PlaybackControls
        playback={state.playback ?? snapshot.playback}
        disabled={disabled}
        onStart={api.startPlayback}
        onPause={api.pausePlayback}
        onResume={api.resumePlayback}
        onSkip={api.skipSong}
      />

      <div className="remote-tabs" role="tablist" aria-label="Room panels">
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'library'}
          className="remote-tab"
          onClick={() => setTab('library')}
        >
          Library
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'youtube'}
          className="remote-tab"
          onClick={() => setTab('youtube')}
        >
          YouTube
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'queue'}
          className="remote-tab"
          onClick={() => setTab('queue')}
        >
          Queue ({state.queue.length})
        </button>
      </div>

      <div hidden={tab !== 'library'}>
        <LibraryTab
          disabled={disabled}
          onEnqueue={api.enqueueLibrarySong}
          onSongsLoaded={setSongs}
        />
      </div>

      <div hidden={tab !== 'youtube'}>
        <YouTubeTab
          disabled={disabled}
          onEnqueue={api.enqueueYouTubeSong}
        />
      </div>

      <div hidden={tab !== 'queue'}>
        <QueueList
          queue={state.queue}
          songs={songs}
          downloads={state.downloads}
          disabled={disabled}
          onMove={api.moveQueueItem}
          onRemove={api.removeQueueItem}
          onRetry={api.retryYouTubeSong}
        />
      </div>
    </main>
  );
}
