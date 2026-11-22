/* eslint-disable react-refresh/only-export-components */
// Contract table columns by register column key, shared by the register, Overview drill-down,
// person and counterparty pages. Wide screens show more columns (see `defaultColumns`).
import { Link } from 'react-router-dom';
import type { ContractListItem, CustomFieldDef, Lookups, RegisterColumn, TagRef } from '../api/types';
import { Avatar, NoticePill, StatusDot, type Column } from '../components';
import { displayValue } from '../lib/customFields';
import { formatDate } from '../lib/dates';
import { formatMoneyCompact, statusLabel } from '../lib/format';
import { useMedia } from '../lib/media';

export const COLUMN_LABELS: Record<string, string> = {
  contract: 'Contract',
  contractNo: 'Contract no.',
  counterparty: 'Counterparty',
  type: 'Type',
  entity: 'Entity',
  department: 'Department',
  owner: 'Owner',
  delegate: 'Delegate',
  responsible: 'Responsible',
  notice: 'Notice',
  start: 'Start',
  end: 'End',
  annualValue: 'Annual value',
  totalValue: 'Total value',
  status: 'Status',
  tags: 'Tags',
  autoRenew: 'Renewal',
  changeOfControl: 'Change of control',
};

/** Sort field the API knows for a column, if any. */
const SORT_OF: Record<string, string> = {
  contract: 'title',
  contractNo: 'contractNo',
  counterparty: 'counterparty',
  type: 'type',
  entity: 'entity',
  department: 'department',
  owner: 'owner',
  notice: 'notice',
  start: 'start',
  end: 'end',
  annualValue: 'value',
  totalValue: 'totalValue',
  status: 'status',
};

/** How many columns fit, by screen width: more on 1920 and 2560 px screens. */
export function useColumnBudget(): 'sm' | 'md' | 'lg' | 'xl' {
  const xl = useMedia('(min-width: 2200px)');
  const lg = useMedia('(min-width: 1700px)');
  const md = useMedia('(min-width: 1001px)');
  return xl ? 'xl' : lg ? 'lg' : md ? 'md' : 'sm';
}

export function defaultColumns(budget: ReturnType<typeof useColumnBudget>): RegisterColumn[] {
  if (budget === 'xl') return ['contract', 'contractNo', 'counterparty', 'entity', 'department', 'owner', 'notice', 'end', 'annualValue', 'totalValue', 'tags', 'status'];
  if (budget === 'lg') return ['contract', 'entity', 'owner', 'notice', 'end', 'annualValue', 'tags', 'status'];
  if (budget === 'md') return ['contract', 'entity', 'owner', 'notice', 'status'];
  return ['contract', 'notice', 'status'];
}

export function TagList({ tags, max = 3 }: { tags: TagRef[] | undefined; max?: number }) {
  if (!tags?.length) return <span className="caption">–</span>;
  return (
    <span className="tag-list" title={tags.map((t) => t.name).join(', ')}>
      {tags.slice(0, max).map((t) => (
        <span key={t.id} className={`tag tag--${t.color}`}>
          {t.name}
        </span>
      ))}
      {tags.length > max ? <span className="caption">+{tags.length - max}</span> : null}
    </span>
  );
}

/** A person's name linking to their page ("Contracts of <person>"). */
export function PersonLink({ id, name, initials, avatar }: { id: string | null | undefined; name: string; initials?: string; avatar?: boolean }) {
  if (!id) return <span>{name}</span>;
  return (
    <Link to={`/people/${id}`} className="person-link" onClick={(e) => e.stopPropagation()} title={`Contracts of ${name}`}>
      {avatar && initials ? <Avatar initials={initials} size="sm" /> : null}
      <span className="cell-clip">{name}</span>
    </Link>
  );
}

export interface ColumnOptions {
  /** Renders the title cell (the register uses a preview button). */
  title?: (r: ContractListItem) => React.ReactNode;
  customFields?: CustomFieldDef[];
  lookups?: Pick<Lookups, 'users'>;
}

