import { expect, test } from '@playwright/test';

/**
 * Two-context party flow: the stage auto-creates a room, the phone joins via the QR code, and
 * both surfaces converge on the same queue. The API runs in fallback mode (no PostgreSQL): the
 * three demo songs come from the filesystem library and each one carries a deterministic
 * `enqueueId` GUID accepted by `EnqueueLibrarySong`.
 */
test('phone remote and stage stay in sync and restore state after reconnect', async ({ browser }) => {
  const stageContext = await browser.newContext();
  const phoneContext = await browser.newContext();
  const stage = await stageContext.newPage();
  const phone = await phoneContext.newPage();

  try {
    await stage.goto('/stage');
    const codeElement = stage.locator('.stage-room-code');
    await expect(codeElement).toBeVisible({ timeout: 15_000 });

    const code = ((await codeElement.textContent()) ?? '').trim();
    // 23-letter alphabet: no I/O/Q.
    expect(code).toMatch(/^[ABCDEFGHJKLMNPRSTUVWXYZ]{4}$/);

    await phone.goto(`/remote?code=${code}`);
    await expect(phone.locator('#room-code')).toHaveValue(code);
    await phone.locator('#display-name').fill('Ada');
    await phone.getByRole('button', { name: 'Join room' }).click();
    await expect(phone.locator('.lobby-code')).toHaveText(code);

    const twinkleRow = phone.locator('.library-row', { hasText: 'Twinkle Variations' });
    const canyonRow = phone.locator('.library-row', { hasText: 'Countdown Canyon' });
    await expect(twinkleRow.getByRole('button', { name: 'Add to queue' })).toBeVisible();

    await twinkleRow.getByRole('button', { name: 'Add to queue' }).click();
    await expect(twinkleRow.getByRole('button', { name: /Added/ })).toBeVisible();
    await expect(stage.locator('.stage-room-queue')).toContainText('Twinkle Variations', {
      timeout: 1_000,
    });

    await canyonRow.getByRole('button', { name: 'Add to queue' }).click();
    await expect(stage.locator('.stage-room-queue li')).toHaveCount(2, { timeout: 1_000 });
    await expect(stage.locator('.stage-room-queue')).toContainText('Countdown Canyon', {
      timeout: 1_000,
    });

    await phone.getByRole('tab', { name: /Queue/ }).click();
    const queueRows = phone.locator('.queue-row');
    await expect(queueRows).toHaveCount(2);
    await expect(queueRows.nth(0)).toContainText('Twinkle Variations');
    await expect(stage.locator('.stage-room-queue li').first()).toContainText('Twinkle Variations');

    // Reorder: move the second song to the top and observe the stage's queue order.
    await phone.getByRole('button', { name: 'Move Countdown Canyon up' }).click();
    await expect(queueRows.nth(0)).toContainText('Countdown Canyon');
    await expect(stage.locator('.stage-room-queue li').first()).toContainText('Countdown Canyon', {
      timeout: 1_000,
    });

    // Remove: the stage drops the row too.
    await phone.getByRole('button', { name: 'Remove Twinkle Variations' }).click();
    await expect(queueRows).toHaveCount(1);
    await expect(stage.locator('.stage-room-queue li')).toHaveCount(1, { timeout: 1_000 });
    await expect(stage.locator('.stage-room-queue')).not.toContainText('Twinkle Variations', {
      timeout: 1_000,
    });

    // Disconnect + rejoin: the full snapshot restores the remaining queue with no delta replay.
    await phone.reload();
    await expect(phone.locator('#room-code')).toHaveValue(code);
    await phone.locator('#display-name').fill('Ada');
    await phone.getByRole('button', { name: 'Join room' }).click();
    await expect(phone.locator('.lobby-code')).toHaveText(code);

    await phone.getByRole('tab', { name: /Queue/ }).click();
    await expect(phone.locator('.queue-row')).toHaveCount(1);
    await expect(phone.locator('.queue-row')).toContainText('Countdown Canyon');
    await expect(stage.locator('.stage-room-queue li')).toHaveCount(1, { timeout: 1_000 });
    await expect(stage.locator('.stage-room-queue')).not.toContainText('Twinkle Variations');
  } finally {
    await phoneContext.close();
    await stageContext.close();
  }
});
