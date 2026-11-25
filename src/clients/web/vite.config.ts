import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

/** Same-origin dev/preview proxy onto the local API (Aspire profile uses port 5255). */
const apiProxy = {
  '/api': {
    target: 'http://127.0.0.1:5255',
    changeOrigin: true,
  },
  '/hubs': {
    target: 'http://127.0.0.1:5255',
    changeOrigin: true,
    ws: true,
  },
};

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: apiProxy,
  },
  preview: {
    port: 4173,
    strictPort: true,
    proxy: apiProxy,
  },
  test: {
    // Renderer unit tests are DOM-less (pure math/state), so node is enough.
    environment: 'node',
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
