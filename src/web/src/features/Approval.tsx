/* eslint-disable react-refresh/only-export-components */
import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { qk, useApi, useInvalidateContracts, useLookups, useMe } from '../app/context';
import type { ApprovalInstance, ApprovalPreview, ApprovalStepStatus, ContractDetail, Deviation, LookupUser } from '../api/types';
import { Avatar, Button, EmptyState, ErrorState, Field, Modal, Pill, Skeleton, Textarea, useToast } from '../components';
import { formatDate, termLength } from '../lib/dates';
import { formatMoneyCompact, type Tone } from '../lib/format';
import { PersonPicker } from './Pickers';

const STEP_TONE: Record<ApprovalStepStatus, Tone> = { Waiting: 'gray', Pending: 'blue', Approved: 'green', Rejected: 'red', Delegated: 'gray' };
const STEP_TEXT: Record<ApprovalStepStatus, string> = { Waiting: 'Waiting', Pending: 'Approving now', Approved: 'Approved', Rejected: 'Rejected', Delegated: 'Delegated' };

/** The stages of an approval, with who acts in parallel and where it stands. */
export function ApprovalRoute({ approval }: { approval: ApprovalInstance }) {
  return (
    <ol className="route" aria-label="Approval route">
      {approval.stages.map((stage) => (
        <li key={stage.order} className="route__stage">
          <span className="route__label">
            Step {stage.order} of {approval.stages.length}
            {stage.steps.filter((s) => s.status !== 'Delegated').length > 1 ? ' · in parallel' : ''}
          </span>
          {stage.steps.map((s) => (
            <div key={s.id} className="route__step">
              <Avatar initials={s.initials} />
              <span className="kv" style={{ flex: '1 1 auto' }}>
                <span className="kv__v">{s.approverName}</span>
                <span className="kv__k">
                  {s.roleLabel}
                  {s.decidedAt ? ` · ${formatDate(s.decidedAt.slice(0, 10))}` : ''}
                  {s.comment ? ` · "${s.comment}"` : ''}
                </span>
              </span>
              <Pill tone={STEP_TONE[s.status]}>{STEP_TEXT[s.status]}</Pill>
            </div>
          ))}
        </li>
      ))}
    </ol>
  );
}

/** Route preview from `GET approvals/preview`, before the contract is submitted. */
export function RoutePreview({ preview, loading }: { preview: ApprovalPreview | undefined; loading: boolean }) {
  if (loading) {
    return (
      <div className="stack" style={{ gap: 8 }} role="status" aria-label="Loading approval route">
        <Skeleton width="70%" />
        <Skeleton width="55%" />
      </div>
    );
  }
  if (!preview) return <span className="secondary">Choose the legal entity, contract type and department to see who approves.</span>;
  if (preview.stages.length === 0) return <span className="secondary">No approval rule matches yet. An administrator can add one in Settings.</span>;
  return (
    <ol className="stack" style={{ gap: 8 }}>
      {preview.stages.map((s) => (
        <li key={s.order} className="route__step" style={{ alignItems: 'flex-start', fontSize: 14 }}>
          <span className="route__n" aria-hidden="true">
            {s.order}
          </span>
          <span className="stack">
            {s.approvers.map((a) => (
              <span key={a}>{a}</span>
            ))}
            {s.approvers.length > 1 ? <span className="caption">in parallel</span> : null}
          </span>
        </li>
      ))}
    </ol>
  );
}

const SEVERITY_TONE: Record<Deviation['severity'], Tone> = { High: 'red', Medium: 'amber', Low: 'blue', None: 'green' };

export function Deviations({ items, wide }: { items: Deviation[]; wide?: boolean }) {
  if (items.length === 0) return <EmptyState compact title="No deviations found" text="Nothing in the uploaded file differs from the DAFA standard terms." />;
  return (
    <div>
      {items.map((d, i) => (
        <div key={i} className={`deviation${wide ? ' deviation--wide' : ''}`} style={i === 0 && !wide ? { borderTop: 0, paddingTop: 0 } : undefined}>
          <Pill tone={SEVERITY_TONE[d.severity]}>{d.severity === 'None' ? 'OK' : d.severity}</Pill>
          <span className="kv">
            <span className="kv__v">{d.text}</span>
            {d.standard ? <span className="kv__k">Standard: {d.standard}</span> : null}
          </span>
        </div>
      ))}
      <p className="caption" style={{ marginTop: 8 }}>
        Found by AI<span className="hide-phone"> in the uploaded file</span>. Check <span className="hide-phone">against </span>the document before you decide.
      </p>
    </div>
  );
}

