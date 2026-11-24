import { useEffect, useState } from 'react';
import type { LyricsDocument } from './lyrics';

export const DEMO_TRACK_SLUGS = [
  'synth-twinkle-variations',
  'synth-spoken-telegram',
  'synth-countdown-canyon',
] as const;

export type DemoTrackSlug = (typeof DEMO_TRACK_SLUGS)[number];

export interface PickedDemoTrack {
  slug: DemoTrackSlug;
  lyrics: LyricsDocument;
}

interface DemoTrackPickerProps {
  onPick: (song: PickedDemoTrack) => void;
  disabled?: boolean;
}

type TrackState =
  | { slug: DemoTrackSlug; status: 'loading' }
  | { slug: DemoTrackSlug; status: 'error'; error: string }
  | { slug: DemoTrackSlug; status: 'ready'; lyrics: LyricsDocument };

function formatDuration(durationMs: number): string {
  const totalSeconds = Math.round(durationMs / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

function TrackRow({
  track,
  disabled,
  onPick,
}: {
  track: TrackState;
  disabled: boolean;
  onPick: (song: PickedDemoTrack) => void;
}) {
  if (track.status === 'loading') {
    return <p className="track-status">Loading {track.slug}…</p>;
  }

  if (track.status === 'error') {
    return (
      <p className="track-status track-status-error" role="alert">
        Could not load {track.slug}: {track.error}
      </p>
    );
  }

  const { lyrics } = track;

  return (
    <button
      type="button"
      className="track-button"
      disabled={disabled}
      onClick={() => onPick({ slug: track.slug, lyrics })}
    >
      <span className="track-title">{lyrics.title}</span>
      <span className="track-meta">
        {lyrics.artist} · {formatDuration(lyrics.durationMs)}
      </span>
    </button>
  );
}

export function DemoTrackPicker({ onPick, disabled = false }: DemoTrackPickerProps) {
  const [tracks, setTracks] = useState<TrackState[]>(() =>
    DEMO_TRACK_SLUGS.map((slug) => ({ slug, status: 'loading' as const })));

  useEffect(() => {
    let cancelled = false;

    const loadTrack = async (slug: DemoTrackSlug): Promise<TrackState> => {
      try {
        const response = await fetch(`/demo-tracks/${slug}/lyrics.json`);
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }

        const lyrics = (await response.json()) as LyricsDocument;
        return { slug, status: 'ready', lyrics };
      } catch (error) {
        return {
          slug,
          status: 'error',
          error: error instanceof Error ? error.message : 'Failed to load lyrics',
        };
      }
    };

    for (const slug of DEMO_TRACK_SLUGS) {
      void loadTrack(slug).then((result) => {
        if (cancelled) {
          return;
        }

        setTracks((current) =>
          current.map((track) => (track.slug === slug ? result : track)));
      });
    }

    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <section className="demo-picker" aria-labelledby="demo-picker-heading">
      <h2 id="demo-picker-heading">Choose a demo song</h2>
      <ul className="track-list">
        {tracks.map((track) => (
          <li key={track.slug}>
            <TrackRow track={track} disabled={disabled} onPick={onPick} />
          </li>
        ))}
      </ul>
    </section>
  );
}
