// Type-ahead pickers (release 4, owner item C). The user types part of a name and must click (or
// press Enter on) a match: there is no free text. Used for every person and counterparty choice.
import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi, useAppConfig, useLookups } from '../app/context';
import { ApiError } from '../api/api';
import type { Counterparty, CounterpartyCreateInput, LookupUser } from '../api/types';
import { Avatar, Button, Field, Icon, Modal, Select, TextInput, useToast } from '../components';
import { problemErrors } from '../pages/settings/Reference';

export interface PickOption {
  id: string;
  label: string;
  sub?: string;
  initials?: string;
}

interface ComboboxProps {
  /** From `Field`: id, aria-invalid, aria-describedby. */
  id?: string;
  'aria-invalid'?: true;
  'aria-describedby'?: string;
  /** Accessible name when there is no visible label (filters). */
  ariaLabel?: string;
  value: PickOption | null;
  onChange: (option: PickOption | null) => void;
  /** Options for the typed text (already filtered). */
  options: (query: string) => PickOption[];
  loading?: boolean;
  placeholder?: string;
  disabled?: boolean;
  suggested?: boolean;
  /** Text to start with when nothing is selected (e.g. an AI-suggested name to match). */
  initialQuery?: string;
  /** A last row such as "Create new counterparty". */
  footer?: { label: string; onSelect: (query: string) => void };
  onQuery?: (query: string) => void;
  /** Smaller pill style, for filter bars. */
  compact?: boolean;
  clearLabel?: string;
}

/**
 * WAI-ARIA combobox with a listbox popup. Typing filters, ArrowUp/Down move, Enter picks,
 * Escape closes and restores the chosen value. Leaving the field without picking restores the
 * chosen value too: free text is never kept.
 */
