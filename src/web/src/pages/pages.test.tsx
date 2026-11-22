import { describe, expect, it } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { App } from '../App';
import { ApiProvider } from '../app/context';
import { createMockApi, type MockApi } from '../api/mock/mockApi';
import { USER } from '../api/mock/seed';
import { ToastProvider } from '../components';

function renderApp(path: string, userId = USER.ras): MockApi {
  const api = createMockApi({ today: '2026-11-22', userId });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <ApiProvider api={api}>
        <ToastProvider>
          <MemoryRouter initialEntries={[path]}>
            <App />
          </MemoryRouter>
        </ToastProvider>
      </ApiProvider>
    </QueryClientProvider>,
  );
  return api;
}

describe('app shell', () => {
  it('shows the Work and Manage navigation for group management, without Settings', async () => {
    renderApp('/');
    const nav = await screen.findByRole('navigation', { name: 'Main' });
    expect(within(nav).getByRole('link', { name: /My work/ })).toHaveAttribute('aria-current', 'page');
    expect(within(nav).getByRole('link', { name: 'Overview' })).toBeInTheDocument();
    expect(within(nav).queryByRole('link', { name: 'Settings' })).not.toBeInTheDocument();
  });

  it('hides Manage links and New contract from a reader', async () => {
    renderApp('/', USER.emm);
    const nav = await screen.findByRole('navigation', { name: 'Main' });
    expect(within(nav).queryByRole('link', { name: 'Overview' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'New contract' })).not.toBeInTheDocument();
  });

});

describe('Home', () => {
  it('lists the tasks and completes an obligation from the list', async () => {
    const user = userEvent.setup();
    renderApp('/');
    expect(await screen.findByText('2 decisions need you')).toBeInTheDocument();
    const done = await screen.findByRole('button', { name: 'Mark done: Send the renewal questionnaire to the broker' });
    await user.click(done);
    expect(await screen.findByText(/Marked as done/)).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText('Due in 11 days')).not.toBeInTheDocument());
  });

  it('filters the task list to approvals', async () => {
    const user = userEvent.setup();
    renderApp('/');
    await screen.findByText('Group insurance programme');
    await user.click(screen.getByRole('button', { name: 'Approvals', pressed: false }));
    expect(screen.queryByText('Group insurance programme')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review: ERP software subscription' })).toBeInTheDocument();
  });
});

describe('Contracts register', () => {
  it('shows only what the user may see and filters by view', async () => {
    const user = userEvent.setup();
    renderApp('/contracts', USER.emm);
    expect(await screen.findByText('2 contracts you can see, across 1 legal entity')).toBeInTheDocument();
    const table = await screen.findByRole('table', { name: 'Contracts' });
    expect(within(table).getAllByRole('row')).toHaveLength(3);
    await user.click(screen.getByRole('button', { name: 'In approval' }));
    expect(await screen.findByText('No contracts match these filters')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Clear filters' }));
    expect(await screen.findByRole('table', { name: 'Contracts' })).toBeInTheDocument();
  });

  it('reads view and search from the URL and shows the quick look', async () => {
    renderApp('/contracts?view=due90&q=insurance');
    const table = await screen.findByRole('table', { name: 'Contracts' });
    expect(within(table).getAllByRole('row')).toHaveLength(2);
    const quick = screen.getByRole('complementary', { name: 'Quick look' });
    expect(within(quick).getByRole('heading', { name: 'Group insurance programme' })).toBeInTheDocument();
    expect(within(quick).getByText('27')).toBeInTheDocument();
    expect(await within(quick).findByText('Auto, 12 months')).toBeInTheDocument();
  });
});

describe('Contract record', () => {
  it('says so when the user has no access', async () => {
    // Seeded ids are deterministic, so a second mock instance gives the same id.
    const api = createMockApi({ today: '2026-11-22' });
    const erp = api.db.contracts.find((c) => c.title === 'ERP software subscription')!;
    renderApp(`/contracts/${erp.id}`, USER.emm);
    expect(await screen.findByText('This contract is not available')).toBeInTheDocument();
  });

  it('lets a person on the contract record the renewal decision', async () => {
    const user = userEvent.setup();
    const api = createMockApi({ today: '2026-11-22', userId: USER.met });
    const id = api.db.contracts.find((c) => c.title === 'Raw material supply – rubber compounds')!.id;
    renderApp(`/contracts/${id}`, USER.met);
    await user.click(await screen.findByRole('button', { name: 'Give notice' }));
    const dialog = await screen.findByRole('dialog', { name: 'Decide on renewal' });
    await waitFor(() => expect(within(dialog).getByRole('radio', { name: /Give notice/ })).toBeChecked());
    await user.click(within(dialog).getByRole('button', { name: 'Save decision' }));
    expect(await within(dialog).findByText('Add a comment that explains why the contract ends')).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: /Comment/ }), 'Moving volume');
    await user.click(within(dialog).getByRole('button', { name: 'Save decision' }));
    expect(await screen.findByText('Notice given', { selector: '.pill' })).toBeInTheDocument();
  });
});

describe('New contract', () => {
  it('blocks the details step until the required fields are filled', async () => {
    const user = userEvent.setup();
    renderApp('/contracts/new', USER.jon);
    await user.click(await screen.findByRole('button', { name: 'Continue without a file' }));
    await user.click(screen.getAllByRole('button', { name: 'Continue' })[0]!);
    expect(await screen.findByText('Enter a title')).toBeInTheDocument();
    expect(screen.getByText('Choose whether there is a change-of-control clause')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Title' })).toHaveAttribute('aria-invalid', 'true');
    expect(screen.queryByRole('heading', { name: 'Links and obligations' })).not.toBeInTheDocument();
  });

  it('turns an upload into suggestions that are accepted one by one or all at once', async () => {
    const user = userEvent.setup();
    renderApp('/contracts/new', USER.tho);
    await screen.findByRole('heading', { name: 'New contract' });
    const input = document.querySelector<HTMLInputElement>('input[type="file"]')!;
    await user.upload(input, new File([new Uint8Array(300_000)], 'Freight agreement ScanFreight – draft.pdf', { type: 'application/pdf' }));
    expect(await screen.findByLabelText('Counterparty')).toHaveValue('ScanFreight Logistics A/S');
    const before = screen.getAllByText('Suggested').length;
    await user.click(screen.getByRole('button', { name: 'Accept suggestion for Counterparty' }));
    expect(screen.getAllByText('Suggested')).toHaveLength(before - 1);
    expect(screen.getAllByText('Accepted')).toHaveLength(1);
    await user.click(screen.getByRole('button', { name: 'Accept all suggestions' }));
    expect(screen.queryByText('Suggested')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'All suggestions handled' })).toBeDisabled();
  });
});
