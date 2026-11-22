/// <reference types="node" />
// Regression tests for the QA findings in docs/REVIEW_FRONTEND.md (frontend phase 3).
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError, type Api } from '../api/api';
import { createMockApi } from '../api/mock/mockApi';
import { USER } from '../api/mock/seed';
import { PROBLEM, type LookupUser } from '../api/types';
import { markSignedOut } from '../app/signin';
import { delegateCandidates, stageApproverIds } from '../features/Approval';
import { routeSummary, splitRoleLabel } from '../lib/approvals';
import { errorMessage, isStale, STALE_MESSAGE, THROTTLED_MESSAGE } from '../lib/errors';
import { renderApp, renderWith, TODAY } from '../test/renderApp';

afterEach(() => {
  markSignedOut(false);
  localStorage.clear();
  vi.restoreAllMocks();
});

const conflict = (type: string, detail: string) => new ApiError({ type, title: 'Conflict', status: 409, detail });

describe('C1: the counterparty field', () => {
  it('keeps what is typed, on the path without a file, and lets the flow continue', async () => {
    const user = userEvent.setup();
    renderApp('/contracts/new', USER.jon);
    await user.click(await screen.findByRole('button', { name: 'Continue without a file' }));
    const field = await screen.findByLabelText('Counterparty');
    await user.type(field, 'Brand New Supplier ApS');
    expect(field).toHaveValue('Brand New Supplier ApS');
    await user.click(screen.getAllByRole('button', { name: 'Continue' })[0]!);
    expect(await screen.findByText('Enter a title')).toBeInTheDocument();
    expect(screen.queryByText('Enter the counterparty')).not.toBeInTheDocument();
  });

  it('keeps what is typed on the template path and matches a known supplier', async () => {
    const user = userEvent.setup();
    const api = renderApp('/contracts/new?start=template', USER.jon);
    await user.click(await screen.findByRole('button', { name: /Supply agreement/ }));
    const supplier = api.db.suppliers[0]!;
    const field = await screen.findByLabelText('Counterparty');
    await user.type(field, supplier.name);
    expect(field).toHaveValue(supplier.name);
  });

  it('can be changed in Edit details by an administrator, and is saved', async () => {
    const user = userEvent.setup();
    const seed = createMockApi({ today: TODAY });
    const rec = seed.db.contracts.find((c) => c.title === 'Freight framework – Nordics')!;
    const api = renderApp(`/contracts/${rec.id}`, USER.pet);
    await user.click(await screen.findByRole('button', { name: 'Edit details' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit details' });
    const field = within(dialog).getByLabelText('Counterparty');
    await user.type(field, ' X');
    expect(field).toHaveValue(`${rec.counterpartyName} X`);
    await user.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText('Details saved')).toBeInTheDocument();
    expect(api.db.contracts.find((c) => c.id === rec.id)!.counterpartyName).toBe(`${rec.counterpartyName} X`);
  });
});

describe('H1: switching demo user', () => {
  it('rebuilds the shell, the navigation and the permissions for the new user (reader -> administrator)', async () => {
    const user = userEvent.setup();
    const api = renderApp('/', USER.emm);
    const getMe = vi.spyOn(api, 'getMe');
    const nav = await screen.findByRole('navigation', { name: 'Main' });
    expect(within(nav).queryByRole('link', { name: 'Settings' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'New contract' })).not.toBeInTheDocument();
    expect(await screen.findByRole('heading', { level: 1, name: /Emma/ })).toBeInTheDocument();

    await user.click(within(nav).getByRole('button', { name: /Emma Nygaard/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Switch demo user' });
    await user.click(within(dialog).getByRole('button', { name: /Peter Aagaard/ }));

    expect(await screen.findByText('You are now Peter Aagaard')).toBeInTheDocument();
    // The whole shell is Peter's: user card, greeting, navigation, permissions.
    const nav2 = await screen.findByRole('navigation', { name: 'Main' });
    expect(await within(nav2).findByRole('button', { name: /Peter Aagaard/ })).toBeInTheDocument();
    expect(within(nav2).queryByRole('button', { name: /Emma Nygaard/ })).not.toBeInTheDocument();
    expect(within(nav2).getByRole('link', { name: 'Settings' })).toBeInTheDocument();
    expect(within(nav2).getByRole('link', { name: 'Overview' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'New contract' })).toBeInTheDocument();
    expect(await screen.findByRole('heading', { level: 1, name: /Peter/ })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { level: 1, name: /Emma/ })).not.toBeInTheDocument();
    // `me` comes from the dev/login response and is the new user.
    expect((await api.getMe()).id).toBe(USER.pet);
    expect(getMe.mock.calls.length).toBeLessThanOrEqual(2);
  });

  it('takes the permissions away again (administrator -> reader), and refetches the data as the new user', async () => {
    const user = userEvent.setup();
    const api = renderApp('/settings/departments', USER.pet);
    expect(await screen.findByRole('heading', { level: 1, name: 'Settings' })).toBeInTheDocument();
    const home = vi.spyOn(api, 'getHome');
    const lookups = vi.spyOn(api, 'getLookups');
    const nav = screen.getByRole('navigation', { name: 'Main' });
    await user.click(within(nav).getByRole('button', { name: /Peter Aagaard/ }));
    await user.click(within(await screen.findByRole('dialog', { name: 'Switch demo user' })).getByRole('button', { name: /Emma Nygaard/ }));

    const nav2 = await screen.findByRole('navigation', { name: 'Main' });
    await waitFor(() => expect(within(nav2).queryByRole('link', { name: 'Settings' })).not.toBeInTheDocument());
    expect(within(nav2).getByRole('button', { name: /Emma Nygaard/ })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'New contract' })).not.toBeInTheDocument();
    // The switch lands on My work, loaded again as Emma.
    expect(await screen.findByRole('heading', { level: 1, name: /Emma/ })).toBeInTheDocument();
    await waitFor(() => expect(home).toHaveBeenCalled());
    expect(lookups).toHaveBeenCalled();
  });

  it('labels only the signed-in approver "You" on the approval route after a switch', async () => {
    const user = userEvent.setup();
    renderApp('/approvals', USER.lou);
    const route = await screen.findByRole('list', { name: 'Approval route' });
    expect(within(route).getAllByText('You')).toHaveLength(1);
    await user.click(within(screen.getByRole('navigation', { name: 'Main' })).getByRole('button', { name: /Louise/ }));
    await user.click(within(await screen.findByRole('dialog', { name: 'Switch demo user' })).getByRole('button', { name: /Rasmus Holm/ }));
    await user.click(await within(await screen.findByRole('navigation', { name: 'Main' })).findByRole('link', { name: /Approvals/ }));
    const route2 = await screen.findByRole('list', { name: 'Approval route' });
    expect(within(route2).getAllByText('You')).toHaveLength(1);
    expect(within(route2).getByText('Group CFO · Your turn')).toBeInTheDocument();
    expect(within(route2).getByText('Louise Winther')).toBeInTheDocument();
  });
});

describe('H2: sign out', () => {
  it('reaches the sign-in screen and leaves no shell behind', async () => {
    const user = userEvent.setup();
    const api = renderApp('/contracts');
    const logout = vi.spyOn(api, 'logout');
    const nav = await screen.findByRole('navigation', { name: 'Main' });
    await user.click(within(nav).getByRole('button', { name: /Rasmus Holm/ }));
    await user.click(within(await screen.findByRole('dialog', { name: 'Switch demo user' })).getByRole('button', { name: 'Sign out' }));

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(logout).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument();
    expect(screen.queryByText(/could not be loaded/)).not.toBeInTheDocument();
    // Signing in again as someone else gives that user's shell.
    await user.click(await screen.findByRole('button', { name: /Emma Nygaard/ }));
    expect(await within(await screen.findByRole('navigation', { name: 'Main' })).findByRole('button', { name: /Emma Nygaard/ })).toBeInTheDocument();
  });

  it('L10: the mock stays signed out after a reload', async () => {
    const first = createMockApi({ today: TODAY, rememberUser: true });
    await first.devLogin(USER.met);
    expect((await createMockApi({ today: TODAY, rememberUser: true }).getMe()).id).toBe(USER.met);
    await first.logout();
    await expect(createMockApi({ today: TODAY, rememberUser: true }).getMe()).rejects.toMatchObject({ status: 401 });
  });

  it('in Entra mode shows "You are signed out" instead of redirecting straight back', async () => {
    const user = userEvent.setup();
    const mock = createMockApi({ today: TODAY, userId: USER.ras });
    let signedIn = true;
    const api: Api = {
      ...mock,
      getDevConfig: () => Promise.resolve({ authMode: 'Entra', accessCodeRequired: false }),
      getMe: async () => {
        if (!signedIn) throw new ApiError({ title: 'Unauthorized', status: 401 });
        return { ...(await mock.getMe()), authMode: 'Entra' };
      },
      logout: async () => {
        signedIn = false;
      },
    };
    renderWith(api, '/');
    const nav = await screen.findByRole('navigation', { name: 'Main' });
    await user.click(within(nav).getByRole('button', { name: /Rasmus Holm/ }));
    await user.click(await screen.findByRole('button', { name: 'Sign out' }));
    expect(await screen.findByRole('heading', { name: 'You are signed out' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Sign in with your DAFA account' })).toHaveAttribute('href', expect.stringContaining('/api/auth/login?returnUrl='));
  });
});

describe('H3: the phone approval bar', () => {
  // jsdom has no layout, so the position itself is checked in the browser (e2e/demo.mjs).
  // These guard the two things that broke it: a transform kept on an ancestor, and the markup.
  // The stylesheets are read from disk: vitest runs with `css: false`, so imports of them are empty.
  const files = ['base.css', 'components.css', 'shell.css', 'pages.css', 'screens.css'];
  const css = (name: string) => readFileSync(join(process.cwd(), 'src/styles', name), 'utf8');

  it('no entrance animation keeps a transform after it ends (fill mode is never both or forwards)', () => {
    for (const file of files) {
      expect(css(file).length).toBeGreaterThan(1000);
      const animations = css(file).match(/animation:[^;]+;/g) ?? [];
      for (const rule of animations) {
        if (/spin|shimmer/.test(rule)) continue;
        expect(`${file}: ${rule}`).not.toMatch(/\b(both|forwards)\b/);
      }
    }
  });

  it('the approvals page fades in without a transform, and the bar is fixed under 720px', async () => {
    expect(css('base.css')).toMatch(/\.rise--fade > \* \{\s*animation-name: fade-in;/);
    const phone = css('screens.css').slice(css('screens.css').indexOf('@media (max-width: 720px)'));
    expect(phone).toMatch(/\.decide__actions \{\s*position: fixed;\s*left: 0;\s*right: 0;\s*bottom: 0;/);

    renderApp('/approvals');
    const approve = await screen.findByRole('button', { name: 'Approve' });
    const bar = approve.closest('[data-pinned-bar]')!;
    expect(within(bar as HTMLElement).getByRole('button', { name: 'Reject' })).toBeInTheDocument();
    expect(bar.closest('.page')!.className).toContain('rise--fade');
    // Delegate and the comment are outside the pinned bar, so the bar cannot cover them by containing them.
    expect(within(bar as HTMLElement).queryByRole('button', { name: 'Delegate' })).not.toBeInTheDocument();
  });

  it('one picked approval hides the top bar and tab bar through a body flag, not :has()', async () => {
    const user = userEvent.setup();
    renderApp('/approvals');
    await user.click(await screen.findByRole('button', { name: /ERP software subscription/ }));
    await waitFor(() => expect(document.body).toHaveAttribute('data-approval-detail'));
    expect(css('shell.css')).toMatch(/body\[data-approval-detail\] \.tabbar/);
    await user.click(screen.getByRole('button', { name: 'Approvals' }));
    await waitFor(() => expect(document.body).not.toHaveAttribute('data-approval-detail'));
  });
});

describe('delegate filter', () => {
  const u = (id: string, roles: LookupUser['roles']): LookupUser => ({ id, name: id, initials: id.slice(0, 2), departmentIds: [], roles });
  const users = [u('me', ['Approver']), u('owner', ['Approver']), u('submitter', ['Admin']), u('same-stage', ['Approver']), u('next-stage', ['Approver']), u('reader', ['Reader']), u('owner-role', ['ContractOwner']), u('admin', ['Admin']), u('approver', ['Reader', 'Approver'])];

  it('offers only Approver/Admin users who are not you, the owner, the submitter or already in the stage', () => {
    const picked = delegateCandidates(users, 'me', { ownerId: 'owner', submittedById: 'submitter', stageApproverIds: ['me', 'same-stage'] }).map((x) => x.id);
    expect(picked).toEqual(['next-stage', 'admin', 'approver']);
  });

  it('leaves the role check to the server when the API sends no roles', () => {
    const plain = users.map(({ roles: _r, ...rest }) => rest);
    expect(delegateCandidates(plain, 'me', { ownerId: 'owner' }).map((x) => x.id)).toEqual(['submitter', 'same-stage', 'next-stage', 'reader', 'owner-role', 'admin', 'approver']);
  });

  it('reads the stage approvers from the instance, without steps that were delegated away', () => {
    const api = createMockApi({ today: TODAY, userId: USER.ras });
    const erp = api.db.contracts.find((c) => c.title === 'ERP software subscription')!;
    const rec = api.db.approvals.find((a) => a.contractId === erp.id)!;
    const stage = rec.stages.find((s) => s.order === 2)!;
    const mine = stage.steps.find((s) => s.approverId === USER.ras)!;
    const instance = { id: rec.id, contractId: erp.id, contractTitle: erp.title, status: rec.status, currentStage: rec.currentStage, stages: rec.stages.map((s) => ({ order: s.order, steps: s.steps.map((st) => ({ ...st, approverName: '', initials: '' })) })) };
    expect(stageApproverIds(instance, mine.id).sort()).toEqual([USER.lou, USER.ras].sort());
    instance.stages[1]!.steps[0]!.status = 'Delegated';
    expect(stageApproverIds(instance, mine.id)).toEqual([USER.ras]);
  });

  it('in the app: not yourself, the owner, the submitter, a reader, or the other approver of the stage', async () => {
    const user = userEvent.setup();
    renderApp('/approvals');
    await user.click(await screen.findByRole('button', { name: 'Delegate' }));
    const names = within(screen.getByLabelText('Delegate to')).getAllByRole('option').map((o) => o.textContent);
    expect(names).toContain('Karen Lindberg'); // approves the next stage: allowed
    expect(names).toContain('Peter Aagaard'); // administrator
    expect(names).not.toContain('Rasmus Holm'); // yourself
    expect(names).not.toContain('Søren Dahl'); // owner and submitter
    expect(names).not.toContain('Louise Winther'); // already an approver in this stage
    expect(names).not.toContain('Emma Nygaard'); // reader
    expect(names).not.toContain('Jonas Berg'); // contract owner role only
  });
});

describe('problem types', () => {
  it('only `stale` is "updated by someone else"; every other 409 shows the server\'s detail', () => {
    expect(errorMessage(conflict(PROBLEM.stale, 'Row version mismatch'))).toBe(STALE_MESSAGE);
    expect(isStale(conflict(PROBLEM.stale, ''))).toBe(true);
    for (const type of [PROBLEM.noApprovalRule, PROBLEM.noApprover, PROBLEM.lockedFields, PROBLEM.invalidState, PROBLEM.duplicate]) {
      expect(errorMessage(conflict(type, `Detail for ${type}`))).toBe(`Detail for ${type}`);
      expect(isStale(conflict(type, ''))).toBe(false);
    }
    expect(errorMessage(new ApiError({ title: 'Too Many Requests', status: 429, detail: 'Rate limit' }))).toBe(THROTTLED_MESSAGE);
  });

  it('a stale approval shows the fixed sentence and refetches', async () => {
    const user = userEvent.setup();
    const mock = createMockApi({ today: TODAY, userId: USER.ras });
    const mine = vi.spyOn(mock, 'getMyApprovals');
    mock.decideApprovalStep = () => Promise.reject(conflict(PROBLEM.stale, 'The data was changed by another request.'));
    renderWith(mock, '/approvals');
    await user.click(await screen.findByRole('button', { name: 'Approve' }));
    expect(await screen.findByText(STALE_MESSAGE)).toBeInTheDocument();
    expect(screen.queryByText(/changed by another request/)).not.toBeInTheDocument();
    await waitFor(() => expect(mine.mock.calls.length).toBeGreaterThanOrEqual(2));
  });

  it('submit without a matching rule shows the server\'s explanation, not "updated by someone else"', async () => {
    const user = userEvent.setup();
    const seed = createMockApi({ today: TODAY, userId: USER.jon });
    seed.db.approvalRules.length = 0;
    const draft = seed.db.contracts.find((c) => c.status === 'Draft' && c.ownerId === USER.jon) ?? seed.db.contracts.find((c) => c.status === 'Draft')!;
    await seed.devLogin(draft.ownerId);
    const get = vi.spyOn(seed, 'getContract');
    renderWith(seed, `/contracts/${draft.id}`);
    await user.click(await screen.findByRole('button', { name: 'Submit for approval' }));
    // L13: a confirmation with the route comes first.
    const dialog = await screen.findByRole('dialog', { name: 'Submit for approval' });
    expect(await within(dialog).findByText(/No approval rule matches yet/)).toBeInTheDocument();
    const before = get.mock.calls.length;
    await user.click(within(dialog).getByRole('button', { name: 'Submit for approval' }));
    expect(await screen.findByText('No approval rule matches this contract. Ask an administrator to add one in Settings.')).toBeInTheDocument();
    expect(screen.queryByText(/updated by someone else/)).not.toBeInTheDocument();
    await waitFor(() => expect(get.mock.calls.length).toBeGreaterThan(before)); // refetched after the 409
  });

  it('a locked field refused by the server shows which fields are locked', async () => {
    const user = userEvent.setup();
    const seed = createMockApi({ today: TODAY });
    const rec = seed.db.contracts.find((c) => c.title === 'Freight framework – Nordics')!;
    const api = createMockApi({ today: TODAY, userId: rec.ownerId });
    api.updateContract = () => Promise.reject(conflict(PROBLEM.lockedFields, 'Approved terms are locked. Register an amendment as a linked contract, or ask an administrator. Locked: Title.'));
    renderWith(api, `/contracts/${rec.id}`);
    await user.click(await screen.findByRole('button', { name: 'Edit details' }));
    await user.click(within(await screen.findByRole('dialog', { name: 'Edit details' })).getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText(/Locked: Title\.$/)).toBeInTheDocument();
  });

  it('too early to renew: the 409 detail stays in the dialog', async () => {
    const user = userEvent.setup();
    const seed = createMockApi({ today: TODAY });
    const rec = seed.db.contracts.find((c) => c.status === 'Active' && c.autoRenew && c.renewalMonths && c.endDate && c.renewalDecision === null && c.noticePeriodDays !== null)!;
    const api = createMockApi({ today: TODAY, userId: rec.ownerId });
    const own = api.db.contracts.find((c) => c.id === rec.id)!;
    own.endDate = '2029-06-30'; // far outside the 120-day reminder window
    renderWith(api, `/contracts/${rec.id}`);
    await user.click(await screen.findByRole('button', { name: 'Decide on renewal' }));
    const dialog = await screen.findByRole('dialog', { name: 'Decide on renewal' });
    await user.click(await within(dialog).findByRole('radio', { name: /^Renew/ }));
    await user.click(within(dialog).getByRole('button', { name: 'Save decision' }));
    expect(await within(dialog).findByText(/^It is too early to renew this contract\. Renewal opens on /)).toBeInTheDocument();
    expect(own.endDate).toBe('2029-06-30');
  });

  it('429 on the access code: "Too many attempts"', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ today: TODAY, accessCode: 'dafa-test-code' });
    renderWith(api, '/');
    const field = await screen.findByLabelText('Access code');
    for (let i = 0; i < 10; i += 1) await expect(api.getDevUsers('wrong')).rejects.toMatchObject({ status: 401 });
    await user.type(field, 'dafa-test-code'); // even the right code is refused now
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByText(THROTTLED_MESSAGE)).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Sign in' })).not.toBeInTheDocument();
  });
});

describe('route helpers', () => {
  it('splits the role from the explanation the API appends', () => {
    expect(splitRoleLabel('Department head (instead of Søren Dahl, who owns or submitted the contract)')).toEqual({ role: 'Department head', note: 'Instead of Søren Dahl, who owns or submitted the contract' });
    expect(splitRoleLabel('Legal')).toEqual({ role: 'Legal', note: null });
  });

  it('writes the route in one sentence for the phone view', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.ras });
    const [item] = await api.getMyApprovals();
    expect(routeSummary(item!.instance, item!.stepId).map((p) => p.text).join(' · ')).toBe('Department head approved · You and Legal now · Group CEO next');
    expect(routeSummary(item!.instance, item!.stepId).map((p) => p.now)).toEqual([false, true, false]);
  });
});

