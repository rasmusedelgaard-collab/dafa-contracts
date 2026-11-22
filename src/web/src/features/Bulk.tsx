import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { useApi, useAppConfig, useInvalidateContracts, useLookups } from '../app/context';
import type { BulkInput, BulkResult, ContractRole } from '../api/types';
import { Button, Field, Modal, Select, Textarea, useToast } from '../components';
import { plural, saveBlob } from '../lib/format';

type Action = 'tag' | 'untag' | 'reassign' | 'complete' | null;

/** Bar shown when register rows are selected: export, tag, reassign, request data completion. */
export function BulkBar({ ids, onClear }: { ids: string[]; onClear: () => void }) {
  const api = useApi();
  const toast = useToast();
  const config = useAppConfig();
  const lookups = useLookups();
  const invalidate = useInvalidateContracts();
  const [action, setAction] = useState<Action>(null);
  const [tagId, setTagId] = useState('');
  const [role, setRole] = useState<ContractRole>('Owner');
  const [toUserId, setTo] = useState('');
  const [message, setMessage] = useState('');
  const [result, setResult] = useState<BulkResult | null>(null);
  const [error, setError] = useState<string>();

  const exportSel = useMutation({
    mutationFn: () => api.exportContracts({ id: ids, columns: ['contract', 'contractNo', 'counterparty', 'entity', 'department', 'owner', 'notice', 'end', 'annualValue', 'totalValue', 'status', 'tags'] } as never),
    onSuccess: (f) => {
      saveBlob(f.blob, f.fileName);
      toast.show(`${plural(ids.length, 'contract')} exported`);
    },
    onError: (e) => toast.error(e),
  });
  const run = useMutation({
    mutationFn: (input: BulkInput) => api.bulk(input),
    onSuccess: async (r) => {
      await invalidate();
      setAction(null);
      if (r.failed.length) setResult(r);
      else {
        toast.show(`Done for ${plural(r.succeeded, 'contract')}`);
        onClear();
      }
    },
    onError: (e) => toast.error(e),
  });

  const submit = () => {
    setError(undefined);
    if ((action === 'tag' || action === 'untag') && !tagId) return setError('Choose a tag');
    if (action === 'reassign' && !toUserId) return setError('Choose a person');
    const base = { contractIds: ids };
    if (action === 'tag') run.mutate({ ...base, action: 'AddTag', tagId });
    else if (action === 'untag') run.mutate({ ...base, action: 'RemoveTag', tagId });
    else if (action === 'reassign') run.mutate({ ...base, action: 'Reassign', role, toUserId });
    else if (action === 'complete') run.mutate({ ...base, action: 'RequestDataCompletion', message: message.trim() || undefined });
  };
  const owners = lookups.users.filter((u) => u.isActive !== false && (role !== 'Owner' || !u.roles || u.roles.includes('ContractOwner') || u.roles.includes('Admin')));
  const title = action === 'tag' ? 'Add a tag' : action === 'untag' ? 'Remove a tag' : action === 'reassign' ? 'Reassign' : 'Request data completion';

  return (
    <div className="bulkbar" role="region" aria-label="Selected contracts">
      <strong>{plural(ids.length, 'contract')} selected</strong>
      <div className="bulkbar__actions">
        <Button variant="on-dark" size="sm" icon="download" loading={exportSel.isPending} onClick={() => exportSel.mutate()}>
          Export selected
        </Button>
        {config.tags.length ? (
          <>
            <Button variant="on-dark-ghost" size="sm" icon="tag" onClick={() => setAction('tag')}>
              Add tag
            </Button>
            <Button variant="on-dark-ghost" size="sm" onClick={() => setAction('untag')}>
              Remove tag
            </Button>
          </>
        ) : null}
        <Button variant="on-dark-ghost" size="sm" icon="users" onClick={() => setAction('reassign')}>
          Reassign
        </Button>
        <Button variant="on-dark-ghost" size="sm" icon="alert" onClick={() => setAction('complete')}>
          Request data
        </Button>
      </div>
      <div className="spacer" />
      <Button variant="on-dark-ghost" size="sm" icon="x" onClick={onClear}>
        Clear
      </Button>

      {action ? (
        <Modal
          title={title}
          subtitle={`${plural(ids.length, 'selected contract')}. Contracts you may not change are skipped and listed afterwards.`}
          onClose={() => setAction(null)}
          onSubmit={submit}
          footer={
            <>
              <Button variant="secondary" onClick={() => setAction(null)}>
                Cancel
              </Button>
              <Button type="submit" loading={run.isPending}>
                {action === 'complete' ? 'Send request' : 'Apply'}
              </Button>
            </>
          }
        >
          <div className="stack" style={{ gap: 14 }}>
            {action === 'tag' || action === 'untag' ? (
              <Field label="Tag" error={error}>
                {(p) => <Select {...p} value={tagId} placeholder="Choose" options={config.tags.map((t) => ({ value: t.id, label: t.name }))} onChange={(e) => setTagId(e.target.value)} />}
              </Field>
            ) : action === 'reassign' ? (
              <>
                <Field label="Role">
                  {(p) => (
                    <Select
                      {...p}
                      value={role}
                      options={[
                        { value: 'Owner', label: 'Owner' },
                        { value: 'Delegate', label: 'Delegate' },
                        { value: 'Responsible', label: 'Responsible' },
                      ]}
                      onChange={(e) => setRole(e.target.value as ContractRole)}
                    />
                  )}
                </Field>
                <Field label="New person" error={error} hint={role === 'Owner' ? 'Only people with the contract owner role are listed.' : undefined}>
                  {(p) => <Select {...p} value={toUserId} placeholder="Choose" options={owners.map((u) => ({ value: u.id, label: u.name }))} onChange={(e) => setTo(e.target.value)} />}
                </Field>
              </>
            ) : (
              <>
                <p className="secondary">The owner and delegate of each selected contract with missing data get a notification listing what is missing.</p>
                <Field label="Note" optional>
                  {(p) => <Textarea {...p} rows={3} value={message} onChange={(e) => setMessage(e.target.value)} />}
                </Field>
              </>
            )}
          </div>
        </Modal>
      ) : null}
      {result ? (
        <Modal
          title={`Done for ${plural(result.succeeded, 'contract')}`}
          subtitle={`${plural(result.failed.length, 'contract')} could not be changed.`}
          onClose={() => {
            setResult(null);
            onClear();
          }}
          footer={
            <Button
              onClick={() => {
                setResult(null);
                onClear();
              }}
            >
              Close
            </Button>
          }
        >
          <ul className="stack" style={{ gap: 6 }}>
            {result.failed.map((f) => (
              <li key={f.contractId} className="mini-row">
                <span className="mini-row__a">{f.title}</span>
                <span className="caption">{f.reason}</span>
              </li>
            ))}
          </ul>
        </Modal>
      ) : null}
    </div>
  );
}
