import { describe, expect, it } from 'vitest';
import type { CustomFieldDef, Lookups, SavedView } from '../api/types';
import { displayValue, fieldsForType, pruneValues, validateCustomFields, validateStandardRules } from './customFields';
import { applyDrill, chipsOf, filterToParams, isEmptyFilter, monthRange, parseFilter, rangeLabel } from './drill';
import { highlightRanges, makeSnippet, matchesAll, segments, terms } from './search';
import { paramsFromView, queryFromParams, sanitizeColumns, viewInput, viewMatches } from './views';
import { emptyDraft, validateDraft } from './validation';

const lookups = {
  entities: [{ id: 'e1', name: 'DAFA A/S' }],
  departments: [{ id: 'd1', name: 'Purchasing' }],
  contractTypes: [{ id: 't1', name: 'NDA' }],
  users: [{ id: 'u1', name: 'Mette Kjær', initials: 'MK', departmentIds: [] }],
  currencies: ['DKK'],
} as Lookups;

describe('drill-down state in the URL', () => {
  it('round-trips repeatable, boolean, numeric and custom-field filters', () => {
    const filter = { entityId: ['e1', 'e2'], status: 'Active' as const, noticeWithinDays: 30, changeOfControl: true, cf: { riskLevel: 'High' }, dateField: 'notice' as const, from: '2027-01-01', to: '2027-01-31' };
    const params = filterToParams(filter, new URLSearchParams('sort=-value&page=3'));
    expect(params.getAll('entity')).toEqual(['e1', 'e2']);
    expect(params.get('sort')).toBe('-value');
    expect(params.get('cf.riskLevel')).toBe('High');
    expect(parseFilter(params)).toEqual(filter);
  });

  it('a drill-down replaces its key, keeps the rest, and removing a range drops its date field', () => {
    const f = applyDrill({ departmentId: 'd1', typeId: 't1' }, { departmentId: 'd2', from: '2027-01-01', dateField: 'end' });
    expect(f).toEqual({ departmentId: 'd2', typeId: 't1', from: '2027-01-01', dateField: 'end' });
    expect(applyDrill(f, { from: undefined })).toEqual({ departmentId: 'd2', typeId: 't1' });
    expect(isEmptyFilter({ includeEnded: false })).toBe(true);
  });

  it('builds a breadcrumb with readable labels; each chip removes only itself', () => {
    const f = { departmentId: 'd1', ownerId: 'u1', dateField: 'notice' as const, ...monthRange('2027-02'), missingData: true };
    const chips = chipsOf(f, lookups);
    expect(chips.map((c) => c.label)).toEqual(['Purchasing', 'Owner: Mette Kjær', 'Notice deadline in Feb 2027', 'Missing data']);
    expect(chips[0]?.without).toEqual({ ownerId: 'u1', dateField: 'notice', from: '2027-02-01', to: '2027-02-28', missingData: true });
    expect(chips[2]?.without).toEqual({ departmentId: 'd1', ownerId: 'u1', missingData: true });
  });

  it('removing any chip removes its URL parameters completely and never leaves an empty or placeholder value', () => {
    const f = parseFilter(
      new URLSearchParams('entity=e1&entity=e2&department=d1&department=d2&type=t1&status=Active&counterparty=c1&owner=u1&band=1to5m&tag=g1&dateField=end&from=2027-01-01&to=2027-12-31&notice=30&missing=true&cf.riskLevel=High&cf.soleSupplier=true&q=gasket&sort=-value'),
    );
    const chips = chipsOf(f, lookups);
    expect(chips).toHaveLength(16);
    for (const chip of chips) {
      const params = filterToParams(chip.without, new URLSearchParams('sort=-value'));
      for (const [k, v] of params) {
        expect(v, `${chip.id}: ${k}`).not.toBe('');
        expect(['undefined', 'null', 'NaN'], `${chip.id}: ${k}`).not.toContain(v);
      }
      // Exactly one fewer value (two/three for a date range), the rest untouched.
      expect(parseFilter(params)).toEqual(chip.without);
    }
    const all = chips.reduce((acc, c) => chipsOf(acc, lookups).find((x) => x.id === c.id)?.without ?? acc, f);
    expect(all).toEqual({});
    expect([...filterToParams(all, new URLSearchParams('sort=-value')).keys()]).toEqual(['sort']);
  });

  it('an emptied custom field is dropped from the filter, not kept as an empty value', () => {
    expect(applyDrill({ cf: { riskLevel: 'High', soleSupplier: 'true' } }, { cf: { riskLevel: '' } })).toEqual({ cf: { soleSupplier: 'true' } });
    expect(applyDrill({ cf: { riskLevel: 'High' } }, { cf: { riskLevel: '' } })).toEqual({});
    expect(filterToParams({ cf: { a: '' } }).toString()).toBe('');
  });

  it('labels quarters and open ranges', () => {
    expect(rangeLabel('end', '2027-04-01', '2027-06-30')).toBe('End date in Q2 2027');
    expect(rangeLabel('start', '2027-01-15', undefined)).toBe('Start date from 15 Jan 2027');
    expect(monthRange('2028-02')).toEqual({ from: '2028-02-01', to: '2028-02-29' });
  });
});

