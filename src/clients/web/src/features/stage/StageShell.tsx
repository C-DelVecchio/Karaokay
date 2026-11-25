import { useEffect, useRef, useState } from 'react';
import { DemoTrackPicker } from './DemoTrackPicker';
import type { PickedDemoTrack } from './DemoTrackPicker';
import { findLeadInGap } from './lyrics';
import { CanvasLyricsRenderer } from './renderer/CanvasLyricsRenderer';
import { StageRoomPanel } from './StageRoomPanel';
import { useStageClock } from './useStageClock';
import { useStageRoom } from './useStageRoom';
import { useStageVideo } from './useStageVideo';
import { stageTestClock, WebAudioEngine } from './WebAudioEngine';

/**
 * The stage owns one engine for the whole page session: once the gesture gate is passed, the
 * unlocked context stays runnable across songs and route visits.
 */
const engine = new WebAudioEngine();

let audioUnlockAcknowledged = false;

type StagePhase = 'picking' | 'countdown' | 'playing' | 'ended';

interface Selection {
  song: PickedDemoTrack;
  token: number;
}

interface CountdownState {
  totalBeats: number;
  beatsLeft: number;
  totalMs: number;
}

export function StageShell() {
  const [audioGateVisible, setAudioGateVisible] = useState(
    () => !engine.unlocked && !audioUnlockAcknowledged,
  );
  const [phase, setPhase] = useState<StagePhase>('picking');
  const [selection, setSelection] = useState<Selection | null>(null);
  const [countdown, setCountdown] = useState<CountdownState | null>(null);
  const [guideVolume, setGuideVolume] = useState(() => engine.gains().guide);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [audioGateMessage, setAudioGateMessage] = useState<string | null>(null);
  const [audioUnlocked, setAudioUnlocked] = useState(() => engine.unlocked);

  const tokenRef = useRef(0);
  const phaseRef = useRef<StagePhase>('picking');
  const countdownElapsedRef = useRef(0);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const rendererRef = useRef<CanvasLyricsRenderer | null>(null);
  const subscribeStageClock = useStageClock(engine);
  const { status: roomStatus, room, serverNow, reportSongEnded } = useStageRoom();
  const playback = room.playback ?? room.snapshot?.playback ?? null;
  const video = useStageVideo({
    playback,
    audioUnlocked,
    engine,
    serverNow,
    onSongEnded: (queueItemId) => {
      void reportSongEnded(queueItemId);
    },
  });
  const videoMode = video.mode === 'video';

  phaseRef.current = phase;

  useEffect(() => {
    engine.onEnded = () => {
      tokenRef.current += 1;
      setSelection(null);
      setCountdown(null);
      setPhase('ended');
      rendererRef.current?.clear();
    };

    return () => {
      engine.onEnded = null;
      tokenRef.current += 1;
      engine.stop();
    };
  }, []);

  // Entering video mode owns the stage: stop any local demo stems first so a room YouTube item
  // never overlaps the demo picker's audio. The demo picker/phase return untouched afterwards.
  useEffect(() => {
    if (!videoMode) {
      return;
    }

    tokenRef.current += 1;
    engine.stop();
    setSelection(null);
    setCountdown(null);
    setPhase('picking');
    rendererRef.current?.clear();
  }, [videoMode]);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) {
      return undefined;
    }

    const renderer = new CanvasLyricsRenderer(canvas);
    rendererRef.current = renderer;
    window.__karaokayCanvas = {
      samplePixel: (x, y) => renderer.samplePixel(x, y),
      countNonBackgroundPixels: () => renderer.countNonBackgroundPixels(),
    };

    return () => {
      if (rendererRef.current === renderer) {
        rendererRef.current = null;
      }
      delete window.__karaokayCanvas;
    };
  }, []);

  // The canvas consumes the stage clock during the lead-in and playback; React never re-renders.
  useEffect(() => {
    if (phase !== 'countdown' && phase !== 'playing') {
      return undefined;
    }

    return subscribeStageClock((positionMs) => {
      const renderTimeMs = phaseRef.current === 'playing' || stageTestClock.isSet()
        ? positionMs
        : countdownElapsedRef.current;
      rendererRef.current?.render(renderTimeMs);
    });
  }, [phase, subscribeStageClock]);

  const handleEnableAudio = () => {
    setAudioGateMessage(null);
    // Start a waiting video (muted, autoplay-safe) inside the same gesture that unlocks Web Audio.
    video.primeForUnlock();
    void engine.unlock()
      .then(() => {
        audioUnlockAcknowledged = true;
        setAudioUnlocked(true);
        setAudioGateVisible(false);
      })
      .catch(() => {
        // Keep the gate up: `unlock()` rolled its promise back, so the next click retries.
        setAudioGateMessage('Audio unavailable — try again.');
      });
  };

  const handlePick = (song: PickedDemoTrack) => {
    if (!engine.unlocked || videoMode) {
      return;
    }

    const token = tokenRef.current + 1;
    tokenRef.current = token;
    setLoadError(null);
    setCountdown(null);
    countdownElapsedRef.current = 0;
    rendererRef.current?.load(song.lyrics);
    setSelection({ song, token });
  };

  // Pre-buffer + lead-in countdown run concurrently; start waits for both (and for the unlock).
  useEffect(() => {
    if (!selection) {
      return undefined;
    }

    const { song, token } = selection;
    let cancelled = false;
    let rafId: number | null = null;

    setPhase('countdown');
    countdownElapsedRef.current = 0;

    const gap = findLeadInGap(song.lyrics);
    const totalMs = gap?.renderCountdown ? Math.max(0, gap.toMs - gap.fromMs) : 0;
    const totalBeats = gap?.renderCountdown ? gap.countdownBeats : 0;
    setCountdown({ totalBeats, beatsLeft: totalBeats, totalMs });

    const loadPromise = engine.loadSong({
      id: song.slug,
      instrumentalUrl: `/demo-tracks/${song.slug}/instrumental.opus`,
      vocalsUrl: `/demo-tracks/${song.slug}/vocals.opus`,
    });

    const countdownPromise = new Promise<void>((resolve) => {
      if (totalMs <= 0) {
        resolve();
        return;
      }

      const startedAt = performance.now();
      let lastBeatsLeft = totalBeats;
      const frame = () => {
        if (cancelled) {
          return;
        }

        const elapsedMs = Math.min(performance.now() - startedAt, totalMs);
        countdownElapsedRef.current = elapsedMs;
        const beatsLeft = Math.max(0, Math.ceil(totalBeats * (1 - elapsedMs / totalMs)));
        if (beatsLeft !== lastBeatsLeft) {
          lastBeatsLeft = beatsLeft;
          setCountdown({ totalBeats, beatsLeft, totalMs });
        }

        if (elapsedMs >= totalMs) {
          resolve();
          return;
        }

        rafId = requestAnimationFrame(frame);
      };

      rafId = requestAnimationFrame(frame);
    });

    void Promise.all([loadPromise, countdownPromise])
      .then(() => {
        if (cancelled || token !== tokenRef.current) {
          return;
        }

        const leadInOffsetMs = gap?.renderCountdown ? gap.toMs : 0;
        if (!engine.start(leadInOffsetMs)) {
          setLoadError('Audio is not ready. Enable stage audio, then choose the song again.');
          setPhase('picking');
          setSelection(null);
          return;
        }

        setPhase('playing');
      })
      .catch((error: unknown) => {
        if (cancelled || token !== tokenRef.current) {
          return;
        }

        setLoadError(error instanceof Error ? error.message : 'Failed to load the song.');
        setPhase('picking');
        setSelection(null);
      });

    return () => {
      cancelled = true;
      if (rafId !== null) {
        cancelAnimationFrame(rafId);
      }
    };
  }, [selection]);

  return (
    <main className="stage" data-phase={phase} data-video={videoMode ? 'true' : 'false'}>
      {/*
        One persistent video element for the whole page session. It is never recreated, so the
        single MediaElementAudioSourceNode created on unlock stays valid across songs.
      */}
      <video
        id="video-surface"
        ref={video.videoRef}
        data-mode={video.mode}
        data-song-id={video.songId ?? undefined}
        className="stage-video"
        playsInline
        muted
        preload="auto"
        aria-hidden={!videoMode}
      />

      <canvas
        id="stage-canvas"
        ref={canvasRef}
        className="stage-canvas"
        width={1280}
        height={720}
        aria-hidden="true"
      />

      {audioGateVisible && (
        <div className="audio-gate">
          <button type="button" className="audio-gate-button" onClick={handleEnableAudio}>
            Click to Enable Stage Audio
          </button>
          {audioGateMessage && (
            <p className="audio-gate-error" role="alert">
              {audioGateMessage}
            </p>
          )}
        </div>
      )}

      <header className="stage-header">
        <h1>Karaokay Stage</h1>
        {videoMode && playback?.currentSong && (
          <p className="stage-now-playing">{playback.currentSong.title}</p>
        )}
        {!videoMode && phase === 'playing' && selection && (
          <p className="stage-now-playing">{selection.song.lyrics.title}</p>
        )}
      </header>

      <StageRoomPanel status={roomStatus} room={room} />

      {videoMode && video.notice && (
        <div className="stage-video-notice" role="alert">
          <span>{video.notice}</span>
          <button type="button" className="remote-secondary" onClick={video.retryPlayback}>
            Start video
          </button>
          <button type="button" className="remote-link" onClick={video.dismissNotice}>
            Dismiss
          </button>
        </div>
      )}

      {loadError && (
        <p className="stage-error" role="alert">
          {loadError}
        </p>
      )}

      {phase === 'countdown' && countdown && selection && (
        <section className="stage-countdown" aria-live="polite">
          <p className="stage-now-playing">{selection.song.lyrics.title}</p>
          <p>Get ready… {countdown.beatsLeft} beats</p>
          <div className="countdown-dots" aria-hidden="true">
            {Array.from({ length: countdown.beatsLeft }, (_, index) => (
              <span key={index} className="countdown-dot" />
            ))}
          </div>
        </section>
      )}

      {!videoMode && phase === 'playing' && selection && (
        <section className="stage-transport">
          <label htmlFor="guide-vocals">Guide vocals</label>
          <input
            id="guide-vocals"
            type="range"
            min={0}
            max={1}
            step={0.01}
            value={guideVolume}
            onChange={(event) => {
              const value = Number(event.currentTarget.value);
              setGuideVolume(value);
              engine.setGuideVocalVolume(value);
            }}
          />
        </section>
      )}

      {!videoMode && (phase === 'picking' || phase === 'ended') && (
        <>
          {phase === 'ended' && <p className="stage-complete">Song complete — pick another!</p>}
          <DemoTrackPicker
            onPick={handlePick}
            disabled={audioGateVisible || !engine.unlocked}
          />
        </>
      )}
    </main>
  );
}
