import { expect, test } from '@playwright/test';
import { audioLog, setRangeValue, startSong } from './helpers';

test('crossfades guide vocals without recreating playback sources', async ({ page }) => {
  await startSong(page, /Twinkle Variations/);

  const slider = page.locator('#guide-vocals');
  await expect(slider).toHaveValue('0.6');

  const initial = await page.evaluate(() => window.__karaokayAudio?.gains());
  expect(initial).toEqual({ instrumental: 1, guide: 0.6, master: 1 });

  const sourceEventsBefore = (await audioLog(page))
    .filter((entry) => entry === 'createBufferSource' || entry === 'start').length;

  await setRangeValue(page, '#guide-vocals', '1');
  await page.waitForTimeout(200);

  await expect.poll(
    () => page.evaluate(() => window.__karaokayAudio?.gains().guide ?? -1),
    { timeout: 1_000 },
  ).toBeCloseTo(1, 5);

  const fullVocals = await page.evaluate(() => ({
    gains: window.__karaokayAudio?.gains(),
    log: [...(window.__karaokayAudioLog ?? [])],
  }));
  expect(fullVocals.gains).toEqual({ instrumental: 1, guide: 1, master: 1 });

  const crossfades = fullVocals.log.filter((entry) => entry.startsWith('gain:crossfade:'));
  expect(crossfades.length).toBeGreaterThan(0);
  for (const entry of crossfades) {
    expect(Number(entry.slice('gain:crossfade:'.length))).toBeLessThanOrEqual(50);
  }

  await setRangeValue(page, '#guide-vocals', '0');
  await page.waitForTimeout(200);

  await expect.poll(
    () => page.evaluate(() => window.__karaokayAudio?.gains().guide ?? -1),
    { timeout: 1_000 },
  ).toBe(0);

  const final = await page.evaluate(() => ({
    gains: window.__karaokayAudio?.gains(),
    log: [...(window.__karaokayAudioLog ?? [])],
  }));
  expect(final.gains).toEqual({ instrumental: 1, guide: 0, master: 1 });

  const sourceEventsAfter = final.log
    .filter((entry) => entry === 'createBufferSource' || entry === 'start').length;
  expect(sourceEventsAfter).toBe(sourceEventsBefore);
  expect(final.log).not.toContain('createMediaElementSource');
});
