// Register filtering, sorting and paging. Runs AFTER the access filter, on rows the user may see.
import type { ContractListItem, ContractQuery, ContractSortField, Id, Paged } from '../types';

/** A list item plus the ids the filters need. */
export interface RegisterRow {
  item: ContractListItem;
  entityId: Id;
  departmentId: Id;
  typeId: Id;
  /** Owner, delegate and responsible user ids. */
  peopleIds: Id[];
}

const STATUS_ORDER = ['Draft', 'InApproval', 'Approved', 'Active', 'NoticeGiven', 'Expired', 'Terminated'];

export function matchesView(row: RegisterRow, view: ContractQuery['view'], userId: Id): boolean {
  const { item } = row;
  switch (view ?? 'all') {
    case 'due90':
      return item.status === 'Active' && item.daysToNotice !== null && item.daysToNotice <= 90;
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

export function filterRows(rows: RegisterRow[], query: ContractQuery, userId: Id): RegisterRow[] {
  return rows.filter(
    (r) =>
      matchesView(r, query.view, userId) &&
      matchesText(r.item, query.q) &&
      (!query.entityId || r.entityId === query.entityId) &&
      (!query.departmentId || r.departmentId === query.departmentId) &&
      (!query.typeId || r.typeId === query.typeId) &&
      (!query.status || r.item.status === query.status),
  );
}

function compare(a: ContractListItem, b: ContractListItem, field: ContractSortField): number {
  switch (field) {
    case 'title':
      return a.title.localeCompare(b.title);
    case 'entity':
      return a.entityName.localeCompare(b.entityName);
    case 'owner':
      return a.ownerName.localeCompare(b.ownerName);
    case 'status':
      return STATUS_ORDER.indexOf(a.status) - STATUS_ORDER.indexOf(b.status);
    case 'value':
      return (a.annualValue ?? -1) - (b.annualValue ?? -1);
    case 'notice':
      return (a.daysToNotice ?? 0) - (b.daysToNotice ?? 0);
  }
}

/** Sorts by `sort` (default: nearest notice deadline first). Contracts without a deadline always go last when sorting by notice. */
export function sortRows(rows: RegisterRow[], sort: ContractQuery['sort']): RegisterRow[] {
  const raw = sort ?? 'notice';
  const desc = raw.startsWith('-');
  const field = (desc ? raw.slice(1) : raw) as ContractSortField;
  const dir = desc ? -1 : 1;
  return [...rows].sort((x, y) => {
    if (field === 'notice') {
      const xn = x.item.daysToNotice === null;
      const yn = y.item.daysToNotice === null;
      if (xn !== yn) return xn ? 1 : -1;
    }
    return dir * compare(x.item, y.item, field) || x.item.title.localeCompare(y.item.title);
  });
}

export function pageRows<T>(rows: T[], page = 1, pageSize = 25): Paged<T> {
  const size = Math.min(Math.max(1, Math.floor(pageSize)), 200);
  const p = Math.max(1, Math.floor(page));
  return { items: rows.slice((p - 1) * size, p * size), total: rows.length, page: p, pageSize: size };
}

export function queryRows(rows: RegisterRow[], query: ContractQuery, userId: Id): Paged<ContractListItem> {
  const sorted = sortRows(filterRows(rows, query, userId), query.sort);
  return pageRows(sorted.map((r) => r.item), query.page, query.pageSize);
}

function csvCell(value: string | number | null): string {
  if (value === null) return '';
  const s = String(value);
  return /[",\n\r;]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

export function toCsv(items: ContractListItem[]): string {
  const header = ['Contract no', 'Title', 'Counterparty', 'Type', 'Legal entity', 'Department', 'Owner', 'Status', 'Notice deadline', 'Days to notice', 'Annual value', 'Currency', 'Missing data'];
  const lines = items.map((i) =>
    [i.contractNo, i.title, i.counterparty, i.typeName, i.entityName, i.departmentName, i.ownerName, i.status, i.noticeDeadline, i.daysToNotice, i.annualValue, i.currency, i.missingData.join('; ')]
      .map(csvCell)
      .join(','),
  );
  return [header.join(','), ...lines].join('\r\n');
}
