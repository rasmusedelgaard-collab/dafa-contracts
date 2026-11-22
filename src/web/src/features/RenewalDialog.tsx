import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { qk, useApi, useInvalidateContracts } from '../app/context';
import type { RenewalDecision } from '../api/types';
import { Button, ErrorState, Field, Modal, Skeleton, Textarea, TextInput, useToast } from '../components';
import { addDays, addMonths, formatDate, isIsoDate } from '../lib/dates';

const DONE_TEXT: Record<RenewalDecision, string> = {
  Renew: 'Renewal recorded',
  Renegotiate: 'Decision recorded: renegotiate',
  GiveNotice: 'Decision recorded: give notice',
  LetExpire: 'Decision recorded: let it expire',
};

/** Records the renewal decision for a contract (`POST contracts/{id}/renewal-decision`). */
export function RenewalDialog({ contractId, initial, onClose }: { contractId: string; initial?: RenewalDecision; onClose: () => void }) {
  const api = useApi();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const contract = useQuery({ queryKey: qk.contractPeek(contractId), queryFn: () => api.getContract(contractId, { peek: true }) });
  const [picked, setDecision] = useState<RenewalDecision | null>(initial ?? null);
  const [comment, setComment] = useState('');
  const [newEndDate, setNewEndDate] = useState('');
  const [tried, setTried] = useState(false);
  const c = contract.data;
  const openEnded = !!c && c.endDate === null;
  // Giving notice is the only decision an open-ended contract can take.
  const decision: RenewalDecision | null = openEnded ? 'GiveNotice' : picked;
  // A fixed-term contract has no renewal period, so "Renew" needs the new end date.
  const needsEndDate = decision === 'Renew' && !!c && !c.autoRenew && c.endDate !== null;
  const endDateError = !tried || !needsEndDate ? undefined : !isIsoDate(newEndDate) ? 'Enter the new end date' : c?.endDate && newEndDate <= c.endDate ? `The new end date must be after ${formatDate(c.endDate)}` : undefined;

  const needsComment = decision === 'GiveNotice' || decision === 'LetExpire';
  const decisionError = tried && !decision ? 'Choose a decision' : undefined;
  const commentError = tried && needsComment && !comment.trim() ? 'Add a comment that explains why the contract ends' : undefined;

  const save = useMutation({
    mutationFn: (d: RenewalDecision) => api.decideRenewal(contractId, { decision: d, comment: comment.trim(), ...(needsEndDate ? { newEndDate } : {}) }),
    onSuccess: async (_data, d) => {
      await invalidate();
      toast.show(DONE_TEXT[d]);
      onClose();
    },
    onError: (e) => toast.error(e),
  });

  const submit = () => {
    setTried(true);
    if (!decision || (needsComment && !comment.trim())) return;
    if (needsEndDate && (!isIsoDate(newEndDate) || (c?.endDate && newEndDate <= c.endDate))) return;
    save.mutate(decision);
  };

  const options: { value: RenewalDecision; title: string; text: string }[] = !c
    ? []
    : openEnded
      ? [{ value: 'GiveNotice', title: 'Give notice', text: `Terminate the contract. It has no end date, so it runs until notice is given${c.noticePeriodDays ? `; the notice period is ${c.noticePeriodDays} days` : ''}.` }]
      : [
        {
          value: 'Renew',
          title: 'Renew',
          text: c.autoRenew && c.renewalMonths && c.endDate ? `Let it renew for ${c.renewalMonths} months. The end date moves to ${formatDate(addMonths(c.endDate, c.renewalMonths))}.` : 'Extend the contract. You enter the new end date.',
        },
        { value: 'Renegotiate', title: 'Renegotiate', text: 'Keep the contract running while you renegotiate the terms. Reminders stop.' },
        { value: 'GiveNotice', title: 'Give notice', text: `Terminate the contract. Notice must reach the counterparty by ${formatDate(c.noticeDeadline)}.` },
        ...(c.autoRenew ? [] : [{ value: 'LetExpire' as const, title: 'Let it expire', text: `Do nothing. The contract ends on ${formatDate(c.endDate)}.` }]),
      ];

  return (
    <Modal
      title="Decide on renewal"
      subtitle={c ? `${c.title} · ${c.counterparty}` : undefined}
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending} disabled={!c || !c.canDecideRenewal}>
            Save decision
          </Button>
        </>
      }
    >
      {contract.isLoading ? (
        <div className="stack" style={{ gap: 12 }} role="status" aria-label="Loading contract">
          <Skeleton height={56} radius={12} />
          <Skeleton height={56} radius={12} />
          <Skeleton height={56} radius={12} />
        </div>
      ) : contract.isError || !c ? (
        <ErrorState text="The contract could not be loaded." onRetry={() => void contract.refetch()} />
      ) : !c.canDecideRenewal ? (
        <p className="banner tone-amber">Only the owner, delegate or responsible person on an active contract can record the renewal decision.</p>
      ) : (
        <>
          {openEnded ? (
            <p className="secondary">Open-ended contract: there is no notice deadline, and notice can be given at any time.</p>
          ) : (
          <p className="secondary">
            Notice deadline <strong>{formatDate(c.noticeDeadline)}</strong>
            {c.daysToNotice !== null ? (c.daysToNotice >= 0 ? ` · ${c.daysToNotice} days left` : ` · ${-c.daysToNotice} days overdue`) : ''}
            {c.autoRenew && c.renewalMonths ? ` · renews automatically for ${c.renewalMonths} months` : ' · fixed term'}
          </p>
          )}
          <fieldset style={{ border: 0, padding: 0, margin: 0 }} aria-describedby={decisionError ? 'renewal-decision-error' : undefined}>
            <legend className="sr-only">Decision</legend>
            <div className="choices">
              {options.map((o, i) => (
                <label key={o.value} className="choice">
                  <input type="radio" name="renewal-decision" value={o.value} checked={decision === o.value} onChange={() => setDecision(o.value)} data-autofocus={(initial ? o.value === initial : i === 0) ? '' : undefined} />
                  <span className="choice__title">{o.title}</span>
                  <span className="choice__text">{o.text}</span>
                </label>
              ))}
            </div>
            {decisionError ? (
              <span className="field__error" id="renewal-decision-error" role="alert" style={{ display: 'block', marginTop: 8 }}>
                {decisionError}
              </span>
            ) : null}
          </fieldset>
          {needsEndDate ? (
            <Field label="New end date" error={endDateError} hint={`The contract ends on ${formatDate(c.endDate)} today.`}>
              {(p) => <TextInput {...p} type="date" min={c.endDate ? addDays(c.endDate, 1) : undefined} value={newEndDate} onChange={(e) => setNewEndDate(e.target.value)} />}
            </Field>
          ) : null}
          <Field label="Comment" optional={!needsComment} error={commentError} hint="Saved in the activity log and sent to the owner.">
            {(p) => <Textarea {...p} value={comment} onChange={(e) => setComment(e.target.value)} rows={3} />}
          </Field>
        </>
      )}
    </Modal>
  );
}
