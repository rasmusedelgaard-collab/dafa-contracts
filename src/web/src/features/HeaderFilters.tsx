/* eslint-disable react-refresh/only-export-components */
// Register column header filters (owner item B): every column gets a filter next to its sort.
// Text columns: "contains" (and a value list where it makes sense); entity, department, type,
// status, owner, delegate, responsible, counterparty, tags, currency: a searchable multi-select of
// the values in the current selection (`GET contracts/distinct`); values: min/max in DKK; dates:
// from/to; custom fields by their type. The filters live in the URL like every other filter, so
// they work with saved views, the breadcrumb chips and export.
import { createContext, useContext, useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useApi } from '../app/context';
import type { ContractFilter, ContractView, CustomFieldDef, DistinctColumn } from '../api/types';
import { Button, Icon, NumberInput, TextInput } from '../components';

type Key = keyof ContractFilter;
export type FilterSpec =
  | { kind: 'text'; param: Key; label: string; also?: { column: DistinctColumn; param: Key } }
  | { kind: 'multi'; column: DistinctColumn; param: Key | `cf.${string}`; label: string }
  | { kind: 'range'; min: Key | `cf.${string}`; max: Key | `cf.${string}`; input: 'money' | 'number' | 'date'; label: string }
  | { kind: 'yesno'; param: Key | `cf.${string}`; label: string };

/** The header filter of a register column, if it has one. */
export function filterSpecFor(column: string, customFields: CustomFieldDef[] = []): FilterSpec | null {
  switch (column) {
    case 'contract':
      return { kind: 'text', param: 'title', label: 'Title contains' };
    case 'contractNo':
      return { kind: 'text', param: 'contractNo', label: 'Contract no. contains' };
    case 'counterparty':
      return { kind: 'text', param: 'counterpartyName', label: 'Name contains', also: { column: 'counterparty', param: 'counterpartyId' } };
    case 'type':
    case 'entity':
    case 'department':
    case 'owner':
    case 'delegate':
    case 'responsible':
    case 'status':
    case 'tags': {
      const param: Record<string, Key> = { type: 'typeId', entity: 'entityId', department: 'departmentId', owner: 'ownerId', delegate: 'delegateId', responsible: 'responsibleId', status: 'status', tags: 'tagId' };
      return { kind: 'multi', column: column as DistinctColumn, param: param[column] as Key, label: 'Show only' };
    }
    case 'annualValue':
      return { kind: 'range', min: 'annualValueMin', max: 'annualValueMax', input: 'money', label: 'Annual value in DKK' };
    case 'totalValue':
      return { kind: 'range', min: 'totalValueMin', max: 'totalValueMax', input: 'money', label: 'Total value in DKK' };
    case 'notice':
    case 'start':
    case 'end':
      return { kind: 'range', min: `${column}From` as Key, max: `${column}To` as Key, input: 'date', label: column === 'notice' ? 'Notice deadline' : column === 'start' ? 'Start date' : 'End date' };
    case 'autoRenew':
      return { kind: 'yesno', param: 'autoRenew', label: 'Renews automatically' };
    case 'changeOfControl':
      return { kind: 'yesno', param: 'changeOfControl', label: 'Change-of-control clause' };
  }
  if (column.startsWith('cf.')) {
    const def = customFields.find((d) => `cf.${d.key}` === column);
    if (!def) return null;
    const cf = `cf.${def.key}` as const;
    switch (def.type) {
      case 'Choice':
      case 'MultiChoice':
      case 'Person':
      case 'Text':
        return def.type === 'Text' ? { kind: 'multi', column: cf, param: cf, label: 'Show only' } : { kind: 'multi', column: cf, param: cf, label: 'Show only' };
      case 'YesNo':
        return { kind: 'yesno', param: cf, label: def.label };
      case 'Number':
      case 'Money':
        return { kind: 'range', min: `${cf}.min`, max: `${cf}.max`, input: 'number', label: def.type === 'Money' ? `${def.label} (amount)` : def.label };
      case 'Date':
        return { kind: 'range', min: `${cf}.min`, max: `${cf}.max`, input: 'date', label: def.label };
    }
  }
  return null;
}

