/**
 * Canvas lyric renderer (docs/ARCHITECTURE.md §6.1). All measurement and layout happens once in
 * `load()`. The per-frame `render(T)` path only reads precomputed structures, mutates numeric
 * scratch fields, sets canvas state, and issues draw calls: no measureText, no allocations.
 */

import type { LyricsDocument, PhraseGap } from '../lyrics';
import {
  clamp01,
  defaultPartOf,
  easeOut,
  findActiveGapIndex,
  flattenLines,
  wordFillAt,
} from './decisions';
import {
  DEFAULT_MAX_LINE_WIDTH_EM,
  layoutLines,
  type LayoutRow,
} from './layout';

export const STAGE_WIDTH = 1280;
export const STAGE_HEIGHT = 720;

const MARGIN_X = 80;
const ROW_BASELINES = [190, 330, 470, 610];
const DOT_Y = 64;
const DOT_RADIUS = 11;
const DOT_SPACING = 28;
const TAU = Math.PI * 2;
const PREVIOUS_FADE_MS = 300;
const DIM_SCALE = 0.85;

const TEXT_FILLED = '#f5f7ff';
const TEXT_UNFILLED = '#525866';
const DIM_TEXT_FILLED = '#9aa0ad';
const DIM_TEXT_UNFILLED = '#3f4450';
const FADE_TEXT_FILLED = '#c7cbd5';
const FADE_TEXT_UNFILLED = '#4a4f5b';
const DOT_STROKE = '#7d8494';
const DOT_FILLED = '#f5f7ff';

interface SyllableRuntime {
  text: string;
  startMs: number;
  endMs: number;
  talky: boolean;
  connectsToNext: boolean;
  lineBreakAfter: boolean;
  wordGroupStartMs: number;
  wordGroupEndMs: number;
  x: number;
  width: number;
}

interface UnitRuntime {
  startMs: number;
  endMs: number;
  wordGroupStartMs: number;
  wordGroupEndMs: number;
  isWordGroup: boolean;
  isTalky: boolean;
  x: number;
  width: number;
  firstSyllableIndex: number;
  lastSyllableIndex: number;
}

interface RowRuntime {
  sourceLineIndex: number;
  firstStartMs: number;
  lastEndMs: number;
  width: number;
  syllables: SyllableRuntime[];
  units: UnitRuntime[];
}

export interface CanvasLyricsRendererOptions {
  /** Visible drawing context; defaults to `canvas.getContext('2d')`. Injectable for tests. */
  context?: CanvasRenderingContext2D;
  /** Offscreen measurement context; defaults to a detached canvas. Injectable for tests. */
  measureContext?: CanvasRenderingContext2D;
  em?: number;
  maxLineWidthEm?: number;
}

declare global {
  interface Window {
    __karaokayCanvas?: {
      samplePixel: (x: number, y: number) => [number, number, number, number];
      countNonBackgroundPixels: () => number;
    };
  }
}

function buildUnits(syllables: readonly SyllableRuntime[]): UnitRuntime[] {
  const units: UnitRuntime[] = [];
  let index = 0;

  while (index < syllables.length) {
    const first = syllables[index];

    // Talky syllables always fill as a step, even when the importer marked connectsToNext.
    if (first.talky) {
      units.push({
        startMs: first.startMs,
        endMs: first.endMs,
        wordGroupStartMs: -1,
        wordGroupEndMs: -1,
        isWordGroup: false,
        isTalky: true,
        x: first.x,
        width: first.width,
        firstSyllableIndex: index,
        lastSyllableIndex: index,
      });
      index++;
      continue;
    }

    let endIndex = index;
    while (
      endIndex < syllables.length - 1
      && syllables[endIndex].connectsToNext
      && !syllables[endIndex + 1].talky
    ) {
      endIndex++;
    }

    const last = syllables[endIndex];
    units.push({
      startMs: first.startMs,
      endMs: last.endMs,
      wordGroupStartMs: first.wordGroupStartMs,
      wordGroupEndMs: first.wordGroupEndMs,
      isWordGroup: first.wordGroupStartMs >= 0,
      isTalky: false,
      x: first.x,
      width: last.x + last.width - first.x,
      firstSyllableIndex: index,
      lastSyllableIndex: endIndex,
    });

    index = endIndex + 1;
  }

  return units;
}

export class CanvasLyricsRenderer {
  private readonly canvas: HTMLCanvasElement;
  private readonly ctx: CanvasRenderingContext2D;
  private readonly measureCtx: CanvasRenderingContext2D;
  private readonly em: number;
  private readonly maxLineWidthEm: number;
  private readonly font: string;
  private readonly dimFont: string;

