// Release 3 parts of the contract record: watch, tags, additional details, comments with
// @mentions, and the comparison of two versions of the key terms.
import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi, useAppConfig, useInvalidateContracts, useLookups, useMe } from '../../app/context';
import type { Comment, ContractDetail } from '../../api/types';
import { Avatar, Button, Card, CardTitle, EmptyState, ErrorState, Field, Modal, Select, SkeletonRows, Textarea, useToast } from '../../components';
import { PersonLink, TagList } from '../../features/columns';
import { CustomFieldValues, TagPicker } from '../../features/CustomFields';
import { fieldsForType } from '../../lib/customFields';
import { formatDate, formatDateTime } from '../../lib/dates';

export function WatchButton({ c }: { c: ContractDetail }) {
  const api = useApi();
  const toast = useToast();
  const client = useQueryClient();
  const watching = !!c.watching;
  const toggle = useMutation({
    mutationFn: () => (watching ? api.unwatchContract(c.id) : api.watchContract(c.id)),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['contracts'] });
      toast.show(watching ? 'You no longer watch this contract' : 'Watching. Changes show up in your notifications and on My work.');
    },
    onError: (e) => toast.error(e),
  });
  return (
    <Button variant={watching ? 'tint' : 'secondary'} icon="eye" aria-pressed={watching} loading={toggle.isPending} onClick={() => toggle.mutate()} title={c.watcherCount ? `${c.watcherCount} watching` : undefined}>
      {watching ? 'Watching' : 'Watch'}
    </Button>
  );
}

export function TagsCard({ c }: { c: ContractDetail }) {
  const api = useApi();
  const config = useAppConfig();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const [editing, setEditing] = useState(false);
  const [ids, setIds] = useState<string[]>([]);
  const save = useMutation({
    mutationFn: () => api.setContractTags(c.id, ids),
    onSuccess: async () => {
      await invalidate();
      setEditing(false);
      toast.show('Tags saved');
    },
    onError: (e) => toast.error(e),
  });
  const canTag = c.canTag ?? c.canEdit;
  if (!canTag && !c.tags?.length) return null;
  return (
    <Card pad className="stack" style={{ gap: 10 }}>
      <CardTitle
        action={
          canTag && !editing && config.tags.length ? (
            <button
              type="button"
              className="link-btn"
              onClick={() => {
                setIds((c.tags ?? []).map((t) => t.id));
                setEditing(true);
              }}
            >
              Edit
            </button>
          ) : undefined
        }
      >
        Tags
      </CardTitle>
      {editing ? (
        <>
          <TagPicker label="Choose tags" tags={config.tags} value={ids} onChange={setIds} />
          <div className="row-flex" style={{ gap: 8 }}>
            <Button size="sm" loading={save.isPending} onClick={() => save.mutate()}>
              Save
            </Button>
            <Button size="sm" variant="secondary" onClick={() => setEditing(false)}>
              Cancel
            </Button>
          </div>
        </>
      ) : c.tags?.length ? (
        <TagList tags={c.tags} max={10} />
      ) : (
        <span className="secondary">No tags.</span>
      )}
    </Card>
  );
}

export function AdditionalDetails({ c, onEdit }: { c: ContractDetail; onEdit?: () => void }) {
  const config = useAppConfig();
  const defs = fieldsForType(config.customFields, c.typeId);
  if (defs.length === 0) return null;
  return (
    <Card pad className="stack" style={{ gap: 12 }} aria-labelledby="extra-heading">
      <CardTitle
        id="extra-heading"
        action={
          onEdit ? (
            <button type="button" className="link-btn" onClick={onEdit}>
              Edit
            </button>
          ) : undefined
        }
      >
        Additional details
      </CardTitle>
      <CustomFieldValues defs={defs} values={c.customFields} />
    </Card>
  );
}

/** Renders a comment body with the mentioned names emphasised. */
function Body({ comment }: { comment: Comment }) {
  const names = comment.mentions.map((m) => `@${m.name}`);
  if (names.length === 0) return <>{comment.body}</>;
  const re = new RegExp(`(${names.map((n) => n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})`, 'g');
  return (
    <>
      {comment.body.split(re).map((part, i) =>
        names.includes(part) ? (
          <strong key={i} className="mention">
            {part}
          </strong>
        ) : (
          <span key={i}>{part}</span>
        ),
      )}
    </>
  );
}

