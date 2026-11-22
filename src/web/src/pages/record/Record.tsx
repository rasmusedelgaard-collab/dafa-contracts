import { useState, type ReactNode } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { apiMode } from '../../api';
import { ApiError } from '../../api/api';
import { qk, useApi, useAppConfig, useInvalidateContracts, useMe } from '../../app/context';
import type { ContractDetail, ContractReminder, Obligation, RenewalDecision } from '../../api/types';
import { Avatar, Button, ButtonLink, Card, CardTitle, EmptyState, ErrorState, Icon, Pill, Segmented, Skeleton, StatusPill, useToast } from '../../components';
import { ApprovalRoute, DecisionForm, Deviations } from '../../features/Approval';
import { RenewalDialog } from '../../features/RenewalDialog';
import { addDays, daysBetween, formatDate, formatDateLong, formatDayMonth, todayIso } from '../../lib/dates';
import { fileExtension, formatFileSize, formatMoney, saveBlob } from '../../lib/format';
import { EditDetailsDialog, LinkDialog, ObligationDialog, SignDialog, SubmitDialog, UploadDialog } from './dialogs';
import { AdditionalDetails, CommentsTab, CompareDialog, TagsCard, WatchButton } from './collab';
import { PersonLink } from '../../features/columns';

type Tab = 'overview' | 'obligations' | 'approvals' | 'comments' | 'activity';
const TABS: { value: Tab; label: string }[] = [
  { value: 'overview', label: 'Overview' },
  { value: 'obligations', label: 'Obligations' },
  { value: 'approvals', label: 'Approvals' },
  { value: 'comments', label: 'Comments' },
  { value: 'activity', label: 'Activity' },
];

type Dialog =
  | { kind: 'renewal'; initial?: RenewalDecision }
  | { kind: 'edit' }
  | { kind: 'sign' }
  | { kind: 'submit' }
  | { kind: 'upload' }
  | { kind: 'link' }
  | { kind: 'obligation'; obligation: Obligation | null }
  | { kind: 'compare' }
  | null;

const DECISION_LABEL: Record<RenewalDecision, string> = { Renew: 'Renew', Renegotiate: 'Renegotiate', GiveNotice: 'Give notice', LetExpire: 'Let it expire' };
const LINK_LABEL: Record<string, { out: string; in: string }> = {
  Amendment: { out: 'Amends', in: 'Amendment' },
  Appendix: { out: 'Appendix to', in: 'Appendix' },
  Framework: { out: 'Under framework', in: 'Call-off' },
  Related: { out: 'Related', in: 'Related' },
};
const DOC_KIND_LABEL: Record<string, string> = { Main: 'Main contract', Appendix: 'Appendix', Amendment: 'Amendment', ApprovalLog: 'Approval log', Other: 'Other' };

function reminderLabel(r: ContractReminder): string {
  if (r.kind === 'Escalation') return 'Escalates to owner';
  if (r.kind === 'AnnualReview') return 'Annual review';
  const days = /^Notice(\d+)$/.exec(r.kind)?.[1];
  return days ? `${days} days` : r.kind;
}

function obligationMeta(o: Obligation): string {
  const parts: string[] = [];
  if (o.status === 'Done') parts.push('Done');
  else if (o.dueDate) parts.push(`Due ${formatDate(o.dueDate)}`);
  else parts.push('No due date');
  if (o.frequencyMonths) parts.push(o.frequencyMonths === 12 ? 'every year' : `every ${o.frequencyMonths} months`);
  parts.push(o.responsibleName);
  return parts.join(' · ');
}

// ---------------------------------------------------------------------------

function Lifecycle({ c }: { c: ContractDetail }) {
  return (
    <Card as="div">
      <ol className="lifecycle" aria-label="Lifecycle">
        {c.lifecycle.map((l) => (
          <li key={l.name} data-state={l.state} aria-current={l.state === 'current' || l.state === 'alert' ? 'step' : undefined}>
            <span className="lifecycle__bar" aria-hidden="true" />
            <span className="lifecycle__name">{l.name}</span>
            <span className="caption">{l.date ? `${l.name === 'Active' && l.state === 'done' ? 'since ' : ''}${formatDate(l.date.slice(0, 10))}` : l.state === 'upcoming' ? 'Not yet' : '–'}</span>
          </li>
        ))}
      </ol>
    </Card>
  );
}

