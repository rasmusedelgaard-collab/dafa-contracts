import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi, useLookups } from '../../app/context';
import type { ApprovalRule, ApproverRole } from '../../api/types';
import { Button, Card, CardTitle, EmptyState, ErrorState, Field, Modal, Pill, Select, SkeletonRows, TextInput, useToast } from '../../components';
import { RoutePreview } from '../../features/Approval';
import {
  APPROVER_ROLES,
  describeApprover,
  describeRuleCriteria,
  draftToRuleInput,
  emptyRuleDraft,
  newApprover,
  newStage,
  ruleHasErrors,
  ruleToDraft,
  validateRuleDraft,
  type ApproverDraft,
  type RuleDraft,
  type RuleErrors,
  type StageDraft,
  refs,
} from '../../lib/settingsForms';
import { parseAmount } from '../../lib/validation';
import { ConfirmDelete, SectionHead } from './common';
import { PersonPicker } from '../../features/Pickers';

const NO_ERRORS: RuleErrors = { stage: {}, approver: {} };

function StagesEditor({ stages, errors, onChange }: { stages: StageDraft[]; errors: RuleErrors; onChange: (stages: StageDraft[]) => void }) {
  const setStage = (si: number, approvers: ApproverDraft[]) => onChange(stages.map((s, i) => (i === si ? { ...s, approvers } : s)));
  const setApprover = (si: number, ai: number, patch: Partial<ApproverDraft>) => setStage(si, (stages[si]?.approvers ?? []).map((a, i) => (i === ai ? { ...a, ...patch } : a)));
  const move = (si: number, by: number) => {
    const next = [...stages];
    const [stage] = next.splice(si, 1);
    if (stage) next.splice(si + by, 0, stage);
    onChange(next);
  };

  return (
    <fieldset className="stages">
      <legend className="field__label">Stages</legend>
      <p className="field__hint">Stages run in order. The approvers of one stage act in parallel, and the stage is done when all of them have approved.</p>
      {errors.stages ? (
        <span className="field__error" role="alert">
          {errors.stages}
        </span>
      ) : null}
      <ol className="stages__list">
        {stages.map((stage, si) => (
          <li key={stage.key} className="stage" aria-label={`Stage ${si + 1}`}>
            <div className="stage__head">
              <span className="route__n" aria-hidden="true">
                {si + 1}
              </span>
              <span className="stage__title">Stage {si + 1}</span>
              <div className="spacer" />
              <Button variant="ghost" size="sm" icon="chevronUp" iconOnly disabled={si === 0} onClick={() => move(si, -1)} aria-label={`Move stage ${si + 1} up`} />
              <Button variant="ghost" size="sm" icon="chevronDown" iconOnly disabled={si === stages.length - 1} onClick={() => move(si, 1)} aria-label={`Move stage ${si + 1} down`} />
              <Button variant="ghost" size="sm" icon="trash" iconOnly onClick={() => onChange(stages.filter((_, i) => i !== si))} aria-label={`Remove stage ${si + 1}`} />
            </div>
            {stage.approvers.map((a, ai) => {
              const error = errors.approver[`${si}-${ai}`];
              const errorId = `stage-${stage.key}-approver-${ai}-error`;
              const label = `Stage ${si + 1}, approver ${ai + 1}`;
              return (
                <div key={ai} className="approver">
                  <div className="approver__row">
                    <Select
                      aria-label={`${label}: kind`}
                      value={a.kind}
                      onChange={(e) => setApprover(si, ai, { kind: e.target.value as ApproverDraft['kind'] })}
                      options={[
                        { value: 'role', label: 'Role' },
                        { value: 'user', label: 'Specific person' },
                      ]}
                    />
                    {a.kind === 'role' ? (
                      <Select aria-label={`${label}: role`} aria-invalid={error ? true : undefined} aria-describedby={error ? errorId : undefined} value={a.role} placeholder="Choose a role" onChange={(e) => setApprover(si, ai, { role: e.target.value as ApproverRole | '' })} options={APPROVER_ROLES.map((r) => ({ value: r.value, label: r.label }))} />
                    ) : (
                      <PersonPicker ariaLabel={`${label}: person`} aria-invalid={error ? true : undefined} aria-describedby={error ? errorId : undefined} value={a.userId || null} clearLabel={`Clear ${label}: person`} onChange={(id) => setApprover(si, ai, { userId: id ?? '' })} />
                    )}
                    <Button variant="ghost" size="sm" icon="x" iconOnly onClick={() => setStage(si, stage.approvers.filter((_, i) => i !== ai))} aria-label={`Remove approver ${ai + 1} from stage ${si + 1}`} />
                  </div>
                  {error ? (
                    <span className="field__error" id={errorId} role="alert">
                      {error}
                    </span>
                  ) : null}
                </div>
              );
            })}
            {errors.stage[si] ? (
              <span className="field__error" role="alert">
                {errors.stage[si]}
              </span>
            ) : null}
            <Button variant="tint" size="sm" icon="plus" style={{ alignSelf: 'flex-start' }} onClick={() => setStage(si, [...stage.approvers, newApprover()])} aria-label={`Add approver to stage ${si + 1}`}>
              Add approver
            </Button>
          </li>
        ))}
      </ol>
      <Button variant="secondary" size="md" icon="plus" style={{ alignSelf: 'flex-start' }} onClick={() => onChange([...stages, newStage()])}>
        Add stage
      </Button>
    </fieldset>
  );
}