/** The terms an approver looks at first. `tiles` renders one tinted tile per term (Approvals screen). */
export function KeyTerms({ contract: c, tiles }: { contract: ContractDetail; tiles?: boolean }) {
  const value = c.totalValue ?? c.annualValue;
  const terms: [string, string][] = [
    [c.totalValue !== null ? 'Total value' : 'Annual value', formatMoneyCompact(value, c.currency)],
    ['Term', c.endDate ? `${formatDate(c.startDate)} – ${formatDate(c.endDate)}` : `From ${formatDate(c.startDate)}, open-ended`],
    ['Notice period', c.noticePeriodDays === null ? 'Missing' : `${c.noticePeriodDays} days`],
    ['Renewal', c.autoRenew && c.renewalMonths ? `Auto, ${c.renewalMonths} months` : c.endDate ? 'Fixed term' : 'Open-ended'],
    ['Change of control', c.changeOfControl === null ? 'Not set' : c.changeOfControl ? 'Yes' : 'No clause'],
  ];
  // A phone shows four tiles in two columns (PhoneApprove.dc.html): the term as a length, no Renewal tile.
  const short = c.endDate ? termLength(c.startDate, c.endDate) : 'Open-ended';
  return (
    <dl className={tiles ? 'term-tiles' : 'terms-grid'}>
      {terms.map(([k, v]) => (
        <div key={k} className={`kv${tiles && k === 'Renewal' ? ' term-tiles__wide-only' : ''}`}>
          <dt className="kv__k">{k}</dt>
          <dd className="kv__v">
            {tiles && k === 'Term' ? (
              <>
                <span className="term-tiles__long">{v}</span>
                <span className="term-tiles__short" aria-hidden="true">
                  {short}
                </span>
              </>
            ) : (
              v
            )}
          </dd>
        </div>
      ))}
    </dl>
  );
}

/**
 * State and actions behind every decision control: the comment, Approve, Reject (comment
 * required) and Delegate for one pending step of the signed-in user.
 */
export interface DecisionContract {
  ownerId: string;
  submittedById?: string | null;
  /** Everyone who already approves in the stage the step belongs to (delegated-away steps excluded). */
  stageApproverIds?: string[];
}

/** The approvers of the stage that holds `stepId`; a step that was delegated away no longer counts. */
export function stageApproverIds(instance: ApprovalInstance, stepId: string): string[] {
  const stage = instance.stages.find((s) => s.steps.some((st) => st.id === stepId));
  return (stage?.steps ?? []).filter((st) => st.status !== 'Delegated').map((st) => st.approverId);
}

/**
 * Who a step may be delegated to, by the API's rules: an Approver or Admin who is not you, not
 * the contract's owner, not its submitter and not already an approver in the same stage.
 */
export function delegateCandidates(users: LookupUser[], meId: string, contract?: DecisionContract): LookupUser[] {
  const blocked = new Set([meId, contract?.ownerId, contract?.submittedById, ...(contract?.stageApproverIds ?? [])].filter((x): x is string => !!x));
  // An API that does not send roles on lookup users leaves the role check to the server.
  return users.filter((u) => !blocked.has(u.id) && (!u.roles || u.roles.includes('Approver') || u.roles.includes('Admin')));
}

export function useApprovalDecision(stepId: string, onDone?: () => void, contract?: DecisionContract) {
  const api = useApi();
  const me = useMe();
  const lookups = useLookups();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const [comment, setCommentText] = useState('');
  const [error, setError] = useState<string>();
  const [delegating, setDelegating] = useState(false);
  const [delegateTo, setDelegateTo] = useState('');

  const done = async (text: string) => {
    await invalidate();
    toast.show(text);
    onDone?.();
  };
  /**
   * A 409 means the step is no longer what the screen shows; the query client refetches on every
   * 409. `stale` gets the fixed sentence, every other type the server's own explanation.
   */
  const failed = (e: unknown) => toast.error(e);
  const decide = useMutation({
    mutationFn: (decision: 'Approve' | 'Reject') => api.decideApprovalStep(stepId, { decision, comment: comment.trim() }),
    onSuccess: (_d, decision) => done(decision === 'Approve' ? 'Approved' : 'Rejected. The contract is back with the owner as a draft'),
    onError: (e) => failed(e),
  });
  const delegate = useMutation({
    mutationFn: () => api.delegateApprovalStep(stepId, delegateTo),
    onSuccess: () => done('Approval step delegated'),
    onError: (e) => failed(e),
  });

  return {
    comment,
    setComment: (text: string) => {
      setCommentText(text);
      if (error) setError(undefined);
    },
    error,
    busy: decide.isPending || delegate.isPending,
    approving: decide.isPending && decide.variables === 'Approve',
    rejecting: decide.isPending && decide.variables === 'Reject',
    delegatingNow: delegate.isPending,
    approve: () => decide.mutate('Approve'),
    /** Returns false (and sets the error) when the required comment is missing. */
    reject: (): boolean => {
      if (!comment.trim()) {
        setError('Add a comment that explains why you reject the contract');
        return false;
      }
      setError(undefined);
      decide.mutate('Reject');
      return true;
    },
    delegating,
    setDelegating,
    delegateTo,
    setDelegateTo,
    delegate: () => delegate.mutate(),
    /** Who the step may be delegated to (person picker filter). */
    canDelegateTo: (u: LookupUser) => delegateCandidates(lookups.users, me.id, contract).some((x) => x.id === u.id),
  };
}

