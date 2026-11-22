import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi, useLookups } from '../../app/context';
import type { Template, TemplateCategory, TemplateInput, TemplateLanguage } from '../../api/types';
import { Button, Card, EmptyState, ErrorState, Field, Modal, Select, SkeletonRows, Textarea, TextInput, useToast } from '../../components';
import { fileExtension, formatFileSize, saveBlob } from '../../lib/format';
import { refs, validateName } from '../../lib/settingsForms';
import { ConfirmDelete, SectionHead } from './common';
import { useFileError } from '../../features/fileErrors';

const MAX_TEMPLATE_BYTES = 10 * 1024 * 1024;
const LANGUAGES = [
  { value: 'da', label: 'Danish' },
  { value: 'en', label: 'English' },
  { value: 'sv', label: 'Swedish' },
  { value: 'de', label: 'German' },
  { value: 'pl', label: 'Polish' },
  { value: 'it', label: 'Italian' },
  { value: 'zh', label: 'Chinese' },
];

function TemplateDialog({ template, existing, onClose }: { template: Template | null; existing: Template[]; onClose: () => void }) {
  const api = useApi();
  const lookups = useLookups();
  const client = useQueryClient();
  const toast = useToast();
  const [file, setFile] = useState<File | null>(null);
  const [name, setName] = useState(template?.name ?? '');
  const [description, setDescription] = useState(template?.description ?? '');
  const [typeIds, setTypeIds] = useState<string[]>(template?.typeIds?.length ? template.typeIds : template?.typeId ? [template.typeId] : []);
  const [categoryId, setCategoryId] = useState(template?.categoryId ?? '');
  const [language, setLanguage] = useState<string>(template?.language ?? '');
  const [isActive, setActive] = useState(template?.isActive ?? true);
  const [tried, setTried] = useState(false);
  const categories = useQuery({ queryKey: qk.admin('template-categories'), queryFn: () => api.admin.getTemplateCategories() });

  const fileError = template ? undefined : !file ? 'Choose a Word file' : !/\.(docx|dotx)$/i.test(file.name) ? 'Templates must be Word files (.docx or .dotx)' : file.size > MAX_TEMPLATE_BYTES ? 'The file is larger than 10 MB' : undefined;
  const nameError = validateName(name, existing, template?.id);

  const save = useMutation({
    mutationFn: () => {
      const input: TemplateInput = { name: name.trim(), description: description.trim(), typeId: typeIds[0] ?? null, typeIds, categoryId: categoryId || null, language: (language || null) as TemplateLanguage | null, isActive };
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
      <Field label="Category" optional hint="New contract shows templates grouped by category.">
        {(p) => <Select {...p} value={categoryId} placeholder="No category" onChange={(e) => setCategoryId(e.target.value)} options={refs((categories.data ?? []).filter((c) => c.isActive || c.id === categoryId))} />}
      </Field>
      <Field label="Language" optional>
        {(p) => <Select {...p} value={language} placeholder="Not set" onChange={(e) => setLanguage(e.target.value)} options={LANGUAGES} />}
      </Field>
      <fieldset className="fieldset">
        <legend className="field__label">Contract types</legend>
        <span className="field__hint">None ticked = every type. The first one is preselected on contracts started from the template.</span>
        <div className="check-grid">
          {lookups.contractTypes.filter((t) => t.isActive !== false || typeIds.includes(t.id)).map((t) => (
            <label key={t.id} className="check">
              <input type="checkbox" checked={typeIds.includes(t.id)} onChange={(e) => setTypeIds(e.target.checked ? [...typeIds, t.id] : typeIds.filter((x) => x !== t.id))} />
              {t.name}
            </label>
          ))}
        </div>
      </fieldset>
      <label className="check">
        <input type="checkbox" checked={isActive} onChange={(e) => setActive(e.target.checked)} />
        Active (offered in New contract)
      </label>
    </Modal>
  );
}

export function Templates() {
  const api = useApi();
  const lookups = useLookups();
  const client = useQueryClient();
  const toast = useToast();
  const fileError = useFileError();
  const [editing, setEditing] = useState<Template | 'new' | null>(null);
  const [deleting, setDeleting] = useState<Template | null>(null);
  const templates = useQuery({ queryKey: qk.admin('templates'), queryFn: () => api.admin.getTemplates() });
  const list = templates.data ?? [];

  const download = useMutation({
    mutationFn: (t: Template) => api.admin.getTemplateContent(t.id),
    onSuccess: (file) => saveBlob(file.blob, file.fileName),
    onError: (e, t) => fileError(e, 'The template could not be downloaded. Try again.', () => download.mutate(t)),
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
                    {t.categoryName ?? 'No category'}
                    {t.isActive === false ? ' · inactive' : ''} · {t.fileName} · {(t.typeIds?.length ? t.typeIds : t.typeId ? [t.typeId] : []).map((id) => lookups.contractTypes.find((x) => x.id === id)?.name ?? 'Unknown type').join(', ') || 'Any contract type'}
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

function CategoryDialog({ item, onClose }: { item: TemplateCategory | null; onClose: () => void }) {
  const api = useApi();
  const client = useQueryClient();
  const toast = useToast();
  const [name, setName] = useState(item?.name ?? '');
  const [description, setDescription] = useState(item?.description ?? '');
  const [isActive, setActive] = useState(item?.isActive ?? true);
  const [error, setError] = useState<string | undefined>();
  const save = useMutation({
    mutationFn: () => (item ? api.admin.updateTemplateCategory(item.id, { name: name.trim(), description: description.trim(), isActive }) : api.admin.createTemplateCategory({ name: name.trim(), description: description.trim(), isActive })),
    onSuccess: async (c) => {
      await Promise.all([client.invalidateQueries({ queryKey: qk.admin('template-categories') }), client.invalidateQueries({ queryKey: qk.admin('templates') }), client.invalidateQueries({ queryKey: qk.templates })]);
      toast.show(item ? `Saved category "${c.name}"` : `Added category "${c.name}"`);
      onClose();
    },
    onError: (e) => toast.error(e),
  });
  return (
    <Modal
      title={item ? 'Edit category' : 'Add category'}
      onClose={onClose}
      onSubmit={() => (name.trim() ? save.mutate() : setError('Enter a name'))}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            {item ? 'Save' : 'Add category'}
          </Button>
        </>
      }
    >
      <Field label="Name" error={error}>
        {(p) => <TextInput {...p} data-autofocus="" maxLength={80} value={name} onChange={(e) => setName(e.target.value)} />}
      </Field>
      <Field label="Description" optional>
        {(p) => <Textarea {...p} rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />}
      </Field>
      <label className="check">
        <input type="checkbox" checked={isActive} onChange={(e) => setActive(e.target.checked)} />
        Active
      </label>
    </Modal>
  );
}

/** Settings > Template categories (owner item E): add, rename, order, deactivate, delete when empty. */
export function TemplateCategories() {
  const api = useApi();
  const client = useQueryClient();
  const toast = useToast();
  const [editing, setEditing] = useState<TemplateCategory | 'new' | null>(null);
  const [deleting, setDeleting] = useState<TemplateCategory | null>(null);
  const q = useQuery({ queryKey: qk.admin('template-categories'), queryFn: () => api.admin.getTemplateCategories() });
  const list = q.data ?? [];
  const refresh = () => Promise.all([client.invalidateQueries({ queryKey: qk.admin('template-categories') }), client.invalidateQueries({ queryKey: qk.templates })]);
  const order = useMutation({
    mutationFn: (ids: string[]) => api.admin.orderTemplateCategories(ids),
    onSuccess: refresh,
    onError: (e) => toast.error(e),
  });
  const del = useMutation({
    mutationFn: (c: TemplateCategory) => api.admin.deleteTemplateCategory(c.id),
    onSuccess: async (_d, c) => {
      await refresh();
      toast.show(`Deleted category "${c.name}"`);
      setDeleting(null);
    },
    onError: (e) => {
      toast.error(e);
      setDeleting(null);
    },
  });
  const move = (i: number, by: number) => {
    const ids = list.map((c) => c.id);
    const [x] = ids.splice(i, 1);
    ids.splice(i + by, 0, x as string);
    order.mutate(ids);
  };
  return (
    <>
      <SectionHead title="Template categories" text="Groups for the templates in New contract, in this order. An inactive category hides its templates." action={<Button icon="plus" onClick={() => setEditing('new')}>Add category</Button>} />
      <Card clip>
        {q.isLoading ? (
          <SkeletonRows rows={3} />
        ) : q.isError ? (
          <ErrorState text="The categories could not be loaded." onRetry={() => void q.refetch()} />
        ) : list.length === 0 ? (
          <EmptyState compact title="No categories yet" />
        ) : (
          <ul aria-label="Template categories">
            {list.map((c, i) => (
              <li key={c.id} className="list-row list-row--wrap" style={i === 0 ? { borderTop: 0 } : undefined}>
                <span className="kv" style={{ flex: '1 1 260px' }}>
                  <span className="kv__v">
                    {c.name}
                    {c.isActive ? null : <span className="caption"> · inactive</span>}
                  </span>
                  <span className="kv__k">{c.description || 'No description'} · {c.templateCount} {c.templateCount === 1 ? 'template' : 'templates'}</span>
                </span>
                <div className="wide-row__actions">
                  <Button variant="ghost" size="sm" icon="chevronUp" iconOnly disabled={i === 0 || order.isPending} onClick={() => move(i, -1)} aria-label={`Move ${c.name} up`} />
                  <Button variant="ghost" size="sm" icon="chevronDown" iconOnly disabled={i === list.length - 1 || order.isPending} onClick={() => move(i, 1)} aria-label={`Move ${c.name} down`} />
                  <Button variant="ghost" size="sm" icon="edit" onClick={() => setEditing(c)} aria-label={`Edit ${c.name}`}>
                    Edit
                  </Button>
                  <Button variant="ghost" size="sm" icon="trash" iconOnly onClick={() => setDeleting(c)} aria-label={`Delete ${c.name}`} />
                </div>
              </li>
            ))}
          </ul>
        )}
      </Card>
      {editing ? <CategoryDialog item={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
      {deleting ? <ConfirmDelete title={`Delete category "${deleting.name}"?`} text="Only an empty category can be deleted. Move its templates first, or make it inactive." pending={del.isPending} onConfirm={() => del.mutate(deleting)} onClose={() => setDeleting(null)} /> : null}
    </>
  );
}
