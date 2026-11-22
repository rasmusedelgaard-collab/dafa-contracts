import { describe, expect, it } from 'vitest';
import { ApiError } from '../api';
import type { ContractInput, CustomFieldDef, Id, ImportRunInput } from '../types';
import { createMockApi, type MockApi } from './mockApi';
import { importFields, parseBool, parseCsv, parseDate, parseNumber, planImport, suggestMapping, type ImportRefs } from './importer';
import { matchesFilter, type RegisterRow } from './query';
import { DEP, ENT, TYP, USER, departmentId, entityId, typeId } from './seed';
const SUPPLIER_1 = '00000005-0000-4000-8000-000000000001';

const TODAY = '2026-11-22';
const as = (userId: Id): MockApi => createMockApi({ today: TODAY, userId });

async function status(p: Promise<unknown>): Promise<number> {
  try {
    await p;
    return 200;
  } catch (e) {
    if (e instanceof ApiError) return e.status;
    throw e;
  }
}
async function errorsOf(p: Promise<unknown>): Promise<Record<string, string[]>> {
  try {
    await p;
    return {};
  } catch (e) {
    if (e instanceof ApiError) return e.problem.errors ?? { _: [e.problem.detail ?? ''] };
    throw e;
  }
}
const idOf = (api: MockApi, title: string): Id => {
  const c = api.db.contracts.find((x) => x.title === title);
  if (!c) throw new Error(`No seeded contract "${title}"`);
  return c.id;
};

function contract(overrides: Partial<ContractInput> = {}): ContractInput {
  return {
    title: 'Gasket supply',
    typeId: typeId(TYP.direct),
    supplierId: SUPPLIER_1,
    counterpartyName: 'Test Supplier ApS',
    entityId: entityId(ENT.dafa),
    departmentId: departmentId(DEP.purchasing),
    ownerId: USER.jon,
    delegateId: null,
    responsibleId: null,
    reminderRecipient: 'Owner',
    startDate: '2027-01-01',
    endDate: '2028-12-31',
    noticePeriodDays: 90,
    autoRenew: false,
    renewalMonths: null,
    annualValue: 100_000,
    totalValue: null,
    currency: 'DKK',
    paymentTerms: null,
    priceAdjustment: null,
    priceAdjustmentDate: null,
    governingLaw: null,
    liabilityCap: null,
    exclusivity: null,
    changeOfControl: false,
    summary: null,
    ...overrides,
  };
}

const row = (over: Partial<RegisterRow> & { status?: string; days?: number | null } = {}): RegisterRow => ({
  item: {
    id: 'c1', contractNo: 'C-1', title: 'T', counterparty: 'X', typeName: 'Ty', entityName: 'E', departmentName: 'D', ownerName: 'O', ownerInitials: 'O',
    noticeDeadline: null, daysToNotice: over.days ?? null, annualValue: 0, currency: 'DKK', status: over.status ?? 'Active', missingData: [],
  },
  entityId: 'e1', departmentId: 'd1', typeId: 't1', peopleIds: ['u1'], valueDkk: 0, ...over,
});
const ctx = { userId: 'u1', today: TODAY, includeEndedDefault: false };

