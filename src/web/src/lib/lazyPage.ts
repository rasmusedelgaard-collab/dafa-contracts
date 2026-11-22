// Lazy route chunks that survive a deploy. A page loaded before a new release still points at the
// old hashed chunk files; when the server no longer has them the dynamic import fails. The first
// failure reloads the page once (fresh index.html, new chunk names); a second failure within a
// minute is not retried but shown by the route error boundary with a "Reload" button.
import { lazy, type ComponentType } from 'react';
import { sessionStore } from './storage';

const KEY = 'dafa.chunk-reload';
const WINDOW_MS = 60_000;

/** Errors from a failed `import()` in Chrome, Firefox and Safari, and Vite's preload error. */
export function isChunkLoadError(e: unknown): boolean {
  const text = e instanceof Error ? `${e.name} ${e.message}` : String(e);
  return /Failed to fetch dynamically imported module|error loading dynamically imported module|Importing a module script failed|ChunkLoadError|Unable to preload CSS|Loading chunk .* failed/i.test(text);
}

export interface ReloadHost {
  now(): number;
  get(): string | null;
  /** False when the value could not be kept across a reload (storage refused by the browser). */
  set(value: string): unknown;
  reload(): void;
}

const browserHost: ReloadHost = {
  now: () => Date.now(),
  get: () => sessionStore.get(KEY),
  set: (v) => sessionStore.set(KEY, v),
  reload: () => window.location.reload(),
};

/** True when a reload was started (the caller should wait); false when one was tried recently. */
export function reloadOnce(host: ReloadHost = browserHost): boolean {
  const last = Number(host.get() ?? 0);
  if (last && host.now() - last < WINDOW_MS) return false;
  // Without storage the reload could not be remembered, and a chunk that is really missing would
  // reload the page for ever: then there is no automatic reload, the boundary offers one.
  if (host.set(String(host.now())) === false) return false;
  host.reload();
  return true;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function lazyPage<T extends ComponentType<any>>(load: () => Promise<T>, host: ReloadHost = browserHost) {
  return lazy(async () => {
    try {
      return { default: await load() };
    } catch (e) {
      if (isChunkLoadError(e) && reloadOnce(host)) return new Promise<never>(() => undefined);
      throw e;
    }
  });
}
