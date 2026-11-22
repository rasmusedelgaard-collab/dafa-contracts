import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useOutletContext } from 'react-router-dom';
import { useBodyFlag } from '../app/bodyFlag';
import { apiMode } from '../api';
import { permissions, qk, useApi, useInvalidateContracts, useMe } from '../app/context';
import type { HomeData, HomeTask, Urgency } from '../api/types';
import { Avatar, Button, ButtonLink, Card, CardTitle, EmptyState, ErrorState, Icon, NoticePill, Pill, Segmented, Skeleton, SkeletonRows, useToast, type IconName } from '../components';
import { ApprovalDialog } from '../features/Approval';
import { RenewalDialog } from '../features/RenewalDialog';
import { formatWeekdayLong, monthShort, todayIso } from '../lib/dates';
import { formatMoneyCompact, plural, type Tone } from '../lib/format';
import { usePhone } from '../lib/media';

type Filter = 'all' | 'renewals' | 'approvals';

const URGENCY_TONE: Record<Urgency, Tone> = { high: 'red', medium: 'amber', low: 'blue', info: 'gray' };
const TASK_ICON: Record<HomeTask['kind'], IconName> = { Renewal: 'refresh', Approval: 'check', Obligation: 'clock', MissingData: 'alert' };
const ACTION: Record<HomeTask['kind'], string> = { Renewal: 'Decide', Approval: 'Review', Obligation: 'Mark done', MissingData: 'Complete' };
/** The full-width button on a phone task card (Phone.dc.html). */
const ACTION_LONG: Record<HomeTask['kind'], string> = { Renewal: 'Decide on renewal', Approval: 'Review and approve', Obligation: 'Mark as done', MissingData: 'Complete the data' };

const taskTone = (t: HomeTask): Tone => (t.kind === 'Approval' ? 'blue' : URGENCY_TONE[t.urgency]);
const taskKey = (t: HomeTask) => `${t.kind}-${t.contractId ?? ''}-${t.approvalStepId ?? ''}-${t.obligationId ?? ''}`;

function greeting(now = new Date()): string {
  const h = now.getHours();
  return h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
}

function heroText(home: HomeData): { title: string; text: string } {
  const n = home.decisionsThisWeek;
  if (n === 0) {
    return {
      title: 'Nothing needs a decision from you',
      text: home.tasks.length > 0 ? `${plural(home.tasks.length, 'task')} on your list, none of them urgent.` : 'No renewals close to their notice deadline and no approvals waiting.',
    };
  }
  const parts: string[] = [];
  if (home.renewalsThisWeek > 0) {
    const first = home.tasks.find((t) => t.kind === 'Renewal' && t.urgency === 'high');
    parts.push(home.renewalsThisWeek === 1 && first ? `one renewal (${first.pill.toLowerCase()})` : `${home.renewalsThisWeek} renewals within 30 days of the notice deadline`);
  }
  if (home.approvalsWaiting > 0) parts.push(home.approvalsWaiting === 1 ? 'one approval waiting for you' : `${home.approvalsWaiting} approvals waiting for you`);
  const sentence = parts.join(', and ');
  return { title: `${n} ${n === 1 ? 'decision needs' : 'decisions need'} you`, text: `${sentence.charAt(0).toUpperCase()}${sentence.slice(1)}.` };
}

/** The phone hero has one short line: "1 renewal · 1 approval". */
function heroShort(home: HomeData): string {
  const parts: string[] = [];
  if (home.renewalsThisWeek > 0) parts.push(plural(home.renewalsThisWeek, 'renewal'));
  if (home.approvalsWaiting > 0) parts.push(plural(home.approvalsWaiting, 'approval'));
  if (parts.length > 0) return parts.join(' · ');
  return home.tasks.length > 0 ? `${plural(home.tasks.length, 'task')}, none urgent` : 'Nothing is waiting';
}

function NoticeChart({ data }: { data: HomeData['noticeByMonth'] }) {
  const max = Math.max(1, ...data.map((d) => d.count));
  return (
    <ol className="bars" aria-label="Notice deadlines per month">
      {data.map((d, i) => (
        <li key={d.month} className="bar">
          <span className="bar__n">
            {d.count}
            <span className="sr-only"> in </span>
          </span>
          <span aria-hidden="true" className={`bar__fill ${d.count === 0 ? 'bar__fill--zero' : i === 0 ? 'bar__fill--now' : i <= 2 ? 'bar__fill--soon' : ''}`} style={{ height: d.count === 0 ? 4 : `${Math.max(8, (d.count / max) * 96)}px` }} />
          <span className="bar__m">{monthShort(d.month)}</span>
        </li>
      ))}
    </ol>
  );
}

