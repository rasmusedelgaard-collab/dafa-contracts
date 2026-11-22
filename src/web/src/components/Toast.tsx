import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import { errorMessage } from '../lib/errors';
import { Icon } from './Icon';

export interface ToastAction {
  label: string;
  onClick?: () => void;
  href?: string;
}

interface ToastItem {
  id: number;
  kind: 'info' | 'error';
  text: string;
  action?: ToastAction;
}

export interface ToastApi {
  show: (text: string) => void;
  /** `action` adds a button (e.g. Retry) or a link to the message. */
  error: (error: unknown, fallback?: string, action?: ToastAction) => void;
}

const ToastContext = createContext<ToastApi | null>(null);

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([]);
  const nextId = useRef(1);

  const dismiss = useCallback((id: number) => setItems((list) => list.filter((t) => t.id !== id)), []);
  const push = useCallback(
    (kind: ToastItem['kind'], text: string, action?: ToastAction) => {
      const id = nextId.current++;
      setItems((list) => [...list.slice(-3), { id, kind, text, action }]);
      window.setTimeout(() => dismiss(id), action ? 15000 : kind === 'error' ? 8000 : 4500);
    },
    [dismiss],
  );

  const api = useMemo<ToastApi>(
    () => ({
      show: (text) => push('info', text),
      error: (error, fallback, action) => push('error', errorMessage(error, fallback), action),
    }),
    [push],
  );

  return (
    <ToastContext.Provider value={api}>
      {children}
      <div className="toasts" aria-live="polite" aria-atomic="false">
        {items.map((t) => (
          <div key={t.id} className={`toast${t.kind === 'error' ? ' toast--error' : ''}`} role={t.kind === 'error' ? 'alert' : 'status'}>
            <span className="toast__text">{t.text}</span>
            {t.action?.href ? (
              <a className="toast__action" href={t.action.href}>
                {t.action.label}
              </a>
            ) : t.action ? (
              <button type="button" className="toast__action" onClick={() => { dismiss(t.id); t.action?.onClick?.(); }}>
                {t.action.label}
              </button>
            ) : null}
            <button type="button" className="toast__close" aria-label="Dismiss" onClick={() => dismiss(t.id)}>
              <Icon name="x" size={14} stroke={2.4} />
            </button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function useToast(): ToastApi {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error('useToast must be used inside ToastProvider');
  return ctx;
}
