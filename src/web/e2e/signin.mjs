// Sign-in smoke test of the REAL-mode build against a stub API, in tablet emulation with touch:
// `npm run e2e:signin`.
//
// Why a stub and not the mock: the bug this guards (owner's report, iPad Safari, 2026-11-22: "I
// can just keep clicking users when nothing happens") lives between the HTTP client, the session
// cookie and the 401 handling, none of which the in-browser mock exercises. The stub answers
// `dev/config`, `dev/users`, `dev/login`, `me` and `lookups` and sets the session cookie with the
// same attributes as the API (`HttpOnly; Secure; SameSite=Lax`).
//
// Scenarios (iPad viewport, touch, taps):
//   1. normal                      -> the app opens
//   2. storage throws (private)    -> the access code is asked once, the app opens
//   3. plain http on a real host   -> the browser drops the Secure cookie; the sign-in screen must
//                                     say so (and offer https), never return to the picker silently
//   4. as 3, API says https is required -> the page goes to https by itself
//   5. no cookie at all (blocked)  -> the same visible message
//   6. `dev/login` fails (500)     -> a visible error, the picker stays usable
//   7. diagnostics panel           -> shows browser, cookies, storage and the two status codes
//
// Browser: WebKit when Playwright's build is installed (`npx playwright-core install webkit`,
// set E2E_BROWSER=webkit), else Chrome with an iPad profile. Scenarios 3 and 4 need a host name
// mapped to this machine (`--host-resolver-rules`), which only Chrome offers; WebKit skips them.
import { createServer } from 'node:http';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, devices, webkit } from 'playwright-core';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const dist = resolve(process.env.E2E_DIST ?? join(root, 'node_modules', '.cache', 'e2e-signin'));
const fixtures = JSON.parse(readFileSync(join(root, 'e2e', 'fixtures', 'signin.json'), 'utf8'));
const ACCESS_CODE = 'stub-access-code';
const COOKIE = 'dafa.contracts.auth';
const FAKE_HOST = 'contracts.dafa-test.example';
const useWebkit = process.env.E2E_BROWSER === 'webkit';

