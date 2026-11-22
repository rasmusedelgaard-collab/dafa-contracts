/* eslint-disable react-refresh/only-export-components */
import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi, useAppConfig } from '../../app/context';
import type { Currency, LegalEntity, LegalEntityInput, LinkStatus, LinkTestResult, Tag, TagColor, TagInput } from '../../api/types';
import { Button, Card, EmptyState, ErrorState, Field, Modal, Pill, Select, SkeletonRows, Table, TextInput, useToast } from '../../components';
import { plural } from '../../lib/format';
import { formatDateTime } from '../../lib/dates';
import { fieldErrors } from '../../lib/errors';
import { ConfirmDelete, SectionHead } from './common';

/** Refreshes lookups and app config after a reference-data change. */
export function useRefreshReference() {
  const client = useQueryClient();
  return () => Promise.all([client.invalidateQueries({ queryKey: qk.lookups }), client.invalidateQueries({ queryKey: qk.appConfig }), client.invalidateQueries({ queryKey: ['admin'] })]);
}

/** Field errors from a 400 problem. */
export function problemErrors(e: unknown): Record<string, string> {
  return fieldErrors(e);
}

function EntityDialog({ item, currencies, onClose }: { item: LegalEntity | null; currencies: Currency[]; onClose: () => void }) {
  const api = useApi();
  const toast = useToast();
  const refresh = useRefreshReference();
  const [d, setD] = useState<LegalEntityInput>(item ? { name: item.name, country: item.country, currency: item.currency, isActive: item.isActive, sharePointSiteUrl: item.sharePointSiteUrl ?? '', managementGroupId: item.managementGroupId ?? '' } : { name: '', country: 'DK', currency: 'DKK', isActive: true, sharePointSiteUrl: '', managementGroupId: '' });
  const config = useAppConfig();
  const [errors, setErrors] = useState<Record<string, string>>({});
  const save = useMutation({
    mutationFn: () => {
      const input = { ...d, sharePointSiteUrl: d.sharePointSiteUrl?.trim() || null, managementGroupId: d.managementGroupId?.trim() || null };
      return item ? api.admin.updateEntity(item.id, input) : api.admin.createEntity(input);
    },
    onSuccess: async () => {
      await refresh();
      toast.show(item ? 'Legal entity saved' : 'Legal entity added');
      onClose();
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  const submit = () => {
    const e: Record<string, string> = {};
    if (!d.name.trim()) e.name = 'Enter the name';
    if (!/^[A-Za-z]{2}$/.test(d.country.trim())) e.country = 'Two letters, e.g. DK';
    if (d.sharePointSiteUrl?.trim() && !/^https:\/\/[a-z0-9-]+\.sharepoint\.com\/sites\/[A-Za-z0-9._-]+\/?$/.test(d.sharePointSiteUrl.trim())) e.sharePointSiteUrl = 'Use https://<tenant>.sharepoint.com/sites/<name>';
    if (d.managementGroupId?.trim() && !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(d.managementGroupId.trim())) e.managementGroupId = 'The Entra group object id (a GUID)';
    setErrors(e);
    if (!Object.keys(e).length) save.mutate();
  };
  return (
    <Modal
      title={item ? 'Edit legal entity' : 'Add legal entity'}
      onClose={onClose}
      onSubmit={submit}
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
      <div className="form-grid form-grid--flat">
        <Field label="Name" error={errors.name} className="form-grid__full">
          {(p) => <TextInput {...p} value={d.name} onChange={(e) => setD({ ...d, name: e.target.value })} />}
        </Field>
        <Field label="Country" error={errors.country} hint="ISO code, e.g. DK, SE, DE">
          {(p) => <TextInput {...p} maxLength={2} value={d.country} onChange={(e) => setD({ ...d, country: e.target.value.toUpperCase() })} />}
        </Field>
        <Field label="Currency" error={errors.currency}>
          {(p) => <Select {...p} value={d.currency} options={currencies.filter((c) => c.isActive).map((c) => ({ value: c.code, label: `${c.code} – ${c.name}` }))} onChange={(e) => setD({ ...d, currency: e.target.value })} />}
        </Field>
        <label className="check form-grid__full">
          <input type="checkbox" checked={d.isActive} onChange={(e) => setD({ ...d, isActive: e.target.checked })} />
          Active (offered for new contracts)
        </label>
        <h3 className="form-section">SharePoint and Entra ID{config.storageMode !== 'SharePoint' ? <Pill tone="gray" small>Used in SharePoint mode</Pill> : null}</h3>
        <Field label="SharePoint site URL" optional error={errors.sharePointSiteUrl} className="form-grid__full" hint="The site that holds this entity's contract libraries.">
          {(p) => <TextInput {...p} type="url" placeholder="https://dafa.sharepoint.com/sites/contracts-…" value={d.sharePointSiteUrl ?? ''} onChange={(e) => setD({ ...d, sharePointSiteUrl: e.target.value })} />}
        </Field>
        <Field label="Resolved site id" optional className="form-grid__full" hint="Filled in by Test link.">
          {(p) => <TextInput {...p} readOnly value={item?.sharePointSiteId ?? ''} placeholder="Not resolved yet" />}
        </Field>
        <Field label="Entra group of the entity's management" optional error={errors.managementGroupId} className="form-grid__full" hint={item?.managementGroupName ? `Resolved: ${item.managementGroupName}` : 'Object id of the group, e.g. 6f1a2b3c-…'}>
          {(p) => <TextInput {...p} value={d.managementGroupId ?? ''} onChange={(e) => setD({ ...d, managementGroupId: e.target.value })} />}
        </Field>
      </div>
    </Modal>
  );
}

export function LegalEntitiesAdmin() {
  const api = useApi();
  const list = useQuery({ queryKey: qk.admin('entities'), queryFn: () => api.admin.getEntities() });
  const currencies = useQuery({ queryKey: qk.admin('currencies'), queryFn: () => api.admin.getCurrencies() });
  const [editing, setEditing] = useState<LegalEntity | null | 'new'>(null);
  const test = useLinkTest((id) => api.admin.testEntityLink(id), qk.admin('entities'));
  return (
    <>
      <SectionHead title="Legal entities" text="The DAFA companies a contract can belong to, with their country and functional currency. Each one gets its own SharePoint site in production. Deactivate instead of deleting." action={<Button icon="plus" onClick={() => setEditing('new')}>Add legal entity</Button>} />
      <Card clip>
        {list.isLoading ? (
          <SkeletonRows rows={6} />
        ) : list.isError ? (
          <ErrorState onRetry={() => void list.refetch()} />
        ) : (
          <Table<LegalEntity>
            caption="Legal entities" minWidth={760}
            rows={list.data ?? []}
            rowKey={(r) => r.id}
            onRowClick={(r) => setEditing(r)}
            columns={[
              { key: 'n', header: 'Name', render: (r) => <strong>{r.name}</strong> },
              { key: 'c', header: 'Country', render: (r) => r.country },
              { key: 'cur', header: 'Currency', render: (r) => r.currency },
              { key: 'k', header: 'Contracts', className: 'num', render: (r) => r.contractCount },
              { key: 'a', header: 'Status', render: (r) => (r.isActive ? <Pill tone="green">Active</Pill> : <Pill tone="gray">Inactive</Pill>) },
              { key: 'l', header: 'SharePoint link', render: (r) => <LinkCell link={r.link} onTest={() => test.mutate(r.id)} testing={test.isPending && test.variables === r.id} name={r.name} /> },
              { key: 'e', header: '', className: 'num', render: (r) => <Button variant="ghost" size="sm" iconOnly icon="edit" aria-label={`Edit ${r.name}`} onClick={(e) => { e.stopPropagation(); setEditing(r); }} /> },
            ]}
          />
        )}
      </Card>
      {editing ? <EntityDialog item={editing === 'new' ? null : editing} currencies={currencies.data ?? []} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

export function CurrenciesAdmin() {
  const api = useApi();
  const toast = useToast();
  const refresh = useRefreshReference();
  const q = useQuery({ queryKey: qk.admin('currencies'), queryFn: () => api.admin.getCurrencies() });
  const [rows, setRows] = useState<Currency[] | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  useEffect(() => {
    if (q.data && !rows) setRows(q.data);
  }, [q.data, rows]);
  const save = useMutation({
    mutationFn: (r: Currency[]) => api.admin.updateCurrencies(r),
    onSuccess: async (r) => {
      setRows(r);
      setErrors({});
      await refresh();
      toast.show('Currencies saved');
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  const set = (i: number, patch: Partial<Currency>) => setRows((rs) => rs?.map((r, j) => (j === i ? { ...r, ...patch } : r)) ?? rs);
  return (
    <>
      <SectionHead title="Currencies" text="Currencies a contract can be in, and the rate to DKK used for every DKK figure: Overview, value bands and the approval route." action={<Button loading={save.isPending} disabled={!rows} onClick={() => rows && save.mutate(rows)}>Save changes</Button>} />
      <Card clip>
        {q.isError && !rows ? (
          <ErrorState text="The currencies could not be loaded." onRetry={() => void q.refetch()} />
        ) : !rows ? (
          <SkeletonRows rows={6} />
        ) : (
          <div className="table-wrap">
            <table className="table">
              <caption className="sr-only">Currencies</caption>
              <thead>
                <tr>
                  <th scope="col">Code</th>
                  <th scope="col">Name</th>
                  <th scope="col">DKK per unit</th>
                  <th scope="col">Active</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={i}>
                    <td>
                      <input className={`input input--sm${errors[`${i}.code`] ? ' input--invalid' : ''}`} aria-label="Code" value={r.code} maxLength={3} disabled={r.code === 'DKK' && i < (q.data?.length ?? 0)} onChange={(e) => set(i, { code: e.target.value.toUpperCase() })} style={{ width: 80 }} />
                    </td>
                    <td>
                      <input className="input input--sm" aria-label={`Name of ${r.code}`} value={r.name} onChange={(e) => set(i, { name: e.target.value })} />
                    </td>
                    <td>
                      <input className={`input input--sm${errors[`${i}.rateToDkk`] ? ' input--invalid' : ''}`} aria-label={`Rate of ${r.code}`} inputMode="decimal" value={String(r.rateToDkk)} disabled={r.code === 'DKK'} onChange={(e) => set(i, { rateToDkk: Number(e.target.value.replace(',', '.')) || 0 })} style={{ width: 110 }} />
                    </td>
                    <td>
                      <input type="checkbox" className="check__box" aria-label={`${r.code} active`} checked={r.isActive} disabled={r.code === 'DKK'} onChange={(e) => set(i, { isActive: e.target.checked })} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <div className="pager">
          <Button variant="secondary" size="sm" icon="plus" onClick={() => setRows((rs) => [...(rs ?? []), { code: '', name: '', rateToDkk: 1, isActive: true }])}>
            Add currency
          </Button>
          {Object.values(errors)[0] ? <span className="field__error">{Object.values(errors)[0]}</span> : null}
        </div>
      </Card>
    </>
  );
}

const COLORS: { value: TagColor; label: string }[] = [
  { value: 'gray', label: 'Gray' },
  { value: 'blue', label: 'Blue' },
  { value: 'green', label: 'Green' },
  { value: 'amber', label: 'Amber' },
  { value: 'red', label: 'Red' },
  { value: 'purple', label: 'Purple' },
];

export function TagsAdmin() {
  const api = useApi();
  const toast = useToast();
  const refresh = useRefreshReference();
  const q = useQuery({ queryKey: qk.admin('tags'), queryFn: () => api.admin.getTags() });
  const [editing, setEditing] = useState<Tag | 'new' | null>(null);
  const [deleting, setDeleting] = useState<Tag | null>(null);
  const [d, setD] = useState<TagInput>({ name: '', color: 'blue', description: null });
  const [error, setError] = useState<string>();
  const save = useMutation({
    mutationFn: () => (editing && editing !== 'new' ? api.admin.updateTag(editing.id, d) : api.admin.createTag(d)),
    onSuccess: async () => {
      await refresh();
      setEditing(null);
      toast.show('Tag saved');
    },
    onError: (e) => {
      setError(e instanceof Error ? e.message : undefined);
      toast.error(e);
    },
  });
  const remove = useMutation({
    mutationFn: (t: Tag) => api.admin.deleteTag(t.id),
    onSuccess: async () => {
      await refresh();
      setDeleting(null);
      toast.show('Tag deleted');
    },
    onError: (e) => toast.error(e),
  });
  const open = (t: Tag | 'new') => {
    setD(t === 'new' ? { name: '', color: 'blue', description: null } : { name: t.name, color: t.color, description: t.description });
    setError(undefined);
    setEditing(t);
  };
  return (
    <>
      <SectionHead title="Tags" text="Labels people can put on contracts, for example for a review or a due diligence. Tags can be filtered on in the register and in Overview." action={<Button icon="plus" onClick={() => open('new')}>Add tag</Button>} />
      <Card clip>
        {q.isLoading ? (
          <SkeletonRows rows={4} />
        ) : q.isError ? (
          <ErrorState text="The tags could not be loaded." onRetry={() => void q.refetch()} />
        ) : (q.data ?? []).length === 0 ? (
          <EmptyState icon="tag" title="No tags yet" />
        ) : (
          <Table<Tag>
            caption="Tags"
            rows={q.data ?? []}
            rowKey={(r) => r.id}
            columns={[
              { key: 'n', header: 'Tag', render: (r) => <span className={`tag tag--${r.color}`}>{r.name}</span> },
              { key: 'd', header: 'Description', render: (r) => <span className="secondary">{r.description ?? '–'}</span> },
              { key: 'u', header: 'Used on', className: 'num', render: (r) => plural(r.usageCount, 'contract') },
              {
                key: 'a',
                header: '',
                className: 'num',
                render: (r) => (
                  <span className="row-flex" style={{ gap: 4, justifyContent: 'flex-end' }}>
                    <Button variant="ghost" size="sm" iconOnly icon="edit" aria-label={`Edit ${r.name}`} onClick={() => open(r)} />
                    <Button variant="ghost" size="sm" iconOnly icon="trash" aria-label={`Delete ${r.name}`} onClick={() => setDeleting(r)} />
                  </span>
                ),
              },
            ]}
          />
        )}
      </Card>
      {editing ? (
        <Modal
          title={editing === 'new' ? 'Add tag' : 'Edit tag'}
          onClose={() => setEditing(null)}
          onSubmit={() => (d.name.trim() ? save.mutate() : setError('Enter a name'))}
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
          <div className="stack" style={{ gap: 14 }}>
            <Field label="Name" error={error}>
              {(p) => <TextInput {...p} maxLength={40} value={d.name} onChange={(e) => setD({ ...d, name: e.target.value })} />}
            </Field>
            <Field label="Colour">{(p) => <Select {...p} value={d.color} options={COLORS} onChange={(e) => setD({ ...d, color: e.target.value as TagColor })} />}</Field>
            <Field label="Description" optional>
              {(p) => <TextInput {...p} value={d.description ?? ''} onChange={(e) => setD({ ...d, description: e.target.value || null })} />}
            </Field>
            <span>
              Preview: <span className={`tag tag--${d.color}`}>{d.name || 'Tag'}</span>
            </span>
          </div>
        </Modal>
      ) : null}
      {deleting ? <ConfirmDelete title={`Delete "${deleting.name}"?`} text={`The tag is removed from ${plural(deleting.usageCount, 'contract')}.`} pending={remove.isPending} onConfirm={() => remove.mutate(deleting)} onClose={() => setDeleting(null)} /> : null}
    </>
  );
}

/** "Test link" for an entity or department: shows the result and refreshes the list. */
export function useLinkTest(run: (id: string) => Promise<LinkTestResult>, key: readonly unknown[]) {
  const client = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: run,
    onSuccess: async (r) => {
      await client.invalidateQueries({ queryKey: key });
      await client.invalidateQueries({ queryKey: qk.admin('access-mapping') });
      if (r.link.state === 'Linked') toast.show(`Linked. ${r.details.join(' ')}`);
      else toast.error(null, `Not linked: ${r.details.join(' ')}`);
    },
    onError: (e) => toast.error(e),
  });
}

export function LinkCell({ link, onTest, testing, name }: { link?: LinkStatus; onTest: () => void; testing: boolean; name: string }) {
  const state = link?.state ?? 'NotLinked';
  return (
    <span className="link-cell" onClick={(e) => e.stopPropagation()}>
      <Pill tone={state === 'Linked' ? 'green' : state === 'Error' ? 'red' : 'gray'} small>
        {state === 'Linked' ? 'Linked' : state === 'Error' ? 'Error' : 'Not linked'}
      </Pill>
      <span className="caption" title={link?.message ?? ''}>
        {link?.checkedAt ? `checked ${formatDateTime(link.checkedAt)}` : 'never checked'}
        {link?.message ? ` · ${link.message}` : ''}
      </span>
      <Button variant="ghost" size="sm" loading={testing} onClick={onTest} aria-label={`Test link of ${name}`}>
        Test link
      </Button>
    </span>
  );
}
