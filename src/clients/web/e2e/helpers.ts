import { expect, type Page } from '@playwright/test';

export async function gotoStage(page: Page): Promise<void> {
  await page.goto('/stage');
  await expect(page.locator('.stage')).toHaveAttribute('data-phase', 'picking');
}

export async function unlockStage(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Click to Enable Stage Audio' }).click();
}

export async function startSong(page: Page, title: RegExp): Promise<void> {
  await gotoStage(page);
  await unlockStage(page);
  await page.getByRole('button', { name: title }).click();
  await expect(page.locator('.stage')).toHaveAttribute('data-phase', 'playing', {
    timeout: 15_000,
  });
}

export async function setTestClock(page: Page, tMs: number): Promise<void> {
  await page.evaluate((value) => window.__karaokayTestClock?.set(value), tMs);
  await waitForFrames(page, 2);
}

export async function waitForFrames(page: Page, frameCount: number): Promise<void> {
  await page.evaluate((count) => new Promise<void>((resolve) => {
    let remaining = count;
    const tick = () => {
      remaining -= 1;
      if (remaining <= 0) {
        resolve();
        return;
      }
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }), frameCount);
}

export async function waitForStableCanvas(page: Page): Promise<void> {
  await waitForFrames(page, 2);
  await page.waitForTimeout(50);
}

export async function audioLog(page: Page): Promise<string[]> {
  return page.evaluate(() => [...(window.__karaokayAudioLog ?? [])]);
}

export async function setRangeValue(page: Page, selector: string, value: string): Promise<void> {
  await page.locator(selector).evaluate((element, nextValue) => {
    const input = element as HTMLInputElement;
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    setter?.call(input, nextValue);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }, value);
}

/** Fraction of differing bytes; 1 when the encoded screenshots have different lengths. */
export function differingByteRatio(left: Uint8Array, right: Uint8Array): number {
  if (left.length !== right.length) {
    return 1;
  }

  let differing = 0;
  for (let index = 0; index < left.length; index++) {
    if (left[index] !== right[index]) {
      differing++;
    }
  }

  return differing / left.length;
}
