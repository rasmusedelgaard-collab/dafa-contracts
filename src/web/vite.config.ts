/// <reference types="vitest/config" />
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// API mode: `mock` (in-browser mock, default for `npm run dev`) or `real` (.NET API).
// In real mode the dev server proxies /api to the backend on :5080.
// The Vite mode picks it, so the npm scripts need no inline environment variables (which do
// not work in Windows shells) and no env files (the repository ignores `.env*`):
//   `vite --mode real` -> real, `vite build --mode demo` -> mock, `vite build` -> real.
// `VITE_API_MODE` still overrides all of these.
const MODE_DEFAULT: Record<string, 'mock' | 'real'> = { real: 'real', production: 'real', demo: 'mock' };

export default defineConfig(({ mode }) => {
  const env = { ...loadEnv(mode, process.cwd(), 'VITE_'), ...process.env };
  const apiMode = env.VITE_API_MODE ?? MODE_DEFAULT[mode] ?? 'mock';
  return {
    plugins: [react()],
    define: {
      __API_MODE__: JSON.stringify(apiMode),
      __BUILD_ID__: JSON.stringify(mode === 'test' ? 'test' : `${new Date().toISOString().slice(0, 16)}Z ${apiMode}`),
    },
    // An explicit floor instead of Vite's moving default: iPads that no longer get updates stay
    // on Safari 14/15. Syntax and CSS are lowered to this; `src/polyfills.ts` adds the few newer
    // library functions the app uses. (No top-level await: see main.tsx.)
    build: { outDir: 'dist', sourcemap: false, target: ['es2020', 'safari14'], cssTarget: ['safari14'] },
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
