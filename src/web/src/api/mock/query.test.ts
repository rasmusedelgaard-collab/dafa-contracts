import { describe, expect, it } from 'vitest';
import type { ContractListItem } from '../types';
import { createMockApi } from './mockApi';
import { filterRows, pageRows, queryRows, sortRows, toCsv, type RegisterRow } from './query';
import { DEP, ENT, TYP, USER, departmentId, entityId, typeId } from './seed';

function row(over: Partial<ContractListItem> & { title: string }, ids: Partial<Omit<RegisterRow, 'item'>> = {}): RegisterRow {
  return {
    item: {
      id: over.title,
      contractNo: 'C-2026-0001',
      counterparty: 'Supplier',
      typeName: 'Logistics',
      entityName: 'DAFA A/S',
      departmentName: 'Purchasing',
      ownerName: 'Owner',
      ownerInitials: 'OW',
      noticeDeadline: null,
      daysToNotice: null,
      annualValue: null,
      currency: 'DKK',
      status: 'Active',
      missingData: [],
      ...over,
    },
    entityId: 'e1',
    departmentId: 'd1',
    typeId: 't1',
    peopleIds: [],
    ...ids,
  };
}

const rows: RegisterRow[] = [
  row({ title: 'Alpha', daysToNotice: 23, annualValue: 500, ownerName: 'Zed' }, { peopleIds: ['me'] }),
  row({ title: 'Bravo', daysToNotice: 90, annualValue: 100, counterparty: 'Nordpolymer' }, { entityId: 'e2' }),
  row({ title: 'Charlie', daysToNotice: 91, status: 'Active' }, { departmentId: 'd2' }),
  row({ title: 'Delta', daysToNotice: 10, status: 'InApproval' }, { typeId: 't2', peopleIds: ['me', 'you'] }),
  row({ title: 'Echo', daysToNotice: null, missingData: ['Notice period'] }),
  row({ title: 'Foxtrot', daysToNotice: -4, status: 'Draft', contractNo: 'C-2025-0777' }),
];
const titles = (r: RegisterRow[]) => r.map((x) => x.item.title);

describe('register views', () => {
  it('all shows everything', () => {
    expect(filterRows(rows, {}, 'me')).toHaveLength(6);
    expect(filterRows(rows, { view: 'all' }, 'me')).toHaveLength(6);
  });
  it('due90 is active contracts with 90 days or less to the notice deadline, including day 90', () => {
    expect(titles(filterRows(rows, { view: 'due90' }, 'me'))).toEqual(['Alpha', 'Bravo']);
  });
  it('approval is contracts in approval', () => {
    expect(titles(filterRows(rows, { view: 'approval' }, 'me'))).toEqual(['Delta']);
  });
  it('mine is contracts where the user is owner, delegate or responsible', () => {
    expect(titles(filterRows(rows, { view: 'mine' }, 'me'))).toEqual(['Alpha', 'Delta']);
    expect(titles(filterRows(rows, { view: 'mine' }, 'you'))).toEqual(['Delta']);
  });
  it('missing is contracts with missing data', () => {
    expect(titles(filterRows(rows, { view: 'missing' }, 'me'))).toEqual(['Echo']);
  });
});

describe('register filters', () => {
  it('filters by entity, department, type and status, and combines them', () => {
    expect(titles(filterRows(rows, { entityId: 'e2' }, 'me'))).toEqual(['Bravo']);
    expect(titles(filterRows(rows, { departmentId: 'd2' }, 'me'))).toEqual(['Charlie']);
    expect(titles(filterRows(rows, { typeId: 't2' }, 'me'))).toEqual(['Delta']);
    expect(titles(filterRows(rows, { status: 'Draft' }, 'me'))).toEqual(['Foxtrot']);
    expect(filterRows(rows, { entityId: 'e2', status: 'Draft' }, 'me')).toEqual([]);
    expect(titles(filterRows(rows, { view: 'mine', typeId: 't2' }, 'me'))).toEqual(['Delta']);
  });
  it('searches title, counterparty and contract number without case', () => {
    expect(titles(filterRows(rows, { q: 'nordPOLY' }, 'me'))).toEqual(['Bravo']);
    expect(titles(filterRows(rows, { q: ' char ' }, 'me'))).toEqual(['Charlie']);
    expect(titles(filterRows(rows, { q: '0777' }, 'me'))).toEqual(['Foxtrot']);
    expect(filterRows(rows, { q: 'no such thing' }, 'me')).toEqual([]);
  });
});