  private dpr = 1;
  private capHeight = 0;
  private loaded = false;
  private rows: RowRuntime[] = [];
  private gaps: readonly PhraseGap[] = [];
  private dotCenters: Float64Array[] = [];

  // Reused per-row scratch fields: the render loop must not allocate.
  private activeUnitIndex = -1;
  private activeUnitFill = 0;
  private pulseSyllableIndex = -1;
  private pulseScale = 1;

  constructor(canvas: HTMLCanvasElement, options: CanvasLyricsRendererOptions = {}) {
    this.canvas = canvas;
    this.em = options.em ?? 48;
    this.maxLineWidthEm = options.maxLineWidthEm ?? DEFAULT_MAX_LINE_WIDTH_EM;
    this.font = `bold ${this.em}px system-ui, sans-serif`;
    this.dimFont = `bold ${this.em * DIM_SCALE}px system-ui, sans-serif`;

    const context = options.context ?? canvas.getContext('2d');
    if (!context) {
      throw new Error('2d canvas context is unavailable');
    }
    this.ctx = context;

    if (options.measureContext) {
      this.measureCtx = options.measureContext;
    } else {
      if (typeof document === 'undefined') {
        throw new Error('An offscreen measure context is required outside the browser');
      }
      const offscreen = document.createElement('canvas');
      const measureContext = offscreen.getContext('2d');
      if (!measureContext) {
        throw new Error('2d offscreen canvas context is unavailable');
      }
      this.measureCtx = measureContext;
    }
  }

  /** Measures and lays out the song once; subsequent `render()` calls are allocation-free. */
  load(lyrics: LyricsDocument): void {
    this.dpr = typeof window !== 'undefined' ? Math.max(1, window.devicePixelRatio || 1) : 1;
    this.canvas.width = STAGE_WIDTH * this.dpr;
    this.canvas.height = STAGE_HEIGHT * this.dpr;
    this.ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    this.ctx.font = this.font;
    this.ctx.textBaseline = 'alphabetic';
    this.measureCtx.font = this.font;

    const measured = new Map<string, number>();
    const measure = (text: string): number => {
      const cached = measured.get(text);
      if (cached !== undefined) {
        return cached;
      }

      const width = this.measureCtx.measureText(text).width;
      measured.set(text, width);
      return width;
    };

    measure('0');
    const m = this.measureCtx.measureText('M');
    measured.set('M', m.width);
    this.capHeight = m.actualBoundingBoxAscent || this.em * 0.72;

    const flatLines = flattenLines(lyrics);
    const runtimeByFlatIndex: SyllableRuntime[] = [];

    for (let lineIndex = 0; lineIndex < flatLines.length; lineIndex++) {
      const line = flatLines[lineIndex];
      for (let syllableIndex = 0; syllableIndex < line.syllables.length; syllableIndex++) {
        const syllable = line.syllables[syllableIndex];
        const width = measure(syllable.text);
        runtimeByFlatIndex[syllable.flatIndex] = {
          text: syllable.text,
          startMs: syllable.startMs,
          endMs: syllable.endMs,
          talky: syllable.talky,
          connectsToNext: syllable.connectsToNext,
          lineBreakAfter: syllable.lineBreakAfter,
          wordGroupStartMs: syllable.wordGroupStartMs ?? -1,
          wordGroupEndMs: syllable.wordGroupEndMs ?? -1,
          x: 0,
          width,
        };
      }
    }

    const layout = layoutLines(flatLines, measure, this.em, this.maxLineWidthEm);
    this.rows = [];
    for (let rowIndex = 0; rowIndex < layout.rows.length; rowIndex++) {
      this.rows.push(this.createRow(layout.rows[rowIndex], runtimeByFlatIndex));
    }

    const part = defaultPartOf(lyrics);
    this.gaps = part ? part.gaps : [];
    this.dotCenters = [];
    for (let gapIndex = 0; gapIndex < this.gaps.length; gapIndex++) {
      const gap = this.gaps[gapIndex];
      if (!gap.renderCountdown || gap.countdownBeats <= 0) {
        this.dotCenters.push(new Float64Array(0));
        continue;
      }

      const centers = new Float64Array(gap.countdownBeats);
      const totalWidth = (gap.countdownBeats - 1) * DOT_SPACING;
      const startX = STAGE_WIDTH / 2 - totalWidth / 2;
      for (let dotIndex = 0; dotIndex < gap.countdownBeats; dotIndex++) {
        centers[dotIndex] = startX + dotIndex * DOT_SPACING;
      }
      this.dotCenters.push(centers);
    }

    this.loaded = true;
    this.clear();
  }