const list = (v: unknown): string[] => (v === undefined || v === null || v === '' ? [] : Array.isArray(v) ? v.map(String) : [String(v)]);

/** Reads one filter parameter (a standard key or `cf.<key>[.min|.max]`). */
export function readParam(filter: ContractFilter, param: string): string[] {
  if (param.startsWith('cf.')) return list(filter.cf?.[param.slice(3)]);
  return list((filter as Record<string, unknown>)[param]);
}

/** A patch for `applyDrill` that sets (or with [] removes) one parameter. */
export function patchFor(param: string, values: string[], number = false): Partial<ContractFilter> {
  const value = values.length === 0 ? undefined : values.length === 1 ? (number ? Number(values[0]) : values[0]) : values;
  if (param.startsWith('cf.')) return { cf: { [param.slice(3)]: (value as string | string[] | undefined) ?? '' } };
  return { [param]: value } as Partial<ContractFilter>;
}

export function isFilterActive(spec: FilterSpec, filter: ContractFilter): boolean {
  switch (spec.kind) {
    case 'text':
      return readParam(filter, spec.param).length > 0 || (!!spec.also && readParam(filter, spec.also.param).length > 0);
    case 'multi':
    case 'yesno':
      return readParam(filter, spec.param).length > 0;
    case 'range':
      return readParam(filter, spec.min).length > 0 || readParam(filter, spec.max).length > 0;
  }
}

/**
 * What the register shows besides the filter: its tab and whether a search text is active. The
 * value lists are counted within the tab (`view`); the API cannot count within the free-text
 * search (`q` is the search inside the value list there), so the list says so (review finding L4).
 */
export interface DistinctScope {
  view?: ContractView;
  searchActive?: boolean;
}
const DistinctScopeContext = createContext<DistinctScope>({});

interface HeaderFilterProps {
  label: string;
  spec: FilterSpec;
  filter: ContractFilter;
  onApply: (patch: Partial<ContractFilter>) => void;
  scope?: DistinctScope;
}

const NO_SCOPE: DistinctScope = {};

/** The filter button in a column header and its popover. */
export function HeaderFilter({ label, spec, filter, onApply, scope }: HeaderFilterProps) {
  const [open, setOpen] = useState(false);
  const button = useRef<HTMLButtonElement>(null);
  const active = isFilterActive(spec, filter);
  return (
    <>
      <button
        ref={button}
        type="button"
        className={`th-filter${active ? ' th-filter--on' : ''}`}
        aria-label={`Filter ${label}${active ? ' (active)' : ''}`}
        aria-haspopup="dialog"
        aria-expanded={open}
        onClick={(e) => {
          e.stopPropagation();
          setOpen((o) => !o);
        }}
      >
        <Icon name="filter" size={13} stroke={2.2} />
      </button>
      {open ? (
        <Popover anchor={button.current} label={`Filter ${label}`} onClose={() => setOpen(false)}>
          <DistinctScopeContext.Provider value={scope ?? NO_SCOPE}>
            <FilterForm
              spec={spec}
              filter={filter}
              onApply={(patch) => {
                onApply(patch);
                setOpen(false);
                button.current?.focus();
              }}
            />
          </DistinctScopeContext.Provider>
        </Popover>
      ) : null}
    </>
  );
}

function Popover({ anchor, label, onClose, children }: { anchor: HTMLElement | null; label: string; onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);
  useLayoutEffect(() => {
    if (!anchor) return;
    const r = anchor.getBoundingClientRect();
    const width = Math.min(320, window.innerWidth - 16);
    setPos({ top: r.bottom + 6, left: Math.max(8, Math.min(r.left, window.innerWidth - width - 8)) });
  }, [anchor]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
        anchor?.focus();
      }
    };
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node) && !anchor?.contains(e.target as Node)) onClose();
    };
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onDown);
    ref.current?.querySelector<HTMLElement>('input, button')?.focus();
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('mousedown', onDown);
    };
  }, [anchor, onClose]);
  return createPortal(
    <div ref={ref} role="dialog" aria-label={label} className="th-popover" style={pos ? { top: pos.top, left: pos.left } : { visibility: 'hidden' }}>
      {children}
    </div>,
    document.body,
  );
}

