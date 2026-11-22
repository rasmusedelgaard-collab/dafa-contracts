// Sign-in helpers: the Dev-mode access code and the Entra sign-in redirect.

const CODE_KEY = 'dafa.dev.accessCode';
const REDIRECT_KEY = 'dafa.auth.redirectedAt';
let memoryCode: string | null = null;

/** The access code entered for this browser tab (sessionStorage, with an in-memory fallback). */
export function readAccessCode(): string | undefined {
  try {
    return globalThis.sessionStorage?.getItem(CODE_KEY) ?? memoryCode ?? undefined;
  } catch {
    return memoryCode ?? undefined;
  }
}

export function storeAccessCode(code: string | null): void {
  memoryCode = code;
  try {
    if (code) globalThis.sessionStorage?.setItem(CODE_KEY, code);
    else globalThis.sessionStorage?.removeItem(CODE_KEY);
  } catch {
    /* storage unavailable: the code lives in memory until the page reloads */
  }
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
  try {
    const at = Number(globalThis.sessionStorage?.getItem(REDIRECT_KEY) ?? 0);
    return at > 0 && now - at < 15_000;
  } catch {
    return false;
  }
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
    try {
      globalThis.sessionStorage?.setItem(REDIRECT_KEY, String(Date.now()));
    } catch {
      /* ignore */
    }
    window.location.assign(url);
  },
};