describe('drill-down filter (ContractFilter)', () => {
  it('ORs repeated values and ANDs different parameters', () => {
    const r = row({ entityId: 'e1', departmentId: 'd2' });
    expect(matchesFilter(r, { entityId: ['e1', 'e9'] }, ctx)).toBe(true);
    expect(matchesFilter(r, { entityId: ['e1'], departmentId: 'd1' }, ctx)).toBe(false);
    expect(matchesFilter(r, { entityId: 'unknown' }, ctx)).toBe(false);
  });

  it('value bands use max(total, annual) in DKK with min inclusive and max exclusive', () => {
    expect(matchesFilter(row({ valueDkk: 1_000_000 }), { valueBand: '1to5m' }, ctx)).toBe(true);
    expect(matchesFilter(row({ valueDkk: 1_000_000 }), { valueBand: 'lt1m' }, ctx)).toBe(false);
    expect(matchesFilter(row({ valueDkk: 25_000_000 }), { valueBand: ['5to20m', 'gte20m'] }, ctx)).toBe(true);
  });

  it('date ranges apply to the chosen date and never match a missing date', () => {
    const r = row({ dates: { end: '2027-03-31', notice: '2027-01-01' } });
    expect(matchesFilter(r, { dateField: 'end', from: '2027-01-01', to: '2027-03-31' }, ctx)).toBe(true);
    expect(matchesFilter(r, { dateField: 'notice', from: '2027-02-01' }, ctx)).toBe(false);
    expect(matchesFilter(row(), { dateField: 'end', from: '2020-01-01' }, ctx)).toBe(false);
  });

  it('ended contracts are left out of analytics unless asked for', () => {
    expect(matchesFilter(row({ status: 'Expired' }), {}, ctx)).toBe(false);
    expect(matchesFilter(row({ status: 'Expired' }), { includeEnded: true }, ctx)).toBe(true);
    expect(matchesFilter(row({ status: 'Expired' }), { status: 'Expired' }, ctx)).toBe(true);
    expect(matchesFilter(row({ status: 'Expired' }), {}, { ...ctx, includeEndedDefault: true })).toBe(true);
  });

  it('notice-within, flags and custom fields', () => {
    expect(matchesFilter(row({ days: 20 }), { noticeWithinDays: 30 }, ctx)).toBe(true);
    expect(matchesFilter(row({ days: 20, decided: true }), { noticeWithinDays: 30 }, ctx)).toBe(false);
    expect(matchesFilter(row({ days: -3 }), { noticeWithinDays: 30 }, ctx)).toBe(false);
    expect(matchesFilter(row({ exclusivity: 'None' }), { exclusivity: true }, ctx)).toBe(false);
    expect(matchesFilter(row({ exclusivity: 'Nordics' }), { exclusivity: true }, ctx)).toBe(true);
    expect(matchesFilter(row({ hasMainDoc: false }), { missingSignedDocument: true }, ctx)).toBe(true);
    expect(matchesFilter(row({ status: 'Draft', hasMainDoc: false }), { missingSignedDocument: true }, ctx)).toBe(false);
    expect(matchesFilter(row({ customFields: { riskLevel: 'High' } }), { cf: { riskLevel: 'high' } }, ctx)).toBe(true);
    expect(matchesFilter(row({ customFields: { certs: ['ISO 9001', 'REACH'] } }), { cf: { certs: 'REACH' } }, ctx)).toBe(true);
    expect(matchesFilter(row({ customFields: { sole: true } }), { cf: { sole: 'false' } }, ctx)).toBe(false);
  });
});

describe('overview analytics in the mock', () => {
  it('is open to every user and scoped: a department member sees less than group management', async () => {
    const cfo = await as(USER.ras).getOverviewAnalytics({});
    const quality = await as(USER.emm).getOverviewAnalytics({});
    expect(quality.kpis.count).toBeGreaterThan(0);
    expect(quality.kpis.count).toBeLessThan(cfo.kpis.count);
    expect(quality.kpis.annualValueDkk).toBeLessThan(cfo.kpis.annualValueDkk);
    expect(cfo.scope.label).toBe('All contracts');
    expect(quality.scope.label).toBe('Quality');
  });

  it('a drill-down bucket key used as the filter gives the bucket count, and the register agrees', async () => {
    const api = as(USER.ras);
    const page = await api.getOverviewAnalytics({});
    const top = page.byEntity[0];
    if (!top) throw new Error('no buckets');
    const drilled = await api.getOverviewAnalytics({ entityId: top.key });
    expect(drilled.kpis.count).toBe(top.count);
    expect(drilled.kpis.annualValueDkk).toBe(top.annualValueDkk);
    const list = await api.getContracts({ entityId: top.key, includeEnded: false, pageSize: 200 });
    expect(list.total).toBe(top.count);
  });

  it('has 24 notice months, 8 quarters, 7 statuses and due-diligence lists', async () => {
    const a = await as(USER.ras).getOverviewAnalytics({});
    expect(a.noticeByMonth).toHaveLength(24);
    expect(a.noticeByMonth[0]?.month).toBe('2026-11');
    expect(a.expiringByQuarter).toHaveLength(8);
    expect(a.expiringByQuarter[0]).toMatchObject({ quarter: '2026-Q4', from: '2026-10-01', to: '2026-12-31' });
    expect(a.statusFunnel.map((b) => b.key)).toEqual(['Draft', 'InApproval', 'Approved', 'Active', 'NoticeGiven', 'Expired', 'Terminated']);
    expect(a.dueDiligence.changeOfControl.length).toBeGreaterThan(0);
    expect(a.dueDiligence.missingSignedDocument.some((x) => x.title === 'Legal services framework')).toBe(true);
    expect(a.topCounterparties.length).toBeLessThanOrEqual(10);
    expect(a.top10SharePct).toBeGreaterThan(0);
    const sum = a.noticeByMonth.reduce((s, m) => s + m.high + m.medium + m.low + m.decided, 0);
    expect(sum).toBeGreaterThan(0);
  });

  it('notice KPIs match the noticeWithinDays drill-down', async () => {
    const api = as(USER.ras);
    const a = await api.getOverviewAnalytics({});
    const d = await api.getOverviewAnalytics({ noticeWithinDays: 90 });
    expect(d.kpis.count).toBe(a.kpis.notice90);
  });
});

