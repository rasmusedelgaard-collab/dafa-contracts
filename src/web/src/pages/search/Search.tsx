import { Fragment, useEffect, useState } from 'react';
import { keepPreviousData, useMutation, useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { qk, useApi, useLookups } from '../../app/context';
import type { ContractHit, DocumentHit, DocumentKind, FullSearchQuery, SearchGroup, Snippet } from '../../api/types';
import { Avatar, Button, Card, ChipSelect, EmptyState, ErrorState, Field, Icon, Pill, Segmented, SkeletonRows, StatusDot, TextInput, useToast } from '../../components';
import { PersonLink, TagList } from '../../features/columns';
import { formatDate } from '../../lib/dates';
import { formatFileSize, plural, saveBlob } from '../../lib/format';
import { highlightRanges, segments, terms } from '../../lib/search';

const GROUPS: { value: SearchGroup | 'all'; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'contracts', label: 'Contracts' },
  { value: 'documents', label: 'Documents' },
  { value: 'people', label: 'People' },
  { value: 'counterparties', label: 'Counterparties' },
];
const DOC_KINDS: { value: DocumentKind; label: string }[] = [
  { value: 'Main', label: 'Main contract' },
  { value: 'Appendix', label: 'Appendix' },
  { value: 'Amendment', label: 'Amendment' },
  { value: 'ApprovalLog', label: 'Approval log' },
  { value: 'Other', label: 'Other' },
];
const MATCH_LABEL: Record<string, string> = { title: 'title', contractNo: 'contract no.', counterparty: 'counterparty', owner: 'owner', delegate: 'delegate', responsible: 'responsible', summary: 'summary', obligation: 'obligation', tag: 'tag', customField: 'additional details', name: 'file name', text: 'document text' };

/** Text with search terms marked. `snippet` comes from the API; plain text is highlighted here. */
export function Highlight({ text, q, snippet }: { text?: string; q?: string; snippet?: Snippet | null }) {
  const t = snippet?.text ?? text ?? '';
  const ranges = snippet ? snippet.highlights : highlightRanges(t, terms(q ?? ''));
  return (
    <>
      {segments(t, ranges).map((s, i) => (s.hit ? <mark key={i}>{s.text}</mark> : <Fragment key={i}>{s.text}</Fragment>))}
    </>
  );
}

function ContractRow({ h, q }: { h: ContractHit; q: string }) {
  const i = h.item;
  return (
    <li className="hit">
      <span className="tile" aria-hidden="true">
        <Icon name="doc" size={18} />
      </span>
      <div className="hit__body">
        <Link to={`/contracts/${i.id}`} className="hit__title">
          <Highlight text={i.title} q={q} />
        </Link>
        <span className="caption">
          <Highlight text={`${i.contractNo} · ${i.counterparty}`} q={q} /> · {i.entityName} · {i.departmentName}
        </span>
        {h.snippet ? (
          <p className="hit__snip">
            <Highlight snippet={h.snippet} />
          </p>
        ) : null}
        <span className="hit__meta">
          <PersonLink id={i.ownerId} name={i.ownerName} initials={i.ownerInitials} avatar />
          <StatusDot status={i.status} />
          <TagList tags={i.tags} />
          <span className="caption">Matched in {h.matchedIn.map((m) => MATCH_LABEL[m] ?? m).join(', ')}</span>
        </span>
      </div>
    </li>
  );
}

function DocumentRow({ d, onOpen, opening }: { d: DocumentHit; onOpen: (d: DocumentHit) => void; opening: boolean }) {
  return (
    <li className="hit">
      <span className="tile" aria-hidden="true">
        <Icon name="docPlus" size={18} />
      </span>
      <div className="hit__body">
        <span className="hit__title">{d.fileName}</span>
        <span className="caption">
          {d.kind === 'ApprovalLog' ? 'Approval log' : d.kind} · version {d.version} · {formatFileSize(d.size)} · uploaded {formatDate(d.uploadedAt.slice(0, 10))} by {d.uploadedBy}
        </span>
        {d.snippet ? (
          <p className="hit__snip">
            <Highlight snippet={d.snippet} />
          </p>
        ) : null}
        <span className="hit__meta">
          <Link to={`/contracts/${d.contractId}`}>
            {d.contractTitle} ({d.contractNo})
          </Link>
          <span className="caption">Owner {d.ownerName}</span>
        </span>
      </div>
      <Button variant="secondary" size="sm" icon="download" loading={opening} onClick={() => onOpen(d)} aria-label={`Download ${d.fileName}`}>
        Open
      </Button>
    </li>
  );
}

