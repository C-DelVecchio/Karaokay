import os from 'node:os';
import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

const port = 4173;
const baseURL = `http://127.0.0.1:${port}`;
const apiURL = 'http://127.0.0.1:5255';
const mediaRoot = path.resolve(import.meta.dirname, 'public', 'demo-tracks');
// Each Playwright run gets a fresh cache/channel so the YouTube spec always observes a real
// Queued → % → Ready progression instead of a stale cache hit from a previous local run.
// The cache lives OUTSIDE the media root, mirroring Development/Aspire and guarding the
// CachePath-relative-to-CacheDirectory convention end-to-end in no-database mode.
const runStamp = `${process.pid}-${Date.now()}`;
const ytCacheRoot = path.join(os.tmpdir(), 'karaokay-e2e');
const ytCacheDirectory = path.join(ytCacheRoot, `ytcache-${runStamp}`);
const ytStagingRoot = path.join(ytCacheRoot, `staging-${runStamp}`);
const ytDlpShim = path.resolve(import.meta.dirname, 'e2e', 'fixtures', 'yt-dlp-shim.mjs');

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  // CI keeps two retries. Locally one retry is enough: the first cold run under 8 parallel
  // workers starves the four timing-sensitive specs (the decode poll, the two position-progression
  // polls, and the youtube drift `beforeMs` assertion), and with zero local retries that cold-start
  // flake failed the lane instead of recovering.
  retries: process.env.CI ? 2 : 1,
  reporter: process.env.CI
    ? [['github'], ['html', { open: 'never' }]]
    : 'list',
  use: {
    baseURL,
    trace: 'on-first-retry',
    launchOptions: {
      args: [
        '--mute-audio',
        '--autoplay-policy=user-gesture-required',
        '--use-fake-device-for-media-stream',
        '--use-fake-ui-for-media-stream',
      ],
    },
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: [
    {
      // Fallback mode: no PostgreSQL is required — rooms live in RoomRegistry and the three
      // demo songs are served from the filesystem library. This is the same mode used by the
      // offline stage degradation tests.
      command:
        'dotnet run --project ../../apps/Karaokay.Api/Karaokay.Api.csproj '
        + '--configuration Release --no-launch-profile --urls http://127.0.0.1:5255',
      url: `${apiURL}/healthz`,
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
      env: {
        ...(process.env as Record<string, string>),
        ASPNETCORE_ENVIRONMENT: 'Development',
        // Absolute so the API content root / shell working directory can never change the library.
        MediaRoot: mediaRoot,
        // Slice 5: the UI talks to a deterministic local yt-dlp shim — no network, no real binary.
        Features__EnableYouTubeIngestion: 'true',
        YtDlp__ExecutablePath: ytDlpShim,
        YtDlp__CacheDirectory: ytCacheDirectory,
        YtDlp__StagingRoot: ytStagingRoot,
        YtDlp__RetryBackoffSeconds: '0',
        YtDlp__MaxAttempts: '1',
      },
    },
    {
      // `npm run test:e2e` builds first (vite preview serves dist/).
      command: `npm run preview -- --host 127.0.0.1 --port ${port} --strictPort`,
      url: baseURL,
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
  ],
});
