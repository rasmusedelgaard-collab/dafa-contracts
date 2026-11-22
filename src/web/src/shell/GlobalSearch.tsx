import { useEffect, useId, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { qk, useApi, useScope } from '../app/context';
import type { SearchResult } from '../api/types';
import { Icon, Skeleton } from '../components';

function useDebounced<T>(value: T, ms: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return debounced;
}

/** Top bar search over `GET search?q=` (contracts and suppliers). Ctrl/Cmd+K focuses it. */
export function GlobalSearch() {
  const api = useApi();
  const navigate = useNavigate();
  const listId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const boxRef = useRef<HTMLDivElement>(null);
  const [text, setText] = useState('');
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const q = useDebounced(text.trim(), 200);
  const enabled = q.length >= 2;

  const scope = useScope();
  const results = useQuery({ queryKey: [...qk.search(q), scope.entityIds], queryFn: () => api.search(q, scope.entityIds), enabled, staleTime: 30_000 });
  // The last option always opens the full search page (contracts, documents, people).
  const all: SearchResult = { kind: 'Contract', id: '__all__', title: `See all results for "${text.trim()}"`, subtitle: 'Contracts, documents, people and counterparties' };
  const found = enabled ? (results.data ?? []) : [];
  const items = enabled && results.data ? [...found, all] : found;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        inputRef.current?.focus();
        inputRef.current?.select();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!boxRef.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [open]);

  const go = (r: SearchResult) => {
    setOpen(false);
    setText('');
    inputRef.current?.blur();
    if (r.id === '__all__') navigate(`/search?q=${encodeURIComponent(text.trim())}`);
    else if (r.kind === 'Contract') navigate(`/contracts/${r.id}`);
    else if (r.kind === 'Document') navigate(`/contracts/${r.contractId ?? ''}?doc=${r.id}`);
    else if (r.kind === 'Person') navigate(`/people/${r.id}`);
    else navigate(`/counterparties/${r.id}`);
  };

  const showPop = open && text.trim().length >= 2;
  const activeIndex = Math.min(active, Math.max(0, items.length - 1));

  return (
    <div className="gsearch" ref={boxRef}>
      <label htmlFor={`${listId}-input`} className="sr-only">
        Search contracts, documents and people
      </label>
      <div className="gsearch__box">
        <Icon name="search" size={18} stroke={2} />
        <input
          ref={inputRef}
          id={`${listId}-input`}
          className="gsearch__input"
          type="search"
          role="combobox"
          aria-expanded={showPop}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={showPop && items[activeIndex] ? `${listId}-${activeIndex}` : undefined}
          autoComplete="off"
          placeholder="Search contracts, suppliers, documents"
          value={text}
          onChange={(e) => {
            setText(e.target.value);
            setOpen(true);
            setActive(0);
          }}
          onFocus={() => setOpen(true)}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') {
              e.preventDefault();
              setOpen(true);
              setActive(items.length ? (activeIndex + 1) % items.length : 0);
            } else if (e.key === 'ArrowUp') {
              e.preventDefault();
              setActive(items.length ? (activeIndex - 1 + items.length) % items.length : 0);
            } else if (e.key === 'Enter') {
              const item = items[activeIndex];
              if (showPop && item) {
                e.preventDefault();
                go(item);
              } else if (text.trim()) {
                setOpen(false);
                navigate(`/search?q=${encodeURIComponent(text.trim())}`);
              }
            } else if (e.key === 'Escape') {
              setOpen(false);
            }
          }}
        />
        <span className="kbd" aria-hidden="true">
          Ctrl K
        </span>
      </div>
      <div className="gsearch__pop" id={listId} role="listbox" aria-label="Search results" hidden={!showPop}>
        {showPop && (results.isLoading || q !== text.trim()) ? (
          <div className="gsearch__note" role="status" aria-label="Searching">
            <Skeleton width="70%" />
          </div>
        ) : showPop && results.isError ? (
          <div className="gsearch__note" role="alert">
            Search is not available right now.
          </div>
        ) : showPop && found.length === 0 ? (
          <div className="gsearch__note">
            Nothing matches "{text.trim()}".{' '}
            <button type="button" className="link-btn" onMouseDown={(e) => e.preventDefault()} onClick={() => go(all)}>
              Search everything
            </button>
          </div>
        ) : (
          items.map((r, i) => (
            <div
              key={`${r.kind}-${r.id}`}
              id={`${listId}-${i}`}
              role="option"
              aria-selected={i === activeIndex}
              className={`gsearch__opt${r.id === '__all__' ? ' gsearch__opt--all' : ''}`}
              onMouseEnter={() => setActive(i)}
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => go(r)}
            >
              <span className="tile" aria-hidden="true">
                <Icon name={r.id === '__all__' ? 'search' : r.kind === 'Contract' ? 'doc' : r.kind === 'Document' ? 'docPlus' : r.kind === 'Person' ? 'person' : 'building'} size={18} />
              </span>
              <span className="kv">
                <span className="kv__v" style={{ fontSize: 14 }}>
                  {r.title}
                </span>
                <span className="kv__k">{r.subtitle}</span>
              </span>
            </div>
          ))
        )}
      </div>
    </div>
  );
}
