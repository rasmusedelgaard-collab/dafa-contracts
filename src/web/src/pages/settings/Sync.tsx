/* eslint-disable react-refresh/only-export-components */
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi } from '../../app/context';
import type { ImportKind, ImportMapping, Schedule, SyncSource, SyncSourceInput, SyncTestResult } from '../../api/types';
import { Button, Card, EmptyState, ErrorState, Field, Modal, Pill, Segmented, Select, SkeletonRows, Table, Textarea, TextInput, useToast } from '../../components';
import { formatDateTime } from '../../lib/dates';
import { plural } from '../../lib/format';
import { useInvalidateContracts } from '../../app/context';
import { ConfirmDelete, SectionHead } from './common';
import { problemErrors, useRefreshReference } from './Reference';

const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

export function scheduleText(s: Schedule): string {
  if (s.frequency === 'Off') return 'Off';
  if (s.frequency === 'Hourly') return 'Every hour';
  if (s.frequency === 'Daily') return `Daily at ${s.time}`;
  return `${DAYS[(s.dayOfWeek ?? 1) - 1]}s at ${s.time}`;
}

export function ScheduleFields({ value, onChange, errors = {} }: { value: Schedule; onChange: (s: Schedule) => void; errors?: Record<string, string> }) {
  return (
    <div className="row-flex schedule-fields" style={{ gap: 12, alignItems: 'flex-end' }}>
      <Field label="Runs">
        {(p) => (
          <Select
            {...p}
            value={value.frequency}
            options={[
              { value: 'Off', label: 'Off (only on request)' },
              { value: 'Hourly', label: 'Every hour' },
              { value: 'Daily', label: 'Daily' },
              { value: 'Weekly', label: 'Weekly' },
            ]}
            onChange={(e) => onChange({ ...value, frequency: e.target.value as Schedule['frequency'], dayOfWeek: e.target.value === 'Weekly' ? (value.dayOfWeek ?? 1) : null })}
          />
        )}
      </Field>
      {value.frequency === 'Weekly' ? (
        <Field label="Day" error={errors['schedule.dayOfWeek']}>
          {(p) => <Select {...p} value={String(value.dayOfWeek ?? 1)} options={DAYS.map((d, i) => ({ value: String(i + 1), label: d }))} onChange={(e) => onChange({ ...value, dayOfWeek: Number(e.target.value) })} />}
        </Field>
      ) : null}
      {value.frequency === 'Daily' || value.frequency === 'Weekly' ? (
        <Field label="Time (Danish time)" error={errors['schedule.time']}>
          {(p) => <TextInput {...p} type="time" value={value.time} onChange={(e) => onChange({ ...value, time: e.target.value })} />}
        </Field>
      ) : null}
    </div>
  );
}

const EMPTY: SyncSourceInput = {
  name: '',
  target: 'counterparties',
  kind: 'PowerBi',
  powerBi: { workspaceId: '', datasetId: '', daxQuery: '', table: null },
  sql: null,
  mapping: {},
  options: { mode: 'CreateAndUpdate' },
  schedule: { frequency: 'Daily', time: '05:00', dayOfWeek: null },
  isActive: true,
};

