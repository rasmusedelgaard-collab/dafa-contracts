import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi, useAppConfig, useLookups } from '../../app/context';
import { DATA_QUALITY_FIELDS, type AppSettings, type JobInfo, type NotificationChannel, type NotificationEventSetting, type NotificationSettings, type RecipientRule, type Role, type Schedule, type TemplatePreview } from '../../api/types';
import { Button, Card, ErrorState, Field, Modal, Pill, Select, SkeletonRows, Table, Textarea, TextInput, useToast } from '../../components';
import { formatDateTime } from '../../lib/dates';
import { LANDING_OPTIONS } from '../Preferences';
import { SectionHead } from './common';
import { problemErrors, useRefreshReference } from './Reference';
import { ScheduleFields, scheduleText } from './Sync';

const CHANNELS: { value: NotificationChannel; label: string }[] = [
  { value: 'Email', label: 'E-mail' },
  { value: 'Teams', label: 'Teams' },
  { value: 'InApp', label: 'In the app' },
];
const RECIPIENTS: { value: RecipientRule; label: string }[] = [
  { value: 'ReminderRecipient', label: 'Reminder recipient' },
  { value: 'Owner', label: 'Owner' },
  { value: 'Delegate', label: 'Delegate' },
  { value: 'Responsible', label: 'Responsible' },
  { value: 'Watchers', label: 'Watchers' },
  { value: 'Approver', label: 'Approver' },
  { value: 'Mentioned', label: 'Mentioned people' },
  { value: 'Admins', label: 'Administrators' },
];

function TemplateDialog({ ev, onSave, onClose }: { ev: NotificationEventSetting; onSave: (e: NotificationEventSetting) => void; onClose: () => void }) {
  const api = useApi();
  const lookups = useLookups();
  const [d, setD] = useState(ev);
  const [preview, setPreview] = useState<TemplatePreview | null>(null);
  const show = useMutation({ mutationFn: () => api.admin.previewNotificationTemplate({ event: d.event, subject: d.subject, body: d.body }), onSuccess: setPreview });
  const insert = (p: string) => setD({ ...d, body: `${d.body}{{${p}}}` });
  return (
    <Modal
      wide
      title={d.label}
      subtitle="Channels, recipients and the text. Placeholders in double braces are filled in when the message is sent."
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" loading={show.isPending} onClick={() => show.mutate()}>
            Preview
          </Button>
          <div className="spacer" />
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => onSave(d)}>Use these settings</Button>
        </>
      }
    >
      <div className="stack" style={{ gap: 14 }}>
        <label className="check">
          <input type="checkbox" checked={d.enabled} onChange={(e) => setD({ ...d, enabled: e.target.checked })} />
          Send this notification
        </label>
        <fieldset className="fieldset-plain">
          <legend className="field__label">Channels</legend>
          <div className="check-row">
            {CHANNELS.map((c) => (
              <label key={c.value} className="check">
                <input type="checkbox" checked={d.channels.includes(c.value)} onChange={(e) => setD({ ...d, channels: e.target.checked ? [...d.channels, c.value] : d.channels.filter((x) => x !== c.value) })} />
                {c.label}
              </label>
            ))}
          </div>
        </fieldset>
        <fieldset className="fieldset-plain">
          <legend className="field__label">Recipients</legend>
          <div className="check-row">
            {RECIPIENTS.map((c) => (
              <label key={c.value} className="check">
                <input type="checkbox" checked={d.recipients.includes(c.value)} onChange={(e) => setD({ ...d, recipients: e.target.checked ? [...d.recipients, c.value] : d.recipients.filter((x) => x !== c.value) })} />
                {c.label}
              </label>
            ))}
          </div>
        </fieldset>
        <Field label="Also send to" optional hint="Fixed people who always get this notification.">
          {(p) => (
            <select {...p} multiple className="select select--multi" value={d.extraRecipientIds} onChange={(e) => setD({ ...d, extraRecipientIds: [...e.target.selectedOptions].map((o) => o.value) })}>
              {lookups.users.map((u) => (
                <option key={u.id} value={u.id}>
                  {u.name}
                </option>
              ))}
            </select>
          )}
        </Field>
        <Field label="Subject">{(p) => <TextInput {...p} value={d.subject} onChange={(e) => setD({ ...d, subject: e.target.value })} />}</Field>
        <Field label="Text">{(p) => <Textarea {...p} rows={5} value={d.body} onChange={(e) => setD({ ...d, body: e.target.value })} />}</Field>
        <div className="placeholders">
          <span className="caption">Insert:</span>
          {d.placeholders.map((p) => (
            <button key={p} type="button" className="tag tag--gray tag--toggle tag--on" onClick={() => insert(p)}>
              {`{{${p}}}`}
            </button>
          ))}
        </div>
        {preview ? (
          <div className="mail-preview" aria-label="Preview">
            <strong>{preview.subject}</strong>
            <p style={{ whiteSpace: 'pre-wrap' }}>{preview.body}</p>
            {preview.unknown.length ? <p className="field__error">Unknown placeholder: {preview.unknown.join(', ')}</p> : null}
          </div>
        ) : null}
      </div>
    </Modal>
  );
}