function FilterForm({ spec, filter, onApply }: { spec: FilterSpec; filter: ContractFilter; onApply: (patch: Partial<ContractFilter>) => void }) {
  const id = useId();
  if (spec.kind === 'text') {
    return <TextFilter spec={spec} filter={filter} onApply={onApply} />;
  }
  if (spec.kind === 'multi') return <MultiFilter column={spec.column} param={spec.param} filter={filter} onApply={onApply} />;
  if (spec.kind === 'yesno') {
    const current = readParam(filter, spec.param)[0] ?? '';
    return (
      <div className="stack" style={{ gap: 10 }} role="radiogroup" aria-labelledby={`${id}-l`}>
        <span id={`${id}-l`} className="field__label">
          {spec.label}
        </span>
        {[
          ['', 'Any'],
          ['true', 'Yes'],
          ['false', 'No'],
        ].map(([v, l]) => (
          <label key={v} className="check">
            <input type="radio" name={id} checked={current === v} onChange={() => onApply(patchFor(spec.param, v ? [v] : []))} />
            {l}
          </label>
        ))}
      </div>
    );
  }
  return <RangeFilter spec={spec} filter={filter} onApply={onApply} />;
}

function Footer({ onClear, onApply, canClear }: { onClear: () => void; onApply?: () => void; canClear: boolean }) {
  return (
    <div className="th-popover__foot">
      <Button variant="ghost" size="sm" onClick={onClear} disabled={!canClear}>
        Clear
      </Button>
      {onApply ? (
        <Button size="sm" type="submit" onClick={onApply}>
          Apply
        </Button>
      ) : null}
    </div>
  );
}

function TextFilter({ spec, filter, onApply }: { spec: Extract<FilterSpec, { kind: 'text' }>; filter: ContractFilter; onApply: (patch: Partial<ContractFilter>) => void }) {
  const id = useId();
  const [text, setText] = useState(readParam(filter, spec.param)[0] ?? '');
  const clearAll = () => onApply({ ...patchFor(spec.param, []), ...(spec.also ? patchFor(spec.also.param, []) : {}) });
  return (
    <form
      className="stack"
      style={{ gap: 12 }}
      onSubmit={(e) => {
        e.preventDefault();
        onApply(patchFor(spec.param, text.trim() ? [text.trim()] : []));
      }}
    >
      <label className="field__label" htmlFor={id}>
        {spec.label}
      </label>
      <TextInput id={id} value={text} onChange={(e) => setText(e.target.value)} />
      {spec.also ? (
        <details className="th-popover__more" open={readParam(filter, spec.also.param).length > 0}>
          <summary>Or pick from the list</summary>
          <MultiFilter column={spec.also.column} param={spec.also.param} filter={filter} onApply={onApply} embedded />
        </details>
      ) : null}
      <Footer canClear={isFilterActive(spec, filter) || !!text} onClear={clearAll} onApply={() => undefined} />
    </form>
  );
}