describe('search', () => {
  it('finds a document of another owner inside the user’s access, with a highlighted snippet', async () => {
    // Jonas (Purchasing) is not on "Packaging supply" (owner Camilla) but sees it through his department.
    const r = await as(USER.jon).searchFull({ q: 'FSC certified' });
    const hit = r.documents.items.find((d) => d.contractTitle === 'Packaging supply');
    expect(hit).toBeDefined();
    expect(hit?.ownerName).toBe('Camilla Frost');
    expect(hit?.matchedIn).toContain('text');
    const snip = hit?.snippet;
    expect(snip).not.toBeNull();
    const [s, e] = snip?.highlights[0] ?? [0, 0];
    expect(snip?.text.slice(s, e).toLowerCase()).toBe('fsc');
  });

  it('never returns documents or contracts outside the user’s access', async () => {
    // Emma (Quality) cannot see the Purchasing packaging contract.
    const r = await as(USER.emm).searchFull({ q: 'FSC certified' });
    expect(r.documents.total).toBe(0);
    const c = await as(USER.emm).searchFull({ q: 'Packaging supply' });
    expect(c.contracts.items.some((x) => x.item.title === 'Packaging supply')).toBe(false);
  });

  it('groups results and requires all terms', async () => {
    const r = await as(USER.ras).searchFull({ q: 'Mette' });
    expect(r.people.items.some((p) => p.name === 'Mette Kjær')).toBe(true);
    expect(r.contracts.items.some((x) => x.matchedIn.includes('owner'))).toBe(true);
    const none = await as(USER.ras).searchFull({ q: 'Mette zzzzqq' });
    expect(none.contracts.total + none.documents.total + none.people.total).toBe(0);
    const obligations = await as(USER.ras).searchFull({ q: 'covenant', group: 'contracts' });
    expect(obligations.contracts.items[0]?.matchedIn).toContain('obligation');
    expect(obligations.contracts.items[0]?.snippet?.text.toLowerCase()).toContain('covenant');
  });

  it('person page lists contracts in any role, only those the caller can see', async () => {
    const mette = await as(USER.ras).getPerson(USER.met);
    expect(mette.counts.total).toBe(mette.contracts.length);
    expect(mette.contracts.every((c) => c.roles.length > 0)).toBe(true);
    const viaSverige = await as(USER.ann).getPerson(USER.met);
    expect(viaSverige.contracts.length).toBeLessThan(mette.contracts.length);
  });

  it('counterparty page is scoped', async () => {
    const api = as(USER.ras);
    const nordpolymer = api.db.suppliers.find((s) => s.name === 'Nordpolymer Compounds ApS');
    if (!nordpolymer) throw new Error('seed');
    const page = await api.getCounterparty(nordpolymer.id);
    expect(page.summary.contracts).toBeGreaterThanOrEqual(3);
    expect(page.documents.length).toBeGreaterThan(0);
    const other = await as(USER.ann).getCounterparty(nordpolymer.id);
    expect(other.contracts).toHaveLength(0);
  });
});

