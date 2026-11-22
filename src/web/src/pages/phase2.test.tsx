import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError, type Api } from '../api/api';
import { createMockApi } from '../api/mock/mockApi';
import { USER } from '../api/mock/seed';
import { browser, readAccessCode, storeAccessCode } from '../app/signin';
import { renderApp, renderWith, TODAY } from '../test/renderApp';

const ERP = 'ERP software subscription';
const erpOf = (api: ReturnType<typeof renderApp>) => api.db.contracts.find((c) => c.title === ERP)!;
const rasStep = (api: ReturnType<typeof renderApp>) => api.db.approvals.find((a) => a.contractId === erpOf(api).id)!.stages.flatMap((s) => s.steps).find((s) => s.approverId === USER.ras)!;

describe('Approvals', () => {
  it('shows the queue, the key terms, the deviations and the route with parallel steps', async () => {
    renderApp('/approvals');
    expect(await screen.findByText('1 contract is waiting for your decision')).toBeInTheDocument();
    const queue = screen.getByRole('list', { name: 'Waiting for your decision' });
    expect(within(queue).getByText('Waiting 2 days')).toBeInTheDocument();
    expect(screen.getByText('Step 2 of 3 · you and Legal in parallel')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 2, name: ERP })).toBeInTheDocument();
    expect(screen.getByText('DKK 7.8m')).toBeInTheDocument();
    expect(screen.getByText('Liability cap at 6 months of fees')).toBeInTheDocument();
    expect(screen.getByText('High')).toBeInTheDocument();
    const route = screen.getByRole('list', { name: 'Approval route' });
    expect(within(route).getByText('Step 2 · in parallel')).toBeInTheDocument();
    expect(within(route).getByText('Group CFO · Your turn')).toBeInTheDocument();
    expect(within(route).getByText('Legal · Reviewing')).toBeInTheDocument();
    expect(within(route).getByText('Group CEO · Next')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Open contract' })).toHaveAttribute('href', `/contracts/${erpOf(createMockApi({ today: TODAY })).id}`);
  });

  it('approves with a comment and then shows the empty state', async () => {
    const user = userEvent.setup();
    const api = renderApp('/approvals');
    await user.type(await screen.findByLabelText('Comment'), 'Within budget');
    await user.click(screen.getByRole('button', { name: 'Approve' }));
    expect(await screen.findByText('Approved')).toBeInTheDocument();
    expect(await screen.findByText('Nothing is waiting for you')).toBeInTheDocument();
    expect(rasStep(api)).toMatchObject({ status: 'Approved', comment: 'Within budget' });
    expect(erpOf(api).status).toBe('InApproval');
  });

  it('does not reject without a comment; with one the contract goes back to draft', async () => {
    const user = userEvent.setup();
    const api = renderApp('/approvals');
    await user.click(await screen.findByRole('button', { name: 'Reject' }));
    expect(await screen.findByText('Add a comment that explains why you reject the contract')).toBeInTheDocument();
    expect(screen.getByLabelText('Comment')).toHaveFocus();
    expect(rasStep(api).status).toBe('Pending');

    await user.type(screen.getByLabelText('Comment'), 'Liability cap is too low');
    await user.click(screen.getByRole('button', { name: 'Reject' }));
    expect(await screen.findByText(/Rejected\. The contract is back with the owner/)).toBeInTheDocument();
    await waitFor(() => expect(erpOf(api).status).toBe('Draft'));
    expect(rasStep(api)).toMatchObject({ status: 'Rejected', comment: 'Liability cap is too low' });
    expect(await screen.findByText('Nothing is waiting for you')).toBeInTheDocument();
  });

  it('delegates to an approver; the owner and readers are not offered', async () => {
    const user = userEvent.setup();
    const api = renderApp('/approvals');
    await user.click(await screen.findByRole('button', { name: 'Delegate' }));
    const picker = screen.getByLabelText('Delegate to');
    const names = within(picker).getAllByRole('option').map((o) => o.textContent);
    expect(names).toContain('Karen Lindberg');
    expect(names).not.toContain('Rasmus Holm'); // yourself
    expect(names).not.toContain('Søren Dahl'); // owner of the contract
    expect(names).not.toContain('Emma Nygaard'); // reader
    expect(screen.getByRole('button', { name: 'Delegate step' })).toBeDisabled();
    await user.selectOptions(picker, 'Karen Lindberg');
    await user.click(screen.getByRole('button', { name: 'Delegate step' }));
    expect(await screen.findByText('Approval step delegated')).toBeInTheDocument();
    expect(rasStep(api).status).toBe('Delegated');
    expect(await screen.findByText('Nothing is waiting for you')).toBeInTheDocument();
  });

  it('tells the approver when someone else acted first (409) and refreshes', async () => {
    const user = userEvent.setup();
    const api = renderApp('/approvals');
    await screen.findByRole('button', { name: 'Approve' });
    // Another session rejects the contract in the meantime.
    const a = api.db.approvals.find((x) => x.contractId === erpOf(api).id)!;
    a.status = 'Rejected';
    await user.click(screen.getByRole('button', { name: 'Approve' }));
    expect(await screen.findByText(/This was just updated by someone else\./)).toBeInTheDocument();
    expect(await screen.findByText('Nothing is waiting for you')).toBeInTheDocument();
  });

  it('shows the empty state when nothing waits', async () => {
    renderApp('/approvals', USER.emm);
    expect(await screen.findByText('Nothing is waiting for you')).toBeInTheDocument();
    expect(screen.getByText('Nothing is waiting for your decision')).toBeInTheDocument();
  });
});