describe('search highlighting', () => {
  it('requires every term and highlights every occurrence', () => {
    expect(terms('  FSC  Packaging ')).toEqual(['fsc', 'packaging']);
    expect(matchesAll(['Packaging supply', 'FSC certified'], ['fsc', 'packaging'])).toBe(true);
    expect(matchesAll(['Packaging supply'], ['fsc', 'packaging'])).toBe(false);
    expect(highlightRanges('fsc and FSC', ['fsc'])).toEqual([[0, 3], [8, 11]]);
  });

  it('cuts a snippet around the first hit with ellipses and offsets relative to the snippet', () => {
    const text = `${'word '.repeat(80)}the FSC certified cardboard ${'tail '.repeat(80)}`;
    const s = makeSnippet(text, ['fsc'], 120);
    expect(s).not.toBeNull();
    expect(s?.text.startsWith('…')).toBe(true);
    expect(s?.text.endsWith('…')).toBe(true);
    const [a, b] = s?.highlights[0] ?? [0, 0];
    expect(s?.text.slice(a, b)).toBe('FSC');
    expect(segments('a FSC b', [[2, 5]])).toEqual([{ text: 'a ', hit: false }, { text: 'FSC', hit: true }, { text: ' b', hit: false }]);
  });
});

const field = (over: Partial<CustomFieldDef>): CustomFieldDef => ({ id: 'f', key: 'risk', label: 'Risk level', type: 'Choice', contractTypeIds: [], required: false, helpText: null, order: 0, options: ['Low', 'High'], showInRegister: false, showInFilters: false, isActive: true, ...over });

describe('custom fields', () => {
  const defs = [field({}), field({ id: 'g', key: 'dpa', label: 'DPA signed', type: 'YesNo', contractTypeIds: ['it'], required: true, order: 1 }), field({ id: 'h', key: 'old', label: 'Old', type: 'Text', isActive: false })];

  it('applies per contract type and only when active', () => {
    expect(fieldsForType(defs, 'it').map((d) => d.key)).toEqual(['risk', 'dpa']);
    expect(fieldsForType(defs, 'nda').map((d) => d.key)).toEqual(['risk']);
  });

  it('validates required values, types and fields that do not apply', () => {
    expect(validateCustomFields(defs, 'it', {}, true)).toEqual({ 'customFields.dpa': 'DPA signed is required' });
    expect(validateCustomFields(defs, 'it', {}, false)).toEqual({});
    expect(validateCustomFields(defs, 'nda', { risk: 'Extreme', dpa: true }, true)).toEqual({ 'customFields.risk': 'Choose one of the options', 'customFields.dpa': 'This field does not apply to the contract type' });
    expect(pruneValues(defs, 'nda', { risk: 'Low', dpa: true })).toEqual({ risk: 'Low' });
  });

  it('displays values for people', () => {
    expect(displayValue({ type: 'YesNo' }, false)).toBe('No');
    expect(displayValue({ type: 'MultiChoice' }, ['ISO 9001', 'REACH'])).toBe('ISO 9001, REACH');
    expect(displayValue({ type: 'Money' }, { amount: 1250000, currency: 'EUR' })).toBe('EUR 1,250,000');
    expect(displayValue({ type: 'Person' }, 'u1', lookups)).toBe('Mette Kjær');
    expect(displayValue({ type: 'Text' }, null)).toBe('–');
  });

  it('per-type field rules add required standard fields, and the form validation uses them', () => {
    const rules = { typeId: 'lease', required: ['endDate' as const, 'responsibleId' as const], defaults: { currency: null, noticePeriodDays: null, reminderRecipient: null, autoRenew: null, renewalMonths: null } };
    expect(validateStandardRules(rules, { openEnded: true, responsibleId: '' })).toEqual({ endDate: 'This contract type needs an end date', responsibleId: 'Responsible is required for this contract type' });
    const draft = emptyDraft({ typeId: 'it', title: 'X' });
    const errors = validateDraft(draft, [], { customFields: defs, fieldRules: { types: [] }, enforceRequired: true });
    expect(errors['customFields.dpa']).toBe('DPA signed is required');
  });
});

describe('saved views', () => {
  it('stores the register query and columns, and restores them as URL parameters', () => {
    const params = new URLSearchParams('view=due90&department=d1&band=5to20m&band=gte20m&sort=-value&page=2&sel=x');
    const input = viewInput(' Big renewals ', 'Personal', params, ['contract', 'annualValue'], true);
    expect(input).toEqual({ name: 'Big renewals', scope: 'Personal', columns: ['contract', 'annualValue'], isDefault: true, query: { departmentId: 'd1', valueBand: ['5to20m', 'gte20m'], view: 'due90', sort: '-value' } });
    const view = { id: 'v1', ...input } as unknown as SavedView;
    const back = paramsFromView(view);
    expect(back.get('view')).toBe('due90');
    expect(back.getAll('band')).toEqual(['5to20m', 'gte20m']);
    expect(back.get('cols')).toBe('contract,annualValue');
    expect(back.get('sv')).toBe('v1');
    expect(back.has('page')).toBe(false);
    expect(viewMatches(view, back, ['contract', 'annualValue'])).toBe(true);
    back.set('department', 'd2');
    expect(viewMatches(view, back, ['contract', 'annualValue'])).toBe(false);
  });

  it('keeps only known columns, the contract column first', () => {
    const cf = [field({ key: 'risk', showInRegister: true }), field({ id: 'z', key: 'hidden', showInRegister: false })];
    expect(sanitizeColumns(['status', 'nope', 'cf.risk', 'cf.hidden', 'status', 'contract'], cf)).toEqual(['contract', 'status', 'cf.risk']);
    expect(queryFromParams(new URLSearchParams('view=all'))).toEqual({});
  });
});