describe('imports', () => {
  it('parses CSV with ; and quotes, booleans, numbers and dates', () => {
    expect(parseCsv('Name;VAT\r\n"Hansen; Co";DK1\r\n')).toEqual([['Name', 'VAT'], ['Hansen; Co', 'DK1']]);
    expect(parseBool('ja')).toBe(true);
    expect(parseBool('maybe')).toBeUndefined();
    expect(parseNumber('1.250.000,50', ',')).toBe(1_250_000.5);
    expect(parseNumber('1,250,000.50', '.')).toBe(1_250_000.5);
    expect(parseDate('31.12.2027')).toBe('2027-12-31');
    expect(parseDate('31/02/2027')).toBeUndefined();
    expect(parseDate('12/31/2027', 'MM/dd/yyyy')).toBe('2027-12-31');
  });

  it('suggests a mapping from headers and labels', () => {
    const m = suggestMapping(['Vendor name', 'VAT number', 'External ID', 'Country'], importFields('counterparties'));
    expect(m.vatNo).toBe('VAT number');
    expect(m.externalId).toBe('External ID');
    expect(m.country).toBe('Country');
    expect(m.name).toBeNull();
  });

  it('validates rows per mode and reports errors per row', () => {
    const refs = { entities: [], departments: [], types: [], users: [], contracts: [], currencies: ['DKK'], tags: [], customFields: [], fieldRules: { types: [] }, today: TODAY, counterparties: [{ id: 'x', name: 'Nordic Seals ApS', vatNo: 'DK1', externalId: null, isActive: true }] } as ImportRefs;
    const cols = ['Name', 'VAT', 'Country'];
    const rows = [['Nordic Seals', 'DK1', 'DK'], ['New One A/S', '', 'Danmark'], ['', '', ''], ['New One A/S', '', 'DK']];
    const input = { name: 'Name', vatNo: 'VAT', country: 'Country' };
    const skip = planImport('counterparties', cols, rows, input, { mode: 'SkipDuplicates' }, refs).plans.map((p) => p.preview.action);
    expect(skip).toEqual(['Skip', 'Error', 'Error', 'Error']);
    const strict = planImport('counterparties', cols, rows, input, { mode: 'CreateOnly' }, refs).plans;
    expect(strict[0]?.preview.errors[0]?.message).toContain('Already exists');
    expect(strict[1]?.preview.errors[0]?.message).toContain('two-letter');
    const missing = planImport('counterparties', cols, rows, { vatNo: 'VAT' }, { mode: 'CreateOnly' }, refs);
    expect(missing.mappingErrors[0]).toContain('Name');
  });

  it('admin imports a supplier CSV end to end; others may not', async () => {
    const api = as(USER.pet);
    const before = api.db.suppliers.length;
    const csv = 'Name,VAT number,Country,External id\nVejle Gummi ApS,DK99887766,DK,V20001\nNordpolymer Compounds ApS,,DK,V10040\n';
    const session = await api.admin.uploadImport('counterparties', new File([csv], 'vendors.csv', { type: 'text/csv' }));
    expect(session.rowCount).toBe(2);
    const input: ImportRunInput = { mapping: session.suggestedMapping, options: { mode: 'SkipDuplicates' } };
    const preview = await api.admin.previewImport(session.id, input);
    expect(preview.create).toBe(1);
    expect(preview.skip).toBe(1);
    const result = await api.admin.runImport(session.id, input);
    expect(result.created).toBe(1);
    expect(api.db.suppliers.length).toBe(before + 1);
    expect(await status(api.admin.runImport(session.id, input))).toBe(409);
    expect(await status(as(USER.ras).admin.uploadImport('counterparties', new File([csv], 'v.csv')))).toBe(403);
  });

  it('bulk-imports contracts as Active records with warnings for missing data', async () => {
    const api = as(USER.pet);
    const csv = [
      'Title;Counterparty;Contract type;Legal entity;Department;Owner;Start date;End date;Notice period (days);Annual value',
      'Legacy hose supply;Nordpolymer Compounds ApS;Supplier – direct materials;DAFA A/S;Purchasing;jonas.bak@dafa.example;01-01-2024;31-12-2027;;120000',
      'Unknown type row;Nordpolymer Compounds ApS;Gadgets;DAFA A/S;Purchasing;jonas.bak@dafa.example;01-01-2024;;;',
    ].join('\n');
    const s = await api.admin.uploadImport('contracts', new File([csv], 'register.csv'));
    const input: ImportRunInput = { mapping: s.suggestedMapping, options: { mode: 'CreateOnly', contractStatus: 'Active' } };
    const p = await api.admin.previewImport(s.id, input);
    expect(p.rows[0]?.action).toBe('Create');
    expect(p.rows[0]?.warnings.join(' ')).toContain('notice period');
    expect(p.rows[1]?.action).toBe('Error');
    await api.admin.runImport(s.id, input);
    const c = api.db.contracts.find((x) => x.title === 'Legacy hose supply');
    expect(c?.status).toBe('Active');
    expect(c?.noticePeriodDays).toBeNull();
    const item = (await api.getContracts({ q: 'Legacy hose' })).items[0];
    expect(item?.missingData).toContain('Notice period');
  });
});