function Ring({ days, window: span }: { days: number; window: number }) {
  const circumference = 2 * Math.PI * 50;
  const fraction = Math.max(0, Math.min(1, days / span));
  const shown = Math.abs(days);
  return (
    <svg role="img" aria-label={days >= 0 ? `${days} days left of the ${span}-day reminder window` : `${shown} days past the notice deadline`} width="88" height="88" viewBox="0 0 120 120" style={{ flex: '0 0 auto' }}>
      <circle cx="60" cy="60" r="50" fill="none" stroke="rgba(255,255,255,0.18)" strokeWidth="10" />
      {fraction > 0 ? <circle cx="60" cy="60" r="50" fill="none" stroke="#ffffff" strokeWidth="10" strokeLinecap="round" strokeDasharray={`${fraction * circumference} ${circumference}`} transform="rotate(-90 60 60)" /> : null}
      <text x="60" y="62" textAnchor="middle" fontSize={shown > 999 ? 26 : 34} fontWeight="700" fill="#ffffff">
        {shown}
      </text>
      <text x="60" y="82" textAnchor="middle" fontSize="12" fill="#ffffff">
        {days >= 0 ? 'days left' : 'days over'}
      </text>
    </svg>
  );
}

function StatusCard({ c, open, setTab }: { c: ContractDetail; open: (d: Dialog) => void; setTab: (t: Tab) => void }) {
  const wrap = (eyebrow: string, title: string, actions?: ReactNode, ring?: ReactNode, note?: ReactNode) => (
    <Card tone="primary" className="notice-card" aria-label="Status">
      {ring}
      <div className="notice-card__text">
        <span style={{ fontSize: 13, opacity: 0.9 }}>{eyebrow}</span>
        <span className="notice-card__title">{title}</span>
        {note ? <span style={{ fontSize: 13, opacity: 0.92 }}>{note}</span> : null}
        {actions ? (
          <div className="row-flex" style={{ gap: 10, marginTop: 6 }}>
            {actions}
          </div>
        ) : null}
      </div>
    </Card>
  );

  if (c.status === 'Draft') {
    const rejected = c.approval?.status === 'Rejected';
    return wrap(
      rejected ? 'Draft · returned by an approver' : 'Draft',
      rejected ? 'Rejected in approval. Change the contract and submit it again' : 'Check the details, then submit the contract for approval',
      c.canEdit ? (
        <>
          <Button variant="on-dark" size="md" onClick={() => open({ kind: 'edit' })}>
            Edit details
          </Button>
          {rejected ? (
            <Button variant="on-dark-ghost" size="md" onClick={() => setTab('approvals')}>
              See the comment
            </Button>
          ) : null}
        </>
      ) : undefined,
    );
  }
  if (c.status === 'InApproval') {
    const stage = c.approval?.stages.find((s) => s.order === c.approval?.currentStage);
    const names = stage?.steps.filter((s) => s.status === 'Pending').map((s) => s.approverName) ?? [];
    return wrap(
      c.approval ? `In approval · step ${c.approval.currentStage} of ${c.approval.stages.length}` : 'In approval',
      names.length ? `Waiting for ${names.join(' and ')}` : 'Waiting for approval',
      <Button variant="on-dark" size="md" onClick={() => setTab('approvals')}>
        See the approval route
      </Button>,
    );
  }
  if (c.status === 'Approved') {
    return wrap(
      'Approved',
      'Mark the contract as signed when both parties have signed',
      c.canEdit ? (
        <Button variant="on-dark" size="md" onClick={() => open({ kind: 'sign' })}>
          Mark as signed
        </Button>
      ) : undefined,
    );
  }
  if (c.status === 'Expired' || c.status === 'Terminated') {
    return wrap(c.status, c.endDate ? `Ended on ${formatDateLong(c.endDate)}` : 'This contract has ended', undefined, undefined, c.renewalComment);
  }
  if (!c.endDate) {
    return wrap('Open-ended', c.noticePeriodDays ? `Runs until either party gives ${c.noticePeriodDays} days' notice` : 'Runs until it is terminated');
  }
  if (c.status === 'NoticeGiven') {
    return wrap('Notice given', `The contract ends on ${formatDateLong(c.endDate)}`, undefined, <Ring days={daysBetween(todayIso(), c.endDate)} window={Math.max(1, c.noticePeriodDays ?? 90)} />, c.renewalComment);
  }
  if (c.noticeDeadline === null || c.daysToNotice === null) {
    return wrap(
      `Ends ${formatDateLong(c.endDate)}`,
      'The notice deadline is unknown because the notice period is missing',
      c.canEdit ? (
        <Button variant="on-dark" size="md" onClick={() => open({ kind: 'edit' })}>
          Add the notice period
        </Button>
      ) : undefined,
    );
  }

  const ring = <Ring days={c.daysToNotice} window={120} />;
  const eyebrow = `Notice deadline · ${formatDateLong(c.noticeDeadline)}`;
  if (c.renewalDecision) {
    return wrap(
      eyebrow,
      `Decision: ${DECISION_LABEL[c.renewalDecision].toLowerCase()}`,
      c.canDecideRenewal ? (
        <Button variant="on-dark-ghost" size="md" onClick={() => open({ kind: 'renewal', initial: c.renewalDecision ?? undefined })}>
          Change decision
        </Button>
      ) : undefined,
      ring,
      <>
        {c.renewalDecidedBy ? `${c.renewalDecidedBy}, ${formatDate(c.renewalDecidedAt?.slice(0, 10))}` : null}
        {c.renewalComment ? ` · "${c.renewalComment}"` : null}
      </>,
    );
  }
  const title = c.autoRenew && c.renewalMonths ? `Renews automatically for ${c.renewalMonths} months unless notice is given` : `Ends on ${formatDateLong(c.endDate)} unless it is extended`;
  return wrap(
    eyebrow,
    title,
    c.canDecideRenewal ? (
      <>
        <Button variant="on-dark" size="md" onClick={() => open({ kind: 'renewal', initial: 'Renew' })}>
          Renew
        </Button>
        <Button variant="on-dark-ghost" size="md" onClick={() => open({ kind: 'renewal', initial: 'Renegotiate' })}>
          Renegotiate
        </Button>
        <Button variant="on-dark-ghost" size="md" onClick={() => open({ kind: 'renewal', initial: 'GiveNotice' })}>
          Give notice
        </Button>
      </>
    ) : undefined,
    ring,
  );
}