export function Combobox(props: ComboboxProps) {
  const { value, onChange, options, loading, placeholder, disabled, suggested, initialQuery, footer, onQuery, compact, ariaLabel, clearLabel } = props;
  const autoId = useId();
  const inputId = props.id ?? `${autoId}-input`;
  const listId = `${autoId}-list`;
  const [query, setQuery] = useState(value ? '' : (initialQuery ?? ''));
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const editing = open || (!value && query !== '');
  const shown = useMemo(() => (open ? options(query) : []), [open, options, query]);
  const rows = shown.length + (footer ? 1 : 0);

  useEffect(() => onQuery?.(query), [query, onQuery]);
  // A value set from outside (accepting a suggestion, a dialog) replaces what was being typed.
  const valueId = value?.id ?? null;
  const [lastId, setLastId] = useState(valueId);
  if (valueId !== lastId) {
    setLastId(valueId);
    if (valueId) setQuery('');
  }

  const pick = (o: PickOption | null) => {
    onChange(o);
    setQuery('');
    setOpen(false);
  };
  const choose = (i: number) => {
    if (i < shown.length) pick(shown[i] ?? null);
    else if (footer) {
      setOpen(false);
      footer.onSelect(query);
    }
  };
  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      if (!open) setOpen(true);
      else setActive((a) => Math.min(rows - 1, a + 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActive((a) => Math.max(0, a - 1));
    } else if (e.key === 'Enter') {
      if (open && rows > 0) {
        e.preventDefault();
        choose(active);
      }
    } else if (e.key === 'Escape') {
      if (open) {
        e.preventDefault();
        e.stopPropagation();
      }
      setOpen(false);
      setQuery('');
    }
  };

  return (
    <div className={`combo${compact ? ' combo--compact' : ''}${value ? ' combo--set' : ''}`}>
      <div className="combo__box">
        {!compact ? (
          <span className="combo__icon" aria-hidden="true">
            <Icon name="search" size={15} stroke={2.2} />
          </span>
        ) : null}
        <input
          ref={inputRef}
          id={inputId}
          className={`input combo__input${props['aria-invalid'] ? ' input--invalid' : ''}${suggested ? ' input--suggested' : ''}`}
          role="combobox"
          aria-label={ariaLabel}
          aria-autocomplete="list"
          aria-expanded={open}
          aria-controls={listId}
          aria-activedescendant={open && rows > 0 ? `${listId}-${active}` : undefined}
          aria-invalid={props['aria-invalid']}
          aria-describedby={props['aria-describedby']}
          autoComplete="off"
          disabled={disabled}
          placeholder={value ? value.label : (placeholder ?? 'Type to search')}
          value={editing ? query : (value?.label ?? '')}
          onChange={(e) => {
            setQuery(e.target.value);
            setActive(0);
            setOpen(true);
          }}
          onFocus={() => {
            if (!disabled) setOpen(true);
          }}
          onClick={() => !disabled && setOpen(true)}
          onBlur={() => {
            // Leave time for a click on an option (mousedown keeps focus, see below).
            // No free text: what was typed without picking is dropped (an AI-suggested name stays
            // as the starting search until something is picked).
            setOpen(false);
            setQuery(value ? '' : query === (initialQuery ?? '') ? query : '');
          }}
          onKeyDown={onKey}
        />
        {value && !disabled ? (
          <button type="button" className="combo__clear" aria-label={clearLabel ?? `Clear ${ariaLabel ?? 'choice'}`} onClick={() => { pick(null); inputRef.current?.focus(); }}>
            <Icon name="x" size={12} stroke={2.6} />
          </button>
        ) : null}
      </div>
      {open ? (
        <ul id={listId} role="listbox" className="combo__list" aria-label={ariaLabel ? `${ariaLabel} options` : 'Options'}>
          {loading && shown.length === 0 ? <li className="combo__empty">Searching…</li> : null}
          {!loading && shown.length === 0 ? <li className="combo__empty">{query.trim() ? 'No match. Check the spelling.' : 'Type to search'}</li> : null}
          {shown.map((o, i) => (
            <li
              key={o.id}
              id={`${listId}-${i}`}
              role="option"
              aria-selected={i === active}
              className={`combo__opt${i === active ? ' combo__opt--active' : ''}`}
              onMouseDown={(e) => e.preventDefault()}
              onMouseEnter={() => setActive(i)}
              onClick={() => choose(i)}
            >
              {o.initials ? <Avatar initials={o.initials} size="sm" /> : null}
              <span className="stack" style={{ gap: 0, minWidth: 0 }}>
                <span className="truncate">{o.label}</span>
                {o.sub ? <span className="caption truncate">{o.sub}</span> : null}
              </span>
            </li>
          ))}
          {footer ? (
            <li
              id={`${listId}-${shown.length}`}
              role="option"
              aria-selected={active === shown.length}
              className={`combo__opt combo__opt--footer${active === shown.length ? ' combo__opt--active' : ''}`}
              onMouseDown={(e) => e.preventDefault()}
              onMouseEnter={() => setActive(shown.length)}
              onClick={() => choose(shown.length)}
            >
              <Icon name="plus" size={14} stroke={2.4} />
              <span>{footer.label}</span>
            </li>
          ) : null}
        </ul>
      ) : null}
    </div>
  );
}

// ----- People -----

function personSub(u: LookupUser, lookups: ReturnType<typeof useLookups>): string {
  const dept = u.departmentIds[0] ? lookups.departments.find((d) => d.id === u.departmentIds[0])?.name : null;
  const entity = u.entityId ? lookups.entities.find((e) => e.id === u.entityId)?.name : null;
  return [dept, entity].filter(Boolean).join(' · ') || (u.title ?? '').split(' · ')[0] || '';
}

const match = (text: string, q: string) => {
  const t = text.toLowerCase();
  return q
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean)
    .every((w) => t.includes(w));
};

export interface PersonPickerProps extends Omit<ComboboxProps, 'value' | 'onChange' | 'options'> {
  value: string | null | undefined;
  onChange: (id: string | null) => void;
  /** Extra rule for who may be chosen (e.g. only approvers). */
  filter?: (u: LookupUser) => boolean;
  /** List inactive users too (reassigning the contracts of someone who left). Hidden otherwise. */
  includeInactive?: boolean;
}

