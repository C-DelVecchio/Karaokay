import { expect, test } from '@playwright/test';

test('boots the landing page', async ({ page }) => {
  await page.goto('/');

  await expect(page).toHaveTitle('Karaokay');
  await expect(page.getByRole('heading', { name: 'Karaokay' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Enter the stage' })).toBeVisible();
});

test('serves a 404 route', async ({ page }) => {
  await page.goto('/does-not-exist');

  await expect(page.getByRole('heading', { name: /404/ })).toBeVisible();
});
