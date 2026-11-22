// Register filtering, sorting and paging. Runs AFTER the access filter, on rows the user may see.
// The release 3 filter (docs/CONTRACT_V3.md section 2) is implemented in `matchesFilter`.
import { VALUE_BANDS, type ContractFilter, type ContractListItem, type ContractQuery, type ContractStatus, type CustomFieldDef, type CustomFieldValue, type DateField, type Id, type Paged, type ValueBand } from '../types';

/** A list item plus the ids and facts the filters need. */
export interface RegisterRow {
  item: ContractListItem;
  entityId: Id;
  departmentId: Id;
  typeId: Id;
  /** Owner, delegate and responsible user ids. */
  peopleIds: Id[];
  /** A renewal decision is recorded; such a contract is no longer "due" (`view=due90`). */
  decided?: boolean;
  // ----- release 3 (optional so older tests can build rows by hand) -----
  ownerId?: Id;
  supplierId?: Id | null;
  /** max(total, annual) in DKK. */
  valueDkk?: number;
  dates?: Partial<Record<DateField, string | null>>;
  tagIds?: Id[];
  watching?: boolean;
  customFields?: Record<string, CustomFieldValue>;
  /** Data-quality keys the contract is missing. */
  missingKeys?: string[];
  hasMainDoc?: boolean;
  exclusivity?: string | null;
  changeOfControl?: boolean | null;
  autoRenew?: boolean;
  // ----- release 4 (header filters) -----
  delegateId?: Id | null;
  responsibleId?: Id | null;
  currency?: string;
  annualDkk?: number;
  totalDkk?: number;
}

const STATUS_ORDER = ['Draft', 'InApproval', 'Approved', 'Active', 'NoticeGiven', 'Expired', 'Terminated'];
const ENDED: string[] = ['Expired', 'Terminated'];

export function matchesView(row: RegisterRow, view: ContractQuery['view'], userId: Id): boolean {
  const { item } = row;
  switch (view ?? 'all') {
    case 'due90':
      return item.status === 'Active' && !row.decided && item.daysToNotice !== null && item.daysToNotice <= 90;
    case 'approval':
      return item.status === 'InApproval';
    case 'mine':
      return row.peopleIds.includes(userId);
    case 'missing':
      return item.missingData.length > 0;
    default:
      return true;
  }
}

export function matchesText(item: ContractListItem, q: string | undefined): boolean {
  const needle = q?.trim().toLowerCase();
  if (!needle) return true;
  return [item.title, item.counterparty, item.contractNo, item.typeName, item.entityName, item.departmentName, item.ownerName]
    .some((v) => v.toLowerCase().includes(needle));
}

const list = <T,>(v: T | T[] | undefined): T[] => (v === undefined || v === null ? [] : Array.isArray(v) ? v : [v]);
const anyOf = <T,>(wanted: T | T[] | undefined, actual: T | null | undefined): boolean => {
  const w = list(wanted);
  return w.length === 0 || (actual !== null && actual !== undefined && w.includes(actual));
};

export function inBand(valueDkk: number, band: ValueBand): boolean {
  const b = VALUE_BANDS.find((x) => x.key === band);
  if (!b) return false;
  return (b.min === null || valueDkk >= b.min) && (b.max === null || valueDkk < b.max);
}

export const hasExclusivity = (text: string | null | undefined): boolean => !!text && text.trim() !== '' && text.trim().toLowerCase() !== 'none';

/** `cf.<key>=value` per the contract's rules for each field type. */
export function matchesCustom(value: CustomFieldValue | undefined, wanted: string, def?: Pick<CustomFieldDef, 'type'>): boolean {
  if (value === undefined || value === null) return false;
  const w = wanted.trim().toLowerCase();
  if (Array.isArray(value)) return value.some((v) => v.toLowerCase() === w);
  if (typeof value === 'boolean') return String(value) === w;
  if (typeof value === 'number') return value === Number(wanted);
  if (typeof value === 'object') return value.amount === Number(wanted);
  if (def?.type === 'Text') return value.toLowerCase().includes(w);
  return value.toLowerCase() === w;
}

export interface FilterContext {
  userId: Id;
  today: string;
  /** Default for `includeEnded` when the filter does not say (true for the register, false for analytics). */
  includeEndedDefault: boolean;
  customFields?: CustomFieldDef[];
}

