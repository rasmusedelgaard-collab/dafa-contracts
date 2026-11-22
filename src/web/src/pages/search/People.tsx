import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ApiError } from '../../api/api';
import { qk, useApi, useAppConfig, useLookups } from '../../app/context';
import type { ContractListItem, PersonContract } from '../../api/types';
import { Avatar, Card, EmptyState, ErrorState, Icon, Pill, Segmented, Skeleton, SkeletonRows, Table, useToast, type Column } from '../../components';
import { dkk, KpiTile } from '../../features/charts';
import { contractColumns, defaultColumns, useColumnBudget } from '../../features/columns';
import { formatDate } from '../../lib/dates';
import { formatFileSize, plural, saveBlob } from '../../lib/format';

function Back({ to = -1 }: { to?: string | -1 }) {
  const navigate = useNavigate();
  return (
    <button type="button" className="back-link" onClick={() => (to === -1 ? navigate(-1) : navigate(to))}>
      <Icon name="chevronLeft" size={14} stroke={2.4} />
      Back
    </button>
  );
}

function notFound(e: unknown) {
  return e instanceof ApiError && (e.status === 404 || e.status === 403);
}

/** "Contracts of <person>": every contract (the caller can see) where the person has any role. */
export function PersonPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const config = useAppConfig();
  const lookups = useLookups();
  const budget = useColumnBudget();
  const [role, setRole] = useState<'all' | 'Owner' | 'Delegate' | 'Responsible'>('all');
  const q = useQuery({ queryKey: qk.person(id), queryFn: () => api.getPerson(id) });

  if (q.isLoading) {
    return (
      <div className="page" style={{ paddingTop: 10 }} role="status" aria-label="Loading">
        <Skeleton width={280} height={30} />
        <SkeletonRows rows={6} />
      </div>
    );
  }
  if (q.isError || !q.data) {
    return (
      <div className="page rise" style={{ paddingTop: 10 }}>
        <Back />
        <Card>{notFound(q.error) ? <EmptyState icon="person" title="This person was not found" /> : <ErrorState onRetry={() => void q.refetch()} />}</Card>
      </div>
    );
  }
  const d = q.data;
  const rows = role === 'all' ? d.contracts : d.contracts.filter((c) => c.roles.includes(role));
  const base = contractColumns(defaultColumns(budget).filter((k) => k !== 'owner'), { customFields: config.customFields, lookups }) as Column<PersonContract>[];
  const columns: Column<PersonContract>[] = [...base.slice(0, 1), { key: 'roles', header: 'Role', width: '150px', render: (r) => r.roles.join(', ') }, ...base.slice(1)];

  return (
    <div className="page rise" style={{ paddingTop: 6 }}>
      <Back />
      <header className="record-head">
        <Avatar initials={d.person.initials} size="lg" solid />
        <div className="record-head__text">
          <div className="row-flex" style={{ gap: 10 }}>
            <h1 className="record-title">Contracts of {d.person.name}</h1>
            {!d.person.isActive ? <Pill tone="gray">Inactive</Pill> : null}
          </div>
          <span className="page-sub">{[d.person.title?.split(' · ')[0], ...d.person.departmentNames, d.person.email].filter(Boolean).join(' · ')}</span>
        </div>
        <div className="row-flex record-actions">
          <Link className="btn btn--secondary" to={`/search?person=${d.person.id}&q=${encodeURIComponent(d.person.name.split(' ')[0] ?? '')}&group=documents`}>
            Search their documents
          </Link>
        </div>
      </header>
      <div className="kpis kpis--5">
        <KpiTile label="Contracts" value={d.counts.total} sub={`${d.counts.active} active`} />
        <KpiTile label="As owner" value={d.counts.owner} onClick={() => setRole('Owner')} active={role === 'Owner'} />
        <KpiTile label="As delegate" value={d.counts.delegate} onClick={() => setRole('Delegate')} active={role === 'Delegate'} />
        <KpiTile label="As responsible" value={d.counts.responsible} onClick={() => setRole('Responsible')} active={role === 'Responsible'} />
        <KpiTile label="Annual value" value={dkk(d.annualValueDkk)} sub="Active contracts" />
      </div>
      <Card clip className="register-card">
        <div className="card-list-head card-list-head--row">
          <h2 className="card-title">{role === 'all' ? 'All roles' : `As ${role.toLowerCase()}`}</h2>
          <div className="spacer" />
          <Segmented ariaLabel="Role" size="sm" options={[{ value: 'all', label: 'All' }, { value: 'Owner', label: 'Owner' }, { value: 'Delegate', label: 'Delegate' }, { value: 'Responsible', label: 'Responsible' }]} value={role} onChange={setRole} />
        </div>
        {rows.length === 0 ? <EmptyState compact title="No contracts you can see" text="Only contracts you have access to are listed." /> : <Table fixed caption={`Contracts of ${d.person.name}`} columns={columns} rows={rows} rowKey={(r) => r.id} />}
      </Card>
      <p className="caption">Only contracts you have access to are listed.</p>
    </div>
  );
}

