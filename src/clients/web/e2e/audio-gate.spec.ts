import { expect, test } from '@playwright/test';
import { audioLog, gotoStage, startSong, unlockStage } from './helpers';

const PLAYBACK_PATH = /^(AudioContext:created|resume|createBufferSource|start|createMediaElementSource)$/;

function playbackPathEntries(log: readonly string[]): string[] {
  return log.filter((entry) => PLAYBACK_PATH.test(entry) || entry.startsWith('decodeAudioData:'));
}

test('keeps every playback path gated until the unlock click', async ({ page }) => {
  await gotoStage(page);

  const track = page.getByRole('button', { name: /Twinkle Variations/ });
  await expect(track).toBeVisible();
  await expect(track).toBeDisabled();

  const before = await page.evaluate(() => ({
    unlocked: window.__karaokayAudio?.unlocked ?? null,
    log: [...(window.__karaokayAudioLog ?? [])],
  }));
  expect(before.unlocked).toBe(false);
  expect(playbackPathEntries(before.log)).toEqual([]);

  // The picker button is disabled; a programmatic click must remain a no-op.
  await track.evaluate((button) => (button as HTMLButtonElement).click());
  await page.waitForTimeout(100);
  await expect(page.locator('.stage')).toHaveAttribute('data-phase', 'picking');

  // Prove nothing lazily creates a context or starts decoding while the gate is up.
  await page.waitForTimeout(500);

  const after = await page.evaluate(() => ({
    unlocked: window.__karaokayAudio?.unlocked ?? null,
    log: [...(window.__karaokayAudioLog ?? [])],
  }));
  expect(after.unlocked).toBe(false);
  expect(playbackPathEntries(after.log)).toEqual([]);
  await expect(track).toBeDisabled();
});

test('unlocks, counts down, then starts audio only after the lead-in', async ({ page }) => {
  await gotoStage(page);
  await unlockStage(page);
  await page.getByRole('button', { name: /Twinkle Variations/ }).click();

  const stage = page.locator('.stage');
  await expect(stage).toHaveAttribute('data-phase', 'countdown');

  const decodeSnapshot = await page.waitForFunction(
    () => {
      const log = [...(window.__karaokayAudioLog ?? [])];
      if (!log.includes('decodeAudioData:synth-twinkle-variations')) {
        return null;
      }

      return {
        log,
        phase: document.querySelector('.stage')?.getAttribute('data-phase') ?? '',
      };
    },
    null,
    // Decoding runs concurrently with the 2 000 ms count-in. Capture the log and the phase
    // atomically the instant the decode entry appears, so a slow decode (longer than the
    // count-in) cannot race the assertions below.
    { timeout: 3_000 },
  );

  const snapshot = (await decodeSnapshot.jsonValue()) as { log: string[]; phase: string };
  expect(snapshot.log).toContain('AudioContext:created');
  expect(snapshot.log).toContain('decodeAudioData:synth-twinkle-variations');
  expect(snapshot.log).not.toContain('createMediaElementSource');

  const startIndex = snapshot.log.indexOf('start');
  const decodeIndex = snapshot.log.indexOf('decodeAudioData:synth-twinkle-variations');
  if (snapshot.phase === 'countdown') {
    // The count-in is still running: nothing may have started yet.
    expect(startIndex).toBe(-1);
  } else {
    // The decode outlasted the count-in, so playback is allowed to have started — but only after
    // the stems finished decoding.
    expect(startIndex).toBeGreaterThan(decodeIndex);
  }

  await expect(stage).toHaveAttribute('data-phase', 'playing', { timeout: 15_000 });

  const playing = await page.evaluate(() => ({
    log: [...(window.__karaokayAudioLog ?? [])],
    positionMs: window.__karaokayAudio?.positionMs() ?? -1,
  }));
  expect(playing.log).toContain('start');
  expect(playing.log.indexOf('decodeAudioData:synth-twinkle-variations'))
    .toBeLessThan(playing.log.indexOf('start'));
  // Audio begins at the end of the 2 000 ms count-in, not at buffer position zero.
  expect(playing.positionMs).toBeGreaterThanOrEqual(1_900);
  expect(playing.positionMs).toBeLessThanOrEqual(2_600);
});

test('never routes media elements and keeps the session unlock across songs', async ({ page }) => {
  await startSong(page, /Twinkle Variations/);

  // Let real playback run for a couple of seconds.
  await page.waitForTimeout(2_000);

  let log = await audioLog(page);
  expect(log).not.toContain('createMediaElementSource');
  expect(log.filter((entry) => entry === 'AudioContext:created')).toHaveLength(1);

  // Finish the song through the same callback a natural source end uses.
  await page.evaluate(() => window.__karaokayAudio?.engine.onEnded?.());
  const stage = page.locator('.stage');
  await expect(stage).toHaveAttribute('data-phase', 'ended');
  await expect(page.getByRole('button', { name: 'Click to Enable Stage Audio' })).toHaveCount(0);

  await page.getByRole('button', { name: /Countdown Canyon/ }).click();
  await expect(stage).toHaveAttribute('data-phase', 'countdown');
  await expect(stage).toHaveAttribute('data-phase', 'playing', { timeout: 15_000 });
  await expect(page.getByRole('button', { name: 'Click to Enable Stage Audio' })).toHaveCount(0);

  log = await audioLog(page);
  expect(log.filter((entry) => entry === 'AudioContext:created')).toHaveLength(1);
  expect(log).not.toContain('createMediaElementSource');
});
