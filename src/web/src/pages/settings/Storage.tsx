// Settings for where files live (owner item G, and "the app is the only door" for storage):
// department libraries and groups, the access mapping matrix, storage health and the
// reconciliation report.
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi, useAppConfig } from '../../app/context';
import type { DepartmentAdmin, ReconciliationItem, ReconciliationKind } from '../../api/types';
import { Button, Card, EmptyState, ErrorState, Field, Modal, Pill, SkeletonRows, Table, TextInput, useToast } from '../../components';
import { formatDateTime } from '../../lib/dates';
import { plural } from '../../lib/format';
import { SectionHead } from './common';
import { LinkCell, problemErrors, useLinkTest } from './Reference';

function TestModeNote() {
  const config = useAppConfig();
  if (config.storageMode === 'SharePoint') return null;
  return (
    <p className="caption">
      <Pill tone="gray" small>
        Used in SharePoint mode
      </Pill>{' '}
      The test site keeps files in local storage. These settings are saved now and used when the site runs against SharePoint.
    </p>
  );
}

function DepartmentLinkDialog({ item, onClose }: { item: DepartmentAdmin; onClose: () => void }) {
  const api = useApi();
  const client = useQueryClient();
  const toast = useToast();
  const [library, setLibrary] = useState(item.documentLibrary ?? '');
  const [groupId, setGroupId] = useState(item.groupId ?? '');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const save = useMutation({
    mutationFn: () => api.admin.updateDepartmentV4(item.id, { name: item.name, isActive: item.isActive, documentLibrary: library.trim() || null, groupId: groupId.trim() || null }),
    onSuccess: async () => {
      await Promise.all([client.invalidateQueries({ queryKey: qk.admin('departments-v4') }), client.invalidateQueries({ queryKey: qk.admin('access-mapping') })]);
      toast.show(`${item.name} saved`);
      onClose();
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  return (
    <Modal
      title={`SharePoint and Entra: ${item.name}`}
      onClose={onClose}
      onSubmit={() => (groupId.trim() && !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(groupId.trim()) ? setErrors({ groupId: 'The Entra group object id (a GUID)' }) : save.mutate())}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            Save
          </Button>
        </>
      }
    >
      <Field label="Document library" optional hint="Name of the library in every entity site, e.g. Purchasing contracts." error={errors.documentLibrary}>
        {(p) => <TextInput {...p} data-autofocus="" value={library} onChange={(e) => setLibrary(e.target.value)} />}
      </Field>
      <Field label="Entra group of the department" optional hint={item.groupName ? `Resolved: ${item.groupName}` : 'Object id of the group'} error={errors.groupId}>
        {(p) => <TextInput {...p} value={groupId} onChange={(e) => setGroupId(e.target.value)} />}
      </Field>
    </Modal>
  );
}

/** Below the department list: library and group per department, with "Test link". */
export function DepartmentLinks() {
  const api = useApi();
  const q = useQuery({ queryKey: qk.admin('departments-v4'), queryFn: () => api.admin.getDepartmentsV4() });
  const test = useLinkTest((id) => api.admin.testDepartmentLink(id), qk.admin('departments-v4'));
  const [editing, setEditing] = useState<DepartmentAdmin | null>(null);
  return (
    <Card clip style={{ marginTop: 16 }}>
      <div className="card-list-head">
        <h3 className="card-title">Document libraries and groups</h3>
        <TestModeNote />
      </div>
      {q.isLoading ? (
        <SkeletonRows rows={4} />
      ) : q.isError ? (
        <ErrorState text="The departments could not be loaded." onRetry={() => void q.refetch()} />
      ) : (
        <Table<DepartmentAdmin>
          caption="Department libraries and groups"
          minWidth={760}
          rows={(q.data ?? []).filter((d) => d.isActive !== false)}
          rowKey={(r) => r.id}
          columns={[
            { key: 'n', header: 'Department', render: (r) => <strong>{r.name}</strong> },
            { key: 'l', header: 'Document library', render: (r) => r.documentLibrary ?? <span className="caption">Not set</span> },
            { key: 'g', header: 'Entra group', render: (r) => r.groupName ?? (r.groupId ? <span className="mono caption">{r.groupId}</span> : <span className="caption">Not set</span>) },
            { key: 's', header: 'Link', render: (r) => <LinkCell link={r.link} name={r.name} testing={test.isPending && test.variables === r.id} onTest={() => test.mutate(r.id)} /> },
            { key: 'e', header: '', className: 'num', render: (r) => <Button variant="ghost" size="sm" iconOnly icon="edit" aria-label={`Edit library and group of ${r.name}`} onClick={() => setEditing(r)} /> },
          ]}
        />
      )}
      {editing ? <DepartmentLinkDialog item={editing} onClose={() => setEditing(null)} /> : null}
    </Card>
  );
}

/** Settings > Access mapping: entity × department -> site, library, groups, with warnings. */
export function AccessMappingAdmin() {
  const api = useApi();
  const q = useQuery({ queryKey: qk.admin('access-mapping'), queryFn: () => api.admin.getAccessMapping() });
  const data = q.data;
  return (
    <>
      <SectionHead title="Access mapping" text="Where each legal entity and department keeps its contract files, and which Entra groups get access through the app. Users never open the libraries directly: the app is the only door." />
      <TestModeNote />
      {q.isLoading ? (
        <SkeletonRows rows={6} />
      ) : q.isError || !data ? (
        <ErrorState text="The access mapping could not be loaded." onRetry={() => void q.refetch()} />
      ) : (
        <div className="stack" style={{ gap: 16 }}>
          {data.warnings.length ? (
            <Card pad className="stack" style={{ gap: 6 }} role="region" aria-label="Missing mappings">
              <strong>
                {plural(data.warnings.length, 'warning')}
              </strong>
              <ul className="warning-list">
                {data.warnings.map((w) => (
                  <li key={w}>{w}</li>
                ))}
              </ul>
            </Card>
          ) : (
            <Card pad>
              <Pill tone="green">Every entity and department is mapped</Pill>
            </Card>
          )}
          <Card clip>
            <Table
              caption="Access mapping"
              minWidth={900}
              rows={data.cells}
              rowKey={(r) => `${r.entityId}:${r.departmentId}`}
              columns={[
                { key: 'e', header: 'Legal entity', render: (r) => data.entities.find((x) => x.id === r.entityId)?.name ?? '–' },
                { key: 'd', header: 'Department', render: (r) => data.departments.find((x) => x.id === r.departmentId)?.name ?? '–' },
                { key: 's', header: 'Site', render: (r) => (r.siteUrl ? <span className="cell-clip mono caption" title={r.siteUrl}>{r.siteUrl.replace(/^https:\/\//, '')}</span> : <Pill tone="red" small>No site</Pill>) },
                { key: 'l', header: 'Library', render: (r) => r.library ?? <Pill tone="amber" small>No library</Pill> },
                { key: 'g', header: 'Groups', render: (r) => [r.entityGroupName, r.departmentGroupName].filter(Boolean).join(' · ') || <span className="caption">None</span> },
                { key: 'c', header: 'Contracts', className: 'num', render: (r) => r.contractCount },
                { key: 'w', header: 'Warnings', render: (r) => (r.warnings.length ? <span className="caption fg-amber">{r.warnings.join('; ')}</span> : <span className="caption">–</span>) },
              ]}
            />
          </Card>
        </div>
      )}
    </>
  );
}

const KIND_LABEL: Record<ReconciliationKind, string> = { MissingFile: 'Missing file', Orphan: 'Orphan file', Mismatch: 'Mismatch', MisplacedFolder: 'Misplaced folder' };

/** Settings > Storage health: last successful call and the latest reconciliation report. */
export function StorageHealthAdmin() {
  const api = useApi();
  const client = useQueryClient();
  const toast = useToast();
  const health = useQuery({ queryKey: qk.admin('storage-health'), queryFn: () => api.admin.getStorageHealth() });
  const report = useQuery({ queryKey: qk.admin('reconciliation'), queryFn: () => api.admin.getReconciliation() });
  const run = useMutation({
    mutationFn: () => api.admin.runReconciliation(),
    onSuccess: async (r) => {
      await client.invalidateQueries({ queryKey: qk.admin('reconciliation') });
      await client.invalidateQueries({ queryKey: qk.admin('storage-health') });
      toast.show(r.status === 'Running' ? 'The check is running' : `Check finished: ${plural(r.items.length, 'finding')}`);
    },
    onError: (e) => toast.error(e),
  });
  const h = health.data;
  const r = report.data;
  const counts = (kind: ReconciliationKind) => r?.items.filter((i) => i.kind === kind).length ?? 0;
  return (
    <>
      <SectionHead title="Storage health" text="Whether the app can reach the file storage, and whether every document row has its file and every file its row." action={<Button icon="refresh" loading={run.isPending} onClick={() => run.mutate()}>Run check now</Button>} />
      <div className="settings-grid">
        <Card pad className="stack" style={{ gap: 10 }}>
          <h3 className="card-title">Connection</h3>
          {health.isLoading ? (
            <SkeletonRows rows={3} />
          ) : health.isError || !h ? (
            <ErrorState text="The storage health could not be loaded." onRetry={() => void health.refetch()} />
          ) : (
            <>
              <span>
                <Pill tone={h.state === 'Healthy' ? 'green' : h.state === 'Degraded' ? 'amber' : h.state === 'Down' ? 'red' : 'gray'}>{h.state === 'NotConfigured' ? 'Not configured' : h.state}</Pill> <span className="caption">{h.mode === 'Local' ? 'Local storage (test)' : 'SharePoint'}</span>
              </span>
              <dl className="kv-list">
                <dt>Last successful call</dt>
                <dd>{h.lastSuccessAt ? formatDateTime(h.lastSuccessAt) : 'Never'}</dd>
                <dt>Last failure</dt>
                <dd>{h.lastFailureAt ? `${formatDateTime(h.lastFailureAt)}${h.lastError ? ` · ${h.lastError}` : ''}` : 'None'}</dd>
                <dt>Failures in the last 24 hours</dt>
                <dd>{h.recentFailures}</dd>
                <dt>Checked</dt>
                <dd>{formatDateTime(h.checkedAt)}</dd>
              </dl>
              {h.details.map((d) => (
                <span key={d} className="caption">
                  {d}
                </span>
              ))}
            </>
          )}
        </Card>
        <Card pad className="stack" style={{ gap: 10 }}>
          <h3 className="card-title">Latest check</h3>
          {report.isLoading ? (
            <SkeletonRows rows={3} />
          ) : report.isError ? (
            <ErrorState text="The report could not be loaded." onRetry={() => void report.refetch()} />
          ) : !r ? (
            <EmptyState compact title="Not run yet" text="Run a check to compare the document rows with the files in storage." />
          ) : (
            <>
              <span className="caption">
                {r.status === 'Running' ? 'Running since' : 'Finished'} {formatDateTime(r.finishedAt ?? r.startedAt)} · {plural(r.checkedDocuments, 'document')} in {plural(r.checkedFolders, 'folder')}
              </span>
              {r.error ? <span className="fg-red">{r.error}</span> : null}
              <div className="row-flex" style={{ gap: 8, flexWrap: 'wrap' }}>
                {(Object.keys(KIND_LABEL) as ReconciliationKind[]).map((k) => (
                  <Pill key={k} tone={counts(k) ? 'amber' : 'green'} small>
                    {KIND_LABEL[k]}: {counts(k)}
                  </Pill>
                ))}
              </div>
            </>
          )}
        </Card>
      </div>
      {r && r.items.length ? (
        <Card clip style={{ marginTop: 16 }}>
          <Table<ReconciliationItem>
            caption="Findings"
            minWidth={760}
            rows={r.items}
            rowKey={(i) => `${i.kind}:${i.path}:${i.documentId ?? ''}`}
            columns={[
              { key: 'k', header: 'Finding', render: (i) => KIND_LABEL[i.kind] },
              { key: 'c', header: 'Contract', render: (i) => i.contractNo ?? '–' },
              { key: 'p', header: 'Path', render: (i) => <span className="cell-clip mono caption" title={i.path}>{i.path}</span> },
              { key: 'd', header: 'Detail', render: (i) => i.detail },
            ]}
          />
        </Card>
      ) : null}
    </>
  );
}
