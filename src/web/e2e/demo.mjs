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
// Release 3 adds a second journey per width (`journeyR3`): Overview drill-down as two users with
// different totals, search finds another owner's document, and (desktop) an admin imports a
// supplier CSV and adds a custom field that then appears in New contract. It also adds a legal-entity
// filter on Overview through the filter bar and through a chart drill-down and removes each with
// the breadcrumb and with Clear all; the totals must return to the unfiltered values.
//
// Release 4 adds `journeyR4`: removing filters (Search, top-bar scope), a register header filter,
// template categories, e-signature (simulated) for a permitted and a non-permitted user, the logo
// upload and the dark theme.
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
    // Release 4 (item C): the counterparty is a type-ahead; typed text alone is not kept, a click picks it.
    const counterparty = page.getByRole('combobox', { name: 'Counterparty', exact: true });
    await counterparty.click();
    await counterparty.pressSequentially('Smoke Test Polymers', { delay: 10 });
    await page.getByRole('listbox').getByText('No match. Check the spelling.').waitFor();
    await counterparty.fill('');
    await counterparty.pressSequentially('nordpoly', { delay: 10 });
    await page.getByRole('listbox').getByRole('option', { name: /Nordpolymer/ }).first().click();
    const pickedCounterparty = await counterparty.inputValue();
    check(/^Nordpolymer/.test(pickedCounterparty), `The counterparty picker holds "${pickedCounterparty}" after picking.`);
    await page.getByLabel('Contract type', { exact: true }).selectOption({ label: 'Supplier – direct materials' });
    await page.getByLabel('Legal entity', { exact: true }).selectOption({ label: 'DAFA A/S' });
    await page.getByLabel('Department', { exact: true }).selectOption({ label: 'Purchasing' });
    // Owner: type part of the name, then click the match (item C).
    const owner = page.getByRole('combobox', { name: 'Owner', exact: true });
    if (!(await owner.inputValue()).startsWith('Jonas Bak')) {
      await owner.click();
      await owner.pressSequentially('Jonas', { delay: 10 });
      await page.getByRole('listbox').getByRole('option', { name: /Jonas Bak/ }).click();
    }
    await page.getByLabel('Start date', { exact: true }).fill('2027-01-01');
    await page.getByLabel('End date', { exact: true }).fill('2028-12-31');
    await page.getByLabel('Notice period (days)', { exact: true }).fill('90');
    await page.getByRole('radiogroup', { name: 'Renews automatically' }).getByRole('radio', { name: 'No' }).click();
    const annual = page.getByLabel('Annual value', { exact: true });
    await annual.click();
    await annual.pressSequentially('400000', { delay: 10 });
    // Release 4 (item D): thousand separators while typing, in the General settings format (demo: en-GB).
    check((await annual.inputValue()) === '400,000', `The annual value shows "${await annual.inputValue()}" while typing, expected "400,000".`);
    await page.getByRole('radiogroup', { name: 'Change-of-control clause' }).getByRole('radio', { name: 'No' }).click();
    check((await counterparty.inputValue()) === pickedCounterparty, 'The counterparty was lost while the rest of the form was filled in.');
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

/**
 * Release 3 journey (one page load, mock state in memory): Overview drill-down as two users with
 * different totals, search finds another owner's document, admin imports a supplier CSV, admin
 * adds a custom field and it appears in New contract. On a phone only the first two run.
 */
