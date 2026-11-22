import { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { permissions, qk, useApi, useAppConfig, useMe } from '../app/context';
import { REGISTER_COLUMNS, type RegisterColumn, type SavedView, type ViewScope } from '../api/types';
import { Button, Field, Modal, Segmented, TextInput, useToast } from '../components';
import { paramsFromView, viewInput, viewMatches } from '../lib/views';
import { COLUMN_LABELS } from './columns';

/** "View" select with the saved views, plus save / update / delete. */
export function ViewsMenu({ params, columns, onApply }: { params: URLSearchParams; columns: RegisterColumn[]; onApply: (p: URLSearchParams) => void }) {
  const api = useApi();
  const me = useMe();
  const toast = useToast();
  const client = useQueryClient();
  const views = useQuery({ queryKey: qk.views, queryFn: () => api.getViews(), retry: false });
  const [saving, setSaving] = useState(false);
  const [name, setName] = useState('');
  const [scope, setScope] = useState<ViewScope>('Personal');
  const [isDefault, setDefault] = useState(false);
  const [error, setError] = useState<string>();
  const applied = views.data?.find((v) => v.id === params.get('sv'));
  const changed = applied ? !viewMatches(applied, params, columns) : false;
  const mayShare = permissions(me).canCreate;

  const refresh = () => client.invalidateQueries({ queryKey: qk.views });
  const create = useMutation({
    mutationFn: () => api.createView(viewInput(name, scope, params, columns, isDefault)),
    onSuccess: async (v) => {
      await refresh();
      setSaving(false);
      onApply(paramsFromView(v));
      toast.show(`View "${v.name}" saved`);
    },
    onError: (e) => {
      setError(e instanceof Error ? e.message : undefined);
      toast.error(e);
    },
  });
  const update = useMutation({
    mutationFn: (v: SavedView) => api.updateView(v.id, viewInput(v.name, v.scope, params, columns, v.isDefault)),
    onSuccess: async (v) => {
      await refresh();
      onApply(paramsFromView(v));
      toast.show(`View "${v.name}" updated`);
    },
    onError: (e) => toast.error(e),
  });
  const remove = useMutation({
    mutationFn: (v: SavedView) => api.deleteView(v.id),
    onSuccess: async () => {
      await refresh();
      const p = new URLSearchParams(params);
      p.delete('sv');
      onApply(p);
      toast.show('View deleted');
    },
    onError: (e) => toast.error(e),
  });

  // A personal default view opens with the register when the link carries no filters of its own.
  const defaulted = useRef(false);
  useEffect(() => {
    if (defaulted.current || !views.data) return;
    defaulted.current = true;
    const own = [...params.keys()].filter((k) => k !== 'entity');
    const def = views.data.find((v) => v.isDefault && v.scope === 'Personal');
    if (def && own.length === 0) onApply(paramsFromView(def));
  }, [views.data, params, onApply]);

  const personal = (views.data ?? []).filter((v) => v.scope === 'Personal');
  const shared = (views.data ?? []).filter((v) => v.scope === 'Shared');

  return (
    <div className="views-menu">
      <select
        className={`chip${applied ? ' chip--on' : ''}`}
        aria-label="Saved view"
        value={applied?.id ?? ''}
        onChange={(e) => {
          const v = views.data?.find((x) => x.id === e.target.value);
          if (v) onApply(paramsFromView(v));
          else onApply(new URLSearchParams());
        }}
      >
        <option value="">Saved views{views.data?.length ? ` (${views.data.length})` : ''}</option>
        {personal.length ? (
          <optgroup label="My views">
            {personal.map((v) => (
              <option key={v.id} value={v.id}>
                {v.name}
                {v.isDefault ? ' (default)' : ''}
              </option>
            ))}
          </optgroup>
        ) : null}
        {shared.length ? (
          <optgroup label="Shared views">
            {shared.map((v) => (
              <option key={v.id} value={v.id}>
                {v.name} · {v.ownerName}
              </option>
            ))}
          </optgroup>
        ) : null}
      </select>
      {applied && changed && applied.canEdit ? (
        <Button variant="tint" size="sm" loading={update.isPending} onClick={() => update.mutate(applied)}>
          Update view
        </Button>
      ) : null}
      <Button
        variant="secondary"
        size="sm"
        icon="star"
        onClick={() => {
          setName(applied && changed ? `${applied.name} (copy)` : '');
          setError(undefined);
          setSaving(true);
        }}
      >
        Save view
      </Button>
      {applied?.canEdit ? (
        <Button variant="ghost" size="sm" iconOnly icon="trash" aria-label={`Delete view ${applied.name}`} loading={remove.isPending} onClick={() => remove.mutate(applied)} />
      ) : null}
      {saving ? (
        <Modal
          title="Save view"
          subtitle="Saves the filters, the sort and the columns. A shared view shows each person only the contracts they have access to."
          onClose={() => setSaving(false)}
          onSubmit={() => (name.trim() ? create.mutate() : setError('Give the view a name'))}
          footer={
            <>
              <Button variant="secondary" onClick={() => setSaving(false)}>
                Cancel
              </Button>
              <Button type="submit" loading={create.isPending}>
                Save view
              </Button>
            </>
          }
        >
          <div className="stack" style={{ gap: 14 }}>
            <Field label="Name" error={error}>
              {(p) => <TextInput {...p} value={name} maxLength={80} onChange={(e) => setName(e.target.value)} />}
            </Field>
            <div className="field">
              <span className="field__label">Who can use it</span>
              <Segmented mode="radio" ariaLabel="Who can use it" options={[{ value: 'Personal', label: 'Only me' }, ...(mayShare ? [{ value: 'Shared' as const, label: 'Everyone' }] : [])]} value={scope} onChange={(v) => setScope(v as ViewScope)} />
            </div>
            {scope === 'Personal' ? (
              <label className="check">
                <input type="checkbox" checked={isDefault} onChange={(e) => setDefault(e.target.checked)} />
                Open the register with this view
              </label>
            ) : null}
          </div>
        </Modal>
      ) : null}
    </div>
  );
}

/** Column chooser: standard columns and custom fields marked "show in register". */
export function ColumnChooser({ columns, onChange, onClose, onReset }: { columns: RegisterColumn[]; onChange: (c: RegisterColumn[]) => void; onClose: () => void; onReset: () => void }) {
  const config = useAppConfig();
  const [picked, setPicked] = useState<RegisterColumn[]>(columns);
  const all: { key: RegisterColumn; label: string }[] = [
    ...REGISTER_COLUMNS.map((k) => ({ key: k as RegisterColumn, label: COLUMN_LABELS[k] ?? k })),
    ...config.customFields.filter((d) => d.showInRegister).map((d) => ({ key: `cf.${d.key}` as RegisterColumn, label: d.label })),
  ];
  const move = (key: RegisterColumn, by: number) => {
    const i = picked.indexOf(key);
    const j = i + by;
    if (i <= 0 || j <= 0 || j >= picked.length) return;
    const next = [...picked];
    [next[i], next[j]] = [next[j] as RegisterColumn, next[i] as RegisterColumn];
    setPicked(next);
  };
  return (
    <Modal
      title="Columns"
      subtitle="Choose and order the register columns. Wide screens have room for more."
      onClose={onClose}
      footer={
        <>
          <Button variant="ghost" onClick={onReset}>
            Reset to default
          </Button>
          <div className="spacer" />
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => onChange(picked)}>Apply</Button>
        </>
      }
    >
      <ul className="col-chooser">
        {all.map((c) => {
          const on = picked.includes(c.key);
          const fixed = c.key === 'contract';
          return (
            <li key={c.key}>
              <label className="check">
                <input type="checkbox" checked={on} disabled={fixed} onChange={(e) => setPicked(e.target.checked ? [...picked, c.key] : picked.filter((x) => x !== c.key))} />
                {c.label}
                {c.key.startsWith('cf.') ? <span className="caption"> · additional detail</span> : null}
              </label>
              {on && !fixed ? (
                <span className="row-flex" style={{ gap: 2 }}>
                  <Button variant="ghost" size="sm" iconOnly icon="chevronUp" aria-label={`Move ${c.label} left`} onClick={() => move(c.key, -1)} />
                  <Button variant="ghost" size="sm" iconOnly icon="chevronDown" aria-label={`Move ${c.label} right`} onClick={() => move(c.key, 1)} />
                </span>
              ) : null}
            </li>
          );
        })}
      </ul>
    </Modal>
  );
}
