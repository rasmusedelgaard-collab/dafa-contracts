// E-signature (owner item H): "Send for signature" for permitted users, a Signature card with the
// status per signatory, resend/cancel, and in Simulated mode a "Simulate signing" control.
import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { qk, useApi, useAppConfig, useInvalidateContracts, useLookups } from '../app/context';
import type { ContractDetail, SignatureRequest, SignerInput, SignerStatus, SigningOrder } from '../api/types';
import { Avatar, Button, Card, ErrorState, Field, Modal, Pill, Segmented, Skeleton, Textarea, TextInput, useToast } from '../components';
import { formatDateTime } from '../lib/dates';
import { problemErrors } from '../pages/settings/Reference';
import { useFileError } from './fileErrors';
import { PersonPicker } from './Pickers';

const SIGNER_TONE: Record<SignerStatus, 'gray' | 'blue' | 'green' | 'red' | 'amber'> = { Waiting: 'gray', Sent: 'blue', Viewed: 'amber', Signed: 'green', Declined: 'red', Expired: 'gray' };
const REQUEST_LABEL: Record<SignatureRequest['status'], string> = { Draft: 'Draft', Sent: 'Out for signature', Completed: 'Signed by everyone', Declined: 'Declined', Expired: 'Expired', Cancelled: 'Cancelled' };

/** Record action: "Send for signature", or who to ask when the user has no signing right. */
export function SignatureAction({ c, onOpen }: { c: ContractDetail; onOpen: () => void }) {
  const config = useAppConfig();
  const [asking, setAsking] = useState(false);
  if (!config.eSign?.enabled || c.status !== 'Approved' || c.signature?.status === 'Sent') return null;
  if (c.canSendForSignature) {
    return (
      <Button icon="send" onClick={onOpen}>
        Send for signature
      </Button>
    );
  }
  return (
    <>
      <Button variant="secondary" onClick={() => setAsking(true)}>
        Send for signature
      </Button>
      {asking ? <AskColleague contractId={c.id} onClose={() => setAsking(false)} /> : null}
    </>
  );
}

