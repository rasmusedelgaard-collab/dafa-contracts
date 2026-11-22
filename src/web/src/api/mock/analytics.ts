// Overview analytics (docs/CONTRACT_V3.md section 5), computed over the base set: visible
// contracts that match the filter. Pure, so it can be tested without the mock.
import { addDays, addMonths, daysBetween } from '../../lib/dates';
import type { Bucket, ContractStatus, DueDiligenceItem, ExpiryQuarter, NoticeMonth, OverviewAnalytics } from '../types';
import { hasExclusivity, type RegisterRow } from './query';

export interface AnalyticsInput {
  rows: RegisterRow[];
  today: string;
  visibleContracts: number;
  scopeLabel: string;
  thresholdDkk: number;
  asOf: string;
}

const STATUSES: ContractStatus[] = ['Draft', 'InApproval', 'Approved', 'Active', 'NoticeGiven', 'Expired', 'Terminated'];
const STATUS_LABEL: Record<ContractStatus, string> = {
  Draft: 'Draft',
  InApproval: 'In approval',
  Approved: 'Approved',
  Active: 'Active',
  NoticeGiven: 'Notice given',
  Expired: 'Expired',
  Terminated: 'Terminated',
};

const annual = (r: RegisterRow) => r.item.annualValueDkk ?? 0;
const total = (r: RegisterRow) => r.item.totalValueDkk ?? 0;
const pct = (part: number, whole: number) => (whole > 0 ? Math.round((part / whole) * 1000) / 10 : 0);

export function buckets(rows: RegisterRow[], key: (r: RegisterRow) => string, label: (r: RegisterRow) => string, grandAnnual: number): Bucket[] {
  const map = new Map<string, Bucket>();
  for (const r of rows) {
    const k = key(r);
    const b = map.get(k) ?? { key: k, label: label(r), count: 0, annualValueDkk: 0, totalValueDkk: 0, sharePct: 0 };
    b.count += 1;
    b.annualValueDkk += annual(r);
    b.totalValueDkk += total(r);
    map.set(k, b);
  }
  return [...map.values()]
    .map((b) => ({ ...b, annualValueDkk: Math.round(b.annualValueDkk), totalValueDkk: Math.round(b.totalValueDkk), sharePct: pct(b.annualValueDkk, grandAnnual) }))
    .sort((a, b) => b.annualValueDkk - a.annualValueDkk || b.count - a.count || a.label.localeCompare(b.label));
}

export function quarterOf(iso: string): string {
  return `${iso.slice(0, 4)}-Q${Math.floor((Number(iso.slice(5, 7)) - 1) / 3) + 1}`;
}

function quarterStart(iso: string): string {
  const m = Math.floor((Number(iso.slice(5, 7)) - 1) / 3) * 3 + 1;
  return `${iso.slice(0, 4)}-${String(m).padStart(2, '0')}-01`;
}

function ddItem(r: RegisterRow, note: string | null): DueDiligenceItem {
  const i = r.item;
  return { id: i.id, contractNo: i.contractNo, title: i.title, counterparty: i.counterparty, entityName: i.entityName, status: i.status as ContractStatus, annualValueDkk: annual(r), totalValueDkk: total(r), note };
}

const ddSort = (a: DueDiligenceItem, b: DueDiligenceItem) => b.totalValueDkk - a.totalValueDkk || b.annualValueDkk - a.annualValueDkk || a.title.localeCompare(b.title);