async function journeyR3(browser, baseUrl, width, height) {
  const phone = width < 720;
  const tag = `${width}px r3`;
  const log = (text) => console.log(`  [${tag}] ${text}`);
  const context = await browser.newContext({ viewport: { width, height } });
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
  const shot = async (name) => {
    if (shotsDir) await page.screenshot({ path: join(shotsDir, `${width}-r3-${name}.png`), fullPage: !phone });
  };
  const openUserMenu = () => visible(page.locator('.user-card, .topbar__avatar, .home-head__avatar')).click();
  const switchTo = async (name) => {
    await openUserMenu();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: new RegExp(`^(\\S+ )?${name}`) }).click();
    await page.getByText(`You are now ${name}`).waitFor();
    await dialog.waitFor({ state: 'detached' });
  };
  /** Overview through the sidebar (desktop) or the user menu (phone, where the tab bar has no Overview). */
  const openOverview = async () => {
    if (phone) {
      await openUserMenu();
      await page.getByRole('dialog').getByRole('link', { name: 'Overview' }).click();
    } else {
      await visible(page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Overview' })).click();
    }
    await page.getByRole('heading', { level: 1, name: 'Overview' }).waitFor();
    await page.locator('.kpi').first().waitFor();
  };
  const kpi = async (label) => {
    const tile = page.locator('.kpi', { has: page.locator('.kpi__label', { hasText: new RegExp(`^${label}$`) }) }).first();
    return (await tile.locator('.kpi__value').innerText()).trim();
  };
  const settingsSection = async (label) => {
    if (!(await page.locator('.settings').count())) await visible(page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Settings' })).click();
    await page.getByRole('navigation', { name: 'Settings' }).getByRole('link', { name: label, exact: true }).click();
    await page.getByRole('heading', { level: 2, name: new RegExp(label === 'Counterparties' ? 'counterparties' : `^${label}`, 'i') }).first().waitFor();
  };

  try {
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await page.getByText('Choose a demo user').waitFor();
    await page.getByRole('button', { name: /Rasmus Holm/ }).click();
    await page.locator('.shell').waitFor();

    // 1. Overview as group management, then drill down by department.
    await openOverview();
    const cfoCount = Number(await kpi('Contracts'));
    const cfoValue = await kpi('Annual value');
    const bars = page.getByRole('list', { name: 'Annual value by department' });
    const firstBar = bars.getByRole('button').first();
    const barLabel = (await firstBar.getAttribute('aria-label')) ?? '';
    const deptName = barLabel.split(':')[0];
    await firstBar.click();
    const crumbs = page.getByRole('navigation', { name: 'Applied drill-downs' });
    await crumbs.getByText(deptName, { exact: true }).waitFor();
    await page.waitForFunction((n) => {
      const t = [...document.querySelectorAll('.kpi')].find((k) => k.querySelector('.kpi__label')?.textContent === 'Contracts');
      return t && Number(t.querySelector('.kpi__value')?.textContent) < n;
    }, cfoCount);
    const drilled = Number(await kpi('Contracts'));
    await page.getByText(new RegExp(`^${drilled} contracts? match the drill-down$`)).waitFor();
    check(drilled > 0 && drilled < cfoCount, `Drilling into ${deptName} gave ${drilled} of ${cfoCount} contracts.`);
    log(`Rasmus Holm (group management): ${cfoCount} contracts, ${cfoValue} a year; drill-down "${deptName}" -> ${drilled} contracts in the list`);
    await shot('overview-cfo-drilled');
    await crumbs.getByRole('button', { name: `Remove ${deptName}` }).click();
    await crumbs.waitFor({ state: 'detached' });

    // 1b. A legal-entity filter can be removed again (owner's bug report): added in the filter bar
    // and by a chart drill-down, removed with the breadcrumb and with Clear all; the totals must
    // return to the unfiltered values and stay there (nothing may put `?entity=` back).
    const entitySelect = page.getByRole('combobox', { name: 'Entity', exact: true });
    const entityOption = await entitySelect.locator('option').nth(1).getAttribute('value');
    const entityBars = page.getByRole('list', { name: 'Annual value by legal entity' });
    const adders = {
      'the filter bar': async () => entitySelect.selectOption(entityOption),
      'a chart drill-down': async () => entityBars.getByRole('button').first().click(),
    };
    const removers = {
      'the breadcrumb': async () => crumbs.getByRole('button', { name: /^Remove / }).first().click(),
      'Clear all': async () => crumbs.getByRole('button', { name: 'Clear all' }).click(),
    };
    for (const [added, add] of Object.entries(adders)) {
      for (const [removed, remove] of Object.entries(removers)) {
        await add();
        await crumbs.waitFor();
        await page.waitForFunction((n) => {
          const t = [...document.querySelectorAll('.kpi')].find((k) => k.querySelector('.kpi__label')?.textContent === 'Contracts');
          return t && Number(t.querySelector('.kpi__value')?.textContent) < n;
        }, cfoCount);
        const entityCount = Number(await kpi('Contracts'));
        check(new URL(page.url()).searchParams.has('entity'), `Adding an entity with ${added} did not put it in the URL.`);
        await remove();
        await crumbs.waitFor({ state: 'detached' });
        await page.waitForFunction(
          ([n, v]) => {
            const val = (label) => [...document.querySelectorAll('.kpi')].find((k) => k.querySelector('.kpi__label')?.textContent === label)?.querySelector('.kpi__value')?.textContent;
            return Number(val('Contracts')) === n && val('Annual value') === v && !document.querySelector('.overview [aria-busy="true"]');
          },
          [cfoCount, cfoValue],
        );
        // Wait a moment: the release 3 bug wrote the entity back into the URL right after removal.
        await page.waitForTimeout(400);
        check(!new URL(page.url()).searchParams.has('entity'), `The entity came back in the URL after removing it with ${removed} (added with ${added}).`);
        check(!(await crumbs.count()), `The entity breadcrumb came back after removing it with ${removed} (added with ${added}).`);
        check((await entitySelect.inputValue()) === '', `The Entity filter still shows a value after removing it with ${removed}.`);
        check(Number(await kpi('Contracts')) === cfoCount && (await kpi('Annual value')) === cfoValue, `Totals did not return to ${cfoCount} / ${cfoValue} after removing the entity with ${removed}.`);
        if (!phone) check((await visible(page.getByRole('combobox', { name: 'Legal entity scope' })).inputValue()) === '', `The top-bar entity scope still shows an entity after removing it with ${removed}.`);
        log(`entity added with ${added} (${entityCount} contracts), removed with ${removed}: back to ${cfoCount} contracts, ${cfoValue}`);
      }
    }
    await shot('overview-entity-removed');

    // 2. The same page as a reader shows only her scope (different totals).
    await switchTo('Emma Nygaard');
    await openOverview();
    const readerCount = Number(await kpi('Contracts'));
    const readerValue = await kpi('Annual value');
    check(readerCount > 0 && readerCount < cfoCount, `The reader sees ${readerCount} contracts, group management ${cfoCount}; expected fewer but some.`);
    check(readerValue !== cfoValue, 'The reader sees the same annual value as group management.');
    const readerBars = page.getByRole('list', { name: 'Annual value by department' }).getByRole('button');
    check((await readerBars.count()) === 1, `The reader sees ${await readerBars.count()} departments, expected only her own.`);
    await readerBars.first().click();
    await page.getByRole('navigation', { name: 'Applied drill-downs' }).waitFor();
    log(`Emma Nygaard (reader): ${readerCount} contracts, ${readerValue} a year (scoped); drill-down works`);
    await shot('overview-reader');

    // 3. Search finds a document of another owner, inside the user's access.
    await switchTo('Jonas Bak');
    // Phone My work has its own header without the top bar: go to Contracts, where the search box is.
    if (phone) await visible(page.getByRole('navigation', { name: 'Main, phone' }).getByRole('link', { name: /^Contracts/ })).click();
    const box = visible(page.getByRole('combobox', { name: /Search contracts, documents and people/ }));
    await box.click();
    await box.pressSequentially('FSC certified', { delay: 10 });
    await box.press('Enter');
    await page.getByRole('heading', { level: 1, name: 'Search' }).waitFor();
    await page.getByText('Packaging supply – signed.pdf').waitFor();
    check(await page.getByText(/Owner Camilla Frost/).isVisible(), 'The document hit does not name its owner.');
    check((await page.locator('mark').count()) >= 2, 'The matched words are not highlighted.');
    log('Jonas Bak found "Packaging supply – signed.pdf" (owner Camilla Frost) by words in the document');
    await shot('search');

    if (!phone) {
      // 4. Admin imports a supplier CSV.
      await switchTo('Peter Aagaard');
      await settingsSection('Imports');
      const csv = 'Name;VAT number;Country;External id\nE2E Gummi ApS;DK55667788;DK;V77001\nNordpolymer Compounds ApS;;DK;V10040\n';
      await page.locator('input[type="file"]').setInputFiles({ name: 'vendors-e2e.csv', mimeType: 'text/csv', buffer: Buffer.from(csv, 'utf8') });
      await page.getByText(/2 rows · 4 columns/).waitFor();
      await shot('import-mapping');
      await page.getByRole('button', { name: 'Check 2 rows' }).click();
      await page.getByRole('table', { name: 'Rows to import' }).waitFor();
      await page.getByRole('button', { name: 'Import 1 row' }).click();
      await page.getByText(/Import of vendors-e2e\.csv finished: 1 created, 0 updated, 1 skipped, 0 failed/).waitFor();
      await shot('import-done');
      await settingsSection('Counterparties');
      await page.getByRole('searchbox', { name: 'Search counterparties' }).fill('E2E Gummi');
      await page.getByRole('table', { name: 'Counterparties' }).getByText('E2E Gummi ApS').waitFor();
      log('Peter Aagaard imported a supplier CSV (1 created, 1 skipped as existing); the supplier is in the master data');

      // 5. Admin adds a custom field; it appears in New contract.
      await settingsSection('Custom fields');
      await page.getByRole('button', { name: 'Add field' }).click();
      const dialog = page.getByRole('dialog', { name: 'Add field' });
      await dialog.getByLabel('Label', { exact: true }).fill('E2E insurance reference');
      await dialog.getByRole('button', { name: 'Save field' }).click();
      await dialog.waitFor({ state: 'detached' });
      await page.getByText('cf.e2eInsuranceReference').waitFor();
      await visible(page.locator('a[href="/contracts/new"]')).click();
      await page.getByRole('heading', { name: 'New contract' }).waitFor();
      await page.getByRole('button', { name: 'Continue without a file' }).click();
      await page.getByLabel('Contract type', { exact: true }).selectOption({ label: 'IT and SaaS' });
      await page.getByRole('heading', { name: 'Additional details' }).waitFor();
      await page.getByLabel(/E2E insurance reference/).fill('POL-2027-001');
      await shot('new-contract-custom-field');
      log('Peter Aagaard added the custom field "E2E insurance reference"; it shows in New contract');
    }

    check(problems.length === 0, `Browser errors:\n    ${problems.join('\n    ')}`);
  } catch (e) {
    if (shotsDir) await page.screenshot({ path: join(shotsDir, `${width}-r3-FAILED.png`) }).catch(() => {});
    throw new Error(`[${tag}] ${e.message}`);
  } finally {
    await context.close();
  }
}

/**
 * Release 4 journey (one page load): filters can be removed (Overview chips, Search "All", the
 * top-bar scope), a register header filter, template categories, the logo upload, e-signature
 * (simulated) by a permitted user and the "ask a colleague" path for another, and the dark theme.
 */
async function journeyR4(browser, baseUrl, width, height) {
  const phone = width < 720;
  const tag = `${width}px r4`;
  const log = (text) => console.log(`  [${tag}] ${text}`);
  const context = await browser.newContext({ viewport: { width, height } });
  await context.addInitScript(() => {
    if (!sessionStorage.getItem('e2e.started')) {
      localStorage.setItem('dafa.mock.userId', 'signed-out');
      localStorage.removeItem('dafa.theme');
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
  const shot = async (name) => {
    if (shotsDir) await page.screenshot({ path: join(shotsDir, `${width}-r4-${name}.png`), fullPage: !phone });
  };
  const openUserMenu = () => visible(page.locator('.user-card, .topbar__avatar, .home-head__avatar')).click();
  const switchTo = async (name) => {
    // New contract has no shell: go home first.
    if (!(await page.locator('.shell').count())) {
      await go('/');
      await page.locator('.shell').waitFor();
    }
    await openUserMenu();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: new RegExp(`^(\\S+ )?${name}`) }).click();
    await page.getByText(`You are now ${name}`).waitFor();
    await dialog.waitFor({ state: 'detached' });
  };
  /** In-app navigation (the mock keeps its data only while the page is not reloaded). */
  const go = async (path) => {
    await page.evaluate((p) => {
      window.history.pushState({}, '', p);
      window.dispatchEvent(new PopStateEvent('popstate'));
    }, path);
  };
  const urlParam = (name) => new URL(page.url()).searchParams.getAll(name);
  const kpi = async (label) => (await page.locator('.kpi', { has: page.locator('.kpi__label', { hasText: new RegExp(`^${label}$`) }) }).first().locator('.kpi__value').innerText()).trim();

  try {
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await page.getByText('Choose a demo user').waitFor();
    await page.getByRole('button', { name: /Rasmus Holm/ }).click();
    await page.locator('.shell').waitFor();

    // 1. Search: choose an entity, then set it back to All (owner bug A2).
    await go('/search?q=supply');
    await page.getByRole('heading', { level: 1, name: 'Search' }).waitFor();
    const entity = page.getByRole('combobox', { name: 'Entity', exact: true });
    const entityId = await entity.locator('option').nth(2).getAttribute('value');
    await entity.selectOption(entityId);
    await page.waitForFunction(() => new URL(location.href).searchParams.has('entity'));
    await entity.selectOption('');
    await page.waitForTimeout(400);
    check(urlParam('entity').length === 0, 'Search: the entity came back after choosing All.');
    log('Search: entity chosen and set back to All; it stays removed');

    // 2. Top-bar scope (desktop): set, scopes Renewals with a removable chip, then cleared.
    if (!phone) {
      await go('/renewals');
      await page.getByRole('heading', { level: 1, name: 'Renewals' }).waitFor();
      const scope = visible(page.getByRole('combobox', { name: 'Legal entity scope' }));
      await scope.selectOption(entityId);
      const chip = page.getByRole('navigation', { name: 'Entity scope' });
      await chip.waitFor();
      await chip.getByRole('button', { name: /^Remove entity scope/ }).click();
      await chip.waitFor({ state: 'detached' });
      await page.waitForTimeout(400);
      check(urlParam('entity').length === 0 && (await scope.inputValue()) === '', 'The top-bar scope came back after removing its chip.');
      log('top-bar entity scope: set on Renewals (chip shown), removed with the chip');
    }

    // 3. Register header filter (desktop table; on a phone the register shows cards).
    if (!phone) {
      await go('/contracts?cols=contract,owner,annualValue,status');
      await page.getByRole('heading', { level: 1, name: 'Contracts' }).waitFor();
      await page.locator('table tbody tr').first().waitFor();
      const before = await page.locator('table tbody tr').count();
      await page.getByRole('button', { name: 'Filter Annual value' }).click();
      const pop = page.getByRole('dialog', { name: 'Filter Annual value' });
      await pop.getByLabel('Min').pressSequentially('1000000', { delay: 5 });
      check((await pop.getByLabel('Min').inputValue()) === '1,000,000', 'The header filter amount has no thousand separators.');
      await pop.getByRole('button', { name: 'Apply' }).click();
      await page.waitForFunction(() => new URL(location.href).searchParams.get('annualValueMin') === '1000000');
      await page.getByRole('button', { name: 'Filter Annual value (active)' }).waitFor();
      const chips = page.getByRole('navigation', { name: 'More filters' });
      await chips.getByRole('button', { name: /^Remove Annual value/ }).click();
      await page.waitForFunction(() => !new URL(location.href).searchParams.has('annualValueMin'));
      log(`register: header filter "Annual value from 1,000,000" applied (${before} rows before) and removed with its chip`);
    }

    // 4. Template categories in New contract.
    await go('/contracts/new?start=template');
    const cats = page.getByRole('group', { name: 'Template category' });
    await cats.waitFor();
    await cats.getByRole('button', { name: /Confidentiality/ }).click();
    await page.getByRole('button', { name: /Mutual NDA/ }).waitFor();
    check((await page.getByRole('button', { name: /Supply agreement/ }).count()) === 0, 'The Confidentiality category still lists the Supply agreement.');
    log('New contract: the Confidentiality category shows only its template');

    // 5. E-signature: Henrik has no signing right (asks a colleague); Rasmus sends and the simulated signing completes.
    await switchTo('Henrik Madsen');
    await go('/contracts?q=Fire%20protection');
    // The register title opens the contract on a phone; on a desktop a double-click does.
    const fire = page.getByRole('button', { name: /(Open|Preview) Fire protection service agreement/ }).first();
    if (phone) await fire.click();
    else await fire.dblclick();
    await page.getByRole('heading', { level: 1, name: 'Fire protection service agreement' }).waitFor();
    const recordUrl = new URL(page.url()).pathname;
    await visible(page.getByRole('button', { name: 'Send for signature' })).click();
    const ask = page.getByRole('dialog', { name: 'Ask a colleague with signing rights' });
    await ask.getByRole('list', { name: 'People with signing rights' }).getByText('Rasmus Holm').waitFor();
    await ask.getByRole('button', { name: 'Close' }).first().click();
    log('Henrik Madsen (no signing right) is shown who can send it');
    await switchTo('Rasmus Holm');
    await go(recordUrl);
    await page.getByRole('heading', { level: 1, name: 'Fire protection service agreement' }).waitFor();
    await visible(page.getByRole('button', { name: 'Send for signature' })).click();
    const send = page.getByRole('dialog', { name: 'Send for signature' });
    const signer = send.getByRole('combobox', { name: 'Signatory 1' });
    await signer.click();
    await signer.pressSequentially('Rasm', { delay: 10 });
    await page.getByRole('listbox').getByRole('option', { name: /Rasmus Holm/ }).click();
    await send.getByLabel('Signatory 2 name').fill('Klaus Weber');
    await send.getByLabel('Signatory 2 e-mail').fill('klaus@example.de');
    await send.getByRole('button', { name: 'Preview' }).click();
    await send.getByRole('button', { name: 'Send now' }).click();
    await page.getByText('Sent for signature').first().waitFor();
    await page.getByRole('group', { name: 'Simulate signing for Rasmus Holm' }).getByRole('button', { name: 'Simulate signing' }).click();
    await page.getByRole('group', { name: 'Simulate signing for Klaus Weber' }).getByRole('button', { name: 'Simulate signing' }).click();
    await page.getByText(/Signed by everyone/).first().waitFor();
    await page.getByText(/– signed\.pdf/).first().waitFor();
    log('Rasmus Holm sent it for signature; simulated signing stored the signed document and the contract is active');
    await shot('signed');

    // 6. Logo upload (desktop: the sidebar shows it).
    if (!phone) {
      await switchTo('Peter Aagaard');
      await go('/settings/general');
      const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="160" height="32"><rect width="32" height="32" rx="8" fill="#c0392b"/><text x="40" y="22" font-size="16">E2E logo</text></svg>';
      await page.getByLabel('Upload logo', { exact: true }).setInputFiles({ name: 'logo.svg', mimeType: 'image/svg+xml', buffer: Buffer.from(svg) });
      await page.getByText('Logo saved').waitFor();
      const img = page.getByRole('navigation', { name: 'Main' }).getByRole('img', { name: 'DAFA Contracts' });
      await img.waitFor();
      const box = await img.boundingBox();
      check(box && box.height <= 40.5, `The logo is ${box?.height}px high, expected at most 40.`);
      const bg = await img.evaluate((el) => getComputedStyle(el).backgroundColor);
      check(bg === 'rgba(0, 0, 0, 0)', `The logo has a background (${bg}).`);
      log(`Peter Aagaard uploaded an SVG logo: shown in the sidebar as an image, ${Math.round(box.height)}px high, transparent`);
    }

    // 7. Dark theme from My settings: page background and text tokens change.
    await go('/preferences');
    await page.getByRole('heading', { level: 1, name: 'My settings' }).waitFor();
    const tokens = () => page.evaluate(() => ({ theme: document.documentElement.getAttribute('data-theme'), page: getComputedStyle(document.body).backgroundColor, text: getComputedStyle(document.body).color }));
    const light = await tokens();
    await page.getByRole('radiogroup', { name: 'Theme' }).getByRole('radio', { name: 'Dark' }).click();
    await page.waitForFunction(() => document.documentElement.getAttribute('data-theme') === 'dark');
    const dark = await tokens();
    check(dark.page !== light.page && dark.text !== light.text, `Dark theme did not change the page colors: ${JSON.stringify({ light, dark })}`);
    check(dark.page === 'rgb(15, 22, 32)', `Dark page background is ${dark.page}, expected rgb(15, 22, 32).`);
    check((await page.evaluate(() => localStorage.getItem('dafa.theme'))) === 'dark', 'The theme was not kept for the next page load.');
    await shot('dark');
    await page.getByRole('radiogroup', { name: 'Theme' }).getByRole('radio', { name: 'Light' }).click();
    await page.waitForFunction(() => document.documentElement.getAttribute('data-theme') === 'light');
    log(`theme: dark (page ${dark.page}, text ${dark.text}) and back to light (page ${light.page})`);

    check(problems.length === 0, `Browser errors:\n    ${problems.join('\n    ')}`);
  } catch (e) {
    if (shotsDir) await page.screenshot({ path: join(shotsDir, `${width}-r4-FAILED.png`) }).catch(() => {});
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
      try {
        await journeyR3(browser, baseUrl, width, height);
        console.log(`PASS ${width}px release 3`);
      } catch (e) {
        failed = true;
        console.error(`FAIL ${e.message}`);
      }
      try {
        await journeyR4(browser, baseUrl, width, height);
        console.log(`PASS ${width}px release 4`);
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