function RuleDialog({ rule, onClose }: { rule: ApprovalRule | null; onClose: () => void }) {
  const api = useApi();
  const lookups = useLookups();
  const client = useQueryClient();
  const toast = useToast();
  const [draft, setDraft] = useState<RuleDraft>(() => (rule ? ruleToDraft(rule) : emptyRuleDraft()));
  const [tried, setTried] = useState(false);
  const errors = tried ? validateRuleDraft(draft) : NO_ERRORS;
  const set = <K extends keyof RuleDraft>(key: K, value: RuleDraft[K]) => setDraft((d) => ({ ...d, [key]: value }));

  const save = useMutation({
    mutationFn: () => (rule ? api.admin.updateApprovalRule(rule.id, draftToRuleInput(draft)) : api.admin.createApprovalRule(draftToRuleInput(draft))),
    onSuccess: async (saved) => {
      await Promise.all([client.invalidateQueries({ queryKey: qk.admin('approval-rules') }), client.invalidateQueries({ queryKey: qk.approvals }), client.invalidateQueries({ queryKey: qk.admin('audit') })]);
      toast.show(rule ? `Saved rule "${saved.name}"` : `Added rule "${saved.name}"`);
      onClose();
    },
    onError: (e) => toast.error(e),
  });

  const submit = () => {
    setTried(true);
    if (ruleHasErrors(validateRuleDraft(draft))) {
      window.setTimeout(() => document.querySelector<HTMLElement>('.modal [aria-invalid="true"]')?.focus(), 0);
      return;
    }
    save.mutate();
  };

  const any = (label: string, key: 'entityId' | 'typeId' | 'departmentId', list: { id: string; name: string }[], placeholder: string) => (
    <Field label={label} optional>
      {(p) => <Select {...p} value={draft[key]} placeholder={placeholder} onChange={(e) => set(key, e.target.value)} options={refs(list)} />}
    </Field>
  );

  return (
    <Modal
      wide
      title={rule ? 'Edit approval rule' : 'New approval rule'}
      subtitle="A rule applies to the contracts that match all of its criteria. When several rules match, the one with the most criteria wins, then the highest priority."
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            {rule ? 'Save rule' : 'Add rule'}
          </Button>
        </>
      }
    >
      {tried && ruleHasErrors(errors) ? (
        <p className="error-summary" role="alert">
          Check the marked fields before you save.
        </p>
      ) : null}
      <div className="form-grid form-grid--flat">
        <Field label="Name" error={errors.name} className="form-grid__full">
          {(p) => <TextInput {...p} data-autofocus="" value={draft.name} maxLength={80} onChange={(e) => set('name', e.target.value)} />}
        </Field>
        {any('Legal entity', 'entityId', lookups.entities, 'Any entity')}
        {any('Contract type', 'typeId', lookups.contractTypes, 'Any type')}
        {any('Department', 'departmentId', lookups.departments, 'Any department')}
        <Field label="Priority" error={errors.priority} hint="Breaks a tie between rules with the same number of criteria. Higher wins.">
          {(p) => <TextInput {...p} inputMode="numeric" value={draft.priority} onChange={(e) => set('priority', e.target.value)} />}
        </Field>
        <Field label="Value from, DKK" optional error={errors.minValue} hint="Contracts at this value or above.">
          {(p) => <TextInput {...p} inputMode="decimal" value={draft.minValue} placeholder="Any" onChange={(e) => set('minValue', e.target.value)} />}
        </Field>
        <Field label="Value below, DKK" optional error={errors.maxValue} hint="Contracts under this value.">
          {(p) => <TextInput {...p} inputMode="decimal" value={draft.maxValue} placeholder="Any" onChange={(e) => set('maxValue', e.target.value)} />}
        </Field>
      </div>
      <StagesEditor stages={draft.stages} errors={errors} onChange={(stages) => set('stages', stages)} />
    </Modal>
  );
}

