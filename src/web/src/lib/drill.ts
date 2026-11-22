// Overview drill-down state: the ContractFilter lives in the URL so a drill-down can be shared,
// reloaded and undone with the browser's back button. Shared by Overview and the register.
import { VALUE_BANDS, type ContractFilter, type ContractStatus, type DateField, type Lookups, type ValueBand } from '../api/types';
import { formatDate, monthShort } from './dates';
import { statusLabel } from './format';

/** URL parameter -> filter key. Repeatable ones may appear several times. */
const PARAMS = {
  entity: 'entityId',
  department: 'departmentId',
  type: 'typeId',
  status: 'status',
  counterparty: 'counterpartyId',
  owner: 'ownerId',
  person: 'personId',
  band: 'valueBand',
  tag: 'tagId',
  dateField: 'dateField',
  from: 'from',
  to: 'to',
  notice: 'noticeWithinDays',
  decided: 'decided',
  missing: 'missingData',
  missingField: 'missingField',
  autoRenew: 'autoRenew',
  coc: 'changeOfControl',
  excl: 'exclusivity',
  nosigned: 'missingSignedDocument',
  minValue: 'minValueDkk',
  ended: 'includeEnded',
  q: 'q',
} as const satisfies Record<string, keyof ContractFilter>;
type ParamName = keyof typeof PARAMS;
const REPEATABLE = new Set<string>(['entityId', 'departmentId', 'typeId', 'status', 'counterpartyId', 'ownerId', 'valueBand', 'tagId']);
const BOOLEAN = new Set<string>(['decided', 'missingData', 'autoRenew', 'changeOfControl', 'exclusivity', 'missingSignedDocument', 'includeEnded']);
const NUMBER = new Set<string>(['noticeWithinDays', 'minValueDkk']);

const one = <T,>(v: T | T[] | undefined): T[] => (v === undefined ? [] : Array.isArray(v) ? v : [v]);

/** Reads the filter from URL parameters. Unknown parameters are ignored. */
export function parseFilter(params: URLSearchParams): ContractFilter {
  const f: Record<string, unknown> = {};
  for (const [name, key] of Object.entries(PARAMS) as [ParamName, keyof ContractFilter][]) {
    const values = params.getAll(name).filter((v) => v !== '');
    if (values.length === 0) continue;
    if (REPEATABLE.has(key)) f[key] = values.length === 1 ? values[0] : values;
    else if (BOOLEAN.has(key)) f[key] = values[0] === 'true';
    else if (NUMBER.has(key)) {
      const n = Number(values[0]);
      if (Number.isFinite(n)) f[key] = n;
    } else f[key] = values[0];
  }
  const cf: Record<string, string> = {};
  for (const [k, v] of params.entries()) if (k.startsWith('cf.') && v) cf[k.slice(3)] = v;
  if (Object.keys(cf).length) f.cf = cf;
  return f as ContractFilter;
}

/** Writes the filter into a copy of `base`, replacing every filter parameter. */
export function filterToParams(filter: ContractFilter, base: URLSearchParams = new URLSearchParams()): URLSearchParams {
  const next = new URLSearchParams(base);
  for (const name of Object.keys(PARAMS)) next.delete(name);
  for (const k of [...next.keys()]) if (k.startsWith('cf.')) next.delete(k);
  for (const [name, key] of Object.entries(PARAMS) as [ParamName, keyof ContractFilter][]) {
    const v = filter[key];
    if (v === undefined || v === null || v === '') continue;
    for (const x of one(v as unknown)) if (x !== undefined && x !== null && x !== '') next.append(name, String(x));
  }
  for (const [k, v] of Object.entries(filter.cf ?? {})) if (v) next.set(`cf.${k}`, v);
  return next;
}

/** Applies a drill-down: the patch replaces those keys; `undefined` removes a key. */
export function applyDrill(filter: ContractFilter, patch: Partial<ContractFilter>): ContractFilter {
  const next: Record<string, unknown> = { ...filter };
  for (const [k, v] of Object.entries(patch)) {
    if (v === undefined || v === null || v === '' || (Array.isArray(v) && v.length === 0)) delete next[k];
    else if (k === 'cf') next.cf = { ...(filter.cf ?? {}), ...(v as Record<string, string>) };
    else next[k] = v;
  }
  // An emptied custom field is removed, not sent as `cf.<key>=` (the API would filter on an empty value).
  if (next.cf) {
    const cf = Object.fromEntries(Object.entries(next.cf as Record<string, string | undefined>).filter(([, x]) => x !== undefined && x !== null && x !== ''));
    if (Object.keys(cf).length) next.cf = cf;
    else delete next.cf;
  }
  // A date range belongs to its date field.
  if (!next.from && !next.to) delete next.dateField;
  return next as ContractFilter;
}

export function isEmptyFilter(filter: ContractFilter): boolean {
  return Object.keys(filter).filter((k) => k !== 'includeEnded').length === 0;
}

export interface Chip {
  id: string;
  label: string;
  /** The filter without this chip. */
  without: ContractFilter;
}

const DATE_FIELD_LABEL: Record<DateField, string> = { notice: 'Notice deadline', end: 'End date', start: 'Start date', created: 'Created', signed: 'Signed' };