describe('custom fields', () => {
  const field: Omit<CustomFieldDef, 'id' | 'order'> = { key: 'insuranceRef', label: 'Insurance reference', type: 'Text', contractTypeIds: [typeId(TYP.direct)], required: true, helpText: null, options: [], showInRegister: true, showInFilters: false, isActive: true };

  it('an admin-defined required field is enforced on create and stored on the contract', async () => {
    const admin = as(USER.pet);
    await admin.admin.createCustomField({ ...field, order: 0 });
    const cfg = await admin.getAppConfig();
    expect(cfg.customFields.some((d) => d.key === 'insuranceRef')).toBe(true);
    const errors = await errorsOf(admin.createContract(contract()));
    expect(errors['customFields.insuranceRef']?.[0]).toContain('required');
    const created = await admin.createContract(contract({ customFields: { insuranceRef: 'POL-77' } }));
    expect(created.customFields?.insuranceRef).toBe('POL-77');
    // Does not apply to another type, so it is neither required nor accepted there.
    const nda = await errorsOf(admin.createContract(contract({ typeId: typeId(TYP.nda), customFields: { insuranceRef: 'x' } })));
    expect(nda['customFields.insuranceRef']?.[0]).toContain('does not apply');
  });

  it('checks value types and protects fields in use', async () => {
    const api = as(USER.pet);
    const errors = await errorsOf(api.createContract(contract({ customFields: { riskLevel: 'Extreme' } })));
    expect(errors['customFields.riskLevel']).toBeDefined();
    const risk = api.db.customFields.find((d) => d.key === 'riskLevel');
    if (!risk) throw new Error('seed');
    expect(await status(api.admin.deleteCustomField(risk.id))).toBe(409);
    expect(await status(api.admin.updateCustomField(risk.id, { ...risk, options: ['Low', 'Medium'] }))).toBe(409);
    expect(await status(api.admin.createCustomField({ ...field, key: 'Bad key', order: 0 }))).toBe(400);
  });

  it('per-type field rules make standard fields required', async () => {
    const api = as(USER.pet);
    const errors = await errorsOf(api.createContract(contract({ typeId: typeId(TYP.lease), endDate: null })));
    expect(errors.endDate?.[0]).toContain('end date');
  });
});