const CHROME_CANDIDATES = [process.env.CHROME_PATH, '/usr/bin/google-chrome', '/usr/bin/google-chrome-stable', '/usr/bin/chromium', '/usr/bin/chromium-browser', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'];
const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.woff': 'font/woff', '.json': 'application/json' };

function check(condition, message) {
  if (!condition) throw new Error(message);
}

/** The stub API plus the static files of the build. `state` is changed per scenario. */
function startServer(state) {
  const server = createServer((req, res) => {
    const url = new URL(req.url, 'http://x');
    const send = (status, body, headers = {}) => {
      res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', ...headers });
      res.end(body === undefined ? '' : JSON.stringify(body));
    };
    if (url.pathname.startsWith('/api/')) {
      const signedIn = (req.headers.cookie ?? '').includes(`${COOKIE}=ticket`);
      const route = `${req.method} ${url.pathname.slice(5)}`;
      state.log.push(`${route}${signedIn ? ' [cookie]' : ''}`);
      if (route === 'GET dev/config') return send(200, { authMode: 'Dev', accessCodeRequired: true, httpsRequired: state.httpsRequired });
      if (route === 'GET dev/users') return signedIn || req.headers['x-dev-access-code'] === ACCESS_CODE ? send(200, fixtures.users) : send(401, { title: 'Access code required', status: 401 });
      if (route === 'POST dev/login') {
        let raw = '';
        req.on('data', (c) => (raw += c));
        req.on('end', () => {
          if (req.headers['x-requested-with'] !== 'dafa-contracts') return send(400, { title: 'Missing request header', status: 400 });
          if (state.loginFails) return send(500, { title: 'Unexpected error', status: 500, detail: 'Something went wrong. Try again, and contact IT if it keeps happening.' });
          if (JSON.parse(raw || '{}').accessCode !== ACCESS_CODE) return send(401, { title: 'Access code required', status: 401, detail: 'The access code is missing or wrong.' });
          send(200, fixtures.me, state.noCookie ? {} : { 'Set-Cookie': `${COOKIE}=ticket; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=3600` });
        });
        return undefined;
      }
      if (!signedIn) return send(401, { title: 'Unauthorized', status: 401 });
      if (route === 'GET me') return send(200, fixtures.me);
      if (route === 'GET lookups') return send(200, fixtures.lookups);
      return send(404, { title: 'Not found', status: 404 });
    }
    let file = normalize(join(dist, decodeURIComponent(url.pathname)));
    if (!file.startsWith(dist) || !existsSync(file) || statSync(file).isDirectory()) file = join(dist, 'index.html');
    res.writeHead(200, { 'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-cache' });
    res.end(readFileSync(file));
    return undefined;
  });
  return new Promise((done) => server.listen(Number(process.env.E2E_PORT ?? 0), '127.0.0.1', () => done(server)));
}

const SCENARIOS = [
  { name: 'normal', expect: 'app' },
  { name: 'storage throws (private mode)', storageThrows: true, expect: 'app' },
  { name: 'plain http on a real host name (Secure cookie dropped)', fakeHost: true, expect: 'cookie-message', chromeOnly: true },
  { name: 'plain http, API requires https', fakeHost: true, httpsRequired: true, expect: 'https-redirect', chromeOnly: true },
  { name: 'no cookie stored (cookies blocked)', noCookie: true, expect: 'cookie-message' },
  { name: 'dev/login fails', loginFails: true, expect: 'login-error' },
  { name: 'diagnostics panel', expect: 'diagnostics' },
];

async function run(browser, port, state, scenario) {
  Object.assign(state, { httpsRequired: !!scenario.httpsRequired, noCookie: !!scenario.noCookie, loginFails: !!scenario.loginFails, log: [] });
  const iPad = devices['iPad (gen 7)'];
  const context = await browser.newContext(useWebkit ? iPad : { viewport: iPad.viewport, userAgent: iPad.userAgent, deviceScaleFactor: 2, hasTouch: true, isMobile: true });
  if (scenario.storageThrows) {
    await context.addInitScript(() => {
      const fail = () => {
        throw new DOMException('The operation is insecure.', 'SecurityError');
      };
      for (const name of ['getItem', 'setItem', 'removeItem', 'clear', 'key']) Storage.prototype[name] = fail;
    });
  }
  const page = await context.newPage();
  page.setDefaultTimeout(10_000);
  const problems = [];
  page.on('pageerror', (e) => problems.push(`page error: ${e.message}`));
  const baseUrl = `http://${scenario.fakeHost ? FAKE_HOST : '127.0.0.1'}:${port}/`;
  try {
    if (scenario.expect === 'https-redirect') {
      // https is not served here; the attempt to go there is the proof.
      const wentToHttps = page.waitForRequest((r) => r.url().startsWith(`https://${FAKE_HOST}`), { timeout: 10_000 });
      await page.goto(baseUrl).catch(() => undefined);
      await wentToHttps;
      return;
    }
    await page.goto(baseUrl);
    if (scenario.expect === 'diagnostics') {
      await page.getByRole('heading', { name: 'Enter the access code' }).waitFor();
      await page.getByRole('button', { name: 'Having trouble signing in?' }).tap();
      await page.locator('.diag__row').first().waitFor();
      const text = await page.locator('.diag').innerText();
      for (const part of ['Browser', 'Cookies', 'Storage', 'Secure connection', '/api/dev/config', '200', '/api/me', '401']) check(text.includes(part), `diagnostics lacks "${part}": ${text}`);
      check(!text.includes(ACCESS_CODE), 'diagnostics must not show the access code');
      return;
    }
    await page.getByLabel('Access code').fill(ACCESS_CODE);
    await page.getByRole('button', { name: 'Continue' }).tap();
    const tile = page.locator('button.user-opt').first();
    await tile.waitFor();
    // The tile must be the element that receives a tap in its centre (nothing lies on top of it).
    const hit = await tile.evaluate((el) => {
      const r = el.getBoundingClientRect();
      const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      return !!top && (top === el || el.contains(top));
    });
    check(hit, 'Something covers the demo-user tile');
    check((await tile.evaluate((el) => el.getBoundingClientRect().height)) >= 44, 'The demo-user tile is smaller than a 44 px touch target');
    await tile.tap();
    if (scenario.expect === 'app') {
      await page.locator('.shell').waitFor();
      check(state.log.includes('GET lookups [cookie]'), 'lookups was not asked with the session cookie');
    } else if (scenario.expect === 'cookie-message') {
      const alert = page.getByRole('alert').filter({ hasText: 'did not keep the sign-in cookie' });
      await alert.waitFor();
      if (scenario.fakeHost) await page.getByRole('link', { name: /secure address/i }).waitFor();
      check((await page.locator('.shell').count()) === 0, 'The app opened without a session');
      await page.locator('button.user-opt').first().waitFor();
      check(await page.locator('button.user-opt').first().isEnabled(), 'The picker stayed locked after the failure');
    } else if (scenario.expect === 'login-error') {
      await page.getByRole('alert').filter({ hasText: 'Something went wrong' }).first().waitFor();
      check(await page.locator('button.user-opt').first().isEnabled(), 'The picker stayed locked after the failure');
    }
    check(problems.length === 0, problems.join('\n'));
  } catch (e) {
    const where = await page.evaluate(() => document.body.innerText.replace(/\s+/g, ' ').slice(0, 400)).catch(() => '(page gone)');
    throw new Error(`${e.message.split('\n')[0]}\n    screen: ${where}\n    api: ${state.log.join(', ')}\n    ${problems.join('\n    ')}`, { cause: e });
  } finally {
    // Screenshots: set `E2E_SHOTS=<folder>` to save the last screen of each scenario.
    if (process.env.E2E_SHOTS) await page.screenshot({ path: join(resolve(process.env.E2E_SHOTS), `signin-${SCENARIOS.indexOf(scenario) + 1}.png`) }).catch(() => undefined);
    await context.close();
  }
}

async function main() {
  check(existsSync(join(dist, 'index.html')), `No build in ${dist}. Run the real-mode build first.`);
  const state = { log: [] };
  const server = await startServer(state);
  const port = server.address().port;
  let browser;
  if (useWebkit) browser = await webkit.launch();
  else {
    const executablePath = CHROME_CANDIDATES.find((p) => p && existsSync(p));
    check(executablePath, 'No Chrome found. Set CHROME_PATH to a Chrome or Chromium executable.');
    browser = await chromium.launch({ executablePath, args: ['--no-sandbox', '--disable-background-networking', '--disable-component-update', '--no-first-run', `--host-resolver-rules=MAP ${FAKE_HOST} 127.0.0.1`] });
  }
  console.log(`e2e:signin  port ${port}  ${useWebkit ? 'WebKit, iPad' : 'Chrome, iPad profile'}  ${dist}`);
  let failed = false;
  try {
    for (const scenario of SCENARIOS) {
      if (useWebkit && scenario.chromeOnly) {
        console.log(`SKIP ${scenario.name} (needs a mapped host name)`);
        continue;
      }
      try {
        await run(browser, port, state, scenario);
        console.log(`PASS ${scenario.name}`);
      } catch (e) {
        failed = true;
        console.error(`FAIL ${scenario.name}: ${e.message}`);
      }
    }
  } finally {
    await browser.close();
    server.close();
  }
  if (failed) process.exit(1);
  console.log('e2e:signin passed');
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