/** A counterparty: all its contracts (visible to the caller), value, renewals and documents. */
export function CounterpartyPage() {
  const { id = '' } = useParams();
  const api = useApi();
  const config = useAppConfig();
  const lookups = useLookups();
  const toast = useToast();
  const budget = useColumnBudget();
  const q = useQuery({ queryKey: qk.counterparty(id), queryFn: () => api.getCounterparty(id) });
  const open = useMutation({
    mutationFn: (docId: string) => api.getDocumentContent(docId),
    onSuccess: (f) => saveBlob(f.blob, f.fileName),
    onError: (e) => toast.error(e),
  });

  if (q.isLoading) {
    return (
      <div className="page" style={{ paddingTop: 10 }} role="status" aria-label="Loading">
        <Skeleton width={280} height={30} />
        <SkeletonRows rows={6} />
      </div>
    );
  }
  if (q.isError || !q.data) {
    return (
      <div className="page rise" style={{ paddingTop: 10 }}>
        <Back />
        <Card>{notFound(q.error) ? <EmptyState icon="building" title="This counterparty was not found" /> : <ErrorState onRetry={() => void q.refetch()} />}</Card>
      </div>
    );
  }
  const d = q.data;
  const cp = d.counterparty;
  const columns = contractColumns(defaultColumns(budget).filter((k) => k !== 'counterparty'), { customFields: config.customFields, lookups }) as Column<ContractListItem>[];

  return (
    <div className="page rise" style={{ paddingTop: 6 }}>
      <Back />
      <header className="record-head">
        <span className="tile tile--xl" aria-hidden="true">
          <Icon name="building" size={26} />
        </span>
        <div className="record-head__text">
          <div className="row-flex" style={{ gap: 10 }}>
            <h1 className="record-title">{cp.name}</h1>
            <Pill tone="gray">{cp.kind}</Pill>
            {!cp.isActive ? <Pill tone="amber">Inactive</Pill> : null}
          </div>
          <span className="page-sub">{[cp.country, cp.city, cp.vatNo && `VAT ${cp.vatNo}`, cp.externalId && `ERP ${cp.externalId}`].filter(Boolean).join(' · ') || 'No master data'}</span>
        </div>
        <div className="row-flex record-actions">
          <Link className="btn btn--secondary" to={`/overview?counterparty=${cp.id}`}>
            Open in Overview
          </Link>
        </div>
      </header>
      <div className="kpis kpis--5">
        <KpiTile label="Contracts" value={d.summary.contracts} sub={`${d.summary.live} live`} />
        <KpiTile label="Annual value" value={dkk(d.summary.annualValueDkk)} sub="Live contracts" />
        <KpiTile label="Total value" value={dkk(d.summary.totalValueDkk)} />
        <KpiTile label="Auto-renewing" value={d.summary.autoRenewing} />
        <KpiTile label="Next notice deadline" value={d.summary.nextNoticeDeadline ? formatDate(d.summary.nextNoticeDeadline) : '–'} />
      </div>
      <div className="split">
        <Card clip className="split__main split__main--wide register-card">
          <div className="card-list-head card-list-head--row">
            <h2 className="card-title">Contracts</h2>
          </div>
          {d.contracts.length === 0 ? <EmptyState compact title="No contracts you can see" /> : <Table fixed caption="Contracts" columns={columns} rows={d.contracts} rowKey={(r) => r.id} />}
        </Card>
        <aside className="split__aside">
          <Card pad className="stack" style={{ gap: 10 }}>
            <h2 className="card-title">Renewals</h2>
            {d.renewals.length === 0 ? (
              <span className="secondary">No upcoming notice deadlines.</span>
            ) : (
              d.renewals.slice(0, 8).map((r) => (
                <div key={r.id} className="mini-row">
                  <Link to={`/contracts/${r.id}`} className="mini-row__a">
                    {r.title}
                  </Link>
                  <span className={`caption${r.daysToNotice < 30 ? ' fg-red' : ''}`}>
                    Notice {formatDate(r.noticeDeadline)} · {r.daysToNotice < 0 ? `${-r.daysToNotice} days overdue` : `in ${r.daysToNotice} days`}
                    {r.autoRenew ? ' · auto-renews' : ''}
                  </span>
                </div>
              ))
            )}
          </Card>
          <Card pad className="stack" style={{ gap: 8 }}>
            <h2 className="card-title">Documents</h2>
            {d.documents.length === 0 ? (
              <span className="secondary">No documents.</span>
            ) : (
              d.documents.slice(0, 12).map((doc) => (
                <div key={doc.documentId} className="doc-row">
                  <div className="kv" style={{ minWidth: 0, flex: '1 1 auto' }}>
                    <button type="button" className="link-btn cell-clip" style={{ textAlign: 'left' }} onClick={() => open.mutate(doc.documentId)} title={`Download ${doc.fileName}`}>
                      {doc.fileName}
                    </button>
                    <span className="kv__k">
                      {doc.contractTitle} · {formatFileSize(doc.size)}
                    </span>
                  </div>
                </div>
              ))
            )}
            {d.documents.length > 12 ? <Link to={`/search?q=${encodeURIComponent(cp.name.split(' ')[0] ?? '')}&group=documents`}>{plural(d.documents.length - 12, 'more document')}</Link> : null}
          </Card>
        </aside>
      </div>
    </div>
  );
}
