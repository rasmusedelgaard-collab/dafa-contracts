// Release 4 pages: e-signature, templates by category, logo, entity scope chip, storage problems.
import { afterEach, describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '../api/api';
import { STORAGE_PROBLEMS } from '../api/types';
import { ENT, USER, entityId } from '../api/mock/seed';
import { renderApp, urlParams } from '../test/renderApp';
import { pick } from '../test/pickers';

afterEach(() => localStorage.clear());

const FIRE = 'Fire protection service agreement';

describe('E-signature (owner item H)', () => {
  it('a permitted user sends an approved contract for signature; simulated signing stores the signed document and activates it', async () => {
    const user = userEvent.setup();
    const probe = renderApp('/', USER.ras);
    const id = probe.db.contracts.find((c) => c.title === FIRE)!.id;
    document.body.innerHTML = '';
    const api = renderApp(`/contracts/${id}`, USER.ras);
    await user.click((await screen.findAllByRole('button', { name: 'Send for signature' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Send for signature' });
    await pick(user, within(dialog).getByRole('combobox', { name: 'Signatory 1' }), 'Rasmus Holm');
    await user.type(within(dialog).getByLabelText('Signatory 2 name'), 'Klaus Weber');
    await user.type(within(dialog).getByLabelText('Signatory 2 e-mail'), 'klaus@example.de');
    await user.click(within(dialog).getByRole('button', { name: 'Preview' }));
    expect(within(dialog).getByText(/Klaus Weber <klaus@example.de>/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Send now' }));
    expect(await screen.findByText('Sent for signature')).toBeInTheDocument();
    const card = await screen.findByRole('region', { name: 'Signature' }).catch(() => screen.getByRole('heading', { name: 'Signature' }).closest('section, div')! as HTMLElement);
    expect(within(card).getByText('Out for signature')).toBeInTheDocument();
    // In order: Rasmus first, then Klaus.
    await user.click(within(screen.getByRole('group', { name: 'Simulate signing for Rasmus Holm' })).getByRole('button', { name: 'Simulate signing' }));
    await user.click(await within(await screen.findByRole('group', { name: 'Simulate signing for Klaus Weber' })).findByRole('button', { name: 'Simulate signing' }));
    expect((await screen.findAllByText(/Signed by everyone/)).length).toBeGreaterThan(0);
    const c = api.db.contracts.find((x) => x.id === id)!;
    expect(c.status).toBe('Active');
    expect(api.db.documents.some((d) => d.contractId === id && d.name.endsWith('– signed.pdf'))).toBe(true);
  });

  it('a user without the right is shown who can send it instead', async () => {
    const user = userEvent.setup();
    const probe = renderApp('/', USER.hen);
    const id = probe.db.contracts.find((c) => c.title === FIRE)!.id;
    document.body.innerHTML = '';
    const api = renderApp(`/contracts/${id}`, USER.hen);
    await user.click((await screen.findAllByRole('button', { name: 'Send for signature' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Ask a colleague with signing rights' });
    const people = await within(dialog).findByRole('list', { name: 'People with signing rights' });
    expect(within(people).getByText('Rasmus Holm')).toBeInTheDocument();
    await expect(api.createSignatureRequest(id, { documentIds: [], signers: [], order: 'Sequential', message: '' })).rejects.toMatchObject({ status: 403 });
  });
});

describe('Templates by category (owner item E)', () => {
  it('New contract shows categories as filters and a preview of the fields', async () => {
    const user = userEvent.setup();
    renderApp('/contracts/new?start=template', USER.jon);
    const cats = await screen.findByRole('group', { name: 'Template category' });
    await user.click(within(cats).getByRole('button', { name: /Confidentiality/ }));
    expect(screen.queryByRole('button', { name: /^DOC Supply agreement/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Mutual NDA/ })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Preview' }));
    expect(await screen.findByText('{{counterparty}}')).toBeInTheDocument();
  });

  it('an admin adds a category and it is listed', async () => {
    const user = userEvent.setup();
    renderApp('/settings/template-categories', USER.pet);
    await user.click(await screen.findByRole('button', { name: 'Add category' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add category' });
    await user.type(within(dialog).getByLabelText('Name'), 'Logistics');
    await user.click(within(dialog).getByRole('button', { name: 'Add category' }));
    expect(await within(await screen.findByRole('list', { name: 'Template categories' })).findByText('Logistics')).toBeInTheDocument();
  });
});

describe('Logo (owner item F)', () => {
  it('an uploaded logo replaces the default mark in the menu, as an image', async () => {
    const user = userEvent.setup();
    renderApp('/settings/general', USER.pet);
    const input = await screen.findByLabelText('Upload logo');
    await user.upload(input, new File(['<svg xmlns="http://www.w3.org/2000/svg"/>'], 'logo.svg', { type: 'image/svg+xml' }));
    expect(await screen.findByText('Logo saved')).toBeInTheDocument();
    const nav = screen.getByRole('navigation', { name: 'Main' });
    await waitFor(() => expect(within(nav).getByRole('img', { name: 'DAFA Contracts' })).toHaveAttribute('src', expect.stringMatching(/^data:image\/svg\+xml/)));
    expect(nav.querySelector('svg.brand-inline')).toBeNull();
  });

  it('rejects a file that is too large', async () => {
    const user = userEvent.setup();
    renderApp('/settings/general', USER.pet);
    const input = await screen.findByLabelText('Upload logo');
    await user.upload(input, new File([new Uint8Array(1_100_000)], 'big.png', { type: 'image/png' }));
    expect(await screen.findByText('The logo can be at most 1 MB.')).toBeInTheDocument();
  });
});

describe('Global entity scope (owner item A3)', () => {
  it('scopes My work and Renewals and shows as a removable chip', async () => {
    const user = userEvent.setup();
    const api = renderApp(`/renewals?entity=${entityId(ENT.dafa)}`, USER.ras);
    const chip = await screen.findByRole('navigation', { name: 'Entity scope' });
    const scoped = await api.getRenewals(13, [entityId(ENT.dafa)]);
    const all = await api.getRenewals(13);
    expect(scoped.length).toBeLessThan(all.length);
    await user.click(within(chip).getByRole('button', { name: /^Remove entity scope/ }));
    await waitFor(() => expect(urlParams().get('entity')).toBeNull());
    expect(screen.queryByRole('navigation', { name: 'Entity scope' })).not.toBeInTheDocument();
  });
});

describe('Storage problems (the app is the only door)', () => {
  it('"SharePoint is busy" offers Retry; a missing file has its own sentence', async () => {
    const user = userEvent.setup();
    const probe = renderApp('/', USER.ras);
    const id = probe.db.contracts.find((c) => c.title === FIRE)!.id;
    document.body.innerHTML = '';
    const api = renderApp(`/contracts/${id}`, USER.ras);
    let calls = 0;
    const original = api.getDocumentContent;
    api.getDocumentContent = async (docId) => {
      calls += 1;
      if (calls === 1) throw new ApiError({ type: STORAGE_PROBLEMS.unavailable, title: 'Busy', status: 503 });
      if (calls === 2) throw new ApiError({ type: STORAGE_PROBLEMS.fileMissing, title: 'Missing', status: 404 });
      return original(docId);
    };
    await user.click((await screen.findAllByRole('button', { name: /^Download/ }))[0]!);
    expect(await screen.findByText('SharePoint is busy. Try again in a moment.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('This file can no longer be found in storage. An administrator has been notified.')).toBeInTheDocument();
    expect(calls).toBe(2);
  });
});
