import { useEffect, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { keepPreviousData, useMutation, useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { apiMode } from '../api';
import { qk, useApi, useAppConfig, useLookups, useScope } from '../app/context';
import type { ContractListItem, ContractQuery, ContractSort, ContractStatus, ContractView, RegisterColumn } from '../api/types';
import { Button, ButtonLink, Card, ChipSelect, EmptyState, ErrorState, Icon, Segmented, Skeleton, SkeletonRows, Table, useToast, type Column } from '../components';
import { BulkBar } from '../features/Bulk';
import { contractColumns, defaultColumns, TagList, useColumnBudget } from '../features/columns';
import { ColumnChooser, ViewsMenu } from '../features/Views';
import { chipsOf, filterToParams as filterParams, parseFilter } from '../lib/drill';
import { sanitizeColumns } from '../lib/views';
import { formatDate, noticeTone } from '../lib/dates';
import { ALL_STATUSES, formatMoneyCompact, plural, saveBlob, statusLabel } from '../lib/format';
import { useNarrow, usePhone } from '../lib/media';

const VIEWS: { value: ContractView; label: string }[] = [
  { value: 'all', label: 'All contracts' },
  { value: 'due90', label: 'Notice in 90 days' },
  { value: 'approval', label: 'In approval' },
  { value: 'mine', label: 'Mine' },
  { value: 'missing', label: 'Missing data' },
];
const PAGE_SIZE = 15;
/** Phone layout: the table headers (and their sort buttons) are hidden, so sorting is a select. */
const SORTS: { value: ContractSort; label: string }[] = [
  { value: 'notice', label: 'Notice deadline' },
  { value: 'title', label: 'Title' },
  { value: '-value', label: 'Highest value' },
  { value: 'status', label: 'Status' },
  { value: 'entity', label: 'Legal entity' },
];

const isView = (v: string | null): v is ContractView => VIEWS.some((x) => x.value === v);

function QuickLook({ item }: { item: ContractListItem }) {
  const api = useApi();
  const detail = useQuery({ queryKey: qk.contractPeek(item.id), queryFn: () => api.getContract(item.id, { peek: true }) });
  const d = detail.data;
  const tone = noticeTone(item.daysToNotice);
  const pending = <Skeleton width={90} height={16} />;
  return (
    <Card as="aside" aria-label="Quick look" className="quick split__aside">
      <div className="stack" style={{ gap: 6 }}>
        <span className="eyebrow" style={{ color: 'var(--primary)', opacity: 1, letterSpacing: '0.06em' }}>
          Quick look
        </span>
        <h2 style={{ fontSize: 18, fontWeight: 700, letterSpacing: '-0.01em' }}>{item.title}</h2>
        <span className="secondary">
          {item.counterparty} · {item.entityName}
        </span>
      </div>
      <div className="quick__days">
        <span className={`quick__n fg-${tone}`}>{item.daysToNotice === null ? '–' : Math.abs(item.daysToNotice)}</span>
        {item.daysToNotice === null ? (
          <span>{item.missingData.includes('Notice period') ? 'No notice deadline: the notice period is missing' : ['NoticeGiven', 'Expired', 'Terminated'].includes(item.status) ? `No notice deadline: ${statusLabel(item.status).toLowerCase()}` : 'No notice deadline'}</span>
        ) : (
          <span>
            {item.daysToNotice < 0 ? 'days past the notice deadline' : 'days to notice deadline'}
            <br />
            {formatDate(item.noticeDeadline)}
          </span>
        )}
      </div>
      <dl className="dl-grid">
        <div className="kv">
          <dt className="kv__k">Annual value</dt>
          <dd className="kv__v">{formatMoneyCompact(item.annualValue, item.currency)}</dd>
        </div>
        <div className="kv">
          <dt className="kv__k">Renewal</dt>
          <dd className="kv__v">{d ? (d.autoRenew && d.renewalMonths ? `Auto, ${d.renewalMonths} months` : d.endDate ? 'Fixed term' : 'Open-ended') : pending}</dd>
        </div>
        <div className="kv">
          <dt className="kv__k">Department</dt>
          <dd className="kv__v">{item.departmentName}</dd>
        </div>
        <div className="kv">
          <dt className="kv__k">Documents</dt>
          <dd className="kv__v">{d ? plural(d.documents.length, 'file') : pending}</dd>
        </div>
        <div className="kv">
          <dt className="kv__k">Owner</dt>
          <dd className="kv__v">{item.ownerName}</dd>
        </div>
        <div className="kv">
          <dt className="kv__k">Contract no.</dt>
          <dd className="kv__v">{item.contractNo}</dd>
        </div>
      </dl>
      {item.tags?.length ? <TagList tags={item.tags} max={6} /> : null}
      {item.missingData.length > 0 ? <p className="banner tone-amber">Missing: {item.missingData.join(', ').toLowerCase()}</p> : null}
      <ButtonLink to={`/contracts/${item.id}`} block>
        Open contract
      </ButtonLink>
    </Card>
  );
}

/** Renders its children in the top bar, next to "New contract" (Register.dc.html has Export there). */
function TopBarActions({ children }: { children: ReactNode }) {
  const [target, setTarget] = useState<HTMLElement | null>(null);
  useEffect(() => setTarget(document.getElementById('topbar-actions')), []);
  return target ? createPortal(children, target) : null;
}

export function Register() {
  const api = useApi();
  const lookups = useLookups();
  const scope = useScope();
  const toast = useToast();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const phone = usePhone();
  const narrow = useNarrow();
  const config = useAppConfig();
  const budget = useColumnBudget();
  const [choosing, setChoosing] = useState(false);
  const [selected, setSelected] = useState<string[]>([]);
  // With many columns the table needs the full width: the quick look is left out (double-click or the title opens the contract).
  const wide = false;

  const view: ContractView = isView(params.get('view')) ? (params.get('view') as ContractView) : 'all';
  const q = params.get('q') ?? '';
  const departmentId = params.get('department') ?? '';
  const typeId = params.get('type') ?? '';
  const status = (params.get('status') ?? '') as ContractStatus | '';
  const sort = (params.get('sort') ?? 'notice') as ContractSort;
  const page = Math.max(1, Number(params.get('page')) || 1);
  const selectedId = params.get('sel');

  /** Updates URL params; any filter change goes back to page 1. */
  const update = (changes: Record<string, string | null>, keepPage = false) => {
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        if (!keepPage) next.delete('page');
        for (const [k, v] of Object.entries(changes)) {
          if (v === null || v === '') next.delete(k);
          else next.set(k, v);
        }
        return next;
      },
      { replace: true },
    );
  };

  // The search box is local state, written to the URL after a short pause.
  const [text, setText] = useState(q);
  useEffect(() => setText(q), [q]);
  useEffect(() => {
    if (text === q) return;
    const t = window.setTimeout(() => update({ q: text.trim() ? text : null }), 250);
    return () => window.clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [text]);

  // Release 3: every filter of the shared contract filter can come from the URL (saved views, Overview links).
  const { entityId: _urlEntity, ...extra } = parseFilter(params);
  const filters: ContractQuery = {
    ...extra,
    view,
    q: q.trim() || undefined,
    entityId: scope.entityId || undefined,
    departmentId: departmentId || undefined,
    typeId: typeId || undefined,
    status: status || undefined,
    sort,
  };
  const query: ContractQuery = { ...filters, page, pageSize: PAGE_SIZE };

  const list = useQuery({ queryKey: qk.contractList(query), queryFn: () => api.getContracts(query), placeholderData: keepPreviousData });
  // Everything the user can see, to say how many contracts in how many legal entities (up to 200 rows).
  const all = useQuery({ queryKey: qk.contractList({ count: true }), queryFn: () => api.getContracts({ pageSize: 200 }) });
  const entityCount = all.data && all.data.total <= all.data.items.length ? new Set(all.data.items.map((i) => i.entityName)).size : null;

  const exportCsv = useMutation({
    mutationFn: () => api.exportContracts(filters),
    onSuccess: (file) => {
      saveBlob(file.blob, file.fileName);
      toast.show('Export downloaded');
    },
    onError: (e) => toast.error(e, 'The export could not be created. Try again.'),
  });

  const items = list.data?.items ?? [];
  const total = list.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  // A page past the end (an old link, or a filter that shortened the list) goes to the last page.
  const pastEnd = !!list.data && !list.isPlaceholderData && page > pages;
  useEffect(() => {
    if (pastEnd) update({ page: pages <= 1 ? null : String(pages) }, true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pastEnd, pages]);
  const peek = items.find((i) => i.id === selectedId) ?? items[0] ?? null;
  const extraChips = chipsOf({ ...extra, q: undefined, departmentId: undefined, typeId: undefined, status: undefined }, lookups, { tags: Object.fromEntries(config.tags.map((t) => [t.id, t.name])), customFields: Object.fromEntries(config.customFields.map((d) => [d.key, d.label])) });
  const hasFilters = !!(q || scope.entityId || departmentId || typeId || status || view !== 'all' || extraChips.length);
  const colsParam = params.get('cols');
  const columnKeys: RegisterColumn[] = colsParam ? sanitizeColumns(colsParam.split(','), config.customFields) : defaultColumns(budget);

  const clear = () => {
    scope.setEntityId('');
    setText('');
    setParams({}, { replace: true });
  };

  const pick = (row: ContractListItem) => {
    // Under 720px there is no quick look panel: go straight to the contract.
    if (phone) navigate(`/contracts/${row.id}`);
    else update({ sel: row.id }, true);
  };

  const titleCell = (r: ContractListItem) => (
    <div className="cell-title">
      <button type="button" className="cell-title__btn" title={r.title} aria-label={`${phone ? 'Open' : 'Preview'} ${r.title}`} onClick={(e) => { e.stopPropagation(); pick(r); }} onDoubleClick={() => navigate(`/contracts/${r.id}`)}>
        {r.title}
      </button>
      <span className="caption cell-title__meta" title={`${r.counterparty} · ${r.typeName}`}>
        {r.counterparty} · {r.typeName}
        <span className="only-sm"> · {statusLabel(r.status)}</span>
      </span>
    </div>
  );
  const pageIds = items.map((i) => i.id);
  const allOnPage = pageIds.length > 0 && pageIds.every((id) => selected.includes(id));
  const selectColumn: Column<ContractListItem> = {
    key: 'select',
    header: (
      <input type="checkbox" className="check__box" aria-label="Select all on this page" checked={allOnPage} onChange={(e) => setSelected(e.target.checked ? [...new Set([...selected, ...pageIds])] : selected.filter((id) => !pageIds.includes(id)))} />
    ),
    width: '44px',
    className: 'col-select hide-phone',
    render: (r) => <input type="checkbox" className="check__box" aria-label={`Select ${r.title}`} checked={selected.includes(r.id)} onClick={(e) => e.stopPropagation()} onChange={(e) => setSelected(e.target.checked ? [...selected, r.id] : selected.filter((x) => x !== r.id))} />,
  };
  const configured = contractColumns(columnKeys, { title: titleCell, customFields: config.customFields, lookups });
  // Tablet (720–1000px): no room for five columns. Contract, Notice and Status stay; the entity and
  // the owner are in the record. (A phone shows cards; the CSS hides the extra cells there.)
  const tablet = narrow && !phone;
  const columns = [
    selectColumn,
    ...(tablet ? contractColumns(['contract', 'notice', 'status'], { title: titleCell }).map((c) => (c.key === 'contract' ? { ...c, width: undefined } : c)) : configured),
  ];

  const exportButton = (
    <Button variant="secondary" icon="download" loading={exportCsv.isPending} onClick={() => exportCsv.mutate()}>
      Export
    </Button>
  );

  const refs = (xs: { id: string; name: string }[]) => xs.map((x) => ({ value: x.id, label: x.name }));

  return (
    <div className="page rise" style={{ paddingTop: 10 }}>
      <div className="page-head">
        <div className="page-head__text">
          <h1 className="h1">Contracts</h1>
          <span className="page-sub">
            {all.data ? (
              `${plural(all.data.total, 'contract')} you can see${entityCount ? `, across ${plural(entityCount, 'legal entity', 'legal entities')}` : ''}`
            ) : all.isError ? (
              'Contracts you can see'
            ) : (
              <Skeleton width={260} height={16} />
            )}
          </span>
        </div>
      </div>
      {/* Export sits beside "New contract" in the top bar; under 1000px it is in the filter row. */}
      {narrow ? null : <TopBarActions>{exportButton}</TopBarActions>}

      <div className="toolbar toolbar--views">
        <Segmented ariaLabel="Views" options={VIEWS} value={view} onChange={(v) => update({ view: v === 'all' ? null : v, sel: null })} />
        <div className="spacer" />
        {phone ? null : (
          <>
            <ViewsMenu
              params={params}
              columns={columnKeys}
              onApply={(p) => {
                const e = p.get('entity');
                if (e) scope.adopt(e);
                setParams(p, { replace: false });
              }}
            />
            <Button variant="secondary" size="sm" icon="columns" onClick={() => setChoosing(true)}>
              Columns
            </Button>
          </>
        )}
        <label className="search-pill">
          <span className="sr-only">Search contracts</span>
          <Icon name="search" size={15} stroke={2.2} />
          <input type="search" placeholder="Search" value={text} onChange={(e) => setText(e.target.value)} />
        </label>
      </div>
      <div className="toolbar toolbar--chips" role="group" aria-label="Filters">
        <ChipSelect label="Entity" options={refs(lookups.entities)} value={scope.entityId} onChange={(v) => { scope.setEntityId(v); update({ sel: null }); }} />
        <ChipSelect label="Department" options={refs(lookups.departments)} value={departmentId} onChange={(v) => update({ department: v, sel: null })} />
        <ChipSelect label="Type" options={refs(lookups.contractTypes)} value={typeId} onChange={(v) => update({ type: v, sel: null })} />
        <ChipSelect label="Status" options={ALL_STATUSES.map((s) => ({ value: s, label: statusLabel(s) }))} value={status} onChange={(v) => update({ status: v, sel: null })} />
        {config.tags.length ? <ChipSelect label="Tag" options={config.tags.map((t) => ({ value: t.id, label: t.name }))} value={params.get('tag') ?? ''} onChange={(v) => update({ tag: v, sel: null })} /> : null}
        {narrow
          ? null
          : config.customFields
              .filter((d) => d.showInFilters && (d.type === 'Choice' || d.type === 'MultiChoice' || d.type === 'YesNo'))
              .map((d) => (
                <ChipSelect key={d.key} label={d.label} options={d.type === 'YesNo' ? [{ value: 'true', label: 'Yes' }, { value: 'false', label: 'No' }] : d.options.map((o) => ({ value: o, label: o }))} value={params.get(`cf.${d.key}`) ?? ''} onChange={(v) => update({ [`cf.${d.key}`]: v, sel: null })} />
              ))}
        <select className={`chip only-phone${sort !== 'notice' ? ' chip--on' : ''}`} aria-label="Sort by" value={sort} onChange={(e) => update({ sort: e.target.value === 'notice' ? null : e.target.value })}>
          {SORTS.map((s) => (
            <option key={s.value} value={s.value}>
              Sort: {s.label}
            </option>
          ))}
        </select>
        {narrow ? (
          <>
            <div className="spacer hide-phone" />
            {exportButton}
          </>
        ) : null}
      </div>
      {extraChips.length ? (
        <nav className="crumbs" aria-label="More filters">
          <span className="caption">Also filtered by</span>
          <ol>
            {extraChips.map((c) => (
              <li key={c.id}>
                <span className="crumb">
                  {c.label}
                  <button
                    type="button"
                    aria-label={`Remove ${c.label}`}
                    onClick={() => {
                      const next = filterParams({ ...c.without, q: q || undefined, departmentId: departmentId || undefined, typeId: typeId || undefined, status: (status || undefined) as ContractStatus | undefined, entityId: scope.entityId || undefined }, params);
                      next.delete('page');
                      setParams(next, { replace: true });
                    }}
                  >
                    <Icon name="x" size={12} stroke={2.6} />
                  </button>
                </span>
              </li>
            ))}
          </ol>
        </nav>
      ) : null}
      {selected.length > 0 && !phone ? <BulkBar ids={selected} onClear={() => setSelected([])} /> : null}
      {choosing ? (
        <ColumnChooser
          columns={columnKeys}
          onClose={() => setChoosing(false)}
          onReset={() => {
            update({ cols: null }, true);
            setChoosing(false);
          }}
          onChange={(c) => {
            update({ cols: c.join(',') }, true);
            setChoosing(false);
          }}
        />
      ) : null}

      <div className="split">
        <Card clip className="split__main split__main--wide register-card" style={{ gap: 0 }}>
          {list.isLoading ? (
            <SkeletonRows rows={8} label="Loading contracts" />
          ) : list.isError ? (
            <ErrorState text="The contracts could not be loaded." onRetry={() => void list.refetch()} />
          ) : items.length === 0 ? (
            <EmptyState
              icon="search"
              title={hasFilters ? 'No contracts match these filters' : 'No contracts yet'}
              text={hasFilters ? 'Try another view, or clear the search and filters.' : 'Contracts you have access to show up here.'}
              action={hasFilters ? <Button variant="secondary" size="md" onClick={clear}>Clear filters</Button> : undefined}
            />
          ) : (
            <>
              <div style={{ opacity: list.isPlaceholderData ? 0.6 : 1 }} aria-busy={list.isPlaceholderData}>
                <Table fixed caption="Contracts" columns={columns} rows={items} rowKey={(r) => r.id} sort={sort} onSort={(s) => update({ sort: s === 'notice' ? null : s })} onRowClick={pick} selectedKey={peek?.id} />
              </div>
              <div className="pager">
                <span aria-live="polite">
                  {(page - 1) * PAGE_SIZE + 1}–{Math.min(page * PAGE_SIZE, total)} of {total}
                </span>
                <div className="spacer" />
                <Button variant="secondary" size="sm" icon="chevronLeft" disabled={page <= 1} onClick={() => update({ page: page - 1 <= 1 ? null : String(page - 1), sel: null }, true)}>
                  Previous
                </Button>
                <Button variant="secondary" size="sm" disabled={page >= pages} onClick={() => update({ page: String(page + 1), sel: null }, true)}>
                  Next
                </Button>
              </div>
            </>
          )}
        </Card>
        {peek && !wide ? <QuickLook key={peek.id} item={peek} /> : null}
      </div>
      {apiMode === 'mock' ? <p className="caption">Sample data.<span className="hide-phone"> Select a row to preview it.</span></p> : null}
    </div>
  );
}