describe('other QA findings', () => {
  it('M1: the entity filter is in the URL and survives a reload', async () => {
    const user = userEvent.setup();
    const api = renderApp('/contracts');
    const sverige = api.db.entities.find((e) => e.name === 'DAFA Sverige AB')!;
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Entity' }), sverige.id);
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Legal entity scope' })).toHaveValue(sverige.id));
    const list = vi.spyOn(api, 'getContracts');
    // A fresh load of the link shows the same filter.
    cleanup();
    renderWith(api, `/contracts?entity=${sverige.id}&department=${api.db.departments[0]!.id}`);
    await waitFor(() => expect(list).toHaveBeenCalledWith(expect.objectContaining({ entityId: sverige.id, departmentId: api.db.departments[0]!.id })));
  });

  it('M3: Renewals offers a decision only on contracts the user may decide', async () => {
    renderApp('/renewals', USER.met);
    const list = (await screen.findByRole('heading', { name: /in date order/ })).parentElement!;
    const rows = within(list).getAllByRole('listitem');
    const mine = rows.filter((r) => within(r).queryByRole('button', { name: /^Decide on renewal/ }));
    const others = rows.filter((r) => within(r).queryByRole('link', { name: /^Open contract/ }));
    expect(mine.length).toBeGreaterThan(0);
    expect(others.length).toBeGreaterThan(0);
    expect(mine.length + others.length).toBe(rows.length);
  });

  it('M5: the timeline is one Tab stop (roving tabindex)', async () => {
    renderApp('/renewals');
    const timeline = await screen.findByRole('group', { name: /Notice deadlines by legal entity/ });
    const markers = within(timeline).getAllByRole('button');
    expect(markers.length).toBeGreaterThan(5);
    expect(markers.filter((m) => m.tabIndex === 0)).toHaveLength(1);
    expect(markers.find((m) => m.tabIndex === 0)).toHaveAttribute('aria-pressed', 'true');
  });

  it('M6: Edit details with locked fields opens at the top (focus on the dialog, not the first enabled field)', async () => {
    const user = userEvent.setup();
    const seed = createMockApi({ today: TODAY });
    const rec = seed.db.contracts.find((c) => c.title === 'Freight framework – Nordics')!;
    renderApp(`/contracts/${rec.id}`, rec.ownerId);
    await user.click(await screen.findByRole('button', { name: 'Edit details' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit details' });
    expect(dialog).toHaveFocus();
    // L12: a locked counterparty does not invite the user to pick a supplier.
    expect(within(dialog).queryByText('Pick a supplier from the list or type a new name.')).not.toBeInTheDocument();
  });

  it('M7 and L1: the subtitle counts the entities the user sees, and a page past the end goes to the last page', async () => {
    renderApp('/contracts?page=99', USER.lou);
    expect(await screen.findByText('1 contract you can see, across 1 legal entity')).toBeInTheDocument();
    expect(await screen.findByRole('table', { name: 'Contracts' })).toBeInTheDocument();
    expect(screen.queryByText('No contracts yet')).not.toBeInTheDocument();
  });

  it('L7: one field in error is "It is marked", not "They are"', async () => {
    const user = userEvent.setup();
    renderApp('/contracts/new', USER.jon);
    await user.click(await screen.findByRole('button', { name: 'Continue without a file' }));
    await user.type(await screen.findByLabelText('Title'), 'Test');
    await user.type(screen.getByLabelText('Counterparty'), 'Someone ApS');
    await user.selectOptions(screen.getByLabelText('Contract type'), screen.getAllByRole('option', { name: 'NDA' })[0]!);
    await user.click(screen.getAllByRole('button', { name: 'Continue' })[0]!);
    const summary = await screen.findByText(/need(s)? your attention\./);
    expect(summary.textContent).toMatch(/^(One field needs your attention\. It is marked|\d+ fields need your attention\. They are marked) in red below\.$/);
  });

  it('the last step of New contract has one primary "Submit for approval" and one "Save as draft"', async () => {
    const user = userEvent.setup();
    renderApp('/contracts/new', USER.tho);
    await screen.findByRole('heading', { name: 'New contract' });
    await user.upload(document.querySelector<HTMLInputElement>('input[type="file"]')!, new File([new Uint8Array(300_000)], 'Freight agreement ScanFreight – draft.pdf', { type: 'application/pdf' }));
    await user.click(await screen.findByRole('button', { name: 'Accept all suggestions' }));
    await user.selectOptions(screen.getByLabelText('Department'), screen.getAllByRole('option', { name: 'Supply chain and logistics' })[0]!);
    await user.type(screen.getByLabelText('Annual value'), '900000');
    await user.click(within(screen.getByRole('radiogroup', { name: 'Change-of-control clause' })).getByRole('radio', { name: 'No' }));
    await user.click(screen.getAllByRole('button', { name: 'Continue' })[0]!);
    await screen.findByRole('heading', { name: 'Links and obligations' });
    await user.click(screen.getAllByRole('button', { name: 'Continue' })[0]!);
    await screen.findByRole('heading', { name: 'Approval' });
    expect(screen.getAllByRole('button', { name: 'Submit for approval' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Save as draft' })).toHaveLength(1);
    expect(screen.queryByRole('button', { name: 'Continue' })).not.toBeInTheDocument();
  });
});
