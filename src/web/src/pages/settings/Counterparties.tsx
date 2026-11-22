import { useState } from 'react';
import { keepPreviousData, useMutation, useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { qk, useApi } from '../../app/context';
import type { Counterparty, CounterpartyInput, CounterpartyKind, DuplicateGroup } from '../../api/types';
import { Button, ButtonLink, Card, ChipSelect, EmptyState, ErrorState, Field, Icon, Modal, Pill, Select, SkeletonRows, Table, TextInput, useToast } from '../../components';
import { formatDate } from '../../lib/dates';
import { plural } from '../../lib/format';
import { SectionHead } from './common';
import { problemErrors, useRefreshReference } from './Reference';

const KINDS: CounterpartyKind[] = ['Supplier', 'Customer', 'Partner', 'Authority', 'Other'];
const SOURCE: Record<string, string> = { Manual: 'Added here', Import: 'File import', PowerBi: 'Power BI sync', Sql: 'SQL sync', Entra: 'Entra ID', Seed: 'Initial data' };

function CounterpartyDialog({ item, onClose }: { item: Counterparty | null; onClose: () => void }) {
  const api = useApi();
  const toast = useToast();
  const refresh = useRefreshReference();
  const [d, setD] = useState<CounterpartyInput>(
    item
      ? { name: item.name, country: item.country, vatNo: item.vatNo, kind: item.kind, externalId: item.externalId, city: item.city, email: item.email, phone: item.phone, isActive: item.isActive }
      : { name: '', country: 'DK', vatNo: null, kind: 'Supplier', externalId: null, city: null, email: null, phone: null, isActive: true },
  );
  const [errors, setErrors] = useState<Record<string, string>>({});
  const save = useMutation({
    mutationFn: () => (item ? api.admin.updateCounterparty(item.id, d) : api.admin.createCounterparty(d)),
    onSuccess: async () => {
      await refresh();
      toast.show(item ? 'Counterparty saved' : 'Counterparty added');
      onClose();
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  const text = (key: keyof CounterpartyInput, label: string, opt = true, hint?: string) => (
    <Field label={label} optional={opt} error={errors[key]} hint={hint}>
      {(p) => <TextInput {...p} value={(d[key] as string | null) ?? ''} onChange={(e) => setD({ ...d, [key]: e.target.value || (key === 'name' ? '' : null) })} />}
    </Field>
  );
  return (
    <Modal
      wide
      title={item ? `Edit ${item.name}` : 'Add counterparty'}
      subtitle={item ? `${plural(item.contractCount, 'contract')} · source: ${SOURCE[item.source] ?? item.source}. Renaming also renames it on the contracts.` : undefined}
      onClose={onClose}
      onSubmit={() => (d.name.trim() ? save.mutate() : setErrors({ name: 'Enter the name' }))}
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
        <div className="form-grid__full">{text('name', 'Name', false)}</div>
        <Field label="Kind">{(p) => <Select {...p} value={d.kind} options={KINDS.map((k) => ({ value: k, label: k }))} onChange={(e) => setD({ ...d, kind: e.target.value as CounterpartyKind })} />}</Field>
        {text('country', 'Country', true, 'Two letters, e.g. DK')}
        {text('vatNo', 'VAT number')}
        {text('externalId', 'External id', true, 'Vendor number in the ERP system')}
        {text('city', 'City')}
        {text('email', 'E-mail')}
        {text('phone', 'Phone')}
        <label className="check form-grid__full">
          <input type="checkbox" checked={d.isActive} onChange={(e) => setD({ ...d, isActive: e.target.checked })} />
          Active. Inactive counterparties keep their contracts but are not offered for new ones.
        </label>
      </div>
    </Modal>
  );
}

function MergeDialog({ onClose }: { onClose: () => void }) {
  const api = useApi();
  const toast = useToast();
  const refresh = useRefreshReference();
  const q = useQuery({ queryKey: qk.admin('duplicates'), queryFn: () => api.admin.getCounterpartyDuplicates() });
  const [keep, setKeep] = useState<Record<number, string>>({});
  const merge = useMutation({
    mutationFn: ({ g, target }: { g: DuplicateGroup; target: string }) => api.admin.mergeCounterparties({ targetId: target, sourceIds: g.items.map((i) => i.id).filter((id) => id !== target) }),
    onSuccess: async (r) => {
      await Promise.all([refresh(), q.refetch()]);
      toast.show(`Merged into ${r.target.name}: ${plural(r.movedContracts, 'contract')} moved, ${plural(r.deactivated, 'record')} deactivated`);
    },
    onError: (e) => toast.error(e),
  });
  const reason = { VatNo: 'Same VAT number', ExternalId: 'Same external id', Name: 'Similar name' };
  return (
    <Modal wide title="Possible duplicates" subtitle="Choose the record to keep. The contracts of the others move to it, and the others are deactivated." onClose={onClose} footer={<Button onClick={onClose}>Close</Button>}>
      {q.isLoading ? (
        <SkeletonRows rows={4} />
      ) : (q.data ?? []).length === 0 ? (
        <EmptyState compact icon="check" title="No duplicates found" />
      ) : (
        <div className="stack" style={{ gap: 14 }}>
          {q.data?.map((g, i) => {
            const target = keep[i] ?? [...g.items].sort((a, b) => b.contractCount - a.contractCount)[0]?.id ?? '';
            return (
              <fieldset key={i} className="dup-group">
                <legend>{reason[g.reason]}</legend>
                {g.items.map((it) => (
                  <label key={it.id} className="check dup-group__row">
                    <input type="radio" name={`dup-${i}`} checked={target === it.id} onChange={() => setKeep({ ...keep, [i]: it.id })} />
                    <span>
                      <strong>{it.name}</strong>
                      <span className="caption">
                        {' '}
                        · {[it.vatNo, it.externalId, it.country].filter(Boolean).join(' · ') || 'no ids'} · {plural(it.contractCount, 'contract')} · {SOURCE[it.source] ?? it.source}
                      </span>
                    </span>
                  </label>
                ))}
                <Button size="sm" loading={merge.isPending && merge.variables?.g === g} onClick={() => merge.mutate({ g, target })}>
                  Merge into the chosen record
                </Button>
              </fieldset>
            );
          })}
        </div>
      )}
    </Modal>
  );
}

export function Counterparties() {
  const api = useApi();
  const [q, setQ] = useState('');
  const [active, setActive] = useState('');
  const [kind, setKind] = useState('');
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<Counterparty | 'new' | null>(null);
  const [merging, setMerging] = useState(false);
  const query = { q: q.trim() || undefined, active: active === '' ? undefined : active === 'true', kind: (kind || undefined) as CounterpartyKind | undefined, page, pageSize: 25 };
  const list = useQuery({ queryKey: qk.admin('counterparties', query), queryFn: () => api.admin.getCounterparties(query), placeholderData: keepPreviousData });
  const total = list.data?.total ?? 0;
  return (
    <>
      <SectionHead
        title="Suppliers and counterparties"
        text="The master data contracts point to. Keep one record per company: merge duplicates, deactivate instead of deleting. Records can also come from a file import or a sync source."
        action={
          <div className="row-flex" style={{ gap: 8 }}>
            <Button variant="secondary" icon="users" onClick={() => setMerging(true)}>
              Find duplicates
            </Button>
            <ButtonLink variant="secondary" icon="upload" to="/settings/imports?kind=counterparties">
              Import
            </ButtonLink>
            <Button icon="plus" onClick={() => setEditing('new')}>
              Add
            </Button>
          </div>
        }
      />
      <div className="toolbar">
        <label className="search-pill">
          <span className="sr-only">Search counterparties</span>
          <Icon name="search" size={15} stroke={2.2} />
          <input type="search" placeholder="Name, VAT, external id, city" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} />
        </label>
        <ChipSelect label="Status" options={[{ value: 'true', label: 'Active' }, { value: 'false', label: 'Inactive' }]} value={active} onChange={(v) => { setActive(v); setPage(1); }} />
        <ChipSelect label="Kind" options={KINDS.map((k) => ({ value: k, label: k }))} value={kind} onChange={(v) => { setKind(v); setPage(1); }} />
        <div className="spacer" />
        <Link to="/settings/sync">Sync sources</Link>
      </div>
      <Card clip>
        {list.isLoading ? (
          <SkeletonRows rows={8} />
        ) : list.isError ? (
          <ErrorState onRetry={() => void list.refetch()} />
        ) : (list.data?.items.length ?? 0) === 0 ? (
          <EmptyState compact title="No counterparties match" />
        ) : (
          <>
            <Table<Counterparty>
              caption="Counterparties" minWidth={760}
              fixed
              rows={list.data?.items ?? []}
              rowKey={(r) => r.id}
              onRowClick={(r) => setEditing(r)}
              columns={[
                { key: 'n', header: 'Name', width: '28%', render: (r) => <span className="cell-clip" title={r.name}><strong>{r.name}</strong></span> },
                { key: 'k', header: 'Kind', width: '10%', render: (r) => r.kind },
                { key: 'c', header: 'Country', width: '8%', render: (r) => r.country ?? '–' },
                { key: 'v', header: 'VAT', width: '13%', render: (r) => <span className="mono">{r.vatNo ?? '–'}</span> },
                { key: 'x', header: 'External id', width: '11%', render: (r) => <span className="mono">{r.externalId ?? '–'}</span> },
                { key: 'cc', header: 'Contracts', width: '9%', className: 'num', render: (r) => (r.contractCount ? <Link to={`/counterparties/${r.id}`} onClick={(e) => e.stopPropagation()}>{r.contractCount}</Link> : '0') },
                { key: 's', header: 'Source', width: '11%', render: (r) => <span className="caption">{SOURCE[r.source] ?? r.source}</span> },
                { key: 'u', header: 'Updated', width: '112px', render: (r) => formatDate(r.updatedAt.slice(0, 10)) },
                { key: 'a', header: 'Status', render: (r) => (r.isActive ? <Pill tone="green">Active</Pill> : <Pill tone="gray">Inactive</Pill>) },
              ]}
            />
            <div className="pager">
              <span>
                {(page - 1) * 25 + 1}–{Math.min(page * 25, total)} of {total}
              </span>
              <div className="spacer" />
              <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => setPage(page - 1)}>
                Previous
              </Button>
              <Button variant="secondary" size="sm" disabled={page * 25 >= total} onClick={() => setPage(page + 1)}>
                Next
              </Button>
            </div>
          </>
        )}
      </Card>
      {editing ? <CounterpartyDialog item={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
      {merging ? <MergeDialog onClose={() => setMerging(false)} /> : null}
    </>
  );
}