describe('Renewals', () => {
  it('draws one lane per legal entity with a marker per contract, and selects with click and arrow keys', async () => {
    const user = userEvent.setup();
    const api = renderApp('/renewals');
    const timeline = await screen.findByRole('group', { name: /Notice deadlines by legal entity/ });
    expect(within(timeline).getByRole('group', { name: /^DAFA A\/S, \d+ deadlines?$/ })).toBeInTheDocument();
    const marker = within(timeline).getByRole('button', { name: /^Freight framework – Nordics, notice by 20 Jan 2027, in 59 days$/ });
    // 20 January: third column of 12, 19 of 31 days in.
    expect(parseFloat(marker.style.left)).toBeCloseTo(((2 + 19 / 31) / 12) * 100, 1);
    expect(marker.className).toContain('marker--amber');

    await user.click(marker);
    expect(marker).toHaveAttribute('aria-pressed', 'true');
    const card = screen.getByRole('region', { name: 'Selected contract' });
    expect(within(card).getByText('Freight framework – Nordics')).toBeInTheDocument();
    expect(within(card).getByText('59')).toBeInTheDocument();
    const id = api.db.contracts.find((c) => c.title === 'Freight framework – Nordics')!.id;
    expect(within(card).getByRole('link', { name: 'Open contract' })).toHaveAttribute('href', `/contracts/${id}`);

    await user.keyboard('{ArrowRight}');
    const next = within(timeline).getByRole('button', { pressed: true });
    expect(next).not.toBe(marker);
    expect(next).toHaveFocus();
  });

  it('groups the lanes by department instead', async () => {
    const user = userEvent.setup();
    renderApp('/renewals');
    await screen.findByRole('group', { name: /Notice deadlines by legal entity/ });
    await user.click(screen.getByRole('button', { name: 'By department' }));
    const timeline = screen.getByRole('group', { name: /Notice deadlines by department/ });
    expect(within(timeline).getByRole('group', { name: /^Purchasing, \d+ deadlines?$/ })).toBeInTheDocument();
    expect(within(timeline).queryByRole('group', { name: /^DAFA A\/S,/ })).not.toBeInTheDocument();
  });

  it('opens the renewal decision dialog for the selected contract', async () => {
    const user = userEvent.setup();
    renderApp('/renewals');
    await user.click(await screen.findByRole('button', { name: 'Record decision' }));
    expect(await screen.findByRole('dialog', { name: 'Decide on renewal' })).toBeInTheDocument();
  });

  it('shows an overdue deadline first, in red, pinned at the left edge', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.ras });
    const rec = api.db.contracts.find((c) => c.title === 'Freight framework – Nordics')!;
    rec.endDate = '2026-12-02';
    rec.noticePeriodDays = 400;
    renderWith(api, '/renewals');
    const marker = await screen.findByRole('button', { name: /^Freight framework – Nordics, notice by .*, 390 days overdue$/ });
    expect(marker.style.left).toBe('0%');
    expect(marker.className).toContain('marker--red');
    expect(screen.getByText(/^\d+ overdue$/)).toBeInTheDocument();
    const list = screen.getByRole('heading', { name: /in date order/ }).parentElement!;
    expect(within(list).getAllByRole('listitem')[0]).toHaveTextContent('Freight framework – Nordics');
  });
});