export function Search() {
  const api = useApi();
  const lookups = useLookups();
  const toast = useToast();
  const [params, setParams] = useSearchParams();
  const q = params.get('q') ?? '';
  const group = (params.get('group') ?? 'all') as SearchGroup | 'all';
  const page = Math.max(1, Number(params.get('page')) || 1);
  const [text, setText] = useState(q);
  useEffect(() => setText(q), [q]);

  const set = (changes: Record<string, string | null>) => {
    const next = new URLSearchParams(params);
    next.delete('page');
    for (const [k, v] of Object.entries(changes)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    setParams(next, { replace: true });
  };
  useEffect(() => {
    if (text === q) return;
    const t = window.setTimeout(() => set({ q: text.trim() || null }), 300);
    return () => window.clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [text]);

  const query: FullSearchQuery = {
    q,
    group: group === 'all' ? undefined : group,
    personId: params.get('person') || undefined,
    entityId: params.get('entity') || undefined,
    departmentId: params.get('department') || undefined,
    typeId: params.get('type') || undefined,
    documentKind: (params.get('kind') as DocumentKind | null) || undefined,
    uploadedFrom: params.get('from') || undefined,
    uploadedTo: params.get('to') || undefined,
    page,
    pageSize: group === 'all' ? 8 : 25,
  };
  const enabled = q.trim().length >= 2;
  const result = useQuery({ queryKey: qk.searchFull(query), queryFn: () => api.searchFull(query), enabled, placeholderData: keepPreviousData });
  const open = useMutation({
    mutationFn: (d: DocumentHit) => api.getDocumentContent(d.documentId),
    onSuccess: (f) => saveBlob(f.blob, f.fileName),
    onError: (e) => toast.error(e),
  });

  const r = result.data;
  const counts = r ? { contracts: r.contracts.total, documents: r.documents.total, people: r.people.total, counterparties: r.counterparties.total } : null;
  const total = counts ? Object.values(counts).reduce((a, b) => a + b, 0) : 0;
  const refs = (xs: { id: string; name: string }[]) => xs.map((x) => ({ value: x.id, label: x.name }));
  const show = (g: SearchGroup) => group === 'all' || group === g;
  const more = (g: SearchGroup, n: number, shown: number) =>
    group === 'all' && n > shown ? (
      <button type="button" className="link-btn hit-more" onClick={() => set({ group: g })}>
        Show all {n}
      </button>
    ) : null;
  const pageSize = query.pageSize ?? 25;
  const groupTotal = group !== 'all' && counts ? counts[group] : 0;

  return (
    <div className="page rise" style={{ paddingTop: 10 }}>
      <div className="page-head">
        <div className="page-head__text">
          <h1 className="h1">Search</h1>
          <span className="page-sub">Contracts, documents and people you have access to, including contracts owned by others.</span>
        </div>
      </div>
      <form
        className="search-hero"
        role="search"
        onSubmit={(e) => {
          e.preventDefault();
          set({ q: text.trim() || null });
        }}
      >
        <label className="search-hero__box">
          <Icon name="search" size={20} stroke={2} />
          <span className="sr-only">Search for</span>
          <input type="search" autoFocus value={text} placeholder="Title, contract no., counterparty, person, file name or words in a document" onChange={(e) => setText(e.target.value)} />
        </label>
        <Button type="submit">Search</Button>
      </form>
      <Segmented ariaLabel="Result type" options={GROUPS.map((g) => ({ ...g, label: counts && g.value !== 'all' ? `${g.label} (${counts[g.value]})` : g.label }))} value={group} onChange={(v) => set({ group: v === 'all' ? null : v })} />
      <div className="toolbar toolbar--chips" role="group" aria-label="Filters">
        <ChipSelect label="Person" options={lookups.users.filter((u) => u.isActive !== false).map((u) => ({ value: u.id, label: u.name }))} value={params.get('person') ?? ''} onChange={(v) => set({ person: v })} />
        <ChipSelect label="Entity" options={refs(lookups.entities)} value={params.get('entity') ?? ''} onChange={(v) => set({ entity: v })} />
        <ChipSelect label="Department" options={refs(lookups.departments)} value={params.get('department') ?? ''} onChange={(v) => set({ department: v })} />
        <ChipSelect label="Type" options={refs(lookups.contractTypes)} value={params.get('type') ?? ''} onChange={(v) => set({ type: v })} />
        <ChipSelect label="Document" options={DOC_KINDS} value={params.get('kind') ?? ''} onChange={(v) => set({ kind: v })} />
        <span className="daterange daterange--inline">
          <Field label="Uploaded from">{(p) => <TextInput {...p} type="date" value={params.get('from') ?? ''} onChange={(e) => set({ from: e.target.value })} />}</Field>
          <Field label="to">{(p) => <TextInput {...p} type="date" value={params.get('to') ?? ''} onChange={(e) => set({ to: e.target.value })} />}</Field>
        </span>
      </div>

      {!enabled ? (
        <Card>
          <EmptyState icon="search" title="Type at least two characters" text="Search finds contracts by title, number, counterparty, owner, delegate or responsible, obligations and tags, and documents by file name and the words in them." />
        </Card>
      ) : result.isError ? (
        <Card>
          <ErrorState text="Search did not answer." onRetry={() => void result.refetch()} />
        </Card>
      ) : !r ? (
        <Card>
          <SkeletonRows rows={6} label="Searching" />
        </Card>
      ) : total === 0 ? (
        <Card>
          <EmptyState icon="search" title={`Nothing matches “${q}”`} text="Every word must match. Try fewer words, or remove a filter." />
        </Card>
      ) : (
        <div className={`search-results${group === 'all' ? ' search-results--all' : ''}`} aria-live="polite" style={{ opacity: result.isPlaceholderData ? 0.6 : 1 }}>
          {show('contracts') && (group !== 'all' || r.contracts.total > 0) ? (
            <Card clip className="search-group" aria-labelledby="g-contracts">
              <div className="card-list-head card-list-head--row">
                <h2 className="card-title" id="g-contracts">
                  Contracts <span className="caption">{r.contracts.total}</span>
                </h2>
              </div>
              {r.contracts.items.length ? (
                <ol className="hits">{r.contracts.items.map((h) => <ContractRow key={h.item.id} h={h} q={q} />)}</ol>
              ) : (
                <EmptyState compact title="No contracts" />
              )}
              {more('contracts', r.contracts.total, r.contracts.items.length)}
            </Card>
          ) : null}
          {show('documents') && (group !== 'all' || r.documents.total > 0) ? (
            <Card clip className="search-group" aria-labelledby="g-docs">
              <div className="card-list-head card-list-head--row">
                <h2 className="card-title" id="g-docs">
                  Documents <span className="caption">{r.documents.total}</span>
                </h2>
              </div>
              {r.documents.items.length ? (
                <ol className="hits">{r.documents.items.map((d) => <DocumentRow key={d.documentId} d={d} onOpen={(x) => open.mutate(x)} opening={open.isPending && open.variables?.documentId === d.documentId} />)}</ol>
              ) : (
                <EmptyState compact title="No documents" />
              )}
              {more('documents', r.documents.total, r.documents.items.length)}
            </Card>
          ) : null}
          {show('people') && (group !== 'all' || r.people.total > 0) ? (
            <Card clip className="search-group" aria-labelledby="g-people">
              <div className="card-list-head card-list-head--row">
                <h2 className="card-title" id="g-people">
                  People <span className="caption">{r.people.total}</span>
                </h2>
              </div>
              <ol className="hits">
                {r.people.items.map((p) => (
                  <li key={p.id} className="hit">
                    <Avatar initials={p.initials} size="lg" />
                    <div className="hit__body">
                      <Link to={`/people/${p.id}`} className="hit__title">
                        <Highlight text={p.name} q={q} />
                      </Link>
                      <span className="caption">{[p.title?.split(' · ')[0], ...p.departmentNames].filter(Boolean).join(' · ')}</span>
                    </div>
                    <Pill tone="blue">{plural(p.contractCount, 'contract')}</Pill>
                  </li>
                ))}
              </ol>
              {more('people', r.people.total, r.people.items.length)}
            </Card>
          ) : null}
          {show('counterparties') && (group !== 'all' || r.counterparties.total > 0) ? (
            <Card clip className="search-group" aria-labelledby="g-cp">
              <div className="card-list-head card-list-head--row">
                <h2 className="card-title" id="g-cp">
                  Counterparties <span className="caption">{r.counterparties.total}</span>
                </h2>
              </div>
              <ol className="hits">
                {r.counterparties.items.map((c) => (
                  <li key={c.id} className="hit">
                    <span className="tile" aria-hidden="true">
                      <Icon name="building" size={18} />
                    </span>
                    <div className="hit__body">
                      <Link to={`/counterparties/${c.id}`} className="hit__title">
                        <Highlight text={c.name} q={q} />
                      </Link>
                      <span className="caption">{c.country ?? 'Country not given'}</span>
                    </div>
                    <Pill tone="gray">{plural(c.contractCount, 'contract')}</Pill>
                  </li>
                ))}
              </ol>
              {more('counterparties', r.counterparties.total, r.counterparties.items.length)}
            </Card>
          ) : null}
          {group !== 'all' && groupTotal > pageSize ? (
            <div className="pager">
              <span>
                {(page - 1) * pageSize + 1}–{Math.min(page * pageSize, groupTotal)} of {groupTotal}
              </span>
              <div className="spacer" />
              <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => { const n = new URLSearchParams(params); n.set('page', String(page - 1)); setParams(n); }}>
                Previous
              </Button>
              <Button variant="secondary" size="sm" disabled={page * pageSize >= groupTotal} onClick={() => { const n = new URLSearchParams(params); n.set('page', String(page + 1)); setParams(n); }}>
                Next
              </Button>
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}