export function contractColumns(keys: RegisterColumn[], opts: ColumnOptions = {}): Column<ContractListItem>[] {
  const out: Column<ContractListItem>[] = [];
  for (const key of keys) {
    if (key.startsWith('cf.')) {
      const def = opts.customFields?.find((d) => `cf.${d.key}` === key);
      if (!def) continue;
      out.push({ key, header: def.label, width: '140px', render: (r) => <span className="cell-clip">{displayValue(def, r.customFields?.[def.key], opts.lookups, formatDate)}</span> });
      continue;
    }
    const header = COLUMN_LABELS[key] ?? key;
    const sortKey = SORT_OF[key];
    const clip = (text: string | null | undefined) => <span className="cell-clip" title={text ?? ''}>{text || '–'}</span>;
    let col: Column<ContractListItem> | null = null;
    switch (key) {
      case 'contract':
        col = {
          key,
          header,
          sortKey,
          width: keys.length > 10 ? '22%' : keys.length > 5 ? '28%' : '40%',
          render: (r) =>
            opts.title ? (
              opts.title(r)
            ) : (
              <div className="cell-title">
                <Link to={`/contracts/${r.id}`} className="cell-title__btn" title={r.title}>
                  {r.title}
                </Link>
                <span className="caption cell-title__meta" title={`${r.counterparty} · ${r.typeName}`}>
                  {r.counterparty} · {r.typeName}
                  <span className="only-sm"> · {statusLabel(r.status)}</span>
                </span>
              </div>
            ),
        };
        break;
      case 'contractNo':
        col = { key, header, sortKey, width: '118px', render: (r) => <span className="mono">{r.contractNo}</span> };
        break;
      case 'counterparty':
        col = { key, header, sortKey, width: '15%', render: (r) => (r.counterpartyId ? <Link className="cell-clip link-quiet" to={`/counterparties/${r.counterpartyId}`} onClick={(e) => e.stopPropagation()} title={r.counterparty}>{r.counterparty}</Link> : clip(r.counterparty)) };
        break;
      case 'type':
        col = { key, header, sortKey, width: '13%', render: (r) => clip(r.typeName) };
        break;
      case 'entity':
        col = { key, header, sortKey, className: 'col-hide-sm', width: '15%', render: (r) => clip(r.entityName) };
        break;
      case 'department':
        col = { key, header, sortKey, width: '12%', render: (r) => clip(r.departmentName) };
        break;
      case 'owner':
        col = { key, header, sortKey, className: 'col-hide-sm', width: '14%', render: (r) => <PersonLink id={r.ownerId} name={r.ownerName} initials={r.ownerInitials} avatar /> };
        break;
      case 'delegate':
        col = { key, header, width: '12%', render: (r) => clip(r.delegateName) };
        break;
      case 'responsible':
        col = { key, header, width: '12%', render: (r) => clip(r.responsibleName) };
        break;
      case 'notice':
        col = { key, header, sortKey, width: '136px', render: (r) => <NoticePill days={r.daysToNotice} /> };
        break;
      case 'start':
        col = { key, header, sortKey, width: '108px', render: (r) => formatDate(r.startDate) };
        break;
      case 'end':
        col = { key, header, sortKey, width: '108px', render: (r) => (r.endDate === null ? <span className="caption">Open-ended</span> : formatDate(r.endDate)) };
        break;
      case 'annualValue':
        col = { key, header, sortKey, width: '112px', className: 'num', render: (r) => formatMoneyCompact(r.annualValue, r.currency) };
        break;
      case 'totalValue':
        col = { key, header, sortKey, width: '112px', className: 'num', render: (r) => formatMoneyCompact(r.totalValue ?? null, r.currency) };
        break;
      case 'status':
        col = { key, header, sortKey, className: 'col-hide-sm', width: '120px', render: (r) => <StatusDot status={r.status} /> };
        break;
      case 'tags':
        col = { key, header, width: '16%', render: (r) => <TagList tags={r.tags} max={2} /> };
        break;
      case 'autoRenew':
        col = { key, header, width: '104px', render: (r) => (r.autoRenew ? 'Auto' : r.endDate === null ? 'Open-ended' : 'Fixed term') };
        break;
      case 'changeOfControl':
        col = { key, header, width: '96px', render: (r) => (r.changeOfControl === null || r.changeOfControl === undefined ? '–' : r.changeOfControl ? 'Yes' : 'No') };
        break;
    }
    if (col) out.push(col);
  }
  return out;
}
