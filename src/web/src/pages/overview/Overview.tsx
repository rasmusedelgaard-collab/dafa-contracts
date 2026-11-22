import { useMemo, useState } from 'react';
import { keepPreviousData, useMutation, useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { apiMode } from '../../api';
import { qk, useApi, useAppConfig, useLookups, useMe, permissions } from '../../app/context';
import { VALUE_BANDS, type Bucket, type ContractFilter, type ContractStatus, type DateField, type DueDiligenceItem, type OverviewAnalytics } from '../../api/types';
import { Button, Card, ChipSelect, EmptyState, ErrorState, Field, Icon, Segmented, Skeleton, SkeletonRows, StatusDot, Table, TextInput, useToast } from '../../components';
import { BarList, Columns, dkk, KpiTile, ScoreList, StackedColumns, type StackColumn } from '../../features/charts';
import { contractColumns, defaultColumns, useColumnBudget } from '../../features/columns';
import { DataRoomDialog } from '../../features/DataRoom';
import { addDays, addMonths, monthShort, todayIso } from '../../lib/dates';
import { applyDrill, chipsOf, filterToParams, isEmptyFilter, monthRange, parseFilter } from '../../lib/drill';
import { ALL_STATUSES, formatMoney, plural, saveBlob, statusLabel } from '../../lib/format';
import { CounterpartyPicker, PersonPicker } from '../../features/Pickers';

const PAGE_SIZE = 20;
const URGENCY = [
  { key: 'high', label: 'Within 30 days', className: 'seg-high' },
  { key: 'medium', label: '31–90 days', className: 'seg-medium' },
  { key: 'low', label: 'Later', className: 'seg-low' },
  { key: 'decided', label: 'Decision recorded', className: 'seg-done' },
] as const;
const MONTHS_LONG = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

type DdTab = 'changeOfControl' | 'exclusivity' | 'aboveThreshold' | 'missingSignedDocument';
const DD_TABS: { value: DdTab; label: string }[] = [
  { value: 'changeOfControl', label: 'Change of control' },
  { value: 'exclusivity', label: 'Exclusivity' },
  { value: 'aboveThreshold', label: 'Above threshold' },
  { value: 'missingSignedDocument', label: 'No signed document' },
];

const DATE_PRESETS: { value: string; label: string; field: DateField; range: (t: string) => { from?: string; to?: string } }[] = [
  { value: 'notice90', label: 'Notice deadline in the next 90 days', field: 'notice', range: (t) => ({ from: t, to: addDays(t, 90) }) },
  { value: 'notice12', label: 'Notice deadline in the next 12 months', field: 'notice', range: (t) => ({ from: t, to: addMonths(t, 12) }) },
  { value: 'end12', label: 'Ending in the next 12 months', field: 'end', range: (t) => ({ from: t, to: addMonths(t, 12) }) },
  { value: 'start12', label: 'Started in the last 12 months', field: 'start', range: (t) => ({ from: addMonths(t, -12), to: t }) },
  { value: 'custom', label: 'Custom range…', field: 'end', range: () => ({}) },
];

function shareText(b: Bucket) {
  return `${plural(b.count, 'contract')} · ${b.sharePct}% of the annual value`;
}

function ChartCard({ title, sub, children, wide, id }: { title: string; sub?: string; children: React.ReactNode; wide?: boolean; id?: string }) {
  return (
    <Card pad className={`chart-card${wide ? ' chart-card--wide' : ''}`} aria-labelledby={id}>
      <div className="chart-card__head">
        <h2 className="card-title" id={id}>
          {title}
        </h2>
        {sub ? <span className="caption">{sub}</span> : null}
      </div>
      {children}
    </Card>
  );
}

function DueDiligence({ data, filter, drill, threshold, setThreshold }: { data: OverviewAnalytics; filter: ContractFilter; drill: (p: Partial<ContractFilter>) => void; threshold: number; setThreshold: (n: number) => void }) {
  const [tab, setTab] = useState<DdTab>('changeOfControl');
  const [text, setText] = useState(String(threshold / 1_000_000));
  const rows = data.dueDiligence[tab];
  const drillPatch: Record<DdTab, Partial<ContractFilter>> = {
    changeOfControl: { changeOfControl: true },
    exclusivity: { exclusivity: true },
    aboveThreshold: { minValueDkk: threshold },
    missingSignedDocument: { missingSignedDocument: true },
  };
  const counts = DD_TABS.map((t) => ({ ...t, label: `${t.label} (${data.dueDiligence[t.value].length})` }));
  return (
    <Card clip className="dd-card" aria-labelledby="dd-title">
      <div className="card-list-head dd-card__head">
        <div className="stack" style={{ gap: 4 }}>
          <h2 className="card-title" id="dd-title">
            Due diligence
          </h2>
          <span className="caption">Contracts a buyer will ask about, within the current selection</span>
        </div>
        <div className="spacer" />
        <Segmented ariaLabel="Due diligence list" mode="tabs" idBase="dd" size="sm" options={counts} value={tab} onChange={setTab} />
      </div>
      {tab === 'aboveThreshold' ? (
        <form
          className="row-flex dd-card__threshold"
          onSubmit={(e) => {
            e.preventDefault();
            const n = Number(text.replace(',', '.'));
            if (Number.isFinite(n) && n >= 0) setThreshold(Math.round(n * 1_000_000));
          }}
        >
          <Field label="Threshold, DKK million" hint="Contract value: the larger of total and annual value.">
            {(p) => <TextInput {...p} inputMode="decimal" value={text} onChange={(e) => setText(e.target.value)} style={{ width: 120 }} />}
          </Field>
          <Button type="submit" variant="secondary" size="md">
            Apply
          </Button>
        </form>
      ) : null}
      <div role="tabpanel" id={`dd-panel-${tab}`} aria-labelledby={`dd-tab-${tab}`}>
        {rows.length === 0 ? (
          <EmptyState compact title="None in this selection" />
        ) : (
          <Table<DueDiligenceItem>
            caption="Due diligence"
            fixed
            rows={rows.slice(0, 50)}
            rowKey={(r) => r.id}
            columns={[
              { key: 't', header: 'Contract', width: '34%', render: (r) => <Link to={`/contracts/${r.id}`} className="cell-clip" title={r.title}>{r.title}</Link> },
              { key: 'cp', header: 'Counterparty', width: '20%', render: (r) => <span className="cell-clip" title={r.counterparty}>{r.counterparty}</span> },
              { key: 'e', header: 'Entity', className: 'col-hide-sm', width: '18%', render: (r) => <span className="cell-clip">{r.entityName}</span> },
              { key: 'v', header: 'Total value', className: 'num', width: '120px', render: (r) => dkk(r.totalValueDkk || r.annualValueDkk) },
              { key: 'n', header: tab === 'exclusivity' ? 'Exclusivity' : 'Status', className: 'col-hide-sm', render: (r) => (tab === 'exclusivity' ? <span className="cell-clip" title={r.note ?? ''}>{r.note}</span> : <StatusDot status={r.status} />) },
            ]}
          />
        )}
      </div>
      <div className="pager">
        <span className="caption">{rows.length > 50 ? `Showing 50 of ${rows.length}.` : plural(rows.length, 'contract')}</span>
        <div className="spacer" />
        <Button variant="secondary" size="sm" icon="filter" onClick={() => drill(drillPatch[tab])} disabled={rows.length === 0 || Object.entries(drillPatch[tab]).every(([k, v]) => (filter as Record<string, unknown>)[k] === v)}>
          Show in the list
        </Button>
      </div>
    </Card>
  );
}

export function Overview() {
  const api = useApi();
  const me = useMe();
  const lookups = useLookups();
  const config = useAppConfig();
  const toast = useToast();
  const budget = useColumnBudget();
  const [params, setParams] = useSearchParams();
  const [threshold, setThreshold] = useState<number | null>(null);
  const [dataRoom, setDataRoom] = useState(false);
  const [customDates, setCustomDates] = useState(false);

  const filter = useMemo(() => parseFilter(params), [params]);
  const sort = params.get('sort') ?? 'notice';
  const page = Math.max(1, Number(params.get('page')) || 1);
  const dd = threshold ?? config.ddThresholdDkk;

  /** Every drill-down goes through here: new browser history entry, back to page 1. */
  const drill = (patch: Partial<ContractFilter>, replace = false) => {
    const next = applyDrill(filter, patch);
    const p = filterToParams(next, params);
    p.delete('page');
    setParams(p, { replace });
  };
  /** Breadcrumb remove and Clear all. The URL is the only state: the entity scope in the top bar follows it. */
  const setFilter = (next: ContractFilter) => {
    const p = filterToParams(next, params);
    p.delete('page');
    setParams(p);
    if (!next.from && !next.to) setCustomDates(false);
  };

  const analytics = useQuery({ queryKey: qk.analytics({ ...filter, dd }), queryFn: () => api.getOverviewAnalytics(filter, dd), placeholderData: keepPreviousData });
  const listQuery = { ...filter, includeEnded: filter.includeEnded ?? false, sort: sort as never, page, pageSize: PAGE_SIZE };
  const list = useQuery({ queryKey: qk.contractList({ overview: true, ...listQuery }), queryFn: () => api.getContracts(listQuery), placeholderData: keepPreviousData });

  const cpId = typeof filter.counterpartyId === 'string' ? filter.counterpartyId : null;
  const cpPage = useQuery({ queryKey: qk.counterparty(cpId ?? ''), queryFn: () => api.getCounterparty(cpId ?? ''), enabled: !!cpId, staleTime: 60_000 });

  const exportCsv = useMutation({
    mutationFn: () => api.exportContracts({ ...filter, includeEnded: filter.includeEnded ?? false, sort: sort as never, columns: defaultColumns('xl') } as never),
    onSuccess: (file) => {
      saveBlob(file.blob, file.fileName);
      toast.show('Export downloaded. It opens in Excel.');
    },
    onError: (e) => toast.error(e, 'The export could not be created. Try again.'),
  });

  const data = analytics.data;
  const chips = chipsOf(filter, lookups, {
    counterparties: Object.fromEntries([...(data?.topCounterparties ?? []).map((b) => [b.key, b.label]), ...(cpPage.data ? [[cpPage.data.counterparty.id, cpPage.data.counterparty.name]] : [])]),
    tags: Object.fromEntries(config.tags.map((t) => [t.id, t.name])),
    customFields: Object.fromEntries(config.customFields.map((d) => [d.key, d.label])),
  });
  const refs = (xs: { id: string; name: string }[]) => xs.map((x) => ({ value: x.id, label: x.name }));
  const single = (v: string | string[] | undefined) => (Array.isArray(v) ? (v.length === 1 ? (v[0] ?? '') : '') : (v ?? ''));
  const datePreset = filter.from || filter.to ? (DATE_PRESETS.find((p) => { const r = p.range(todayIso()); return p.field === (filter.dateField ?? 'end') && r.from === filter.from && r.to === filter.to; })?.value ?? 'custom') : '';
  const filterFields = config.customFields.filter((d) => d.showInFilters && (d.type === 'Choice' || d.type === 'MultiChoice' || d.type === 'YesNo'));

  const columns = contractColumns(defaultColumns(budget).filter((c) => c !== 'tags' || budget === 'xl'), { customFields: config.customFields, lookups });
  const items = list.data?.items ?? [];
  const total = list.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const setPage = (n: number) => {
    const p = new URLSearchParams(params);
    if (n <= 1) p.delete('page');
    else p.set('page', String(n));
    setParams(p, { replace: true });
  };

  const monthCols: StackColumn[] = (data?.noticeByMonth ?? []).map((m) => {
    const [y, mm] = m.month.split('-');
    return {
      key: m.month,
      label: monthShort(m.month),
      long: `${MONTHS_LONG[Number(mm) - 1]} ${y}`,
      sub: mm === '01' ? y : undefined,
      segments: URGENCY.map((u) => ({ key: u.key, label: u.label, value: m[u.key], className: u.className })),
    };
  });

  const active = (key: keyof ContractFilter, value: string) => single(filter[key] as string | string[] | undefined) === value;

  return (
    <div className="page rise overview" style={{ paddingTop: 10 }}>
      <div className="page-head">
        <div className="page-head__text">
          <h1 className="h1">Overview</h1>
          <span className="page-sub">
            {data ? (
              <>
                {data.scope.label} · {plural(data.scope.visibleContracts, 'contract')} you can see. Click any bar or tile to drill down.
              </>
            ) : (
              <Skeleton width={320} height={16} />
            )}
          </span>
        </div>
        <div className="spacer" />
        <div className="row-flex" style={{ gap: 10 }}>
          <Button variant="secondary" icon="download" loading={exportCsv.isPending} onClick={() => exportCsv.mutate()}>
            Export to Excel
          </Button>
          {permissions(me).canExportDataRoom ? (
            <Button variant="secondary" icon="archive" onClick={() => setDataRoom(true)}>
              Data room export
            </Button>
          ) : null}
        </div>
      </div>

      <div className="toolbar toolbar--chips filterbar" role="group" aria-label="Filters">
        <ChipSelect label="Entity" options={refs(lookups.entities)} value={filter.entityId} onChange={(v) => drill({ entityId: v || undefined }, true)} />
        <ChipSelect label="Department" options={refs(lookups.departments)} value={filter.departmentId} onChange={(v) => drill({ departmentId: v || undefined }, true)} />
        <ChipSelect label="Type" options={refs(lookups.contractTypes)} value={filter.typeId} onChange={(v) => drill({ typeId: v || undefined }, true)} />
        <ChipSelect label="Status" options={ALL_STATUSES.map((s) => ({ value: s, label: statusLabel(s) }))} value={filter.status} onChange={(v) => drill({ status: (v || undefined) as ContractStatus | undefined }, true)} />
        <CounterpartyPicker
          compact
          ariaLabel="Counterparty"
          allowCreate={false}
          placeholder={Array.isArray(filter.counterpartyId) ? `Counterparty: ${filter.counterpartyId.length} selected` : 'Counterparty: All'}
          value={cpId ? { supplierId: cpId, name: cpPage.data?.counterparty.name ?? data?.topCounterparties.find((b) => b.key === cpId)?.label ?? 'Counterparty' } : { supplierId: null, name: '' }}
          onChange={(v) => drill({ counterpartyId: v.supplierId ?? undefined }, true)}
        />
        <PersonPicker compact ariaLabel="Owner" placeholder={Array.isArray(filter.ownerId) ? `Owner: ${filter.ownerId.length} selected` : 'Owner: All'} value={typeof filter.ownerId === 'string' ? filter.ownerId : null} clearLabel="Clear owner filter" onChange={(id) => drill({ ownerId: id ?? undefined }, true)} />
        <ChipSelect label="Value" options={VALUE_BANDS.map((b) => ({ value: b.key, label: b.label }))} value={filter.valueBand} onChange={(v) => drill({ valueBand: (v || undefined) as never }, true)} />
        <ChipSelect
          label="Dates"
          allLabel="Any"
          options={DATE_PRESETS.map((p) => ({ value: p.value, label: p.label }))}
          value={customDates ? 'custom' : datePreset}
          onChange={(v) => {
            const preset = DATE_PRESETS.find((p) => p.value === v);
            if (!preset) {
              setCustomDates(false);
              drill({ from: undefined, to: undefined, dateField: undefined }, true);
            } else if (preset.value === 'custom') setCustomDates(true);
            else {
              setCustomDates(false);
              drill({ dateField: preset.field, ...preset.range(todayIso()) }, true);
            }
          }}
        />
        {filterFields.map((d) => (
          <ChipSelect
            key={d.key}
            label={d.label}
            options={d.type === 'YesNo' ? [{ value: 'true', label: 'Yes' }, { value: 'false', label: 'No' }] : d.options.map((o) => ({ value: o, label: o }))}
            value={filter.cf?.[d.key] ?? ''}
            onChange={(v) => drill({ cf: { [d.key]: v } }, true)}
          />
        ))}
        <label className="check check--chip">
          <input type="checkbox" checked={!!filter.includeEnded} onChange={(e) => drill({ includeEnded: e.target.checked || undefined }, true)} />
          Include ended
        </label>
      </div>
      {customDates || datePreset === 'custom' ? (
        <div className="toolbar daterange" role="group" aria-label="Custom date range">
          <Field label="Date">
            {(p) => (
              <select {...p} className="select" value={filter.dateField ?? 'end'} onChange={(e) => drill({ dateField: e.target.value as DateField, from: filter.from ?? todayIso() }, true)}>
                <option value="notice">Notice deadline</option>
                <option value="end">End date</option>
                <option value="start">Start date</option>
                <option value="signed">Signed date</option>
                <option value="created">Created</option>
              </select>
            )}
          </Field>
          <Field label="From">{(p) => <TextInput {...p} type="date" value={filter.from ?? ''} onChange={(e) => drill({ from: e.target.value || undefined, dateField: filter.dateField ?? 'end' }, true)} />}</Field>
          <Field label="To">{(p) => <TextInput {...p} type="date" value={filter.to ?? ''} onChange={(e) => drill({ to: e.target.value || undefined, dateField: filter.dateField ?? 'end' }, true)} />}</Field>
        </div>
      ) : null}

      {chips.length > 0 ? (
        <nav className="crumbs" aria-label="Applied drill-downs">
          <span className="caption">Showing</span>
          <ol>
            {chips.map((c) => (
              <li key={c.id}>
                <span className="crumb">
                  {c.label}
                  <button type="button" aria-label={`Remove ${c.label}`} onClick={() => setFilter(c.without)}>
                    <Icon name="x" size={12} stroke={2.6} />
                  </button>
                </span>
              </li>
            ))}
          </ol>
          <button type="button" className="link-btn" onClick={() => { setCustomDates(false); setFilter({}); }}>
            Clear all
          </button>
        </nav>
      ) : null}

      {analytics.isError ? (
        <Card>
          <ErrorState text="The overview could not be loaded." onRetry={() => void analytics.refetch()} />
        </Card>
      ) : !data ? (
        <div className="kpis kpis--7">{Array.from({ length: 7 }, (_, i) => <Skeleton key={i} height={104} radius={18} />)}</div>
      ) : (
        <div className="stack" style={{ gap: 16, opacity: analytics.isPlaceholderData ? 0.6 : 1 }} aria-busy={analytics.isPlaceholderData}>
          <div className="kpis kpis--7">
            <KpiTile label="Contracts" value={data.kpis.count} sub={filter.includeEnded ? 'All statuses' : 'Excluding ended'} />
            <KpiTile label="Annual value" value={dkk(data.kpis.annualValueDkk)} sub="Converted to DKK" />
            <KpiTile label="Total value" value={dkk(data.kpis.totalValueDkk)} sub="Where a total is given" />
            <KpiTile label="Notice in 30 days" value={data.kpis.notice30} sub={`${data.kpis.notice90} within 90 days`} tone={data.kpis.notice30 > 0 ? 'red' : undefined} active={filter.noticeWithinDays === 30} onClick={() => drill({ noticeWithinDays: filter.noticeWithinDays === 30 ? 90 : 30 })} />
            <KpiTile label="In approval" value={data.kpis.inApproval} sub="Waiting for approvers" active={active('status', 'InApproval')} onClick={() => drill({ status: 'InApproval' })} />
            <KpiTile label="Missing data" value={data.kpis.missingData} sub={data.kpis.missingData ? 'Contracts to complete' : 'All complete'} tone={data.kpis.missingData > 0 ? 'amber' : undefined} active={filter.missingData === true} onClick={() => drill({ missingData: true })} />
            <KpiTile label="Auto-renewing" value={`${Math.round(data.kpis.autoRenewSharePct)}%`} sub={plural(data.kpis.autoRenewCount, 'contract')} active={filter.autoRenew === true} onClick={() => drill({ autoRenew: true })} />
          </div>

          <div className="chart-grid">
            <ChartCard title="Annual value by legal entity" id="c-entity">
              <BarList label="Annual value by legal entity" empty="No contracts in this selection." rows={data.byEntity.map((b) => ({ key: b.key, label: b.label, value: b.annualValueDkk, text: dkk(b.annualValueDkk), detail: shareText(b), active: active('entityId', b.key) }))} onPick={(r) => drill({ entityId: r.key })} />
            </ChartCard>
            <ChartCard title="Annual value by department" id="c-dept">
              <BarList label="Annual value by department" empty="No contracts in this selection." rows={data.byDepartment.map((b) => ({ key: b.key, label: b.label, value: b.annualValueDkk, text: dkk(b.annualValueDkk), detail: shareText(b), active: active('departmentId', b.key) }))} onPick={(r) => drill({ departmentId: r.key })} />
            </ChartCard>
            <ChartCard title="Annual value by contract type" id="c-type">
              <BarList label="Annual value by contract type" empty="No contracts in this selection." rows={data.byType.map((b) => ({ key: b.key, label: b.label, value: b.annualValueDkk, text: dkk(b.annualValueDkk), detail: shareText(b), active: active('typeId', b.key) }))} onPick={(r) => drill({ typeId: r.key })} />
            </ChartCard>
            <ChartCard title="Top 10 counterparties" sub={`The top 10 hold ${data.top10SharePct}% of the annual value`} id="c-cp">
              <BarList
                label="Top 10 counterparties by annual value"
                empty="No contracts in this selection."
                rows={data.topCounterparties.map((b) => ({ key: b.key || `name:${b.label}`, label: b.label, value: b.annualValueDkk, text: `${dkk(b.annualValueDkk)} · ${b.sharePct}%`, detail: plural(b.count, 'contract'), active: active('counterpartyId', b.key) }))}
                onPick={(r) => (r.key.startsWith('name:') ? drill({ q: r.label }) : drill({ counterpartyId: r.key }))}
              />
            </ChartCard>
            <ChartCard wide title="Notice deadlines by month" sub="Next 24 months, active contracts; overdue undecided ones count in the current month" id="c-notice">
              <StackedColumns
                label="Notice deadlines by month, stacked by urgency"
                columns={monthCols}
                legend={URGENCY.map((u) => ({ label: u.label, className: u.className }))}
                onPick={(col, seg) => drill({ dateField: 'notice', ...monthRange(col.key), decided: seg?.key === 'decided' ? true : seg ? false : undefined, noticeWithinDays: undefined })}
              />
            </ChartCard>
            <ChartCard title="Contracts ending by quarter" sub="By end date; open-ended contracts are not shown" id="c-quarter">
              <Columns label="Contracts ending by quarter" columns={data.expiringByQuarter.map((q) => ({ key: q.quarter, label: q.quarter.replace('-', ' '), long: q.quarter.replace('-', ' '), value: q.count, text: plural(q.count, 'contract'), detail: `${q.autoRenew} auto-renew · ${dkk(q.annualValueDkk)} a year` }))} onPick={(k) => { const q = data.expiringByQuarter.find((x) => x.quarter === k); if (q) drill({ dateField: 'end', from: q.from, to: q.to }); }} />
            </ChartCard>
            <ChartCard title="Status" sub="Contracts per lifecycle status" id="c-status">
              <BarList label="Contracts by status" empty="No contracts." rows={data.statusFunnel.filter((b) => filter.includeEnded || filter.status || (b.key !== 'Expired' && b.key !== 'Terminated')).map((b) => ({ key: b.key, label: b.label, value: b.count, text: String(b.count), detail: dkk(b.annualValueDkk), active: active('status', b.key) }))} onPick={(r) => drill({ status: r.key as ContractStatus })} />
            </ChartCard>
            <ChartCard title="Data quality by legal entity" sub="Share of contracts with no missing data" id="c-dq">
              <ScoreList label="Data quality by legal entity" rows={data.dataQuality.map((d) => ({ key: d.entityId, label: d.name, score: d.score, detail: `${d.complete} of ${d.contracts} complete` }))} onPick={(k) => drill({ entityId: k, missingData: true })} />
            </ChartCard>
          </div>

          <DueDiligence data={data} filter={filter} drill={drill} threshold={dd} setThreshold={setThreshold} />
        </div>
      )}

      <Card clip className="register-card" aria-labelledby="ov-list">
        <div className="card-list-head card-list-head--row">
          <div className="stack" style={{ gap: 4 }}>
            <h2 className="card-title" id="ov-list">
              {isEmptyFilter(filter) ? 'Contracts' : 'Matching contracts'}
            </h2>
            <span className="caption" aria-live="polite">
              {list.data ? `${plural(total, 'contract')}${isEmptyFilter(filter) ? '' : ' match the drill-down'}` : 'Loading'}
            </span>
          </div>
          <div className="spacer" />
          <Button variant="secondary" size="sm" icon="download" loading={exportCsv.isPending} onClick={() => exportCsv.mutate()}>
            Export
          </Button>
        </div>
        {list.isLoading ? (
          <SkeletonRows rows={6} label="Loading contracts" />
        ) : list.isError ? (
          <ErrorState text="The contracts could not be loaded." onRetry={() => void list.refetch()} />
        ) : items.length === 0 ? (
          <EmptyState icon="search" title="No contracts match" text="Remove a drill-down above to see more." />
        ) : (
          <>
            <div style={{ opacity: list.isPlaceholderData ? 0.6 : 1 }}>
              <Table fixed caption="Matching contracts" columns={columns} rows={items} rowKey={(r) => r.id} sort={sort} onSort={(s) => { const p = new URLSearchParams(params); if (s === 'notice') p.delete('sort'); else p.set('sort', s); p.delete('page'); setParams(p, { replace: true }); }} />
            </div>
            <div className="pager">
              <span>
                {(page - 1) * PAGE_SIZE + 1}–{Math.min(page * PAGE_SIZE, total)} of {total} · {formatMoney(items.reduce((s, i) => s + (i.annualValueDkk ?? 0), 0), 'DKK')} a year on this page
              </span>
              <div className="spacer" />
              <Button variant="secondary" size="sm" icon="chevronLeft" disabled={page <= 1} onClick={() => setPage(page - 1)}>
                Previous
              </Button>
              <Button variant="secondary" size="sm" disabled={page >= pages} onClick={() => setPage(page + 1)}>
                Next
              </Button>
            </div>
          </>
        )}
      </Card>
      {apiMode === 'mock' ? <p className="caption">Sample data. Values in other currencies are converted with the rates in Settings.</p> : null}
      {dataRoom ? <DataRoomDialog filter={{ ...filter, includeEnded: filter.includeEnded ?? false }} chips={chips.map((c) => c.label)} onClose={() => setDataRoom(false)} /> : null}
    </div>
  );
}
