// Release 4 alignment with the real API (docs/REVIEW_ALIGNMENT_R4.md): the screens.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError, type Api } from '../api/api';
import { createMockApi } from '../api/mock/mockApi';
import { DEP, USER, departmentId } from '../api/mock/seed';
import { browser } from '../app/signin';
import { NO_ACCESS_MESSAGE } from '../lib/errors';
import { renderApp, renderWith, router, TODAY } from '../test/renderApp';

afterEach(() => {
  localStorage.clear();
  sessionStorage.clear();
  vi.restoreAllMocks();
});

const NO_MASTER = 'Temporary staff – production';
const FIRE = 'Fire protection service agreement';

describe('H1: renaming or deactivating a department keeps its SharePoint library and Entra group', () => {
  it('rename and deactivate send the existing link along', async () => {
    const user = userEvent.setup();
    const api = renderApp('/settings/departments', USER.pet);
    const id = departmentId(DEP.purchasing);
    const list = await screen.findByRole('list', { name: 'Departments' });
    await user.click(within(list).getByRole('button', { name: 'Rename Purchasing' }));
    const dialog = await screen.findByRole('dialog', { name: 'Rename department' });
    const name = within(dialog).getByLabelText('Name');
    await user.clear(name);
    await user.type(name, 'Procurement');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Renamed to Procurement')).toBeInTheDocument();
    const linked = { name: 'Procurement', documentLibrary: 'Purchasing contracts', groupId: '6f1a2b3c-0000-4000-8000-000000000101', groupName: 'DAFA Purchasing', link: { state: 'Linked' } };
    expect((await api.admin.getDepartmentsV4()).find((d) => d.id === id)).toMatchObject(linked);

    const row = (await within(await screen.findByRole('list', { name: 'Departments' })).findByText('Procurement')).closest('li')!;
    await user.click(within(row).getByRole('button', { name: 'Deactivate' }));
    expect(await screen.findByText('Procurement is inactive')).toBeInTheDocument();
    expect((await api.admin.getDepartmentsV4()).find((d) => d.id === id)).toMatchObject({ ...linked, isActive: false });
  });
});

describe('H2: Edit details on a contract without a master counterparty', () => {
  it('the owner of a non-draft contract completes missing data and saves', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ today: TODAY, userId: USER.hen });
    const c = api.db.contracts.find((x) => x.title === NO_MASTER)!;
    expect(c).toMatchObject({ supplierId: null, status: 'Active', ownerId: USER.hen });
    const counterparty = c.counterpartyName;
    c.paymentTerms = null;
    renderWith(api, `/contracts/${c.id}`);
    await user.click((await screen.findAllByRole('button', { name: 'Edit details' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Edit details' });
    // The counterparty is an approved term: locked, shown, and not asked for again.
    expect(within(dialog).getByRole('combobox', { name: /Counterparty/ })).toBeDisabled();
    expect(within(dialog).getByText(new RegExp(`“${counterparty}” is kept as it is`))).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText(/Payment terms/), '45 days net');
    await user.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText('Details saved')).toBeInTheDocument();
    expect(screen.queryByText('Pick the counterparty from the list')).not.toBeInTheDocument();
    expect(c).toMatchObject({ paymentTerms: '45 days net', supplierId: null, counterpartyName: counterparty });
  });

  it('a contract that has a counterparty still cannot lose it', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.pet });
    const c = api.db.contracts.find((x) => x.supplierId && x.status === 'Draft')!;
    const detail = await api.getContract(c.id);
    const e = await api.updateContract(c.id, { ...(detail as never as Record<string, unknown>), supplierId: null } as never).catch((x: unknown) => x);
    expect(e).toMatchObject({ status: 400 });
    expect((e as ApiError).problem.errors?.supplierId?.[0]).toMatch(/Choose a counterparty from the list/);
  });
});

describe('M1: a refused action shows the reason, not "One or more fields are invalid."', () => {
  it('a rename the API refuses shows the sentence of the field in the toast', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ today: TODAY, userId: USER.pet });
    // The answer is shaped as the API writes it: a generic detail, the reason at the field.
    vi.spyOn(api.admin, 'updateContractType').mockRejectedValue(new ApiError({ title: 'Validation failed', status: 400, detail: 'One or more fields are invalid.', errors: { name: ['A contract type with this name already exists.'] } }));
    renderWith(api, '/settings/contract-types');
    const list = await screen.findByRole('list', { name: 'Contract types' });
    await user.click(within(list).getAllByRole('button', { name: /^Rename / })[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Rename contract type' });
    await user.type(within(dialog).getByLabelText('Name'), ' 2');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('A contract type with this name already exists.');
    expect(screen.queryByText(/One or more fields are invalid/)).not.toBeInTheDocument();
  });

  it('the sign dialog shows the server sentence at the date field', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ today: TODAY, userId: USER.ann });
    const id = api.db.contracts.find((c) => c.title === FIRE)!.id;
    vi.spyOn(api, 'signContract').mockRejectedValue(new ApiError({ title: 'Validation failed', status: 400, detail: 'One or more fields are invalid.', errors: { signedDate: ['The signed date cannot be before the contract was approved.'] } }));
    renderWith(api, `/contracts/${id}`);
    await user.click((await screen.findAllByRole('button', { name: 'Record manual signature' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Record manual signature' });
    await user.click(within(dialog).getByRole('button', { name: 'Record signature' }));
    expect(await within(dialog).findByText('The signed date cannot be before the contract was approved')).toBeInTheDocument();
  });
});

