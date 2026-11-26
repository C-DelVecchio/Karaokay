import { expect, test, type Browser, type Page } from '@playwright/test';

/**
 * A YouTube song whose cached MP4 is missing must fail loudly on the stage. The API's
 * `/api/songs/{id}/video` answers 404 in that case, and previously the stage kept its black
 * `<video>` surface while the room reported Playing.
 *
 * The 404 is simulated at the browser boundary (Playwright route) so the test does not race the
 * shared yt-dlp cache used by `youtube.spec.ts`; the API and queue state are real. The shim
 * returns a second video id for a "missing" search so this spec's download cannot warm the happy
 * path's cache entry when the suites run in parallel.
 */
async function enqueueYouTubeFixture(browser: Browser): Promise<{
  stage: Page;
  phone: Page;
  close: () => Promise<void>;
}> {
  const stageContext = await browser.newContext();
  const phoneContext = await browser.newContext();
  const stage = await stageContext.newPage();
  const phone = await phoneContext.newPage();

  // Install the failure before playback starts so the element's first fetch sees it.
  await stage.route('**/api/songs/*/video', (route) => route.fulfill({
    status: 404,
    contentType: 'application/problem+json',
    body: JSON.stringify({ title: 'Song media not found', status: 404 }),
  }));

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

  await phone.getByRole('tab', { name: 'YouTube' }).click();
  await phone.locator('#youtube-search').fill('karaoke missing fixture');

  const resultRow = phone.locator('.youtube-row[data-video-id="e2eTestVid2"]');
  await expect(resultRow).toBeVisible({ timeout: 15_000 });
  await resultRow.getByRole('button', { name: 'Add to queue' }).click();
  await expect(resultRow.getByRole('button', { name: /Added/ })).toBeVisible({ timeout: 15_000 });

  await phone.getByRole('tab', { name: /Queue/ }).click();
  const queueRow = phone.locator('.queue-row[data-youtube="true"]');
  await expect(queueRow).toHaveCount(1);
  await expect(queueRow).toHaveAttribute('data-state', '2', { timeout: 30_000 });

  return {
    stage,
    phone,
    close: async () => {
      await phoneContext.close();
      await stageContext.close();
    },
  };
}

test('missing cached video shows a clear stage error while the room plays', async ({ browser }) => {
  test.setTimeout(60_000);

  const session = await enqueueYouTubeFixture(browser);
  const { stage, phone } = session;

  try {
    await phone.getByRole('button', { name: 'Start' }).click();

    const stageMain = stage.locator('main.stage');
    await expect(stageMain).toHaveAttribute('data-video', 'true', { timeout: 15_000 });

    // The authoritative room state really is Playing even though no media can play.
    await expect(phone.locator('.playback-state')).toHaveText('Playing', { timeout: 15_000 });

    const errorCard = stage.locator('.stage-video-error');
    await expect(errorCard).toBeVisible({ timeout: 15_000 });
    await expect(stageMain).toHaveAttribute('data-video-error', 'true');
    await expect(errorCard.locator('.stage-video-error-heading')).toHaveText('Video unavailable');
    await expect(errorCard.locator('.stage-video-error-title')).toHaveText('E2E Missing Video Fixture');
    await expect(errorCard.locator('.stage-video-error-message')).toContainText('download failed');

    // This is a load failure, not an autoplay block: the tap-to-start banner must not appear.
    await expect(stage.locator('.stage-video-notice')).toHaveCount(0);
    await expect
      .poll(() => stage.evaluate(() => window.__karaokayVideo?.problem ?? null))
      .toBe('unavailable');

    // Presentation only: the phone can still skip, and the stage leaves video mode cleanly.
    await phone.getByRole('button', { name: 'Skip' }).click();
    await expect(stageMain).toHaveAttribute('data-video', 'false', { timeout: 15_000 });
    await expect(stage.locator('.stage-video-error')).toHaveCount(0);
  } finally {
    await session.close();
  }
});