/** "Which rule applies?": runs `GET approvals/preview` for a made-up contract. */
function RuleTester() {
  const api = useApi();
  const lookups = useLookups();
  const [entityId, setEntityId] = useState('');
  const [typeId, setTypeId] = useState('');
  const [departmentId, setDepartmentId] = useState('');
  const [valueText, setValueText] = useState('');
  const parsed = parseAmount(valueText);
  const valueError = Number.isNaN(parsed) ? 'Enter an amount in DKK' : undefined;
  const query = { entityId: entityId || undefined, typeId: typeId || undefined, departmentId: departmentId || undefined, value: parsed !== null && !Number.isNaN(parsed) ? parsed : 0 };
  const preview = useQuery({ queryKey: qk.approvalPreview(query), queryFn: () => api.previewApproval(query), enabled: !valueError });

  return (
    <Card pad className="stack" style={{ gap: 14 }}>
      <CardTitle>Which rule applies?</CardTitle>
      <p className="secondary">Describe a contract to see the rule that would match and who would approve it.</p>
      <div className="inline-form">
        <Field label="Legal entity" optional>
          {(p) => <Select {...p} value={entityId} placeholder="Any entity" onChange={(e) => setEntityId(e.target.value)} options={refs(lookups.entities)} />}
        </Field>
        <Field label="Contract type" optional>
          {(p) => <Select {...p} value={typeId} placeholder="Any type" onChange={(e) => setTypeId(e.target.value)} options={refs(lookups.contractTypes)} />}
        </Field>
        <Field label="Department" optional>
          {(p) => <Select {...p} value={departmentId} placeholder="Any department" onChange={(e) => setDepartmentId(e.target.value)} options={refs(lookups.departments)} />}
        </Field>
        <Field label="Contract value, DKK" optional error={valueError}>
          {(p) => <TextInput {...p} inputMode="decimal" value={valueText} placeholder="0" onChange={(e) => setValueText(e.target.value)} />}
        </Field>
      </div>
      <div className="tester-result" aria-live="polite">
        {preview.isError ? (
          <span className="secondary">The preview could not be loaded.</span>
        ) : (
          <>
            {preview.data && preview.data.stages.length > 0 ? (
              <span className="tester-result__rule">
                <Pill tone="blue">Matching rule</Pill>
                <strong>{preview.data.ruleName ?? 'Not named by the API'}</strong>
              </span>
            ) : null}
            <RoutePreview preview={preview.data} loading={preview.isLoading} />
          </>
        )}
      </div>
    </Card>
  );
}

