import { expect, test, type Page } from '@playwright/test';

/**
 * Slice 4 authoritative playback: the phone drives Start/Pause/Resume/Skip and the browser stage
 * converges on the server anchor while using its own audio clock as ground truth.
 *
 * The API runs in its no-PostgreSQL fallback mode (see `playwright.config.ts`): the three demo
 * songs are served from the filesystem library. A real 80 ms round trip would also throttle the
 * stem downloads, so the stage's clock estimate is biased by RTT/2 = 40 ms through
 * `window.__karaokayClockBiasMs` instead: the same residual error the median clock filter leaves
 * behind, without making every media fetch slow and flaky.
 */

const RTT_CLOCK_BIAS_MS = 40;

interface PlaybackDebug {
  active: boolean;
  phase: string;
  songId: string | null;
  expectedMs: number;
  audioPositionMs: number;
  driftMs: number;
  anchorCorrectionMs: number | null;
}

async function readPlaybackDebug(page: Page): Promise<PlaybackDebug | null> {
  return page.evaluate(() => {
    const playback = window.__karaokayPlayback;
    if (!playback) {
      return null;
    }

    return {
      active: playback.active,
      phase: playback.phase,
      songId: playback.songId,
      expectedMs: playback.expectedMs(),
      audioPositionMs: playback.audioPositionMs(),
      driftMs: playback.driftMs(),
      anchorCorrectionMs: playback.anchorCorrectionMs,
    };
  });
}

async function createRoomAndJoinPhone(
  stage: Page,
  phone: Page,
): Promise<string> {
  await stage.goto('/stage');
  const codeElement = stage.locator('.stage-room-code');
  await expect(codeElement).toBeVisible({ timeout: 15_000 });
  const code = ((await codeElement.textContent()) ?? '').trim();
  expect(code).toMatch(/^[ABCDEFGHJKLMNPRSTUVWXYZ]{4}$/);

  await stage.getByRole('button', { name: 'Click to Enable Stage Audio' }).click();

  await phone.goto(`/remote?code=${code}`);
  await expect(phone.locator('#room-code')).toHaveValue(code);
  await phone.locator('#display-name').fill('Ada');
  await phone.getByRole('button', { name: 'Join room' }).click();
  await expect(phone.locator('.lobby-code')).toHaveText(code);

  return code;
}

async function enqueue(page: Page, title: string): Promise<void> {
  const row = page.locator('.library-row', { hasText: title });
  await row.getByRole('button', { name: 'Add to queue' }).click();
  await expect(row.getByRole('button', { name: /Added/ })).toBeVisible({ timeout: 10_000 });
}

test('phone start, pause/resume and skip drive the stage from the authoritative anchor', async ({
  browser,
}) => {
  test.setTimeout(90_000);

  const stageContext = await browser.newContext();
  const phoneContext = await browser.newContext();
  const stage = await stageContext.newPage();
  const phone = await phoneContext.newPage();

  try {
    await createRoomAndJoinPhone(stage, phone);
    await enqueue(phone, 'Twinkle Variations');
    await enqueue(phone, 'Countdown Canyon');

    const stageMain = stage.locator('main.stage');

    // Simulate the residual clock error of an 80 ms round trip before playback begins.
    await stage.evaluate((bias) => {
      window.__karaokayClockBiasMs = bias;
    }, RTT_CLOCK_BIAS_MS);

    await phone.getByRole('button', { name: 'Start' }).click();

    await expect(stageMain).toHaveAttribute('data-room', 'true', { timeout: 15_000 });
    await expect(stageMain).toHaveAttribute('data-phase', 'playing', { timeout: 30_000 });
    await expect(stageMain.locator('.stage-header .stage-now-playing'))
      .toContainText('Twinkle Variations');
    await expect(stageMain).toHaveAttribute('data-video', 'false');

    await expect
      .poll(
        () => stage.evaluate(() => window.__karaokayAudio?.positionMs() ?? 0),
        { timeout: 15_000 },
      )
      .toBeGreaterThan(0.2);

    // The stage started its stems on the server anchor (bias included) instead of at zero.
    await expect
      .poll(async () => Math.abs((await readPlaybackDebug(stage))?.driftMs ?? Number.POSITIVE_INFINITY), {
        timeout: 15_000,
      })
      .toBeLessThan(150);

    // Let the song reach ≈5 s, then pause from the phone.
    await expect
      .poll(
        () => stage.evaluate(() => window.__karaokayAudio?.positionMs() ?? 0),
        { timeout: 20_000 },
      )
      .toBeGreaterThan(4_500);

    await phone.getByRole('button', { name: 'Pause' }).click();
    await expect(stageMain).toHaveAttribute('data-phase', 'paused', { timeout: 10_000 });

    const paused = await readPlaybackDebug(stage);
    expect(paused?.expectedMs ?? 0).toBeGreaterThan(4_000);

    await phone.waitForTimeout(400);
    await phone.getByRole('button', { name: 'Resume' }).click();
    await expect(stageMain).toHaveAttribute('data-phase', 'playing', { timeout: 15_000 });

    // Resume restarts at the authoritative paused position; the song does not replay from zero.
    await expect
      .poll(
        () => stage.evaluate(() => window.__karaokayAudio?.positionMs() ?? 0),
        { timeout: 15_000 },
      )
      .toBeGreaterThan((paused?.expectedMs ?? 0) + 200);

    await expect
      .poll(async () => Math.abs((await readPlaybackDebug(stage))?.driftMs ?? Number.POSITIVE_INFINITY), {
        timeout: 15_000,
      })
      .toBeLessThan(150);

    // Skip advances to the next queued library song and re-aligns on its fresh anchor.
    await phone.getByRole('button', { name: 'Skip' }).click();
    await expect(stageMain.locator('.stage-header .stage-now-playing'))
      .toContainText('Countdown Canyon', { timeout: 30_000 });
    await expect(stageMain).toHaveAttribute('data-phase', 'playing', { timeout: 30_000 });

    await expect
      .poll(async () => Math.abs((await readPlaybackDebug(stage))?.driftMs ?? Number.POSITIVE_INFINITY), {
        timeout: 15_000,
      })
      .toBeLessThan(150);
  } finally {
    await phoneContext.close();
    await stageContext.close();
  }
});

