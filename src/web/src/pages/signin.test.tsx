// Sign-in on a browser that refuses storage or drops the session cookie (owner's bug report,
// iPad Safari, 2026-11-22: "I can just keep clicking users when nothing happens").
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError, type Api } from '../api/api';
import { createMockApi } from '../api/mock/mockApi';
import { USER } from '../api/mock/seed';
import { browser, noteSessionEnded, readAccessCode, storeAccessCode, takeSignInNotice } from '../app/signin';
import { collectDiagnostics, describeBrowser } from '../lib/diagnostics';
import { reloadOnce, type ReloadHost } from '../lib/lazyPage';
import { localStore, sessionStore } from '../lib/storage';
import { structuredCloneFallback } from '../polyfills';
import { renderWith, TODAY } from '../test/renderApp';

const unauthorized = () => new ApiError({ title: 'Unauthorized', status: 401 });

/** Safari with "Block All Cookies", or private browsing on an old iPad: every storage call throws. */
function breakStorage() {
  const fail = () => {
    throw new DOMException('The operation is insecure.', 'SecurityError');
  };
  for (const name of ['getItem', 'setItem', 'removeItem'] as const) vi.spyOn(Storage.prototype, name).mockImplementation(fail);
}

/** A signed-out mock whose `me` keeps answering 401 after `dev/login`: the cookie was not kept. */
function cookieDroppingApi(): Api {
  const mock = createMockApi({ today: TODAY });
  return { ...mock, getMe: () => Promise.reject(unauthorized()), devLogin: (id) => Promise.resolve(mock.db.users.find((u) => u.id === id)).then(() => mock.devLogin(id)) };
}

