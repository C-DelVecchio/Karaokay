#!/usr/bin/env node
/**
 * Opportunistic dev-container helper for `playwright test`.
 *
 * Some restricted dev hosts lack system libnspr4/libnss3 and cannot install them (no sudo).
 * When the previously extracted fallback libraries are present at the well-known temp path, this
 * launcher prepends them to LD_LIBRARY_PATH for the child process only. It is a no-op on healthy
 * dev machines and on CI, where `playwright install --with-deps chromium` provides the libraries.
 *
 * Arguments are passed straight through to Playwright, e.g. `npm run test:e2e -- --grep gate`.
 */

import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import path from 'node:path';

const webRoot = path.resolve(import.meta.dirname, '..');
const fallbackLibDir = '/tmp/opencode/pw-libs/root/usr/lib/x86_64-linux-gnu';
const env = { ...process.env };

if (existsSync(fallbackLibDir)) {
  const currentParts = (env.LD_LIBRARY_PATH ?? '').split(path.delimiter).filter(Boolean);
  if (!currentParts.includes(fallbackLibDir)) {
    env.LD_LIBRARY_PATH = [fallbackLibDir, ...currentParts].join(path.delimiter);
    console.log(`[run-e2e] using fallback runtime libraries from ${fallbackLibDir}`);
  }
}

const executable = path.join(
  webRoot,
  'node_modules',
  '.bin',
  process.platform === 'win32' ? 'playwright.cmd' : 'playwright',
);

const child = spawn(executable, ['test', ...process.argv.slice(2)], {
  cwd: webRoot,
  env,
  stdio: 'inherit',
  shell: process.platform === 'win32',
});

child.on('error', (error) => {
  console.error('[run-e2e] failed to launch Playwright:', error);
  process.exit(1);
});

child.on('close', (code) => {
  process.exit(code ?? 1);
});