export function NotificationsAdmin() {
  const api = useApi();
  const lookups = useLookups();
  const toast = useToast();
  const q = useQuery({ queryKey: qk.admin('notification-settings'), queryFn: () => api.admin.getNotificationSettings() });
  const [s, setS] = useState<NotificationSettings | null>(null);
  const [editing, setEditing] = useState<NotificationEventSetting | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  useEffect(() => {
    if (q.data && !s) setS(q.data);
  }, [q.data, s]);
  const save = useMutation({
    mutationFn: (x: NotificationSettings) => api.admin.updateNotificationSettings(x),
    onSuccess: (x) => {
      setS(x);
      setErrors({});
      toast.show('Notification settings saved');
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  if (q.isError) return <ErrorState onRetry={() => void q.refetch()} />;
  if (!s) return <SkeletonRows rows={8} />;
  const typeName = (id: string) => lookups.contractTypes.find((t) => t.id === id)?.name ?? id;
  const unused = lookups.contractTypes.filter((t) => !s.reminderSchedules.some((r) => r.typeId === t.id));
  return (
    <>
      <SectionHead title="Notifications" text="Which events send a message, on which channel, to whom, and the text. People can turn channels off for themselves in My settings." action={<Button loading={save.isPending} onClick={() => save.mutate(s)}>Save changes</Button>} />
      {Object.values(errors)[0] ? <div className="banner tone-red">{Object.entries(errors).map(([k, v]) => `${k}: ${v}`).join(' · ')}</div> : null}
      <Card clip>
        <Table<NotificationEventSetting>
          caption="Notification events" minWidth={760}
          rows={s.events}
          rowKey={(r) => r.event}
          onRowClick={(r) => setEditing(r)}
          columns={[
            { key: 'e', header: 'Event', render: (r) => <span className="stack" style={{ gap: 2 }}><strong>{r.label}</strong><span className="caption cell-clip">{r.subject}</span></span> },
            { key: 'c', header: 'Channels', render: (r) => (r.enabled ? r.channels.map((c) => CHANNELS.find((x) => x.value === c)?.label).join(', ') || 'None' : <Pill tone="gray">Off</Pill>) },
            { key: 'r', header: 'Recipients', render: (r) => <span className="cell-clip">{r.recipients.map((c) => RECIPIENTS.find((x) => x.value === c)?.label).join(', ')}</span> },
            {
              key: 'x',
              header: '',
              className: 'num',
              render: (r) => (
                <span className="row-flex" style={{ gap: 6, justifyContent: 'flex-end', flexWrap: 'nowrap' }}>
                  <input type="checkbox" className="check__box" aria-label={`${r.label} on`} checked={r.enabled} onClick={(e) => e.stopPropagation()} onChange={(e) => setS({ ...s, events: s.events.map((x) => (x.event === r.event ? { ...x, enabled: e.target.checked } : x)) })} />
                  <Button variant="ghost" size="sm" iconOnly icon="edit" aria-label={`Edit ${r.label}`} onClick={(e) => { e.stopPropagation(); setEditing(r); }} />
                </span>
              ),
            },
          ]}
        />
      </Card>
      <div className="settings-grid">
        <Card pad className="stack" style={{ gap: 12 }}>
          <h3 className="card-title">Digest</h3>
          <Field label="Default for new users" hint="A summary e-mail of tasks, deadlines and watched contracts.">
            {(p) => <Select {...p} value={s.digestDefault} options={[{ value: 'Off', label: 'Off' }, { value: 'Daily', label: 'Daily' }, { value: 'Weekly', label: 'Weekly' }]} onChange={(e) => setS({ ...s, digestDefault: e.target.value as NotificationSettings['digestDefault'] })} />}
          </Field>
        </Card>
        <Card pad className="stack" style={{ gap: 12 }}>
          <h3 className="card-title">Reminder schedule per contract type</h3>
          <p className="secondary" style={{ fontSize: 13 }}>Types without their own schedule use the general reminder settings.</p>
          {s.reminderSchedules.map((r, i) => (
            <div key={r.typeId} className="schedule-row">
              <strong>{typeName(r.typeId)}</strong>
              <Field label="Days before the notice deadline" error={errors[`reminderSchedules.${i}.noticeDays`]}>
                {(p) => <TextInput {...p} value={r.noticeDays.join(', ')} onChange={(e) => setS({ ...s, reminderSchedules: s.reminderSchedules.map((x, j) => (j === i ? { ...x, noticeDays: e.target.value.split(/[,\s]+/).map(Number).filter((n) => n > 0) } : x)) })} />}
              </Field>
              <Field label="Escalate at (days)">
                {(p) => <TextInput {...p} inputMode="numeric" value={String(r.escalationDays)} onChange={(e) => setS({ ...s, reminderSchedules: s.reminderSchedules.map((x, j) => (j === i ? { ...x, escalationDays: Number(e.target.value) || 0 } : x)) })} />}
              </Field>
              <Button variant="ghost" size="sm" iconOnly icon="trash" aria-label={`Remove schedule for ${typeName(r.typeId)}`} onClick={() => setS({ ...s, reminderSchedules: s.reminderSchedules.filter((_, j) => j !== i) })} />
            </div>
          ))}
          {unused.length ? (
            <Select aria-label="Add a schedule for a contract type" value="" placeholder="Add a schedule for…" options={unused.map((t) => ({ value: t.id, label: t.name }))} onChange={(e) => e.target.value && setS({ ...s, reminderSchedules: [...s.reminderSchedules, { typeId: e.target.value, noticeDays: [120, 60, 30], escalationDays: 14 }] })} />
          ) : null}
        </Card>
      </div>
      {editing ? (
        <TemplateDialog
          ev={editing}
          onClose={() => setEditing(null)}
          onSave={(ev) => {
            setS({ ...s, events: s.events.map((x) => (x.event === ev.event ? ev : x)) });
            setEditing(null);
            toast.show('Changed. Save changes to keep it.');
          }}
        />
      ) : null}
    </>
  );
}

export function JobsAdmin() {
  const api = useApi();
  const toast = useToast();
  const client = useQueryClient();
  const q = useQuery({ queryKey: qk.admin('jobs'), queryFn: () => api.admin.getJobs() });
  const [editing, setEditing] = useState<JobInfo | null>(null);
  const [schedule, setSchedule] = useState<Schedule | null>(null);
  const run = useMutation({
    mutationFn: (j: JobInfo) => api.admin.runJob(j.key),
    onSuccess: async (j) => {
      await client.invalidateQueries();
      toast.show(`${j.label}: ${j.lastRun?.message ?? 'done'}`);
    },
    onError: (e) => toast.error(e),
  });
  const save = useMutation({
    mutationFn: () => api.admin.updateJobSchedule(editing?.key ?? 'Reminders', schedule as Schedule),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: qk.admin('jobs') });
      setEditing(null);
      toast.show('Schedule saved');
    },
    onError: (e) => toast.error(e),
  });
  return (
    <>
      <SectionHead title="Jobs" text="Background work and when it runs. Times are Danish time. On the test site jobs only run while the site is awake; use Run now to test." />
      <Card clip>
        {q.isLoading ? (
          <SkeletonRows rows={8} />
        ) : (
          <Table<JobInfo>
            caption="Jobs" minWidth={760}
            rows={q.data ?? []}
            rowKey={(r) => r.key}
            columns={[
              { key: 'n', header: 'Job', render: (r) => <span className="stack" style={{ gap: 2 }}><strong>{r.label}</strong><span className="caption">{r.description}</span></span> },
              { key: 's', header: 'Schedule', render: (r) => scheduleText(r.schedule) },
              { key: 'l', header: 'Last run', render: (r) => (r.lastRun ? <span className="stack" style={{ gap: 2 }}><span className="row-flex" style={{ gap: 6 }}><Pill tone={r.lastRun.status === 'Succeeded' ? 'green' : r.lastRun.status === 'Failed' ? 'red' : 'amber'} small>{r.lastRun.status}</Pill>{formatDateTime(r.lastRun.at)}</span>{r.lastRun.message ? <span className="caption">{r.lastRun.message}</span> : null}</span> : 'Never') },
              { key: 'x', header: 'Next run', render: (r) => (r.nextRunAt ? formatDateTime(r.nextRunAt) : '–') },
              {
                key: 'a',
                header: '',
                className: 'num',
                render: (r) => (
                  <span className="row-flex" style={{ gap: 6, justifyContent: 'flex-end', flexWrap: 'nowrap' }}>
                    <Button size="sm" variant="secondary" icon="refresh" loading={run.isPending && run.variables?.key === r.key} onClick={() => run.mutate(r)}>
                      Run now
                    </Button>
                    <Button size="sm" variant="ghost" iconOnly icon="clock" aria-label={`Schedule of ${r.label}`} onClick={() => { setEditing(r); setSchedule(r.schedule); }} />
                  </span>
                ),
              },
            ]}
          />
        )}
      </Card>
      {editing && schedule ? (
        <Modal
          title={`Schedule: ${editing.label}`}
          onClose={() => setEditing(null)}
          onSubmit={() => save.mutate()}
          footer={
            <>
              <Button variant="secondary" onClick={() => setEditing(null)}>
                Cancel
              </Button>
              <Button type="submit" loading={save.isPending}>
                Save
              </Button>
            </>
          }
        >
          <ScheduleFields value={schedule} onChange={setSchedule} />
        </Modal>
      ) : null}
    </>
  );
}