function AskColleague({ contractId, onClose }: { contractId: string; onClose: () => void }) {
  const api = useApi();
  const p = useQuery({ queryKey: [...qk.contract(contractId), 'signature-permissions'], queryFn: () => api.getSignaturePermissions(contractId) });
  return (
    <Modal title="Ask a colleague with signing rights" onClose={onClose} footer={<Button onClick={onClose}>Close</Button>}>
      {p.isLoading ? (
        <Skeleton height={80} />
      ) : p.isError || !p.data ? (
        <ErrorState onRetry={() => void p.refetch()} />
      ) : (
        <div className="stack" style={{ gap: 12 }}>
          <p className="secondary">{p.data.reason ?? 'You cannot send this contract for signature.'} These people can send it:</p>
          {p.data.permittedUsers.length === 0 ? (
            <p className="caption">Nobody with access to this contract has signing rights. Ask an administrator.</p>
          ) : (
            <ul className="people-list" aria-label="People with signing rights">
              {p.data.permittedUsers.map((u) => (
                <li key={u.id}>
                  <Avatar initials={u.initials} size="sm" />
                  <span className="stack" style={{ gap: 0 }}>
                    <span>{u.name}</span>
                    <span className="caption">{[u.departmentName, u.entityName, u.email].filter(Boolean).join(' · ')}</span>
                  </span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </Modal>
  );
}

interface SignerDraft {
  kind: 'Internal' | 'External';
  userId: string | null;
  name: string;
  email: string;
}

export function SendForSignatureDialog({ c, onClose }: { c: ContractDetail; onClose: () => void }) {
  const api = useApi();
  const toast = useToast();
  const fileError = useFileError();
  const invalidate = useInvalidateContracts();
  const lookups = useLookups();
  const [docs, setDocs] = useState<string[]>(c.documents.filter((d) => d.kind === 'Main').slice(-1).map((d) => d.id));
  const [signers, setSigners] = useState<SignerDraft[]>([{ kind: 'Internal', userId: null, name: '', email: '' }, { kind: 'External', userId: null, name: '', email: '' }]);
  const [order, setOrder] = useState<SigningOrder>('Sequential');
  const [message, setMessage] = useState('');
  const [preview, setPreview] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const input = () => ({ documentIds: docs, order, message, signers: signers.map((s, i): SignerInput => (s.kind === 'Internal' ? { kind: 'Internal', userId: s.userId, order: order === 'Parallel' ? 1 : i + 1 } : { kind: 'External', name: s.name.trim(), email: s.email.trim(), order: order === 'Parallel' ? 1 : i + 1 })) });
  const send = useMutation({
    mutationFn: () => api.createSignatureRequest(c.id, input()),
    onSuccess: async () => {
      await invalidate();
      toast.show('Sent for signature');
      onClose();
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      fileError(e, 'The contract could not be sent for signature.', () => send.mutate());
    },
  });
  const check = () => {
    const e: Record<string, string> = {};
    if (docs.length === 0) e.documentIds = 'Choose at least one document';
    if (signers.length === 0) e.signers = 'Add at least one signatory';
    signers.forEach((s, i) => {
      if (s.kind === 'Internal' && !s.userId) e[`signers.${i}.userId`] = 'Choose a person';
      if (s.kind === 'External' && !s.name.trim()) e[`signers.${i}.name`] = 'Enter the name';
      if (s.kind === 'External' && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(s.email.trim())) e[`signers.${i}.email`] = 'Enter a valid e-mail';
    });
    setErrors(e);
    return Object.keys(e).length === 0;
  };
  const set = (i: number, patch: Partial<SignerDraft>) => setSigners((list) => list.map((s, j) => (j === i ? { ...s, ...patch } : s)));
  const move = (i: number, by: number) =>
    setSigners((list) => {
      const next = [...list];
      const [x] = next.splice(i, 1);
      next.splice(i + by, 0, x as SignerDraft);
      return next;
    });

  return (
    <Modal
      wide
      title="Send for signature"
      subtitle={c.title}
      onClose={onClose}
      onSubmit={() => {
        if (!check()) return;
        if (!preview) setPreview(true);
        else send.mutate();
      }}
      footer={
        <>
          <Button variant="secondary" onClick={preview ? () => setPreview(false) : onClose}>
            {preview ? 'Back' : 'Cancel'}
          </Button>
          <Button type="submit" loading={send.isPending}>
            {preview ? 'Send now' : 'Preview'}
          </Button>
        </>
      }
    >
      {preview ? (
        <div className="stack" style={{ gap: 12 }} aria-label="Preview">
          <p className="secondary">Check before sending. Signatories get an e-mail from the signing service.</p>
          <dl className="kv-list">
            <dt>Documents</dt>
            <dd>{c.documents.filter((d) => docs.includes(d.id)).map((d) => d.name).join(', ')}</dd>
            <dt>Order</dt>
            <dd>{order === 'Sequential' ? 'One after the other, in this order' : 'Everyone at the same time'}</dd>
            <dt>Signatories</dt>
            <dd>
              <ol style={{ margin: 0, paddingLeft: 18 }}>
                {signers.map((s, i) => (
                  <li key={i}>{s.kind === 'Internal' ? `${nameOf(s.userId)} (DAFA)` : `${s.name} <${s.email}>`}</li>
                ))}
              </ol>
            </dd>
            <dt>Message</dt>
            <dd>{message.trim() || 'The default message from Settings'}</dd>
          </dl>
        </div>
      ) : (
        <div className="stack" style={{ gap: 16 }}>
          <fieldset className="fieldset">
            <legend className="field__label">Documents to sign</legend>
            {c.documents.length === 0 ? <p className="caption">Upload the contract document first.</p> : null}
            <div className="stack" style={{ gap: 6, marginTop: 6 }}>
              {c.documents.map((d) => (
                <label key={d.id} className="check">
                  <input type="checkbox" checked={docs.includes(d.id)} onChange={(e) => setDocs(e.target.checked ? [...docs, d.id] : docs.filter((x) => x !== d.id))} />
                  {d.name} <span className="caption">· {d.kind}</span>
                </label>
              ))}
            </div>
            {errors.documentIds ? <span className="field__error">{errors.documentIds}</span> : null}
          </fieldset>
          <div className="stack" style={{ gap: 8 }}>
            <span className="field__label" id="sign-order">
              Signing order
            </span>
            <Segmented mode="radio" ariaLabel="Signing order" options={[{ value: 'Sequential', label: 'In order' }, { value: 'Parallel', label: 'All at once' }]} value={order} onChange={setOrder} />
          </div>
          <fieldset className="fieldset">
            <legend className="field__label">Signatories</legend>
            <ol className="signer-list">
              {signers.map((s, i) => (
                <li key={i} className="signer-row">
                  <Segmented mode="radio" size="sm" ariaLabel={`Signatory ${i + 1}: DAFA or external`} options={[{ value: 'Internal', label: 'DAFA' }, { value: 'External', label: 'External' }]} value={s.kind} onChange={(k) => set(i, { kind: k })} />
                  {s.kind === 'Internal' ? (
                    <Field label={`Signatory ${i + 1}`} error={errors[`signers.${i}.userId`]}>
                      {(p) => <PersonPicker {...p} value={s.userId} clearLabel={`Clear signatory ${i + 1}`} onChange={(id) => set(i, { userId: id })} />}
                    </Field>
                  ) : (
                    <>
                      <Field label={`Signatory ${i + 1} name`} error={errors[`signers.${i}.name`]}>
                        {(p) => <TextInput {...p} value={s.name} onChange={(e) => set(i, { name: e.target.value })} />}
                      </Field>
                      <Field label={`Signatory ${i + 1} e-mail`} error={errors[`signers.${i}.email`]}>
                        {(p) => <TextInput {...p} type="email" value={s.email} onChange={(e) => set(i, { email: e.target.value })} />}
                      </Field>
                    </>
                  )}
                  <span className="row-flex" style={{ gap: 4 }}>
                    <Button variant="ghost" size="sm" iconOnly icon="chevronUp" disabled={i === 0 || order === 'Parallel'} aria-label={`Move signatory ${i + 1} up`} onClick={() => move(i, -1)} />
                    <Button variant="ghost" size="sm" iconOnly icon="chevronDown" disabled={i === signers.length - 1 || order === 'Parallel'} aria-label={`Move signatory ${i + 1} down`} onClick={() => move(i, 1)} />
                    <Button variant="ghost" size="sm" iconOnly icon="trash" aria-label={`Remove signatory ${i + 1}`} onClick={() => setSigners(signers.filter((_, j) => j !== i))} />
                  </span>
                </li>
              ))}
            </ol>
            {errors.signers ? <span className="field__error">{errors.signers}</span> : null}
            <span className="row-flex" style={{ gap: 8 }}>
              <Button variant="tint" size="sm" icon="plus" onClick={() => setSigners([...signers, { kind: 'Internal', userId: null, name: '', email: '' }])}>
                Add DAFA signatory
              </Button>
              <Button variant="tint" size="sm" icon="plus" onClick={() => setSigners([...signers, { kind: 'External', userId: null, name: '', email: '' }])}>
                Add external signatory
              </Button>
            </span>
          </fieldset>
          <Field label="Message" optional hint="Leave empty for the default message from Settings.">
            {(p) => <Textarea {...p} rows={3} value={message} onChange={(e) => setMessage(e.target.value)} />}
          </Field>
        </div>
      )}
    </Modal>
  );

  function nameOf(id: string | null) {
    return id ? (lookups.users.find((u) => u.id === id)?.name ?? '') : '';
  }
}

/** The Signature card on the record: status per signatory, resend, cancel, simulate. */
export function SignatureCard({ c }: { c: ContractDetail }) {
  const api = useApi();
  const config = useAppConfig();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const r = c.signature;
  const act = useMutation({
    mutationFn: async ({ kind, signerId }: { kind: 'resend' | 'cancel' | 'View' | 'Sign' | 'Decline'; signerId?: string }) => {
      if (!r) throw new Error('No request');
      if (kind === 'resend') return api.resendSignatureRequest(r.id);
      if (kind === 'cancel') return api.cancelSignatureRequest(r.id);
      return api.simulateSignature(r.id, { signerId: signerId ?? '', action: kind, reason: kind === 'Decline' ? 'Simulated decline' : undefined });
    },
    onSuccess: async (x, v) => {
      await invalidate();
      toast.show(v.kind === 'resend' ? 'Reminder sent' : v.kind === 'cancel' ? 'Signature request cancelled' : x.status === 'Completed' ? 'Signed by everyone: the signed document is stored and the contract is active' : 'Simulated');
    },
    onError: (e) => toast.error(e),
  });
  if (!r) return null;
  const simulated = config.eSign?.provider === 'Simulated' && r.status === 'Sent';
  return (
    <Card clip aria-labelledby="signature-heading">
      <div className="card-list-head">
        <h2 className="card-title" id="signature-heading">
          Signature
        </h2>
        <Pill tone={r.status === 'Completed' ? 'green' : r.status === 'Sent' ? 'blue' : r.status === 'Declined' ? 'red' : 'gray'}>{REQUEST_LABEL[r.status]}</Pill>
        <div className="spacer" />
        {r.status === 'Sent' ? (
          <span className="row-flex" style={{ gap: 6 }}>
            <Button variant="ghost" size="sm" loading={act.isPending && act.variables?.kind === 'resend'} onClick={() => act.mutate({ kind: 'resend' })}>
              Resend
            </Button>
            <Button variant="ghost" size="sm" loading={act.isPending && act.variables?.kind === 'cancel'} onClick={() => act.mutate({ kind: 'cancel' })}>
              Cancel request
            </Button>
          </span>
        ) : null}
      </div>
      <p className="caption" style={{ padding: '0 20px' }}>
        Sent {r.sentAt ? formatDateTime(r.sentAt) : '–'} by {r.createdBy} · {r.order === 'Sequential' ? 'in order' : 'all at once'} · {r.provider === 'Simulated' ? 'simulated signing (test)' : r.provider}
        {r.completedAt ? ` · completed ${formatDateTime(r.completedAt)}` : ''}
      </p>
      <ol className="signer-status" aria-label="Signatories">
        {r.signers.map((s) => (
          <li key={s.id}>
            <span className="stack" style={{ gap: 0, minWidth: 0 }}>
              <strong className="truncate">{s.name}</strong>
              <span className="caption truncate">
                {s.kind === 'Internal' ? 'DAFA' : 'External'} · {s.email}
                {s.at ? ` · ${formatDateTime(s.at)}` : ''}
                {s.declineReason ? ` · ${s.declineReason}` : ''}
              </span>
            </span>
            <Pill tone={SIGNER_TONE[s.status]} small>
              {s.status}
            </Pill>
            {simulated && (s.status === 'Sent' || s.status === 'Viewed') ? (
              <span className="row-flex simulate" style={{ gap: 4 }} role="group" aria-label={`Simulate signing for ${s.name}`}>
                <Button variant="tint" size="sm" onClick={() => act.mutate({ kind: 'Sign', signerId: s.id })} loading={act.isPending && act.variables?.signerId === s.id && act.variables.kind === 'Sign'}>
                  Simulate signing
                </Button>
                <Button variant="ghost" size="sm" onClick={() => act.mutate({ kind: 'Decline', signerId: s.id })}>
                  Decline
                </Button>
              </span>
            ) : null}
          </li>
        ))}
      </ol>
    </Card>
  );
}