/** One contract against the release 3 filter. Unknown ids simply match nothing. */
export function matchesFilter(row: RegisterRow, f: ContractFilter, ctx: FilterContext): boolean {
  const { item } = row;
  const status = item.status as ContractStatus;
  const statuses = list(f.status);
  if (statuses.length > 0) {
    if (!statuses.includes(status)) return false;
  } else if (!(f.includeEnded ?? ctx.includeEndedDefault) && ENDED.includes(status)) {
    return false;
  }
  if (!matchesText(item, f.q)) return false;
  if (f.id && f.id.length > 0 && !f.id.includes(item.id)) return false;
  if (!anyOf(f.entityId, row.entityId) || !anyOf(f.departmentId, row.departmentId) || !anyOf(f.typeId, row.typeId)) return false;
  if (!anyOf(f.counterpartyId, row.supplierId ?? null)) return false;
  if (!anyOf(f.ownerId, row.ownerId ?? null)) return false;
  if (f.personId && !row.peopleIds.includes(f.personId)) return false;
  const value = row.valueDkk ?? 0;
  const bands = list(f.valueBand);
  if (bands.length > 0 && !bands.some((b) => inBand(value, b))) return false;
  if (f.minValueDkk !== undefined && f.minValueDkk !== null && value < Number(f.minValueDkk)) return false;
  if (f.from || f.to) {
    const d = row.dates?.[f.dateField ?? 'end'] ?? null;
    if (!d) return false;
    if (f.from && d < f.from) return false;
    if (f.to && d > f.to) return false;
  }
  if (f.noticeWithinDays !== undefined && f.noticeWithinDays !== null) {
    const days = item.daysToNotice;
    if (status !== 'Active' || row.decided || days === null || days < 0 || days > Number(f.noticeWithinDays)) return false;
  }
  if (f.decided !== undefined && f.decided !== null && !!row.decided !== bool(f.decided)) return false;
  const tags = list(f.tagId);
  if (tags.length > 0 && !(row.tagIds ?? []).some((t) => tags.includes(t))) return false;
  if (f.changeOfControl !== undefined && f.changeOfControl !== null && row.changeOfControl !== bool(f.changeOfControl)) return false;
  if (f.exclusivity !== undefined && f.exclusivity !== null && hasExclusivity(row.exclusivity) !== bool(f.exclusivity)) return false;
  if (f.autoRenew !== undefined && f.autoRenew !== null && !!row.autoRenew !== bool(f.autoRenew)) return false;
  if (f.missingData !== undefined && f.missingData !== null && (item.missingData.length > 0) !== bool(f.missingData)) return false;
  if (f.missingField && !(row.missingKeys ?? []).includes(f.missingField)) return false;
  if (f.missingSignedDocument !== undefined && f.missingSignedDocument !== null) {
    const missing = (status === 'Active' || status === 'NoticeGiven') && !row.hasMainDoc;
    if (missing !== bool(f.missingSignedDocument)) return false;
  }
  if (f.watching !== undefined && f.watching !== null && !!row.watching !== bool(f.watching)) return false;
  for (const [rawKey, wantedRaw] of Object.entries(f.cf ?? {})) {
    const wanted = list(wantedRaw).filter((w) => w !== '' && w !== undefined && w !== null);
    if (wanted.length === 0) continue;
    // Release 4: `cf.<key>.min` / `.max` for Number, Money and Date fields.
    const bound = /\.(min|max)$/.exec(rawKey)?.[1];
    const key = bound ? rawKey.slice(0, -4) : rawKey;
    const def = ctx.customFields?.find((d) => d.key === key);
    const value = row.customFields?.[key];
    if (bound) {
      if (value === undefined || value === null) return false;
      const w = wanted[0] as string;
      const actual = typeof value === 'number' ? value : typeof value === 'object' && !Array.isArray(value) ? value.amount : typeof value === 'string' ? value : null;
      if (actual === null) return false;
      const cmp = typeof actual === 'number' ? actual - Number(w) : actual.localeCompare(w);
      if (bound === 'min' ? cmp < 0 : cmp > 0) return false;
      continue;
    }
    if (!wanted.some((w) => matchesCustom(value, w, def))) return false;
  }
  return matchesFilterV4(row, f);
}

const contains = (text: string | null | undefined, needle: string | undefined) => !needle?.trim() || (text ?? '').toLowerCase().includes(needle.trim().toLowerCase());
const num = (v: unknown): number | null => (v === undefined || v === null || v === '' || !Number.isFinite(Number(v)) ? null : Number(v));