const DQ_LABEL: Record<string, string> = {
  noticePeriodDays: 'Notice period',
  contractValue: 'Contract value',
  signedDate: 'Signed date (live contracts)',
  changeOfControl: 'Change of control answered',
  signedDocument: 'Signed main document (live contracts)',
  endDate: 'End date (open-ended counts as missing)',
  responsibleId: 'Responsible person',
  paymentTerms: 'Payment terms',
  governingLaw: 'Governing law',
};
const ROLE_LABEL: Record<Role, string> = { Admin: 'Administrators', Approver: 'Approvers', ContractOwner: 'Contract owners', Reader: 'Readers' };

export function GeneralAdmin() {
  const api = useApi();
  const toast = useToast();
  const refresh = useRefreshReference();
  const config = useAppConfig();
  const q = useQuery({ queryKey: qk.admin('app-settings'), queryFn: () => api.admin.getAppSettings() });
  const [s, setS] = useState<AppSettings | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  useEffect(() => {
    if (q.data && !s) setS(q.data);
  }, [q.data, s]);
  const save = useMutation({
    mutationFn: (x: AppSettings) => api.admin.updateAppSettings(x),
    onSuccess: async (x) => {
      setS(x);
      setErrors({});
      await refresh();
      toast.show('General settings saved');
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  if (!s) return <SkeletonRows rows={8} />;
  const dqOptions = [...DATA_QUALITY_FIELDS.map((k) => ({ key: k as string, label: DQ_LABEL[k] ?? k })), ...config.customFields.map((d) => ({ key: `cf.${d.key}`, label: `${d.label} (custom field)` }))];
  return (
    <>
      <SectionHead title="General" text="Name, formats, start pages, features and what counts as complete data." action={<Button loading={save.isPending} onClick={() => save.mutate(s)}>Save changes</Button>} />
      <div className="settings-grid">
        <Card pad className="stack" style={{ gap: 14 }}>
          <h3 className="card-title">App</h3>
          <Field label="App name" error={errors.appName}>
            {(p) => <TextInput {...p} value={s.appName} onChange={(e) => setS({ ...s, appName: e.target.value })} />}
          </Field>
          <Field label="Date format">
            {(p) => <Select {...p} value={s.dateFormat} options={['d MMM yyyy', 'dd-MM-yyyy', 'dd.MM.yyyy', 'yyyy-MM-dd', 'MM/dd/yyyy'].map((x) => ({ value: x, label: x }))} onChange={(e) => setS({ ...s, dateFormat: e.target.value as AppSettings['dateFormat'] })} />}
          </Field>
          <Field label="Number format">
            {(p) => <Select {...p} value={s.numberFormat} options={[{ value: 'en-GB', label: '1,250,000.50' }, { value: 'da-DK', label: '1.250.000,50' }, { value: 'de-DE', label: '1.250.000,50 (German)' }, { value: 'en-US', label: '1,250,000.50 (US)' }]} onChange={(e) => setS({ ...s, numberFormat: e.target.value as AppSettings['numberFormat'] })} />}
          </Field>
          <Field label="Sign out after (minutes without activity)" error={errors.sessionTimeoutMinutes}>
            {(p) => <TextInput {...p} inputMode="numeric" value={String(s.sessionTimeoutMinutes)} onChange={(e) => setS({ ...s, sessionTimeoutMinutes: Number(e.target.value) || 0 })} />}
          </Field>
        </Card>
        <Card pad className="stack" style={{ gap: 14 }}>
          <h3 className="card-title">Start page per role</h3>
          {(Object.keys(ROLE_LABEL) as Role[]).map((r) => (
            <Field key={r} label={ROLE_LABEL[r]}>
              {(p) => <Select {...p} value={s.landingPageByRole[r]} options={LANDING_OPTIONS} onChange={(e) => setS({ ...s, landingPageByRole: { ...s.landingPageByRole, [r]: e.target.value as AppSettings['landingPageByRole'][Role] } })} />}
            </Field>
          ))}
          <span className="caption">With several roles, the first in this order applies. People can choose their own in My settings.</span>
        </Card>
        <Card pad className="stack" style={{ gap: 14 }}>
          <h3 className="card-title">Features</h3>
          <label className="check">
            <input type="checkbox" checked={s.features.aiSuggestions} onChange={(e) => setS({ ...s, features: { ...s.features, aiSuggestions: e.target.checked } })} />
            AI suggestions when a contract file is uploaded
          </label>
          <label className="check">
            <input type="checkbox" checked={s.features.templates} onChange={(e) => setS({ ...s, features: { ...s.features, templates: e.target.checked } })} />
            Start new contracts from Word templates
          </label>
          <label className="check">
            <input type="checkbox" checked={s.features.eSign} onChange={(e) => setS({ ...s, features: { ...s.features, eSign: e.target.checked } })} />
            E-signature (placeholder; no provider is connected yet)
          </label>
          <Field label="Due diligence threshold, DKK" hint="Overview lists contracts at or above this value." error={errors.ddThresholdDkk}>
            {(p) => <TextInput {...p} inputMode="numeric" value={String(s.ddThresholdDkk)} onChange={(e) => setS({ ...s, ddThresholdDkk: Number(e.target.value.replace(/\D/g, '')) || 0 })} />}
          </Field>
        </Card>
        <Card pad className="stack" style={{ gap: 14 }}>
          <h3 className="card-title">Data quality</h3>
          <p className="secondary" style={{ fontSize: 13 }}>Fields that count toward “missing data” on contracts, on My work and in the Overview score.</p>
          <div className="stack" style={{ gap: 6 }}>
            {dqOptions.map((o) => (
              <label key={o.key} className="check">
                <input type="checkbox" checked={s.dataQualityFields.includes(o.key as never)} onChange={(e) => setS({ ...s, dataQualityFields: e.target.checked ? [...s.dataQualityFields, o.key as never] : s.dataQualityFields.filter((x) => x !== o.key) })} />
                {o.label}
              </label>
            ))}
          </div>
        </Card>
      </div>
    </>
  );
}