describe('M4: Record manual signature only for users who can edit', () => {
  it('an owner without the ContractOwner role is not offered the button', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.ann });
    const c = api.db.contracts.find((x) => x.title === FIRE)!;
    expect(c.ownerId).toBe(USER.ann);
    api.db.users.find((u) => u.id === USER.ann)!.roles = ['Reader', 'Approver'];
    renderWith(api, `/contracts/${c.id}`);
    expect(await screen.findByRole('heading', { level: 1, name: FIRE })).toBeInTheDocument();
    await screen.findByRole('region', { name: 'Status' }).catch(() => screen.findByText('Approved'));
    expect(screen.queryByRole('button', { name: 'Record manual signature' })).not.toBeInTheDocument();
    // And the API would have refused it.
    await expect(api.signContract(c.id, { signedDate: TODAY, method: 'Wet ink, scanned' })).rejects.toMatchObject({ status: 403 });
  });

  it('the owner with the role is', async () => {
    const api = renderApp('/', USER.ann);
    const id = api.db.contracts.find((x) => x.title === FIRE)!.id;
    document.body.innerHTML = '';
    renderApp(`/contracts/${id}`, USER.ann);
    expect((await screen.findAllByRole('button', { name: 'Record manual signature' })).length).toBeGreaterThan(0);
  });
});

describe('M5: "Only approved contracts can be sent" switched off', () => {
  const setup = async (requireApproved: boolean) => {
    const api = createMockApi({ today: TODAY, userId: USER.pet });
    await api.admin.updateESignSettings({ ...(await api.admin.getESignSettings()), requireApproved });
    await api.devLogin(USER.ras);
    const c = api.db.contracts.find((x) => x.status === 'Active' && x.title !== NO_MASTER)!;
    renderWith(api, `/contracts/${c.id}`);
    expect(await screen.findByRole('heading', { level: 1, name: c.title })).toBeInTheDocument();
    await screen.findByRole('button', { name: /Watch|Watching|Stop watching/ });
    return { api, c };
  };

  it('offers Send for signature on an active contract and the API accepts it', async () => {
    const user = userEvent.setup();
    const { api, c } = await setup(false);
    expect((await api.getAppConfig()).eSign).toMatchObject({ enabled: true, requireApproved: false });
    await user.click(await screen.findByRole('button', { name: 'Send for signature' }));
    expect(await screen.findByRole('dialog', { name: 'Send for signature' })).toBeInTheDocument();
    expect((await api.getSignaturePermissions(c.id)).canSend).toBe(true);
  });

  it('with the setting on, an active contract has no Send for signature', async () => {
    await setup(true);
    expect(screen.queryByRole('button', { name: 'Send for signature' })).not.toBeInTheDocument();
  });
});

describe('L1 and L6: the signature request', () => {
  it('the approval log is not offered as a document to sign', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ today: TODAY, userId: USER.ras });
    const c = api.db.contracts.find((x) => x.title === FIRE)!;
    expect(api.db.documents.some((d) => d.contractId === c.id && d.kind === 'ApprovalLog')).toBe(true);
    renderWith(api, `/contracts/${c.id}`);
    await user.click((await screen.findAllByRole('button', { name: 'Send for signature' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Send for signature' });
    const docs = within(dialog).getByRole('group', { name: 'Documents to sign' });
    expect(within(docs).getAllByRole('checkbox').length).toBeGreaterThan(0);
    expect(within(docs).queryByText(/Approval log/)).not.toBeInTheDocument();
  });

  it('someone who cannot manage the request sees it without Resend, Cancel and Simulate', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.ras });
    const c = api.db.contracts.find((x) => x.title === FIRE)!;
    const doc = api.db.documents.find((d) => d.contractId === c.id && d.kind === 'Main')!;
    await api.createSignatureRequest(c.id, { documentIds: [doc.id], order: 'Parallel', message: '', signers: [{ kind: 'External', name: 'Klaus Weber', email: 'klaus@example.de', order: 1 }] });
    // The owner has no signing right and did not send it.
    await api.devLogin(USER.ann);
    renderWith(api, `/contracts/${c.id}`);
    expect(await screen.findByText('Out for signature')).toBeInTheDocument();
    expect(screen.getByText('Klaus Weber')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Resend' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel request' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Simulate signing' })).not.toBeInTheDocument();
  });

  it('the sender has them', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.ras });
    const c = api.db.contracts.find((x) => x.title === FIRE)!;
    const doc = api.db.documents.find((d) => d.contractId === c.id && d.kind === 'Main')!;
    await api.createSignatureRequest(c.id, { documentIds: [doc.id], order: 'Parallel', message: '', signers: [{ kind: 'External', name: 'Klaus Weber', email: 'klaus@example.de', order: 1 }] });
    renderWith(api, `/contracts/${c.id}`);
    expect(await screen.findByRole('button', { name: 'Resend' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancel request' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Simulate signing' })).toBeInTheDocument();
  });
});