/** Contracts the user watches (release 3): nearest notice first, with the countdown. */
function WatchingCard() {
  const api = useApi();
  const q = useQuery({ queryKey: qk.watching, queryFn: () => api.getWatching(), retry: false });
  if (q.isError) return null;
  return (
    <Card pad className="stack" style={{ gap: 10 }}>
      <CardTitle>Watching</CardTitle>
      {!q.data ? (
        <Skeleton width="70%" />
      ) : q.data.length === 0 ? (
        <span className="secondary">Press Watch on a contract to follow its changes here.</span>
      ) : (
        q.data.slice(0, 6).map((c) => (
          <div key={c.id} className="watch-row">
            <Link to={`/contracts/${c.id}`} className="watch-row__title">
              {c.title}
            </Link>
            <NoticePill days={c.daysToNotice} />
          </div>
        ))
      )}
    </Card>
  );
}

export function Home() {
  const api = useApi();
  const me = useMe();
  const navigate = useNavigate();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const perms = permissions(me);
  const [filter, setFilter] = useState<Filter>('all');
  const [renewalFor, setRenewalFor] = useState<string | null>(null);
  const [approvalFor, setApprovalFor] = useState<string | null>(null);

  const home = useQuery({ queryKey: qk.home, queryFn: () => api.getHome() });
  // Phone: the page has its own header (date, "My work", avatar) instead of the top bar.
  useBodyFlag('page-home', true);
  const phone = usePhone();
  const shell = useOutletContext<{ openUser?: () => void } | null>();

  const complete = useMutation({
    mutationFn: (obligationId: string) => api.completeObligation(obligationId),
    onSuccess: async (o) => {
      await invalidate();
      toast.show(o.status === 'Done' ? 'Obligation marked as done' : 'Marked as done. It comes back when it is next due');
    },
    onError: (e) => toast.error(e),
  });

  const act = (t: HomeTask) => {
    if (t.kind === 'Renewal' && t.contractId) setRenewalFor(t.contractId);
    else if (t.kind === 'Approval' && t.approvalStepId) setApprovalFor(t.approvalStepId);
    else if (t.kind === 'Obligation' && t.obligationId) complete.mutate(t.obligationId);
    else if (t.contractId) navigate(`/contracts/${t.contractId}`);
    else navigate('/contracts?view=missing');
  };

  const data = home.data;
  const tasks = (data?.tasks ?? []).filter((t) => filter === 'all' || (filter === 'renewals' && t.kind === 'Renewal') || (filter === 'approvals' && t.kind === 'Approval'));
  const urgent = data?.tasks.find((t) => t.kind === 'Approval' || (t.kind === 'Renewal' && t.urgency === 'high')) ?? data?.tasks[0];
  const hero = data ? heroText(data) : null;

  const quickTiles = (
      <div className="quick-tiles">
        {perms.canCreate ? (
          <>
            <Link to="/contracts/new" className="quick-tile lift">
              <span className="tile" aria-hidden="true">
                <Icon name="upload" size={18} stroke={2} />
              </span>
              Upload a contract
            </Link>
            <Link to="/contracts/new?start=template" className="quick-tile lift">
              <span className="tile" aria-hidden="true">
                <Icon name="docPlus" size={18} stroke={2} />
              </span>
              Start from a template
            </Link>
          </>
        ) : null}
        <Link to="/contracts" className="quick-tile lift">
          <span className="tile" aria-hidden="true">
            <Icon name="search" size={18} stroke={2} />
          </span>
          Find a contract
        </Link>
      </div>
  );

  return (
    <div className="page page--home rise">
      <header className="home-head">
        <div className="stack home-head__text">
          <span className="secondary">{formatWeekdayLong(todayIso())}</span>
          <h1 className="h1">
            <span className="home-head__wide">
              {greeting()}, {me.name.split(' ')[0]}
            </span>
            <span className="home-head__phone">My work</span>
          </h1>
        </div>
        <button type="button" className="home-head__avatar" onClick={() => shell?.openUser?.()} aria-haspopup="dialog" aria-label={`${me.name}. ${me.authMode === 'Dev' ? 'Switch demo user' : 'Account'}`}>
          <Avatar initials={me.initials} solid size="lg" />
        </button>
      </header>

      <section className="hero" aria-label="Summary">
        <div className="hero__main">
          <span className="eyebrow">This week</span>
          {hero && data ? (
            <>
              <span className="hero__title">{hero.title}</span>
              <span className="hero__text hero__text--wide">{hero.text}</span>
              <span className="hero__text hero__text--phone">{heroShort(data)}</span>
              {urgent && data.decisionsThisWeek > 0 ? (
                <Button variant="on-dark" className="hero__cta" style={{ alignSelf: 'flex-start', marginTop: 8 }} onClick={() => act(urgent)}>
                  Start with the most urgent
                </Button>
              ) : null}
            </>
          ) : home.isError ? (
            <span className="hero__text">Your summary could not be loaded.</span>
          ) : (
            <span role="status" aria-label="Loading summary" className="stack" style={{ gap: 10, opacity: 0.35 }}>
              <Skeleton width="60%" height={30} />
              <Skeleton width="85%" height={14} />
            </span>
          )}
        </div>
        <div className="hero__stats">
          <div className="hero-stat">
            <span className="hero-stat__label">Renewing in 90 days</span>
            <span className="hero-stat__value">{data ? formatMoneyCompact(data.renewingIn90.valueDkk, 'DKK') : '–'}</span>
            <span className="hero-stat__label">{data ? plural(data.renewingIn90.count, 'contract') : ' '}</span>
          </div>
          <div className="hero-stat">
            <span className="hero-stat__label">Your contracts</span>
            <span className="hero-stat__value">{data ? data.myContracts : '–'}</span>
            <span className="hero-stat__label">owner, delegate, responsible</span>
          </div>
        </div>
      </section>

      {phone ? null : quickTiles}

      <div className="split split--home">
        <section className="split__main" style={{ gap: 14 }} aria-labelledby="my-work-heading">
          <div className="section-head section-head--tasks">
            <h2 className="h2" id="my-work-heading">
              My work
            </h2>
            <div className="spacer" />
            <Segmented
              size="sm"
              ariaLabel="Filter tasks"
              value={filter}
              onChange={setFilter}
              options={[
                { value: 'all', label: 'All' },
                { value: 'renewals', label: 'Renewals' },
                { value: 'approvals', label: 'Approvals' },
              ]}
            />
          </div>
          <Card clip className="task-card">
            {home.isLoading ? (
              <SkeletonRows rows={5} label="Loading your tasks" />
            ) : home.isError ? (
              <ErrorState text="Your tasks could not be loaded." onRetry={() => void home.refetch()} />
            ) : tasks.length === 0 ? (
              <EmptyState
                icon="check"
                title={filter === 'all' ? 'You are all caught up' : filter === 'renewals' ? 'No renewals to decide' : 'No approvals waiting for you'}
                text={filter === 'all' ? 'Renewals, approvals and obligations that need you show up here.' : 'Switch to All to see the rest of your tasks.'}
                action={filter === 'all' ? <ButtonLink to="/contracts" variant="secondary" size="md">Browse contracts</ButtonLink> : undefined}
              />
            ) : (
              <ul>
                {tasks.map((t) => {
                  const tone = taskTone(t);
                  const busy = t.kind === 'Obligation' && complete.isPending && complete.variables === t.obligationId;
                  return (
                    <li key={taskKey(t)} className="task">
                      {/* The icon tile carries the urgency color (Home.dc.html, Phone.dc.html). */}
                      <span className={`tile tile--lg tone-${tone}`} data-tone={tone} aria-hidden="true">
                        <Icon name={TASK_ICON[t.kind]} stroke={2} />
                      </span>
                      <div className="task__text">
                        {/* Phone layout: the pill text moves above the title (Phone.dc.html). */}
                        <span className={`task__label fg-${tone}`}>
                          {t.pill}
                        </span>
                        {t.contractId ? (
                          <Link to={`/contracts/${t.contractId}`} className="task__title">
                            {t.title}
                          </Link>
                        ) : (
                          <span className="task__title">{t.title}</span>
                        )}
                        <span className="caption">{t.meta}</span>
                      </div>
                      <Pill tone={tone}>{t.pill}</Pill>
                      <Button size="sm" loading={busy} onClick={() => act(t)} aria-label={`${ACTION[t.kind]}: ${t.title}`}>
                        <span className="task__action-short">{ACTION[t.kind]}</span>
                        <span className="task__action-long" aria-hidden="true">
                          {ACTION_LONG[t.kind]}
                        </span>
                      </Button>
                    </li>
                  );
                })}
              </ul>
            )}
          </Card>
        </section>

        {/* Phone (Phone.dc.html): the tasks come first; the shortcuts follow them. */}
        {phone ? quickTiles : null}

        <aside className="split__aside">
          <Card pad className="stack" style={{ gap: 16 }}>
            <CardTitle
              action={
                <Link to="/renewals" style={{ fontSize: 13, fontWeight: 600 }}>
                  Timeline
                </Link>
              }
            >
              Notice deadlines
            </CardTitle>
            {data ? <NoticeChart data={data.noticeByMonth} /> : home.isError ? <span className="secondary">Not available.</span> : <Skeleton height={144} radius={12} />}
          </Card>
          <WatchingCard />
          <Card pad className="stack" style={{ gap: 12 }}>
            <CardTitle>Recently opened</CardTitle>
            {data ? (
              data.recent.length > 0 ? (
                <div className="plain-links">
                  {data.recent.map((r) => (
                    <Link key={r.id} to={`/contracts/${r.id}`}>
                      {r.title}
                    </Link>
                  ))}
                </div>
              ) : (
                <span className="secondary">Contracts you open show up here.</span>
              )
            ) : home.isError ? (
              <span className="secondary">Not available.</span>
            ) : (
              <span className="stack" style={{ gap: 12 }} role="status" aria-label="Loading">
                <Skeleton width="80%" />
                <Skeleton width="60%" />
                <Skeleton width="70%" />
              </span>
            )}
          </Card>
        </aside>
      </div>
      {apiMode === 'mock' ? <p className="caption">Sample data.</p> : null}

      {renewalFor ? <RenewalDialog contractId={renewalFor} onClose={() => setRenewalFor(null)} /> : null}
      {approvalFor ? <ApprovalDialog stepId={approvalFor} onClose={() => setApprovalFor(null)} /> : null}
    </div>
  );
}