export function ApprovalRules() {
  const api = useApi();
  const lookups = useLookups();
  const client = useQueryClient();
  const toast = useToast();
  const [editing, setEditing] = useState<ApprovalRule | 'new' | null>(null);
  const [deleting, setDeleting] = useState<ApprovalRule | null>(null);
  const rules = useQuery({ queryKey: qk.admin('approval-rules'), queryFn: () => api.admin.getApprovalRules() });

  const del = useMutation({
    mutationFn: (rule: ApprovalRule) => api.admin.deleteApprovalRule(rule.id),
    onSuccess: async (_d, rule) => {
      await Promise.all([client.invalidateQueries({ queryKey: qk.admin('approval-rules') }), client.invalidateQueries({ queryKey: qk.approvals }), client.invalidateQueries({ queryKey: qk.admin('audit') })]);
      toast.show(`Deleted rule "${rule.name}"`);
      setDeleting(null);
    },
    onError: (e) => {
      toast.error(e);
      setDeleting(null);
    },
  });

  const sorted = [...(rules.data ?? [])].sort((a, b) => (a.minValue ?? -1) - (b.minValue ?? -1) || b.priority - a.priority || a.name.localeCompare(b.name));

  return (
    <>
      <SectionHead
        title="Approval rules"
        text="Who approves a contract, by legal entity, type, department and value. The value bands are placeholders until the authority matrix is decided."
        action={
          <Button size="md" icon="plus" onClick={() => setEditing('new')}>
            New rule
          </Button>
        }
      />
      <Card clip>
        {rules.isLoading ? (
          <SkeletonRows rows={4} label="Loading approval rules" />
        ) : rules.isError ? (
          <ErrorState text="The approval rules could not be loaded." onRetry={() => void rules.refetch()} />
        ) : sorted.length === 0 ? (
          <EmptyState compact title="No approval rules yet" text="Without a rule, contracts cannot be submitted for approval." />
        ) : (
          <ul aria-label="Approval rules">
            {sorted.map((rule, i) => (
              <li key={rule.id} className="rule-row" style={i === 0 ? { borderTop: 0 } : undefined}>
                <div className="rule-row__main">
                  <span className="kv__v">{rule.name}</span>
                  <span className="caption">
                    {describeRuleCriteria(rule, lookups)} · priority {rule.priority}
                  </span>
                  <ol className="rule-row__stages">
                    {[...rule.stages]
                      .sort((a, b) => a.order - b.order)
                      .map((s, si) => (
                        <li key={s.order}>
                          <span className="route__n" aria-hidden="true">
                            {si + 1}
                          </span>
                          <span className="sr-only">Stage {si + 1}: </span>
                          {s.approvers.map((a) => describeApprover(a, lookups)).join(' + ')}
                        </li>
                      ))}
                  </ol>
                </div>
                <div className="wide-row__actions">
                  <Button variant="ghost" size="sm" icon="edit" onClick={() => setEditing(rule)} aria-label={`Edit rule ${rule.name}`}>
                    Edit
                  </Button>
                  <Button variant="ghost" size="sm" icon="trash" iconOnly onClick={() => setDeleting(rule)} aria-label={`Delete rule ${rule.name}`} />
                </div>
              </li>
            ))}
          </ul>
        )}
      </Card>
      <RuleTester />
      {editing ? <RuleDialog rule={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
      {deleting ? <ConfirmDelete title={`Delete rule "${deleting.name}"?`} text="Contracts already in approval keep their route. New submissions use the remaining rules." pending={del.isPending} onConfirm={() => del.mutate(deleting)} onClose={() => setDeleting(null)} /> : null}
    </>
  );
}
