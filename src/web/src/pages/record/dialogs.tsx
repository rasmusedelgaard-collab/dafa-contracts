import { useRef, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { qk, useApi, useInvalidateContracts, useLookups } from '../../app/context';
import { ApiError } from '../../api/api';
import type { ContractDetail, DocumentKind, LinkKind, Obligation } from '../../api/types';
import { Button, Field, Modal, Select, Skeleton, TextInput, useToast } from '../../components';
import { ContractForm } from '../../features/ContractForm';
import { isIsoDate, todayIso } from '../../lib/dates';
import { formatFileSize } from '../../lib/format';
import { draftToInput, fieldsToDraft, hasErrors, parseWhole, validateDraft, type ContractDraft, type DraftErrors, type DraftField } from '../../lib/validation';

interface DialogProps {
  contract: ContractDetail;
  onClose: () => void;
}

/** Maps RFC 7807 `errors` from the API back onto form fields. */
function serverErrors(e: unknown): DraftErrors {
  if (!(e instanceof ApiError) || !e.problem.errors) return {};
  return Object.fromEntries(Object.entries(e.problem.errors).map(([k, v]) => [k, v[0] ?? 'Not valid'])) as DraftErrors;
}

export function EditDetailsDialog({ contract, onClose }: DialogProps) {
  const api = useApi();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const [draft, setDraft] = useState<ContractDraft>(() => fieldsToDraft(contract));
  const [errors, setErrors] = useState<DraftErrors>({});
  const [tried, setTried] = useState(false);

  const save = useMutation({
    mutationFn: () => api.updateContract(contract.id, draftToInput(draft)),
    onSuccess: async () => {
      await invalidate();
      toast.show('Details saved');
      onClose();
    },
    onError: (e) => {
      setErrors(serverErrors(e));
      toast.error(e);
    },
  });

  const onChange = <K extends DraftField>(field: K, value: ContractDraft[K]) => {
    const next = { ...draft, [field]: value };
    setDraft(next);
    if (tried) setErrors(validateDraft(next));
  };

  const submit = () => {
    setTried(true);
    const found = validateDraft(draft);
    setErrors(found);
    if (hasErrors(found)) {
      toast.error(null, `Check the ${Object.keys(found).length === 1 ? 'field' : 'fields'} marked in red.`);
      return;
    }
    save.mutate();
  };

  return (
    <Modal
      wide
      title="Edit details"
      subtitle={`${contract.title} · ${contract.contractNo}`}
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            Save changes
          </Button>
        </>
      }
    >
      <ContractForm flat draft={draft} errors={errors} onChange={onChange} />
    </Modal>
  );
}

const SIGN_METHODS = ['Signed by both parties', 'E-signature', 'Wet ink, scanned', 'Signed by the counterparty only'];

export function SignDialog({ contract, onClose }: DialogProps) {
  const api = useApi();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const [signedDate, setSignedDate] = useState(todayIso());
  const [method, setMethod] = useState(SIGN_METHODS[0] ?? '');
  const [error, setError] = useState<string>();

  const save = useMutation({
    mutationFn: () => api.signContract(contract.id, { signedDate, method }),
    onSuccess: async () => {
      await invalidate();
      toast.show('Marked as signed. The contract is now active');
      onClose();
    },
    onError: (e) => toast.error(e),
  });

  const submit = () => {
    if (!isIsoDate(signedDate)) return setError('Enter the date the contract was signed');
    if (signedDate > todayIso()) return setError('The signing date cannot be in the future');
    setError(undefined);
    save.mutate();
  };

  return (
    <Modal
      title="Mark as signed"
      subtitle="The contract becomes active and reminders start. Upload the signed copy under Documents."
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            Mark as signed
          </Button>
        </>
      }
    >
      <Field label="Signed on" error={error}>
        {(p) => <TextInput {...p} type="date" max={todayIso()} value={signedDate} onChange={(e) => setSignedDate(e.target.value)} />}
      </Field>
      <Field label="How it was signed">{(p) => <Select {...p} value={method} onChange={(e) => setMethod(e.target.value)} options={SIGN_METHODS.map((m) => ({ value: m, label: m }))} />}</Field>
    </Modal>
  );
}