describe('Overview access', () => {
  it('shows the portfolio to group management', async () => {
    renderApp('/overview');
    expect(await screen.findByRole('heading', { level: 1, name: 'Overview' })).toBeInTheDocument();
    expect(await screen.findByRole('list', { name: 'Annual contract value by legal entity, in DKK' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Change of control' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Missing data/ })).toHaveAttribute('href', '/contracts?view=missing');
  });

  it('gives a reader a clear no-access state, hides the menu item and never asks the API', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.emm });
    const spy = vi.spyOn(api, 'getOverview');
    renderWith(api, '/overview');
    expect(await screen.findByRole('heading', { name: 'You do not have access to Overview' })).toBeInTheDocument();
    expect(within(screen.getByRole('navigation', { name: 'Main' })).queryByRole('link', { name: 'Overview' })).not.toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });

  it('shows the no-access state when the API refuses, whatever the client believes', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.ras });
    api.getOverview = () => Promise.reject(new ApiError({ title: 'Forbidden', status: 403 }));
    renderWith(api, '/overview');
    expect(await screen.findByRole('heading', { name: 'You do not have access to Overview' })).toBeInTheDocument();
  });

  it('shows entity management the overview and keeps Settings for administrators', async () => {
    renderApp('/settings', USER.ann);
    expect(await screen.findByRole('heading', { name: 'You do not have access to Settings' })).toBeInTheDocument();
    expect(within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', { name: 'Overview' })).toBeInTheDocument();
  });
});

describe('Settings', () => {
  it('validates a new approval rule, saves it and lists it', async () => {
    const user = userEvent.setup();
    const api = renderApp('/settings/approval-rules', USER.pet);
    await user.click(await screen.findByRole('button', { name: 'New rule' }));
    const dialog = await screen.findByRole('dialog', { name: 'New approval rule' });
    await user.click(within(dialog).getByRole('button', { name: 'Add rule' }));
    expect(within(dialog).getByText('Enter a name for the rule')).toBeInTheDocument();
    expect(within(dialog).getByText('Choose a role', { selector: '.field__error' })).toBeInTheDocument();
    expect(api.db.approvalRules.some((r) => r.name === 'NDA fast track')).toBe(false);

    await user.type(within(dialog).getByLabelText('Name'), 'NDA fast track');
    await user.selectOptions(within(dialog).getByLabelText(/^Contract type/), 'NDA');
    await user.selectOptions(within(dialog).getByLabelText('Stage 1, approver 1: role'), 'Legal');
    await user.click(within(dialog).getByRole('button', { name: 'Add stage' }));
    await user.selectOptions(within(dialog).getByLabelText('Stage 2, approver 1: kind'), 'Specific person');
    await user.selectOptions(within(dialog).getByLabelText('Stage 2, approver 1: person'), 'Karen Lindberg');
    await user.click(within(dialog).getByRole('button', { name: 'Add rule' }));

    expect(await screen.findByText('Added rule "NDA fast track"')).toBeInTheDocument();
    const saved = api.db.approvalRules.find((r) => r.name === 'NDA fast track')!;
    expect(saved.stages).toEqual([
      { order: 1, approvers: [{ userId: null, role: 'Legal' }] },
      { order: 2, approvers: [{ userId: USER.kar, role: null }] },
    ]);
    const row = (await screen.findByText('NDA fast track')).closest('li')!;
    expect(within(row).getByText(/NDA · priority 0/)).toBeInTheDocument();
  });

  it('shows which rule would match a contract', async () => {
    renderApp('/settings/approval-rules', USER.pet);
    expect(await screen.findByText('Matching rule')).toBeInTheDocument();
  });

  it('adds a department with validation, and it reaches the lookups', async () => {
    const user = userEvent.setup();
    const api = renderApp('/settings/departments', USER.pet);
    await user.click(await screen.findByRole('button', { name: 'Add department' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add department' });
    await user.type(within(dialog).getByLabelText('Name'), 'finance');
    await user.click(within(dialog).getByRole('button', { name: 'Add department' }));
    expect(within(dialog).getByText('That name is already in use')).toBeInTheDocument();
    await user.clear(within(dialog).getByLabelText('Name'));
    await user.type(within(dialog).getByLabelText('Name'), 'Legal');
    await user.click(within(dialog).getByRole('button', { name: 'Add department' }));
    expect(await screen.findByText('Added Legal')).toBeInTheDocument();
    expect((await api.getLookups()).departments.some((d) => d.name === 'Legal')).toBe(true);
  });

  it('saves the reminder days', async () => {
    const user = userEvent.setup();
    const api = renderApp('/settings/reminders', USER.pet);
    await user.type(await screen.findByLabelText('Add a reminder, days before'), '60');
    await user.click(screen.getByRole('button', { name: 'Add' }));
    expect(screen.getByText('A reminder 60 days before is already in the list')).toBeInTheDocument();
    await user.clear(screen.getByLabelText('Add a reminder, days before'));
    await user.type(screen.getByLabelText('Add a reminder, days before'), '90');
    await user.click(screen.getByRole('button', { name: 'Add' }));
    await user.click(screen.getByRole('button', { name: 'Remove the reminder 30 days before' }));
    await user.click(screen.getByRole('button', { name: 'Save reminder settings' }));
    expect(await screen.findByText('Reminder settings saved')).toBeInTheDocument();
    expect(api.db.reminderSettings.noticeDays).toEqual([120, 90, 60]);
  });

  it('runs the jobs and reports the result', async () => {
    const user = userEvent.setup();
    renderApp('/settings/outbox', USER.pet);
    await user.click(await screen.findByRole('button', { name: 'Run jobs now' }));
    expect(await screen.findByText(/^Jobs run: \d+ reminders?, /)).toBeInTheDocument();
  });

  it('pages and filters the audit log by contract', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.pet });
    const id = api.db.contracts.find((c) => c.title === ERP)!.id;
    renderWith(api, `/settings/audit?contractId=${id}`);
    expect(await screen.findByText(`Contract: ${ERP}`)).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Audit log' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.length).toBeGreaterThan(0);
    for (const r of rows) expect(within(r).getByRole('link', { name: ERP })).toBeInTheDocument();
  });
});