export function CommentsTab({ c }: { c: ContractDetail }) {
  const api = useApi();
  const me = useMe();
  const lookups = useLookups();
  const toast = useToast();
  const client = useQueryClient();
  const comments = useQuery({ queryKey: qk.comments(c.id), queryFn: () => api.getComments(c.id) });
  const [text, setText] = useState('');
  const [error, setError] = useState<string>();
  const [editing, setEditing] = useState<string | null>(null);
  const [mentionQuery, setMentionQuery] = useState<string | null>(null);
  const [active, setActive] = useState(0);
  const ref = useRef<HTMLTextAreaElement>(null);
  const people = lookups.users.filter((u) => u.isActive !== false && u.id !== me.id);
  const matches = useMemo(() => (mentionQuery === null ? [] : people.filter((u) => u.name.toLowerCase().includes(mentionQuery.toLowerCase())).slice(0, 6)), [mentionQuery, people]);

  useEffect(() => {
    if (window.location.hash === '#comments') ref.current?.focus();
  }, []);

  const refresh = () => Promise.all([client.invalidateQueries({ queryKey: qk.comments(c.id) }), client.invalidateQueries({ queryKey: qk.contract(c.id) })]);
  const mentionIds = (body: string) => people.filter((u) => body.includes(`@${u.name}`)).map((u) => u.id);
  const add = useMutation({
    mutationFn: () => (editing ? api.updateComment(editing, { body: text.trim(), mentionIds: mentionIds(text) }) : api.addComment(c.id, { body: text.trim(), mentionIds: mentionIds(text) })),
    onSuccess: async (saved) => {
      setText('');
      setEditing(null);
      await refresh();
      toast.show(saved.mentions.length ? `Comment saved. ${saved.mentions.map((m) => m.name).join(' and ')} ${saved.mentions.length === 1 ? 'is' : 'are'} notified.` : 'Comment saved');
    },
    onError: (e) => {
      const msg = e instanceof Error ? e.message : undefined;
      setError(msg);
      toast.error(e);
    },
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.deleteComment(id),
    onSuccess: refresh,
    onError: (e) => toast.error(e),
  });

  const onType = (value: string, caret: number) => {
    setText(value);
    setError(undefined);
    const before = value.slice(0, caret);
    const m = /(^|\s)@([\p{L} ]{0,30})$/u.exec(before);
    setMentionQuery(m ? (m[2] ?? '') : null);
    setActive(0);
  };
  const insert = (name: string) => {
    const el = ref.current;
    const caret = el?.selectionStart ?? text.length;
    const before = text.slice(0, caret).replace(/@([\p{L} ]{0,30})$/u, `@${name} `);
    const next = before + text.slice(caret);
    setText(next);
    setMentionQuery(null);
    window.setTimeout(() => {
      el?.focus();
      el?.setSelectionRange(before.length, before.length);
    }, 0);
  };

  return (
    <div className="split">
      <Card className="tab-card split__main" id="comments" aria-labelledby="comments-heading">
        <h2 className="h2" id="comments-heading" style={{ marginBottom: 8 }}>
          Comments
        </h2>
        {comments.isLoading ? (
          <SkeletonRows rows={3} label="Loading comments" />
        ) : comments.isError ? (
          <ErrorState onRetry={() => void comments.refetch()} />
        ) : (comments.data ?? []).length === 0 ? (
          <EmptyState compact title="No comments yet" text="Ask a question or leave a note for the people on this contract. Type @ to mention someone." />
        ) : (
          <ol className="comments">
            {comments.data?.map((x) => (
              <li key={x.id} className="comment">
                <Avatar initials={x.authorInitials} />
                <div className="comment__body">
                  <div className="comment__head">
                    <PersonLink id={x.authorId} name={x.authorName} />
                    <span className="caption">
                      {formatDateTime(x.createdAt)}
                      {x.editedAt ? ' · edited' : ''}
                    </span>
                  </div>
                  <p className="comment__text prose">
                    <Body comment={x} />
                  </p>
                  {x.canEdit || x.canDelete ? (
                    <div className="row-flex" style={{ gap: 12 }}>
                      {x.canEdit ? (
                        <button
                          type="button"
                          className="link-btn"
                          onClick={() => {
                            setEditing(x.id);
                            setText(x.body);
                            ref.current?.focus();
                          }}
                        >
                          Edit
                        </button>
                      ) : null}
                      {x.canDelete ? (
                        <button type="button" className="link-btn" onClick={() => remove.mutate(x.id)}>
                          Delete
                        </button>
                      ) : null}
                    </div>
                  ) : null}
                </div>
              </li>
            ))}
          </ol>
        )}
        <form
          className="comment-form"
          onSubmit={(e) => {
            e.preventDefault();
            if (!text.trim()) return setError('Write a comment first');
            add.mutate();
          }}
        >
          <div className="mention-box">
            <Field label={editing ? 'Edit your comment' : 'Add a comment'} error={error} hint="Type @ and a name to mention someone. Only people who can see this contract can be mentioned.">
              {(p) => (
                <Textarea
                  {...p}
                  ref={ref}
                  rows={3}
                  value={text}
                  role="combobox"
                  aria-expanded={matches.length > 0}
                  aria-autocomplete="list"
                  aria-controls="mention-list"
                  onChange={(e) => onType(e.target.value, e.target.selectionStart)}
                  onKeyDown={(e) => {
                    if (!matches.length) return;
                    if (e.key === 'ArrowDown') {
                      e.preventDefault();
                      setActive((active + 1) % matches.length);
                    } else if (e.key === 'ArrowUp') {
                      e.preventDefault();
                      setActive((active - 1 + matches.length) % matches.length);
                    } else if (e.key === 'Enter' || e.key === 'Tab') {
                      e.preventDefault();
                      const m = matches[active];
                      if (m) insert(m.name);
                    } else if (e.key === 'Escape') setMentionQuery(null);
                  }}
                />
              )}
            </Field>
            {matches.length ? (
              <ul className="mention-list" id="mention-list" role="listbox" aria-label="People">
                {matches.map((u, i) => (
                  <li key={u.id} role="option" aria-selected={i === active} className={i === active ? 'is-active' : undefined} onMouseDown={(e) => e.preventDefault()} onClick={() => insert(u.name)}>
                    <Avatar initials={u.initials} size="sm" /> {u.name}
                  </li>
                ))}
              </ul>
            ) : null}
          </div>
          <div className="row-flex" style={{ gap: 8 }}>
            <Button type="submit" loading={add.isPending}>
              {editing ? 'Save comment' : 'Post comment'}
            </Button>
            {editing ? (
              <Button
                variant="secondary"
                onClick={() => {
                  setEditing(null);
                  setText('');
                }}
              >
                Cancel
              </Button>
            ) : null}
          </div>
        </form>
      </Card>
      <aside className="split__aside">
        <Card pad tone="tint" className="stack" style={{ gap: 6 }}>
          <span style={{ fontWeight: 700 }}>Who is notified</span>
          <span className="secondary" style={{ fontSize: 13 }}>
            People you mention get a notification in the app (and an e-mail if they have it on). Everyone watching the contract sees that a comment was added.
          </span>
        </Card>
      </aside>
    </div>
  );
}

