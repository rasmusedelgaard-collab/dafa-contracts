import { useMemo, useState, type KeyboardEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiMode } from '../../api';
import { qk, useApi, useScope } from '../../app/context';
import type { RenewalItem } from '../../api/types';
import { Button, ButtonLink, Card, EmptyState, ErrorState, NoticePill, Segmented, Skeleton } from '../../components';
import { RenewalDialog } from '../../features/RenewalDialog';
import { addDays, formatDate, monthShort, noticeLabel, todayIso } from '../../lib/dates';
import { formatMoneyCompact, plural } from '../../lib/format';
import { buildLanes, isOverdue, markerUrgency, onAxis, timelineAxis, type LaneGrouping, type Marker } from '../../lib/timeline';

// One extra month is requested so the last column of the axis (which starts on the 1st of this month) is complete.
const MONTHS = 12;
const ROW_STEP = 26;

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

  return (
    <Card className="timeline-card">
      <div className="timeline" role="group" aria-label={`Notice deadlines by ${grouping === 'entity' ? 'legal entity' : 'department'}. Use the left and right arrow keys to move between contracts.`}>
        <div className="timeline__months" aria-hidden="true">
          <span />
          {axis.months.map((m) => (
            <span key={m} className="timeline__month">
              {monthShort(m)}
            </span>
          ))}
        </div>
        {lanes.map((lane) => (
          <div key={lane.key} className="timeline__lane" role="group" aria-label={`${lane.name}, ${plural(lane.markers.length, 'deadline')}`}>
            <span className="timeline__name">{lane.name}</span>
            <div className="timeline__track" style={{ height: 60 + (lane.rows - 1) * ROW_STEP, backgroundSize: `${100 / MONTHS}% 100%` }}>
              <span className="timeline__today" style={{ left: `${axis.todayPct}%` }} aria-hidden="true" />
              {lane.markers.map((m) => (
                <button
                  key={m.item.id}
                  type="button"
                  className={`marker marker--${m.urgency}`}
                  data-marker={m.item.id}
                  style={{ left: `${m.leftPct}%`, top: 8 + m.row * ROW_STEP }}
                  aria-pressed={m.item.id === selectedId}
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

export function Renewals() {
  const api = useApi();
  const scope = useScope();
  const [grouping, setGrouping] = useState<LaneGrouping>('entity');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [decideFor, setDecideFor] = useState<string | null>(null);

  const renewals = useQuery({ queryKey: qk.renewals(MONTHS + 1), queryFn: () => api.getRenewals(MONTHS + 1) });

  // "Today" as the API sees it, so the axis and the day counts agree.
  const first = renewals.data?.[0];
  const today = first ? addDays(first.noticeDeadline, -first.daysToNotice) : todayIso();
  const items = useMemo(() => {
    const scoped = (renewals.data ?? []).filter((i) => !scope.entityId || i.entityId === scope.entityId);
    return onAxis(scoped, timelineAxis(today, MONTHS)).sort((a, b) => a.noticeDeadline.localeCompare(b.noticeDeadline));
  }, [renewals.data, scope.entityId, today]);

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
            text={scope.entityId ? 'No active contract in the selected legal entity has a notice deadline in this period. Choose "All entities" in the top bar to see the rest.' : 'Active contracts with an end date and a notice period show up here when their deadline comes within 12 months.'}
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
              <Button variant="on-dark-ghost" onClick={() => setDecideFor(selected.id)}>
                Record decision
              </Button>
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
                    <Button size="sm" variant="secondary" className="rlist__btn" onClick={() => setDecideFor(i.id)} aria-label={`Decide on renewal: ${i.title}`}>
                      <span className="rlist__btn-short">Decide</span>
                      <span className="rlist__btn-long">Decide on renewal</span>
                    </Button>
                  </li>
                ))}
              </ul>
            </Card>
          </section>
        </>
      )}
      {apiMode === 'mock' ? <p className="caption">Sample data. Select a marker to see the contract.</p> : null}

      {decideFor ? <RenewalDialog contractId={decideFor} onClose={() => setDecideFor(null)} /> : null}
    </div>
  );
}