function MultiFilter({ column, param, filter, onApply, embedded }: { column: DistinctColumn; param: string; filter: ContractFilter; onApply: (patch: Partial<ContractFilter>) => void; embedded?: boolean }) {
  const api = useApi();
  const [q, setQ] = useState('');
  const [chosen, setChosen] = useState<string[]>(readParam(filter, param));
  const id = useId();
  const scope = useContext(DistinctScopeContext);
  const view = scope.view && scope.view !== 'all' ? scope.view : undefined;
  const scoped: ContractFilter & { view?: ContractView } = view ? { ...filter, view } : filter;
  const values = useQuery({ queryKey: ['contracts', 'distinct', column, scoped, q], queryFn: () => api.getDistinct(column, scoped, q, 100), placeholderData: keepPreviousData, staleTime: 15_000 });
  const toggle = (v: string, on: boolean) => setChosen((c) => (on ? [...c, v] : c.filter((x) => x !== v)));
  const body = (
    <>
      <label className="sr-only" htmlFor={id}>
        Search values
      </label>
      <TextInput id={id} placeholder="Search" value={q} onChange={(e) => setQ(e.target.value)} />
      <div className="th-popover__list" role="group" aria-label="Values">
        {values.isLoading ? <span className="caption">Loading…</span> : null}
        {values.isError ? <span className="caption">The values could not be loaded.</span> : null}
        {(values.data?.values ?? []).length === 0 && !values.isLoading ? <span className="caption">No values</span> : null}
        {(values.data?.values ?? []).map((v) => (
          <label key={v.value} className="check th-popover__opt">
            <input type="checkbox" checked={chosen.includes(v.value)} onChange={(e) => toggle(v.value, e.target.checked)} />
            <span className="truncate">{v.label}</span>
            <span className="caption">{v.count}</span>
          </label>
        ))}
        {values.data?.truncated ? <span className="caption">More values: search to narrow the list.</span> : null}
        {scope.searchActive && (values.data?.values ?? []).length > 0 ? <span className="caption">The counts do not take the search text of the list into account.</span> : null}
      </div>
    </>
  );
  if (embedded) {
    return (
      <div className="stack" style={{ gap: 8 }}>
        {body}
        <Button size="sm" variant="secondary" onClick={() => onApply(patchFor(param, chosen))}>
          Apply selection
        </Button>
      </div>
    );
  }
  return (
    <form
      className="stack"
      style={{ gap: 10 }}
      onSubmit={(e) => {
        e.preventDefault();
        onApply(patchFor(param, chosen));
      }}
    >
      {body}
      <Footer canClear={chosen.length > 0 || readParam(filter, param).length > 0} onClear={() => onApply(patchFor(param, []))} onApply={() => undefined} />
    </form>
  );
}

function RangeFilter({ spec, filter, onApply }: { spec: Extract<FilterSpec, { kind: 'range' }>; filter: ContractFilter; onApply: (patch: Partial<ContractFilter>) => void }) {
  const id = useId();
  const [min, setMin] = useState(readParam(filter, spec.min)[0] ?? '');
  const [max, setMax] = useState(readParam(filter, spec.max)[0] ?? '');
  const isNumber = spec.input !== 'date';
  const patch = (a: string, b: string) => ({ ...patchFor(spec.min, a ? [a] : [], isNumber && !spec.min.startsWith('cf.')), ...mergeCf(patchFor(spec.max, b ? [b] : [], isNumber && !spec.max.startsWith('cf.')), patchFor(spec.min, a ? [a] : [], isNumber && !spec.min.startsWith('cf.'))) });
  return (
    <form
      className="stack"
      style={{ gap: 12 }}
      onSubmit={(e) => {
        e.preventDefault();
        onApply(patch(min, max));
      }}
    >
      <span className="field__label">{spec.label}</span>
      <div className="th-popover__range">
        <label className="stack" style={{ gap: 4 }} htmlFor={`${id}-min`}>
          <span className="caption">{spec.input === 'date' ? 'From' : 'Min'}</span>
          {isNumber ? <NumberInput id={`${id}-min`} decimals={spec.input === 'money' ? 0 : 2} value={min} onValueChange={setMin} /> : <TextInput id={`${id}-min`} type="date" value={min} onChange={(e) => setMin(e.target.value)} />}
        </label>
        <label className="stack" style={{ gap: 4 }} htmlFor={`${id}-max`}>
          <span className="caption">{spec.input === 'date' ? 'To' : 'Max'}</span>
          {isNumber ? <NumberInput id={`${id}-max`} decimals={spec.input === 'money' ? 0 : 2} value={max} onValueChange={setMax} /> : <TextInput id={`${id}-max`} type="date" value={max} onChange={(e) => setMax(e.target.value)} />}
        </label>
      </div>
      <Footer canClear={isFilterActive(spec, filter) || !!min || !!max} onClear={() => onApply(patch('', ''))} onApply={() => undefined} />
    </form>
  );
}

/** Two patches that may both carry `cf`: merge the `cf` objects. */
function mergeCf(a: Partial<ContractFilter>, b: Partial<ContractFilter>): Partial<ContractFilter> {
  if (a.cf && b.cf) return { ...a, cf: { ...b.cf, ...a.cf } };
  return a;
}