describe('sign-in', () => {
  beforeEach(() => storeAccessCode(null));
  afterEach(() => {
    storeAccessCode(null);
    vi.restoreAllMocks();
  });

  it('asks for the access code before the demo-user picker and remembers it for the tab', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ today: TODAY, accessCode: 'dafa-test' });
    const users = vi.spyOn(api, 'getDevUsers');
    const login = vi.spyOn(api, 'devLogin');
    renderWith(api, '/');
    expect(await screen.findByRole('heading', { name: 'Enter the access code' })).toBeInTheDocument();
    expect(screen.queryByText('Rasmus Holm')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Continue' }));
    expect(screen.getByText('Enter the access code', { selector: '.field__error' })).toBeInTheDocument();
    expect(users).not.toHaveBeenCalled();

    await user.type(screen.getByLabelText('Access code'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByText('The access code is not correct')).toBeInTheDocument();
    expect(readAccessCode()).toBeUndefined();

    await user.clear(screen.getByLabelText('Access code'));
    await user.type(screen.getByLabelText('Access code'), 'dafa-test');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(users).toHaveBeenLastCalledWith('dafa-test');
    expect(readAccessCode()).toBe('dafa-test');
    expect(sessionStorage.getItem('dafa.dev.accessCode')).toBe('dafa-test');

    await user.click(await screen.findByRole('button', { name: /Rasmus Holm/ }));
    expect(login).toHaveBeenCalledWith(USER.ras, 'dafa-test');
    expect(await screen.findByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it('asks for the code again when a remembered code stopped working', async () => {
    storeAccessCode('old-code');
    const api = createMockApi({ today: TODAY, accessCode: 'dafa-test' });
    renderWith(api, '/');
    expect(await screen.findByText('The access code is no longer valid. Enter it again')).toBeInTheDocument();
    expect(readAccessCode()).toBeUndefined();
  });

  it('goes straight to the picker when no access code is required', async () => {
    const api = createMockApi({ today: TODAY });
    await api.logout();
    renderWith(api, '/');
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: /Rasmus Holm/ })).toBeInTheDocument();
  });

  it('in Entra mode a 401 from me redirects to the API sign-in with the current path', async () => {
    const redirect = vi.spyOn(browser, 'redirect').mockImplementation(() => undefined);
    const mock = createMockApi({ today: TODAY });
    const api: Api = { ...mock, getDevConfig: () => Promise.resolve({ authMode: 'Entra', accessCodeRequired: false }), getMe: () => Promise.reject(new ApiError({ title: 'Unauthorized', status: 401 })) };
    const users = vi.spyOn(api, 'getDevUsers');
    renderWith(api, '/contracts');
    await waitFor(() => expect(redirect).toHaveBeenCalledTimes(1));
    expect(redirect).toHaveBeenCalledWith(`/api/auth/login?returnUrl=${encodeURIComponent(window.location.pathname + window.location.search + window.location.hash)}`);
    expect(users).not.toHaveBeenCalled();
    expect(screen.getByRole('heading', { name: 'Signing you in' })).toBeInTheDocument();
  });
});

