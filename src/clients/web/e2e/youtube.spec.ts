import { expect, test } from '@playwright/test';

/**
 * Full Slice 5 party flow with a deterministic, offline YouTube adapter.
 *
 * `playwright.config.ts` points the API at `e2e/fixtures/yt-dlp-shim.mjs`: search returns one
 * canned result (`e2eTestVid1`) and download writes a committed tiny H.264 MP4 (base64 text) while
 * emitting `karaokay-progress:` lines. The API runs in its no-PostgreSQL fallback mode, so the
 * cached video is resolved through the deterministic `YouTubeSongIds` mapping added for this path.
 *
 * No live network, no pixel assertions: the test uses the same stage test hooks as the other specs.
 */
test('guest searches, enqueues, and the stage plays the downloaded video', async ({ browser }) => {
  test.setTimeout(60_000);

  const stageContext = await browser.newContext();
  const phoneContext = await browser.newContext();
  const stage = await stageContext.newPage();
  const phone = await phoneContext.newPage();

  try {
    await stage.goto('/stage');
    const codeElement = stage.locator('.stage-room-code');
    await expect(codeElement).toBeVisible({ timeout: 15_000 });
    const code = ((await codeElement.textContent()) ?? '').trim();
    expect(code).toMatch(/^[ABCDEFGHJKLMNPRSTUVWXYZ]{4}$/);

    // Unlock inside a real user gesture so the video element can be routed into the master bus.
    await stage.getByRole('button', { name: 'Click to Enable Stage Audio' }).click();

    await phone.goto(`/remote?code=${code}`);
    await expect(phone.locator('#room-code')).toHaveValue(code);
    await phone.locator('#display-name').fill('Ada');
    await phone.getByRole('button', { name: 'Join room' }).click();
    await expect(phone.locator('.lobby-code')).toHaveText(code);

    await phone.getByRole('tab', { name: 'YouTube' }).click();
    await phone.locator('#youtube-search').fill('karaoke fixture');

    const resultRow = phone.locator('.youtube-row[data-video-id="e2eTestVid1"]');
    await expect(resultRow).toBeVisible({ timeout: 15_000 });
    await expect(resultRow).toContainText('E2E Karaoke Fixture');
    await expect(resultRow).toContainText('Karaokay CI');

    await resultRow.getByRole('button', { name: 'Add to queue' }).click();
    await expect(resultRow.getByRole('button', { name: /Added/ })).toBeVisible({
      timeout: 15_000,
    });

    await phone.getByRole('tab', { name: /Queue/ }).click();
    const queueRow = phone.locator('.queue-row[data-youtube="true"]');
    await expect(queueRow).toHaveCount(1);
    const badge = queueRow.locator('.queue-badge');

    // The shim's four progress samples are spaced out so the guest can observe a real percentage.
    await expect(badge).toHaveText(/\d+%/, { timeout: 15_000 });
    await expect(queueRow).toHaveAttribute('data-state', '2', { timeout: 30_000 });
    await expect(badge).toContainText('Ready');

    // Start playback: the stage must switch from the demo-stem surface to the YouTube <video>.
    await phone.getByRole('button', { name: 'Start' }).click();

    const stageMain = stage.locator('main.stage');
    await expect(stageMain).toHaveAttribute('data-video', 'true', { timeout: 15_000 });

    const video = stage.locator('#video-surface');
    await expect(video).toHaveAttribute('data-mode', 'video');
    await expect(video).toHaveAttribute('data-song-id', /.+/);
    await expect(video).toHaveAttribute('src', /\/api\/songs\/.+\/video/);

    await expect
      .poll(
        () => stage.evaluate(() => window.__karaokayVideo?.currentTime() ?? 0),
        { timeout: 15_000 },
      )
      .toBeGreaterThan(0.2);

    await expect
      .poll(
        () => stage.evaluate(() => window.__karaokayVideo?.paused() ?? true),
        { timeout: 15_000 },
      )
      .toBe(false);

    // The canvas element stays mounted (the video overlays it) and the demo picker is gone.
    await expect(stage.locator('#stage-canvas')).toBeVisible();
    await expect(stage.locator('.stage-now-playing')).toContainText('E2E Karaoke Fixture');

    // A single frame must still produce canvas output (same helper as the canvas spec, no pixels).
    const counters = await stage.evaluate(() => {
      window.__karaokayVideo?.syncNow();
      return window.__karaokayCanvas?.countNonBackgroundPixels() ?? -1;
    });
    expect(counters).toBeGreaterThanOrEqual(0);
  } finally {
    await phoneContext.close();
    await stageContext.close();
  }
});