test('a stage joining mid-song starts aligned to the server anchor under simulated latency', async ({
  browser,
}) => {
  test.setTimeout(90_000);

  const stageContext = await browser.newContext();
  const lateContext = await browser.newContext();
  const phoneContext = await browser.newContext();
  const stage = await stageContext.newPage();
  const lateStage = await lateContext.newPage();
  const phone = await phoneContext.newPage();

  try {
    const code = await createRoomAndJoinPhone(stage, phone);
    await enqueue(phone, 'Twinkle Variations');
    await phone.getByRole('button', { name: 'Start' }).click();

    const stageMain = stage.locator('main.stage');
    await expect(stageMain).toHaveAttribute('data-phase', 'playing', { timeout: 30_000 });
    await expect
      .poll(
        () => stage.evaluate(() => window.__karaokayAudio?.positionMs() ?? 0),
        { timeout: 20_000 },
      )
      .toBeGreaterThan(4_000);

    // Join the same room from a second stage while the song is already several seconds in.
    await lateStage.goto(`/stage?code=${code}`);
    await expect(lateStage.locator('.stage-room-code')).toHaveText(code, { timeout: 15_000 });
    await lateStage.evaluate((bias) => {
      window.__karaokayClockBiasMs = bias;
    }, RTT_CLOCK_BIAS_MS);
    await lateStage.getByRole('button', { name: 'Click to Enable Stage Audio' }).click();

    const lateMain = lateStage.locator('main.stage');
    await expect(lateMain).toHaveAttribute('data-room', 'true', { timeout: 15_000 });
    await expect(lateMain).toHaveAttribute('data-phase', 'playing', { timeout: 30_000 });

    // It joined at the anchor, not at buffer position zero.
    await expect
      .poll(
        () => lateStage.evaluate(() => window.__karaokayAudio?.positionMs() ?? 0),
        { timeout: 15_000 },
      )
      .toBeGreaterThan(4_000);

    // Removing the injected clock bias leaves the stage within the ±150 ms budget of the true
    // server position, and both stages are aligned with each other.
    const unbiasedDrift = await lateStage.evaluate(() => {
      const playback = window.__karaokayPlayback;
      if (!playback) {
        return Number.POSITIVE_INFINITY;
      }

      const bias = window.__karaokayClockBiasMs ?? 0;
      return Math.abs(playback.audioPositionMs() - (playback.expectedMs() - bias));
    });
    expect(unbiasedDrift).toBeLessThan(150);

    await expect
      .poll(async () => {
        const first = await readPlaybackDebug(stage);
        const second = await readPlaybackDebug(lateStage);
        if (!first || !second) {
          return Number.POSITIVE_INFINITY;
        }

        return Math.abs(first.audioPositionMs - second.audioPositionMs);
      }, { timeout: 15_000 })
      .toBeLessThan(150);
  } finally {
    await phoneContext.close();
    await lateContext.close();
    await stageContext.close();
  }
});
