// Sign-in helpers: the Dev-mode access code and the Entra sign-in redirect.

import type { Api } from '../api/api';
import { ApiError } from '../api/api';
import type { Me } from '../api/types';
import { sessionStore } from '../lib/storage';

const CODE_KEY = 'dafa.dev.accessCode';
const REDIRECT_KEY = 'dafa.auth.redirectedAt';

/**
 * The access code entered for this browser tab. Kept in sessionStorage; when the browser refuses
 * storage (private browsing, blocked cookies) it lives in memory, and is asked again after a reload.
 */
export function readAccessCode(): string | undefined {
  return sessionStore.get(CODE_KEY) ?? undefined;
}

export function storeAccessCode(code: string | null): void {
  if (code) sessionStore.set(CODE_KEY, code);
  else sessionStore.remove(CODE_KEY);
}

/**
 * `dev/login` answered 200 but the very next request was not signed in: the browser did not keep
 * (or did not send back) the session cookie. Owner's bug report, iPad Safari, 2026-11-22: the app
 * then went back to the demo-user picker without a word, so tapping a user "did nothing".
 */
export class SessionNotKeptError extends Error {
  constructor() {
    super('The browser did not keep the sign-in cookie');
    this.name = 'SessionNotKeptError';
  }
}

/**
 * Signs in as a demo user and proves the session works before the app is shown: `dev/login`, then
 * `me` with the new cookie. A 401 on that `me` becomes a `SessionNotKeptError`.
 */
export async function signInAsDemoUser(api: Api, userId: string, accessCode?: string): Promise<Me> {
  await api.devLogin(userId, accessCode);
  try {
    const me = await api.getMe();
    lastSignInAt = Date.now();
    return me;
  } catch (e) {
    if (e instanceof ApiError && e.status === 401) throw new SessionNotKeptError();
    throw e;
  }
}

/** True on a plain `http://` page. The session cookie is `Secure`: browsers only keep it on https. */
export function isInsecurePage(): boolean {
  return typeof window !== 'undefined' && window.location.protocol === 'http:';
}

/** The same page on https. */
export function secureUrl(): string {
  const { host, pathname, search, hash } = window.location;
  return `https://${host}${pathname}${search}${hash}`;
}

export type SignInNotice = 'session-ended' | 'cookie-not-kept';
let lastSignInAt = 0;
let pendingNotice: SignInNotice | null = null;

/**
 * Called when a 401 ends the session (app/queryClient.ts). The sign-in screen then says why it is
 * showing; a session that ends within half a minute of sign-in was never really kept.
 */
export function noteSessionEnded(now = Date.now()): void {
  pendingNotice = lastSignInAt > 0 && now - lastSignInAt < 30_000 ? 'cookie-not-kept' : 'session-ended';
}

/** The reason the sign-in screen is showing, once. */
export function takeSignInNotice(): SignInNotice | null {
  const notice = pendingNotice;
  pendingNotice = null;
  return notice;
}

/** `/api/auth/login?returnUrl=<current path>`; the API signs the user in with Entra and comes back. */
export function entraLoginUrl(path: string): string {
  return `/api/auth/login?returnUrl=${encodeURIComponent(path || '/')}`;
}

export function currentPath(): string {
  const { pathname, search, hash } = window.location;
  return `${pathname}${search}${hash}`;
}

/**
 * True when this tab was sent to sign-in a moment ago. Then a second 401 means sign-in did not
 * stick, and redirecting again would loop; the app shows a "Sign in" link instead.
 */
export function redirectedRecently(now = Date.now()): boolean {
  const at = Number(sessionStore.get(REDIRECT_KEY) ?? 0);
  return at > 0 && now - at < 15_000;
}

let signedOut = false;
/** Set when the user signs out, so Entra mode shows "You are signed out" instead of signing in again at once. */
export function markSignedOut(value = true): void {
  signedOut = value;
}
export function isSignedOut(): boolean {
  return signedOut;
}

/** The one place the app leaves the SPA, so tests can replace it. */
export const browser = {
  redirect(url: string): void {
    sessionStore.set(REDIRECT_KEY, String(Date.now()));
    window.location.assign(url);
  },
  /** http -> https for the same page; not a sign-in redirect, so it leaves no loop marker. */
  replace(url: string): void {
    window.location.replace(url);
  },
};
