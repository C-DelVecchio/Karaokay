import type { PlaybackStateDto } from '../../lib/signalr/protocol';
import { PlaybackStatus } from '../../lib/signalr/protocol';
import {
  DEFAULT_COUNTDOWN_BEAT_MS,
  type CountdownTickState,
} from '../../lib/signalr/roomState';

const STATUS_LABELS: Record<PlaybackStatus, string> = {
  0: 'Idle',
  1: 'Waiting for download',
  2: 'Count-in',
  3: 'Playing',
  4: 'Paused',
  5: 'Song ended',
};

const RING_RADIUS = 16;
const RING_CIRCUMFERENCE = 2 * Math.PI * RING_RADIUS;

interface PlaybackControlsProps {
  playback: PlaybackStateDto;
  countdown?: CountdownTickState | null;
  disabled?: boolean;
  onStart: () => void | Promise<void>;
  onPause: () => void | Promise<void>;
  onResume: () => void | Promise<void>;
  onSkip: () => void | Promise<void>;
}

function countdownFor(
  playback: PlaybackStateDto,
  countdown: CountdownTickState | null | undefined,
): CountdownTickState | null {
  if (playback.status !== PlaybackStatus.Countdown) {
    return null;
  }

  if (
    countdown
    && (!playback.currentQueueItemId || countdown.queueItemId === playback.currentQueueItemId)
  ) {
    return countdown;
  }

  const remaining = Math.max(0, Math.trunc(Number(playback.countdownRemainingBeats) || 0));
  return {
    queueItemId: playback.currentQueueItemId ?? '',
    remainingBeats: remaining,
    beatMs: DEFAULT_COUNTDOWN_BEAT_MS,
    totalBeats: Math.max(1, remaining),
  };
}

/**
 * Count-in ring for the phone. The server pushes one tick per beat (`remainingBeats` counts down),
 * so the arc and the number both animate from that single value.
 */
function CountdownRing({ tick }: { tick: CountdownTickState }) {
  const total = Math.max(1, tick.totalBeats);
  const remaining = Math.min(total, Math.max(0, tick.remainingBeats));
  const progress = remaining / total;
  const dashOffset = RING_CIRCUMFERENCE * (1 - progress);

  return (
    <span
      className="countdown-ring"
      data-remaining-beats={remaining}
      data-total-beats={total}
      data-beat-ms={tick.beatMs}
      aria-label={`Count-in: ${remaining} of ${total} beats`}
    >
      <svg viewBox="0 0 40 40" width="40" height="40" aria-hidden="true">
        <circle className="countdown-ring-track" cx="20" cy="20" r={RING_RADIUS} />
        <circle
          className="countdown-ring-progress"
          cx="20"
          cy="20"
          r={RING_RADIUS}
          strokeDasharray={RING_CIRCUMFERENCE}
          strokeDashoffset={dashOffset}
          style={{ transitionDuration: `${Math.max(0, tick.beatMs)}ms` }}
        />
      </svg>
      <span className="countdown-ring-value">{remaining}</span>
    </span>
  );
}

export function PlaybackControls({
  playback,
  countdown = null,
  disabled = false,
  onStart,
  onPause,
  onResume,
  onSkip,
}: PlaybackControlsProps) {
  const canStart =
    playback.status === PlaybackStatus.Idle || playback.status === PlaybackStatus.SongEnded;
  const isPlaying = playback.status === PlaybackStatus.Playing;
  // The server supports pausing the count-in (QueueService.PausePlaybackAsync handles Countdown),
  // so the phone must be able to stop a song before it reaches `Playing`.
  const canPause = isPlaying || playback.status === PlaybackStatus.Countdown;
  const isPaused = playback.status === PlaybackStatus.Paused;
  const ring = countdownFor(playback, countdown);

  return (
    <section className="remote-panel playback-controls" aria-labelledby="playback-heading">
      <h2 id="playback-heading">Playback</h2>
      <p className="playback-status" aria-live="polite">
        <span className="playback-state">{STATUS_LABELS[playback.status]}</span>
        {playback.currentSong && (
          <span className="playback-song"> · {playback.currentSong.title}</span>
        )}
        {ring && <CountdownRing tick={ring} />}
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
          disabled={disabled || !canPause}
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