describe('register sorting and paging', () => {
  it('sorts by nearest notice deadline by default, with no-deadline contracts last', () => {
    expect(titles(sortRows(rows, undefined))).toEqual(['Foxtrot', 'Delta', 'Alpha', 'Bravo', 'Charlie', 'Echo']);
  });
  it('keeps no-deadline contracts last when the notice sort is reversed', () => {
    expect(titles(sortRows(rows, '-notice'))).toEqual(['Charlie', 'Bravo', 'Alpha', 'Delta', 'Foxtrot', 'Echo']);
  });
  it('sorts by title, owner, status and value in both directions', () => {
    expect(titles(sortRows(rows, '-title'))[0]).toBe('Foxtrot');
    expect(titles(sortRows(rows, '-owner'))[0]).toBe('Alpha');
    expect(titles(sortRows(rows, 'status')).slice(0, 2)).toEqual(['Foxtrot', 'Delta']);
    expect(titles(sortRows(rows, '-value')).slice(0, 2)).toEqual(['Alpha', 'Bravo']);
  });
  it('pages and reports the total', () => {
    const page2 = queryRows(rows, { sort: 'title', page: 2, pageSize: 4 }, 'me');
    expect(page2).toMatchObject({ total: 6, page: 2, pageSize: 4 });
    expect(page2.items.map((i) => i.title)).toEqual(['Echo', 'Foxtrot']);
    expect(pageRows(rows, 9, 4).items).toEqual([]);
    expect(pageRows(rows, 0, 0)).toMatchObject({ page: 1, pageSize: 1 });
  });
});

describe('CSV export', () => {
  it('quotes commas and quotes, and writes one line per contract', () => {
    const csv = toCsv([row({ title: 'Lease, "north" hall', daysToNotice: 5, noticeDeadline: '2026-11-27', missingData: ['Notice period', 'Contract value'] }).item]);
    const lines = csv.split('\r\n');
    expect(lines).toHaveLength(2);
    expect(lines[0]).toMatch(/^Contract no,Title,Counterparty/);
    expect(lines[1]).toContain('"Lease, ""north"" hall"');
    expect(lines[1]).toContain('2026-11-27,5');
    expect(lines[1]).toContain('Notice period; Contract value');
  });
});

describe('register against the seeded mock', () => {
  const api = createMockApi({ today: '2026-11-22', userId: USER.ras });

  it('combines view, filters, search, sort and paging through the API', async () => {
    const due = await api.getContracts({ view: 'due90', pageSize: 100 });
    expect(due.total).toBeGreaterThan(5);
    expect(due.items.every((i) => i.status === 'Active' && i.daysToNotice !== null && i.daysToNotice <= 90)).toBe(true);
    const days = due.items.map((i) => i.daysToNotice ?? 0);
    expect(days).toEqual([...days].sort((a, b) => a - b));

    const purchasing = await api.getContracts({ departmentId: departmentId(DEP.purchasing), entityId: entityId(ENT.dafa), typeId: typeId(TYP.direct), pageSize: 100 });
    expect(purchasing.items.length).toBeGreaterThan(2);
    expect(purchasing.items.every((i) => i.departmentName === 'Purchasing' && i.entityName === 'DAFA A/S' && i.typeName === 'Supplier – direct materials')).toBe(true);

    const search = await api.getContracts({ q: 'nordpolymer' });
    expect(search.items.map((i) => i.title).sort()).toEqual(['Amendment 1 – volume commitment', 'NDA – Nordpolymer', 'Quality agreement – Nordpolymer', 'Raw material supply – rubber compounds']);

    const p1 = await api.getContracts({ sort: 'title', page: 1, pageSize: 10 });
    const p2 = await api.getContracts({ sort: 'title', page: 2, pageSize: 10 });
    expect(p1.items).toHaveLength(10);
    expect(p1.total).toBe(api.db.contracts.length);
    expect(p1.items.some((a) => p2.items.some((b) => b.id === a.id))).toBe(false);

    expect((await api.getContracts({ view: 'approval' })).total).toBe(3);
    expect((await api.getContracts({ view: 'missing' })).items.map((i) => i.entityName)).toEqual(['DAFA Building Solutions A/S', 'DAFA Building Solutions A/S', 'DAFA Building Solutions A/S']);
    expect((await api.getContracts({ status: 'Expired' })).total).toBe(2);
  });

  it('exports the filtered register, not just the current page', async () => {
    const file = await api.exportContracts({ view: 'approval' });
    expect(file.fileName).toBe('dafa-contracts-2026-11-22.csv');
    const lines = (await file.blob.text()).trim().split('\r\n');
    expect(lines).toHaveLength(4);
  });
});