describe('saved views, collaboration and settings', () => {
  it('personal views are private; shared views are visible to all and only editable by owner or admin', async () => {
    const jon = as(USER.jon);
    const mine = await jon.createView({ name: 'My drafts', scope: 'Personal', columns: ['contract', 'status'], query: { status: 'Draft' }, isDefault: true });
    expect(mine.canEdit).toBe(true);
    const views = await jon.getViews();
    expect(views.some((v) => v.name === 'Purchasing – renewals this year')).toBe(true);
    const shared = views.find((v) => v.scope === 'Shared');
    expect(shared?.canEdit).toBe(false);
    if (!shared) throw new Error('seed');
    expect(await status(jon.updateView(shared.id, { ...shared, name: 'Mine now' }))).toBe(403);
    const emm = as(USER.emm);
    emm.db.views.push(...jon.db.views.filter((v) => v.id === mine.id));
    expect((await emm.getViews()).some((v) => v.id === mine.id)).toBe(false);
    expect(await status(emm.createView({ name: 'Shared by reader', scope: 'Shared', columns: ['contract'], query: {}, isDefault: false }))).toBe(403);
    expect(await status(jon.createView({ name: 'Bad', scope: 'Personal', columns: ['nope' as 'contract'], query: {}, isDefault: false }))).toBe(400);
  });

  it('comments notify mentioned users who can see the contract and refuse those who cannot', async () => {
    const jon = as(USER.jon);
    const id = idOf(jon, 'Raw material supply – rubber compounds');
    const c = await jon.addComment(id, { body: '@Camilla Frost can you check the price list?', mentionIds: [USER.cam] });
    expect(c.mentions[0]?.name).toBe('Camilla Frost');
    expect(jon.db.notifications.some((n) => n.userId === USER.cam && n.event === 'Mention')).toBe(true);
    expect(await status(jon.addComment(id, { body: '@Emma', mentionIds: [USER.ann] }))).toBe(400);
  });

  it('watching, bulk tagging and the notification centre', async () => {
    const met = as(USER.met);
    const id = idOf(met, 'Adhesive supply agreement');
    await met.watchContract(id);
    expect((await met.getWatching()).some((x) => x.id === id)).toBe(true);
    const jon = as(USER.jon);
    jon.db.watchers.push({ contractId: id, userId: USER.met });
    const tag = jon.db.tags[0];
    if (!tag) throw new Error('seed');
    const res = await jon.bulk({ action: 'AddTag', contractIds: [id, idOf(jon, 'Energy supply')], tagId: tag.id });
    expect(res.succeeded).toBe(1);
    expect(res.failed[0]?.reason).toMatch(/access|cannot change/);
    const before = (await met.getUnreadCount()).count;
    expect(before).toBeGreaterThan(0);
    await met.markAllNotificationsRead();
    expect((await met.getUnreadCount()).count).toBe(0);
  });

  it('version comparison shows the seeded changes on the flagship contract', async () => {
    const api = as(USER.ras);
    const id = idOf(api, 'Raw material supply – rubber compounds');
    const versions = await api.getContractVersions(id);
    expect(versions[0]?.id).toBe('current');
    expect(versions[versions.length - 1]?.id).toBe('created');
    const cmp = await api.compareContract(id, 'created', 'current');
    const annual = cmp.fields.find((f) => f.field === 'annualValue');
    expect(annual).toMatchObject({ from: '7900000', to: '8400000', changed: true });
    expect(cmp.fields.find((f) => f.field === 'title')?.changed).toBe(false);
  });

  it('data room export is limited to management and admins and contains an index', async () => {
    expect(await status(as(USER.jon).createDataRoomExport({ name: 'DD', filter: {}, documents: 'All', versions: 'Latest' }))).toBe(403);
    const api = as(USER.ann);
    const ex = await api.createDataRoomExport({ name: 'Sverige DD', filter: { changeOfControl: true }, documents: 'MainOnly', versions: 'Latest' });
    await new Promise((r) => setTimeout(r, 20));
    const ready = await api.getDataRoomExport(ex.id);
    expect(ready.status).toBe('Ready');
    const file = await api.getDataRoomContent(ex.id);
    expect(file.fileName).toMatch(/\.zip$/);
    expect(file.blob.size).toBeGreaterThan(100);
  });

  it('effective access explains each contract and bulk reassign previews before moving', async () => {
    const api = as(USER.pet);
    const ea = await api.admin.getEffectiveAccess(USER.ann, { pageSize: 200 });
    expect(ea.rules.some((r) => r.rule === 'EntityManagement')).toBe(true);
    expect(ea.contracts.every((c) => c.reasons.length > 0)).toBe(true);
    const input = { fromUserId: USER.cam, toUserId: USER.jon, roles: ['Owner' as const, 'Delegate' as const, 'Responsible' as const], contractIds: null, includeEnded: false, includeObligations: true };
    const preview = await api.admin.previewReassign(input);
    expect(preview.contracts).toBeGreaterThan(0);
    const res = await api.admin.reassign(input);
    expect(res.updated).toBe(preview.contracts);
    expect(api.db.contracts.some((c) => c.ownerId === USER.cam && c.status === 'Active')).toBe(false);
  });

  it('every admin route refuses non-admins', async () => {
    const api = as(USER.ras);
    for (const p of [api.admin.getCounterparties(), api.admin.getUsers(), api.admin.getCustomFields(), api.admin.getJobs(), api.admin.getAppSettings(), api.admin.getNotificationSettings(), api.admin.getSyncSources()]) {
      expect(await status(p)).toBe(403);
    }
  });
});