/** Release 4 header filters (docs/CONTRACT_V4.md §2). */
function matchesFilterV4(row: RegisterRow, f: ContractFilter): boolean {
  const { item } = row;
  if (!contains(item.title, f.title) || !contains(item.contractNo, f.contractNo) || !contains(item.counterparty, f.counterpartyName)) return false;
  if (!anyOf(f.delegateId, row.delegateId ?? null) || !anyOf(f.responsibleId, row.responsibleId ?? null)) return false;
  if (!anyOf(f.currency, row.currency ?? item.currency)) return false;
  const ranges: [number | undefined, number | null, number | null][] = [
    [row.annualDkk, num(f.annualValueMin), num(f.annualValueMax)],
    [row.totalDkk, num(f.totalValueMin), num(f.totalValueMax)],
  ];
  for (const [v, min, max] of ranges) {
    if (min === null && max === null) continue;
    const x = v ?? 0;
    if ((min !== null && x < min) || (max !== null && x > max)) return false;
  }
  for (const field of ['notice', 'end', 'start', 'signed', 'created'] as const) {
    const from = f[`${field}From`];
    const to = f[`${field}To`];
    if (!from && !to) continue;
    const d = row.dates?.[field]?.slice(0, 10) ?? null;
    if (!d || (from && d < from) || (to && d > to)) return false;
  }
  return true;
}

/** Query values may arrive as strings ("true") from a URL. */
function bool(v: boolean | string): boolean {
  return v === true || v === 'true';
}

export function filterRows(rows: RegisterRow[], query: ContractQuery, userId: Id, ctx?: Partial<FilterContext>): RegisterRow[] {
  const full: FilterContext = { userId, today: ctx?.today ?? '', includeEndedDefault: ctx?.includeEndedDefault ?? true, customFields: ctx?.customFields };
  return rows.filter((r) => matchesView(r, query.view, userId) && matchesFilter(r, query, full));
}

type SortKey = string;

function compare(a: RegisterRow, b: RegisterRow, field: SortKey): number {
  const x = a.item;
  const y = b.item;
  switch (field) {
    case 'title':
      return x.title.localeCompare(y.title);
    case 'entity':
      return x.entityName.localeCompare(y.entityName);
    case 'owner':
      return x.ownerName.localeCompare(y.ownerName);
    case 'status':
      return STATUS_ORDER.indexOf(x.status) - STATUS_ORDER.indexOf(y.status);
    case 'value':
      return (x.annualValueDkk ?? x.annualValue ?? -1) - (y.annualValueDkk ?? y.annualValue ?? -1);
    case 'totalValue':
      return (x.totalValueDkk ?? -1) - (y.totalValueDkk ?? -1);
    case 'notice':
      return (x.daysToNotice ?? 0) - (y.daysToNotice ?? 0);
    case 'contractNo':
      return x.contractNo.localeCompare(y.contractNo);
    case 'counterparty':
      return x.counterparty.localeCompare(y.counterparty);
    case 'type':
      return x.typeName.localeCompare(y.typeName);
    case 'department':
      return x.departmentName.localeCompare(y.departmentName);
    case 'start':
      return (x.startDate ?? '').localeCompare(y.startDate ?? '');
    case 'end':
      return (x.endDate ?? '9999').localeCompare(y.endDate ?? '9999');
    case 'created':
      return (x.createdAt ?? '').localeCompare(y.createdAt ?? '');
    default:
      return 0;
  }
}

export const SORT_FIELDS = ['title', 'entity', 'owner', 'notice', 'status', 'value', 'contractNo', 'counterparty', 'type', 'department', 'start', 'end', 'totalValue', 'created'];

/** Sorts by `sort` (default: nearest notice deadline first). Contracts without a deadline always go last when sorting by notice. */
export function sortRows(rows: RegisterRow[], sort: ContractQuery['sort'] | string | undefined): RegisterRow[] {
  const raw = sort ?? 'notice';
  const desc = raw.startsWith('-');
  const field = desc ? raw.slice(1) : raw;
  const dir = desc ? -1 : 1;
  return [...rows].sort((x, y) => {
    if (field === 'notice') {
      const xn = x.item.daysToNotice === null;
      const yn = y.item.daysToNotice === null;
      if (xn !== yn) return xn ? 1 : -1;
    }
    return dir * compare(x, y, field) || x.item.title.localeCompare(y.item.title);
  });
}

export function pageRows<T>(rows: T[], page = 1, pageSize = 25): Paged<T> {
  const size = Math.min(Math.max(1, Math.floor(pageSize)), 200);
  const p = Math.max(1, Math.floor(page));
  return { items: rows.slice((p - 1) * size, p * size), total: rows.length, page: p, pageSize: size };
}