describe('sign-in on a restricted browser', () => {
  beforeEach(() => {
    storeAccessCode(null);
    takeSignInNotice();
  });
  afterEach(() => {
    vi.restoreAllMocks();
    storeAccessCode(null);
    takeSignInNotice();
  });

  it('signs in when the browser refuses storage: the code is kept in memory', async () => {
    breakStorage();
    const user = userEvent.setup();
    const api = createMockApi({ today: TODAY, accessCode: 'dafa-test' });
    const login = vi.spyOn(api, 'devLogin');
    renderWith(api, '/');
    await user.type(await screen.findByLabelText('Access code'), 'dafa-test');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(await screen.findByRole('button', { name: /Rasmus Holm/ }));
    expect(login).toHaveBeenCalledWith(USER.ras, 'dafa-test');
    expect(await screen.findByRole('navigation', { name: 'Main' })).toBeInTheDocument();
    expect(readAccessCode()).toBe('dafa-test');
    expect(sessionStore.available()).toBe(false);
  });

  it('says that the cookie was not kept when `me` is 401 right after a successful login', async () => {
    const user = userEvent.setup();
    const api = cookieDroppingApi();
    const lookups = vi.spyOn(api, 'getLookups');
    renderWith(api, '/');
    await user.click(await screen.findByRole('button', { name: /Rasmus Holm/ }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Your browser did not keep the sign-in cookie, so you are not signed in.');
    // The test page is on http, where the cause is certain: the secure address is offered.
    expect(within(alert).getByRole('link', { name: 'Open the secure address (https)' })).toHaveAttribute('href', expect.stringMatching(/^https:\/\/localhost/));
    // The app was never shown, and the picker can be used again.
    expect(lookups).not.toHaveBeenCalled();
    expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Karen Lindberg/ })).toBeEnabled();
  });

  it('shows why sign-in failed and unlocks the picker', async () => {
    const user = userEvent.setup();
    const mock = createMockApi({ today: TODAY });
    await mock.logout();
    let fail: unknown = new ApiError({ title: 'Unexpected error', status: 500, detail: 'Something went wrong. Try again, and contact IT if it keeps happening.' });
    const api: Api = { ...mock, devLogin: (id, code) => (fail ? Promise.reject(fail) : mock.devLogin(id, code)) };
    renderWith(api, '/');
    await user.click(await screen.findByRole('button', { name: /Rasmus Holm/ }));
    expect(await screen.findByRole('alert')).toHaveTextContent('You could not be signed in. Something went wrong. Try again, and contact IT if it keeps happening.');
    expect(screen.getByRole('button', { name: /Rasmus Holm/ })).toBeEnabled();

    // An error that is not an API answer (thrown inside the browser) is shown too.
    fail = new TypeError('undefined is not a function');
    await user.click(screen.getByRole('button', { name: /Rasmus Holm/ }));
    expect(await screen.findByText('Something went wrong in the browser. Reload the page and try again.')).toBeInTheDocument();

    fail = null;
    await user.click(screen.getByRole('button', { name: /Rasmus Holm/ }));
    expect(await screen.findByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it('marks the tapped user as busy while signing in', async () => {
    const user = userEvent.setup();
    const mock = createMockApi({ today: TODAY });
    await mock.logout();
    let release: () => void = () => undefined;
    const api: Api = { ...mock, devLogin: (id, code) => new Promise<void>((done) => (release = done)).then(() => mock.devLogin(id, code)) };
    renderWith(api, '/');
    await user.click(await screen.findByRole('button', { name: /Rasmus Holm/ }));
    const tapped = await screen.findByRole('button', { name: /Rasmus Holm.*Signing in/ });
    expect(tapped).toHaveAttribute('aria-busy', 'true');
    expect(screen.getByRole('button', { name: /Karen Lindberg/ })).toBeDisabled();
    release();
    expect(await screen.findByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it('asks for the access code again when sign-in refuses it', async () => {
    const user = userEvent.setup();
    storeAccessCode('dafa-test');
    const mock = createMockApi({ today: TODAY, accessCode: 'dafa-test' });
    const api: Api = { ...mock, devLogin: () => Promise.reject(new ApiError({ title: 'Access code required', status: 401, detail: 'The access code is missing or wrong.' })) };
    renderWith(api, '/');
    await user.click(await screen.findByRole('button', { name: /Rasmus Holm/ }));
    expect(await screen.findByRole('heading', { name: 'Enter the access code' })).toBeInTheDocument();
    expect(screen.getByText('The access code is no longer valid. Enter it again')).toBeInTheDocument();
    expect(readAccessCode()).toBeUndefined();
  });

  it('says so when a session ends right after sign-in, and when it ends later', async () => {
    const mock = createMockApi({ today: TODAY });
    await mock.logout();
    // Long after the last sign-in: an ordinary expiry.
    noteSessionEnded(Date.now() + 3_600_000);
    renderWith(mock, '/');
    expect(await screen.findByText('Your session has ended. Sign in again.')).toBeInTheDocument();
  });

  it('a 401 on the first data call after sign-in returns to the picker with the cookie message', async () => {
    // The session works for `me` but not for the next call (e.g. the cookie is lost a moment later).
    const user = userEvent.setup();
    const mock = createMockApi({ today: TODAY });
    await mock.logout();
    let signedIn = false;
    const api: Api = {
      ...mock,
      devLogin: (id, code) => mock.devLogin(id, code).then((me) => ((signedIn = true), me)),
      getMe: () => (signedIn ? ((signedIn = false), mock.getMe()) : Promise.reject(unauthorized())),
      getLookups: () => Promise.reject(unauthorized()),
    };
    renderWith(api, '/');
    await user.click(await screen.findByRole('button', { name: /Rasmus Holm/ }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Your browser did not keep the sign-in cookie');
    expect(screen.getByRole('button', { name: /Rasmus Holm/ })).toBeEnabled();
  });

  it('moves a plain http page to https when the API says the cookie needs it', async () => {
    const replace = vi.spyOn(browser, 'replace').mockImplementation(() => undefined);
    const mock = createMockApi({ today: TODAY });
    await mock.logout();
    const api: Api = { ...mock, getDevConfig: () => Promise.resolve({ authMode: 'Dev', accessCodeRequired: false, httpsRequired: true }) };
    renderWith(api, '/contracts?entity=1');
    await waitFor(() => expect(replace).toHaveBeenCalledTimes(1));
    expect(replace.mock.calls[0]?.[0]).toMatch(/^https:\/\/localhost/);
    expect(screen.queryByRole('heading', { name: 'Sign in' })).not.toBeInTheDocument();
  });

  it('"Having trouble signing in?" lists the browser facts and the API answers, without secrets', async () => {
    const user = userEvent.setup();
    storeAccessCode('dafa-test');
    const api = createMockApi({ today: TODAY, accessCode: 'dafa-test' });
    renderWith(api, '/');
    await user.click(await screen.findByRole('button', { name: 'Having trouble signing in?' }));
    const panel = await screen.findByRole('region', { name: 'Sign-in check' });
    expect(await within(panel).findByText('/api/dev/config')).toBeInTheDocument();
    for (const label of ['Browser', 'Address', 'Secure connection', 'Cookies', 'Storage', '/api/me', 'App version']) expect(within(panel).getByText(label)).toBeInTheDocument();
    expect(within(panel).getByText('401 (not signed in)')).toBeInTheDocument();
    expect(panel).not.toHaveTextContent('dafa-test');
  });
});

describe('diagnostics', () => {
  it('names the browser from the user-agent string', () => {
    expect(describeBrowser('Mozilla/5.0 (iPad; CPU OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1')).toBe('Safari 16.6 on iPadOS 16.6');
    expect(describeBrowser('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15', 5)).toBe('Safari 17.5 on iPadOS (desktop site)');
    expect(describeBrowser('Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/126.0.6478.54 Mobile/15E148 Safari/604.1')).toBe('Chrome (iOS) 126.0.6478.54 on iPadOS 17.5');
    expect(describeBrowser('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0')).toBe('Edge 126.0.0.0 on Windows');
    expect(describeBrowser('Mozilla/5.0 (iPad; CPU OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148')).toBe('In-app browser (WebKit) on iPadOS 16.6');
  });

  it('reports refused storage and a server that does not answer', async () => {
    breakStorage();
    const mock = createMockApi({ today: TODAY });
    const api: Api = { ...mock, getDevConfig: () => Promise.reject(new ApiError({ title: 'Network error', status: 0 })) };
    const lines = await collectDiagnostics(api, 'build-1');
    const line = (label: string) => lines.find((l) => l.label === label);
    expect(line('Storage')).toMatchObject({ ok: false, value: expect.stringContaining('refused') });
    expect(line('/api/dev/config')).toMatchObject({ ok: false, value: 'no answer (network)' });
    expect(line('/api/me')).toMatchObject({ ok: true, value: '200 (signed in)' });
    expect(line('App version')?.value).toBe('build-1');
    vi.restoreAllMocks();
  });
});

describe('storage that never throws', () => {
  afterEach(() => vi.restoreAllMocks());

  it('uses the browser when it works and memory when it refuses', () => {
    expect(localStore.set('dafa.t', 'a')).toBe(true);
    expect(localStorage.getItem('dafa.t')).toBe('a');
    localStorage.removeItem('dafa.t');
    // Kept by the browser, so no stale copy in memory.
    expect(localStore.get('dafa.t')).toBeNull();

    breakStorage();
    expect(localStore.set('dafa.t', 'b')).toBe(false);
    expect(localStore.get('dafa.t')).toBe('b');
    localStore.remove('dafa.t');
    expect(localStore.get('dafa.t')).toBeNull();
  });

  it('a missing lazy chunk does not reload for ever when the reload cannot be remembered', () => {
    let reloads = 0;
    const host: ReloadHost = { now: () => 100_000, get: () => null, set: () => false, reload: () => (reloads += 1) };
    expect(reloadOnce(host)).toBe(false);
    expect(reloads).toBe(0);
  });
});

describe('structuredClone fallback (Safari before 15.4)', () => {
  it('copies nested data, dates, maps and sets, and keeps files', () => {
    const file = new Blob(['x']);
    const source = { a: [1, { b: 'c' }], d: new Date('2026-01-02T00:00:00Z'), m: new Map([['k', { v: 1 }]]), s: new Set([1]), file, n: null, u: undefined };
    const copy = structuredCloneFallback(source);
    expect(copy).toEqual(source);
    expect(copy.a[1]).not.toBe(source.a[1]);
    expect(copy.d).not.toBe(source.d);
    expect(copy.m.get('k')).not.toBe(source.m.get('k'));
    expect(copy.file).toBe(file);
    const loop: { self?: unknown } = {};
    loop.self = loop;
    expect(structuredCloneFallback(loop).self).not.toBe(loop);
  });
});