/** Comment + Approve / Reject / Delegate for one pending step of the signed-in user. */
export function DecisionForm({ stepId, onDone, contract }: { stepId: string; onDone?: () => void; contract?: DecisionContract }) {
  const d = useApprovalDecision(stepId, onDone, contract);
  return (
    <div className="stack" style={{ gap: 12 }}>
      <Field label="Comment" error={d.error} hint="Required if you reject.">
        {(p) => <Textarea {...p} rows={2} value={d.comment} placeholder="Add a comment" onChange={(e) => d.setComment(e.target.value)} />}
      </Field>
      {d.delegating ? (
        <div className="row-flex" style={{ alignItems: 'flex-end' }}>
          <Field label="Delegate to" className="spacer">
            {(p) => <PersonPicker {...p} value={d.delegateTo || null} filter={d.canDelegateTo} clearLabel="Clear delegate" onChange={(id) => d.setDelegateTo(id ?? '')} />}
          </Field>
          <Button variant="secondary" onClick={() => d.setDelegating(false)} disabled={d.busy}>
            Cancel
          </Button>
          <Button onClick={d.delegate} disabled={!d.delegateTo || d.busy} loading={d.delegatingNow}>
            Delegate
          </Button>
        </div>
      ) : (
        <div className="row-flex" style={{ justifyContent: 'flex-end' }}>
          <Button variant="ghost" onClick={() => d.setDelegating(true)} disabled={d.busy}>
            Delegate
          </Button>
          <Button variant="danger" onClick={d.reject} disabled={d.busy} loading={d.rejecting}>
            Reject
          </Button>
          <Button icon="check" onClick={d.approve} disabled={d.busy} loading={d.approving}>
            Approve
          </Button>
        </div>
      )}
    </div>
  );
}

/** Review-and-decide dialog opened from a Home approval task. */
export function ApprovalDialog({ stepId, onClose }: { stepId: string; onClose: () => void }) {
  const api = useApi();
  const mine = useQuery({ queryKey: [...qk.approvals, 'mine'], queryFn: () => api.getMyApprovals() });
  const item = mine.data?.find((a) => a.stepId === stepId);

  return (
    <Modal
      wide
      title={item ? item.contract.title : 'Review approval'}
      subtitle={item ? `${item.contract.counterparty} · ${item.contract.entityName} · ${item.contract.departmentName} · waiting ${item.waitingDays} ${item.waitingDays === 1 ? 'day' : 'days'}` : undefined}
      onClose={onClose}
    >
      {mine.isLoading ? (
        <div className="stack" style={{ gap: 12 }} role="status" aria-label="Loading approval">
          <Skeleton height={72} radius={14} />
          <Skeleton height={120} radius={14} />
        </div>
      ) : mine.isError ? (
        <ErrorState text="The approval could not be loaded." onRetry={() => void mine.refetch()} />
      ) : !item ? (
        <EmptyState icon="check" title="This approval is no longer waiting for you" text="It was decided or delegated in the meantime." />
      ) : (
        <>
          <KeyTerms contract={item.contract} />
          {item.contract.summary ? <p style={{ color: 'var(--text-3)', lineHeight: 1.5 }}>{item.contract.summary}</p> : null}
          <div>
            <h3 className="card-title" style={{ marginBottom: 10 }}>
              What differs from the DAFA standard
            </h3>
            <Deviations items={item.contract.deviations} />
          </div>
          <div>
            <h3 className="card-title" style={{ marginBottom: 10 }}>
              Approval route
            </h3>
            <ApprovalRoute approval={item.instance} />
          </div>
          <Link to={`/contracts/${item.contract.id}`} onClick={onClose} style={{ fontWeight: 600, fontSize: 13, alignSelf: 'flex-start' }}>
            Open the contract and its {item.contract.documents.length} {item.contract.documents.length === 1 ? 'document' : 'documents'}
          </Link>
          <DecisionForm stepId={stepId} onDone={onClose} contract={{ ownerId: item.contract.ownerId, submittedById: item.contract.submittedById, stageApproverIds: stageApproverIds(item.instance, stepId) }} />
        </>
      )}
    </Modal>
  );
}