/** Person type-ahead over the users in lookups (name, e-mail, initials; shows department and entity). */
export function PersonPicker({ value, onChange, filter, includeInactive, ...rest }: PersonPickerProps) {
  const lookups = useLookups();
  const current = value ? lookups.users.find((u) => u.id === value) : undefined;
  const selected: PickOption | null = value ? { id: value, label: current ? `${current.name}${current.isActive === false ? ' (inactive)' : ''}` : 'Unknown person', initials: current?.initials } : null;
  const options = useMemo(
    () => (q: string) =>
      lookups.users
        .filter((u) => (includeInactive || u.isActive !== false) && (!filter || filter(u)))
        .filter((u) => !q.trim() || match(`${u.name} ${u.email ?? ''} ${u.initials}`, q))
        .sort((a, b) => a.name.localeCompare(b.name))
        .slice(0, 20)
        .map((u) => ({ id: u.id, label: `${u.name}${u.isActive === false ? ' (inactive)' : ''}`, sub: personSub(u, lookups), initials: u.initials })),
    [lookups, filter, includeInactive],
  );
  return <Combobox {...rest} placeholder={rest.placeholder ?? 'Type a name'} value={selected} onChange={(o) => onChange(o?.id ?? null)} options={options} />;
}

// ----- Counterparties -----

export interface CounterpartyValue {
  supplierId: string | null;
  name: string;
}