/** Plain numbers from the API ("8400000") are shown with separators. */
const showValue = (v: string | null) => (v === null ? '–' : /^-?\d+(\.\d+)?$/.test(v) ? new Intl.NumberFormat('en-US').format(Number(v)) : v);

export function CompareDialog({ c, onClose }: { c: ContractDetail; onClose: () => void }) {
  const api = useApi();
  const versions = useQuery({ queryKey: qk.versions(c.id), queryFn: () => api.getContractVersions(c.id) });
  const list = versions.data ?? [];
  const [from, setFrom] = useState<string>('');
  const [to, setTo] = useState('current');
  const fromId = from || list[list.length - 1]?.id || 'created';
  const cmp = useQuery({ queryKey: qk.compare(c.id, fromId, to), queryFn: () => api.compareContract(c.id, fromId, to), enabled: list.length > 0 });
  const [onlyChanged, setOnlyChanged] = useState(true);
  const options = list.map((v) => ({ value: v.id, label: v.id === 'current' ? 'Current' : `${formatDate(v.at.slice(0, 10))} · ${v.label}` }));
  const rows = (cmp.data?.fields ?? []).filter((f) => !onlyChanged || f.changed);
  return (
    <Modal wide title="Compare versions" subtitle="Key terms as they were after each recorded change, from the audit history." onClose={onClose} footer={<Button onClick={onClose}>Close</Button>}>
      {versions.isLoading ? (
        <SkeletonRows rows={4} label="Loading versions" />
      ) : versions.isError ? (
        <ErrorState onRetry={() => void versions.refetch()} />
      ) : (
        <div className="stack" style={{ gap: 14 }}>
          <div className="form-grid form-grid--flat">
            <Field label="From">{(p) => <Select {...p} value={fromId} options={options} onChange={(e) => setFrom(e.target.value)} />}</Field>
            <Field label="To">{(p) => <Select {...p} value={to} options={options} onChange={(e) => setTo(e.target.value)} />}</Field>
          </div>
          <label className="check">
            <input type="checkbox" checked={onlyChanged} onChange={(e) => setOnlyChanged(e.target.checked)} />
            Only show what changed
          </label>
          {cmp.isLoading ? (
            <SkeletonRows rows={5} />
          ) : rows.length === 0 ? (
            <EmptyState compact title={list.length <= 2 && fromId === to ? 'Choose two different versions' : 'No differences in the key terms'} />
          ) : (
            <div className="table-wrap">
              <table className="table compare">
                <caption className="sr-only">Key terms compared</caption>
                <thead>
                  <tr>
                    <th scope="col">Term</th>
                    <th scope="col">{options.find((o) => o.value === fromId)?.label ?? 'From'}</th>
                    <th scope="col">{options.find((o) => o.value === to)?.label ?? 'To'}</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((f) => (
                    <tr key={f.field} className={f.changed ? 'compare__changed' : undefined}>
                      <th scope="row">{f.label}</th>
                      <td>{showValue(f.from)}</td>
                      <td>
                        {showValue(f.to)}
                        {f.changed ? <span className="sr-only"> (changed)</span> : null}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </Modal>
  );
}
