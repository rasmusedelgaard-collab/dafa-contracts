import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi } from '../../app/context';
import type { DepartmentAdmin, DepartmentInputV4, NamedRef, NameInput } from '../../api/types';
import { Button, Card, EmptyState, ErrorState, Field, Modal, Pill, SkeletonRows, TextInput, useToast } from '../../components';
import { validateName } from '../../lib/settingsForms';
import { ConfirmDelete, SectionHead } from './common';

interface NamedListProps {
  resource: string;
  title: string;
  text: string;
  /** "department", "contract type" */
  noun: string;
  list: () => Promise<NamedRef[]>;
  create: (input: NameInput) => Promise<NamedRef>;
  update: (id: string, input: NameInput) => Promise<NamedRef>;
  remove: (id: string) => Promise<void>;
  /** Release 3: deactivate instead of deleting (`isActive` in the update body). */
  setActive?: (id: string, input: { name: string; isActive: boolean }) => Promise<NamedRef>;
  /**
   * Fields of the item that every update must send along. `PUT` replaces the whole item on the
   * API: a field that is left out is cleared, so a rename must repeat what it does not change.
   */
  keep?: (item: NamedRef) => object;
}

function NameDialog({ noun, item, existing, onSave, pending, onClose }: { noun: string; item: NamedRef | null; existing: NamedRef[]; onSave: (name: string) => void; pending: boolean; onClose: () => void }) {
  const [name, setName] = useState(item?.name ?? '');
  const [error, setError] = useState<string>();
  const submit = () => {
    const found = validateName(name, existing, item?.id);
    setError(found);
    if (!found) onSave(name.trim());
  };
  return (
    <Modal
      title={item ? `Rename ${noun}` : `Add ${noun}`}
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={pending}>
            {item ? 'Save' : `Add ${noun}`}
          </Button>
        </>
      }
    >
      <Field label="Name" error={error}>
        {(p) => (
          <TextInput
            {...p}
            data-autofocus=""
            value={name}
            maxLength={80}
            onChange={(e) => {
              setName(e.target.value);
              if (error) setError(undefined);
            }}
          />
        )}
      </Field>
    </Modal>
  );
}