  /** Clears the visible canvas. */
  clear(): void {
    this.ctx.clearRect(0, 0, STAGE_WIDTH, STAGE_HEIGHT);
  }

  /** Draws one frame from the audio clock (or the test-clock override). */
  render(tMs: number): void {
    if (!this.loaded) {
      return;
    }

    const ctx = this.ctx;
    ctx.clearRect(0, 0, STAGE_WIDTH, STAGE_HEIGHT);

    const rows = this.rows;
    if (rows.length > 0) {
      const activeRow = this.activeRowIndex(tMs);
      const firstRow = activeRow > 0 ? activeRow - 1 : 0;
      const lastRow = Math.min(rows.length - 1, activeRow + 2);

      for (let rowIndex = firstRow; rowIndex <= lastRow; rowIndex++) {
        this.drawRow(rows[rowIndex], tMs, rowIndex - activeRow + 1);
      }
    }

    ctx.globalAlpha = 1;
    this.drawCountdown(tMs);
  }

  /** Reads one logical pixel (scaled by DPR) through `getImageData`. Debug/test only. */
  samplePixel(x: number, y: number): [number, number, number, number] {
    const data = this.ctx.getImageData(
      Math.round(x * this.dpr),
      Math.round(y * this.dpr),
      1,
      1,
    ).data;

    return [data[0], data[1], data[2], data[3]];
  }

  /** Test-only coarse probe: counts visible (non-background) device pixels on the canvas. */
  countNonBackgroundPixels(): number {
    const { data } = this.ctx.getImageData(0, 0, this.canvas.width, this.canvas.height);
    let count = 0;

    for (let index = 0; index < data.length; index += 4) {
      if (data[index + 3] > 0 && (data[index] > 20 || data[index + 1] > 20 || data[index + 2] > 20)) {
        count++;
      }
    }

    return count;
  }

  private createRow(row: LayoutRow, runtimeByFlatIndex: readonly SyllableRuntime[]): RowRuntime {
    const syllables: SyllableRuntime[] = [];
    let firstStartMs = Number.POSITIVE_INFINITY;
    let lastEndMs = Number.NEGATIVE_INFINITY;

    for (let index = 0; index < row.syllables.length; index++) {
      const laidOut = row.syllables[index];
      const runtime = runtimeByFlatIndex[laidOut.flatIndex];
      runtime.x = laidOut.x;
      runtime.width = laidOut.width;
      syllables.push(runtime);
      firstStartMs = Math.min(firstStartMs, runtime.startMs);
      lastEndMs = Math.max(lastEndMs, runtime.endMs);
    }

    return {
      sourceLineIndex: row.sourceLineIndex,
      firstStartMs: Number.isFinite(firstStartMs) ? firstStartMs : 0,
      lastEndMs: Number.isFinite(lastEndMs) ? lastEndMs : 0,
      width: row.width,
      syllables,
      units: buildUnits(syllables),
    };
  }

  private activeRowIndex(tMs: number): number {
    const rows = this.rows;
    for (let index = 0; index < rows.length; index++) {
      if (rows[index].lastEndMs >= tMs) {
        return index;
      }
    }

    return rows.length - 1;
  }

  private drawRow(row: RowRuntime, tMs: number, slot: number): void {
    const isPrevious = slot === 0;
    const isUpcoming = slot >= 2;

    let alpha = 1;
    if (isPrevious) {
      alpha = clamp01(1 - (tMs - row.lastEndMs) / PREVIOUS_FADE_MS);
      if (alpha <= 0) {
        return;
      }
    }

    const scale = isUpcoming ? DIM_SCALE : 1;
    const baseline = ROW_BASELINES[slot];
    const ctx = this.ctx;
    ctx.font = isUpcoming ? this.dimFont : this.font;
    ctx.globalAlpha = alpha;

    const clipY = baseline - this.capHeight - this.em * 0.2;
    const clipHeight = this.em * 1.35;
    const partialWidth = this.computeRowFill(row, tMs) * scale;

    this.pulseSyllableIndex = -1;
    this.pulseScale = 1;
    if (slot === 1) {
      this.computePulse(row, tMs);
    }

    ctx.save();
    ctx.beginPath();
    ctx.rect(MARGIN_X, clipY, partialWidth, clipHeight);
    ctx.clip();
    ctx.fillStyle = isUpcoming ? DIM_TEXT_FILLED : isPrevious ? FADE_TEXT_FILLED : TEXT_FILLED;
    this.drawRowText(row, baseline, scale);
    ctx.restore();

    ctx.fillStyle = isUpcoming ? DIM_TEXT_UNFILLED : isPrevious ? FADE_TEXT_UNFILLED : TEXT_UNFILLED;
    this.drawRowText(row, baseline, scale);
  }

