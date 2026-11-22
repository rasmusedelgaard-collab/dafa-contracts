import { useId, type ReactNode } from 'react';
import { listDays, useAppConfig, useLookups, useReminderSettings } from '../app/context';
import { fieldsForType, rulesForType } from '../lib/customFields';
import { CustomFieldInput, TagPicker } from './CustomFields';
import { CounterpartyPicker, PersonPicker } from './Pickers';
import type { LookupUser } from '../api/types';
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
  /**
   * One update per user action. A field that sets two values at once (the counterparty name and
   * the matched supplier) sends them in one patch, so neither is lost to a stale draft.
   */
  onPatch: (patch: Partial<ContractDraft>) => void;
  suggestions?: SuggestionState;
  /** Renders the form without card padding (inside a dialog). */
  flat?: boolean;
  /** Field names from `ContractDetail.lockedFields`: shown, but disabled and marked "Locked". */
  lockedFields?: string[];
  /** New contract: apply the type's default values (Settings > Field rules) when the type is chosen. */
  applyTypeDefaults?: boolean;
  /** Show the tag picker (New contract and Edit details when the user may tag). */
  showTags?: boolean;
}

const YES_NO = [
  { value: 'yes', label: 'Yes' },
  { value: 'no', label: 'No' },
];
const toYesNo = (v: boolean | null) => (v === null ? null : v ? 'yes' : 'no');