function NamedList({ resource, title, text, noun, list, create, update, remove, setActive, keep }: NamedListProps) {
  const client = useQueryClient();
  const toast = useToast();
  const [editing, setEditing] = useState<NamedRef | 'new' | null>(null);
  const [deleting, setDeleting] = useState<NamedRef | null>(null);
  const items = useQuery({ queryKey: qk.admin(resource), queryFn: list });

  const changed = async (message: string) => {
    // The register filters and every form read these lists from `lookups`.
    await Promise.all([client.invalidateQueries({ queryKey: qk.admin(resource) }), client.invalidateQueries({ queryKey: qk.lookups }), client.invalidateQueries({ queryKey: qk.admin('audit') })]);
    toast.show(message);
    setEditing(null);
    setDeleting(null);
  };
  const save = useMutation({
    mutationFn: (name: string) => (editing && editing !== 'new' ? update(editing.id, { ...keep?.(editing), name }) : create({ name })),
    onSuccess: (saved) => changed(editing === 'new' ? `Added ${saved.name}` : `Renamed to ${saved.name}`),
    onError: (e) => toast.error(e),
  });
  const activate = useMutation({
    mutationFn: (item: NamedRef & { isActive?: boolean }) => (setActive ? setActive(item.id, { ...keep?.(item), name: item.name, isActive: item.isActive === false }) : Promise.reject(new Error('Not supported'))),
    onSuccess: (saved) => changed(`${saved.name} is ${(saved as { isActive?: boolean }).isActive === false ? 'inactive' : 'active'}`),
    onError: (e) => toast.error(e),
  });
  const del = useMutation({
    mutationFn: (item: NamedRef) => remove(item.id),
    onSuccess: (_d, item) => changed(`Deleted ${item.name}`),
    onError: (e) => {
      toast.error(e);
      setDeleting(null);
    },
  });

  const capital = `${noun.charAt(0).toUpperCase()}${noun.slice(1)}`;
  return (
    <>
      <SectionHead
        title={title}
        text={text}
        action={
          <Button size="md" icon="plus" onClick={() => setEditing('new')}>
            Add {noun}
          </Button>
        }
      />
      <Card clip>
        {items.isLoading ? (
          <SkeletonRows rows={5} label={`Loading ${title.toLowerCase()}`} />
        ) : items.isError ? (
          <ErrorState text={`The ${title.toLowerCase()} could not be loaded.`} onRetry={() => void items.refetch()} />
        ) : (items.data ?? []).length === 0 ? (
          <EmptyState compact title={`No ${title.toLowerCase()} yet`} text={`Add the first ${noun}.`} />
        ) : (
          <ul aria-label={title}>
            {(items.data ?? []).map((item, i) => (
              <li key={item.id} className="list-row" style={i === 0 ? { borderTop: 0 } : undefined}>
                <span className="kv__v spacer">
                  {item.name} {(item as { isActive?: boolean }).isActive === false ? <Pill tone="gray" small>Inactive</Pill> : null}
                </span>
                {setActive ? (
                  <Button variant="ghost" size="sm" loading={activate.isPending && activate.variables?.id === item.id} onClick={() => activate.mutate(item)}>
                    {(item as { isActive?: boolean }).isActive === false ? 'Activate' : 'Deactivate'}
                  </Button>
                ) : null}
                <Button variant="ghost" size="sm" icon="edit" onClick={() => setEditing(item)} aria-label={`Rename ${item.name}`}>
                  Rename
                </Button>
                <Button variant="ghost" size="sm" icon="trash" iconOnly onClick={() => setDeleting(item)} aria-label={`Delete ${item.name}`} />
              </li>
            ))}
          </ul>
        )}
      </Card>
      {editing ? <NameDialog noun={noun} item={editing === 'new' ? null : editing} existing={items.data ?? []} pending={save.isPending} onSave={(name) => save.mutate(name)} onClose={() => setEditing(null)} /> : null}
      {deleting ? (
        <ConfirmDelete
          title={`Delete ${deleting.name}?`}
          text={`${capital}s that are used by contracts cannot be deleted. This cannot be undone.`}
          pending={del.isPending}
          onConfirm={() => del.mutate(deleting)}
          onClose={() => setDeleting(null)}
        />
      ) : null}
    </>
  );
}

/**
 * What a department update repeats (review finding H1): the API sets the SharePoint library and
 * the Entra group "as given, null clears", so renaming or (de)activating without them would
 * unlink the department. They are edited under Settings > Access mapping.
 */
function departmentLinkFields(item: DepartmentAdmin): Pick<DepartmentInputV4, 'isActive' | 'documentLibrary' | 'groupId'> {
  return { isActive: item.isActive, documentLibrary: item.documentLibrary ?? null, groupId: item.groupId ?? null };
}

export function Departments() {
  const api = useApi();
  return (
    <NamedList
      resource="departments"
      title="Departments"
      noun="department"
      text="Departments cut across all legal entities: members of a department see its contracts in every entity. In production each department gets a document library in every entity site."
      list={api.admin.getDepartments}
      create={api.admin.createDepartment}
      update={api.admin.updateDepartment}
      remove={api.admin.deleteDepartment}
      setActive={api.admin.updateDepartmentV3}
      keep={departmentLinkFields}
    />
  );
}

export function ContractTypes() {
  const api = useApi();
  return (
    <NamedList
      resource="contract-types"
      title="Contract types"
      noun="contract type"
      text="The type decides which approval rule and which retention rule apply to a contract. Customer and HR contracts are out of scope."
      list={api.admin.getContractTypes}
      create={api.admin.createContractType}
      update={api.admin.updateContractType}
      remove={api.admin.deleteContractType}
      setActive={api.admin.updateContractTypeV3}
    />
  );
}