/** "Signed by both parties" -> "both parties", so the Signed cell stays on one line. */
function shortMethod(method: string): string {
  const m = method.replace(/^signed by (the )?/i, '');
  return `${m.charAt(0).toLowerCase()}${m.slice(1)}`;
}

function Facts({ c }: { c: ContractDetail }) {
  const facts: [string, string][] = [
    ['Term', c.endDate ? `${formatDate(c.startDate)} – ${formatDate(c.endDate)}` : `From ${formatDate(c.startDate)}, open-ended`],
    ['Notice period', c.noticePeriodDays === null ? 'Missing' : c.noticePeriodDays === 0 ? 'None' : `${c.noticePeriodDays} days`],
    ['Annual value', formatMoney(c.annualValue, c.currency)],
    ['Price adjustment', c.priceAdjustment ?? '–'],
    ['Total value', formatMoney(c.totalValue, c.currency)],
    ['Renewal', c.autoRenew && c.renewalMonths ? `Automatic, ${c.renewalMonths} months` : c.endDate ? 'Fixed term' : 'Open-ended'],
    ['Exclusivity', c.exclusivity ?? '–'],
    ['Signed', c.signedDate ? `${formatDate(c.signedDate)}${c.signingMethod ? ` · ${shortMethod(c.signingMethod)}` : ''}` : 'Not signed'],
    ['Payment terms', c.paymentTerms ?? '–'],
    ['Governing law', c.governingLaw ?? '–'],
    ['Change of control', c.changeOfControl === null ? 'Not set' : c.changeOfControl ? 'Yes, has a clause' : 'No clause'],
    ['Liability cap', c.liabilityCap ?? '–'],
    ['Contract type', c.typeName],
    ['Contract no.', c.contractNo],
    // Always shown, so the grid is 15 cells (five full rows of three) with or without a planned reminder.
    ['Next reminder', c.nextReminderDate ? formatDate(c.nextReminderDate) : 'None planned'],
  ];
  return (
    <Card aria-label="Key terms">
      <dl className="facts">
        {facts.map(([k, v]) => (
          <div key={k} className="fact">
            <dt className="caption">{k}</dt>
            <dd className="fact__v">{v}</dd>
          </div>
        ))}
      </dl>
    </Card>
  );
}

