// "Start from a template" in New contract (owner item E): categories as filter chips, a search
// box, each template with its description, category, language and contract types, and a preview
// of the fields it fills in.
import { useId, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { qk, useApi, useLookups } from '../app/context';
import type { Template } from '../api/types';
import { Button, EmptyState, ErrorState, Skeleton, TextInput } from '../components';

const LANGUAGE: Record<string, string> = { da: 'Danish', en: 'English', sv: 'Swedish', de: 'German', pl: 'Polish', it: 'Italian', zh: 'Chinese' };

export function TemplateChooser({ disabled, onPick }: { disabled?: boolean; onPick: (t: Template) => void }) {
  const api = useApi();
  const lookups = useLookups();
  const templates = useQuery({ queryKey: qk.templates, queryFn: () => api.getTemplates() });
  const categories = useQuery({ queryKey: [...qk.templates, 'categories'], queryFn: () => api.getTemplateCategories(), staleTime: 60_000 });
  const [category, setCategory] = useState<string>('all');
  const [q, setQ] = useState('');
  const [preview, setPreview] = useState<string | null>(null);
  const searchId = useId();
  const all = useMemo(() => templates.data ?? [], [templates.data]);
  const shown = all.filter((t) => (category === 'all' || (category === 'none' ? !t.categoryId : t.categoryId === category)) && (!q.trim() || `${t.name} ${t.description}`.toLowerCase().includes(q.trim().toLowerCase())));
  const cats = (categories.data ?? []).filter((c) => all.some((t) => t.categoryId === c.id));
  const uncategorised = all.some((t) => !t.categoryId);
  const typeNames = (t: Template) => {
    const ids = t.typeIds?.length ? t.typeIds : t.typeId ? [t.typeId] : [];
    return ids.length ? ids.map((id) => lookups.contractTypes.find((x) => x.id === id)?.name ?? '').filter(Boolean).join(', ') : 'Any contract type';
  };

  if (templates.isLoading)
    return (
      <div className="stack" style={{ gap: 8 }} role="status" aria-label="Loading templates">
        <Skeleton height={62} radius={12} />
        <Skeleton height={62} radius={12} />
      </div>
    );
  if (templates.isError) return <ErrorState text="The templates could not be loaded." onRetry={() => void templates.refetch()} />;
  if (all.length === 0) return <EmptyState compact title="No templates yet" text="An administrator can add templates in Settings." />;

  return (
    <div className="stack" style={{ gap: 10 }}>
      {cats.length > 0 ? (
        <div className="chip-row" role="group" aria-label="Template category">
          {[{ id: 'all', name: 'All', templateCount: all.length }, ...cats.map((c) => ({ id: c.id, name: c.name, templateCount: all.filter((t) => t.categoryId === c.id).length })), ...(uncategorised ? [{ id: 'none', name: 'Other', templateCount: all.filter((t) => !t.categoryId).length }] : [])].map((c) => (
            <button key={c.id} type="button" className={`chip${category === c.id ? ' chip--on' : ''}`} aria-pressed={category === c.id} onClick={() => setCategory(c.id)}>
              {c.name} <span className="caption">{c.templateCount}</span>
            </button>
          ))}
        </div>
      ) : null}
      {all.length > 4 || cats.length > 0 ? (
        <>
          <label className="sr-only" htmlFor={searchId}>
            Search templates
          </label>
          <TextInput id={searchId} type="search" placeholder="Search templates" value={q} onChange={(e) => setQ(e.target.value)} />
        </>
      ) : null}
      {shown.length === 0 ? <EmptyState compact title="No template matches" /> : null}
      {shown.map((t) => (
        <div key={t.id} className="template-item">
          <button type="button" className="template-opt" disabled={disabled} onClick={() => onPick(t)}>
            <span className="tile tone-blue" aria-hidden="true">
              DOC
            </span>
            <span className="kv">
              <span className="kv__v" id={`${searchId}-${t.id}`}>
                {t.name}
              </span>
              <span className="kv__k">{t.description}</span>
              <span className="kv__k">{[t.categoryName, t.language ? LANGUAGE[t.language] : null, typeNames(t)].filter(Boolean).join(' · ')}</span>
            </span>
          </button>
          <Button variant="ghost" size="sm" aria-expanded={preview === t.id} aria-describedby={`${searchId}-${t.id}`} onClick={() => setPreview(preview === t.id ? null : t.id)}>
            {preview === t.id ? 'Hide preview' : 'Preview'}
          </Button>
          {preview === t.id ? <TemplatePreview id={t.id} /> : null}
        </div>
      ))}
    </div>
  );
}

function TemplatePreview({ id }: { id: string }) {
  const api = useApi();
  const p = useQuery({ queryKey: [...qk.templates, 'preview', id], queryFn: () => api.getTemplatePreview(id), staleTime: 60_000 });
  if (p.isLoading) return <Skeleton height={80} radius={12} />;
  if (p.isError || !p.data) return <ErrorState text="The preview could not be loaded." onRetry={() => void p.refetch()} />;
  return (
    <div className="template-preview">
      <span className="caption">Fields filled in from the form</span>
      <ul className="template-preview__fields">
        {p.data.placeholders.map((f) => (
          <li key={f.name}>
            <code>{`{{${f.name}}}`}</code> <span>{f.label}</span>
            {f.sample ? <span className="caption"> e.g. {f.sample}</span> : null}
          </li>
        ))}
      </ul>
      {p.data.excerpt ? <pre className="template-preview__text">{p.data.excerpt}</pre> : null}
    </div>
  );
}
