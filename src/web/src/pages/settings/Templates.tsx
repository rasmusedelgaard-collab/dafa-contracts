import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi, useLookups } from '../../app/context';
import type { Template, TemplateInput } from '../../api/types';
import { Button, Card, EmptyState, ErrorState, Field, Modal, Select, SkeletonRows, Textarea, TextInput, useToast } from '../../components';
import { fileExtension, formatFileSize, saveBlob } from '../../lib/format';
import { refs, validateName } from '../../lib/settingsForms';
import { ConfirmDelete, SectionHead } from './common';

const MAX_TEMPLATE_BYTES = 10 * 1024 * 1024;

function TemplateDialog({ template, existing, onClose }: { template: Template | null; existing: Template[]; onClose: () => void }) {
  const api = useApi();
  const lookups = useLookups();
  const client = useQueryClient();
  const toast = useToast();
  const [file, setFile] = useState<File | null>(null);
  const [name, setName] = useState(template?.name ?? '');
  const [description, setDescription] = useState(template?.description ?? '');
  const [typeId, setTypeId] = useState(template?.typeId ?? '');
  const [tried, setTried] = useState(false);

  const fileError = template ? undefined : !file ? 'Choose a Word file' : !/\.(docx|dotx)$/i.test(file.name) ? 'Templates must be Word files (.docx or .dotx)' : file.size > MAX_TEMPLATE_BYTES ? 'The file is larger than 10 MB' : undefined;
  const nameError = validateName(name, existing, template?.id);

  const save = useMutation({
    mutationFn: () => {
      const input: TemplateInput = { name: name.trim(), description: description.trim(), typeId: typeId || null };
      return template ? api.admin.updateTemplate(template.id, input) : api.admin.createTemplate(input, file as File);
    },
    onSuccess: async (saved) => {
      await Promise.all([client.invalidateQueries({ queryKey: qk.admin('templates') }), client.invalidateQueries({ queryKey: qk.templates }), client.invalidateQueries({ queryKey: qk.admin('audit') })]);
      toast.show(template ? `Saved template "${saved.name}"` : `Uploaded template "${saved.name}"`);
      onClose();
    },
    onError: (e) => toast.error(e),
  });

  const submit = () => {
    setTried(true);
    if (!fileError && !nameError) save.mutate();
  };

  return (
    <Modal
      title={template ? 'Edit template' : 'Upload template'}
      subtitle={template ? template.fileName : 'A Word file with {{placeholders}} or content controls that the New contract flow fills in.'}
      onClose={onClose}
      onSubmit={submit}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            {template ? 'Save' : 'Upload'}
          </Button>
        </>
      }
    >
      {template ? null : (
        <Field label="Word file" error={tried ? fileError : undefined} hint={file ? `${file.name} · ${formatFileSize(file.size)}` : '.docx or .dotx, up to 10 MB.'}>
          {(p) => (
            <input
              {...p}
              type="file"
              className="file-input"
              accept=".docx,.dotx,application/vnd.openxmlformats-officedocument.wordprocessingml.document"
              data-autofocus=""
              onChange={(e) => {
                const f = e.target.files?.[0] ?? null;
                setFile(f);
                if (f && !name.trim()) setName(f.name.replace(/\.[a-z]+$/i, ''));
              }}
            />
          )}
        </Field>
      )}
      <Field label="Name" error={tried ? nameError : undefined}>
        {(p) => <TextInput {...p} value={name} maxLength={80} onChange={(e) => setName(e.target.value)} data-autofocus={template ? '' : undefined} />}
      </Field>
      <Field label="Description" optional hint="Shown when someone starts a contract from the template.">
        {(p) => <Textarea {...p} rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />}
      </Field>
      <Field label="Contract type" optional hint="Preselected on contracts started from the template.">
        {(p) => <Select {...p} value={typeId} placeholder="No type" onChange={(e) => setTypeId(e.target.value)} options={refs(lookups.contractTypes)} />}
      </Field>
    </Modal>
  );
}

