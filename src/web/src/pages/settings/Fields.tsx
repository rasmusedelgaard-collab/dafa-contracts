/* eslint-disable react-refresh/only-export-components */
import { useEffect, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { qk, useApi, useLookups } from '../../app/context';
import { OPTIONAL_STANDARD_FIELDS, type CustomFieldDef, type CustomFieldInput, type CustomFieldType, type FieldRules, type OptionalStandardField, type ReminderRecipient, type TypeFieldRules } from '../../api/types';
import { Button, Card, EmptyState, ErrorState, Field, Modal, Pill, Select, SkeletonRows, Textarea, TextInput, useToast } from '../../components';
import { STANDARD_FIELD_LABELS } from '../../lib/customFields';
import { ConfirmDelete, SectionHead } from './common';
import { problemErrors, useRefreshReference } from './Reference';

export const TYPE_LABEL: Record<CustomFieldType, string> = {
  Text: 'Text',
  Number: 'Number',
  Money: 'Amount (money)',
  Date: 'Date',
  YesNo: 'Yes / no',
  Choice: 'Single choice',
  MultiChoice: 'Multiple choice',
  Person: 'Person',
};

/** camelCase key from a label: "ERP vendor no." -> "erpVendorNo". */
export function keyFromLabel(label: string): string {
  const words = label
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^A-Za-z0-9 ]/g, ' ')
    .trim()
    .split(/\s+/)
    .filter(Boolean);
  const key = words.map((w, i) => (i === 0 ? w.toLowerCase() : w[0]?.toUpperCase() + w.slice(1).toLowerCase())).join('');
  return /^[a-z]/.test(key) ? key.slice(0, 40) : `f${key}`.slice(0, 40);
}

/** Client-side check of a field definition (the API checks the same). */
export function validateFieldInput(d: CustomFieldInput, others: CustomFieldDef[]): Record<string, string> {
  const e: Record<string, string> = {};
  if (!d.label.trim()) e.label = 'Enter a label';
  if (!/^[a-z][a-zA-Z0-9]{1,39}$/.test(d.key)) e.key = 'Start with a lower-case letter; letters and digits only';
  else if (others.some((o) => o.key === d.key)) e.key = 'Another field uses this key';
  const choice = d.type === 'Choice' || d.type === 'MultiChoice';
  if (choice && d.options.filter((o) => o.trim()).length === 0) e.options = 'Add at least one option';
  if (choice && new Set(d.options.map((o) => o.trim().toLowerCase())).size !== d.options.length) e.options = 'Each option only once';
  return e;
}