/** Readable label for a date range: a whole month or quarter gets a short name. */
export function rangeLabel(field: DateField | undefined, from?: string, to?: string): string {
  const name = DATE_FIELD_LABEL[field ?? 'end'];
  if (from && to && from.slice(0, 7) === to.slice(0, 7) && from.endsWith('-01')) return `${name} in ${monthShort(from.slice(0, 7))} ${from.slice(0, 4)}`;
  if (from && to) {
    const q = /^(\d{4})-(01|04|07|10)-01$/.exec(from);
    const qEnd = q ? { '01': '03-31', '04': '06-30', '07': '09-30', '10': '12-31' }[q[2] as '01'] : null;
    if (q && to === `${q[1]}-${qEnd}`) return `${name} in Q${(Number(q[2]) + 2) / 3} ${q[1]}`;
    return `${name} ${formatDate(from)} – ${formatDate(to)}`;
  }
  if (from) return `${name} from ${formatDate(from)}`;
  return `${name} until ${formatDate(to)}`;
}

/**
 * The breadcrumb of applied filters, in a stable order, each with "remove". Names come from
 * the lookups; counterparty and custom-field labels can be passed in.
 */
export function chipsOf(
  filter: ContractFilter,
  lookups: Pick<Lookups, 'entities' | 'departments' | 'contractTypes' | 'users'>,
  extra: { counterparties?: Record<string, string>; tags?: Record<string, string>; customFields?: Record<string, string> } = {},
): Chip[] {
  const chips: Chip[] = [];
  const name = (list: { id: string; name: string }[], id: string) => list.find((x) => x.id === id)?.name ?? id;
  const many = (key: 'entityId' | 'departmentId' | 'typeId' | 'status' | 'counterpartyId' | 'ownerId' | 'valueBand' | 'tagId', label: (v: string) => string) => {
    for (const v of one(filter[key] as string | string[] | undefined)) {
      const rest = one(filter[key] as string | string[] | undefined).filter((x) => x !== v);
      chips.push({ id: `${key}:${v}`, label: label(v), without: applyDrill(filter, { [key]: rest.length === 0 ? undefined : rest.length === 1 ? rest[0] : rest }) });
    }
  };
  many('entityId', (v) => name(lookups.entities, v));
  many('departmentId', (v) => name(lookups.departments, v));
  many('typeId', (v) => name(lookups.contractTypes, v));
  many('status', (v) => statusLabel(v as ContractStatus));
  many('counterpartyId', (v) => extra.counterparties?.[v] ?? 'Counterparty');
  many('ownerId', (v) => `Owner: ${name(lookups.users, v)}`);
  many('valueBand', (v) => VALUE_BANDS.find((b) => b.key === (v as ValueBand))?.label ?? v);
  many('tagId', (v) => `Tag: ${extra.tags?.[v] ?? v}`);
  const single = (key: keyof ContractFilter, label: string) => chips.push({ id: key, label, without: applyDrill(filter, { [key]: undefined }) });
  if (filter.personId) single('personId', `Person: ${name(lookups.users, filter.personId)}`);
  if (filter.from || filter.to) chips.push({ id: 'range', label: rangeLabel(filter.dateField, filter.from, filter.to), without: applyDrill(filter, { from: undefined, to: undefined, dateField: undefined }) });
  if (filter.noticeWithinDays !== undefined) single('noticeWithinDays', `Notice within ${filter.noticeWithinDays} days`);
  if (filter.decided !== undefined) single('decided', filter.decided ? 'Decision recorded' : 'No decision yet');
  if (filter.missingData !== undefined) single('missingData', filter.missingData ? 'Missing data' : 'Complete data');
  if (filter.missingField) single('missingField', `Missing: ${filter.missingField}`);
  if (filter.autoRenew !== undefined) single('autoRenew', filter.autoRenew ? 'Auto-renewing' : 'Fixed term');
  if (filter.changeOfControl !== undefined) single('changeOfControl', filter.changeOfControl ? 'Change of control' : 'No change of control');
  if (filter.exclusivity !== undefined) single('exclusivity', filter.exclusivity ? 'Exclusivity' : 'No exclusivity');
  if (filter.missingSignedDocument !== undefined) single('missingSignedDocument', 'No signed document');
  if (filter.minValueDkk !== undefined) single('minValueDkk', `Value from DKK ${new Intl.NumberFormat('en-US').format(filter.minValueDkk)}`);
  if (filter.q) single('q', `“${filter.q}”`);
  for (const [k, v] of Object.entries(filter.cf ?? {})) {
    chips.push({ id: `cf.${k}`, label: `${extra.customFields?.[k] ?? k}: ${v === 'true' ? 'Yes' : v === 'false' ? 'No' : v}`, without: applyDrill({ ...filter, cf: Object.fromEntries(Object.entries(filter.cf ?? {}).filter(([x]) => x !== k)) }, {}) });
  }
  return chips;
}

/** First and last day of a `yyyy-MM` month. */
export function monthRange(month: string): { from: string; to: string } {
  const [y, m] = month.split('-').map(Number) as [number, number];
  const last = new Date(Date.UTC(y, m, 0)).getUTCDate();
  return { from: `${month}-01`, to: `${month}-${String(last).padStart(2, '0')}` };
}