export function Templates() {
  const api = useApi();
  const lookups = useLookups();
  const client = useQueryClient();
  const toast = useToast();
  const [editing, setEditing] = useState<Template | 'new' | null>(null);
  const [deleting, setDeleting] = useState<Template | null>(null);
  const templates = useQuery({ queryKey: qk.admin('templates'), queryFn: () => api.admin.getTemplates() });
  const list = templates.data ?? [];

  const download = useMutation({
    mutationFn: (t: Template) => api.admin.getTemplateContent(t.id),
    onSuccess: (file) => saveBlob(file.blob, file.fileName),
    onError: (e) => toast.error(e, 'The template could not be downloaded. Try again.'),
  });
  const del = useMutation({
    mutationFn: (t: Template) => api.admin.deleteTemplate(t.id),
    onSuccess: async (_d, t) => {
      await Promise.all([client.invalidateQueries({ queryKey: qk.admin('templates') }), client.invalidateQueries({ queryKey: qk.templates }), client.invalidateQueries({ queryKey: qk.admin('audit') })]);
      toast.show(`Deleted template "${t.name}"`);
      setDeleting(null);
    },
    onError: (e) => {
      toast.error(e);
      setDeleting(null);
    },
  });

  return (
    <>
      <SectionHead
        title="Templates"
        text="Word templates with the DAFA standard terms. People pick one in New contract and the app fills in the details from the form."
        action={
          <Button size="md" icon="upload" onClick={() => setEditing('new')}>
            Upload template
          </Button>
        }
      />
      <Card clip>
        {templates.isLoading ? (
          <SkeletonRows rows={2} label="Loading templates" />
        ) : templates.isError ? (
          <ErrorState text="The templates could not be loaded." onRetry={() => void templates.refetch()} />
        ) : list.length === 0 ? (
          <EmptyState compact title="No templates yet" text="Upload a Word file to let people start contracts from the DAFA standard terms." />
        ) : (
          <ul aria-label="Templates">
            {list.map((t, i) => (
              <li key={t.id} className="list-row list-row--wrap" style={i === 0 ? { borderTop: 0 } : undefined}>
                <span className="tile" aria-hidden="true">
                  {fileExtension(t.fileName)}
                </span>
                <span className="kv" style={{ flex: '1 1 260px' }}>
                  <span className="kv__v">{t.name}</span>
                  <span className="kv__k">{t.description || 'No description'}</span>
                  <span className="kv__k">
                    {t.fileName} · {t.typeId ? (lookups.contractTypes.find((x) => x.id === t.typeId)?.name ?? 'Unknown type') : 'Any contract type'}
                    {t.placeholders.length > 0 ? ` · fills ${t.placeholders.length} fields` : ''}
                  </span>
                </span>
                <div className="wide-row__actions">
                  <Button variant="ghost" size="sm" icon="download" loading={download.isPending && download.variables?.id === t.id} onClick={() => download.mutate(t)} aria-label={`Download ${t.name}`}>
                    Download
                  </Button>
                  <Button variant="ghost" size="sm" icon="edit" onClick={() => setEditing(t)} aria-label={`Edit ${t.name}`}>
                    Edit
                  </Button>
                  <Button variant="ghost" size="sm" icon="trash" iconOnly onClick={() => setDeleting(t)} aria-label={`Delete ${t.name}`} />
                </div>
              </li>
            ))}
          </ul>
        )}
      </Card>
      {editing ? <TemplateDialog template={editing === 'new' ? null : editing} existing={list} onClose={() => setEditing(null)} /> : null}
      {deleting ? <ConfirmDelete title={`Delete template "${deleting.name}"?`} text="Contracts already drafted from it keep their documents." pending={del.isPending} onConfirm={() => del.mutate(deleting)} onClose={() => setDeleting(null)} /> : null}
    </>
  );
}
