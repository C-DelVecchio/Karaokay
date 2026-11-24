import { expect, test } from '@playwright/test';
import { differingByteRatio, setTestClock, startSong, waitForStableCanvas } from './helpers';

const TIMESTAMPS = [
  { tMs: 0, label: 'lead-in start' },
  { tMs: 1_250, label: 'mid-countdown' },
  { tMs: 9_000, label: 'mid-phrase' },
] as const;

test('pins deterministic, distinct canvas frames at three clock anchors', async ({ page }) => {
  await startSong(page, /Twinkle Variations/);

  const canvas = page.locator('#stage-canvas');
  await expect(canvas).toBeVisible();

  const screenshots = new Map<number, Buffer>();

  for (const { tMs } of TIMESTAMPS) {
    await setTestClock(page, tMs);
    await waitForStableCanvas(page);

    const first = await canvas.screenshot();
    const second = await canvas.screenshot();
    expect(second.equals(first)).toBe(true);
    screenshots.set(tMs, first);

    const nonBackground = await page.evaluate(
      () => window.__karaokayCanvas?.countNonBackgroundPixels() ?? 0,
    );
    expect(nonBackground).toBeGreaterThan(500);
  }

  const leadIn = screenshots.get(0);
  const midCountdown = screenshots.get(1_250);
  const midPhrase = screenshots.get(9_000);
  expect(leadIn).toBeDefined();
  expect(midCountdown).toBeDefined();
  expect(midPhrase).toBeDefined();

  if (!leadIn || !midCountdown || !midPhrase) {
    return;
  }

  expect(differingByteRatio(leadIn, midCountdown)).toBeGreaterThan(0.02);
  expect(differingByteRatio(leadIn, midPhrase)).toBeGreaterThan(0.02);

  // Structural probe: the active row's glyph band must contain visible pixels at mid-phrase.
  const baselineHits = await page.evaluate(() => {
    let hits = 0;
    for (let y = 290; y <= 330; y += 10) {
      for (let x = 80; x <= 1_200; x += 10) {
        const [red, green, blue, alpha] = window.__karaokayCanvas?.samplePixel(x, y) ?? [0, 0, 0, 0];
        if (alpha > 0 && (red > 20 || green > 20 || blue > 20)) {
          hits++;
        }
      }
    }
    return hits;
  });
  expect(baselineHits).toBeGreaterThan(20);
});
