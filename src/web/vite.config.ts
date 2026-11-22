/// <reference types="vitest/config" />
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// API mode: `mock` (in-browser mock, default for `npm run dev`) or `real` (.NET API).
// In real mode the dev server proxies /api to the backend on :5080.
export default defineConfig(({ mode }) => {
  const env = { ...loadEnv(mode, process.cwd(), 'VITE_'), ...process.env };
  const apiMode = env.VITE_API_MODE ?? (mode === 'production' ? 'real' : 'mock');
  return {
    plugins: [react()],
    define: {
      __API_MODE__: JSON.stringify(apiMode),
    },
    build: { outDir: 'dist', sourcemap: false },
    server: {
      port: 5173,
      proxy: apiMode === 'real' ? { '/api': { target: 'http://localhost:5080', changeOrigin: true } } : undefined,
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test/setup.ts'],
      css: false,
    },
  };
});