function useDebounced<T>(value: T, ms: number): T {
  const [d, setD] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setD(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return d;
}

export interface CounterpartyPickerProps extends Omit<ComboboxProps, 'value' | 'onChange' | 'options' | 'footer' | 'initialQuery'> {
  value: CounterpartyValue;
  onChange: (value: CounterpartyValue) => void;
  /** Offer "Create new counterparty" (also needs the caller's right, `AppConfig.canCreateCounterparty`). */
  allowCreate?: boolean;
}

/**
 * Counterparty type-ahead over the active master records (`GET counterparties?q=`). A name
 * without a master record (old data, an AI suggestion) is shown as the starting search text.
 */
export function CounterpartyPicker({ value, onChange, allowCreate = true, ...rest }: CounterpartyPickerProps) {
  const api = useApi();
  const config = useAppConfig();
  const [q, setQ] = useState('');
  const term = useDebounced(q.trim(), 150);
  const list = useQuery({ queryKey: qk.counterparties(term), queryFn: () => api.getCounterparties(term), staleTime: 60_000 });
  const [creating, setCreating] = useState<string | null>(null);
  const selected = value.supplierId ? { id: value.supplierId, label: value.name } : null;
  const options = useMemo(
    () => (query: string) =>
      (list.data ?? [])
        // The server has already searched name, VAT number and external id for `term`; the list is
        // only narrowed here while newer text is on its way, over the same three fields.
        .filter((c) => c.isActive && (!query.trim() || query.trim() === term || match([c.name, c.vatNo, c.externalId].filter(Boolean).join(' '), query)))
        .slice(0, 20)
        .map((c) => ({ id: c.id, label: c.name, sub: [c.country, c.vatNo, c.city].filter(Boolean).join(' · ') })),
    [list.data, term],
  );
  const canCreate = allowCreate && !!config.canCreateCounterparty;
  return (
    <>
      <Combobox
        {...rest}
        placeholder={rest.placeholder ?? 'Type the counterparty name'}
        value={selected}
        initialQuery={value.supplierId ? undefined : value.name}
        onQuery={setQ}
        loading={list.isFetching}
        options={options}
        onChange={(o) => onChange(o ? { supplierId: o.id, name: o.label } : { supplierId: null, name: '' })}
        footer={canCreate ? { label: 'Create new counterparty', onSelect: (text) => setCreating(text.trim()) } : undefined}
        clearLabel="Clear counterparty"
      />
      {creating !== null ? (
        <CreateCounterpartyDialog
          initialName={creating}
          onClose={() => setCreating(null)}
          onCreated={(c) => {
            setCreating(null);
            onChange({ supplierId: c.id, name: c.name });
          }}
        />
      ) : null}
    </>
  );
}

function CreateCounterpartyDialog({ initialName, onClose, onCreated }: { initialName: string; onClose: () => void; onCreated: (c: Pick<Counterparty, 'id' | 'name'>) => void }) {
  const api = useApi();
  const client = useQueryClient();
  const toast = useToast();
  const [d, setD] = useState<CounterpartyCreateInput>({ name: initialName, country: 'DK', vatNo: null, kind: 'Supplier', city: null, email: null, phone: null });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const create = useMutation({
    mutationFn: () => api.createCounterpartyQuick({ ...d, name: d.name.trim(), vatNo: d.vatNo?.trim() || null, country: d.country?.trim().toUpperCase() || null, city: d.city?.trim() || null, email: d.email?.trim() || null }),
    onSuccess: async (c) => {
      await client.invalidateQueries({ queryKey: ['counterparties'] });
      toast.show(`${c.name} created`);
      onCreated(c);
    },
    onError: (e) => {
      // A duplicate answers with the existing record: pick that one instead.
      const existing = e instanceof ApiError ? ((e.problem as unknown as Record<string, unknown>).existingId as string | undefined) : undefined;
      if (existing) {
        // The existing record may have another name (a match on the VAT number): show the master name.
        void api
          .getCounterparty(existing)
          .then((page) => page.counterparty.name)
          .catch(() => d.name.trim())
          .then((name) => {
            toast.show(`${name} already exists. It has been chosen.`);
            onCreated({ id: existing, name });
          });
        return;
      }
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  const set = (patch: Partial<CounterpartyCreateInput>) => setD((x) => ({ ...x, ...patch }));
  return (
    <Modal
      title="Create new counterparty"
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button loading={create.isPending} onClick={() => (d.name.trim() ? create.mutate() : setErrors({ name: 'Enter the name' }))}>
            Create counterparty
          </Button>
        </>
      }
    >
      <div className="form-grid form-grid--flat">
        <Field label="Name" error={errors.name}>
          {(p) => <TextInput {...p} data-autofocus="" value={d.name} onChange={(e) => set({ name: e.target.value })} />}
        </Field>
        <Field label="Kind">
          {(p) => <Select {...p} value={d.kind} onChange={(e) => set({ kind: e.target.value as CounterpartyCreateInput['kind'] })} options={[{ value: 'Supplier', label: 'Supplier' }, { value: 'Customer', label: 'Customer' }, { value: 'Partner', label: 'Partner' }, { value: 'Authority', label: 'Authority' }, { value: 'Other', label: 'Other' }]} />}
        </Field>
        <Field label="Country" optional error={errors.country} hint="Two letters, e.g. DK">
          {(p) => <TextInput {...p} value={d.country ?? ''} maxLength={2} onChange={(e) => set({ country: e.target.value })} />}
        </Field>
        <Field label="VAT number" optional error={errors.vatNo}>
          {(p) => <TextInput {...p} value={d.vatNo ?? ''} onChange={(e) => set({ vatNo: e.target.value })} />}
        </Field>
        <Field label="City" optional>
          {(p) => <TextInput {...p} value={d.city ?? ''} onChange={(e) => set({ city: e.target.value })} />}
        </Field>
        <Field label="E-mail" optional error={errors.email}>
          {(p) => <TextInput {...p} type="email" value={d.email ?? ''} onChange={(e) => set({ email: e.target.value })} />}
        </Field>
      </div>
    </Modal>
  );
}

/** Label + PersonPicker, for forms. */
export function PersonField({ label, optional, error, hint, ...picker }: PersonPickerProps & { label: string; optional?: boolean; error?: string; hint?: string; children?: ReactNode }) {
  return (
    <Field label={label} optional={optional} error={error} hint={hint}>
      {(p) => <PersonPicker {...picker} {...p} clearLabel={`Clear ${label.toLowerCase()}`} />}
    </Field>
  );
}
