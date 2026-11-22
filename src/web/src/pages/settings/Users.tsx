import { useState } from 'react';
import { keepPreviousData, useMutation, useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { qk, useApi, useInvalidateContracts, useLookups, useMe } from '../../app/context';
import type { AccessRule, AdminUser, AdminUserInput, ContractRole, ReassignInput, Role } from '../../api/types';
import { Avatar, Button, ButtonLink, Card, ChipSelect, EmptyState, ErrorState, Field, Icon, Modal, Pill, Segmented, Select, SkeletonRows, Table, TextInput, useToast } from '../../components';
import { formatDateTime } from '../../lib/dates';
import { plural } from '../../lib/format';
import { SectionHead } from './common';
import { problemErrors, useRefreshReference } from './Reference';
import { PersonPicker } from '../../features/Pickers';

const ROLES: { value: Role; label: string; hint: string }[] = [
  { value: 'Reader', label: 'Reader', hint: 'Sees contracts through the access rules' },
  { value: 'ContractOwner', label: 'Contract owner', hint: 'Creates and edits own contracts' },
  { value: 'Approver', label: 'Approver', hint: 'Can be chosen as an approver' },
  { value: 'Admin', label: 'Administrator', hint: 'Settings and every contract' },
];
const RULE_LABEL: Record<AccessRule, string> = { Department: 'Department', EntityManagement: 'Entity management', GroupManagement: 'Group management', OnContract: 'On the contract', Admin: 'Administrator', CurrentApprover: 'Current approver' };
const SOURCE: Record<string, string> = { Entra: 'Entra ID', Manual: 'Added here', Import: 'Imported', Seed: 'Demo data', PowerBi: 'Sync', Sql: 'Sync' };

function UserDialog({ user, onClose }: { user: AdminUser | null; onClose: () => void }) {
  const api = useApi();
  const me = useMe();
  const lookups = useLookups();
  const toast = useToast();
  const refresh = useRefreshReference();
  const [d, setD] = useState<AdminUserInput>(
    user
      ? { name: user.name, email: user.email, title: user.title, roles: user.roles, departmentIds: user.departmentIds, managedEntityIds: user.managedEntityIds, isGroupManagement: user.isGroupManagement, isActive: user.isActive, entityId: user.entityId ?? null, canSendForSignature: user.canSendForSignature ?? false, signatureEntityIds: user.signatureEntityIds ?? [] }
      : { name: '', email: '', title: null, roles: ['Reader'], departmentIds: [], managedEntityIds: [], isGroupManagement: false, isActive: true, entityId: null, canSendForSignature: false, signatureEntityIds: [] },
  );
  const [errors, setErrors] = useState<Record<string, string>>({});
  const synced = (f: string) => user?.syncedFields.includes(f) ?? false;
  const save = useMutation({
    mutationFn: () => (user ? api.admin.updateUser(user.id, d) : api.admin.createUser(d)),
    onSuccess: async () => {
      await refresh();
      toast.show('User saved');
      onClose();
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  const toggle = <T extends string>(list: T[], v: T, on: boolean) => (on ? [...list, v] : list.filter((x) => x !== v));
  const self = user?.id === me.id;
  const syncedTag = <Pill tone="blue" small>From Entra ID</Pill>;
  return (
    <Modal
      wide
      title={user ? user.name : 'Add user'}
      subtitle={user ? `${SOURCE[user.source] ?? user.source}${user.lastSignInAt ? ` · last signed in ${formatDateTime(user.lastSignInAt)}` : ''}${user.syncedFields.length ? '. Fields from Entra ID are read-only here; roles and memberships are managed in DAFA Contracts.' : ''}` : 'With Entra ID sign-in the account is linked by e-mail at the first sign-in.'}
      onClose={onClose}
      onSubmit={() => save.mutate()}
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
        <Field label="Name" error={errors.name} tag={synced('name') ? syncedTag : undefined}>
          {(p) => <TextInput {...p} value={d.name} disabled={synced('name')} onChange={(e) => setD({ ...d, name: e.target.value })} />}
        </Field>
        <Field label="E-mail" error={errors.email} tag={synced('email') ? syncedTag : undefined}>
          {(p) => <TextInput {...p} type="email" value={d.email} disabled={synced('email')} onChange={(e) => setD({ ...d, email: e.target.value })} />}
        </Field>
        <Field label="Job title" optional tag={synced('title') ? syncedTag : undefined}>
          {(p) => <TextInput {...p} value={d.title ?? ''} disabled={synced('title')} onChange={(e) => setD({ ...d, title: e.target.value || null })} />}
        </Field>
        <label className="check" style={{ alignSelf: 'end', minHeight: 44 }}>
          <input type="checkbox" checked={d.isActive} disabled={synced('isActive') || self} onChange={(e) => setD({ ...d, isActive: e.target.checked })} />
          Active{synced('isActive') ? ' (from Entra ID)' : ''}
        </label>
        <fieldset className="fieldset-plain form-grid__full">
          <legend className="field__label">Roles</legend>
          {errors.roles ? <span className="field__error">{errors.roles}</span> : null}
          <div className="check-row">
            {ROLES.map((r) => (
              <label key={r.value} className="check" title={r.hint}>
                <input type="checkbox" checked={d.roles.includes(r.value)} disabled={self && r.value === 'Admin'} onChange={(e) => setD({ ...d, roles: toggle(d.roles, r.value, e.target.checked) })} />
                {r.label}
              </label>
            ))}
          </div>
        </fieldset>
        <fieldset className="fieldset-plain form-grid__full">
          <legend className="field__label">Departments (sees their contracts in every legal entity)</legend>
          <div className="check-row">
            {lookups.departments.map((x) => (
              <label key={x.id} className="check">
                <input type="checkbox" checked={d.departmentIds.includes(x.id)} onChange={(e) => setD({ ...d, departmentIds: toggle(d.departmentIds, x.id, e.target.checked) })} />
                {x.name}
              </label>
            ))}
          </div>
        </fieldset>
        <fieldset className="fieldset-plain form-grid__full">
          <legend className="field__label">Entity management (sees every contract of these legal entities)</legend>
          <div className="check-row">
            {lookups.entities.map((x) => (
              <label key={x.id} className="check">
                <input type="checkbox" checked={d.managedEntityIds.includes(x.id)} onChange={(e) => setD({ ...d, managedEntityIds: toggle(d.managedEntityIds, x.id, e.target.checked) })} />
                {x.name}
              </label>
            ))}
          </div>
        </fieldset>
        <label className="check form-grid__full">
          <input type="checkbox" checked={d.isGroupManagement} onChange={(e) => setD({ ...d, isGroupManagement: e.target.checked })} />
          Group management (sees all contracts)
        </label>
        <Field label="Home legal entity" optional hint="Shown next to the name in person pickers." className="form-grid__full">
          {(p) => <Select {...p} value={d.entityId ?? ''} placeholder="Not set" options={lookups.entities.filter((x) => x.isActive !== false || x.id === d.entityId).map((x) => ({ value: x.id, label: x.name }))} onChange={(e) => setD({ ...d, entityId: e.target.value || null })} />}
        </Field>
        <fieldset className="fieldset-plain form-grid__full">
          <legend className="field__label">E-signature</legend>
          <label className="check">
            <input type="checkbox" checked={!!d.canSendForSignature} onChange={(e) => setD({ ...d, canSendForSignature: e.target.checked })} />
            Can send contracts for signature outside DAFA
          </label>
          {d.canSendForSignature ? (
            <>
              <span className="field__hint">Only for these legal entities (none ticked = every entity the person can see):</span>
              <div className="check-row">
                {lookups.entities.map((x) => (
                  <label key={x.id} className="check">
                    <input type="checkbox" checked={(d.signatureEntityIds ?? []).includes(x.id)} onChange={(e) => setD({ ...d, signatureEntityIds: toggle(d.signatureEntityIds ?? [], x.id, e.target.checked) })} />
                    {x.name}
                  </label>
                ))}
              </div>
            </>
          ) : null}
        </fieldset>
      </div>
    </Modal>
  );
}

function EffectiveAccessDialog({ user, onClose }: { user: AdminUser; onClose: () => void }) {
  const api = useApi();
  const [q, setQ] = useState('');
  const [page, setPage] = useState(1);
  const access = useQuery({ queryKey: qk.admin('effective', user.id, q, page), queryFn: () => api.admin.getEffectiveAccess(user.id, { q: q || undefined, page, pageSize: 20 }), placeholderData: keepPreviousData });
  const d = access.data;
  return (
    <Modal wide title={`What ${user.name} can see`} subtitle="Every contract the access rules give this person, and the rule (or rules) that give it." onClose={onClose} footer={<Button onClick={onClose}>Close</Button>}>
      {!d ? (
        <SkeletonRows rows={5} />
      ) : (
        <div className="stack" style={{ gap: 14 }}>
          <div className="row-flex" style={{ gap: 8, flexWrap: 'wrap' }}>
            <Pill tone="blue">{plural(d.total, 'contract')}</Pill>
            {d.rules.map((r) => (
              <Pill key={r.rule} tone="gray">
                {RULE_LABEL[r.rule]}: {r.count}
              </Pill>
            ))}
            {!user.isActive ? <Pill tone="amber">Inactive: sees nothing</Pill> : null}
          </div>
          <label className="search-pill" style={{ maxWidth: 360 }}>
            <span className="sr-only">Filter contracts</span>
            <Icon name="search" size={15} stroke={2.2} />
            <input type="search" placeholder="Filter by title or counterparty" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} />
          </label>
          {d.contracts.length === 0 ? (
            <EmptyState compact title="No contracts" />
          ) : (
            <Table
              caption="Visible contracts"
              rows={d.contracts}
              rowKey={(r) => r.item.id}
              columns={[
                { key: 't', header: 'Contract', render: (r) => <Link to={`/contracts/${r.item.id}`}>{r.item.title}</Link> },
                { key: 'e', header: 'Entity', render: (r) => r.item.entityName },
                { key: 'd', header: 'Department', render: (r) => r.item.departmentName },
                { key: 'w', header: 'Why', render: (r) => <span className="tag-list">{r.reasons.map((x, i) => <span key={i} className="tag tag--gray">{x.label}</span>)}</span> },
              ]}
            />
          )}
          {d.total > 20 && !q ? (
            <div className="pager">
              <span>
                {(page - 1) * 20 + 1}–{Math.min(page * 20, d.total)} of {d.total}
              </span>
              <div className="spacer" />
              <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => setPage(page - 1)}>
                Previous
              </Button>
              <Button variant="secondary" size="sm" disabled={page * 20 >= d.total} onClick={() => setPage(page + 1)}>
                Next
              </Button>
            </div>
          ) : null}
        </div>
      )}
    </Modal>
  );
}

function ReassignDialog({ from, onClose }: { from: AdminUser | null; onClose: () => void }) {
  const api = useApi();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const [input, setInput] = useState<ReassignInput>({ fromUserId: from?.id ?? '', toUserId: '', roles: ['Owner', 'Delegate', 'Responsible'], contractIds: null, includeEnded: false, includeObligations: true });
  const ready = !!input.fromUserId && !!input.toUserId && input.roles.length > 0;
  const preview = useQuery({ queryKey: qk.admin('reassign', input), queryFn: () => api.admin.previewReassign(input), enabled: ready, retry: false });
  const run = useMutation({
    mutationFn: () => api.admin.reassign(input),
    onSuccess: async (r) => {
      await invalidate();
      toast.show(`${plural(r.updated, 'contract')} and ${plural(r.obligationsMoved, 'obligation')} moved${r.skipped.length ? `, ${r.skipped.length} skipped` : ''}`);
      onClose();
    },
    onError: (e) => toast.error(e),
  });
  const toggleRole = (r: ContractRole, on: boolean) => setInput({ ...input, roles: on ? [...input.roles, r] : input.roles.filter((x) => x !== r) });
  return (
    <Modal
      wide
      title="Reassign contracts"
      subtitle="When someone leaves or changes job: move their roles on contracts to someone else. Check the list before you move anything."
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button disabled={!ready || !preview.data?.contracts} loading={run.isPending} onClick={() => run.mutate()}>
            Move {preview.data ? plural(preview.data.contracts, 'contract') : ''}
          </Button>
        </>
      }
    >
      <div className="stack" style={{ gap: 14 }}>
        <div className="form-grid form-grid--flat">
          <Field label="From" hint="Inactive people are listed too">{(p) => <PersonPicker {...p} includeInactive value={input.fromUserId || null} clearLabel="Clear from" onChange={(id) => setInput({ ...input, fromUserId: id ?? '' })} />}</Field>
          <Field label="To">{(p) => <PersonPicker {...p} value={input.toUserId || null} filter={(u) => u.id !== input.fromUserId} clearLabel="Clear to" onChange={(id) => setInput({ ...input, toUserId: id ?? '' })} />}</Field>
        </div>
        <div className="check-row">
          {(['Owner', 'Delegate', 'Responsible'] as const).map((r) => (
            <label key={r} className="check">
              <input type="checkbox" checked={input.roles.includes(r)} onChange={(e) => toggleRole(r, e.target.checked)} />
              As {r.toLowerCase()}
            </label>
          ))}
          <label className="check">
            <input type="checkbox" checked={input.includeObligations} onChange={(e) => setInput({ ...input, includeObligations: e.target.checked })} />
            Open obligations too
          </label>
          <label className="check">
            <input type="checkbox" checked={input.includeEnded} onChange={(e) => setInput({ ...input, includeEnded: e.target.checked })} />
            Ended contracts too
          </label>
        </div>
        {!ready ? (
          <p className="secondary">Choose who to move from and to.</p>
        ) : preview.isError ? (
          <p className="field__error">{preview.error instanceof Error ? preview.error.message : 'The preview failed'}</p>
        ) : !preview.data ? (
          <SkeletonRows rows={4} />
        ) : preview.data.rows.length === 0 ? (
          <EmptyState compact title="No contracts to move" />
        ) : (
          <>
            <p className="secondary">
              {plural(preview.data.contracts, 'contract')} and {plural(preview.data.obligations, 'open obligation')} will move.
            </p>
            <Table
              caption="Contracts to reassign"
              rows={preview.data.rows}
              rowKey={(r) => r.contractId}
              columns={[
                { key: 'n', header: 'Contract', render: (r) => `${r.title} (${r.contractNo})` },
                { key: 'r', header: 'Roles', render: (r) => r.roles.join(', ') },
                { key: 's', header: 'Result', render: (r) => (r.skipReason ? <Pill tone="amber">Skipped: {r.skipReason}</Pill> : <Pill tone="green">Moves</Pill>) },
              ]}
            />
          </>
        )}
      </div>
    </Modal>
  );
}

export function UsersAdmin() {
  const api = useApi();
  const lookups = useLookups();
  const [q, setQ] = useState('');
  const [active, setActive] = useState('true');
  const users = useQuery({ queryKey: qk.admin('users', q, active), queryFn: () => api.admin.getUsers({ q: q || undefined, active: active === '' ? undefined : active === 'true' }), placeholderData: keepPreviousData });
  const [editing, setEditing] = useState<AdminUser | 'new' | null>(null);
  const [access, setAccess] = useState<AdminUser | null>(null);
  const [reassign, setReassign] = useState<AdminUser | null | 'open'>(null);
  const [tab, setTab] = useState<'list' | 'check'>('list');
  const [checkId, setCheckId] = useState('');
  const names = (ids: string[], list: { id: string; name: string }[]) => ids.map((id) => list.find((x) => x.id === id)?.name).filter(Boolean).join(', ');
  return (
    <>
      <SectionHead
        title="Users and access"
        text="Roles and memberships decide what each person sees: their departments in every entity, the entities they manage, everything for group management, and the contracts they are on."
        action={
          <div className="row-flex" style={{ gap: 8 }}>
            <Button variant="secondary" icon="users" onClick={() => setReassign('open')}>
              Reassign contracts
            </Button>
            <ButtonLink variant="secondary" icon="upload" to="/settings/imports?kind=users">
              Import
            </ButtonLink>
            <Button icon="plus" onClick={() => setEditing('new')}>
              Add user
            </Button>
          </div>
        }
      />
      <Segmented ariaLabel="Users" options={[{ value: 'list', label: 'Users' }, { value: 'check', label: 'Effective access' }]} value={tab} onChange={setTab} />
      {tab === 'check' ? (
        <Card pad className="stack" style={{ gap: 12 }}>
          <p className="secondary prose">Pick a person to see which contracts they can see, and why.</p>
          <div className="row-flex" style={{ gap: 10, alignItems: 'flex-end' }}>
            <Field label="Person">{(p) => <PersonPicker {...p} includeInactive value={checkId || null} clearLabel="Clear person" onChange={(id) => setCheckId(id ?? '')} />}</Field>
            <Button disabled={!checkId} onClick={() => { const u = users.data?.find((x) => x.id === checkId); if (u) setAccess(u); else void api.admin.getUsers({}).then((all) => { const hit = all.find((x) => x.id === checkId); if (hit) setAccess(hit); }); }}>
              Check access
            </Button>
          </div>
        </Card>
      ) : (
        <>
          <div className="toolbar">
            <label className="search-pill">
              <span className="sr-only">Search users</span>
              <Icon name="search" size={15} stroke={2.2} />
              <input type="search" placeholder="Name, e-mail or title" value={q} onChange={(e) => setQ(e.target.value)} />
            </label>
            <ChipSelect label="Status" options={[{ value: 'true', label: 'Active' }, { value: 'false', label: 'Inactive' }]} value={active} onChange={setActive} />
          </div>
          <Card clip>
            {users.isLoading ? (
              <SkeletonRows rows={8} />
            ) : users.isError ? (
              <ErrorState onRetry={() => void users.refetch()} />
            ) : (
              <Table<AdminUser>
                caption="Users" minWidth={760}
                fixed
                rows={users.data ?? []}
                rowKey={(r) => r.id}
                onRowClick={(r) => setEditing(r)}
                columns={[
                  { key: 'n', header: 'Person', width: '24%', render: (r) => <span className="person-link"><Avatar initials={r.initials} size="sm" /><span className="stack" style={{ gap: 0, minWidth: 0 }}><strong className="cell-clip">{r.name}</strong><span className="caption cell-clip">{r.email}</span></span></span> },
                  { key: 'r', header: 'Roles', width: '16%', render: (r) => <span className="cell-clip">{r.roles.map((x) => ROLES.find((y) => y.value === x)?.label ?? x).join(', ')}</span> },
                  { key: 'a', header: 'Sees', width: '28%', render: (r) => <span className="cell-clip" title={[r.isGroupManagement ? 'Group management' : '', names(r.managedEntityIds, lookups.entities), names(r.departmentIds, lookups.departments)].filter(Boolean).join(' · ')}>{r.isGroupManagement ? 'Everything (group management)' : [names(r.managedEntityIds, lookups.entities), names(r.departmentIds, lookups.departments)].filter(Boolean).join(' · ') || 'Own contracts'}</span> },
                  { key: 'c', header: 'Contracts', width: '9%', className: 'num', render: (r) => <Link to={`/people/${r.id}`} onClick={(e) => e.stopPropagation()}>{r.contractCount}</Link> },
                  { key: 's', header: 'Source', width: '10%', render: (r) => <span className="caption">{SOURCE[r.source] ?? r.source}</span> },
                  {
                    key: 'x',
                    header: '',
                    className: 'num',
                    render: (r) => (
                      <span className="row-flex" style={{ gap: 4, justifyContent: 'flex-end' }}>
                        {!r.isActive ? <Pill tone="gray" small>Inactive</Pill> : null}
                        <Button variant="ghost" size="sm" iconOnly icon="eye" aria-label={`What ${r.name} can see`} onClick={(e) => { e.stopPropagation(); setAccess(r); }} />
                      </span>
                    ),
                  },
                ]}
              />
            )}
          </Card>
        </>
      )}
      {editing ? <UserDialog user={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
      {access ? <EffectiveAccessDialog user={access} onClose={() => setAccess(null)} /> : null}
      {reassign ? <ReassignDialog from={reassign === 'open' ? null : reassign} onClose={() => setReassign(null)} /> : null}
    </>
  );
}