function FieldDialog({ item, all, onClose }: { item: CustomFieldDef | null; all: CustomFieldDef[]; onClose: () => void }) {
  const api = useApi();
  const lookups = useLookups();
  const toast = useToast();
  const refresh = useRefreshReference();
  const [d, setD] = useState<CustomFieldInput>(
    item ? (({ id: _id, ...rest }) => rest)(item) : { key: '', label: '', type: 'Text', contractTypeIds: [], required: false, helpText: null, order: all.length, options: [], showInRegister: false, showInFilters: false, isActive: true },
  );
  const [keyTouched, setKeyTouched] = useState(!!item);
  const [optionsText, setOptionsText] = useState((item?.options ?? []).join('\n'));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const save = useMutation({
    mutationFn: (input: CustomFieldInput) => (item ? api.admin.updateCustomField(item.id, input) : api.admin.createCustomField(input)),
    onSuccess: async () => {
      await refresh();
      toast.show(item ? 'Field saved' : 'Field added. It now shows in New contract and Edit details.');
      onClose();
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  const choice = d.type === 'Choice' || d.type === 'MultiChoice';
  const submit = () => {
    const input = { ...d, label: d.label.trim(), options: choice ? optionsText.split('\n').map((o) => o.trim()).filter(Boolean) : [] };
    const e = validateFieldInput(input, all.filter((x) => x.id !== item?.id));
    setErrors(e);
    if (!Object.keys(e).length) save.mutate(input);
  };
  return (
    <Modal
      wide
      title={item ? `Edit "${item.label}"` : 'Add field'}
      subtitle="Extra information to record on contracts. It is shown in New contract, Edit details and on the contract, and optionally as a register column and a filter."
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            Save field
          </Button>
        </>
      }
    >
      <div className="form-grid form-grid--flat">
        <Field label="Label" error={errors.label}>
          {(p) => <TextInput {...p} value={d.label} onChange={(e) => setD({ ...d, label: e.target.value, key: keyTouched ? d.key : keyFromLabel(e.target.value) })} />}
        </Field>
        <Field label="Key" error={errors.key} hint={item ? 'Cannot be changed.' : 'Used in exports, imports (cf.key) and the API.'}>
          {(p) => <TextInput {...p} className="mono" value={d.key} disabled={!!item} onChange={(e) => { setKeyTouched(true); setD({ ...d, key: e.target.value }); }} />}
        </Field>
        <Field label="Type" error={errors.type}>
          {(p) => <Select {...p} value={d.type} options={Object.entries(TYPE_LABEL).map(([value, label]) => ({ value, label }))} onChange={(e) => setD({ ...d, type: e.target.value as CustomFieldType })} />}
        </Field>
        <Field label="Contract types" hint="Leave empty for every type.">
          {(p) => (
            <select {...p} multiple className="select select--multi" value={d.contractTypeIds} onChange={(e) => setD({ ...d, contractTypeIds: [...e.target.selectedOptions].map((o) => o.value) })}>
              {lookups.contractTypes.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </select>
          )}
        </Field>
        {choice ? (
          <Field label="Options" error={errors.options} hint="One per line." className="form-grid__full">
            {(p) => <Textarea {...p} rows={4} value={optionsText} onChange={(e) => setOptionsText(e.target.value)} />}
          </Field>
        ) : null}
        <Field label="Help text" optional className="form-grid__full">
          {(p) => <TextInput {...p} value={d.helpText ?? ''} onChange={(e) => setD({ ...d, helpText: e.target.value || null })} />}
        </Field>
        <fieldset className="fieldset-plain form-grid__full">
          <legend className="field__label">Behaviour</legend>
          <div className="check-row">
            <label className="check">
              <input type="checkbox" checked={d.required} onChange={(e) => setD({ ...d, required: e.target.checked })} />
              Required (new contracts and drafts)
            </label>
            <label className="check">
              <input type="checkbox" checked={d.showInRegister} onChange={(e) => setD({ ...d, showInRegister: e.target.checked })} />
              Available as a register column
            </label>
            <label className="check">
              <input type="checkbox" checked={d.showInFilters} disabled={!['Choice', 'MultiChoice', 'YesNo'].includes(d.type)} onChange={(e) => setD({ ...d, showInFilters: e.target.checked })} />
              Filter in the register and Overview
            </label>
            <label className="check">
              <input type="checkbox" checked={d.isActive} onChange={(e) => setD({ ...d, isActive: e.target.checked })} />
              Active
            </label>
          </div>
        </fieldset>
      </div>
    </Modal>
  );
}

export function CustomFields() {
  const api = useApi();
  const lookups = useLookups();
  const toast = useToast();
  const refresh = useRefreshReference();
  const q = useQuery({ queryKey: qk.admin('custom-fields'), queryFn: () => api.admin.getCustomFields() });
  const [editing, setEditing] = useState<CustomFieldDef | 'new' | null>(null);
  const [deleting, setDeleting] = useState<CustomFieldDef | null>(null);
  const reorder = useMutation({ mutationFn: (ids: string[]) => api.admin.reorderCustomFields(ids), onSuccess: refresh, onError: (e) => toast.error(e) });
  const remove = useMutation({
    mutationFn: (d: CustomFieldDef) => api.admin.deleteCustomField(d.id),
    onSuccess: async () => {
      await refresh();
      setDeleting(null);
      toast.show('Field deleted');
    },
    onError: (e) => {
      setDeleting(null);
      toast.error(e);
    },
  });
  const list = [...(q.data ?? [])].sort((a, b) => a.order - b.order);
  const move = (i: number, by: number) => {
    const ids = list.map((d) => d.id);
    const j = i + by;
    if (j < 0 || j >= ids.length) return;
    [ids[i], ids[j]] = [ids[j] as string, ids[i] as string];
    reorder.mutate(ids);
  };
  const typeNames = (ids: string[]) => (ids.length === 0 ? 'All types' : ids.map((id) => lookups.contractTypes.find((t) => t.id === id)?.name ?? '').join(', '));
  return (
    <>
      <SectionHead title="Custom fields" text="Fields DAFA wants on contracts beyond the standard ones, per contract type. A field with values cannot be deleted or change type; make it inactive instead." action={<Button icon="plus" onClick={() => setEditing('new')}>Add field</Button>} />
      <Card clip>
        {q.isLoading ? (
          <SkeletonRows rows={5} />
        ) : q.isError ? (
          <ErrorState onRetry={() => void q.refetch()} />
        ) : list.length === 0 ? (
          <EmptyState icon="sliders" title="No custom fields yet" />
        ) : (
          <ol aria-label="Custom fields, in form order">
            {list.map((d, i) => (
              <li key={d.id} className="list-row field-row">
                <span className="row-flex" style={{ gap: 2 }}>
                  <Button variant="ghost" size="sm" iconOnly icon="chevronUp" aria-label={`Move ${d.label} up`} disabled={i === 0 || reorder.isPending} onClick={() => move(i, -1)} />
                  <Button variant="ghost" size="sm" iconOnly icon="chevronDown" aria-label={`Move ${d.label} down`} disabled={i === list.length - 1 || reorder.isPending} onClick={() => move(i, 1)} />
                </span>
                <div className="kv" style={{ flex: '1 1 auto', minWidth: 0 }}>
                  <span className="kv__v">
                    {d.label} <span className="caption mono">cf.{d.key}</span>
                  </span>
                  <span className="kv__k">
                    {TYPE_LABEL[d.type]}
                    {d.options.length ? `: ${d.options.join(', ')}` : ''} · {typeNames(d.contractTypeIds)}
                  </span>
                </div>
                <span className="row-flex" style={{ gap: 6 }}>
                  {d.required ? <Pill tone="amber" small>Required</Pill> : null}
                  {d.showInRegister ? <Pill tone="blue" small>Column</Pill> : null}
                  {d.showInFilters ? <Pill tone="blue" small>Filter</Pill> : null}
                  {!d.isActive ? <Pill tone="gray" small>Inactive</Pill> : null}
                </span>
                <Button variant="ghost" size="sm" iconOnly icon="edit" aria-label={`Edit ${d.label}`} onClick={() => setEditing(d)} />
                <Button variant="ghost" size="sm" iconOnly icon="trash" aria-label={`Delete ${d.label}`} onClick={() => setDeleting(d)} />
              </li>
            ))}
          </ol>
        )}
      </Card>
      {editing ? <FieldDialog item={editing === 'new' ? null : editing} all={list} onClose={() => setEditing(null)} /> : null}
      {deleting ? <ConfirmDelete title={`Delete "${deleting.label}"?`} text="Only fields without values can be deleted. Otherwise make the field inactive." pending={remove.isPending} onConfirm={() => remove.mutate(deleting)} onClose={() => setDeleting(null)} /> : null}
    </>
  );
}

const emptyRules = (typeId: string): TypeFieldRules => ({ typeId, required: [], defaults: { currency: null, noticePeriodDays: null, reminderRecipient: null, autoRenew: null, renewalMonths: null } });

export function FieldRulesAdmin() {
  const api = useApi();
  const lookups = useLookups();
  const toast = useToast();
  const refresh = useRefreshReference();
  const q = useQuery({ queryKey: qk.admin('field-rules'), queryFn: () => api.admin.getFieldRules() });
  const [rules, setRules] = useState<FieldRules | null>(null);
  const [typeId, setTypeId] = useState(lookups.contractTypes[0]?.id ?? '');
  const [errors, setErrors] = useState<Record<string, string>>({});
  useEffect(() => {
    if (q.data && !rules) setRules(q.data);
  }, [q.data, rules]);
  const save = useMutation({
    mutationFn: (r: FieldRules) => api.admin.updateFieldRules(r),
    onSuccess: async (r) => {
      setRules(r);
      setErrors({});
      await refresh();
      toast.show('Field rules saved');
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  if (!rules) return <SkeletonRows rows={6} />;
  const current = rules.types.find((t) => t.typeId === typeId) ?? emptyRules(typeId);
  const setCurrent = (next: TypeFieldRules) => setRules({ types: [...rules.types.filter((t) => t.typeId !== typeId), next] });
  const setDefault = <K extends keyof TypeFieldRules['defaults']>(k: K, v: TypeFieldRules['defaults'][K]) => setCurrent({ ...current, defaults: { ...current.defaults, [k]: v } });
  const configured = new Set(rules.types.map((t) => t.typeId));
  return (
    <>
      <SectionHead title="Field rules" text="Per contract type: which standard fields are required on top of the usual ones, and the values New contract fills in when the type is chosen." action={<Button loading={save.isPending} onClick={() => save.mutate(rules)}>Save changes</Button>} />
      <div className="rules-layout">
        <Card clip className="rules-layout__nav">
          <ul aria-label="Contract types">
            {lookups.contractTypes.map((t) => (
              <li key={t.id}>
                <button type="button" className={`rules-type${t.id === typeId ? ' rules-type--on' : ''}`} aria-current={t.id === typeId || undefined} onClick={() => setTypeId(t.id)}>
                  {t.name}
                  {configured.has(t.id) ? <Pill tone="blue" small>Rules</Pill> : null}
                </button>
              </li>
            ))}
          </ul>
        </Card>
        <div className="stack" style={{ gap: 16, minWidth: 0 }}>
          <Card pad className="stack" style={{ gap: 12 }}>
            <h3 className="card-title">Also required</h3>
            <div className="check-row">
              {OPTIONAL_STANDARD_FIELDS.map((f: OptionalStandardField) => (
                <label key={f} className="check">
                  <input type="checkbox" checked={current.required.includes(f)} onChange={(e) => setCurrent({ ...current, required: e.target.checked ? [...current.required, f] : current.required.filter((x) => x !== f) })} />
                  {STANDARD_FIELD_LABELS[f]}
                </label>
              ))}
            </div>
            <span className="caption">Always required: title, counterparty, type, entity, department, owner, dates, notice period, renewal, value, currency and change of control.</span>
          </Card>
          <Card pad className="stack" style={{ gap: 12 }}>
            <h3 className="card-title">Default values</h3>
            <div className="form-grid form-grid--flat">
              <Field label="Currency" error={errors[`types.0.defaults.currency`]}>
                {(p) => <Select {...p} value={current.defaults.currency ?? ''} placeholder="No default" options={lookups.currencies.map((c) => ({ value: c, label: c }))} onChange={(e) => setDefault('currency', e.target.value || null)} />}
              </Field>
              <Field label="Notice period (days)">
                {(p) => <TextInput {...p} inputMode="numeric" value={current.defaults.noticePeriodDays ?? ''} onChange={(e) => setDefault('noticePeriodDays', e.target.value === '' ? null : Number(e.target.value.replace(/\D/g, '')))} />}
              </Field>
              <Field label="Reminders go to">
                {(p) => <Select {...p} value={current.defaults.reminderRecipient ?? ''} placeholder="No default (owner)" options={['Owner', 'Delegate', 'Responsible'].map((x) => ({ value: x, label: x }))} onChange={(e) => setDefault('reminderRecipient', (e.target.value || null) as ReminderRecipient | null)} />}
              </Field>
              <Field label="Renews automatically">
                {(p) => <Select {...p} value={current.defaults.autoRenew === null ? '' : current.defaults.autoRenew ? 'yes' : 'no'} placeholder="No default" options={[{ value: 'yes', label: 'Yes' }, { value: 'no', label: 'No' }]} onChange={(e) => setDefault('autoRenew', e.target.value === '' ? null : e.target.value === 'yes')} />}
              </Field>
              <Field label="Renewal period (months)">
                {(p) => <TextInput {...p} inputMode="numeric" value={current.defaults.renewalMonths ?? ''} onChange={(e) => setDefault('renewalMonths', e.target.value === '' ? null : Number(e.target.value.replace(/\D/g, '')))} />}
              </Field>
            </div>
          </Card>
        </div>
      </div>
    </>
  );
}
