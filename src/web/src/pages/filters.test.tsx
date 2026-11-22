// Regression tests for "If I add an entity to the overview, I cannot remove it from the filters again."
// Root cause: the top-bar entity scope kept its own copy of `?entity=` and wrote it back into the
// URL whenever the parameter disappeared, so every way of removing the entity filter was undone.
// The scope is now read from the URL; it is only carried over when the user moves to another page.
import { describe, expect, it } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ContractFilter } from '../api/types';
import { DEP, ENT, TYP, USER, departmentId, entityId, typeId } from '../api/mock/seed';
import type { MockApi } from '../api/mock/mockApi';
import { seedId } from '../api/mock/db';
import { renderApp, router, urlParams } from '../test/renderApp';

const kpi = (label: string) => {
  const tile = [...document.querySelectorAll('.kpi')].find((k) => k.querySelector('.kpi__label')?.textContent === label);
  return tile?.querySelector('.kpi__value')?.textContent ?? null;
};
async function totalOf(api: MockApi, filter: ContractFilter = {}) {
  return String((await api.getOverviewAnalytics(filter)).kpis.count);
}
async function overviewReady() {
  await screen.findByRole('heading', { level: 1, name: 'Overview' });
  await waitFor(() => expect(kpi('Contracts')).not.toBeNull());
}
const crumbs = () => screen.queryByRole('navigation', { name: 'Applied drill-downs' });
const scopeSelect = () => screen.getAllByRole('combobox', { name: 'Legal entity scope' })[0]!;
const entitySelect = () => screen.getByRole('combobox', { name: 'Entity' });

/** Waits until the filter is gone everywhere: URL, breadcrumb, filter bar, top bar and totals. */
async function expectUnfiltered(total: string, param = 'entity') {
  await waitFor(() => expect(urlParams().getAll(param)).toEqual([]));
  await waitFor(() => expect(crumbs()).toBeNull());
  await waitFor(() => expect(kpi('Contracts')).toBe(total));
  expect(scopeSelect()).toHaveValue('');
  // Nothing puts it back a moment later.
  await act(() => new Promise((r) => setTimeout(r, 50)));
  expect(urlParams().getAll(param)).toEqual([]);
}

const DAFA = entityId(ENT.dafa);
/** First seeded supplier (counterparty) and tag. */
const CP = seedId(5, 1);
const TAG = seedId(20, 1);

