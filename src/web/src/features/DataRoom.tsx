import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi } from '../app/context';
import type { ContractFilter, DataRoomExport, DataRoomInput } from '../api/types';
import { Button, Field, Modal, Pill, Segmented, TextInput, useToast } from '../components';
import { formatDateTime, todayIso } from '../lib/dates';
import { formatFileSize, plural, saveBlob } from '../lib/format';

/**
 * "Data room export" for due diligence: the backend builds a zip with an index (CSV), a manifest
 * and the documents of every contract that matches the filter. The UI starts it and polls.
 */
export function DataRoomDialog({ filter, chips, onClose }: { filter: ContractFilter; chips: string[]; onClose: () => void }) {
  const api = useApi();
  const toast = useToast();
  const client = useQueryClient();
  const [name, setName] = useState(`Due diligence ${todayIso()}`);
  const [documents, setDocuments] = useState<DataRoomInput['documents']>('All');
  const [versions, setVersions] = useState<DataRoomInput['versions']>('Latest');
  const [error, setError] = useState<string>();
  const [started, setStarted] = useState<DataRoomExport | null>(null);

  const create = useMutation({
    mutationFn: () => api.createDataRoomExport({ name: name.trim(), filter, documents, versions }),
    onSuccess: (x) => {
      setStarted(x);
      void client.invalidateQueries({ queryKey: qk.dataRoom });
    },
    onError: (e) => toast.error(e),
  });
  const poll = useQuery({
    queryKey: [...qk.dataRoom, started?.id],
    queryFn: () => api.getDataRoomExport(started?.id ?? ''),
    enabled: !!started,
    refetchInterval: (q) => (q.state.data?.status === 'Ready' || q.state.data?.status === 'Failed' ? false : 1500),
  });
  const history = useQuery({ queryKey: qk.dataRoom, queryFn: () => api.getDataRoomExports() });
  const current = poll.data ?? started;
  const download = useMutation({
    mutationFn: (id: string) => api.getDataRoomContent(id),
    onSuccess: (f) => saveBlob(f.blob, f.fileName),
    onError: (e) => toast.error(e),
  });
  useEffect(() => {
    if (current?.status === 'Ready') void client.invalidateQueries({ queryKey: qk.dataRoom });
  }, [current?.status, client]);

  const submit = () => {
    if (!name.trim()) return setError('Give the export a name');
    setError(undefined);
    create.mutate();
  };

  return (
    <Modal
      title="Data room export"
      subtitle="A zip with an index of the contracts (CSV), a manifest and their documents, for a buyer's data room."
      onClose={onClose}
      onSubmit={current ? undefined : submit}
      footer={
        current ? (
          <>
            <Button variant="secondary" onClick={onClose}>
              Close
            </Button>
            <Button icon="download" disabled={current.status !== 'Ready'} loading={download.isPending || (current.status !== 'Ready' && current.status !== 'Failed')} onClick={() => download.mutate(current.id)}>
              Download zip
            </Button>
          </>
        ) : (
          <>
            <Button variant="secondary" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={create.isPending}>
              Create export
            </Button>
          </>
        )
      }
    >
      {current ? (
        <div className="stack" style={{ gap: 12 }} role="status">
          <div className="row-flex" style={{ gap: 10 }}>
            <Pill tone={current.status === 'Ready' ? 'green' : current.status === 'Failed' ? 'red' : 'blue'}>{current.status === 'Ready' ? 'Ready' : current.status === 'Failed' ? 'Failed' : 'Preparing…'}</Pill>
            <strong>{current.name}</strong>
          </div>
          <p className="secondary">
            {plural(current.contractCount, 'contract')}, {plural(current.documentCount, 'document')}
            {current.sizeBytes ? `, ${formatFileSize(current.sizeBytes)}` : ''}. {current.error ?? ''}
          </p>
          {current.expiresAt ? <p className="caption">Available until {formatDateTime(current.expiresAt)}.</p> : null}
        </div>
      ) : (
        <div className="stack" style={{ gap: 14 }}>
          <div className="banner tone-blue">
            <strong>Contracts:</strong> {chips.length ? chips.join(' · ') : 'all contracts you can see (excluding ended)'}
          </div>
          <Field label="Name" error={error}>
            {(p) => <TextInput {...p} value={name} maxLength={80} onChange={(e) => setName(e.target.value)} />}
          </Field>
          <div className="field">
            <span className="field__label">Documents</span>
            <Segmented mode="radio" ariaLabel="Documents" options={[{ value: 'All', label: 'All documents' }, { value: 'MainOnly', label: 'Main contracts only' }, { value: 'None', label: 'Index only' }]} value={documents} onChange={setDocuments} />
          </div>
          <div className="field">
            <span className="field__label">Versions</span>
            <Segmented mode="radio" ariaLabel="Versions" options={[{ value: 'Latest', label: 'Latest version' }, { value: 'All', label: 'All versions' }]} value={versions} onChange={setVersions} />
          </div>
          {history.data?.length ? (
            <div className="stack" style={{ gap: 6 }}>
              <span className="field__label">Earlier exports</span>
              {history.data.slice(0, 4).map((x) => (
                <div key={x.id} className="mini-row">
                  <span className="mini-row__a">{x.name}</span>
                  <span className="caption">
                    {formatDateTime(x.createdAt)} · {plural(x.contractCount, 'contract')}{' '}
                    {x.status === 'Ready' ? (
                      <button type="button" className="link-btn" onClick={() => download.mutate(x.id)}>
                        Download
                      </button>
                    ) : (
                      x.status
                    )}
                  </span>
                </div>
              ))}
            </div>
          ) : null}
        </div>
      )}
    </Modal>
  );
}
