import type { PlaybackStateDto } from '../../lib/signalr/protocol';
import { PlaybackStatus } from '../../lib/signalr/protocol';

const STATUS_LABELS: Record<PlaybackStatus, string> = {
  0: 'Idle',
  1: 'Waiting for download',
  2: 'Count-in',
  3: 'Playing',
  4: 'Paused',
  5: 'Song ended',
};

interface PlaybackControlsProps {
  playback: PlaybackStateDto;
  disabled?: boolean;
  onStart: () => void | Promise<void>;
  onPause: () => void | Promise<void>;
  onResume: () => void | Promise<void>;
  onSkip: () => void | Promise<void>;
}

export function PlaybackControls({
  playback,
  disabled = false,
  onStart,
  onPause,
  onResume,
  onSkip,
}: PlaybackControlsProps) {
  const canStart =
    playback.status === PlaybackStatus.Idle || playback.status === PlaybackStatus.SongEnded;
  const isPlaying = playback.status === PlaybackStatus.Playing;
  const isPaused = playback.status === PlaybackStatus.Paused;

  return (
    <section className="remote-panel playback-controls" aria-labelledby="playback-heading">
      <h2 id="playback-heading">Playback</h2>
      <p className="playback-status" aria-live="polite">
        <span className="playback-state">{STATUS_LABELS[playback.status]}</span>
        {playback.currentSong && (
          <span className="playback-song"> · {playback.currentSong.title}</span>
        )}
      </p>

      <div className="playback-buttons">
        <button
          type="button"
          className="remote-primary"
          disabled={disabled || !canStart}
          onClick={() => {
            void onStart();
          }}
        >
          Start
        </button>
        <button
          type="button"
          className="remote-secondary"
          disabled={disabled || !isPlaying}
          onClick={() => {
            void onPause();
          }}
        >
          Pause
        </button>
        <button
          type="button"
          className="remote-secondary"
          disabled={disabled || !isPaused}
          onClick={() => {
            void onResume();
          }}
        >
          Resume
        </button>
        <button
          type="button"
          className="remote-secondary"
          disabled={disabled || playback.currentQueueItemId === null}
          onClick={() => {
            void onSkip();
          }}
        >
          Skip
        </button>
      </div>
    </section>
  );
}
