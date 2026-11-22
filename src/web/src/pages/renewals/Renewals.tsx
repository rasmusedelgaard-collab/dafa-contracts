import { useMemo, useState, type KeyboardEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiMode } from '../../api';
import { permissions, qk, useApi, useMe, useScope } from '../../app/context';
import type { RenewalItem } from '../../api/types';
import { Button, ButtonLink, Card, EmptyState, ErrorState, NoticePill, Segmented, Skeleton } from '../../components';
import { RenewalDialog } from '../../features/RenewalDialog';
import { addDays, formatDate, monthShort, noticeLabel, todayIso } from '../../lib/dates';
import { formatMoneyCompact, plural } from '../../lib/format';
import { buildLanes, isOverdue, markerUrgency, onAxis, timelineAxis, type LaneGrouping, type Marker } from '../../lib/timeline';
import { ScopeChip } from '../../shell/ScopeChip';

// One extra month is requested so the last column of the axis (which starts on the 1st of this month) is complete.
const MONTHS = 12;
/** Lane height for one row of markers, and the step for each extra row of overlapping markers (px). */
const LANE = 44;
const ROW_STEP = 22;

const renewalText = (i: RenewalItem) => (i.autoRenew ? 'auto-renews' : 'fixed term');

function Timeline({ items, today, grouping, selectedId, onSelect }: { items: RenewalItem[]; today: string; grouping: LaneGrouping; selectedId: string | null; onSelect: (id: string) => void }) {
  const axis = useMemo(() => timelineAxis(today, MONTHS), [today]);
  const lanes = useMemo(() => buildLanes(items, axis, grouping), [items, axis, grouping]);
  // Arrow keys walk the markers in date order, across lanes.
  const order = useMemo(() => lanes.flatMap((l) => l.markers).sort((a, b) => a.item.noticeDeadline.localeCompare(b.item.noticeDeadline) || a.item.title.localeCompare(b.item.title)), [lanes]);

  const onKeyDown = (e: KeyboardEvent<HTMLButtonElement>, m: Marker) => {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) return;
    e.preventDefault();
    const i = order.findIndex((x) => x.item.id === m.item.id);
    const next = e.key === 'Home' ? 0 : e.key === 'End' ? order.length - 1 : Math.min(order.length - 1, Math.max(0, i + (e.key === 'ArrowRight' ? 1 : -1)));
    const target = order[next];
    if (!target) return;
    onSelect(target.item.id);
    e.currentTarget.closest('.timeline')?.querySelector<HTMLElement>(`[data-marker="${target.item.id}"]`)?.focus();
  };

  // Overdue deadlines get their own narrow column at the left, so they do not sit on a border.
  const hasOverdue = order.some((m) => isOverdue(m.item));

  return (
    <Card className="timeline-card">
      <div className={`timeline${hasOverdue ? ' timeline--overdue' : ''}`} role="group" aria-label={`Notice deadlines by ${grouping === 'entity' ? 'legal entity' : 'department'}. Use the left and right arrow keys to move between contracts.`}>
        <div className="timeline__months" aria-hidden="true">
          <span />
          {hasOverdue ? <span className="timeline__month timeline__month--overdue">Overdue</span> : null}
          {axis.months.map((m) => (
            <span key={m} className="timeline__month">
              {monthShort(m)}
            </span>
          ))}
        </div>
        {lanes.map((lane) => (
          <div key={lane.key} className="timeline__lane" role="group" aria-label={`${lane.name}, ${plural(lane.markers.length, 'deadline')}`}>
            <span className="timeline__name">{lane.name}</span>
            <div className="timeline__track" style={{ height: LANE + (lane.rows - 1) * ROW_STEP }}>
              <div className="timeline__plot" style={{ backgroundSize: `${100 / MONTHS}% 100%` }}>
                <span className="timeline__today" style={{ left: `${axis.todayPct}%` }} aria-hidden="true" />
              </div>
              {lane.markers.map((m) => (
                <button
                  key={m.item.id}
                  type="button"
                  className={`marker marker--${m.urgency}${isOverdue(m.item) ? ' marker--overdue' : ''}`}
                  data-marker={m.item.id}
                  style={{ ['--pct' as string]: m.leftPct / 100, top: 8 + m.row * ROW_STEP }}
                  aria-pressed={m.item.id === selectedId}
                  // Roving tabindex: one Tab stop for the timeline; the arrow keys move between markers.
                  tabIndex={m.item.id === selectedId ? 0 : -1}
                  aria-label={`${m.item.title}, notice by ${formatDate(m.item.noticeDeadline)}, ${noticeLabel(m.item.daysToNotice).toLowerCase()}`}
                  title={`${m.item.title} · ${formatDate(m.item.noticeDeadline)}`}
                  onClick={() => onSelect(m.item.id)}
                  onKeyDown={(e) => onKeyDown(e, m)}
                >
                  <span className="marker__dot" aria-hidden="true" />
                </button>
              ))}
            </div>
          </div>
        ))}
      </div>
    </Card>
  );
}

