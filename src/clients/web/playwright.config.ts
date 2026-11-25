import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';

const port = 4173;
const baseURL = `http://127.0.0.1:${port}`;
const apiURL = 'http://127.0.0.1:5255';
const mediaRoot = path.resolve(import.meta.dirname, 'public', 'demo-tracks');

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 2 : 0,
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
