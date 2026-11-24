import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
  },
  preview: {
    port: 4173,
    strictPort: true,
  },
  test: {
    // Renderer unit tests are DOM-less (pure math/state), so node is enough.
    environment: 'node',
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
