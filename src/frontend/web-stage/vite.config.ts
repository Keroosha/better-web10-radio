import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Dev-only: forward the v0 API to the backend host (Dockerfile exposes 8080).
      // @web10/shared issues relative `/api/v0/...` paths, so production is same-origin.
      '/api': { target: 'http://localhost:8080', changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
  },
});