/** The contract details form, shared by New contract (step 2) and Edit details. */
export function ContractForm({ draft, errors, onPatch, suggestions, flat, lockedFields, applyTypeDefaults, showTags }: ContractFormProps) {
  const onChange = <K extends DraftField>(field: K, value: ContractDraft[K]) => onPatch({ [field]: value } as Partial<ContractDraft>);
  const locked = (field: string) => lockedFields?.includes(field) ?? false;
  const reminderSettings = useReminderSettings();
  const lookups = useLookups();
  const config = useAppConfig();
  const typeRules = rulesForType(config.fieldRules, draft.typeId);
  /** A field the contract type makes required (Settings > Field rules) is not marked optional. */
  const ruleRequired = (field: string) => (typeRules?.required as string[] | undefined)?.includes(field) ?? false;
  const extraFields = fieldsForType(config.customFields, draft.typeId);
  const groupId = useId();

  const refs = (list: { id: string; name: string }[]) => list.map((x) => ({ value: x.id, label: x.name }));

  /** Label tag and accept button for a field that carries an AI suggestion. */
  const ai = (field: DraftField, label: string): { tag?: ReactNode; after?: ReactNode; suggested: boolean } => {
    if (locked(field)) {
      return {
        suggested: false,
        tag: (
          <Pill tone="gray" small>
            Locked
          </Pill>
        ),
      };
    }
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
      <Field label={label} optional={opts.optional && !ruleRequired(field)} error={errors[field]} hint={opts.hint} tag={s.tag} after={s.after} className={opts.full ? 'form-grid__full' : undefined}>
        {(p) => <TextInput {...p} suggested={s.suggested} disabled={locked(field)} inputMode={opts.numeric ? 'decimal' : undefined} placeholder={opts.placeholder} value={draft[field]} onChange={(e) => onChange(field, e.target.value)} />}
      </Field>
    );
  };

  const select = (field: 'typeId' | 'entityId' | 'departmentId' | 'ownerId' | 'delegateId' | 'responsibleId' | 'currency', label: string, options: { value: string; label: string }[], opts: { optional?: boolean; placeholder?: string } = {}) => {
    const s = ai(field, label);
    return (
      <Field label={label} optional={opts.optional && !ruleRequired(field)} error={errors[field]} tag={s.tag} after={s.after}>
        {(p) => <Select {...p} suggested={s.suggested} disabled={locked(field)} value={draft[field]} onChange={(e) => onChange(field, e.target.value)} options={options} placeholder={opts.placeholder ?? (opts.optional ? 'None' : 'Choose')} />}
      </Field>
    );
  };

  /** Person type-ahead (owner item C): type part of a name and click a match. */
  const person = (field: 'ownerId' | 'delegateId' | 'responsibleId', label: string, opts: { optional?: boolean; filter?: (u: LookupUser) => boolean } = {}) => {
    const s = ai(field, label);
    return (
      <Field label={label} optional={opts.optional && !ruleRequired(field)} error={errors[field]} tag={s.tag} after={s.after}>
        {(p) => <PersonPicker {...p} suggested={s.suggested} disabled={locked(field)} value={draft[field] || null} filter={opts.filter} clearLabel={`Clear ${label.toLowerCase()}`} onChange={(id) => onChange(field, id ?? '')} />}
      </Field>
    );
  };

  const date = (field: 'startDate' | 'endDate' | 'priceAdjustmentDate', label: string, given: { optional?: boolean; disabled?: boolean; hint?: string } = {}) => {
    const s = ai(field, label);
    // A locked date keeps showing its value; an open-ended end date is disabled and empty.
    const isLocked = locked(field);
    const opts = given;
    return (
      <Field label={label} optional={opts.optional} error={errors[field]} hint={opts.hint} tag={s.tag} after={opts.disabled ? undefined : s.after}>
        {(p) => <TextInput {...p} type="date" suggested={s.suggested && !opts.disabled} disabled={opts.disabled || isLocked} value={opts.disabled ? '' : draft[field]} onChange={(e) => onChange(field, e.target.value)} />}
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
        <fieldset className="field__row" disabled={locked(field)} style={{ border: 0, padding: 0, margin: 0, minWidth: 0 }}>
          <Segmented mode="radio" block ariaLabel={label} options={YES_NO} value={toYesNo(draft[field])} invalid={!!errors[field]} describedBy={errors[field] ? errorId : undefined} onChange={(v) => onChange(field, v === 'yes')} />
          {s.after}
        </fieldset>
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
      <Field
        label="Counterparty"
        error={errors.counterpartyName ?? errors.supplierId}
        tag={cp.tag}
        after={cp.after}
        hint={locked('counterpartyName') ? undefined : !draft.supplierId && draft.counterpartyName ? `“${draft.counterpartyName}” is not chosen yet: pick it from the list${config.canCreateCounterparty ? ' or create it' : ''}.` : 'Type part of the name and pick the counterparty from the list.'}
      >
        {(p) => (
          <CounterpartyPicker
            {...p}
            suggested={cp.suggested}
            disabled={locked('counterpartyName')}
            value={{ supplierId: draft.supplierId, name: draft.counterpartyName }}
            onChange={(v) => onPatch({ supplierId: v.supplierId, counterpartyName: v.name })}
          />
        )}
      </Field>
      <Field label="Contract type" error={errors.typeId} tag={ai('typeId', 'Contract type').tag} after={ai('typeId', 'Contract type').after}>
        {(p) => (
          <Select
            {...p}
            suggested={ai('typeId', 'Contract type').suggested}
            disabled={locked('typeId')}
            value={draft.typeId}
            placeholder="Choose"
            options={refs(lookups.contractTypes.filter((t) => t.isActive !== false || t.id === draft.typeId))}
            onChange={(e) => {
              const typeId = e.target.value;
              const patch: Partial<ContractDraft> = { typeId };
              // Defaults for the type fill only fields the user has not filled in.
              const d = applyTypeDefaults ? rulesForType(config.fieldRules, typeId)?.defaults : undefined;
              if (d) {
                if (d.currency && (draft.currency === 'DKK' || !draft.currency)) patch.currency = d.currency;
                if (d.noticePeriodDays !== null && !draft.noticePeriodDays) patch.noticePeriodDays = String(d.noticePeriodDays);
                if (d.reminderRecipient && draft.reminderRecipient === 'Owner') patch.reminderRecipient = d.reminderRecipient;
                if (d.autoRenew !== null && draft.autoRenew === null) patch.autoRenew = d.autoRenew;
                if (d.renewalMonths !== null && !draft.renewalMonths) patch.renewalMonths = String(d.renewalMonths);
              }
              onPatch(patch);
            }}
          />
        )}
      </Field>
      {select('entityId', 'Legal entity', refs(lookups.entities.filter((x) => x.isActive !== false || x.id === draft.entityId)))}
      {select('departmentId', 'Department', refs(lookups.departments.filter((x) => x.isActive !== false || x.id === draft.departmentId)))}

      <h2 className="form-section">People</h2>
      {person('ownerId', 'Owner', { filter: (u) => !u.roles || u.roles.includes('ContractOwner') || u.roles.includes('Admin') })}
      {person('delegateId', 'Delegate', { optional: true })}
      {person('responsibleId', 'Responsible', { optional: true })}
      <Field label="Reminders go to" error={errors.reminderRecipient} hint={`${listDays(reminderSettings.noticeDays)} days before the notice deadline.`}>
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
          <input type="checkbox" checked={draft.openEnded} disabled={locked('openEnded') || locked('endDate')} onChange={(e) => onChange('openEnded', e.target.checked)} />
          Open-ended (no end date)
        </label>
      </div>
      {text('noticePeriodDays', 'Notice period (days)', { numeric: true, hint: 'Enter 0 if there is none. 3 months is about 90 days.' })}
      {/* The renewal period sits under the toggle, so the section keeps an even number of cells. */}
      <div className="stack" style={{ gap: 12 }}>
        {yesNo('autoRenew', 'Renews automatically')}
        {draft.autoRenew ? text('renewalMonths', 'Renewal period (months)', { numeric: true }) : null}
      </div>

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
      <Field label="Summary" optional={!ruleRequired('summary')} error={errors.summary} className="form-grid__full" hint="A few sentences on what the contract is about.">
        {(p) => <Textarea {...p} rows={3} value={draft.summary} onChange={(e) => onChange('summary', e.target.value)} />}
      </Field>

      {extraFields.length > 0 ? (
        <>
          <h2 className="form-section">Additional details</h2>
          {extraFields.map((def) => (
            <CustomFieldInput key={def.id} def={def} value={draft.customFields[def.key]} error={errors[`customFields.${def.key}`]} currency={draft.currency || 'DKK'} onChange={(v) => onPatch({ customFields: { ...draft.customFields, [def.key]: v } })} />
          ))}
        </>
      ) : null}
      {showTags && config.tags.length > 0 ? (
        <div className="form-grid__full">
          <TagPicker tags={config.tags} value={draft.tagIds} onChange={(tagIds) => onPatch({ tagIds })} />
        </div>
      ) : null}
    </div>
  );
}
