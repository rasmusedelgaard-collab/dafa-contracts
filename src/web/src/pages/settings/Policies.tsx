import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi, useLookups } from '../../app/context';
import type { HolidayCover as Cover, HolidayCoverInput, ReminderSettings, RetentionRule } from '../../api/types';
import { Button, Card, CardTitle, EmptyState, ErrorState, Field, Modal, Pill, Select, Skeleton, SkeletonRows, TextInput, useToast } from '../../components';
import { formatDate, todayIso } from '../../lib/dates';
import {
  parseNoticeDay,
  remindersToInput,
  RETENTION_ACTIONS,
  retentionToInput,
  validateCover,
  validateReminders,
  validateRetention,
  type CoverErrors,
  type ReminderDraft,
  type RetentionDraft,
  type RetentionErrors,
  refs,
} from '../../lib/settingsForms';
import { ConfirmDelete, SectionHead } from './common';
import { PersonPicker } from '../../features/Pickers';

// ---------------------------------------------------------------------------
// Reminders
// ---------------------------------------------------------------------------

function ReminderForm({ settings }: { settings: ReminderSettings }) {
  const api = useApi();
  const client = useQueryClient();
  const toast = useToast();
  const [draft, setDraft] = useState<ReminderDraft>({ noticeDays: [...settings.noticeDays].sort((a, b) => b - a), escalationDays: String(settings.escalationDays), obligationDays: String(settings.obligationDays) });
  const [adding, setAdding] = useState('');
  const [addError, setAddError] = useState<string>();
  const [tried, setTried] = useState(false);
  const errors = tried ? validateReminders(draft) : {};

  const save = useMutation({
    mutationFn: () => api.admin.updateReminderSettings(remindersToInput(draft)),
    onSuccess: async (saved) => {
      client.setQueryData(qk.admin('reminder-settings'), saved);
      await Promise.all([client.invalidateQueries({ queryKey: qk.reminderSettings }), client.invalidateQueries({ queryKey: qk.contracts }), client.invalidateQueries({ queryKey: qk.admin('audit') })]);
      toast.show('Reminder settings saved');
      setTried(false);
    },
    onError: (e) => toast.error(e),
  });

  const add = () => {
    const result = parseNoticeDay(adding, draft.noticeDays);
    if ('error' in result) {
      setAddError(result.error);
      return;
    }
    setDraft((d) => ({ ...d, noticeDays: [...d.noticeDays, result.value].sort((a, b) => b - a) }));
    setAdding('');
    setAddError(undefined);
  };
  const submit = () => {
    setTried(true);
    if (Object.keys(validateReminders(draft)).length === 0) save.mutate();
  };

  return (
    <form
      noValidate
      className="stack"
      style={{ gap: 16 }}
      onSubmit={(e) => {
        e.preventDefault();
        submit();
      }}
    >
      <Card pad className="stack" style={{ gap: 14 }}>
        <CardTitle>Before the notice deadline</CardTitle>
        <p className="secondary">A reminder goes to the person chosen on the contract (owner, delegate or responsible) this many days before the notice deadline.</p>
        <ul className="day-chips" aria-label="Reminder days">
          {draft.noticeDays.map((n) => (
            <li key={n} className="day-chip">
              {n} days
              <button type="button" className="day-chip__x" aria-label={`Remove the reminder ${n} days before`} onClick={() => setDraft((d) => ({ ...d, noticeDays: d.noticeDays.filter((x) => x !== n) }))}>
                ×
              </button>
            </li>
          ))}
          {draft.noticeDays.length === 0 ? <li className="secondary">No reminders</li> : null}
        </ul>
        {errors.noticeDays ? (
          <span className="field__error" role="alert">
            {errors.noticeDays}
          </span>
        ) : null}
        <Field
          label="Add a reminder, days before"
          error={addError}
          className="field--narrow"
          after={
            <Button variant="secondary" size="md" onClick={add}>
              Add
            </Button>
          }
        >
          {(p) => (
            <TextInput
              {...p}
              inputMode="numeric"
              value={adding}
              onChange={(e) => {
                setAdding(e.target.value);
                if (addError) setAddError(undefined);
              }}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  add();
                }
              }}
            />
          )}
        </Field>
      </Card>
      <Card pad className="stack" style={{ gap: 14 }}>
        <CardTitle>Escalation and obligations</CardTitle>
        <div className="inline-form">
          <Field label="Escalate to the owner, days before" error={errors.escalationDays} hint="Sent when no renewal decision is recorded.">
            {(p) => <TextInput {...p} inputMode="numeric" value={draft.escalationDays} onChange={(e) => setDraft((d) => ({ ...d, escalationDays: e.target.value }))} />}
          </Field>
          <Field label="Obligation reminder, days before due" error={errors.obligationDays} hint="Sent to the person responsible for the obligation.">
            {(p) => <TextInput {...p} inputMode="numeric" value={draft.obligationDays} onChange={(e) => setDraft((d) => ({ ...d, obligationDays: e.target.value }))} />}
          </Field>
        </div>
      </Card>
      <div className="row-flex">
        <Button type="submit" loading={save.isPending}>
          Save reminder settings
        </Button>
      </div>
    </form>
  );
}