const DOC_KINDS: { value: DocumentKind; label: string }[] = [
  { value: 'Main', label: 'Main contract' },
  { value: 'Appendix', label: 'Appendix' },
  { value: 'Amendment', label: 'Amendment' },
  { value: 'Other', label: 'Other' },
];

export function UploadDialog({ contract, onClose }: DialogProps) {
  const api = useApi();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const inputRef = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [kind, setKind] = useState<DocumentKind>(contract.documents.some((d) => d.kind === 'Main') ? 'Appendix' : 'Main');
  const [error, setError] = useState<string>();

  const save = useMutation({
    mutationFn: (f: File) => api.uploadDocument(contract.id, f, kind),
    onSuccess: async (_d, f) => {
      await invalidate();
      toast.show(`${f.name} uploaded`);
      onClose();
    },
    onError: (e) => toast.error(e),
  });

  const submit = () => {
    if (!file) return setError('Choose a file');
    if (file.size > 25 * 1024 * 1024) return setError('The file is larger than 25 MB');
    setError(undefined);
    save.mutate(file);
  };

  return (
    <Modal
      title="Upload a document"
      subtitle={contract.title}
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" icon="upload" loading={save.isPending}>
            Upload
          </Button>
        </>
      }
    >
      <Field label="File" error={error} hint={file ? `${file.name} · ${formatFileSize(file.size)}` : 'PDF or Word, up to 25 MB. A file with the same name becomes a new version.'}>
        {(p) => (
          <input
            {...p}
            ref={inputRef}
            type="file"
            className="input"
            style={{ padding: '9px 14px', height: 'auto' }}
            onChange={(e) => {
              setFile(e.target.files?.[0] ?? null);
              setError(undefined);
            }}
          />
        )}
      </Field>
      <Field label="Document type">{(p) => <Select {...p} value={kind} onChange={(e) => setKind(e.target.value as DocumentKind)} options={DOC_KINDS} />}</Field>
    </Modal>
  );
}