describe('Overview: an entity filter can be removed again', () => {
  const removals = {
    'the breadcrumb remove button': async (user: ReturnType<typeof userEvent.setup>) => user.click(within(crumbs()!).getByRole('button', { name: /^Remove / })),
    'Clear all': async (user: ReturnType<typeof userEvent.setup>) => user.click(within(crumbs()!).getByRole('button', { name: 'Clear all' })),
    'the filter bar set back to All': async (user: ReturnType<typeof userEvent.setup>) => user.selectOptions(entitySelect(), ''),
    'the top-bar scope set back to All entities': async (user: ReturnType<typeof userEvent.setup>) => user.selectOptions(scopeSelect(), ''),
  };

  for (const [how, remove] of Object.entries(removals)) {
    it(`added in the filter bar, removed with ${how}`, async () => {
      const user = userEvent.setup();
      const api = renderApp('/overview');
      await overviewReady();
      const total = await totalOf(api);
      await user.selectOptions(entitySelect(), DAFA);
      await waitFor(() => expect(urlParams().get('entity')).toBe(DAFA));
      const filtered = await totalOf(api, { entityId: DAFA });
      await waitFor(() => expect(kpi('Contracts')).toBe(filtered));
      expect(scopeSelect()).toHaveValue(DAFA);
      await remove(user);
      await expectUnfiltered(total);
      expect(entitySelect()).toHaveValue('');
    });

    it(`added from the URL, removed with ${how}`, async () => {
      const user = userEvent.setup();
      const api = renderApp(`/overview?entity=${DAFA}`);
      await overviewReady();
      await waitFor(async () => expect(kpi('Contracts')).toBe(await totalOf(api, { entityId: DAFA })));
      await remove(user);
      await expectUnfiltered(await totalOf(api));
    });
  }

  it('added by clicking a chart bar, removed with the breadcrumb, then Clear all after a second drill-down', async () => {
    const user = userEvent.setup();
    const api = renderApp('/overview');
    await overviewReady();
    const total = await totalOf(api);
    const bars = await screen.findByRole('list', { name: 'Annual value by legal entity' });
    await user.click(within(bars).getAllByRole('button')[0]!);
    await waitFor(() => expect(urlParams().get('entity')).not.toBeNull());
    expect(scopeSelect()).toHaveValue(urlParams().get('entity'));
    await user.click(within(crumbs()!).getByRole('button', { name: /^Remove / }));
    await expectUnfiltered(total);

    // Drill into an entity and a department, then Clear all.
    await user.click(within(await screen.findByRole('list', { name: 'Annual value by legal entity' })).getAllByRole('button')[0]!);
    await user.click(within(await screen.findByRole('list', { name: 'Annual value by department' })).getAllByRole('button')[0]!);
    await waitFor(() => expect(urlParams().get('department')).not.toBeNull());
    await user.click(within(crumbs()!).getByRole('button', { name: 'Clear all' }));
    await expectUnfiltered(total);
    expect(urlParams().get('department')).toBeNull();
  });

  it('added by a chart drill-down, removed with the browser back button', async () => {
    const user = userEvent.setup();
    const api = renderApp('/overview');
    await overviewReady();
    const total = await totalOf(api);
    await user.click(within(await screen.findByRole('list', { name: 'Annual value by legal entity' })).getAllByRole('button')[0]!);
    await waitFor(() => expect(urlParams().get('entity')).not.toBeNull());
    await act(async () => {
      await router.navigate!(-1);
    });
    await expectUnfiltered(total);
  });

  it('the data-quality score drill-down (entity + missing data) can be removed chip by chip', async () => {
    const user = userEvent.setup();
    const api = renderApp('/overview');
    await overviewReady();
    const total = await totalOf(api);
    const scores = await screen.findByRole('list', { name: 'Data quality by legal entity' }).catch(() => screen.findByRole('group', { name: 'Data quality by legal entity' }));
    await user.click(within(scores).getAllByRole('button')[0]!);
    await waitFor(() => expect(urlParams().get('missing')).toBe('true'));
    for (;;) {
      const nav = crumbs();
      if (!nav) break;
      const before = urlParams().toString();
      await user.click(within(nav).getAllByRole('button', { name: /^Remove / })[0]!);
      await waitFor(() => expect(urlParams().toString()).not.toBe(before));
    }
    await expectUnfiltered(total);
    expect(urlParams().get('missing')).toBeNull();
  });

  it('the entity scope still follows the user to another page, but not after it was removed', async () => {
    const user = userEvent.setup();
    renderApp('/overview');
    await overviewReady();
    await user.selectOptions(entitySelect(), DAFA);
    await waitFor(() => expect(urlParams().get('entity')).toBe(DAFA));
    await user.click(within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', { name: /^Contracts/ }));
    await waitFor(() => expect(router.pathname).toBe('/contracts'));
    await waitFor(() => expect(urlParams().get('entity')).toBe(DAFA));
    expect(screen.getByRole('combobox', { name: 'Entity' })).toHaveValue(DAFA);
    // Removed on the register, it stays removed on Overview.
    await user.selectOptions(screen.getByRole('combobox', { name: 'Entity' }), '');
    await waitFor(() => expect(urlParams().get('entity')).toBeNull());
    await user.click(within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', { name: 'Overview' }));
    await overviewReady();
    await act(() => new Promise((r) => setTimeout(r, 50)));
    expect(urlParams().get('entity')).toBeNull();
    expect(scopeSelect()).toHaveValue('');
  });
});

describe('Overview: every filter can be removed with its chip and with Clear all', () => {
  const PURCH = departmentId(DEP.purchasing);
  const FIN = departmentId(DEP.finance);
  const cases: { name: string; search: string; params: string[] }[] = [
    { name: 'department', search: `department=${PURCH}`, params: ['department'] },
    { name: 'two departments (repeated parameter)', search: `department=${PURCH}&department=${FIN}`, params: ['department'] },
    { name: 'contract type', search: `type=${typeId(TYP.direct)}`, params: ['type'] },
    { name: 'status', search: 'status=Active', params: ['status'] },
    { name: 'counterparty', search: 'counterparty=COUNTERPARTY', params: ['counterparty'] },
    { name: 'owner', search: `owner=${USER.met}`, params: ['owner'] },
    { name: 'value band', search: 'band=1to5m', params: ['band'] },
    { name: 'date range', search: 'dateField=end&from=2027-01-01&to=2027-12-31', params: ['dateField', 'from', 'to'] },
    { name: 'tag', search: 'tag=TAG', params: ['tag'] },
    { name: 'custom field', search: 'cf.riskLevel=High', params: ['cf.riskLevel'] },
    { name: 'yes/no custom field', search: 'cf.soleSupplier=true', params: ['cf.soleSupplier'] },
  ];

  for (const c of cases) {
    for (const how of ['chip', 'Clear all'] as const) {
      it(`${c.name}: removed with ${how}`, async () => {
        const user = userEvent.setup();
        const search = c.search.replace('COUNTERPARTY', CP).replace('TAG', TAG);
        const api = renderApp(`/overview?${search}`);
        await overviewReady();
        await waitFor(() => expect(crumbs()).not.toBeNull());
        const nav = crumbs()!;
        if (how === 'chip') {
          for (;;) {
            const buttons = within(crumbs() ?? nav).queryAllByRole('button', { name: /^Remove / });
            if (!crumbs() || buttons.length === 0) break;
            const before = urlParams().toString();
            await user.click(buttons[0]!);
            await waitFor(() => expect(urlParams().toString()).not.toBe(before));
          }
        } else {
          await user.click(within(nav).getByRole('button', { name: 'Clear all' }));
        }
        for (const p of c.params) await waitFor(() => expect(urlParams().getAll(p)).toEqual([]));
        await expectUnfiltered(await totalOf(api));
        // The filter bar shows "All" again for the select-type filters.
        for (const label of ['Department', 'Type', 'Status', 'Owner', 'Value', 'Dates', 'Counterparty']) expect(screen.getByRole('combobox', { name: label })).toHaveValue('');
      });
    }
  }

  it('a repeated parameter shows as "2 selected" and the select can be set back to All', async () => {
    const user = userEvent.setup();
    const api = renderApp(`/overview?department=${departmentId(DEP.purchasing)}&department=${departmentId(DEP.finance)}`);
    await overviewReady();
    const select = screen.getByRole('combobox', { name: 'Department' });
    await waitFor(() => expect(within(select).getByRole('option', { selected: true })).toHaveTextContent('Department: 2 selected'));
    await user.selectOptions(select, '');
    await expectUnfiltered(await totalOf(api), 'department');
  });

  it('the counterparty filter can be cleared from its own control', async () => {
    const user = userEvent.setup();
    const api = renderApp(`/overview?counterparty=${CP}`);
    await overviewReady();
    await user.click(await screen.findByRole('button', { name: 'Clear counterparty' }));
    await expectUnfiltered(await totalOf(api), 'counterparty');
  });

  it('removing a custom date range also resets the Dates control', async () => {
    const user = userEvent.setup();
    renderApp('/overview');
    await overviewReady();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Dates' }), 'custom');
    const from = await screen.findByLabelText('From');
    await user.type(from, '2027-01-01');
    await waitFor(() => expect(urlParams().get('from')).toBe('2027-01-01'));
    await user.click(within(crumbs()!).getByRole('button', { name: /^Remove / }));
    await waitFor(() => expect(urlParams().get('from')).toBeNull());
    expect(screen.getByRole('combobox', { name: 'Dates' })).toHaveValue('');
    expect(screen.queryByLabelText('From')).not.toBeInTheDocument();
  });
});

describe('Register and search: the same removal paths', () => {
  it('register: an entity can be set and set back to All, and a URL entity can be removed', async () => {
    const user = userEvent.setup();
    renderApp(`/contracts?entity=${DAFA}`);
    const select = await screen.findByRole('combobox', { name: 'Entity' });
    expect(select).toHaveValue(DAFA);
    await user.selectOptions(select, '');
    await waitFor(() => expect(urlParams().get('entity')).toBeNull());
    await act(() => new Promise((r) => setTimeout(r, 50)));
    expect(urlParams().get('entity')).toBeNull();
    expect(scopeSelect()).toHaveValue('');
    await user.selectOptions(select, DAFA);
    await waitFor(() => expect(urlParams().get('entity')).toBe(DAFA));
    await user.selectOptions(scopeSelect(), '');
    await waitFor(() => expect(urlParams().get('entity')).toBeNull());
    expect(select).toHaveValue('');
  });

  it('register: every filter from a link (entities, departments, tag, custom field) can be removed', async () => {
    const user = userEvent.setup();
    renderApp(`/contracts?entity=${DAFA}&entity=${entityId(ENT.group)}&department=${departmentId(DEP.purchasing)}&department=${departmentId(DEP.finance)}&tag=${TAG}&band=1to5m&cf.riskLevel=High`);
    const entity = await screen.findByRole('combobox', { name: 'Entity' });
    await waitFor(() => expect(within(entity).getByRole('option', { selected: true })).toHaveTextContent('Entity: 2 selected'));
    await user.selectOptions(entity, '');
    await waitFor(() => expect(urlParams().getAll('entity')).toEqual([]));
    const dept = screen.getByRole('combobox', { name: 'Department' });
    await waitFor(() => expect(within(dept).getByRole('option', { selected: true })).toHaveTextContent('Department: 2 selected'));
    await user.selectOptions(dept, '');
    await waitFor(() => expect(urlParams().getAll('department')).toEqual([]));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Tag' }), '');
    await waitFor(() => expect(urlParams().get('tag')).toBeNull());
    const more = screen.getByRole('navigation', { name: 'More filters' });
    // Chips are in a stable order: the value band comes before the custom field.
    await user.click(within(more).getAllByRole('button', { name: /^Remove / })[0]!);
    await waitFor(() => expect(urlParams().get('band')).toBeNull());
    await user.selectOptions(screen.getByRole('combobox', { name: 'Risk level' }), '');
    await waitFor(() => expect(urlParams().get('cf.riskLevel')).toBeNull());
    await act(() => new Promise((r) => setTimeout(r, 50)));
    expect([...urlParams().keys()]).toEqual([]);
  });

  it('search: the entity filter can be set back to All', async () => {
    const user = userEvent.setup();
    renderApp(`/search?q=supply&entity=${DAFA}`);
    const select = await screen.findByRole('combobox', { name: 'Entity' });
    expect(select).toHaveValue(DAFA);
    await user.selectOptions(select, '');
    await waitFor(() => expect(urlParams().get('entity')).toBeNull());
    await act(() => new Promise((r) => setTimeout(r, 50)));
    expect(urlParams().get('entity')).toBeNull();
    expect(urlParams().get('q')).toBe('supply');
  });
});