export function Reminders() {
  const api = useApi();
  const settings = useQuery({ queryKey: qk.admin('reminder-settings'), queryFn: () => api.admin.getReminderSettings() });
  return (
    <>
      <SectionHead title="Reminders" text="When reminders about notice deadlines and obligations are sent. Recording a renewal decision stops further reminders for that contract. Changes apply to reminders that have not been sent yet." />
      {settings.isLoading ? (
        <Card pad role="status" aria-label="Loading reminder settings">
          <Skeleton height={120} radius={12} />
        </Card>
      ) : settings.isError || !settings.data ? (
        <Card>
          <ErrorState text="The reminder settings could not be loaded." onRetry={() => void settings.refetch()} />
        </Card>
      ) : (
        <ReminderForm settings={settings.data} />
      )}
    </>
  );
}

// ---------------------------------------------------------------------------
// Retention
// ---------------------------------------------------------------------------

const toRetentionDraft = (rule: RetentionRule | undefined): RetentionDraft => ({ years: rule?.years === null || rule?.years === undefined ? '' : String(rule.years), action: rule?.action ?? 'ReviewThenDelete', reviewerDepartmentId: rule?.reviewerDepartmentId ?? '' });
const sameDraft = (a: RetentionDraft, b: RetentionDraft) => a.years.trim() === b.years.trim() && a.action === b.action && a.reviewerDepartmentId === b.reviewerDepartmentId;

function RetentionTable({ rules }: { rules: RetentionRule[] }) {
  const api = useApi();
  const lookups = useLookups();
  const client = useQueryClient();
  const toast = useToast();
  const [edits, setEdits] = useState<Record<string, RetentionDraft>>({});
  const [errors, setErrors] = useState<Record<string, RetentionErrors>>({});

  const ruleOf = (typeId: string) => rules.find((r) => r.typeId === typeId);
  const draftOf = (typeId: string) => edits[typeId] ?? toRetentionDraft(ruleOf(typeId));
  const dirty = lookups.contractTypes.filter((t) => edits[t.id] && !sameDraft(edits[t.id] as RetentionDraft, toRetentionDraft(ruleOf(t.id))));
  const change = (typeId: string, patch: Partial<RetentionDraft>) => {
    setEdits((e) => ({ ...e, [typeId]: { ...draftOf(typeId), ...patch } }));
    if (errors[typeId]) setErrors((e) => ({ ...e, [typeId]: {} }));
  };

  const save = useMutation({
    mutationFn: async () => {
      for (const t of dirty) {
        const input = retentionToInput(t.id, draftOf(t.id));
        const existing = ruleOf(t.id);
        if (existing) await api.admin.updateRetentionRule(existing.id, input);
        else await api.admin.createRetentionRule(input);
      }
      return dirty.length;
    },
    onSuccess: async (n) => {
      await Promise.all([client.invalidateQueries({ queryKey: qk.admin('retention-rules') }), client.invalidateQueries({ queryKey: qk.admin('audit') })]);
      setEdits({});
      toast.show(n === 1 ? 'Retention rule saved' : `${n} retention rules saved`);
    },
    onError: async (e) => {
      // Some rows may have been saved before the failure: show what the server has now.
      await client.invalidateQueries({ queryKey: qk.admin('retention-rules') });
      toast.error(e);
    },
  });

  const submit = () => {
    const found: Record<string, RetentionErrors> = {};
    for (const t of dirty) {
      const e = validateRetention(draftOf(t.id));
      if (Object.keys(e).length > 0) found[t.id] = e;
    }
    setErrors(found);
    if (Object.keys(found).length === 0) save.mutate();
    else toast.error(null, 'Check the marked retention rules before you save.');
  };

  return (
    <>
      <Card clip>
        <ul aria-label="Retention by contract type">
          {lookups.contractTypes.map((t, i) => {
            const d = draftOf(t.id);
            const e = errors[t.id] ?? {};
            const noRule = !ruleOf(t.id) && !edits[t.id];
            return (
              <li key={t.id} className="retention-row" style={i === 0 ? { borderTop: 0 } : undefined}>
                <span className="retention-row__type">
                  <span className="kv__v">{t.name}</span>
                  {noRule ? <span className="caption">No rule yet</span> : null}
                </span>
                <Field label="Keep after end, years" error={e.years} className="retention-row__years">
                  {(p) => <TextInput {...p} aria-label={`Years for ${t.name}`} inputMode="numeric" value={d.years} placeholder={d.action === 'Keep' ? '–' : ''} onChange={(ev) => change(t.id, { years: ev.target.value })} />}
                </Field>
                <Field label="Then" className="retention-row__action">
                  {(p) => <Select {...p} aria-label={`Action for ${t.name}`} value={d.action} onChange={(ev) => change(t.id, { action: ev.target.value as RetentionDraft['action'] })} options={RETENTION_ACTIONS} />}
                </Field>
                <Field label="Reviewer" optional={d.action !== 'ReviewThenDelete'} error={e.reviewerDepartmentId} className="retention-row__reviewer">
                  {(p) => <Select {...p} aria-label={`Reviewer for ${t.name}`} value={d.reviewerDepartmentId} placeholder="No reviewer" onChange={(ev) => change(t.id, { reviewerDepartmentId: ev.target.value })} options={refs(lookups.departments)} />}
                </Field>
              </li>
            );
          })}
        </ul>
      </Card>
      <div className="row-flex" style={{ alignItems: 'center' }}>
        <Button onClick={submit} disabled={dirty.length === 0} loading={save.isPending}>
          Save changes
        </Button>
        {dirty.length > 0 ? (
          <>
            <Button variant="ghost" onClick={() => { setEdits({}); setErrors({}); }} disabled={save.isPending}>
              Discard
            </Button>
            <span className="secondary" aria-live="polite">
              {dirty.length} unsaved {dirty.length === 1 ? 'change' : 'changes'}
            </span>
          </>
        ) : null}
      </div>
      <p className="caption">Example values, to be set by an administrator. Changes are logged in the audit log.</p>
    </>
  );
}