function Documents({ c, open }: { c: ContractDetail; open: (d: Dialog) => void }) {
  const api = useApi();
  const toast = useToast();
  // `?doc=<id>` (from search): the document is marked in the list.
  const [params] = useSearchParams();
  const highlight = params.get('doc');
  const download = useMutation({
    mutationFn: (id: string) => api.getDocumentContent(id),
    onSuccess: (file) => saveBlob(file.blob, file.fileName),
    onError: (e) => toast.error(e, 'The document could not be downloaded. Try again.'),
  });
  return (
    <Card clip aria-labelledby="documents-heading">
      <div className="card-list-head">
        <CardTitle
          id="documents-heading"
          action={
            c.canEdit ? (
              <Button variant="tint" size="sm" icon="upload" onClick={() => open({ kind: 'upload' })}>
                Upload
              </Button>
            ) : undefined
          }
        >
          Documents
        </CardTitle>
      </div>
      {c.documents.length === 0 ? (
        <EmptyState compact title="No documents yet" text={c.canEdit ? 'Upload the contract, appendices and amendments here.' : 'Nothing has been uploaded to this contract.'} />
      ) : (
        <ul>
          {c.documents.map((d) => {
            const ext = fileExtension(d.name);
            const busy = download.isPending && download.variables === d.id;
            return (
              <li key={d.id} className={`list-row${d.id === highlight ? ' list-row--hit' : ''}`}>
                <span className={`tile ${ext === 'PDF' ? 'tone-red' : 'tone-blue'}`} aria-hidden="true">
                  {ext.slice(0, 3)}
                </span>
                <div className="kv" style={{ flex: '1 1 auto' }}>
                  <button type="button" className="link-btn link-btn--text" onClick={() => download.mutate(d.id)} aria-label={`Download ${d.name}`}>
                    {d.name}
                  </button>
                  <span className="kv__k">
                    {DOC_KIND_LABEL[d.kind] ?? d.kind} · v{d.version} · {formatDate(d.uploadedAt.slice(0, 10))} · {formatFileSize(d.size)} · {d.uploadedBy}
                  </span>
                </div>
                <Button variant="ghost" size="sm" iconOnly icon="download" loading={busy} aria-label={`Download ${d.name}`} onClick={() => download.mutate(d.id)} />
              </li>
            );
          })}
        </ul>
      )}
    </Card>
  );
}

function useObligationActions() {
  const api = useApi();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const complete = useMutation({
    mutationFn: (id: string) => api.completeObligation(id),
    onSuccess: async (o) => {
      await invalidate();
      toast.show(o.status === 'Done' ? 'Obligation marked as done' : `Marked as done. Next due ${formatDate(o.dueDate)}`);
    },
    onError: (e) => toast.error(e),
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.deleteObligation(id),
    onSuccess: async () => {
      await invalidate();
      toast.show('Obligation deleted');
    },
    onError: (e) => toast.error(e),
  });
  return { complete, remove };
}