describe('M6: a signed-in account without access (Entra)', () => {
  it('a 403 on me says so instead of "could not start"', async () => {
    const redirect = vi.spyOn(browser, 'redirect').mockImplementation(() => undefined);
    const mock = createMockApi({ today: TODAY });
    const api: Api = { ...mock, getDevConfig: () => Promise.resolve({ authMode: 'Entra', accessCodeRequired: false }), getMe: () => Promise.reject(new ApiError({ title: 'Forbidden', status: 403 })) };
    renderWith(api, '/contracts');
    expect(await screen.findByText(NO_ACCESS_MESSAGE)).toBeInTheDocument();
    expect(NO_ACCESS_MESSAGE).toBe('Your account does not have access to DAFA Contracts. Contact an administrator.');
    expect(screen.queryByText(/could not start/)).not.toBeInTheDocument();
    expect(redirect).not.toHaveBeenCalled();
  });
});

describe('M7: the session ends while the app is open', () => {
  it('Dev: the next call that gets a 401 leads to the user picker, once, and the page is kept', async () => {
    const user = userEvent.setup();
    const api = renderApp('/renewals', USER.ras);
    const nav = await screen.findByRole('navigation', { name: 'Main' });
    const getMe = vi.spyOn(api, 'getMe');
    // The session times out on the server; the app does not know yet.
    await api.logout();
    await user.click(within(nav).getByRole('link', { name: 'Contracts' }));
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(router.pathname).toBe('/contracts');
    const again = await screen.findByRole('button', { name: /Rasmus Holm/ });
    // `me` was asked again exactly once, however many calls failed with 401.
    await new Promise((r) => setTimeout(r, 50));
    expect(getMe).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(/could not be loaded/)).not.toBeInTheDocument();
    await user.click(again);
    expect(await screen.findByRole('navigation', { name: 'Main' })).toBeInTheDocument();
    expect(router.pathname).toBe('/contracts');
    expect(await screen.findByRole('heading', { level: 1, name: 'Contracts' })).toBeInTheDocument();
  });

  it('Dev: a save that gets a 401 leads to sign-in too', async () => {
    const user = userEvent.setup();
    const api = renderApp('/settings/departments', USER.pet);
    const list = await screen.findByRole('list', { name: 'Departments' });
    await api.logout();
    await user.click(within(list).getAllByRole('button', { name: 'Deactivate' })[0]!);
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(router.pathname).toBe('/settings/departments');
  });

  it('Entra: one redirect to sign-in with the current path, and no loop', async () => {
    const user = userEvent.setup();
    const redirect = vi.spyOn(browser, 'redirect').mockImplementation(() => undefined);
    const mock = createMockApi({ today: TODAY, userId: USER.ras });
    let signedIn = true;
    const gate = <T,>(fn: () => Promise<T>) => (signedIn ? fn() : Promise.reject(new ApiError({ title: 'Unauthorized', status: 401 })));
    const api: Api = {
      ...mock,
      getDevConfig: () => Promise.resolve({ authMode: 'Entra', accessCodeRequired: false }),
      getMe: () => gate(async () => ({ ...(await mock.getMe()), authMode: 'Entra' as const })),
      getContracts: (q) => gate(() => mock.getContracts(q)),
      getUnreadCount: () => gate(() => mock.getUnreadCount()),
      getViews: () => gate(() => mock.getViews()),
    };
    const getMe = vi.spyOn(api, 'getMe');
    renderWith(api, '/renewals');
    const nav = await screen.findByRole('navigation', { name: 'Main' });
    expect(getMe).toHaveBeenCalledTimes(1);
    signedIn = false;
    await user.click(within(nav).getByRole('link', { name: 'Contracts' }));
    await waitFor(() => expect(redirect).toHaveBeenCalledTimes(1));
    expect(redirect).toHaveBeenCalledWith(`/api/auth/login?returnUrl=${encodeURIComponent(window.location.pathname + window.location.search + window.location.hash)}`);
    expect(await screen.findByRole('heading', { name: 'Signing you in' })).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 50));
    expect(redirect).toHaveBeenCalledTimes(1);
    expect(getMe).toHaveBeenCalledTimes(2);
  });
});