export function queryRows(rows: RegisterRow[], query: ContractQuery, userId: Id, ctx?: Partial<FilterContext>): Paged<ContractListItem> {
  const sorted = sortRows(filterRows(rows, query, userId, ctx), query.sort);
  return pageRows(sorted.map((r) => r.item), query.page, query.pageSize);
}

function csvCell(value: string | number | boolean | null | undefined): string {
  if (value === null || value === undefined) return '';
  let s = String(value);
  // Formula guard, as the API does.
  if (/^[=+\-@\t\r]/.test(s)) s = `'${s}`;
  return /[",\n\r;]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

const DEFAULT_HEADER = ['Contract no', 'Title', 'Counterparty', 'Type', 'Legal entity', 'Department', 'Owner', 'Status', 'Notice deadline', 'Days to notice', 'Annual value', 'Currency', 'Missing data'];

/** Export columns by register column key (`columns=`). */
const COLUMN_CSV: Record<string, { header: string[]; cells: (i: ContractListItem) => (string | number | boolean | null | undefined)[] }> = {
  contract: { header: ['Title'], cells: (i) => [i.title] },
  contractNo: { header: ['Contract no'], cells: (i) => [i.contractNo] },
  counterparty: { header: ['Counterparty'], cells: (i) => [i.counterparty] },
  type: { header: ['Type'], cells: (i) => [i.typeName] },
  entity: { header: ['Legal entity'], cells: (i) => [i.entityName] },
  department: { header: ['Department'], cells: (i) => [i.departmentName] },
  owner: { header: ['Owner'], cells: (i) => [i.ownerName] },
  delegate: { header: ['Delegate'], cells: (i) => [i.delegateName] },
  responsible: { header: ['Responsible'], cells: (i) => [i.responsibleName] },
  notice: { header: ['Notice deadline', 'Days to notice'], cells: (i) => [i.noticeDeadline, i.daysToNotice] },
  start: { header: ['Start date'], cells: (i) => [i.startDate] },
  end: { header: ['End date'], cells: (i) => [i.endDate ?? 'Open-ended'] },
  annualValue: { header: ['Annual value', 'Currency', 'Annual value DKK'], cells: (i) => [i.annualValue, i.currency, i.annualValueDkk] },
  totalValue: { header: ['Total value', 'Total value DKK'], cells: (i) => [i.totalValue, i.totalValueDkk] },
  status: { header: ['Status'], cells: (i) => [i.status] },
  tags: { header: ['Tags'], cells: (i) => [(i.tags ?? []).map((t) => t.name).join('; ')] },
  autoRenew: { header: ['Auto-renew'], cells: (i) => [i.autoRenew ? 'Yes' : 'No'] },
  changeOfControl: { header: ['Change of control'], cells: (i) => [i.changeOfControl === null || i.changeOfControl === undefined ? '' : i.changeOfControl ? 'Yes' : 'No'] },
};

export function customCell(v: CustomFieldValue | undefined): string {
  if (v === undefined || v === null) return '';
  if (Array.isArray(v)) return v.join('; ');
  if (typeof v === 'boolean') return v ? 'Yes' : 'No';
  if (typeof v === 'object') return `${v.currency} ${v.amount}`;
  return String(v);
}

export function toCsv(items: ContractListItem[], columns?: string[], customFields: CustomFieldDef[] = []): string {
  if (!columns || columns.length === 0) {
    const lines = items.map((i) =>
      [i.contractNo, i.title, i.counterparty, i.typeName, i.entityName, i.departmentName, i.ownerName, i.status, i.noticeDeadline, i.daysToNotice, i.annualValue, i.currency, i.missingData.join('; ')]
        .map(csvCell)
        .join(','),
    );
    return [DEFAULT_HEADER.join(','), ...lines].join('\r\n');
  }
  const header: string[] = [];
  const getters: ((i: ContractListItem) => (string | number | boolean | null | undefined)[])[] = [];
  for (const col of columns) {
    if (col.startsWith('cf.')) {
      const key = col.slice(3);
      header.push(customFields.find((d) => d.key === key)?.label ?? key);
      getters.push((i) => [customCell(i.customFields?.[key])]);
    } else if (COLUMN_CSV[col]) {
      header.push(...(COLUMN_CSV[col]?.header ?? []));
      getters.push(COLUMN_CSV[col]?.cells ?? (() => []));
    }
  }
  const lines = items.map((i) => getters.flatMap((g) => g(i)).map(csvCell).join(','));
  return [header.map(csvCell).join(','), ...lines].join('\r\n');
}