function ObligationsTab({ c, open }: { c: ContractDetail; open: (d: Dialog) => void }) {
  const me = useMe();
  const { complete, remove } = useObligationActions();
  const [confirming, setConfirming] = useState<string | null>(null);
  const sorted = [...c.obligations].sort((a, b) => (a.status === b.status ? (a.dueDate ?? '9999').localeCompare(b.dueDate ?? '9999') : a.status === 'Open' ? -1 : 1));
  const today = todayIso();
  return (
    <Card className="tab-card">
      <CardTitle
        action={
          c.canEdit ? (
            <Button variant="tint" size="sm" icon="plus" onClick={() => open({ kind: 'obligation', obligation: null })}>
              Add obligation
            </Button>
          ) : undefined
        }
      >
        <span className="h2">Obligations</span>
      </CardTitle>
      {sorted.length === 0 ? (
        <EmptyState icon="clock" title="No obligations yet" text="Track what DAFA or the counterparty has to do during the contract, such as reports, audits and price adjustments." />
      ) : (
        <ul style={{ marginTop: 8 }}>
          {sorted.map((o) => {
            const days = o.dueDate ? daysBetween(today, o.dueDate) : null;
            const mayComplete = o.status === 'Open' && (c.canEdit || o.responsibleId === me.id);
            return (
              <li key={o.id} className="wide-row">
                <div className="wide-row__main">
                  <span className="wide-row__a" style={o.status === 'Done' ? { color: 'var(--text-2)' } : undefined}>
                    {o.description}
                  </span>
                  <span className="caption">{obligationMeta(o)}</span>
                </div>
                {o.status === 'Done' ? <Pill tone="green">Done</Pill> : days !== null && days < 0 ? <Pill tone="red">Overdue by {-days} days</Pill> : days !== null && days <= 30 ? <Pill tone="amber">Due in {days} days</Pill> : null}
                <div className="wide-row__actions">
                  {confirming === o.id ? (
                    <>
                      <Button variant="danger" size="sm" loading={remove.isPending} onClick={() => remove.mutate(o.id, { onSettled: () => setConfirming(null) })}>
                        Delete
                      </Button>
                      <Button variant="secondary" size="sm" onClick={() => setConfirming(null)}>
                        Keep
                      </Button>
                    </>
                  ) : (
                    <>
                      {mayComplete ? (
                        <Button size="sm" icon="check" loading={complete.isPending && complete.variables === o.id} onClick={() => complete.mutate(o.id)} aria-label={`Mark done: ${o.description}`}>
                          Mark done
                        </Button>
                      ) : null}
                      {c.canEdit ? (
                        <>
                          <Button variant="secondary" size="sm" iconOnly icon="edit" aria-label={`Edit: ${o.description}`} onClick={() => open({ kind: 'obligation', obligation: o })} />
                          <Button variant="secondary" size="sm" iconOnly icon="trash" aria-label={`Delete: ${o.description}`} onClick={() => setConfirming(o.id)} />
                        </>
                      ) : null}
                    </>
                  )}
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </Card>
  );
}

function ApprovalsTab({ c, onSubmit, submitting }: { c: ContractDetail; onSubmit: () => void; submitting: boolean }) {
  const me = useMe();
  const a = c.approval;
  const myStep = a?.status === 'Pending' ? a.stages.find((s) => s.order === a.currentStage)?.steps.find((s) => s.status === 'Pending' && s.approverId === me.id) : undefined;
  const rejected = a?.stages.flatMap((s) => s.steps).find((s) => s.status === 'Rejected');
  return (
    <div className="split">
      <div className="split__main">
        <Card className="tab-card" style={{ gap: 14 }}>
          <CardTitle action={a ? <Pill tone={a.status === 'Approved' ? 'green' : a.status === 'Rejected' ? 'red' : 'blue'}>{a.status === 'Pending' ? 'In progress' : a.status}</Pill> : undefined}>
            <span className="h2">Approvals</span>
          </CardTitle>
          {!a ? (
            <EmptyState
              icon="check"
              title={c.status === 'Draft' ? 'Not submitted yet' : 'No approval on record'}
              text={c.status === 'Draft' ? 'The approval route is set by the rules in Settings from the legal entity, contract type, department and value.' : 'This contract was registered without an approval in DAFA Contracts, for example because it was imported.'}
              action={c.status === 'Draft' && c.canEdit ? <Button size="md" loading={submitting} onClick={onSubmit}>Submit for approval</Button> : undefined}
            />
          ) : (
            <>
              {rejected ? (
                <p className="banner tone-red">
                  Rejected by {rejected.approverName}
                  {rejected.comment ? `: "${rejected.comment}"` : ''}. The contract is a draft again.
                </p>
              ) : null}
              <ApprovalRoute approval={a} />
            </>
          )}
        </Card>
        {myStep ? (
          <Card className="tab-card" style={{ gap: 12 }} aria-label="Your decision">
            <h2 className="card-title">Your decision as {myStep.roleLabel}</h2>
            <DecisionForm stepId={myStep.id} contract={c} />
          </Card>
        ) : null}
      </div>
      <aside className="split__aside">
        <Card pad className="stack" style={{ gap: 12 }}>
          <CardTitle>What differs from the DAFA standard</CardTitle>
          <Deviations items={c.deviations} />
        </Card>
      </aside>
    </div>
  );
}

function ActivityTab({ c, open }: { c: ContractDetail; open: (d: Dialog) => void }) {
  return (
    <Card className="tab-card">
      <CardTitle
        action={
          <Button variant="tint" size="sm" icon="compare" onClick={() => open({ kind: 'compare' })}>
            Compare versions
          </Button>
        }
      >
        <span className="h2">Activity</span>
      </CardTitle>
      {c.activity.length === 0 ? (
        <EmptyState compact title="No activity yet" />
      ) : (
        <ol>
          {c.activity.map((a, i) => (
            <li key={`${a.at}-${i}`} className="wide-row">
              <span className="wide-row__a" style={{ flex: '1 1 260px' }}>
                {a.text}
              </span>
              <span className="secondary">
                {a.who} · {formatDate(a.at.slice(0, 10))}
              </span>
            </li>
          ))}
        </ol>
      )}
    </Card>
  );
}

function Overview({ c, open, setTab }: { c: ContractDetail; open: (d: Dialog) => void; setTab: (t: Tab) => void }) {
  const api = useApi();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const unlink = useMutation({
    mutationFn: (id: string) => api.deleteLink(id),
    onSuccess: async () => {
      await invalidate();
      toast.show('Link removed');
    },
    onError: (e) => toast.error(e),
  });
  const openObligations = c.obligations.filter((o) => o.status === 'Open').sort((a, b) => (a.dueDate ?? '9999').localeCompare(b.dueDate ?? '9999'));
  const year = todayIso().slice(0, 4);

  return (
    <>
      <Lifecycle c={c} />
      {c.summary ? (
        <Card className="summary-card" aria-label="Summary">
          <span className="tile" aria-hidden="true">
            <Icon name="star" size={18} stroke={2} />
          </span>
          <div className="summary-card__text">
            <span style={{ fontWeight: 700 }}>In short</span>
            <span style={{ color: 'var(--text-3)', lineHeight: 1.5 }}>{c.summary}</span>
            <span className="caption">Summary written by AI from the contract documents. Check the contract before you act on it.</span>
          </div>
        </Card>
      ) : null}
      <div className="split">
        <div className="split__main">
          <StatusCard c={c} open={open} setTab={setTab} />
          {c.missingData.length > 0 ? (
            <p className="banner tone-amber" style={{ alignItems: 'center' }}>
              <span style={{ flex: '1 1 auto' }}>Missing data: {c.missingData.join(', ').toLowerCase()}.</span>
              {c.canEdit && c.status !== 'InApproval' ? (
                <button type="button" className="link-btn" style={{ color: 'inherit', textDecoration: 'underline' }} onClick={() => open({ kind: c.missingData.every((m) => m === 'Signed date') && c.status === 'Approved' ? 'sign' : 'edit' })}>
                  Complete it
                </button>
              ) : null}
            </p>
          ) : null}
          <Facts c={c} />
          <AdditionalDetails c={c} onEdit={c.canEdit && c.status !== 'InApproval' ? () => open({ kind: 'edit' }) : undefined} />
          <Documents c={c} open={open} />
          <div className="split" style={{ alignItems: 'stretch' }}>
            <Card pad className="stack" style={{ flex: '1 1 260px', minWidth: 0, padding: '14px 18px' }}>
              <CardTitle
                action={
                  <button type="button" className="link-btn" onClick={() => setTab('obligations')}>
                    {c.obligations.length > 0 ? 'All' : c.canEdit ? 'Add' : ''}
                  </button>
                }
              >
                Obligations
              </CardTitle>
              <div style={{ marginTop: 6 }}>
                {openObligations.length === 0 ? (
                  <span className="secondary">No open obligations.</span>
                ) : (
                  openObligations.slice(0, 3).map((o) => (
                    <div key={o.id} className="mini-row">
                      <span className="mini-row__a">{o.description}</span>
                      <span className="caption">{obligationMeta(o)}</span>
                    </div>
                  ))
                )}
              </div>
            </Card>
            <Card pad className="stack" style={{ flex: '1 1 260px', minWidth: 0, padding: '14px 18px' }}>
              <CardTitle
                action={
                  <button type="button" className="link-btn" onClick={() => setTab('activity')}>
                    All
                  </button>
                }
              >
                Recent activity
              </CardTitle>
              <div style={{ marginTop: 6 }}>
                {c.activity.length === 0 ? (
                  <span className="secondary">No activity yet.</span>
                ) : (
                  c.activity.slice(0, 3).map((a, i) => (
                    <div key={`${a.at}-${i}`} className="mini-row">
                      <span className="mini-row__a">{a.text}</span>
                      <span className="caption">
                        {formatDate(a.at.slice(0, 10))} · {a.who}
                      </span>
                    </div>
                  ))
                )}
              </div>
            </Card>
          </div>
        </div>

        <aside className="split__aside">
          <TagsCard c={c} />
          <Card pad className="stack" style={{ gap: 14 }}>
            <CardTitle>People</CardTitle>
            {c.people.map((p) => (
              <div key={p.role} className="person">
                <Avatar initials={p.initials} size="lg" />
                <div className="kv" style={{ flex: '1 1 auto', minWidth: 0 }}>
                  <span className="kv__v">
                    <PersonLink id={p.userId} name={p.name} />
                  </span>
                  <span className="kv__k">{p.role}</span>
                </div>
                {p.getsReminders ? <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--primary)' }}>Gets reminders</span> : null}
              </div>
            ))}
          </Card>

          <Card pad className="stack" style={{ gap: 10 }}>
            <CardTitle
              action={
                c.canEdit ? (
                  <button type="button" className="link-btn" onClick={() => open({ kind: 'link' })}>
                    Add link
                  </button>
                ) : undefined
              }
            >
              Linked contracts
            </CardTitle>
            {c.links.length === 0 ? (
              <span className="secondary">No linked contracts.</span>
            ) : (
              c.links.map((l) => (
                <div key={l.id} className="person" style={{ gap: 8 }}>
                  <div className="kv" style={{ flex: '1 1 auto' }}>
                    <Link to={`/contracts/${l.contractId}`} style={{ fontSize: 14, fontWeight: 600 }}>
                      {l.title}
                    </Link>
                    <span className="kv__k">{LINK_LABEL[l.kind]?.[l.direction] ?? l.kind}</span>
                  </div>
                  {c.canEdit ? <Button variant="ghost" size="sm" iconOnly icon="x" aria-label={`Remove link to ${l.title}`} loading={unlink.isPending && unlink.variables === l.id} onClick={() => unlink.mutate(l.id)} /> : null}
                </div>
              ))
            )}
          </Card>

          <Card pad className="stack" style={{ gap: 8 }}>
            <CardTitle>Reminders</CardTitle>
            {c.reminders.length === 0 ? (
              <span className="secondary">{c.renewalDecision ? 'Stopped: a renewal decision is recorded.' : c.noticeDeadline ? 'No reminders planned.' : 'No reminders: there is no notice deadline.'}</span>
            ) : (
              c.reminders.map((r) => (
                <div key={`${r.kind}-${r.dueDate}`} className="reminder-row">
                  <span>
                    {reminderLabel(r)} · {r.dueDate.startsWith(year) ? formatDayMonth(r.dueDate) : formatDate(r.dueDate)}
                  </span>
                  <span style={{ color: r.sentAt ? 'var(--green-fg)' : 'var(--text-2)', fontWeight: 600 }}>{r.sentAt ? 'Sent' : 'Planned'}</span>
                </div>
              ))
            )}
          </Card>

          <Card pad tone="tint" className="stack" style={{ gap: 8 }}>
            <CardTitle>Who can see this</CardTitle>
            <span style={{ fontSize: 13, color: 'var(--text-3)' }}>{c.access.join(' · ')}</span>
          </Card>
        </aside>
      </div>
    </>
  );
}

function RecordSkeleton() {
  return (
    <div className="page" style={{ paddingTop: 10 }} role="status" aria-label="Loading contract">
      <Skeleton width={90} height={14} />
      <div className="record-head">
        <Skeleton width={56} height={56} radius={16} />
        <div className="record-head__text">
          <Skeleton width="55%" height={28} />
          <Skeleton width="40%" height={14} />
        </div>
      </div>
      <Skeleton width={360} height={42} radius={12} style={{ maxWidth: '100%' }} />
      <Skeleton height={76} radius={18} />
      <div className="split">
        <div className="split__main">
          <Skeleton height={124} radius={20} />
          <Skeleton height={220} radius={18} />
        </div>
        <div className="split__aside">
          <Skeleton height={190} radius={18} />
          <Skeleton height={120} radius={18} />
        </div>
      </div>
    </div>
  );
}

export function Record() {
  const { id = '' } = useParams();
  const api = useApi();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const [params, setParams] = useSearchParams();
  const [dialog, setDialog] = useState<Dialog>(null);
  const config = useAppConfig();

  const tabParam = params.get('tab');
  const tab: Tab = TABS.some((t) => t.value === tabParam) ? (tabParam as Tab) : 'overview';
  const setTab = (t: Tab) => setParams(t === 'overview' ? {} : { tab: t }, { replace: true });

  const contract = useQuery({ queryKey: qk.contract(id), queryFn: () => api.getContract(id), retry: (count, e) => !(e instanceof ApiError && [403, 404].includes(e.status)) && count < 2 });

  const submit = useMutation({
    mutationFn: () => api.submitContract(id),
    onSuccess: async () => {
      await invalidate();
      toast.show('Submitted for approval');
      setTab('approvals');
    },
    // A 409 refetches everything (query client). `stale` gets the fixed sentence; `no-approval-rule`,
    // `no-approver` and `invalid-state` show the server's own explanation.
    onError: (e) => toast.error(e),
    onSettled: () => setDialog(null),
  });

  if (contract.isLoading) return <RecordSkeleton />;
  if (contract.isError || !contract.data) {
    const missing = contract.error instanceof ApiError && [403, 404].includes(contract.error.status);
    return (
      <div className="page rise" style={{ paddingTop: 10 }}>
        <Link to="/contracts" className="back-link">
          <Icon name="chevronLeft" size={14} stroke={2.4} />
          Contracts
        </Link>
        <Card>
          {missing ? (
            <EmptyState icon="doc" title="This contract is not available" text="It does not exist, or you do not have access to it. Access follows your department, the legal entities you manage and the contracts you are on." action={<ButtonLink to="/contracts" variant="secondary" size="md">Back to contracts</ButtonLink>} />
          ) : (
            <ErrorState text="The contract could not be loaded." onRetry={() => void contract.refetch()} />
          )}
        </Card>
      </div>
    );
  }

  const c = contract.data;
  const canRenew = c.canDecideRenewal && c.noticeDeadline !== null && c.renewalDecision === null;
  const dueSoon = c.noticeDeadline !== null && c.noticeDeadline <= addDays(todayIso(), 120);

  return (
    <div className="page rise" style={{ paddingTop: 6 }}>
      <Link to="/contracts" className="back-link">
        <Icon name="chevronLeft" size={14} stroke={2.4} />
        Contracts
      </Link>
      <header className="record-head">
        <span className="tile tile--xl" aria-hidden="true">
          <Icon name="doc" size={26} />
        </span>
        <div className="record-head__text">
          <div className="row-flex" style={{ gap: 10 }}>
            <h1 className="record-title">{c.title}</h1>
            <StatusPill status={c.status} />
          </div>
          <span className="page-sub">
            {c.counterpartyId ? <Link to={`/counterparties/${c.counterpartyId}`}>{c.counterparty}</Link> : c.counterparty} · {c.entityName} · {c.departmentName} · {c.contractNo}
          </span>
        </div>
        <div className="row-flex record-actions" style={{ gap: 10 }}>
          <WatchButton c={c} />
          {config.features.eSign && c.status === 'Approved' && c.canEdit ? (
            <Button variant="secondary" disabled title="No e-signature provider is connected yet">
              Send for e-signature
            </Button>
          ) : null}
          {c.storeUrl ? (
            <a className="btn btn--secondary" href={c.storeUrl} target="_blank" rel="noreferrer">
              Open in SharePoint
            </a>
          ) : null}
          {c.canEdit && c.status !== 'InApproval' ? (
            <Button variant="secondary" icon="edit" onClick={() => setDialog({ kind: 'edit' })}>
              Edit details
            </Button>
          ) : null}
          {c.status === 'Draft' && c.canEdit ? (
            <Button onClick={() => setDialog({ kind: 'submit' })}>Submit for approval</Button>
          ) : c.status === 'Approved' && c.canEdit ? (
            <Button onClick={() => setDialog({ kind: 'sign' })}>Mark as signed</Button>
          ) : c.canDecideRenewal && c.endDate === null && c.renewalDecision === null ? (
            <Button variant="secondary" onClick={() => setDialog({ kind: 'renewal', initial: 'GiveNotice' })}>
              Give notice
            </Button>
          ) : canRenew ? (
            <Button variant={dueSoon ? 'primary' : 'secondary'} onClick={() => setDialog({ kind: 'renewal' })}>
              Decide on renewal
            </Button>
          ) : null}
        </div>
      </header>

      <Segmented mode="tabs" idBase="record" ariaLabel="Contract sections" options={TABS.map((t) => (t.value === 'obligations' && c.obligations.length ? { ...t, label: `Obligations (${c.obligations.filter((o) => o.status === 'Open').length})` } : t.value === 'comments' && c.commentCount ? { ...t, label: `Comments (${c.commentCount})` } : t))} value={tab} onChange={setTab} />

      <div role="tabpanel" id={`record-panel-${tab}`} aria-labelledby={`record-tab-${tab}`} className="stack rise" style={{ gap: 16 }} key={tab}>
        {tab === 'overview' ? <Overview c={c} open={setDialog} setTab={setTab} /> : null}
        {tab === 'obligations' ? <ObligationsTab c={c} open={setDialog} /> : null}
        {tab === 'approvals' ? <ApprovalsTab c={c} onSubmit={() => setDialog({ kind: 'submit' })} submitting={submit.isPending} /> : null}
        {tab === 'comments' ? <CommentsTab c={c} /> : null}
        {tab === 'activity' ? <ActivityTab c={c} open={setDialog} /> : null}
      </div>
      {apiMode === 'mock' ? <p className="caption">Sample data. Seeded documents are placeholder files.</p> : null}

      {dialog?.kind === 'renewal' ? <RenewalDialog contractId={c.id} initial={dialog.initial} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === 'edit' ? <EditDetailsDialog contract={c} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === 'submit' ? <SubmitDialog contract={c} submitting={submit.isPending} onSubmit={() => submit.mutate()} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === 'sign' ? <SignDialog contract={c} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === 'upload' ? <UploadDialog contract={c} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === 'link' ? <LinkDialog contract={c} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === 'compare' ? <CompareDialog c={c} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === 'obligation' ? <ObligationDialog contract={c} obligation={dialog.obligation} onClose={() => setDialog(null)} /> : null}
    </div>
  );
}
