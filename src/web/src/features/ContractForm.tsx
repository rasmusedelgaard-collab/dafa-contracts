import { useEffect, useId, useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { qk, useApi, useLookups } from '../app/context';
import { Button, Field, Pill, Segmented, Select, Textarea, TextInput } from '../components';
import type { ContractDraft, DraftErrors, DraftField } from '../lib/validation';

export interface SuggestionState {
  /** Fields holding a suggestion the user has not accepted or changed. */
  pending: ReadonlySet<string>;
  accepted: ReadonlySet<string>;
  onAccept: (field: DraftField) => void;
}

export interface ContractFormProps {
  draft: ContractDraft;
  errors: DraftErrors;
  onChange: <K extends DraftField>(field: K, value: ContractDraft[K]) => void;
  suggestions?: SuggestionState;
  /** Renders the form without card padding (inside a dialog). */
  flat?: boolean;
}

const YES_NO = [
  { value: 'yes', label: 'Yes' },
  { value: 'no', label: 'No' },
];
const toYesNo = (v: boolean | null) => (v === null ? null : v ? 'yes' : 'no');

function useDebounced<T>(value: T, ms: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return debounced;
}

/** The contract details form, shared by New contract (step 2) and Edit details. */
export function ContractForm({ draft, errors, onChange, suggestions, flat }: ContractFormProps) {
  const api = useApi();
  const lookups = useLookups();
  const listId = useId();
  const groupId = useId();

  const supplierQuery = useDebounced(draft.counterpartyName.trim(), 200);
  const suppliers = useQuery({ queryKey: qk.suppliers(supplierQuery), queryFn: () => api.getSuppliers(supplierQuery), staleTime: 60_000 });

  const users = lookups.users.map((u) => ({ value: u.id, label: u.name }));
  const refs = (list: { id: string; name: string }[]) => list.map((x) => ({ value: x.id, label: x.name }));

  /** Label tag and accept button for a field that carries an AI suggestion. */
  const ai = (field: DraftField, label: string): { tag?: ReactNode; after?: ReactNode; suggested: boolean } => {
    if (!suggestions) return { suggested: false };
    if (suggestions.pending.has(field)) {
      return {
        suggested: true,
        tag: (
          <Pill tone="blue" small>
            Suggested
          </Pill>
        ),
        after: <Button iconOnly icon="check" aria-label={`Accept suggestion for ${label}`} onClick={() => suggestions.onAccept(field)} />,
      };
    }
    if (suggestions.accepted.has(field)) {
      return {
        suggested: false,
        tag: (
          <Pill tone="green" small>
            Accepted
          </Pill>
        ),
      };
    }
    return { suggested: false };
  };

  const text = (field: 'title' | 'noticePeriodDays' | 'renewalMonths' | 'annualValue' | 'totalValue' | 'paymentTerms' | 'priceAdjustment' | 'governingLaw' | 'liabilityCap' | 'exclusivity', label: string, opts: { optional?: boolean; hint?: string; numeric?: boolean; full?: boolean; placeholder?: string } = {}) => {
    const s = ai(field, label);
    return (
      <Field label={label} optional={opts.optional} error={errors[field]} hint={opts.hint} tag={s.tag} after={s.after} className={opts.full ? 'form-grid__full' : undefined}>
        {(p) => <TextInput {...p} suggested={s.suggested} inputMode={opts.numeric ? 'decimal' : undefined} placeholder={opts.placeholder} value={draft[field]} onChange={(e) => onChange(field, e.target.value)} />}
      </Field>
    );
  };

  const select = (field: 'typeId' | 'entityId' | 'departmentId' | 'ownerId' | 'delegateId' | 'responsibleId' | 'currency', label: string, options: { value: string; label: string }[], opts: { optional?: boolean; placeholder?: string } = {}) => {
    const s = ai(field, label);
    return (
      <Field label={label} optional={opts.optional} error={errors[field]} tag={s.tag} after={s.after}>
        {(p) => <Select {...p} suggested={s.suggested} value={draft[field]} onChange={(e) => onChange(field, e.target.value)} options={options} placeholder={opts.placeholder ?? (opts.optional ? 'None' : 'Choose')} />}
      </Field>
    );
  };

  const date = (field: 'startDate' | 'endDate' | 'priceAdjustmentDate', label: string, opts: { optional?: boolean; disabled?: boolean; hint?: string } = {}) => {
    const s = ai(field, label);
    return (
      <Field label={label} optional={opts.optional} error={errors[field]} hint={opts.hint} tag={s.tag} after={opts.disabled ? undefined : s.after}>
        {(p) => <TextInput {...p} type="date" suggested={s.suggested && !opts.disabled} disabled={opts.disabled} value={opts.disabled ? '' : draft[field]} onChange={(e) => onChange(field, e.target.value)} />}
      </Field>
    );
  };

  const yesNo = (field: 'autoRenew' | 'changeOfControl', label: string, hint?: string) => {
    const s = ai(field, label);
    const errorId = `${groupId}-${field}-error`;
    return (
      <div className="field">
        <div className="field__top">
          <span className="field__label" id={`${groupId}-${field}`}>
            {label}
          </span>
          {s.tag}
        </div>
        <div className="field__row">
          <Segmented mode="radio" block ariaLabel={label} options={YES_NO} value={toYesNo(draft[field])} invalid={!!errors[field]} describedBy={errors[field] ? errorId : undefined} onChange={(v) => onChange(field, v === 'yes')} />
          {s.after}
        </div>
        {errors[field] ? (
          <span className="field__error" id={errorId} role="alert">
            {errors[field]}
          </span>
        ) : null}
        {hint ? <span className="field__hint">{hint}</span> : null}
      </div>
    );
  };

  const cp = ai('counterpartyName', 'Counterparty');

  return (
    <div className={`form-grid${flat ? ' form-grid--flat' : ''}`}>
      <h2 className="form-section">Contract</h2>
      {text('title', 'Title', { full: true, placeholder: 'For example: Freight framework – Nordics' })}
      <Field label="Counterparty" error={errors.counterpartyName} tag={cp.tag} after={cp.after} hint="Pick a supplier from the list or type a new name.">
        {(p) => (
          <TextInput
            {...p}
            suggested={cp.suggested}
            list={listId}
            autoComplete="off"
            value={draft.counterpartyName}
            onChange={(e) => {
              const name = e.target.value;
              onChange('counterpartyName', name);
              const match = suppliers.data?.find((s) => s.name.toLowerCase() === name.trim().toLowerCase());
              onChange('supplierId', match?.id ?? null);
            }}
          />
        )}
      </Field>
      <datalist id={listId}>
        {(suppliers.data ?? []).map((s) => (
          <option key={s.id} value={s.name} />
        ))}
      </datalist>
      {select('typeId', 'Contract type', refs(lookups.contractTypes))}
      {select('entityId', 'Legal entity', refs(lookups.entities))}
      {select('departmentId', 'Department', refs(lookups.departments))}

      <h2 className="form-section">People</h2>
      {select('ownerId', 'Owner', users)}
      {select('delegateId', 'Delegate', users, { optional: true })}
      {select('responsibleId', 'Responsible', users, { optional: true })}
      <Field label="Reminders go to" error={errors.reminderRecipient} hint="120, 60 and 30 days before the notice deadline.">
        {(p) => (
          <Select
            {...p}
            value={draft.reminderRecipient}
            onChange={(e) => onChange('reminderRecipient', e.target.value as ContractDraft['reminderRecipient'])}
            options={[
              { value: 'Owner', label: 'Owner' },
              { value: 'Delegate', label: 'Delegate' },
              { value: 'Responsible', label: 'Responsible' },
            ]}
          />
        )}
      </Field>

      <h2 className="form-section">Term and renewal</h2>
      {date('startDate', 'Start date')}
      <div className="stack" style={{ gap: 8 }}>
        {date('endDate', 'End date', { disabled: draft.openEnded })}
        <label className="check">
          <input type="checkbox" checked={draft.openEnded} onChange={(e) => onChange('openEnded', e.target.checked)} />
          Open-ended (no end date)
        </label>
      </div>
      {text('noticePeriodDays', 'Notice period (days)', { numeric: true, hint: 'Enter 0 if there is none. 3 months is about 90 days.' })}
      {yesNo('autoRenew', 'Renews automatically')}
      {draft.autoRenew ? text('renewalMonths', 'Renewal period (months)', { numeric: true }) : null}

      <h2 className="form-section">Value</h2>
      {select('currency', 'Currency', lookups.currencies.map((c) => ({ value: c, label: c })))}
      {text('annualValue', 'Annual value', { numeric: true, hint: 'Annual or total value is required. Enter 0 if there is none.' })}
      {text('totalValue', 'Total value', { numeric: true, optional: true, hint: 'Decides the approval route when filled in.' })}
      {yesNo('changeOfControl', 'Change-of-control clause', 'Can the counterparty terminate or change terms if DAFA changes owner?')}

      <h2 className="form-section">More terms</h2>
      {text('paymentTerms', 'Payment terms', { optional: true, placeholder: '60 days net' })}
      {text('governingLaw', 'Governing law', { optional: true, placeholder: 'Danish law' })}
      {text('priceAdjustment', 'Price adjustment', { optional: true, placeholder: 'Annual, index-linked' })}
      {date('priceAdjustmentDate', 'Next price adjustment', { optional: true })}
      {text('liabilityCap', 'Liability cap', { optional: true, placeholder: '12 months of fees' })}
      {text('exclusivity', 'Exclusivity', { optional: true, placeholder: 'None' })}
      <Field label="Summary" optional error={errors.summary} className="form-grid__full" hint="A few sentences on what the contract is about.">
        {(p) => <Textarea {...p} rows={3} value={draft.summary} onChange={(e) => onChange('summary', e.target.value)} />}
      </Field>
    </div>
  );
}
