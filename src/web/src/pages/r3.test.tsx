import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { USER, TYP, typeId } from '../api/mock/seed';
import { renderApp } from '../test/renderApp';

describe('Search page (release 3)', () => {
  it('groups results and highlights the matched words in a document of another owner', async () => {
    renderApp('/search?q=FSC%20certified', USER.jon);
    const docs = await screen.findByRole('region', { name: /Documents/ }).catch(() => screen.findByRole('heading', { name: /Documents/ }));
    expect(docs).toBeInTheDocument();
    expect(await screen.findByText('Packaging supply – signed.pdf')).toBeInTheDocument();
    const marks = document.querySelectorAll('mark');
    expect([...marks].map((m) => m.textContent?.toLowerCase())).toEqual(expect.arrayContaining(['fsc', 'certified']));
    expect(screen.getByText(/Owner Camilla Frost/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Download Packaging supply – signed.pdf' })).toBeInTheDocument();
    // Result type tabs carry the counts.
    expect(screen.getByRole('button', { name: 'Documents (1)' })).toBeInTheDocument();
  });

  it('does not show a document outside the user’s access', async () => {
    renderApp('/search?q=FSC', USER.emm);
    expect(await screen.findByText(/Nothing matches/)).toBeInTheDocument();
  });

  it('opens a person page with every role, reachable from search', async () => {
    const user = userEvent.setup();
    renderApp('/search?q=Mette&group=people');
    await user.click(await screen.findByRole('link', { name: /Mette\s+Kjær/ }));
    expect(await screen.findByRole('heading', { name: 'Contracts of Mette Kjær' })).toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Contracts of Mette Kjær' })).toBeInTheDocument();
  });
});

describe('Custom fields in forms (release 3)', () => {
  it('a field the admin adds appears in New contract for its type and is required', async () => {
    const user = userEvent.setup();
    const api = renderApp('/contracts/new', USER.jon);
    api.db.customFields.push({ id: 'x1', key: 'insuranceRef', label: 'Insurance reference', type: 'Text', contractTypeIds: [typeId(TYP.direct)], required: true, helpText: 'Policy number', order: 99, options: [], showInRegister: true, showInFilters: false, isActive: true });
    await user.click(await screen.findByRole('button', { name: 'Continue without a file' }));
    expect(screen.queryByRole('textbox', { name: /Insurance reference/ })).not.toBeInTheDocument();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Contract type' }), typeId(TYP.direct));
    const input = await screen.findByRole('textbox', { name: /Insurance reference/ });
    expect(input).toBeInTheDocument();
    await user.click(screen.getAllByRole('button', { name: 'Continue' })[0]!);
    expect(await screen.findByText('Insurance reference is required')).toBeInTheDocument();
  });

  it('type defaults from the field rules fill the form when the type is chosen', async () => {
    const user = userEvent.setup();
    renderApp('/contracts/new', USER.sor);
    await user.click(await screen.findByRole('button', { name: 'Continue without a file' }));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Contract type' }), typeId(TYP.it));
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Notice period (days)' })).toHaveValue('90'));
    expect(screen.getByRole('combobox', { name: 'Reminders go to' })).toHaveValue('Responsible');
  });
});

describe('Register views and bulk actions (release 3)', () => {
  it('saves the current filters as a view and applies it again', async () => {
    const user = userEvent.setup();
    renderApp('/contracts?status=Active&type=' + typeId(TYP.it), USER.ras);
    await screen.findByRole('table', { name: 'Contracts' });
    await user.click(screen.getByRole('button', { name: 'Save view' }));
    const dialog = await screen.findByRole('dialog', { name: 'Save view' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Name' }), 'Active IT');
    await user.click(within(dialog).getByRole('button', { name: 'Save view' }));
    const select = await screen.findByRole('combobox', { name: 'Saved view' });
    await waitFor(() => expect(within(select).getByRole('option', { name: 'Active IT' })).toBeInTheDocument());
    await waitFor(() => expect((select as HTMLSelectElement).selectedOptions[0]?.textContent).toBe('Active IT'));
  });

  it('selects rows and adds a tag to them', async () => {
    const user = userEvent.setup();
    const api = renderApp('/contracts?q=Adhesive', USER.jon);
    const table = await screen.findByRole('table', { name: 'Contracts' });
    await user.click(within(table).getByRole('checkbox', { name: 'Select Adhesive supply agreement' }));
    const bar = screen.getByRole('region', { name: 'Selected contracts' });
    await user.click(within(bar).getByRole('button', { name: 'Add tag' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add a tag' });
    await user.selectOptions(within(dialog).getByRole('combobox', { name: 'Tag' }), 'ESG review');
    await user.click(within(dialog).getByRole('button', { name: 'Apply' }));
    await waitFor(() => expect(api.db.contracts.find((c) => c.title === 'Adhesive supply agreement')?.tagIds.length).toBe(1));
  });
});

describe('Collaboration on the record (release 3)', () => {
  it('posts a comment with an @mention and the mentioned person gets a notification', async () => {
    const user = userEvent.setup();
    const api = renderApp('/contracts/00000006-0000-4000-8000-000000000006?tab=comments', USER.jon);
    const box = await screen.findByRole('combobox', { name: 'Add a comment' });
    await user.type(box, 'Please check @Cam');
    await user.click(await screen.findByRole('option', { name: /Camilla Frost/ }));
    await user.type(box, 'the volumes');
    await user.click(screen.getByRole('button', { name: 'Post comment' }));
    expect(await screen.findByText('@Camilla Frost')).toBeInTheDocument();
    expect(api.db.notifications.some((n) => n.userId === USER.cam && n.event === 'Mention')).toBe(true);
  });
});