describe('L2, L3, L4, L5', () => {
  it('L2: General settings show e-signature read-only, derived from the provider', async () => {
    const api = renderApp('/settings/general', USER.pet);
    const line = await screen.findByLabelText('E-signature');
    expect(line).toHaveTextContent('E-signature is on (simulated signing)');
    expect(within(line).getByRole('link', { name: /E-signature/ })).toHaveAttribute('href', '/settings/esign');
    expect(screen.queryByRole('checkbox', { name: /E-signature/ })).not.toBeInTheDocument();
    // A saved value is ignored by the API (and by the mock).
    const s = await api.admin.getAppSettings();
    expect(s.features.eSign).toBe(true);
    expect((await api.admin.updateAppSettings({ ...s, features: { ...s.features, eSign: false } })).features.eSign).toBe(true);
  });

  it('L3: a .dotx template is refused before it is sent', async () => {
    const user = userEvent.setup({ applyAccept: false });
    const api = renderApp('/settings/templates', USER.pet);
    const create = vi.spyOn(api.admin, 'createTemplate');
    await user.click(await screen.findByRole('button', { name: /Upload template|Add template/ }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Name'), 'Dot template');
    await user.upload(dialog.querySelector('input[type="file"]') as HTMLInputElement, new File(['x'], 'old.dotx'));
    await user.click(within(dialog).getByRole('button', { name: /Upload|Save|Add/ }));
    expect(await within(dialog).findByText(/Templates must be Word files \(\.docx\)/)).toBeInTheDocument();
    expect(create).not.toHaveBeenCalled();
  });

  it('L4: the value list of a column filter is counted within the active tab', async () => {
    const user = userEvent.setup();
    const api = renderApp('/contracts?view=mine', USER.hen);
    const distinct = vi.spyOn(api, 'getDistinct');
    await user.click(await screen.findByRole('button', { name: /Filter Status/ }));
    await waitFor(() => expect(distinct).toHaveBeenCalled());
    expect(distinct.mock.calls[0]![1]).toMatchObject({ view: 'mine' });
    const mine = await api.getDistinct('status', { view: 'mine' } as never, undefined, 100);
    const all = await api.getDistinct('status', {}, undefined, 100);
    const total = (r: typeof all) => r.values.reduce((n, v) => n + v.count, 0);
    expect(total(mine)).toBeLessThan(total(all));
    expect(total(mine)).toBe((await api.getContracts({ view: 'mine', includeEnded: true, pageSize: 200 } as never)).total);
  });

  it('L5: the counterparty search of the mock and the picker accept a VAT number', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.jon });
    const s = api.db.suppliers.find((x) => x.isActive)!;
    s.vatNo = 'DK99887766';
    const found = await api.getCounterparties('99887766');
    expect(found.map((x) => x.name)).toEqual([s.name]);
    const user = userEvent.setup();
    renderWith(api, '/contracts/new');
    await user.click(await screen.findByRole('button', { name: 'Continue without a file' }));
    const box = await screen.findByRole('combobox', { name: /Counterparty/ });
    await user.click(box);
    await user.type(box, '99887766');
    const option = await within(await screen.findByRole('listbox')).findByRole('option', { name: new RegExp(s.name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) });
    expect(option).toHaveTextContent('DK99887766');
  });

  it('L5: creating a counterparty whose VAT number exists chooses the existing one under its master name', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.pet });
    const s = api.db.suppliers.find((x) => x.isActive)!;
    s.vatNo = 'DK99887766';
    const user = userEvent.setup();
    renderWith(api, '/contracts/new');
    await user.click(await screen.findByRole('button', { name: 'Continue without a file' }));
    const box = await screen.findByRole('combobox', { name: /Counterparty/ });
    await user.click(box);
    await user.type(box, 'Zzz Trading');
    await user.click(await screen.findByRole('button', { name: /Create new counterparty/ }).catch(() => screen.findByText(/Create new counterparty/)));
    const dialog = await screen.findByRole('dialog', { name: 'Create new counterparty' });
    await user.type(within(dialog).getByLabelText(/VAT number/), 'DK99887766');
    await user.click(within(dialog).getByRole('button', { name: 'Create counterparty' }));
    expect(await screen.findByText(`${s.name} already exists. It has been chosen.`)).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('combobox', { name: /Counterparty/ })).toHaveAttribute('placeholder', s.name));
  });
});
