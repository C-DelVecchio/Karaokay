/**
 * Typed mirror of the canonical `lyrics.json` document (docs/ARCHITECTURE.md §4.1).
 * Property names match the JSON wire names exactly so fetched documents need no mapping.
 */

export interface TempoMapEntry {
  tick: number;
  bpm: number;
  beatMs: number;
}

export interface Syllable {
  text: string;
  startMs: number;
  endMs: number;
  talky: boolean;
  connectsToNext: boolean;
  lineBreakAfter: boolean;
}

export interface LyricLine {
  syllables: Syllable[];
}

export interface Phrase {
  startMs: number;
  endMs: number;
  lines: LyricLine[];
}

export interface PhraseGap {
  fromMs: number;
  toMs: number;
  countdownBeats: number;
  beatMs: number;
  renderCountdown: boolean;
}

export interface VocalPart {
  part: string;
  isDefaultRenderPart: boolean;
  phrases: Phrase[];
  gaps: PhraseGap[];
}

export interface LyricsDocument {
  version: number;
  songId: string;
  source: string;
  title: string;
  artist: string;
  durationMs: number;
  ticksPerQuarterNote: number;
  tempoMap: TempoMapEntry[];
  initialSilenceMs: number;
  vocalParts: VocalPart[];
}

/** The render target: exactly one part is flagged, with a defensive fallback to the first part. */
export function defaultRenderPart(lyrics: LyricsDocument): VocalPart | undefined {
  return lyrics.vocalParts.find((part) => part.isDefaultRenderPart) ?? lyrics.vocalParts[0];
}

/** The lead-in countdown gap (starting at 0 ms), if the song has one. */
export function findLeadInGap(lyrics: LyricsDocument): PhraseGap | undefined {
  const part = defaultRenderPart(lyrics);
  if (!part) {
    return undefined;
  }

  return part.gaps.find((gap) => gap.renderCountdown && gap.fromMs === 0)
    ?? part.gaps.find((gap) => gap.renderCountdown);
}