/**
 * The contracts the signed-in user may record a renewal decision for: an administrator all of
 * them, others the active contracts they are owner, delegate or responsible on (`view=mine`).
 * `renewals` rows do not carry `canDecideRenewal`, so this decides which rows offer the action;
 * the API still checks.
 */
function useDecidable(): (id: string) => boolean {
  const api = useApi();
  const me = useMe();
  const isAdmin = permissions(me).isAdmin;
  const query = { view: 'mine' as const, status: 'Active' as const, pageSize: 200 };
  const mine = useQuery({ queryKey: qk.contractList(query), queryFn: () => api.getContracts(query), enabled: !isAdmin });
  const ids = useMemo(() => new Set((mine.data?.items ?? []).map((i) => i.id)), [mine.data]);
  return (id) => isAdmin || ids.has(id);
}

export function Renewals() {
  const api = useApi();
  const scope = useScope();
  const canDecide = useDecidable();
  const [grouping, setGrouping] = useState<LaneGrouping>('entity');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [decideFor, setDecideFor] = useState<string | null>(null);

  const renewals = useQuery({ queryKey: [...qk.renewals(MONTHS + 1), scope.entityIds], queryFn: () => api.getRenewals(MONTHS + 1, scope.entityIds) });

  // "Today" as the API sees it, so the axis and the day counts agree.
  const first = renewals.data?.[0];
  const today = first ? addDays(first.noticeDeadline, -first.daysToNotice) : todayIso();
  const items = useMemo(() => {
    // Release 4: the API applies the entity scope (`entityId`).
    return onAxis(renewals.data ?? [], timelineAxis(today, MONTHS)).sort((a, b) => a.noticeDeadline.localeCompare(b.noticeDeadline));
  }, [renewals.data, today]);

  const selected = items.find((i) => i.id === selectedId) ?? items[0] ?? null;
  const overdue = items.filter(isOverdue).length;
  const within30 = items.filter((i) => !isOverdue(i) && i.daysToNotice <= 30).length;
  const within90 = items.filter((i) => !isOverdue(i) && i.daysToNotice <= 90).length;

  return (
    <div className="page rise renewals" style={{ paddingTop: 10 }}>
      <div className="page-head">
        <div className="page-head__text">
          <h1 className="h1">Renewals</h1>
          <span className="page-sub">Notice deadlines for the next 12 months, by {grouping === 'entity' ? 'legal entity' : 'department'}</span>
        </div>
        <div className="spacer" />
        {renewals.data ? (
          <>
            {overdue > 0 ? <span className="count-pill tone-red">{overdue} overdue</span> : null}
            {within30 > 0 ? <span className="count-pill tone-red">{within30} within 30 days</span> : null}
            {within90 > 0 ? <span className="count-pill tone-amber">{within90} within 90 days</span> : null}
          </>
        ) : null}
      </div>
      <ScopeChip />

      {renewals.isLoading ? (
        <Card pad role="status" aria-label="Loading renewals" className="stack" style={{ gap: 14 }}>
          <Skeleton height={18} width="40%" />
          <Skeleton height={220} radius={14} />
        </Card>
      ) : renewals.isError ? (
        <Card>
          <ErrorState text="The renewals could not be loaded." onRetry={() => void renewals.refetch()} />
        </Card>
      ) : items.length === 0 || !selected ? (
        <Card>
          <EmptyState
            icon="calendar"
            title="No notice deadlines in the next 12 months"
            text={scope.entityIds.length ? 'No active contract in the selected legal entity has a notice deadline in this period. Choose "All entities" in the top bar to see the rest.' : 'Active contracts with an end date and a notice period show up here when their deadline comes within 12 months.'}
            action={
              <ButtonLink to="/contracts" variant="secondary" size="md">
                Browse contracts
              </ButtonLink>
            }
          />
        </Card>
      ) : (
        <>
          <div className="toolbar renewals__tools">
            <Segmented
              size="sm"
              ariaLabel="Group the timeline by"
              value={grouping}
              onChange={setGrouping}
              options={[
                { value: 'entity', label: 'By legal entity' },
                { value: 'department', label: 'By department' },
              ]}
            />
            <div className="legend" aria-label="Marker colors">
              <span className="legend__item legend__item--red">Overdue or within 30 days</span>
              <span className="legend__item legend__item--amber">Within 90 days</span>
              <span className="legend__item legend__item--blue">Later</span>
              <span className="legend__item legend__item--today">Today</span>
            </div>
          </div>

          <Timeline items={items} today={today} grouping={grouping} selectedId={selected.id} onSelect={setSelectedId} />

          <section className="selected-card" aria-label="Selected contract" aria-live="polite">
            <div className="selected-card__days">
              <span className="selected-card__n">{Math.abs(selected.daysToNotice)}</span>
              <span className="selected-card__unit">
                {Math.abs(selected.daysToNotice) === 1 ? 'day' : 'days'}
                {isOverdue(selected) ? ' overdue' : ''}
              </span>
            </div>
            <div className="selected-card__text">
              <span className="selected-card__meta">
                {isOverdue(selected) ? 'Notice deadline passed on' : 'Notice by'} {formatDate(selected.noticeDeadline)} · {selected.entityName}
              </span>
              <span className="selected-card__title">{selected.title}</span>
              <span className="selected-card__sub">
                {selected.departmentName} · {renewalText(selected)}
                {selected.annualValue !== null ? ` · ${formatMoneyCompact(selected.annualValue, selected.currency)} a year` : ''}
              </span>
            </div>
            <div className="selected-card__actions">
              {canDecide(selected.id) ? (
                <Button variant="on-dark-ghost" onClick={() => setDecideFor(selected.id)}>
                  Record decision
                </Button>
              ) : null}
              <ButtonLink variant="on-dark" to={`/contracts/${selected.id}`}>
                Open contract
              </ButtonLink>
            </div>
          </section>

          <section aria-labelledby="renewals-list-heading" className="stack" style={{ gap: 12 }}>
            <h2 className="h2" id="renewals-list-heading">
              All {items.length} in date order
            </h2>
            <Card clip className="rlist-card">
              <ul className="rlist">
                {items.map((i) => (
                  <li key={i.id} className={`rlist__row rlist__row--${markerUrgency(i.daysToNotice)}`} aria-current={i.id === selected.id ? 'true' : undefined}>
                    <div className="rlist__main">
                      <span className="rlist__label">{isOverdue(i) ? `Notice deadline ${noticeLabel(i.daysToNotice)}` : `Notice ${noticeLabel(i.daysToNotice).toLowerCase()}`}</span>
                      <Link to={`/contracts/${i.id}`} className="rlist__title">
                        {i.title}
                      </Link>
                      <span className="caption">
                        {i.entityName} · {i.departmentName} · {renewalText(i)}
                      </span>
                    </div>
                    <span className="rlist__date">
                      <span className="sr-only">Notice by </span>
                      {formatDate(i.noticeDeadline)}
                    </span>
                    <span className="rlist__pill">
                      <NoticePill days={i.daysToNotice} />
                    </span>
                    <span className="rlist__value">{formatMoneyCompact(i.annualValue, i.currency)}</span>
                    {canDecide(i.id) ? (
                      <Button size="sm" variant="secondary" className="rlist__btn" onClick={() => setDecideFor(i.id)} aria-label={`Decide on renewal: ${i.title}`}>
                        <span className="rlist__btn-short">Decide</span>
                        <span className="rlist__btn-long">Decide on renewal</span>
                      </Button>
                    ) : (
                      <ButtonLink size="sm" variant="secondary" className="rlist__btn rlist__btn--open" to={`/contracts/${i.id}`} aria-label={`Open contract: ${i.title}`}>
                        <span className="rlist__btn-short">Open</span>
                        <span className="rlist__btn-long">Open contract</span>
                      </ButtonLink>
                    )}
                  </li>
                ))}
              </ul>
            </Card>
          </section>
        </>
      )}
      {apiMode === 'mock' ? <p className="caption">Sample data.<span className="hide-phone"> Select a marker to see the contract.</span></p> : null}

      {decideFor ? <RenewalDialog contractId={decideFor} onClose={() => setDecideFor(null)} /> : null}
    </div>
  );
}
