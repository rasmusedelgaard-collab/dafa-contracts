// Inputs for admin-defined custom fields, and their read-only display on the record.
import { useId } from 'react';
import { useLookups } from '../app/context';
import type { CustomFieldDef, CustomFieldValue, TagRef } from '../api/types';
import { Field, NumberValueInput, Segmented, Select, TextInput } from '../components';
import { displayValue, isEmptyValue } from '../lib/customFields';
import { formatDate } from '../lib/dates';
import { PersonPicker } from './Pickers';

export function CustomFieldInput({ def, value, error, onChange, currency }: { def: CustomFieldDef; value: CustomFieldValue | undefined; error?: string; onChange: (v: CustomFieldValue) => void; currency: string }) {
  const groupId = useId();
  const common = { label: def.label, optional: !def.required, error, hint: def.helpText ?? undefined };
  switch (def.type) {
    case 'Text':
      return <Field {...common}>{(p) => <TextInput {...p} value={typeof value === 'string' ? value : ''} onChange={(e) => onChange(e.target.value)} />}</Field>;
    case 'Number':
      return <Field {...common}>{(p) => <NumberValueInput {...p} value={typeof value === 'number' ? value : null} onNumberChange={(n) => onChange(n)} />}</Field>;
    case 'Money': {
      const money = value && typeof value === 'object' && !Array.isArray(value) ? value : null;
      return (
        <Field {...common} hint={def.helpText ?? `Amount in ${money?.currency ?? currency}`}>
          {(p) => <NumberValueInput {...p} value={money ? money.amount : null} onNumberChange={(n) => onChange(n === null ? null : { amount: n, currency: money?.currency ?? currency })} />}
        </Field>
      );
    }
    case 'Date':
      return <Field {...common}>{(p) => <TextInput {...p} type="date" value={typeof value === 'string' ? value : ''} onChange={(e) => onChange(e.target.value || null)} />}</Field>;
    case 'Choice':
      return <Field {...common}>{(p) => <Select {...p} value={typeof value === 'string' ? value : ''} placeholder={def.required ? 'Choose' : 'None'} options={def.options.map((o) => ({ value: o, label: o }))} onChange={(e) => onChange(e.target.value || null)} />}</Field>;
    case 'Person':
      return <Field {...common}>{(p) => <PersonPicker {...p} value={typeof value === 'string' ? value : null} clearLabel={`Clear ${def.label}`} onChange={(id) => onChange(id)} />}</Field>;
    case 'YesNo':
      return (
        <div className="field">
          <div className="field__top">
            <span className="field__label" id={groupId}>
              {def.label}
              {def.required ? null : <span className="field__optional"> (optional)</span>}
            </span>
          </div>
          <Segmented mode="radio" block ariaLabel={def.label} invalid={!!error} options={[{ value: 'yes', label: 'Yes' }, { value: 'no', label: 'No' }]} value={value === true ? 'yes' : value === false ? 'no' : null} onChange={(v) => onChange(v === 'yes')} />
          {error ? (
            <span className="field__error" role="alert">
              {error}
            </span>
          ) : null}
          {def.helpText ? <span className="field__hint">{def.helpText}</span> : null}
        </div>
      );
    case 'MultiChoice': {
      const list = Array.isArray(value) ? value : [];
      return (
        <fieldset className="field fieldset-plain">
          <legend className="field__label">
            {def.label}
            {def.required ? null : <span className="field__optional"> (optional)</span>}
          </legend>
          <div className="check-row">
            {def.options.map((o) => (
              <label key={o} className="check">
                <input type="checkbox" checked={list.includes(o)} onChange={(e) => onChange(e.target.checked ? [...list, o] : list.filter((x) => x !== o))} />
                {o}
              </label>
            ))}
          </div>
          {error ? (
            <span className="field__error" role="alert">
              {error}
            </span>
          ) : null}
          {def.helpText ? <span className="field__hint">{def.helpText}</span> : null}
        </fieldset>
      );
    }
  }
}

/** Read-only list on the record ("Additional details"). */
export function CustomFieldValues({ defs, values }: { defs: CustomFieldDef[]; values: Record<string, CustomFieldValue> | undefined }) {
  const lookups = useLookups();
  return (
    <dl className="dl-grid dl-grid--wide">
      {defs.map((d) => (
        <div key={d.id} className="kv">
          <dt className="kv__k">{d.label}</dt>
          <dd className={`kv__v${isEmptyValue(values?.[d.key]) ? ' kv__v--empty' : ''}`}>{displayValue(d, values?.[d.key], lookups, formatDate)}</dd>
        </div>
      ))}
    </dl>
  );
}

/** Toggle buttons for tags. */
export function TagPicker({ tags, value, onChange, label = 'Tags' }: { tags: TagRef[]; value: string[]; onChange: (ids: string[]) => void; label?: string }) {
  if (tags.length === 0) return null;
  return (
    <fieldset className="field fieldset-plain">
      <legend className="field__label">
        {label} <span className="field__optional">(optional)</span>
      </legend>
      <div className="check-row">
        {tags.map((t) => {
          const on = value.includes(t.id);
          return (
            <button key={t.id} type="button" className={`tag tag--${t.color} tag--toggle${on ? ' tag--on' : ''}`} aria-pressed={on} onClick={() => onChange(on ? value.filter((x) => x !== t.id) : [...value, t.id])}>
              {t.name}
            </button>
          );
        })}
      </div>
    </fieldset>
  );
}