describe('phase 1 gaps and backend contract changes', () => {
  it('quick look in the register does not count as opening the contract', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.emm });
    const get = vi.spyOn(api, 'getContract');
    renderWith(api, '/contracts');
    await screen.findByRole('complementary', { name: 'Quick look' });
    await waitFor(() => expect(get).toHaveBeenCalled());
    for (const call of get.mock.calls) expect(call[1]).toEqual({ peek: true });
  });

  it('the New contract reminder hint uses the real reminder settings', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ today: TODAY, userId: USER.jon });
    api.db.reminderSettings = { noticeDays: [100, 50], escalationDays: 10, obligationDays: 14 };
    renderWith(api, '/contracts/new');
    await user.click(await screen.findByRole('button', { name: /without a file/i }));
    expect(await screen.findByText('100 and 50 days before the notice deadline.')).toBeInTheDocument();
  });

  it('disables locked fields in Edit details and explains why', async () => {
    const user = userEvent.setup();
    const seed = createMockApi({ today: TODAY });
    const rec = seed.db.contracts.find((c) => c.title === 'Freight framework – Nordics')!;
    renderApp(`/contracts/${rec.id}`, rec.ownerId);
    await user.click(await screen.findByRole('button', { name: 'Edit details' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit details' });
    expect(within(dialog).getByText('Approved terms are locked. Register an amendment as a linked contract, or ask an administrator.')).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Counterparty')).toBeDisabled();
    expect(within(dialog).getByLabelText('Start date')).toBeDisabled();
    expect(within(dialog).getByLabelText('Title')).toBeDisabled();
    expect(within(dialog).getByLabelText(/^Payment terms/)).toBeEnabled();
    expect(within(dialog).getByLabelText('Owner')).toBeEnabled();
  });

  it('shows "Not set" for a change-of-control answer the API does not have, and unknown missing-data labels as they are', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.ras });
    const rec = api.db.contracts.find((c) => c.title === 'Freight framework – Nordics')!;
    const original = api.getContract;
    api.getContract = async (id, o) => ({ ...(await original(id, o)), changeOfControl: null, missingData: ['Change of control', 'Renewal period'] });
    renderWith(api, `/contracts/${rec.id}`);
    expect(await screen.findByText('Not set')).toBeInTheDocument();
    expect(screen.getByText(/Missing data: change of control, renewal period\./)).toBeInTheDocument();
  });

  it('asks for the new end date when a fixed-term contract is renewed', async () => {
    const user = userEvent.setup();
    const seed = createMockApi({ today: TODAY });
    const rec = seed.db.contracts.find((c) => c.status === 'Active' && !c.autoRenew && c.endDate && c.renewalDecision === null && c.noticePeriodDays !== null)!;
    const api = renderApp(`/contracts/${rec.id}`, USER.pet);
    await user.click(await screen.findByRole('button', { name: 'Decide on renewal' }));
    const dialog = await screen.findByRole('dialog', { name: 'Decide on renewal' });
    await user.click(await within(dialog).findByRole('radio', { name: /^Renew/ }));
    const field = within(dialog).getByLabelText('New end date');
    await user.click(within(dialog).getByRole('button', { name: 'Save decision' }));
    expect(within(dialog).getByText('Enter the new end date')).toBeInTheDocument();
    await user.type(field, '2030-06-30');
    await user.click(within(dialog).getByRole('button', { name: 'Save decision' }));
    await waitFor(() => expect(api.db.contracts.find((c) => c.id === rec.id)!.endDate).toBe('2030-06-30'));
  });
});
