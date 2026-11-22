import { useEffect, useMemo, useRef, useState, type DragEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { ApiError } from '../../api/api';
import { permissions, qk, useApi, useAppConfig, useInvalidateContracts, useLookups, useMe, useReminderSettings, useScope } from '../../app/context';
import type { ContractDetail, ExtractResult, LinkKind, ObligationInput, Template } from '../../api/types';
import { Button, ButtonLink, Card, CardTitle, EmptyState, Field, Icon, Pill, Select, Skeleton, TextInput, useToast } from '../../components';
import { Deviations, RoutePreview } from '../../features/Approval';
import { ContractForm } from '../../features/ContractForm';
import { UPLOAD_ACCEPT, uploadError } from '../../lib/uploads';
import { formatDate, formatDayMonth, noticeDeadline, reminderSchedule } from '../../lib/dates';
import { fileExtension, formatFileSize, formatMoney, plural } from '../../lib/format';
import { applySuggestions } from '../../lib/suggestions';
import { draftToInput, emptyDraft, hasErrors, parseAmount, parseWhole, routeValue, validateDraft, type ContractDraft, type DraftErrors } from '../../lib/validation';
import { LINK_KINDS } from '../record/dialogs';
import { PersonPicker } from '../../features/Pickers';
import { TemplateChooser } from '../../features/TemplateChooser';
import { useFileError } from '../../features/fileErrors';

const STEPS = ['Upload', 'Details', 'Links and obligations', 'Approval'] as const;

type Source = { kind: 'upload'; result: ExtractResult } | { kind: 'template'; template: Template } | { kind: 'manual' };
interface PendingLink {
  toContractId: string;
  title: string;
  kind: LinkKind;
}

function useDebounced<T>(value: T, ms: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return debounced;
}

/** The placeholder value bands of the seeded approval rules (DKK). The real authority matrix comes from Settings. */
const BANDS: { label: string; min: number; max: number }[] = [
  { label: 'Under DKK 1m', min: 0, max: 1_000_000 },
  { label: 'DKK 1–5m', min: 1_000_000, max: 5_000_000 },
  { label: 'DKK 5–20m', min: 5_000_000, max: 20_000_000 },
  { label: 'Over DKK 20m', min: 20_000_000, max: Infinity },
];

/** Which band the entered value falls in. Read-only: the value comes from the form, not from the chips. */
function ValueBands({ value }: { value: number }) {
  return (
    <ul className="bands" aria-label="Value band">
      {BANDS.map((b) => {
        const on = value >= b.min && value < b.max;
        return (
          <li key={b.label} className={`band${on ? ' band--on' : ''}`} aria-current={on ? 'true' : undefined}>
            {b.label}
          </li>
        );
      })}
    </ul>
  );
}

// ---------------------------------------------------------------------------
// Step 1
// ---------------------------------------------------------------------------

function StartStep({ onFile, reading, onTemplate, onManual, preferTemplate }: { onFile: (f: File) => void; reading: boolean; onTemplate: (t: Template) => void; onManual: () => void; preferTemplate: boolean }) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);
  const onDrop = (e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setOver(false);
    const file = e.dataTransfer.files[0];
    if (file && !reading) onFile(file);
  };

  const upload = (
    <Card className="start-card" aria-labelledby="start-upload">
      <h2 className="card-title" id="start-upload">
        Upload a contract
      </h2>
      <div className="dropzone" data-over={over} onDragOver={(e) => { e.preventDefault(); setOver(true); }} onDragLeave={() => setOver(false)} onDrop={onDrop}>
        <span className="tile tile--lg" aria-hidden="true">
          <Icon name="upload" stroke={2} />
        </span>
        {reading ? (
          <span role="status" style={{ fontWeight: 600 }}>
            Reading the file and looking for the key terms…
          </span>
        ) : (
          <>
            <span style={{ fontWeight: 600 }}>Drop a PDF or Word file here</span>
            <span className="caption">AI reads the file and suggests the details. You check each suggestion.</span>
          </>
        )}
        <input
          ref={inputRef}
          type="file"
          className="sr-only"
          tabIndex={-1}
          aria-hidden="true"
          accept={UPLOAD_ACCEPT}
          onChange={(e) => {
            const file = e.target.files?.[0];
            e.target.value = '';
            if (file) onFile(file);
          }}
        />
        <Button size="md" loading={reading} onClick={() => inputRef.current?.click()}>
          Choose a file
        </Button>
      </div>
    </Card>
  );

  const fromTemplate = (
    <Card className="start-card" aria-labelledby="start-template">
      <h2 className="card-title" id="start-template">
        Start from a template
      </h2>
      <span className="secondary">The draft is written from the DAFA template with the details you fill in, and saved to the contract's documents.</span>
      <TemplateChooser disabled={reading} onPick={onTemplate} />
    </Card>
  );

  return (
    <div className="flow__main" style={{ flexBasis: '100%' }}>
      <div className="stack" style={{ gap: 6 }}>
        <h1 className="h1" style={{ fontSize: 28 }}>
          New contract
        </h1>
        <span className="page-sub">Start from the contract file or from a DAFA template. Nothing is saved until the last step.</span>
      </div>
      <div className="start-grid">
        {preferTemplate ? fromTemplate : upload}
        {preferTemplate ? upload : fromTemplate}
      </div>
      <button type="button" className="link-btn" style={{ alignSelf: 'flex-start' }} disabled={reading} onClick={onManual}>
        Continue without a file
      </button>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Step 3
// ---------------------------------------------------------------------------

function LinksStep({ links, setLinks, obligations, setObligations, defaultResponsible }: { links: PendingLink[]; setLinks: (l: PendingLink[]) => void; obligations: ObligationInput[]; setObligations: (o: ObligationInput[]) => void; defaultResponsible: string }) {
  const api = useApi();
  const lookups = useLookups();
  const [q, setQ] = useState('');
  const [kind, setKind] = useState<LinkKind>('Related');
  const term = useDebounced(q.trim(), 200);
  const query = { q: term, pageSize: 6, sort: 'title' as const };
  const results = useQuery({ queryKey: qk.contractList(query), queryFn: () => api.getContracts(query), enabled: term.length >= 2 });
  const found = (results.data?.items ?? []).filter((i) => !links.some((l) => l.toContractId === i.id));

  const [description, setDescription] = useState('');
  const [dueDate, setDueDate] = useState('');
  const [frequency, setFrequency] = useState('');
  const [responsibleId, setResponsibleId] = useState(defaultResponsible);
  const [obError, setObError] = useState<{ description?: string; frequency?: string }>({});
  const userName = (id: string) => lookups.users.find((u) => u.id === id)?.name ?? '–';

  const addObligation = () => {
    const months = parseWhole(frequency);
    const errors: typeof obError = {};
    if (!description.trim()) errors.description = 'Describe what has to be done';
    if (months !== null && (Number.isNaN(months) || months < 1)) errors.frequency = 'Enter whole months, or leave it empty';
    setObError(errors);
    if (Object.keys(errors).length) return;
    setObligations([...obligations, { description: description.trim(), dueDate: dueDate || null, frequencyMonths: months, responsibleId: responsibleId || defaultResponsible }]);
    setDescription('');
    setDueDate('');
    setFrequency('');
  };

  return (
    <div className="flow__main" style={{ flexBasis: '100%' }}>
      <div className="stack" style={{ gap: 6 }}>
        <h1 className="h1" style={{ fontSize: 28 }}>
          Links and obligations
        </h1>
        <span className="page-sub">Both are optional. You can also add them later on the contract.</span>
      </div>

      <Card pad className="stack" style={{ gap: 14, padding: 18 }} aria-labelledby="links-heading">
        <CardTitle id="links-heading">Linked contracts</CardTitle>
        <span className="secondary">Link the framework agreement, the contract this one amends, or a related NDA.</span>
        <div className="inline-form">
          <Field label="Find a contract" hint="Type at least two letters of the title, counterparty or number.">
            {(p) => <TextInput {...p} type="search" value={q} onChange={(e) => setQ(e.target.value)} />}
          </Field>
          <Field label="How are they related">{(p) => <Select {...p} value={kind} onChange={(e) => setKind(e.target.value as LinkKind)} options={LINK_KINDS} />}</Field>
        </div>
        {term.length >= 2 ? (
          results.isLoading ? (
            <Skeleton height={44} radius={12} />
          ) : results.isError ? (
            <span className="field__error" role="alert">The search did not work. Try again.</span>
          ) : found.length === 0 ? (
            <span className="secondary">No contracts match "{term}".</span>
          ) : (
            <ul className="stack" style={{ gap: 6 }} aria-label="Search results">
              {found.map((i) => (
                <li key={i.id} className="wide-row" style={{ borderTop: 0, padding: 0 }}>
                  <div className="wide-row__main">
                    <span className="wide-row__a">{i.title}</span>
                    <span className="caption">
                      {i.counterparty} · {i.entityName} · {i.contractNo}
                    </span>
                  </div>
                  <Button variant="tint" size="sm" icon="link" aria-label={`Link ${i.title}`} onClick={() => { setLinks([...links, { toContractId: i.id, title: i.title, kind }]); setQ(''); }}>
                    Link
                  </Button>
                </li>
              ))}
            </ul>
          )
        ) : null}
        {links.length > 0 ? (
          <ul aria-label="Contracts to link">
            {links.map((l) => (
              <li key={l.toContractId} className="wide-row">
                <div className="wide-row__main">
                  <span className="wide-row__a">{l.title}</span>
                  <span className="caption">{LINK_KINDS.find((k) => k.value === l.kind)?.label}</span>
                </div>
                <Button variant="secondary" size="sm" iconOnly icon="x" aria-label={`Remove link to ${l.title}`} onClick={() => setLinks(links.filter((x) => x.toContractId !== l.toContractId))} />
              </li>
            ))}
          </ul>
        ) : null}
      </Card>

      <Card pad className="stack" style={{ gap: 14, padding: 18 }} aria-labelledby="obligations-heading">
        <CardTitle id="obligations-heading">Obligations</CardTitle>
        <span className="secondary">Things DAFA or the counterparty must do during the contract. The responsible person is reminded 14 days before the due date.</span>
        <div className="inline-form">
          <Field label="What has to be done" error={obError.description} className="form-grid__full">
            {(p) => <TextInput {...p} value={description} onChange={(e) => setDescription(e.target.value)} placeholder="For example: Send the annual volume report" />}
          </Field>
          <Field label="Due date" optional>
            {(p) => <TextInput {...p} type="date" value={dueDate} onChange={(e) => setDueDate(e.target.value)} />}
          </Field>
          <Field label="Repeats every (months)" optional error={obError.frequency}>
            {(p) => <TextInput {...p} inputMode="numeric" value={frequency} onChange={(e) => setFrequency(e.target.value)} placeholder="12" />}
          </Field>
          <Field label="Responsible">{(p) => <PersonPicker {...p} value={responsibleId || null} clearLabel="Clear responsible" onChange={(id) => setResponsibleId(id ?? '')} />}</Field>
          <Button variant="tint" icon="plus" onClick={addObligation}>
            Add obligation
          </Button>
        </div>
        {obligations.length > 0 ? (
          <ul aria-label="Obligations to add">
            {obligations.map((o, i) => (
              <li key={`${o.description}-${i}`} className="wide-row">
                <div className="wide-row__main">
                  <span className="wide-row__a">{o.description}</span>
                  <span className="caption">
                    {[o.dueDate ? `Due ${formatDate(o.dueDate)}` : 'No due date', o.frequencyMonths ? `every ${o.frequencyMonths} months` : null, userName(o.responsibleId)].filter(Boolean).join(' · ')}
                  </span>
                </div>
                <Button variant="secondary" size="sm" iconOnly icon="x" aria-label={`Remove obligation: ${o.description}`} onClick={() => setObligations(obligations.filter((_, k) => k !== i))} />
              </li>
            ))}
          </ul>
        ) : null}
      </Card>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export function NewContract() {
  const api = useApi();
  const me = useMe();
  const lookups = useLookups();
  const scope = useScope();
  const toast = useToast();
  const fileError = useFileError();
  const navigate = useNavigate();
  const client = useQueryClient();
  const invalidate = useInvalidateContracts();
  const [params] = useSearchParams();
  const headingRef = useRef<HTMLDivElement>(null);

  const config = useAppConfig();
  const rules = { customFields: config.customFields, fieldRules: config.fieldRules, enforceRequired: true };
  const [step, setStep] = useState(0);
  const [source, setSource] = useState<Source | null>(null);
  const [draft, setDraft] = useState<ContractDraft>(() =>
    emptyDraft({ ownerId: me.id, entityId: scope.entityId || (me.managedEntityIds[0] ?? ''), departmentId: me.departmentIds.length === 1 ? (me.departmentIds[0] ?? '') : '' }),
  );
  const [pending, setPending] = useState<ReadonlySet<string>>(new Set());
  const [accepted, setAccepted] = useState<ReadonlySet<string>>(new Set());
  const [errors, setErrors] = useState<DraftErrors>({});
  const [tried, setTried] = useState(false);
  const [links, setLinks] = useState<PendingLink[]>([]);
  const [obligations, setObligations] = useState<ObligationInput[]>([]);
  const replaceRef = useRef<HTMLInputElement>(null);

  const goTo = (n: number) => {
    setStep(n);
    window.scrollTo(0, 0);
    window.setTimeout(() => headingRef.current?.focus({ preventScroll: true }), 0);
  };

  const extract = useMutation({
    mutationFn: (file: File) => api.extract(file),
    onSuccess: (result) => {
      const applied = applySuggestions(draft, result.suggestions, lookups);
      const next = !applied.draft.summary && result.summary ? { ...applied.draft, summary: result.summary } : applied.draft;
      setDraft(next);
      // Release 4: a suggested counterparty name is matched to the master data (exact name); the
      // user still accepts the suggestion. Without a match the picker starts with the name.
      if (!next.supplierId && next.counterpartyName.trim()) {
        const name = next.counterpartyName.trim().toLowerCase();
        void api
          .getCounterparties(next.counterpartyName.trim())
          .then((list) => {
            const hit = list.find((c) => c.isActive && c.name.trim().toLowerCase() === name);
            if (hit) setDraft((d) => (d.supplierId || d.counterpartyName.trim().toLowerCase() !== name ? d : { ...d, supplierId: hit.id, counterpartyName: hit.name }));
          })
          .catch(() => undefined);
      }
      setPending(new Set(applied.fields));
      setAccepted(new Set());
      setSource({ kind: 'upload', result });
      setErrors({});
      setTried(false);
      goTo(1);
      if (result.note) toast.show(result.note);
      else toast.show(`${plural(applied.fields.length, 'field')} suggested from ${result.fileName}`);
    },
    onError: (e, file) => fileError(e, 'The file could not be read. Try again.', () => extract.mutate(file)),
  });
  /** Checks type and size before the file is sent for reading. */
  const readFile = (file: File) => {
    const problem = uploadError(file);
    if (problem) toast.error(null, `${file.name}: ${problem}.`);
    else extract.mutate(file);
  };

  const change = (patch: Partial<ContractDraft>) => {
    const next = { ...draft, ...patch };
    setDraft(next);
    let nextPending = pending;
    const touched = Object.keys(patch).filter((f) => pending.has(f));
    if (touched.length > 0) {
      // Changing a suggested value counts as handling the suggestion.
      const p = new Set(pending);
      for (const f of touched) p.delete(f);
      nextPending = p;
      setPending(p);
    }
    if (tried) setErrors(validateDraft(next, nextPending, rules));
  };

  const accept = (fields: Iterable<string>) => {
    const p = new Set(pending);
    const a = new Set(accepted);
    for (const f of fields) {
      if (p.delete(f)) a.add(f);
    }
    setPending(p);
    setAccepted(a);
    if (tried) setErrors(validateDraft(draft, p, rules));
  };

  const pickTemplate = (template: Template) => {
    setSource({ kind: 'template', template });
    setPending(new Set());
    setAccepted(new Set());
    if (template.typeId && !draft.typeId) setDraft({ ...draft, typeId: template.typeId });
    goTo(1);
  };

  const validateDetails = (): boolean => {
    setTried(true);
    const found = validateDraft(draft, pending, rules);
    setErrors(found);
    if (!hasErrors(found)) return true;
    const n = Object.keys(found).length;
    toast.error(null, `${n === 1 ? 'One field needs' : `${n} fields need`} your attention before you continue.`);
    window.setTimeout(() => document.querySelector<HTMLElement>('.flow [aria-invalid="true"]')?.focus(), 0);
    return false;
  };

  // ----- calculated side panel -----
  const reminderSettings = useReminderSettings();
  const notice = parseWhole(draft.noticePeriodDays);
  const deadline = draft.openEnded || !draft.endDate ? null : noticeDeadline(draft.endDate, notice !== null && !Number.isNaN(notice) ? notice : null);
  const reminders = reminderSchedule(deadline, reminderSettings.noticeDays, reminderSettings.escalationDays).filter((r) => r.kind !== 'Escalation');
  const entityName = lookups.entities.find((e) => e.id === draft.entityId)?.name;
  const departmentName = lookups.departments.find((d) => d.id === draft.departmentId)?.name;
  const typeName = lookups.contractTypes.find((t) => t.id === draft.typeId)?.name;
  const recipient = (draft.reminderRecipient || 'Owner').toLowerCase();

  const previewQuery = useDebounced(
    useMemo(() => ({ entityId: draft.entityId || undefined, typeId: draft.typeId || undefined, departmentId: draft.departmentId || undefined, value: routeValue(draft) }), [draft]),
    300,
  );
  const canPreview = !!(previewQuery.entityId && previewQuery.typeId && previewQuery.departmentId);
  const preview = useQuery({ queryKey: qk.approvalPreview(previewQuery), queryFn: () => api.previewApproval(previewQuery), enabled: canPreview && step >= 1 });
  // The form already names entity, type and department, but the debounced query has not caught up: still loading.
  const previewPending = !canPreview && !!(draft.entityId && draft.typeId && draft.departmentId);

  // ----- save -----
  const save = useMutation({
    mutationFn: async (submit: boolean): Promise<{ contract: ContractDetail; warnings: string[] }> => {
      let uploadId: string | null = source?.kind === 'upload' ? source.result.uploadId : null;
      if (source?.kind === 'template') {
        const made = await api.fromTemplate({
          templateId: source.template.id,
          fields: {
            title: draft.title,
            entity: entityName ?? '',
            counterparty: draft.counterpartyName,
            startDate: formatDate(draft.startDate, ''),
            endDate: draft.openEnded ? 'further notice' : formatDate(draft.endDate, ''),
            noticePeriodDays: draft.noticePeriodDays,
            currency: draft.currency,
            paymentTerms: draft.paymentTerms,
            governingLaw: draft.governingLaw,
          },
        });
        uploadId = made.uploadId;
      }
      let contract = await api.createContract({ ...draftToInput(draft), uploadId });
      // The draft exists from here on: later failures are reported, not thrown, so the user lands on it.
      const warnings: string[] = [];
      const attempt = async (what: string, fn: () => Promise<unknown>) => {
        try {
          await fn();
        } catch (e) {
          warnings.push(`${what}: ${e instanceof ApiError ? (e.problem.detail ?? e.problem.title) : 'failed'}`);
        }
      };
      for (const l of links) await attempt(`Link to ${l.title}`, () => api.createLink(contract.id, { toContractId: l.toContractId, kind: l.kind }));
      for (const o of obligations) await attempt(`Obligation "${o.description}"`, () => api.createObligation(contract.id, o));
      if (submit) {
        await attempt('Submit for approval', async () => {
          contract = await api.submitContract(contract.id);
        });
      }
      return { contract, warnings };
    },
    onSuccess: async ({ contract, warnings }, submit) => {
      client.removeQueries({ queryKey: qk.contract(contract.id) });
      await invalidate();
      if (warnings.length > 0) toast.error(null, `The draft was saved, but not everything went through. ${warnings.join('. ')}`);
      else toast.show(submit ? `${contract.contractNo} submitted for approval` : `${contract.contractNo} saved as a draft`);
      navigate(`/contracts/${contract.id}${contract.status === 'InApproval' ? '?tab=approvals' : ''}`, { replace: true });
    },
    onError: (e) => {
      if (e instanceof ApiError && e.problem.errors) {
        setErrors(Object.fromEntries(Object.entries(e.problem.errors).map(([k, v]) => [k, v[0] ?? 'Not valid'])) as DraftErrors);
        setTried(true);
        goTo(1);
      }
      toast.error(e, 'The contract could not be saved. Try again.');
    },
  });

  const next = () => {
    if (step === 0) {
      if (!source) setSource({ kind: 'manual' });
      goTo(1);
    } else if (step === 1) {
      if (validateDetails()) goTo(2);
    } else if (step === 2) {
      goTo(3);
    } else {
      save.mutate(true);
    }
  };

  if (!permissions(me).canCreate) {
    return (
      <div className="center-page">
        <Card className="center-card">
          <EmptyState icon="doc" title="You cannot create contracts" text="Your role is reader or approver. Ask a contract owner in your department, or an administrator, to register the contract." action={<ButtonLink to="/contracts" variant="secondary" size="md">Back to contracts</ButtonLink>} />
        </Card>
      </div>
    );
  }

  const busy = extract.isPending || save.isPending;

  const aside = (
    <aside className="flow__aside">
      <Card tone="primary" className="calc-card" aria-label="Calculated from the dates">
        <span className="calc-card__k">Calculated from the dates</span>
        <div className="stack" style={{ gap: 2 }}>
          <span className="calc-card__k">Notice deadline</span>
          <span className="calc-card__big">{deadline ? formatDate(deadline) : draft.openEnded ? 'None, open-ended' : 'Enter end date and notice period'}</span>
        </div>
        <div className="stack" style={{ gap: 2 }}>
          <span className="calc-card__k">Reminders to the {recipient}</span>
          <span className="calc-card__v">{reminders.length > 0 && deadline ? `${reminders.map((r) => formatDayMonth(r.dueDate)).join(' · ')} ${deadline.slice(0, 4) === reminders[0]?.dueDate.slice(0, 4) ? deadline.slice(0, 4) : ''}`.trim() : 'No reminders without a notice deadline'}</span>
        </div>
        <div className="stack" style={{ gap: 2 }}>
          <span className="calc-card__k">Filed under</span>
          <span className="calc-card__v">{entityName && departmentName ? `${entityName} › ${departmentName}` : 'Choose legal entity and department'}</span>
        </div>
      </Card>
      <Card pad className="stack" style={{ gap: 14 }} aria-labelledby="route-heading">
        <CardTitle id="route-heading">Approval route</CardTitle>
        <ValueBands value={routeValue(draft)} />
        <RoutePreview preview={canPreview ? preview.data : undefined} loading={previewPending || (canPreview && preview.isLoading)} />
        <span className="caption">From the approval rules in Settings, for {formatMoney(routeValue(draft), 'DKK')} (the larger of total and annual value). Value bands are placeholders.</span>
      </Card>
      {source?.kind === 'upload' ? (
        <Card pad className="stack" style={{ gap: 12 }}>
          <CardTitle>What differs from the DAFA standard</CardTitle>
          <Deviations items={source.result.deviations} />
        </Card>
      ) : null}
    </aside>
  );

  return (
    <div className="flow">
      <header className="flow__bar">
        <Link to="/contracts" style={{ fontSize: 14, fontWeight: 600 }}>
          Cancel
        </Link>
        <div className="spacer" />
        <ol className="steps" aria-label="Steps">
          {STEPS.map((label, i) => {
            const state = i === step ? 'on' : i < step ? 'done' : 'todo';
            const inner = (
              <>
                <span className="step__n" aria-hidden="true">
                  {state === 'done' ? <Icon name="check" size={12} stroke={3} /> : i + 1}
                </span>
                <span className="step__label">{label}</span>
                <span className="sr-only">{state === 'done' ? ' (done)' : state === 'on' ? ' (current step)' : ''}</span>
              </>
            );
            return (
              <li key={label} aria-current={state === 'on' ? 'step' : undefined}>
                {state === 'done' ? (
                  <button type="button" className="step step--done" disabled={busy} onClick={() => goTo(i)}>
                    {inner}
                  </button>
                ) : (
                  <span className={`step${state === 'on' ? ' step--on' : ''}`}>{inner}</span>
                )}
              </li>
            );
          })}
        </ol>
        <div className="spacer" />
        {step > 0 ? (
          <Button variant="secondary" size="md" disabled={busy} onClick={() => goTo(step - 1)}>
            Back
          </Button>
        ) : null}
        {/* The last step has its two actions (save as draft, submit) together in the page. */}
        {step === 0 || step === 3 ? null : (
          <Button size="md" className="flow__next" style={{ height: 40, borderRadius: 12, fontSize: 14, padding: '0 20px' }} disabled={busy} onClick={next}>
            Continue
          </Button>
        )}
      </header>

      <main className="flow__body">
        <div ref={headingRef} tabIndex={-1} className="sr-only" aria-live="polite">
          Step {step + 1} of 4: {STEPS[step]}
        </div>

        {step === 0 ? <StartStep onFile={readFile} reading={extract.isPending} onTemplate={pickTemplate} onManual={next} preferTemplate={params.get('start') === 'template'} /> : null}

        {step === 1 ? (
          <>
            <div className="flow__main">
              <div className="stack" style={{ gap: 6 }}>
                <h1 className="h1" style={{ fontSize: 28 }}>
                  New contract
                </h1>
                <span className="page-sub">
                  {source?.kind === 'upload' ? 'We read the uploaded file and filled in what we could. Check each suggestion.' : source?.kind === 'template' ? 'Fill in the details. They are written into the template when you save.' : 'Fill in the details of the contract.'}
                </span>
              </div>

              {source?.kind === 'upload' ? (
                <Card className="file-card" aria-label="Uploaded file">
                  <span className={`tile tile--lg ${fileExtension(source.result.fileName) === 'PDF' ? 'tone-red' : 'tone-blue'}`} style={{ width: 44, height: 44 }} aria-hidden="true">
                    {fileExtension(source.result.fileName).slice(0, 3)}
                  </span>
                  <div className="file-card__text">
                    <span style={{ fontWeight: 600, overflowWrap: 'anywhere' }}>{source.result.fileName}</span>
                    <span className="caption">
                      {source.result.note ? formatFileSize(source.result.size) : `${formatFileSize(source.result.size)} · ${plural(source.result.pages, 'page')} · read by AI, ${plural(pending.size + accepted.size, 'field')} suggested`}
                    </span>
                    {source.result.note ? <span className="file-card__note">{source.result.note}</span> : null}
                  </div>
                  <input
                    ref={replaceRef}
                    type="file"
                    className="sr-only"
                    tabIndex={-1}
                    aria-hidden="true"
                    accept={UPLOAD_ACCEPT}
                    onChange={(e) => {
                      const file = e.target.files?.[0];
                      e.target.value = '';
                      if (file) readFile(file);
                    }}
                  />
                  <Button variant="secondary" size="md" loading={extract.isPending} onClick={() => replaceRef.current?.click()}>
                    Replace file
                  </Button>
                  <Button variant="tint" size="md" disabled={pending.size === 0} onClick={() => accept([...pending])}>
                    {pending.size === 0 ? 'All suggestions handled' : 'Accept all suggestions'}
                  </Button>
                </Card>
              ) : source?.kind === 'template' ? (
                <Card className="file-card" aria-label="Template">
                  <span className="tile tile--lg tone-blue" style={{ width: 44, height: 44 }} aria-hidden="true">
                    DOC
                  </span>
                  <div className="file-card__text">
                    <span style={{ fontWeight: 600 }}>{source.template.name} template</span>
                    <span className="caption">The draft document is created from the template when you save.</span>
                  </div>
                  <Button variant="secondary" size="md" onClick={() => goTo(0)}>
                    Change
                  </Button>
                </Card>
              ) : null}

              {tried && hasErrors(errors) ? (
                <p className="error-summary" role="alert">
                  {Object.keys(errors).length === 1 ? 'One field needs' : `${Object.keys(errors).length} fields need`} your attention. {Object.keys(errors).length === 1 ? 'It is' : 'They are'} marked in red below.
                </p>
              ) : null}

              <Card>
                <ContractForm applyTypeDefaults showTags draft={draft} errors={errors} onPatch={change} suggestions={source?.kind === 'upload' ? { pending, accepted, onAccept: (f) => accept([f]) } : undefined} />
              </Card>
              <div className="row-flex" style={{ justifyContent: 'flex-end' }}>
                <Button variant="secondary" onClick={next} disabled={busy}>
                  Continue
                </Button>
              </div>
            </div>
            {aside}
          </>
        ) : null}

        {step === 2 ? (
          <>
            <LinksStep links={links} setLinks={setLinks} obligations={obligations} setObligations={setObligations} defaultResponsible={draft.responsibleId || draft.ownerId || me.id} />
            <div className="row-flex" style={{ justifyContent: 'flex-end', flex: '1 1 100%' }}>
              <Button variant="secondary" onClick={next}>
                Continue
              </Button>
            </div>
          </>
        ) : null}

        {step === 3 ? (
          <>
            <div className="flow__main">
              <div className="stack" style={{ gap: 6 }}>
                <h1 className="h1" style={{ fontSize: 28 }}>
                  Approval
                </h1>
                <span className="page-sub">Check the contract and send it for approval, or save it as a draft and submit it later.</span>
              </div>
              <Card aria-label="Contract summary">
                <dl className="facts">
                  {(
                    [
                      ['Title', draft.title],
                      ['Counterparty', draft.counterpartyName],
                      ['Contract type', typeName ?? '–'],
                      ['Legal entity', entityName ?? '–'],
                      ['Department', departmentName ?? '–'],
                      ['Owner', lookups.users.find((u) => u.id === draft.ownerId)?.name ?? '–'],
                      ['Term', draft.openEnded ? `From ${formatDate(draft.startDate)}, open-ended` : `${formatDate(draft.startDate)} – ${formatDate(draft.endDate)}`],
                      ['Notice period', `${draft.noticePeriodDays} days`],
                      ['Renewal', draft.autoRenew ? `Automatic, ${draft.renewalMonths} months` : draft.openEnded ? 'Open-ended' : 'Fixed term'],
                      ['Annual value', formatMoney(parseAmount(draft.annualValue), draft.currency)],
                      ['Total value', formatMoney(parseAmount(draft.totalValue), draft.currency)],
                      ['Change of control', draft.changeOfControl ? 'Yes, has a clause' : 'No clause'],
                    ] as [string, string][]
                  ).map(([k, v]) => (
                    <div key={k} className="fact">
                      <dt className="caption">{k}</dt>
                      <dd className="fact__v">{v}</dd>
                    </div>
                  ))}
                </dl>
              </Card>
              <Card pad className="stack" style={{ gap: 10, padding: 18 }}>
                <CardTitle>Saved with the contract</CardTitle>
                <div className="row-flex" style={{ gap: 8 }}>
                  <Pill tone="blue">{source?.kind === 'upload' ? source.result.fileName : source?.kind === 'template' ? `Draft from the ${source.template.name} template` : 'No document'}</Pill>
                  <Pill>{plural(links.length, 'linked contract')}</Pill>
                  <Pill>{plural(obligations.length, 'obligation')}</Pill>
                </div>
              </Card>
              <div className="row-flex" style={{ justifyContent: 'flex-end' }}>
                <Button variant="secondary" loading={save.isPending && save.variables === false} disabled={busy} onClick={() => save.mutate(false)}>
                  Save as draft
                </Button>
                <Button loading={save.isPending && save.variables === true} disabled={busy} onClick={() => save.mutate(true)}>
                  Submit for approval
                </Button>
              </div>
            </div>
            {aside}
          </>
        ) : null}
      </main>
    </div>
  );
}
