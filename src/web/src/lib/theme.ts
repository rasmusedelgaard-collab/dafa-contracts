// Light / dark / system theme (owner item I). The preference is stored per user through
// `me/preferences` and copied to localStorage so `public/theme-init.js` can apply it before the
// first paint, the sign-in screen included.
import type { ThemePreference } from '../api/types';
import { localStore } from './storage';

const KEY = 'dafa.theme';
let media: MediaQueryList | null = null;
let listener: (() => void) | null = null;

export function readStoredTheme(): ThemePreference {
  const v = localStore.get(KEY);
  return v === 'dark' || v === 'system' ? v : 'light';
}

export function resolveTheme(pref: ThemePreference, prefersDark: boolean): 'light' | 'dark' {
  return pref === 'dark' || (pref === 'system' && prefersDark) ? 'dark' : 'light';
}

/** Applies the preference to <html data-theme> and remembers it; "system" follows the device. */
export function applyTheme(pref: ThemePreference): void {
  // Storage unavailable: the theme still applies for this page.
  localStore.set(KEY, pref);
  if (media && listener) media.removeEventListener?.('change', listener);
  media = typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  const set = () => document.documentElement.setAttribute('data-theme', resolveTheme(pref, !!media?.matches));
  set();
  if (pref === 'system' && media) {
    listener = set;
    media.addEventListener?.('change', listener);
  } else listener = null;
  const meta = document.querySelector('meta[name="theme-color"]');
  meta?.setAttribute('content', document.documentElement.getAttribute('data-theme') === 'dark' ? '#0f1620' : '#00456c');
}
