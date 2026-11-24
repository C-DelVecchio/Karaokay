import { describe, expect, it, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import type { LyricsDocument } from '../lyrics';
import { CanvasLyricsRenderer } from './CanvasLyricsRenderer';

function loadLyrics(songId: string): LyricsDocument {
  return JSON.parse(
    readFileSync(new URL(`../../../../public/demo-tracks/${songId}/lyrics.json`, import.meta.url), 'utf8'),
  ) as LyricsDocument;
}

function createContextStub() {
  const context = {
    font: '',
    textBaseline: '',
    fillStyle: '',
    strokeStyle: '',
    lineWidth: 0,
    globalAlpha: 1,
    setTransform: vi.fn(),
    clearRect: vi.fn(),
    save: vi.fn(),
    restore: vi.fn(),
    beginPath: vi.fn(),
    rect: vi.fn(),
    clip: vi.fn(),
    fillText: vi.fn(),
    arc: vi.fn(),
    stroke: vi.fn(),
    fill: vi.fn(),
    translate: vi.fn(),
    scale: vi.fn(),
    measureText: vi.fn((text: string) => ({
      width: text.length * 22,
      actualBoundingBoxAscent: 34,
    })),
    getImageData: vi.fn(() => ({ data: new Uint8ClampedArray([12, 34, 56, 255]) })),
  };

  return context;
}

describe('CanvasLyricsRenderer', () => {
  it('never calls measureText during render after load', () => {
    const context = createContextStub();
    const measureContext = createContextStub();
    const canvas = {
      width: 0,
      height: 0,
      getContext: () => context,
    } as unknown as HTMLCanvasElement;

    const renderer = new CanvasLyricsRenderer(canvas, {
      context: context as unknown as CanvasRenderingContext2D,
      measureContext: measureContext as unknown as CanvasRenderingContext2D,
    });
    const lyrics = loadLyrics('synth-countdown-canyon');

    renderer.load(lyrics);
    expect(measureContext.measureText).toHaveBeenCalled();

    measureContext.measureText.mockClear();

    for (const tMs of [0, 250, 500, 1000, 1500, 2000, 14000, 16000, 31000, 39000]) {
      renderer.render(tMs);
    }

    expect(measureContext.measureText).toHaveBeenCalledTimes(0);
    expect(context.fillText).toHaveBeenCalled();
  });

  it('samples logical pixels through the device-pixel transform', () => {
    const context = createContextStub();
    const measureContext = createContextStub();
    const canvas = {
      width: 0,
      height: 0,
      getContext: () => context,
    } as unknown as HTMLCanvasElement;

    const renderer = new CanvasLyricsRenderer(canvas, {
      context: context as unknown as CanvasRenderingContext2D,
      measureContext: measureContext as unknown as CanvasRenderingContext2D,
    });
    renderer.load(loadLyrics('synth-twinkle-variations'));

    expect(renderer.samplePixel(10, 20)).toEqual([12, 34, 56, 255]);
    expect(context.getImageData).toHaveBeenCalledWith(10, 20, 1, 1);
  });
});