export function Retention() {
  const api = useApi();
  const rules = useQuery({ queryKey: qk.admin('retention-rules'), queryFn: () => api.admin.getRetentionRules() });
  return (
    <>
      <SectionHead title="Retention" text="How long contracts are kept after they end, and what happens then. In production the rule is applied to the contract's SharePoint folder as a retention label." />
      {rules.isLoading ? (
        <Card clip>
          <SkeletonRows rows={6} label="Loading retention rules" />
        </Card>
      ) : rules.isError || !rules.data ? (
        <Card>
          <ErrorState text="The retention rules could not be loaded." onRetry={() => void rules.refetch()} />
        </Card>
      ) : (
        <RetentionTable rules={rules.data} />
      )}
    </>
  );
}

// ---------------------------------------------------------------------------
// Holiday cover
// ---------------------------------------------------------------------------

const toInput = (c: Cover): HolidayCoverInput => ({ userId: c.userId, deputyId: c.deputyId, from: c.from, to: c.to });

function CoverDialog({ cover, others, pending, onSave, onClose }: { cover: Cover | null; others: HolidayCoverInput[]; pending: boolean; onSave: (input: HolidayCoverInput) => void; onClose: () => void }) {
  const [draft, setDraft] = useState<HolidayCoverInput>(cover ? toInput(cover) : { userId: '', deputyId: '', from: '', to: '' });
  const [tried, setTried] = useState(false);
  const errors: CoverErrors = tried ? validateCover(draft, others) : {};
  const set = (patch: Partial<HolidayCoverInput>) => setDraft((d) => ({ ...d, ...patch }));
  const submit = () => {
    setTried(true);
    if (Object.keys(validateCover(draft, others)).length === 0) onSave(draft);
  };
  return (
    <Modal
      title={cover ? 'Edit holiday cover' : 'Add holiday cover'}
      subtitle="While the cover is active, approval steps for the person who is away go to the deputy."
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={pending}>
            {cover ? 'Save' : 'Add cover'}
          </Button>
        </>
      }
    >
      <div className="form-grid form-grid--flat">
        <Field label="Who is away" error={errors.userId}>
          {(p) => <PersonPicker {...p} value={draft.userId || null} clearLabel="Clear person" onChange={(id) => set({ userId: id ?? '' })} />}
        </Field>
        <Field label="Deputy" error={errors.deputyId}>
          {(p) => <PersonPicker {...p} value={draft.deputyId || null} clearLabel="Clear deputy" onChange={(id) => set({ deputyId: id ?? '' })} />}
        </Field>
        <Field label="First day" error={errors.from}>
          {(p) => <TextInput {...p} type="date" value={draft.from} onChange={(e) => set({ from: e.target.value })} />}
        </Field>
        <Field label="Last day" error={errors.to}>
          {(p) => <TextInput {...p} type="date" value={draft.to} min={draft.from || undefined} onChange={(e) => set({ to: e.target.value })} />}
        </Field>
      </div>
    </Modal>
  );
}

