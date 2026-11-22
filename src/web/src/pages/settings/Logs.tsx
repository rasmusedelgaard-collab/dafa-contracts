import { useEffect, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { qk, useApi } from '../../app/context';
import type { AuditEvent } from '../../api/types';
import { Button, Card, EmptyState, ErrorState, Icon, Pill, SkeletonRows, Table, type Column } from '../../components';
import { formatDateTime } from '../../lib/dates';
import { plural } from '../../lib/format';
import { SectionHead } from './common';

/** "ApprovalDecision" -> "Approval decision" */
const actionLabel = (action: string) => action.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^(.)(.*)$/, (_m, a: string, rest: string) => a + rest.toLowerCase());

function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setV(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return v;
}

/** Search box that picks one contract to filter the audit log by. */
function ContractFilter({ onPick }: { onPick: (id: string, title: string) => void }) {
  const api = useApi();
  const [text, setText] = useState('');
  const q = useDebounced(text.trim(), 250);
  const results = useQuery({ queryKey: qk.search(q), queryFn: () => api.search(q), enabled: q.length >= 2 });
  const contracts = (results.data ?? []).filter((r) => r.kind === 'Contract');
  return (
    <div className="audit-filter">
      <label className="search-pill">
        <span className="sr-only">Filter by contract</span>
        <Icon name="search" size={15} stroke={2.2} />
        <input type="search" placeholder="Filter by contract" value={text} onChange={(e) => setText(e.target.value)} />
      </label>
      {q.length >= 2 ? (
        <ul className="audit-filter__results" aria-label="Matching contracts">
          {results.isLoading ? (
            <li className="secondary">Searching…</li>
          ) : contracts.length === 0 ? (
            <li className="secondary">No contract matches "{q}"</li>
          ) : (
            contracts.map((c) => (
              <li key={c.id}>
                <button
                  type="button"
                  className="link-btn link-btn--text"
                  onClick={() => {
                    onPick(c.id, c.title);
                    setText('');
                  }}
                >
                  {c.title} <span className="caption">· {c.subtitle}</span>
                </button>
              </li>
            ))
          )}
        </ul>
      ) : null}
    </div>
  );
}

export function AuditLog() {
  const api = useApi();
  const [params, setParams] = useSearchParams();
  const contractId = params.get('contractId') ?? '';
  const page = Math.max(1, Number(params.get('page')) || 1);
  const [pickedTitle, setPickedTitle] = useState<string | null>(null);

  const query = { contractId: contractId || undefined, page };
  const audit = useQuery({ queryKey: qk.admin('audit', query), queryFn: () => api.admin.getAudit(query), placeholderData: keepPreviousData });
  const data = audit.data;
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const title = pickedTitle ?? data?.items.find((i) => i.contractId === contractId)?.contractTitle ?? 'one contract';

  const go = (next: { contractId?: string | null; page?: number }) => {
    const sp = new URLSearchParams();
    const id = next.contractId === undefined ? contractId : next.contractId;
    if (id) sp.set('contractId', id);
    if (next.page && next.page > 1) sp.set('page', String(next.page));
    setParams(sp, { replace: true });
  };

  const columns: Column<AuditEvent>[] = [
    { key: 'at', header: 'When', render: (e) => <span style={{ whiteSpace: 'nowrap' }}>{formatDateTime(e.at)}</span> },
    { key: 'who', header: 'Who', className: 'col-hide-sm', render: (e) => e.who },
    {
      key: 'what',
      header: 'What',
      render: (e) => (
        <span className="kv">
          <span>{e.text}</span>
          <span className="kv__k">
            {actionLabel(e.action)}
            <span className="only-sm"> · {e.who}</span>
          </span>
        </span>
      ),
    },
    { key: 'contract', header: 'Contract', render: (e) => (e.contractId ? <Link to={`/contracts/${e.contractId}`}>{e.contractTitle ?? 'Open contract'}</Link> : <span className="secondary">–</span>) },
  ];

  return (
    <>
      <SectionHead title="Audit log" text="Every create, change, decision, approval, access grant and file download, newest first. The log cannot be edited." />
      <div className="toolbar">
        {contractId ? (
          <span className="filter-tag">
            Contract: {title}
            <button type="button" className="filter-tag__x" aria-label="Clear the contract filter" onClick={() => { setPickedTitle(null); go({ contractId: null, page: 1 }); }}>
              ×
            </button>
          </span>
        ) : (
          <ContractFilter
            onPick={(id, t) => {
              setPickedTitle(t);
              go({ contractId: id, page: 1 });
            }}
          />
        )}
      </div>
      <Card clip>
        {audit.isLoading ? (
          <SkeletonRows rows={8} label="Loading the audit log" />
        ) : audit.isError || !data ? (
          <ErrorState text="The audit log could not be loaded." onRetry={() => void audit.refetch()} />
        ) : data.items.length === 0 ? (
          <EmptyState compact title="No events" text={contractId ? 'Nothing has been logged for this contract.' : 'Nothing has been logged yet.'} />
        ) : (
          <>
            <div style={{ opacity: audit.isPlaceholderData ? 0.6 : 1 }} aria-busy={audit.isPlaceholderData}>
              <Table caption="Audit log" columns={columns} rows={data.items} rowKey={(e) => e.id} />
            </div>
            <div className="pager">
              <span aria-live="polite">
                {(data.page - 1) * data.pageSize + 1}–{Math.min(data.page * data.pageSize, data.total)} of {plural(data.total, 'event')}
              </span>
              <div className="spacer" />
              <Button variant="secondary" size="sm" icon="chevronLeft" disabled={page <= 1} onClick={() => go({ page: page - 1 })}>
                Previous
              </Button>
              <Button variant="secondary" size="sm" disabled={page >= pages} onClick={() => go({ page: page + 1 })}>
                Next
              </Button>
            </div>
          </>
        )}
      </Card>
    </>
  );
}

export function Outbox() {
  const api = useApi();
  const outbox = useQuery({ queryKey: qk.admin('outbox'), queryFn: () => api.admin.getOutbox() });
  const list = outbox.data ?? [];
  return (
    <>
      <SectionHead title="Notifications outbox" text="Messages the app has sent. On the test site nothing leaves the app: mail and Teams messages are only stored here." />
      <Card clip>
        {outbox.isLoading ? (
          <SkeletonRows rows={5} label="Loading the outbox" />
        ) : outbox.isError ? (
          <ErrorState text="The outbox could not be loaded." onRetry={() => void outbox.refetch()} />
        ) : list.length === 0 ? (
          <EmptyState compact title="No messages yet" text='Reminders, escalations and approval messages show up here. Use "Run jobs now" to create the reminders that are due.' />
        ) : (
          <ul aria-label="Sent messages">
            {list.map((m, i) => (
              <li key={m.id} className="outbox-row" style={i === 0 ? { borderTop: 0 } : undefined}>
                <div className="outbox-row__top">
                  <span className="kv__v outbox-row__subject">{m.subject}</span>
                  <Pill tone={m.channel === 'Teams' ? 'blue' : 'gray'}>{m.channel}</Pill>
                </div>
                <span className="caption">
                  To {m.to} · {formatDateTime(m.createdAt)}
                  {m.contractId ? (
                    <>
                      {' · '}
                      <Link to={`/contracts/${m.contractId}`}>Open contract</Link>
                    </>
                  ) : null}
                </span>
                <p className="outbox-row__body">{m.body}</p>
              </li>
            ))}
          </ul>
        )}
      </Card>
      {list.length > 0 ? <p className="caption">{plural(list.length, 'message')}, newest first.</p> : null}
    </>
  );
}
