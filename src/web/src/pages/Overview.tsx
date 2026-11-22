import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { ApiError } from '../api/api';
import { apiMode } from '../api';
import { permissions, qk, useApi, useMe } from '../app/context';
import type { OverviewData } from '../api/types';
import { Avatar, Card, CardTitle, EmptyState, ErrorState, NoticePill, Skeleton } from '../components';
import { formatMoneyCompact, plural } from '../lib/format';
import { NoAccess } from './NotFound';

const NO_ACCESS_TEXT = 'Overview shows the whole portfolio and is for entity management, group management and administrators. Your contracts and tasks are on My work.';

function Kpi({ label, value, sub, to, tone }: { label: string; value: ReactNode; sub: string; to?: string; tone?: 'amber' }) {
  const body = (
    <>
      <span className="kpi__label">{label}</span>
      <span className={`kpi__value${tone ? ` fg-${tone}` : ''}`}>{value}</span>
      <span className="kpi__sub">{sub}</span>
    </>
  );
  return to ? (
    <Link to={to} className="kpi lift">
      {body}
    </Link>
  ) : (
    <div className="kpi">{body}</div>
  );
}

/**
 * Horizontal bar chart in plain HTML: a list where every row carries its label and value as
 * text, so it reads correctly without the bars (which are decorative).
 */
function BarList({ rows, label, empty }: { rows: { name: string; value: number; text: string }[]; label: string; empty: string }) {
  if (rows.length === 0) return <span className="secondary">{empty}</span>;
  const max = Math.max(1, ...rows.map((r) => r.value));
  return (
    <ol className="hbars" aria-label={label}>
      {rows.map((r) => (
        <li key={r.name} className="hbar">
          <span className="hbar__name">{r.name}</span>
          <span className="hbar__track" aria-hidden="true">
            <span className="hbar__fill" style={{ width: `${Math.max(r.value > 0 ? 2 : 0, (r.value / max) * 100)}%` }} />
          </span>
          <span className="hbar__value">{r.text}</span>
        </li>
      ))}
    </ol>
  );
}

function Content({ data }: { data: OverviewData }) {
  return (
    <>
      <div className="kpis">
        <Kpi label="Active contracts" value={data.activeContracts} sub={`Across ${plural(data.valueByEntity.length, 'legal entity', 'legal entities')}`} to="/contracts?status=Active" />
        <Kpi label="Annual contract value" value={formatMoneyCompact(data.annualValueDkk, 'DKK')} sub="Active contracts, converted to DKK" />
        <Kpi label="Notice deadline within 90 days" value={data.dueIn90} sub="Open the list" to="/contracts?view=due90" />
        <Kpi label="In approval" value={data.inApproval} sub="Open the list" to="/contracts?view=approval" />
        <Kpi label="Missing data" value={data.missingData} sub={data.missingData === 0 ? 'Every contract is complete' : 'Contracts to complete'} to="/contracts?view=missing" tone={data.missingData > 0 ? 'amber' : undefined} />
      </div>

      <div className="split">
        <Card pad className="split__main stack" style={{ gap: 16 }}>
          <CardTitle>Annual contract value by legal entity</CardTitle>
          <BarList label="Annual contract value by legal entity, in DKK" empty="No active contracts with a value yet." rows={data.valueByEntity.map((e) => ({ name: e.name, value: e.valueDkk, text: formatMoneyCompact(e.valueDkk, 'DKK') }))} />
          <span className="caption">Active contracts and contracts with notice given. Other currencies are converted with indicative rates.</span>
        </Card>
        <Card pad className="split__aside stack" style={{ gap: 16 }}>
          <CardTitle>Contracts by type</CardTitle>
          <BarList label="Active contracts by type" empty="No active contracts yet." rows={data.countByType.map((t) => ({ name: t.name, value: t.count, text: String(t.count) }))} />
        </Card>
      </div>

      <div className="split">
        <Card className="split__main" style={{ gap: 0 }} clip>
          <div className="card-list-head">
            <h2 className="card-title">Expiring without renewal</h2>
            <span className="caption">Fixed-term contracts that end within 12 months and have no renewal decision</span>
          </div>
          {data.expiringNoRenewal.length === 0 ? (
            <EmptyState compact title="Nothing expires unnoticed" text="Every fixed-term contract ending within 12 months has a renewal decision." />
          ) : (
            <ul>
              {data.expiringNoRenewal.map((c) => (
                <li key={c.id} className="list-row list-row--wrap">
                  <Avatar initials={c.ownerInitials} size="sm" />
                  <span className="kv" style={{ flex: '1 1 220px' }}>
                    <Link to={`/contracts/${c.id}`} className="kv__v">
                      {c.title}
                    </Link>
                    <span className="kv__k">
                      {c.counterparty} · {c.entityName} · owner {c.ownerName}
                    </span>
                  </span>
                  <span className="secondary">{formatMoneyCompact(c.annualValue, c.currency)}</span>
                  <NoticePill days={c.daysToNotice} />
                </li>
              ))}
            </ul>
          )}
        </Card>
        <Card className="split__aside" style={{ gap: 0 }} clip>
          <div className="card-list-head">
            <h2 className="card-title">Change of control</h2>
            <span className="caption">{data.changeOfControl.length === 0 ? 'For due diligence' : `${plural(data.changeOfControl.length, 'active contract')} with a change-of-control clause, for due diligence`}</span>
          </div>
          {data.changeOfControl.length === 0 ? (
            <EmptyState compact title="No change-of-control clauses" text="No active contract is marked with a change-of-control clause." />
          ) : (
            <ul>
              {data.changeOfControl.map((c) => (
                <li key={c.id} className="list-row">
                  <span className="kv">
                    <Link to={`/contracts/${c.id}`} className="kv__v">
                      {c.title}
                    </Link>
                    <span className="kv__k">{c.entityName}</span>
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Card>
      </div>
    </>
  );
}

export function Overview() {
  const api = useApi();
  const me = useMe();
  const allowed = permissions(me).canSeeOverview;
  const overview = useQuery({
    queryKey: qk.overview,
    queryFn: () => api.getOverview(),
    enabled: allowed,
    retry: (count, e) => !(e instanceof ApiError && e.status === 403) && count < 2,
  });

  // The API decides; the menu item is hidden for the same users.
  if (!allowed || (overview.error instanceof ApiError && overview.error.status === 403)) return <NoAccess title="You do not have access to Overview" text={NO_ACCESS_TEXT} />;

  return (
    <div className="page rise" style={{ paddingTop: 10 }}>
      <div className="page-head">
        <div className="page-head__text">
          <h1 className="h1">Overview</h1>
          <span className="page-sub">{me.isGroupManagement || me.roles.includes('Admin') ? 'The contract portfolio across all legal entities' : 'The contract portfolio across the entities you manage and the contracts you can see'}</span>
        </div>
      </div>
      {overview.isLoading ? (
        <div role="status" aria-label="Loading overview" className="stack" style={{ gap: 16 }}>
          <div className="kpis">
            {Array.from({ length: 5 }, (_, i) => (
              <Skeleton key={i} height={112} radius={18} />
            ))}
          </div>
          <Skeleton height={260} radius={18} />
        </div>
      ) : overview.isError || !overview.data ? (
        <Card>
          <ErrorState text="The overview could not be loaded." onRetry={() => void overview.refetch()} />
        </Card>
      ) : (
        <Content data={overview.data} />
      )}
      {apiMode === 'mock' ? <p className="caption">Sample data.</p> : null}
    </div>
  );
}