export function HolidayCover() {
  const api = useApi();
  const client = useQueryClient();
  const toast = useToast();
  const [editing, setEditing] = useState<Cover | 'new' | null>(null);
  const [deleting, setDeleting] = useState<Cover | null>(null);
  const cover = useQuery({ queryKey: qk.admin('holiday-cover'), queryFn: () => api.admin.getHolidayCover() });
  const list = cover.data ?? [];
  const today = todayIso();

  // The endpoint replaces the whole list, so every change sends all rows.
  const save = useMutation({
    mutationFn: (next: { rows: HolidayCoverInput[]; message: string }) => api.admin.updateHolidayCover(next.rows),
    onSuccess: async (saved, next) => {
      client.setQueryData(qk.admin('holiday-cover'), saved);
      await client.invalidateQueries({ queryKey: qk.admin('audit') });
      toast.show(next.message);
      setEditing(null);
      setDeleting(null);
    },
    onError: (e) => {
      toast.error(e);
      setDeleting(null);
    },
  });

  const others = (except: Cover | null) => list.filter((c) => c.id !== except?.id).map(toInput);
  const state = (c: Cover): { text: string; tone: 'green' | 'blue' | 'gray' } => (c.to < today ? { text: 'Ended', tone: 'gray' } : c.from > today ? { text: 'Upcoming', tone: 'blue' } : { text: 'Active now', tone: 'green' });

  return (
    <>
      <SectionHead
        title="Holiday cover"
        text="When an approver is away, their approval steps go to a deputy automatically for the period."
        action={
          <Button size="md" icon="plus" onClick={() => setEditing('new')}>
            Add cover
          </Button>
        }
      />
      <Card clip>
        {cover.isLoading ? (
          <SkeletonRows rows={2} label="Loading holiday cover" />
        ) : cover.isError ? (
          <ErrorState text="The holiday cover could not be loaded." onRetry={() => void cover.refetch()} />
        ) : list.length === 0 ? (
          <EmptyState compact title="No holiday cover" text="Add cover when an approver is away, so approvals do not wait." />
        ) : (
          <ul aria-label="Holiday cover">
            {[...list]
              .sort((a, b) => a.from.localeCompare(b.from))
              .map((c, i) => {
                const s = state(c);
                return (
                  <li key={c.id} className="list-row list-row--wrap" style={i === 0 ? { borderTop: 0 } : undefined}>
                    <span className="kv" style={{ flex: '1 1 240px' }}>
                      <span className="kv__v">
                        {c.userName} is covered by {c.deputyName}
                      </span>
                      <span className="kv__k">
                        {formatDate(c.from)} – {formatDate(c.to)}
                      </span>
                    </span>
                    <Pill tone={s.tone}>{s.text}</Pill>
                    <Button variant="ghost" size="sm" icon="edit" onClick={() => setEditing(c)} aria-label={`Edit cover for ${c.userName}`}>
                      Edit
                    </Button>
                    <Button variant="ghost" size="sm" icon="trash" iconOnly onClick={() => setDeleting(c)} aria-label={`Delete cover for ${c.userName}`} />
                  </li>
                );
              })}
          </ul>
        )}
      </Card>
      {editing ? (
        <CoverDialog
          cover={editing === 'new' ? null : editing}
          others={others(editing === 'new' ? null : editing)}
          pending={save.isPending}
          onSave={(input) => save.mutate({ rows: [...others(editing === 'new' ? null : editing), input], message: editing === 'new' ? 'Holiday cover added' : 'Holiday cover saved' })}
          onClose={() => setEditing(null)}
        />
      ) : null}
      {deleting ? (
        <ConfirmDelete
          title="Delete this holiday cover?"
          text={`${deleting.userName} will get their own approval steps again.`}
          pending={save.isPending}
          onConfirm={() => save.mutate({ rows: others(deleting), message: 'Holiday cover deleted' })}
          onClose={() => setDeleting(null)}
        />
      ) : null}
    </>
  );
}