  private drawRowText(row: RowRuntime, baseline: number, scale: number): void {
    const ctx = this.ctx;
    const syllables = row.syllables;

    for (let index = 0; index < syllables.length; index++) {
      const syllable = syllables[index];
      const x = MARGIN_X + syllable.x * scale;

      if (index === this.pulseSyllableIndex && this.pulseScale !== 1) {
        const centerX = MARGIN_X + (syllable.x + syllable.width / 2) * scale;
        ctx.save();
        ctx.translate(centerX, baseline);
        ctx.scale(this.pulseScale, 1);
        ctx.translate(-centerX, -baseline);
        ctx.fillText(syllable.text, x, baseline);
        ctx.restore();
      } else {
        ctx.fillText(syllable.text, x, baseline);
      }
    }
  }

  /** Returns the filled width of the row and records the active unit/fill in scratch fields. */
  private computeRowFill(row: RowRuntime, tMs: number): number {
    let partialWidth = 0;
    this.activeUnitIndex = -1;
    this.activeUnitFill = 0;

    const units = row.units;
    for (let index = 0; index < units.length; index++) {
      const unit = units[index];
      if (tMs < unit.startMs) {
        break;
      }

      const fill = this.unitFill(unit, tMs);
      if (tMs < unit.endMs) {
        this.activeUnitIndex = index;
        this.activeUnitFill = fill;
        partialWidth = unit.x + unit.width * fill;
        break;
      }

      partialWidth = unit.x + unit.width;
    }

    return partialWidth;
  }

  private computePulse(row: RowRuntime, tMs: number): void {
    this.pulseSyllableIndex = -1;
    this.pulseScale = 1;

    const unitIndex = this.activeUnitIndex;
    if (unitIndex < 0) {
      return;
    }

    const fill = this.activeUnitFill;
    if (fill <= 0 || fill >= 1) {
      return;
    }

    const unit = row.units[unitIndex];
    const syllables = row.syllables;
    let chosen = -1;

    for (let index = unit.firstSyllableIndex; index <= unit.lastSyllableIndex; index++) {
      const syllable = syllables[index];
      if (syllable.startMs <= tMs) {
        chosen = index;
      }
      if (syllable.startMs <= tMs && tMs < syllable.endMs) {
        chosen = index;
        break;
      }
    }

    if (chosen < 0 || syllables[chosen].talky) {
      return;
    }

    this.pulseSyllableIndex = chosen;
    this.pulseScale = 1 + 0.15 * 4 * fill * (1 - fill);
  }

  private unitFill(unit: UnitRuntime, tMs: number): number {
    if (unit.isTalky) {
      return tMs >= unit.startMs ? 1 : 0;
    }

    if (unit.isWordGroup) {
      const startMs = unit.wordGroupStartMs >= 0 ? unit.wordGroupStartMs : unit.startMs;
      const endMs = unit.wordGroupEndMs >= 0 ? unit.wordGroupEndMs : unit.endMs;
      return wordFillAt(startMs, endMs, tMs);
    }

    const progress = clamp01((tMs - unit.startMs) / Math.max(1, unit.endMs - unit.startMs));
    const ease = easeOut(Math.min(1, Math.max(0, (tMs - unit.startMs) / 50)));
    return progress * ease;
  }

  private drawCountdown(tMs: number): void {
    const gapIndex = findActiveGapIndex(this.gaps, tMs);
    if (gapIndex < 0) {
      return;
    }

    const gap = this.gaps[gapIndex];
    const centers = this.dotCenters[gapIndex];
    const ctx = this.ctx;
    ctx.globalAlpha = 1;
    ctx.lineWidth = 2;
    ctx.strokeStyle = DOT_STROKE;
    ctx.fillStyle = DOT_FILLED;

    for (let dotIndex = 0; dotIndex < gap.countdownBeats; dotIndex++) {
      const centerX = centers[dotIndex];
      const boundary = gap.toMs - (dotIndex + 1) * gap.beatMs;
      const fill = clamp01((tMs - boundary) / gap.beatMs);

      ctx.beginPath();
      ctx.arc(centerX, DOT_Y, DOT_RADIUS, 0, TAU);
      ctx.stroke();

      if (fill > 0) {
        ctx.save();
        ctx.beginPath();
        ctx.rect(centerX - DOT_RADIUS, DOT_Y - DOT_RADIUS, 2 * DOT_RADIUS * fill, 2 * DOT_RADIUS);
        ctx.clip();
        ctx.beginPath();
        ctx.arc(centerX, DOT_Y, DOT_RADIUS, 0, TAU);
        ctx.fill();
        ctx.restore();
      }
    }
  }
}
