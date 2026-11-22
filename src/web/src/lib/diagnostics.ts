// What the "Having trouble signing in?" panel shows: facts about this browser and this site that
// explain a failed sign-in, in words the owner can read out. No secrets: never the access code,
// a cookie value or anything a user typed.
import type { Api } from '../api/api';
import { ApiError } from '../api/api';
import { localStore, sessionStore } from './storage';

export interface DiagnosticLine {
  label: string;
  value: string;
  /** False marks a line that explains a failed sign-in. */
  ok?: boolean;
}

/** "Safari 16.6 on iPadOS 16.6", from the user-agent string. Unknown browsers show the raw string. */
export function describeBrowser(ua: string, touchPoints = 0): string {
  const version = (re: RegExp) => re.exec(ua)?.[1]?.replace(/_/g, '.');
  let system = 'unknown system';
  const ios = version(/(?:iPhone OS|CPU OS) ([\d_]+)/);
  if (/iPad/.test(ua)) system = `iPadOS ${ios ?? ''}`.trim();
  else if (/iPhone|iPod/.test(ua)) system = `iOS ${ios ?? ''}`.trim();
  // An iPad asking for the desktop site calls itself a Mac; only the touch screen gives it away.
  else if (/Macintosh/.test(ua)) system = touchPoints > 1 ? 'iPadOS (desktop site)' : `macOS ${version(/Mac OS X ([\d_]+)/) ?? ''}`.trim();
  else if (/Android/.test(ua)) system = `Android ${version(/Android ([\d.]+)/) ?? ''}`.trim();
  else if (/Windows/.test(ua)) system = 'Windows';
  else if (/Linux/.test(ua)) system = 'Linux';

  const known: [string, RegExp][] = [
    ['Edge', /Edg(?:e|A|iOS)?\/([\d.]+)/],
    ['Chrome (iOS)', /CriOS\/([\d.]+)/],
    ['Firefox (iOS)', /FxiOS\/([\d.]+)/],
    ['Firefox', /Firefox\/([\d.]+)/],
    ['Chrome', /Chrome\/([\d.]+)/],
    ['Safari', /Version\/([\d.]+).*Safari/],
  ];
  for (const [name, re] of known) {
    const v = version(re);
    if (v) return `${name} ${v} on ${system}`;
  }
  return /AppleWebKit/.test(ua) && /Mobile\//.test(ua) ? `In-app browser (WebKit) on ${system}` : ua || 'unknown';
}

/** Writes, reads and removes a cookie from script: the plain answer to "does this browser keep cookies here?" */
export function cookiesWork(): boolean {
  try {
    const name = 'dafa.probe';
    document.cookie = `${name}=1; path=/; SameSite=Lax`;
    const kept = document.cookie.split('; ').some((c) => c === `${name}=1`);
    document.cookie = `${name}=; path=/; max-age=0; SameSite=Lax`;
    return kept;
  } catch {
    return false;
  }
}

async function statusOf(call: () => Promise<unknown>): Promise<number> {
  try {
    await call();
    return 200;
  } catch (e) {
    return e instanceof ApiError ? e.status : -1;
  }
}

const statusText = (status: number) => (status === 0 ? 'no answer (network)' : status === -1 ? 'failed in the browser' : String(status));

export async function collectDiagnostics(api: Api, build: string): Promise<DiagnosticLine[]> {
  const secure = window.location.protocol === 'https:';
  const cookies = cookiesWork();
  const session = sessionStore.available();
  const local = localStore.available();
  const [config, me] = await Promise.all([statusOf(() => api.getDevConfig()), statusOf(() => api.getMe())]);
  return [
    { label: 'Browser', value: describeBrowser(navigator.userAgent, navigator.maxTouchPoints) },
    { label: 'Address', value: `${window.location.protocol}//${window.location.host}` },
    { label: 'Secure connection', value: secure ? 'yes (https)' : 'NO (http): the sign-in cookie is only kept on https', ok: secure },
    { label: 'Cookies', value: cookies ? `work${navigator.cookieEnabled ? '' : ' (but the browser reports them as off)'}` : 'BLOCKED in this browser', ok: cookies },
    { label: 'Storage', value: session && local ? 'works' : `${session ? 'tab storage works' : 'tab storage refused'}, ${local ? 'device storage works' : 'device storage refused'} (private browsing?)`, ok: session && local },
    { label: 'Online', value: navigator.onLine ? 'yes' : 'the browser reports it is offline', ok: navigator.onLine },
    { label: '/api/dev/config', value: statusText(config), ok: config === 200 },
    { label: '/api/me', value: me === 200 ? '200 (signed in)' : me === 401 ? '401 (not signed in)' : statusText(me), ok: me === 200 || me === 401 },
    { label: 'App version', value: build },
    { label: 'Checked', value: new Date().toISOString() },
  ];
}
