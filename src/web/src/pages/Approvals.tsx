import { useId, useRef } from 'react';
import { useBodyFlag } from '../app/bodyFlag';
import { useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { apiMode } from '../api';
import { qk, useApi, useMe, useScope } from '../app/context';
import type { ApprovalInstance, ApprovalStep, MyApproval } from '../api/types';
import { Button, ButtonLink, Card, EmptyState, ErrorState, Icon, Skeleton, SkeletonRows } from '../components';
import { Deviations, KeyTerms, stageApproverIds, useApprovalDecision, type DecisionContract } from '../features/Approval';
import { routeSummary, splitRoleLabel, stepLabel, waitInfo } from '../lib/approvals';
import { formatDate } from '../lib/dates';
import { plural } from '../lib/format';
import { ScopeChip } from '../shell/ScopeChip';
import { PersonPicker } from '../features/Pickers';

function stepState(step: ApprovalStep, mine: boolean): { text: string; look: 'done' | 'you' | 'now' | 'next' | 'stop' | 'off' } {
  switch (step.status) {
    case 'Approved':
      return { text: step.decidedAt ? `Approved ${formatDate(step.decidedAt.slice(0, 10))}` : 'Approved', look: 'done' };
    case 'Rejected':
      return { text: 'Rejected', look: 'stop' };
    case 'Delegated':
      return { text: step.comment ?? 'Delegated', look: 'off' };
    case 'Pending':
      return mine ? { text: 'Your turn', look: 'you' } : { text: 'Reviewing', look: 'now' };
    default:
      return { text: 'Next', look: 'next' };
  }
}

/** The route as cards with avatars; approvers of one stage act in parallel and sit in one group. */
function RouteCards({ instance, myStepId }: { instance: ApprovalInstance; myStepId: string }) {
  const me = useMe();
  return (
    <ol className="route-cards" aria-label="Approval route">
      {instance.stages.map((stage) => {
        const parallel = stage.steps.filter((s) => s.status !== 'Delegated').length > 1;
        return (
          <li key={stage.order} className="route-cards__stage">
            <span className="route-cards__label">
              Step {stage.order}
              {parallel ? ' · in parallel' : ''}
            </span>
            <ul className="route-cards__steps">
              {stage.steps.map((s) => {
                // "You" is the signed-in user's own step only, never another person's.
                const mine = s.id === myStepId || s.approverId === me.id;
                const state = stepState(s, s.id === myStepId);
                const { role, note } = splitRoleLabel(s.roleLabel);
                return (
                  <li key={s.id} className={`route-card route-card--${state.look}`} aria-current={s.id === myStepId ? 'step' : undefined}>
                    <span className="route-card__avatar" aria-hidden="true">
                      {s.initials}
                    </span>
                    <span className="kv">
                      <span className="route-card__who">{mine ? 'You' : s.approverName}</span>
                      <span className="kv__k">
                        {role} · {state.text}
                      </span>
                      {note ? (
                        <span className="route-card__note" title={note}>
                          {note}
                        </span>
                      ) : null}
                    </span>
                  </li>
                );
              })}
            </ul>
          </li>
        );
      })}
    </ol>
  );
}

/** Comment, Delegate, Reject and Approve. A dark bar on desktop; on a phone the two main buttons stick to the bottom. */
function DecisionBar({ stepId, onDone, contract }: { stepId: string; onDone: () => void; contract: DecisionContract }) {
  const d = useApprovalDecision(stepId, onDone, contract);
  const id = useId();
  const commentRef = useRef<HTMLInputElement>(null);
  const reject = () => {
    if (!d.reject()) commentRef.current?.focus();
  };
  return (
    <section className="decide" aria-label="Your decision">
      <div className="decide__comment">
        <label htmlFor={`${id}-comment`} className="sr-only">
          Comment
        </label>
        <input
          ref={commentRef}
          id={`${id}-comment`}
          type="text"
          className="decide__input"
          placeholder="Add a comment (required if you reject)"
          value={d.comment}
          onChange={(e) => d.setComment(e.target.value)}
          aria-invalid={d.error ? true : undefined}
          aria-describedby={d.error ? `${id}-error` : undefined}
        />
        {d.error ? (
          <span className="decide__error" id={`${id}-error`} role="alert">
            {d.error}
          </span>
        ) : null}
      </div>
      {d.delegating ? (
        <div className="decide__delegate">
          <label htmlFor={`${id}-delegate`} className="sr-only">
            Delegate to
          </label>
          <PersonPicker id={`${id}-delegate`} value={d.delegateTo || null} filter={d.canDelegateTo} placeholder="Delegate to…" clearLabel="Clear delegate" onChange={(id) => d.setDelegateTo(id ?? '')} />
          <Button variant="on-dark-ghost" onClick={() => d.setDelegating(false)} disabled={d.busy}>
            Cancel
          </Button>
          <Button variant="on-dark" onClick={d.delegate} disabled={!d.delegateTo || d.busy} loading={d.delegatingNow}>
            Delegate step
          </Button>
        </div>
      ) : (
        <>
          <Button variant="on-dark-ghost" className="decide__delegate-btn" onClick={() => d.setDelegating(true)} disabled={d.busy}>
            Delegate
          </Button>
          {/* Under 720px this bar is fixed to the bottom of the screen (styles/screens.css). */}
          <div className="decide__actions" data-pinned-bar>
            <Button variant="on-dark-ghost" className="decide__reject" onClick={reject} disabled={d.busy} loading={d.rejecting}>
              Reject
            </Button>
            <Button variant="on-dark" className="decide__approve" onClick={d.approve} disabled={d.busy} loading={d.approving}>
              Approve
            </Button>
          </div>
        </>
      )}
    </section>
  );
}

function Detail({ item, onDone, onBack }: { item: MyApproval; onDone: () => void; onBack: () => void }) {
  const c = item.contract;
  const docs = c.documents.length;
  return (
    <div className="approvals__detail" aria-label="Approval to decide" role="region">
      <button type="button" className="link-btn approvals__back" onClick={onBack}>
        <Icon name="chevronLeft" size={16} stroke={2.2} />
        Approvals
      </button>
      <Card className="approval-head">
        <div className="approval-head__top">
          <div className="approval-head__text">
            <span className="approval-head__step">{stepLabel(item.instance, item.stepId)}</span>
            <h2 className="approval-head__title" tabIndex={-1} data-approval-title>
              {c.title}
            </h2>
            <span className="secondary">
              {c.counterparty} · {c.entityName} · {c.departmentName}
              <span className="hide-phone"> · owner {c.ownerName}</span>
            </span>
          </div>
          <ButtonLink to={`/contracts/${c.id}`} variant="secondary" size="md">
            Open contract
          </ButtonLink>
        </div>
        {c.summary ? <p className="approval-head__summary">{c.summary}</p> : null}
        <KeyTerms contract={c} tiles />
      </Card>
      <Card className="approval-card">
        <div className="approval-card__head">
          <span className="tile tile--sm" aria-hidden="true">
            <Icon name="star" size={16} stroke={2} />
          </span>
          <h3 className="card-title">What differs from the DAFA standard</h3>
        </div>
        <Deviations items={c.deviations} wide />
      </Card>
      <Card className="approval-card">
        <h3 className="card-title">
          <span className="approval-card__title-wide">Approval route</span>
          <span className="approval-card__title-phone" aria-hidden="true">
            Route
          </span>
        </h3>
        <RouteCards instance={item.instance} myStepId={item.stepId} />
        {/* Phone: the route in one sentence (PhoneApprove.dc.html); the cards above are hidden. */}
        <p className="route-line" aria-hidden="true">
          {routeSummary(item.instance, item.stepId).map((part, i) => (
            <span key={i}>
              {i > 0 ? ' · ' : ''}
              {part.now ? <strong>{part.text}</strong> : part.text}
            </span>
          ))}
        </p>
        <Link to={`/contracts/${c.id}`} className="approval-card__docs">
          <span className="approval-card__title-wide">Open the contract and its {plural(docs, 'document')}</span>
          <span className="approval-card__title-phone" aria-hidden="true">
            {docs === 0 ? 'Open the contract' : `Open the ${plural(docs, 'document')}`}
          </span>
        </Link>
      </Card>
      <DecisionBar key={item.stepId} stepId={item.stepId} onDone={onDone} contract={{ ownerId: c.ownerId, submittedById: c.submittedById, stageApproverIds: stageApproverIds(item.instance, item.stepId) }} />
    </div>
  );
}

export function Approvals() {
  const api = useApi();
  const [params, setParams] = useSearchParams();
  const scope = useScope();
  const mine = useQuery({ queryKey: [...qk.approvals, 'mine', scope.entityIds], queryFn: () => api.getMyApprovals(scope.entityIds) });
  const items = mine.data ?? [];
  const picked = params.get('step');
  const pickedItem = items.find((i) => i.stepId === picked);
  const current = pickedItem ?? items[0];
  // Phone: one approval fills the screen, without the top bar and the tab bar.
  useBodyFlag('approval-detail', !!pickedItem);

  const pick = (stepId: string | null) => {
    setParams(stepId ? { step: stepId } : {}, { replace: true });
    // On a phone the detail replaces the queue: move focus to what just appeared.
    if (stepId) window.setTimeout(() => document.querySelector<HTMLElement>('[data-approval-title]')?.focus({ preventScroll: true }), 0);
  };

  return (
    <div className={`page rise rise--fade approvals${pickedItem ? ' approvals--picked' : ''}`} style={{ paddingTop: 10 }}>
      <div className="page-head approvals__head">
        <div className="page-head__text">
          <h1 className="h1">Approvals</h1>
          <span className="page-sub">
            {mine.isLoading ? <Skeleton width={260} height={16} /> : mine.isError ? 'Contracts waiting for your decision' : items.length === 0 ? 'Nothing is waiting for your decision' : `${plural(items.length, 'contract')} ${items.length === 1 ? 'is' : 'are'} waiting for your decision`}
          </span>
        </div>
      </div>
      <ScopeChip />

      {mine.isLoading ? (
        <Card clip>
          <SkeletonRows rows={3} label="Loading approvals" />
        </Card>
      ) : mine.isError ? (
        <Card>
          <ErrorState text="Your approvals could not be loaded." onRetry={() => void mine.refetch()} />
        </Card>
      ) : !current ? (
        <Card>
          <EmptyState
            icon="check"
            title="Nothing is waiting for you"
            text="When a contract reaches your step in its approval route, it shows up here and on My work."
            action={
              <ButtonLink to="/contracts?view=approval" variant="secondary" size="md">
                See contracts in approval
              </ButtonLink>
            }
          />
        </Card>
      ) : (
        <div className="approvals__body">
          <Card clip className="approvals__queue">
            <ul aria-label="Waiting for your decision">
              {items.map((q) => {
                const wait = waitInfo(q.waitingDays);
                const on = q.stepId === current.stepId;
                return (
                  <li key={q.stepId}>
                    <button type="button" className="queue-item" aria-current={on ? 'true' : undefined} onClick={() => pick(q.stepId)}>
                      <span className={`queue-item__dot queue-item__dot--${wait.level}`} aria-hidden="true" />
                      <span className="queue-item__text">
                        <span className="queue-item__title">{q.contract.title}</span>
                        <span className="caption">
                          {q.contract.entityName} · {q.contract.departmentName}
                        </span>
                        <span className={`queue-item__wait queue-item__wait--${wait.level}`}>{wait.text}</span>
                      </span>
                      <span className="queue-item__chev" aria-hidden="true">
                        <Icon name="chevronRight" size={16} stroke={2.2} />
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </Card>
          <Detail key={current.stepId} item={current} onDone={() => pick(null)} onBack={() => pick(null)} />
        </div>
      )}
      {apiMode === 'mock' ? <p className="caption approvals__note">{items.length > 1 ? 'Sample data. Pick a contract in the queue.' : 'Sample data.'}</p> : null}
    </div>
  );
}
