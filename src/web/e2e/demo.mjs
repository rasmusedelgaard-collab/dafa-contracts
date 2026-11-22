// Smoke test of the demo build (mock API) in a real browser: `npm run e2e:demo`.
//
// Serves `dist` (built by `npm run build:demo`) with `vite preview` and runs the same journey at
// 390 px (phone) and 1440 px (desktop):
//   sign in -> switch user (the shell must change) -> create a contract from a template ->
//   approve on the Approvals page (on the phone the Reject/Approve bar must be inside the
//   viewport and really pinned) -> sign out (the sign-in screen must show).
//
// Motion stays ON: `reducedMotion` is never set, because a transform left behind by an entrance
// animation is exactly what un-pins a fixed bar (QA finding H3). The script fails if the browser
// reports `prefers-reduced-motion: reduce`.
//
// The mock keeps its data in memory, so each journey runs in one page load (no reloads).
//
// Chrome: `CHROME_PATH`, else an installed Chrome/Chromium found in the usual places.
// Screenshots: set `E2E_SHOTS=<folder>` to save one per step.
import { existsSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { preview } from 'vite';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const shotsDir = process.env.E2E_SHOTS ? resolve(process.env.E2E_SHOTS) : null;

const CHROME_CANDIDATES = [
  process.env.CHROME_PATH,
  '/usr/bin/google-chrome',
  '/usr/bin/google-chrome-stable',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
];

function findChrome() {
  const found = CHROME_CANDIDATES.find((p) => p && existsSync(p));
  if (!found) throw new Error('No Chrome found. Set CHROME_PATH to a Chrome or Chromium executable.');
  return found;
}

function check(condition, message) {
  if (!condition) throw new Error(message);
}

/** The visible one of the elements a locator matches (the shell has a desktop and a phone variant of some controls). */
const visible = (locator) => locator.filter({ visible: true }).first();

async function journey(browser, baseUrl, width, height) {
  const phone = width < 720;
  const tag = `${width}px`;
  const log = (text) => console.log(`  [${tag}] ${text}`);
  const context = await browser.newContext({ viewport: { width, height } });
  // The mock signs the first demo user in by default; start signed out so the journey begins at the sign-in screen.
  await context.addInitScript(() => {
    if (!sessionStorage.getItem('e2e.started')) {
      localStorage.setItem('dafa.mock.userId', 'signed-out');
      sessionStorage.setItem('e2e.started', '1');
    }
  });
  const page = await context.newPage();
  page.setDefaultTimeout(15_000);
  const problems = [];
  page.on('pageerror', (e) => problems.push(`page error: ${e.message}`));
  page.on('console', (m) => {
    if (m.type() === 'error') problems.push(`console error: ${m.text()}`);
  });
  let shotNo = 0;
  const shot = async (name) => {
    if (!shotsDir) return;
    shotNo += 1;
    await page.screenshot({ path: join(shotsDir, `${width}-${String(shotNo).padStart(2, '0')}-${name}.png`) });
  };

  /** Name of the signed-in user as the shell shows it (sidebar user card, or the avatar button on a phone). */
  const shellUser = async () => {
    const label = await visible(page.locator('.user-card, .topbar__avatar, .home-head__avatar')).getAttribute('aria-label');
    return (label ?? '').split(/[.,]/)[0].trim();
  };
  const openUserMenu = () => visible(page.locator('.user-card, .topbar__avatar, .home-head__avatar')).click();
  const switchTo = async (name) => {
    await openUserMenu();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: new RegExp(`^(\\S+ )?${name}`) }).click();
    await page.getByText(`You are now ${name}`).waitFor();
    await dialog.waitFor({ state: 'detached' });
  };
  const nav = (name) => visible(page.getByRole('navigation').getByRole('link', { name: new RegExp(`^${name}`) })).click();
  const canCreate = () => page.locator('a[href^="/contracts/new"]').count();

  try {
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    check(await page.evaluate(() => !window.matchMedia('(prefers-reduced-motion: reduce)').matches), 'The browser reports reduced motion; this test must run with motion on.');

    // 1. Sign in as a reader.
    await page.getByText('Choose a demo user').waitFor();
    check((await page.locator('.shell').count()) === 0, 'The shell is on screen before sign-in.');
    await shot('sign-in');
    await page.getByRole('button', { name: /Emma Nygaard/ }).click();
    await page.locator('.shell').waitFor();
    await page.getByRole('heading', { level: 1 }).first().waitFor();
    check((await shellUser()) === 'Emma Nygaard', `After sign-in the shell shows "${await shellUser()}", expected Emma Nygaard.`);
    check((await canCreate()) === 0, 'A reader is offered "New contract".');
    log('signed in as Emma Nygaard (reader): no "New contract"');
    await shot('home-reader');

    // 2. Switch user: the shell, the navigation and the permissions must be the new user's (H1).
    await switchTo('Jonas Bak');
    await page.waitForFunction(() => document.querySelectorAll('a[href^="/contracts/new"]').length > 0);
    check((await shellUser()) === 'Jonas Bak', `After the switch the shell still shows "${await shellUser()}".`);
    if (!phone) check((await page.locator('.sidebar').innerText()).includes('Jonas Bak'), 'The sidebar user card does not name the new user.');
    log('switched to Jonas Bak: shell shows the new user and "New contract" appeared');
    await shot('home-owner');

    // 3. Create a contract from a template (C1: the counterparty field keeps what is typed).
    await visible(page.locator('a[href="/contracts/new?start=template"], a[href="/contracts/new"]')).click();
    await page.getByRole('heading', { name: 'New contract' }).waitFor();
    await page.getByRole('button', { name: /Supply agreement/ }).click();
    const title = `E2E supply agreement ${width}`;
    await page.getByLabel('Title', { exact: true }).fill(title);
    const counterparty = page.getByLabel('Counterparty', { exact: true });
    await counterparty.click();
    await counterparty.pressSequentially('Smoke Test Polymers ApS', { delay: 15 });
    check((await counterparty.inputValue()) === 'Smoke Test Polymers ApS', `The counterparty field holds "${await counterparty.inputValue()}" after typing.`);
    await page.getByLabel('Contract type', { exact: true }).selectOption({ label: 'Supplier – direct materials' });
    await page.getByLabel('Legal entity', { exact: true }).selectOption({ label: 'DAFA A/S' });
    await page.getByLabel('Department', { exact: true }).selectOption({ label: 'Purchasing' });
    await page.getByLabel('Owner', { exact: true }).selectOption({ label: 'Jonas Bak' });
    await page.getByLabel('Start date', { exact: true }).fill('2027-01-01');
    await page.getByLabel('End date', { exact: true }).fill('2028-12-31');
    await page.getByLabel('Notice period (days)', { exact: true }).fill('90');
    await page.getByRole('radiogroup', { name: 'Renews automatically' }).getByRole('radio', { name: 'No' }).click();
    await page.getByLabel('Annual value', { exact: true }).fill('400000');
    await page.getByRole('radiogroup', { name: 'Change-of-control clause' }).getByRole('radio', { name: 'No' }).click();
    check((await counterparty.inputValue()) === 'Smoke Test Polymers ApS', 'The counterparty was lost while the rest of the form was filled in.');
    await shot('new-details');
    await visible(page.getByRole('button', { name: 'Continue' })).click();
    await page.getByRole('heading', { name: 'Links and obligations' }).waitFor();
    await visible(page.getByRole('button', { name: 'Continue' })).click();
    await page.getByRole('heading', { name: 'Approval', exact: true }).waitFor();
    const submits = page.getByRole('button', { name: 'Submit for approval' });
    check((await submits.count()) === 1, `Step 4 has ${await submits.count()} "Submit for approval" buttons, expected one.`);
    await shot('new-approval');
    await submits.click();
    await page.getByText(/C-\d{4}-\d{4} submitted for approval/).waitFor();
    await page.getByRole('heading', { level: 1, name: title }).waitFor();
    check((await page.locator('main').innerText()).includes('In approval'), 'The new contract is not shown as "In approval".');
    log(`created "${title}" from the Supply agreement template and submitted it`);
    await shot('record-in-approval');

    // 4. Approve as an approver with a waiting step.
    await switchTo('Rasmus Holm');
    check((await shellUser()) === 'Rasmus Holm', `After the second switch the shell shows "${await shellUser()}".`);
    await nav('Approvals');
    await page.getByRole('heading', { level: 1, name: 'Approvals' }).waitFor();
    const item = page.locator('.queue-item', { hasText: 'ERP software subscription' });
    await item.waitFor();
    if (phone) await item.click();
    const approve = visible(page.getByRole('button', { name: 'Approve', exact: true }));
    const reject = visible(page.getByRole('button', { name: 'Reject', exact: true }));
    await approve.waitFor();
    // Let the entrance animation (motion is on) run to its end before measuring.
    await page.waitForTimeout(900);
    if (phone) {
      const measure = () =>
        page.evaluate(() => {
          const bar = document.querySelector('[data-pinned-bar]');
          const box = (el) => {
            const r = el.getBoundingClientRect();
            return { top: r.top, bottom: r.bottom, left: r.left, right: r.right };
          };
          const buttons = [...bar.querySelectorAll('button')].map(box);
          // An ancestor with a transform, filter, perspective or containment would become the containing block of the fixed bar.
          const breaking = [];
          for (let el = bar.parentElement; el; el = el.parentElement) {
            const cs = getComputedStyle(el);
            if (cs.transform !== 'none' || cs.filter !== 'none' || cs.perspective !== 'none' || cs.backdropFilter !== 'none' || /paint|layout|strict|content/.test(cs.contain) || /transform|filter|perspective/.test(cs.willChange)) breaking.push(el.className || el.tagName);
          }
          return { position: getComputedStyle(bar).position, bar: box(bar), buttons, breaking, vw: window.innerWidth, vh: window.innerHeight, scrollY: window.scrollY, scrollMax: document.documentElement.scrollHeight - window.innerHeight };
        });
      const inside = (m) => m.bar.bottom <= m.vh + 0.5 && m.bar.top >= 0 && m.buttons.every((b) => b.top >= 0 && b.bottom <= m.vh + 0.5 && b.left >= 0 && b.right <= m.vw + 0.5);
      const top = await measure();
      check(top.position === 'fixed', `The Reject/Approve bar is position: ${top.position}, expected fixed.`);
      check(top.breaking.length === 0, `Ancestors of the fixed bar that un-pin it: ${top.breaking.join(', ')}`);
      check(inside(top), `The Reject/Approve bar is outside the viewport: ${JSON.stringify(top.bar)} in ${top.vw}x${top.vh}.`);
      check(Math.abs(top.bar.bottom - top.vh) <= 1, `The bar does not sit at the bottom edge: bottom ${top.bar.bottom}, viewport ${top.vh}.`);
      await shot('approval-phone');
      // On a short screen the page scrolls: the bar must stay at the bottom edge at both ends of
      // the scroll, and must not cover Delegate.
      await page.setViewportSize({ width, height: 560 });
      await page.waitForTimeout(200);
      const short = await measure();
      check(short.scrollMax > 40, `The page was expected to scroll at ${width}x560 (scroll range ${short.scrollMax}).`);
      check(inside(short) && Math.abs(short.bar.bottom - short.vh) <= 1, `At ${width}x560 the bar is not at the bottom edge: ${JSON.stringify(short.bar)}.`);
      await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
      await page.waitForTimeout(200);
      const end = await measure();
      check(end.scrollY > 40, 'The page did not scroll.');
      check(inside(end) && Math.abs(end.bar.top - short.bar.top) <= 1, `The bar moved when the page scrolled: ${short.bar.top} -> ${end.bar.top}.`);
      const delegate = visible(page.getByRole('button', { name: 'Delegate', exact: true }));
      const hit = await delegate.evaluate((el) => {
        const r = el.getBoundingClientRect();
        return el.contains(document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2));
      });
      check(hit, 'The pinned bar covers the Delegate button.');
      check((await visible(page.locator('.topbar')).count()) === 0 && (await visible(page.locator('.tabbar')).count()) === 0, 'The top bar or tab bar is still shown on the phone approval view.');
      check(await reject.isVisible(), 'Reject is not visible.');
      await shot('approval-phone-short-scrolled');
      await page.setViewportSize({ width, height });
      await page.waitForTimeout(200);
      log(`Reject/Approve bar pinned at y ${Math.round(top.bar.top)}-${Math.round(top.bar.bottom)} of ${top.vh} (and at ${width}x560 after scrolling ${Math.round(end.scrollY)} px); Delegate is not covered`);
    } else {
      check(await approve.isVisible(), 'Approve is not visible.');
      await shot('approval-desktop');
    }
    await approve.click();
    await page.getByText('Approved', { exact: true }).first().waitFor();
    await page.locator('.queue-item', { hasText: 'ERP software subscription' }).waitFor({ state: 'detached' });
    log('approved "ERP software subscription" as Rasmus Holm');
    await shot('approved');

    // 5. Sign out reaches the sign-in screen (H2).
    if (phone) await page.locator('.tabbar').waitFor();
    await openUserMenu();
    await page.getByRole('dialog').getByRole('button', { name: 'Sign out' }).click();
    await page.getByText('Choose a demo user').waitFor();
    check((await page.locator('.shell').count()) === 0, 'The shell is still on screen after sign out.');
    log('signed out: the sign-in screen is shown');
    await shot('signed-out');

    check(problems.length === 0, `Browser errors:\n    ${problems.join('\n    ')}`);
  } catch (e) {
    if (shotsDir) await page.screenshot({ path: join(shotsDir, `${width}-FAILED.png`) }).catch(() => {});
    throw new Error(`[${tag}] ${e.message}`);
  } finally {
    await context.close();
  }
}

