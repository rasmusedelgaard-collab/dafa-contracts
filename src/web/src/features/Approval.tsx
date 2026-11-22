import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { qk, useApi, useInvalidateContracts, useLookups, useMe } from '../app/context';
import type { ApprovalInstance, ApprovalPreview, ApprovalStepStatus, ContractDetail, Deviation } from '../api/types';
import { Avatar, Button, EmptyState, ErrorState, Field, Modal, Pill, Select, Skeleton, Textarea, useToast } from '../components';
import { formatDate } from '../lib/dates';
import { formatMoneyCompact, type Tone } from '../lib/format';

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

export function Deviations({ items }: { items: Deviation[] }) {
  if (items.length === 0) return <EmptyState compact title="No deviations found" text="Nothing in the uploaded file differs from the DAFA standard terms." />;
  return (
    <div>
      {items.map((d, i) => (
        <div key={i} className="deviation" style={i === 0 ? { borderTop: 0, paddingTop: 0 } : undefined}>
          <Pill tone={SEVERITY_TONE[d.severity]}>{d.severity === 'None' ? 'OK' : d.severity}</Pill>
          <span className="kv">
            <span className="kv__v">{d.text}</span>
            <span className="kv__k">Standard: {d.standard}</span>
          </span>
        </div>
      ))}
      <p className="caption" style={{ marginTop: 8 }}>
        Found by AI in the uploaded file. Check against the document before you decide.
      </p>
    </div>
  );
}

export function KeyTerms({ contract: c }: { contract: ContractDetail }) {
  const value = c.totalValue ?? c.annualValue;
  const terms: [string, string][] = [
    [c.totalValue !== null ? 'Total value' : 'Annual value', formatMoneyCompact(value, c.currency)],
    ['Term', c.endDate ? `${formatDate(c.startDate)} – ${formatDate(c.endDate)}` : `From ${formatDate(c.startDate)}, open-ended`],
    ['Notice period', c.noticePeriodDays === null ? 'Missing' : `${c.noticePeriodDays} days`],
    ['Renewal', c.autoRenew && c.renewalMonths ? `Automatic, ${c.renewalMonths} months` : 'Fixed term'],
    ['Change of control', c.changeOfControl ? 'Yes' : 'No clause'],
  ];
  return (
    <dl className="terms-grid">
      {terms.map(([k, v]) => (
        <div key={k} className="kv">
          <dt className="kv__k">{k}</dt>
          <dd className="kv__v">{v}</dd>
        </div>
      ))}
    </dl>
  );
}

/** Comment + Approve / Reject / Delegate for one pending step of the signed-in user. */
export function DecisionForm({ stepId, onDone }: { stepId: string; onDone?: () => void }) {
  const api = useApi();
  const me = useMe();
  const lookups = useLookups();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const [comment, setComment] = useState('');
  const [error, setError] = useState<string>();
  const [delegating, setDelegating] = useState(false);
  const [delegateTo, setDelegateTo] = useState('');

  const done = async (text: string) => {
    await invalidate();
    toast.show(text);
    onDone?.();
  };
  const decide = useMutation({
    mutationFn: (decision: 'Approve' | 'Reject') => api.decideApprovalStep(stepId, { decision, comment: comment.trim() }),
    onSuccess: (_d, decision) => done(decision === 'Approve' ? 'Approved' : 'Rejected. The contract is back with the owner as a draft'),
    onError: (e) => toast.error(e),
  });
  const delegate = useMutation({
    mutationFn: () => api.delegateApprovalStep(stepId, delegateTo),
    onSuccess: () => done('Approval step delegated'),
    onError: (e) => toast.error(e),
  });
  const busy = decide.isPending || delegate.isPending;

  const reject = () => {
    if (!comment.trim()) {
      setError('Add a comment that explains why you reject the contract');
      return;
    }
    setError(undefined);
    decide.mutate('Reject');
  };

  return (
    <div className="stack" style={{ gap: 12 }}>
      <Field label="Comment" error={error} hint="Required if you reject.">
        {(p) => (
          <Textarea
            {...p}
            rows={2}
            value={comment}
            placeholder="Add a comment"
            onChange={(e) => {
              setComment(e.target.value);
              if (error) setError(undefined);
            }}
          />
        )}
      </Field>
      {delegating ? (
        <div className="row-flex" style={{ alignItems: 'flex-end' }}>
          <Field label="Delegate to" className="spacer">
            {(p) => <Select {...p} value={delegateTo} onChange={(e) => setDelegateTo(e.target.value)} placeholder="Choose a person" options={lookups.users.filter((u) => u.id !== me.id).map((u) => ({ value: u.id, label: u.name }))} />}
          </Field>
          <Button variant="secondary" onClick={() => setDelegating(false)} disabled={busy}>
            Cancel
          </Button>
          <Button onClick={() => delegate.mutate()} disabled={!delegateTo || busy} loading={delegate.isPending}>
            Delegate
          </Button>
        </div>
      ) : (
        <div className="row-flex" style={{ justifyContent: 'flex-end' }}>
          <Button variant="ghost" onClick={() => setDelegating(true)} disabled={busy}>
            Delegate
          </Button>
          <Button variant="danger" onClick={reject} disabled={busy} loading={decide.isPending && decide.variables === 'Reject'}>
            Reject
          </Button>
          <Button icon="check" onClick={() => decide.mutate('Approve')} disabled={busy} loading={decide.isPending && decide.variables === 'Approve'}>
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
          <DecisionForm stepId={stepId} onDone={onClose} />
        </>
      )}
    </Modal>
  );
}
