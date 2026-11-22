import { useEffect, useState } from 'react';
import { keepPreviousData, useMutation, useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { apiMode } from '../api';
import { qk, useApi, useLookups, useScope } from '../app/context';
import type { ContractListItem, ContractQuery, ContractSort, ContractStatus, ContractView } from '../api/types';
import { Avatar, Button, ButtonLink, Card, ChipSelect, EmptyState, ErrorState, Icon, NoticePill, Segmented, Skeleton, SkeletonRows, StatusDot, Table, useToast, type Column } from '../components';
import { formatDate, noticeTone } from '../lib/dates';
import { ALL_STATUSES, formatMoneyCompact, plural, saveBlob, statusLabel } from '../lib/format';

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
      {item.missingData.length > 0 ? <p className="banner tone-amber">Missing: {item.missingData.join(', ').toLowerCase()}</p> : null}
      <ButtonLink to={`/contracts/${item.id}`} block>
        Open contract
      </ButtonLink>
    </Card>
  );
}

export function Register() {
  const api = useApi();
  const lookups = useLookups();
  const scope = useScope();
  const toast = useToast();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();

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

  const filters: ContractQuery = {
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
  const all = useQuery({ queryKey: qk.contractList({ count: true }), queryFn: () => api.getContracts({ pageSize: 1 }) });

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
  const selected = items.find((i) => i.id === selectedId) ?? items[0] ?? null;
  const hasFilters = !!(q || scope.entityId || departmentId || typeId || status || view !== 'all');

  const clear = () => {
    scope.setEntityId('');
    setText('');
    setParams({}, { replace: true });
  };

  const pick = (row: ContractListItem) => {
    // Under 720px there is no quick look panel: go straight to the contract.
    if (window.matchMedia?.('(max-width: 720px)').matches) navigate(`/contracts/${row.id}`);
    else update({ sel: row.id }, true);
  };

  const columns: Column<ContractListItem>[] = [
    {
      key: 'contract',
      header: 'Contract',
      sortKey: 'title',
      render: (r) => (
        <div className="cell-title">
          <button type="button" className="cell-title__btn" aria-label={`Preview ${r.title}`} onClick={(e) => { e.stopPropagation(); pick(r); }} onDoubleClick={() => navigate(`/contracts/${r.id}`)}>
            {r.title}
          </button>
          <span className="caption">
            {r.counterparty} · {r.typeName}
            <span className="only-sm"> · {statusLabel(r.status)}</span>
          </span>
        </div>
      ),
    },
    { key: 'entity', header: 'Entity', sortKey: 'entity', className: 'col-hide-sm', render: (r) => r.entityName },
    {
      key: 'owner',
      header: 'Owner',
      sortKey: 'owner',
      className: 'col-hide-sm',
      render: (r) => (
        <span className="cell-owner" title={r.ownerName}>
          <Avatar initials={r.ownerInitials} size="sm" />
          <span className="sr-only">{r.ownerName}, </span>
          {r.departmentName}
        </span>
      ),
    },
    { key: 'notice', header: 'Notice', sortKey: 'notice', render: (r) => <NoticePill days={r.daysToNotice} /> },
    { key: 'status', header: 'Status', sortKey: 'status', className: 'col-hide-sm', render: (r) => <StatusDot status={r.status} /> },
  ];

  const refs = (xs: { id: string; name: string }[]) => xs.map((x) => ({ value: x.id, label: x.name }));

  return (
    <div className="page rise" style={{ paddingTop: 10 }}>
      <div className="page-head">
        <div className="page-head__text">
          <h1 className="h1">Contracts</h1>
          <span className="page-sub">
            {all.data ? `${plural(all.data.total, 'contract')} you can see, across ${lookups.entities.length} legal entities` : all.isError ? 'Contracts you can see' : <Skeleton width={260} height={16} />}
          </span>
        </div>
        <div className="spacer" />
        <Button variant="secondary" icon="download" loading={exportCsv.isPending} onClick={() => exportCsv.mutate()}>
          Export
        </Button>
      </div>

      <div className="toolbar">
        <Segmented ariaLabel="Views" options={VIEWS} value={view} onChange={(v) => update({ view: v === 'all' ? null : v, sel: null })} />
        <div className="spacer" />
        <label className="search-pill">
          <span className="sr-only">Search contracts</span>
          <Icon name="search" size={15} stroke={2.2} />
          <input type="search" placeholder="Search" value={text} onChange={(e) => setText(e.target.value)} />
        </label>
        <ChipSelect label="Entity" options={refs(lookups.entities)} value={scope.entityId} onChange={(v) => { scope.setEntityId(v); update({ sel: null }); }} />
        <ChipSelect label="Department" options={refs(lookups.departments)} value={departmentId} onChange={(v) => update({ department: v, sel: null })} />
        <ChipSelect label="Type" options={refs(lookups.contractTypes)} value={typeId} onChange={(v) => update({ type: v, sel: null })} />
        <select className={`chip only-phone${sort !== 'notice' ? ' chip--on' : ''}`} aria-label="Sort by" value={sort} onChange={(e) => update({ sort: e.target.value === 'notice' ? null : e.target.value })}>
          {SORTS.map((s) => (
            <option key={s.value} value={s.value}>
              Sort: {s.label}
            </option>
          ))}
        </select>
        <ChipSelect label="Status" options={ALL_STATUSES.map((s) => ({ value: s, label: statusLabel(s) }))} value={status} onChange={(v) => update({ status: v, sel: null })} />
      </div>

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
                <Table caption="Contracts" columns={columns} rows={items} rowKey={(r) => r.id} sort={sort} onSort={(s) => update({ sort: s === 'notice' ? null : s })} onRowClick={pick} selectedKey={selected?.id} />
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
        {selected ? <QuickLook key={selected.id} item={selected} /> : null}
      </div>
      {apiMode === 'mock' ? <p className="caption">Sample data. Select a row to preview it.</p> : null}
    </div>
  );
}