function SourceDialog({ item, onClose }: { item: SyncSource | null; onClose: () => void }) {
  const api = useApi();
  const toast = useToast();
  const client = useQueryClient();
  const connections = useQuery({ queryKey: qk.admin('sync-connections'), queryFn: () => api.admin.getSyncConnections() });
  const [d, setD] = useState<SyncSourceInput>(() => (item ? (({ id: _i, lastRun: _l, nextRunAt: _n, ...rest }) => rest)(item) : EMPTY));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [test, setTest] = useState<SyncTestResult | null>(null);
  const [byTable, setByTable] = useState(!!item?.powerBi?.table);
  const fields = useQuery({ queryKey: qk.admin('import-fields', d.target), queryFn: () => api.admin.getImportFields(d.target) });

  const runTest = useMutation({ mutationFn: () => api.admin.testSyncSource(d), onSuccess: setTest, onError: (e) => toast.error(e) });
  const save = useMutation({
    mutationFn: () => (item ? api.admin.updateSyncSource(item.id, d) : api.admin.createSyncSource(d)),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: qk.admin('sync') });
      toast.show('Sync source saved');
      onClose();
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  const setKind = (kind: SyncSourceInput['kind']) =>
    setD({ ...d, kind, powerBi: kind === 'PowerBi' ? (d.powerBi ?? { workspaceId: '', datasetId: '', daxQuery: '', table: null }) : null, sql: kind === 'Sql' ? (d.sql ?? { connectionName: '', query: 'SELECT ' }) : null });
  const columns = test?.ok ? test.columns : Object.values(d.mapping).filter((x): x is string => !!x);

  return (
    <Modal
      wide
      title={item ? `Edit ${item.name}` : 'Add sync source'}
      subtitle="Reads rows from Power BI or a SQL database on a schedule and imports them like a file. Passwords and connection strings stay in the server configuration."
      onClose={onClose}
      onSubmit={() => save.mutate()}
      footer={
        <>
          <Button variant="secondary" loading={runTest.isPending} onClick={() => runTest.mutate()}>
            Test connection
          </Button>
          <div className="spacer" />
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            Save
          </Button>
        </>
      }
    >
      <div className="stack" style={{ gap: 16 }}>
        <div className="form-grid form-grid--flat">
          <Field label="Name" error={errors.name}>
            {(p) => <TextInput {...p} value={d.name} onChange={(e) => setD({ ...d, name: e.target.value })} />}
          </Field>
          <Field label="Imports">
            {(p) => (
              <Select
                {...p}
                value={d.target}
                options={[
                  { value: 'counterparties', label: 'Counterparties' },
                  { value: 'users', label: 'Users' },
                  { value: 'contracts', label: 'Contracts' },
                ]}
                onChange={(e) => setD({ ...d, target: e.target.value as ImportKind, mapping: {} })}
              />
            )}
          </Field>
        </div>
        <div className="field">
          <span className="field__label">Source</span>
          <Segmented mode="radio" ariaLabel="Source" options={[{ value: 'PowerBi', label: 'Power BI semantic model' }, { value: 'Sql', label: 'SQL database' }]} value={d.kind} onChange={setKind} />
        </div>
        {d.kind === 'PowerBi' && d.powerBi ? (
          <div className="form-grid form-grid--flat">
            <Field label="Workspace" error={errors['powerBi.workspaceId']} hint="Workspace name or id">
              {(p) => <TextInput {...p} value={d.powerBi?.workspaceId ?? ''} onChange={(e) => setD({ ...d, powerBi: { ...d.powerBi!, workspaceId: e.target.value } })} />}
            </Field>
            <Field label="Semantic model (dataset)" error={errors['powerBi.datasetId']}>
              {(p) => <TextInput {...p} value={d.powerBi?.datasetId ?? ''} onChange={(e) => setD({ ...d, powerBi: { ...d.powerBi!, datasetId: e.target.value } })} />}
            </Field>
            <div className="form-grid__full field">
              <Segmented size="sm" ariaLabel="Read" options={[{ value: 'dax', label: 'DAX query' }, { value: 'table', label: 'Whole table' }]} value={byTable ? 'table' : 'dax'} onChange={(v) => { setByTable(v === 'table'); setD({ ...d, powerBi: { ...d.powerBi!, daxQuery: v === 'table' ? null : (d.powerBi?.daxQuery ?? ''), table: v === 'table' ? (d.powerBi?.table ?? '') : null } }); }} />
            </div>
            {byTable ? (
              <Field label="Table" error={errors['powerBi.daxQuery']} className="form-grid__full">
                {(p) => <TextInput {...p} value={d.powerBi?.table ?? ''} onChange={(e) => setD({ ...d, powerBi: { ...d.powerBi!, table: e.target.value } })} />}
              </Field>
            ) : (
              <Field label="DAX query" error={errors['powerBi.daxQuery']} className="form-grid__full" hint="Must start with EVALUATE. Row-level security of the model applies to the app registration.">
                {(p) => <Textarea {...p} rows={4} className="textarea mono" value={d.powerBi?.daxQuery ?? ''} onChange={(e) => setD({ ...d, powerBi: { ...d.powerBi!, daxQuery: e.target.value } })} />}
              </Field>
            )}
          </div>
        ) : null}
        {d.kind === 'Sql' && d.sql ? (
          <div className="form-grid form-grid--flat">
            <Field label="Connection" error={errors['sql.connectionName']} hint="Configured on the server; the password is never shown here." className="form-grid__full">
              {(p) => <Select {...p} value={d.sql?.connectionName ?? ''} placeholder="Choose" options={(connections.data ?? []).filter((c) => c.kind === 'Sql').map((c) => ({ value: c.name, label: `${c.name} – ${c.description}` }))} onChange={(e) => setD({ ...d, sql: { ...d.sql!, connectionName: e.target.value } })} />}
            </Field>
            <Field label="Query" error={errors['sql.query']} className="form-grid__full" hint="A read-only SELECT (or WITH … SELECT). Runs with a 60 second limit.">
              {(p) => <Textarea {...p} rows={4} className="textarea mono" value={d.sql?.query ?? ''} onChange={(e) => setD({ ...d, sql: { ...d.sql!, query: e.target.value } })} />}
            </Field>
          </div>
        ) : null}
        {test ? (
          <div className={`banner ${test.ok ? 'tone-green' : 'tone-red'}`} role="status">
            {test.message}
          </div>
        ) : null}
        {test?.ok && test.sample.length ? (
          <div className="table-wrap sample-table">
            <table className="table">
              <caption className="sr-only">First rows</caption>
              <thead>
                <tr>
                  {test.columns.map((c) => (
                    <th key={c} scope="col">
                      {c}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {test.sample.map((r, i) => (
                  <tr key={i}>
                    {r.map((cell, j) => (
                      <td key={j}>{cell}</td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
        <h3 className="form-section">Column mapping</h3>
        {columns.length === 0 ? (
          <p className="secondary">Test the connection to read the columns, then match them to the fields.</p>
        ) : (
          <div className="mapping">
            {(fields.data ?? []).map((f) => (
              <div key={f.key} className={`mapping__row${f.required && !d.mapping[f.key] ? ' mapping__row--missing' : ''}`}>
                <div className="mapping__field">
                  <strong>{f.label}</strong>
                  {f.required ? <Pill tone="amber" small>Required</Pill> : null}
                </div>
                <Select aria-label={`Column for ${f.label}`} value={d.mapping[f.key] ?? ''} placeholder="Not imported" options={[...new Set(columns)].map((c) => ({ value: c, label: c }))} onChange={(e) => setD({ ...d, mapping: { ...d.mapping, [f.key]: e.target.value || null } as ImportMapping })} />
                <span />
              </div>
            ))}
          </div>
        )}
        <div className="form-grid form-grid--flat">
          <Field label="Existing records">
            {(p) => (
              <Select
                {...p}
                value={d.options.mode}
                options={[
                  { value: 'CreateAndUpdate', label: 'Create new, update existing' },
                  { value: 'SkipDuplicates', label: 'Create new, skip existing' },
                  { value: 'CreateOnly', label: 'Create only' },
                ]}
                onChange={(e) => setD({ ...d, options: { ...d.options, mode: e.target.value as SyncSourceInput['options']['mode'] } })}
              />
            )}
          </Field>
          <label className="check" style={{ alignSelf: 'end', minHeight: 44 }}>
            <input type="checkbox" checked={d.isActive} onChange={(e) => setD({ ...d, isActive: e.target.checked })} />
            Active
          </label>
        </div>
        <ScheduleFields value={d.schedule} onChange={(schedule) => setD({ ...d, schedule })} errors={errors} />
      </div>
    </Modal>
  );
}

function Runs({ source, onClose }: { source: SyncSource; onClose: () => void }) {
  const api = useApi();
  const q = useQuery({ queryKey: qk.admin('sync', 'runs', source.id), queryFn: () => api.admin.getSyncRuns(source.id) });
  return (
    <Modal wide title={`Run history – ${source.name}`} onClose={onClose} footer={<Button onClick={onClose}>Close</Button>}>
      {q.isLoading ? (
        <SkeletonRows rows={4} />
      ) : q.isError ? (
        <ErrorState text="The run history could not be loaded." onRetry={() => void q.refetch()} />
      ) : (q.data ?? []).length === 0 ? (
        <EmptyState compact title="Not run yet" />
      ) : (
        <ol className="stack" style={{ gap: 10 }}>
          {q.data?.map((r) => (
            <li key={r.id} className="run-row">
              <div className="row-flex" style={{ gap: 10 }}>
                <Pill tone={r.status === 'Succeeded' ? 'green' : r.status === 'Failed' ? 'red' : 'amber'}>{r.status === 'PartiallySucceeded' ? 'Partly succeeded' : r.status}</Pill>
                <strong>{formatDateTime(r.startedAt)}</strong>
                <span className="caption">{r.trigger === 'Manual' ? 'Run by hand' : 'Scheduled'}</span>
              </div>
              <span className="secondary">
                {r.read} read · {r.created} created · {r.updated} updated · {r.skipped} skipped · {r.failed} failed
              </span>
              {r.errors.map((e, i) => (
                <span key={i} className="fg-red" style={{ fontSize: 13 }}>
                  {e}
                </span>
              ))}
            </li>
          ))}
        </ol>
      )}
    </Modal>
  );
}

export function SyncSources() {
  const api = useApi();
  const toast = useToast();
  const client = useQueryClient();
  const refresh = useRefreshReference();
  const invalidate = useInvalidateContracts();
  const q = useQuery({ queryKey: qk.admin('sync'), queryFn: () => api.admin.getSyncSources() });
  const [editing, setEditing] = useState<SyncSource | 'new' | null>(null);
  const [runs, setRuns] = useState<SyncSource | null>(null);
  const [deleting, setDeleting] = useState<SyncSource | null>(null);
  const run = useMutation({
    mutationFn: (s: SyncSource) => api.admin.runSyncSource(s.id),
    onSuccess: async (r) => {
      await Promise.all([client.invalidateQueries({ queryKey: qk.admin('sync') }), refresh(), invalidate()]);
      if (r.status === 'Failed') toast.error(null, `The run failed: ${r.errors[0] ?? 'see the run history'}`);
      else toast.show(`Run finished: ${r.created} created, ${r.updated} updated, ${r.failed} failed`);
    },
    onError: (e) => toast.error(e),
  });
  const remove = useMutation({
    mutationFn: (s: SyncSource) => api.admin.deleteSyncSource(s.id),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: qk.admin('sync') });
      setDeleting(null);
    },
    onError: (e) => toast.error(e),
  });
  return (
    <>
      <SectionHead title="Sync sources" text="Keep counterparties, users or contracts in step with Power BI or a SQL database. Each source has its own column mapping and schedule." action={<Button icon="plus" onClick={() => setEditing('new')}>Add sync source</Button>} />
      <Card clip>
        {q.isLoading ? (
          <SkeletonRows rows={3} />
        ) : q.isError ? (
          <ErrorState onRetry={() => void q.refetch()} />
        ) : (q.data ?? []).length === 0 ? (
          <EmptyState icon="refresh" title="No sync sources" text="Add one to read the supplier master from Power BI or the data warehouse." />
        ) : (
          <Table<SyncSource>
            caption="Sync sources" minWidth={760}
            rows={q.data ?? []}
            rowKey={(r) => r.id}
            columns={[
              { key: 'n', header: 'Name', render: (r) => <span className="stack" style={{ gap: 2 }}><strong>{r.name}</strong><span className="caption">{r.kind === 'PowerBi' ? `Power BI · ${r.powerBi?.datasetId}` : `SQL · ${r.sql?.connectionName}`} → {r.target}</span></span> },
              { key: 's', header: 'Schedule', render: (r) => (r.isActive ? scheduleText(r.schedule) : <Pill tone="gray">Inactive</Pill>) },
              { key: 'l', header: 'Last run', render: (r) => (r.lastRun ? <span className="stack" style={{ gap: 2 }}><Pill tone={r.lastRun.status === 'Succeeded' ? 'green' : r.lastRun.status === 'Failed' ? 'red' : 'amber'}>{r.lastRun.status === 'PartiallySucceeded' ? 'Partly' : r.lastRun.status}</Pill><span className="caption">{formatDateTime(r.lastRun.startedAt)} · {plural(r.lastRun.read, 'row')}</span></span> : '–') },
              { key: 'x', header: 'Next run', render: (r) => (r.nextRunAt ? formatDateTime(r.nextRunAt) : '–') },
              {
                key: 'a',
                header: '',
                className: 'num',
                render: (r) => (
                  <span className="row-flex" style={{ gap: 6, justifyContent: 'flex-end', flexWrap: 'nowrap' }}>
                    <Button size="sm" variant="secondary" icon="refresh" loading={run.isPending && run.variables?.id === r.id} onClick={() => run.mutate(r)}>
                      Run now
                    </Button>
                    <Button size="sm" variant="ghost" onClick={() => setRuns(r)}>
                      History
                    </Button>
                    <Button size="sm" variant="ghost" iconOnly icon="edit" aria-label={`Edit ${r.name}`} onClick={() => setEditing(r)} />
                    <Button size="sm" variant="ghost" iconOnly icon="trash" aria-label={`Delete ${r.name}`} onClick={() => setDeleting(r)} />
                  </span>
                ),
              },
            ]}
          />
        )}
      </Card>
      {editing ? <SourceDialog item={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
      {runs ? <Runs source={runs} onClose={() => setRuns(null)} /> : null}
      {deleting ? <ConfirmDelete title={`Delete ${deleting.name}?`} text="The run history is kept; nothing that was imported is removed." pending={remove.isPending} onConfirm={() => remove.mutate(deleting)} onClose={() => setDeleting(null)} /> : null}
    </>
  );
}
