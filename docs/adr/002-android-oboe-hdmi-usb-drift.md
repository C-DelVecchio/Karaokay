# ADR-002: Oboe full-duplex engine with manual USB→HDMI clock-drift compensation on Android TV

- **Status:** Accepted
- **Date:** 2026-11-23
- **Deciders:** Architect
- **Context level:** Client platform / Native audio DSP
- **Related:** [ADR-003](003-dual-mode-stage-web-vs-native.md) (why a native stage exists at all), [ARCHITECTURE.md §6.2](../ARCHITECTURE.md#62-android-c-oboe-audio-engine)

## Context

The flagship party experience runs on an **Onn 4K Pro** Android TV box:

- The **microphone is a USB mic plugged into the box** (the only realistic party setup — no
  external hardware mixers, no pro audio interfaces).
- The **output goes over HDMI to the TV** — the box's audio path terminates at the TV's DAC.
- The app must mix three independent buses — instrumental music, guide vocals, and the **live
  mic signal with room reverb** — and must decode `instrumental.opus`/`vocals.opus` into raw
  48 kHz float PCM inside the engine so the mixer callback never touches compressed audio.

Two hard problems drive this decision:

1. **Latency.** The Java `AudioRecord`/`AudioTrack` APIs and high-level .NET
   `MediaElement`-style media paths
   audio paths typically add 100–300 ms round trip — unusable for singing along. Google's Oboe
   library (AAudio with OpenSL ES fallback) is the supported way to reach 10–40 ms round trip
   on capable devices.

2. **Clock drift.** The USB ADC and the HDMI DAC run on **independent crystals** with
   ppm-level relative error (~50 ppm is typical → ~2.4 samples/sec at 48 kHz). Over a
   3-minute song that is ~430 samples of accumulated error. Android provides **no
   cross-device clock discipline or resampler** between an arbitrary USB capture device and
   the HDMI output HAL. Without compensation, a naive ring buffer between the two streams
   will slowly overrun or underrun, producing audible artifacts (drops/duplicates in the vocal
   mid-phrase) after minutes of singing.

## Decision

Use **Google Oboe (AAudio primary, OpenSL ES fallback)** for both streams, with an
application-owned lock-free SPSC ring and **consumer-side drift compensation**:

| Parameter | Input stream (USB mic) | Output stream (HDMI) |
|---|---|---|
| Performance mode | `LowLatency` | `LowLatency` |
| Sharing mode | `Exclusive` (auto-fallback `Shared`) | — |
| Usage / content | — | `Usage::Game`, `ContentType::Music` |
| Format | Float, mono, 48 kHz (device native) | Float, stereo, 48 kHz |
| Buffer size | burst-sized reads | `setBufferSizeInFrames(getFramesPerBurst() * 2)` |

**Real-time rules (both callbacks):** no locks, no heap allocations, no JNI. All control-plane
parameters (`musicGain`, `guideGain`, `micGain`, `dryMicGain`, `reverbWet`) are
`std::atomic<float>`. The mix graph is: SPSC ring read → 3-bus mixer → Schroeder wet on the mic
bus → HDMI out.

**Media decode lives in the engine.** NDK `AMediaExtractor` + `AMediaCodec` decode the Opus
stems into 48 kHz float PCM playback rings consumed by the mixer callback. For YouTube songs,
ExoPlayer renders video while a custom `BaseAudioProcessor` captures its decoded PCM and pushes
it via JNI into a dedicated SPSC ring (replacing ExoPlayer's own `AudioTrack` sink); video pacing
is nudged against `engine_get_position_ms()` with the same hard/soft nudge strategy as the
browser stage.

**Vocal monitoring on HDMI.** The USB-in → HDMI-out path includes TV audio DSP latency (often
30–100 ms+); a full-level dry replica of the singer's voice is speech-jamming at those delays.
`dryMicGain` therefore defaults to **0.0** and live vocals are monitored primarily through the
Schroeder bus (wet ≈ 0.25) — room ambience without a distinct echo. `dryMicGain` remains a stage
toggle for low-latency displays or headphone setups.

**Drift compensation (in the output callback, consumer side):**

```
Ring capacity N = 8192 frames (~171 ms @ 48 kHz)
Target fill = N/2;  low mark = 0.35N;  high mark = 0.65N
  fill < lowMark  → duplicate last sample (inserts time; mic slows vs faster HDMI clock)
  fill > highMark → drop 1 sample (mic speeds up)
  Rate limit: at most ONE correction per 100 ms (≤0.1% speed error — inaudible, no oscillation)
Fill level + correction events exported as atomics for a live telemetry graph on the debug page.
```

Consumer-side-only correction is deliberate: the output callback is the only place that both
knows the fill level *and* can act on it without touching the producer's timing.

## Consequences

**Positive**

- Achievable 10–40 ms mic-to-speaker round trip on capable devices — party-usable.
- **The app owns the drift problem and can display it**: a live ring-fill / corrections-per-sec
  graph on the stage debug page turns a nuisance into a portfolio differentiator that proves
  understanding of real-time audio constraints.
- SPSC ring + atomics-only control plane is textbook real-time discipline — demonstrable in
  interviews and verified by CI tests with a fake-Oboe harness.
- Oboe is Google's supported, maintained path — no bespoke NDK audio risk.

**Negative / Accepted risks**

- **Device-specific quirks**: exclusive-mode input can be revoked (another app grabs the device)
  or unsupported. Mitigation: engine auto-restarts in `Shared` mode and degrades gracefully;
  burst sizes are discovered, never assumed.
- **Drift correction is ±1-sample granularity**, so very fast lyrics could theoretically wobble
  at the correction point — inaudible at the 100 ms rate limit (≤0.1% instantaneous speed
  error).
- Requires writing and maintaining JNI/P/Invoke boundaries and a CMake+NDK build — accepted,
  as low-level C++ audio is an explicit portfolio goal of the project.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Java `AudioRecord`/`AudioTrack` (or high-level .NET media APIs) | 100–300 ms round trip; no exclusive/low-latency control; no place to implement drift compensation cleanly. |
| AAudio built-in reverb / `AudioEffect` HAL effects | Opaque HAL implementations, availability varies by TV box, no parameter control, doesn't address drift at all. |
| Ignore drift (size the buffer "large enough") | A 3-minute song accumulates ~430 samples of error at 50 ppm; any buffer that absorbs a whole evening of drift costs seconds of latency. Untenable. |
| Resample the mic to match the output clock with a software SRC | Strictly better audio quality, but a full polyphase SRC in the real-time callback is significant added complexity for an inaudible improvement over ±1-sample correction at these ppm rates. Documented as a future upgrade path. |
| External USB audio interface for both in and out | Violates the deployment constraint (USB mic + HDMI out only, no extra hardware). |

## Validation plan

- Native gtest suite runs on Linux CI with a **fake Oboe layer** simulating ±80 ppm clocks:
  the compensator must recover fill level to 35–65% band within 5 s and never correct more
  than once per 100 ms.
- Debug-only allocation guard test hook asserts zero heap allocations and zero lock
  acquisitions in both audio callbacks.
- The AMedia decoder is gtest-verified against an FFmpeg reference decode of the same demo
  Opus stems (identical PCM, tolerance for codec container padding).
- On-device checklist (`docs/tv-checklist.md`): drum-test latency screenshot on Onn 4K Pro;
  drift graph stable ±0.05% over a 10-minute session.