async function main() {
  check(existsSync(join(root, 'dist', 'index.html')), 'dist/index.html is missing. Run `npm run build:demo` first (`npm run e2e:demo` does both).');
  if (shotsDir) mkdirSync(shotsDir, { recursive: true });
  const server = await preview({ root, mode: 'demo', logLevel: 'error', preview: { port: Number(process.env.E2E_PORT ?? 4873), strictPort: false, host: '127.0.0.1' } });
  const baseUrl = server.resolvedUrls?.local[0];
  check(baseUrl, 'The preview server did not report a URL.');
  const executablePath = findChrome();
  console.log(`e2e:demo  ${baseUrl}  ${executablePath}`);
  const browser = await chromium.launch({ executablePath, args: ['--no-sandbox', '--disable-background-networking', '--disable-component-update', '--no-first-run'] });
  let failed = false;
  try {
    for (const [width, height] of [
      [390, 844],
      [1440, 900],
    ]) {
      try {
        await journey(browser, baseUrl, width, height);
        console.log(`PASS ${width}px`);
      } catch (e) {
        failed = true;
        console.error(`FAIL ${e.message}`);
      }
    }
  } finally {
    await browser.close();
    await server.close();
  }
  if (failed) process.exit(1);
  console.log('e2e:demo passed at 390 and 1440 px with motion on');
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