export function ObligationDialog({ contract, obligation, onClose }: DialogProps & { obligation: Obligation | null }) {
  const api = useApi();
  const lookups = useLookups();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const [description, setDescription] = useState(obligation?.description ?? '');
  const [dueDate, setDueDate] = useState(obligation?.dueDate ?? '');
  const [frequency, setFrequency] = useState(obligation?.frequencyMonths ? String(obligation.frequencyMonths) : '');
  const [responsibleId, setResponsibleId] = useState(obligation?.responsibleId ?? contract.responsibleId ?? contract.ownerId);
  const [errors, setErrors] = useState<{ description?: string; frequency?: string; responsibleId?: string }>({});

  const save = useMutation({
    mutationFn: () => {
      const input = { description: description.trim(), dueDate: dueDate || null, frequencyMonths: parseWhole(frequency), responsibleId };
      return obligation ? api.updateObligation(obligation.id, input) : api.createObligation(contract.id, input);
    },
    onSuccess: async () => {
      await invalidate();
      toast.show(obligation ? 'Obligation saved' : 'Obligation added');
      onClose();
    },
    onError: (e) => toast.error(e),
  });

  const submit = () => {
    const months = parseWhole(frequency);
    const found: typeof errors = {};
    if (!description.trim()) found.description = 'Describe what has to be done';
    if (months !== null && (Number.isNaN(months) || months < 1)) found.frequency = 'Enter a whole number of months, or leave it empty';
    if (!responsibleId) found.responsibleId = 'Choose who is responsible';
    setErrors(found);
    if (Object.keys(found).length === 0) save.mutate();
  };

  return (
    <Modal
      title={obligation ? 'Edit obligation' : 'Add obligation'}
      subtitle={contract.title}
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            {obligation ? 'Save' : 'Add obligation'}
          </Button>
        </>
      }
    >
      <Field label="What has to be done" error={errors.description}>
        {(p) => <TextInput {...p} value={description} onChange={(e) => setDescription(e.target.value)} placeholder="For example: Send the annual volume report" />}
      </Field>
      <div className="form-grid form-grid--flat">
        <Field label="Due date" optional hint="A reminder goes out 14 days before.">
          {(p) => <TextInput {...p} type="date" value={dueDate} onChange={(e) => setDueDate(e.target.value)} />}
        </Field>
        <Field label="Repeats every (months)" optional error={errors.frequency}>
          {(p) => <TextInput {...p} inputMode="numeric" value={frequency} onChange={(e) => setFrequency(e.target.value)} placeholder="12" />}
        </Field>
      </div>
      <Field label="Responsible" error={errors.responsibleId}>
        {(p) => <Select {...p} value={responsibleId} onChange={(e) => setResponsibleId(e.target.value)} options={lookups.users.map((u) => ({ value: u.id, label: u.name }))} placeholder="Choose" />}
      </Field>
    </Modal>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export const LINK_KINDS: { value: LinkKind; label: string }[] = [
  { value: 'Related', label: 'Related contract' },
  { value: 'Amendment', label: 'This is an amendment to it' },
  { value: 'Appendix', label: 'This is an appendix to it' },
  { value: 'Framework', label: 'It is the framework for this' },
];

export function LinkDialog({ contract, onClose }: DialogProps) {
  const api = useApi();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const [q, setQ] = useState('');
  const [toId, setToId] = useState('');
  const [kind, setKind] = useState<LinkKind>('Related');
  const [error, setError] = useState<string>();
  const term = q.trim();
  const query = { q: term || undefined, pageSize: 8, sort: 'title' as const };
  const results = useQuery({ queryKey: qk.contractList(query), queryFn: () => api.getContracts(query) });
  const linked = new Set([contract.id, ...contract.links.map((l) => l.contractId)]);
  const options = (results.data?.items ?? []).filter((i) => !linked.has(i.id));

  const save = useMutation({
    mutationFn: () => api.createLink(contract.id, { toContractId: toId, kind }),
    onSuccess: async () => {
      await invalidate();
      toast.show('Contracts linked');
      onClose();
    },
    onError: (e) => toast.error(e),
  });

  const submit = () => {
    if (!toId) return setError('Choose a contract to link to');
    setError(undefined);
    save.mutate();
  };

  return (
    <Modal
      title="Link a contract"
      subtitle={contract.title}
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" icon="link" loading={save.isPending}>
            Link
          </Button>
        </>
      }
    >
      <Field label="Find a contract" hint="Search by title, counterparty or contract number.">
        {(p) => (
          <TextInput
            {...p}
            type="search"
            value={q}
            onChange={(e) => {
              setQ(e.target.value);
              setToId('');
            }}
          />
        )}
      </Field>
      <fieldset style={{ border: 0, padding: 0, margin: 0 }}>
        <legend className="field__label" style={{ marginBottom: 6 }}>
          Contract
        </legend>
        {results.isLoading ? (
          <Skeleton height={56} radius={12} />
        ) : options.length === 0 ? (
          <span className="secondary">{term ? `No other contracts match "${term}".` : 'No other contracts to link to.'}</span>
        ) : (
          <div className="stack" style={{ gap: 8 }}>
            {options.map((o) => (
              <label key={o.id} className="choice">
                <input type="radio" name="link-target" checked={toId === o.id} onChange={() => { setToId(o.id); setError(undefined); }} />
                <span className="choice__title">{o.title}</span>
                <span className="choice__text">
                  {o.counterparty} · {o.entityName} · {o.contractNo}
                </span>
              </label>
            ))}
          </div>
        )}
        {error ? (
          <span className="field__error" role="alert" style={{ display: 'block', marginTop: 8 }}>
            {error}
          </span>
        ) : null}
      </fieldset>
      <Field label="How are they related">{(p) => <Select {...p} value={kind} onChange={(e) => setKind(e.target.value as LinkKind)} options={LINK_KINDS} />}</Field>
    </Modal>
  );
}