export function computeAnalytics(input: AnalyticsInput): OverviewAnalytics {
  const { rows, today } = input;
  const grandAnnual = rows.reduce((s, r) => s + annual(r), 0);
  const grandTotal = rows.reduce((s, r) => s + total(r), 0);
  const undecidedActive = (r: RegisterRow) => r.item.status === 'Active' && !r.decided;
  const within = (r: RegisterRow, n: number) => undecidedActive(r) && r.item.daysToNotice !== null && r.item.daysToNotice >= 0 && r.item.daysToNotice <= n;
  const autoRenewCount = rows.filter((r) => r.autoRenew).length;

  const counterpartyKey = (r: RegisterRow) => r.supplierId ?? `name:${r.item.counterparty.toLowerCase()}`;
  const top = buckets(rows, counterpartyKey, (r) => r.item.counterparty, grandAnnual)
    .slice(0, 10)
    .map((b) => (b.key.startsWith('name:') ? { ...b, key: '' } : b));

  // Notice deadlines by month, 24 months from the current month.
  const firstMonth = `${today.slice(0, 7)}-01`;
  const months: NoticeMonth[] = Array.from({ length: 24 }, (_, i) => ({ month: addMonths(firstMonth, i).slice(0, 7), high: 0, medium: 0, low: 0, decided: 0, annualValueDkk: 0 }));
  for (const r of rows) {
    if (r.item.status !== 'Active' || !r.item.noticeDeadline) continue;
    const deadline = r.item.noticeDeadline;
    const days = daysBetween(today, deadline);
    let m: NoticeMonth | undefined;
    if (deadline < today) {
      // Overdue and undecided while the contract is still running: shown in the current month.
      if (r.decided || (r.item.endDate ?? '') < today) continue;
      m = months[0];
    } else {
      m = months.find((x) => x.month === deadline.slice(0, 7));
    }
    if (!m) continue;
    if (r.decided) m.decided += 1;
    else if (days <= 30) m.high += 1;
    else if (days <= 90) m.medium += 1;
    else m.low += 1;
    m.annualValueDkk += annual(r);
  }
  for (const m of months) m.annualValueDkk = Math.round(m.annualValueDkk);

  // Contracts ending by quarter, 8 quarters from the current one.
  const q0 = quarterStart(today);
  const quarters: ExpiryQuarter[] = Array.from({ length: 8 }, (_, i) => {
    const from = addMonths(q0, i * 3);
    return { quarter: quarterOf(from), from, to: addDays(addMonths(from, 3), -1), count: 0, autoRenew: 0, annualValueDkk: 0 };
  });
  for (const r of rows) {
    const end = r.item.endDate;
    if (!end) continue;
    const q = quarters.find((x) => end >= x.from && end <= x.to);
    if (!q) continue;
    q.count += 1;
    if (r.autoRenew) q.autoRenew += 1;
    q.annualValueDkk += annual(r);
  }
  for (const q of quarters) q.annualValueDkk = Math.round(q.annualValueDkk);

  const funnel: Bucket[] = STATUSES.map((s) => {
    const inStatus = rows.filter((r) => r.item.status === s);
    const a = inStatus.reduce((x, r) => x + annual(r), 0);
    return { key: s, label: STATUS_LABEL[s], count: inStatus.length, annualValueDkk: Math.round(a), totalValueDkk: Math.round(inStatus.reduce((x, r) => x + total(r), 0)), sharePct: pct(a, grandAnnual) };
  });

  const byEntity = new Map<string, { name: string; rows: RegisterRow[] }>();
  for (const r of rows) {
    const e = byEntity.get(r.entityId) ?? { name: r.item.entityName, rows: [] };
    e.rows.push(r);
    byEntity.set(r.entityId, e);
  }
  const dataQuality = [...byEntity.entries()]
    .map(([entityId, e]) => {
      const complete = e.rows.filter((r) => r.item.missingData.length === 0).length;
      const missing: Record<string, number> = {};
      for (const r of e.rows) for (const k of r.missingKeys ?? []) missing[k] = (missing[k] ?? 0) + 1;
      return { entityId, name: e.name, contracts: e.rows.length, complete, score: e.rows.length ? Math.round((complete / e.rows.length) * 100) : 100, missing };
    })
    .sort((a, b) => a.score - b.score || a.name.localeCompare(b.name));

  const live = (r: RegisterRow) => r.item.status === 'Active' || r.item.status === 'NoticeGiven';
  return {
    asOf: input.asOf,
    scope: { label: input.scopeLabel, visibleContracts: input.visibleContracts },
    kpis: {
      count: rows.length,
      annualValueDkk: Math.round(grandAnnual),
      totalValueDkk: Math.round(grandTotal),
      notice30: rows.filter((r) => within(r, 30)).length,
      notice90: rows.filter((r) => within(r, 90)).length,
      inApproval: rows.filter((r) => r.item.status === 'InApproval').length,
      missingData: rows.filter((r) => r.item.missingData.length > 0).length,
      autoRenewCount,
      autoRenewSharePct: pct(autoRenewCount, rows.length),
    },
    byEntity: buckets(rows, (r) => r.entityId, (r) => r.item.entityName, grandAnnual),
    byDepartment: buckets(rows, (r) => r.departmentId, (r) => r.item.departmentName, grandAnnual),
    byType: buckets(rows, (r) => r.typeId, (r) => r.item.typeName, grandAnnual),
    topCounterparties: top,
    top10SharePct: pct(top.reduce((s, b) => s + b.annualValueDkk, 0), grandAnnual),
    noticeByMonth: months,
    expiringByQuarter: quarters,
    statusFunnel: funnel,
    dataQuality,
    dueDiligence: {
      thresholdDkk: input.thresholdDkk,
      changeOfControl: rows.filter((r) => r.changeOfControl === true).map((r) => ddItem(r, null)).sort(ddSort),
      exclusivity: rows.filter((r) => hasExclusivity(r.exclusivity)).map((r) => ddItem(r, r.exclusivity ?? null)).sort(ddSort),
      aboveThreshold: rows.filter((r) => (r.valueDkk ?? 0) >= input.thresholdDkk).map((r) => ddItem(r, null)).sort(ddSort),
      missingSignedDocument: rows.filter((r) => live(r) && !r.hasMainDoc).map((r) => ddItem(r, 'No main document')).sort(ddSort),
    },
  };
}
