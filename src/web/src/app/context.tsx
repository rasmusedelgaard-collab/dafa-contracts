/* eslint-disable react-refresh/only-export-components */
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { Api } from '../api/api';
import type { Lookups, Me, ReminderSettings } from '../api/types';

// ----- API -----
const ApiContext = createContext<Api | null>(null);

export function ApiProvider({ api, children }: { api: Api; children: ReactNode }) {
  return <ApiContext.Provider value={api}>{children}</ApiContext.Provider>;
}

export function useApi(): Api {
  const api = useContext(ApiContext);
  if (!api) throw new Error('useApi must be used inside ApiProvider');
  return api;
}

// ----- Query keys (one place, so invalidation stays consistent) -----
export const qk = {
  me: ['me'] as const,
  devConfig: ['dev-config'] as const,
  devUsers: ['dev-users'] as const,
  reminderSettings: ['reminder-settings'] as const,
  lookups: ['lookups'] as const,
  home: ['home'] as const,
  contracts: ['contracts'] as const,
  contractList: (query: object) => ['contracts', 'list', query] as const,
  contract: (id: string) => ['contracts', 'detail', id] as const,
  /** `?peek=true` reads (quick look, dialogs); kept apart so opening the record still counts as opened. */
  contractPeek: (id: string) => ['contracts', 'peek', id] as const,
  search: (q: string) => ['search', q] as const,
  suppliers: (q: string) => ['suppliers', q] as const,
  templates: ['templates'] as const,
  approvals: ['approvals'] as const,
  approvalPreview: (query: object) => ['approvals', 'preview', query] as const,
  renewals: (months: number) => ['renewals', months] as const,
  overview: ['overview'] as const,
  admin: (resource: string, ...rest: unknown[]) => ['admin', resource, ...rest] as const,
};

/** Refreshes everything a contract change can touch: lists, the record, Home, approvals. */
export function useInvalidateContracts(): () => Promise<void> {
  const client = useQueryClient();
  return async () => {
    await Promise.all([
      client.invalidateQueries({ queryKey: qk.contracts }),
      client.invalidateQueries({ queryKey: qk.home }),
      client.invalidateQueries({ queryKey: qk.approvals }),
      client.invalidateQueries({ queryKey: ['renewals'] }),
      client.invalidateQueries({ queryKey: qk.overview }),
    ]);
  };
}

/** Query keys that are not tied to the signed-in user. */
const isSessionFree = (key: readonly unknown[]) => key[0] === qk.devConfig[0];

/**
 * Sign-in, user switch and sign-out, in one place.
 *
 * - `signedIn(me)`: every cached answer belonged to the previous user (or to nobody), so it is
 *   dropped; `me` is set from the `dev/login` response. `App` keys the whole shell on the user
 *   id, so the shell, the navigation and the permissions are rebuilt for the new user and any
 *   query still on screen starts again from nothing.
 * - `signedOut()`: drops everything and asks `me` again; the 401 puts the sign-in screen up.
 */
export function useSessionActions() {
  const client = useQueryClient();
  return useMemo(
    () => ({
      signedIn(me: Me): void {
        client.removeQueries({ predicate: (q) => !isSessionFree(q.queryKey) && q.queryKey[0] !== qk.me[0] });
        client.setQueryData(qk.me, me);
      },
      async signedOut(): Promise<void> {
        client.removeQueries({ predicate: (q) => !isSessionFree(q.queryKey) && q.queryKey[0] !== qk.me[0] });
        // Back to "not asked yet" (the splash), then the fresh answer: 401, so the sign-in screen.
        await client.resetQueries({ queryKey: qk.me });
      },
    }),
    [client],
  );
}

// ----- Session -----
interface Session {
  me: Me;
  lookups: Lookups;
}
const SessionContext = createContext<Session | null>(null);

export function SessionProvider({ me, lookups, children }: Session & { children: ReactNode }) {
  const value = useMemo(() => ({ me, lookups }), [me, lookups]);
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

function useSession(): Session {
  const s = useContext(SessionContext);
  if (!s) throw new Error('Session is not loaded');
  return s;
}

export const useMe = (): Me => useSession().me;
export const useLookups = (): Lookups => useSession().lookups;

export function useMeQuery() {
  const api = useApi();
  return useQuery({ queryKey: qk.me, queryFn: () => api.getMe(), retry: false, staleTime: Infinity });
}

export function useLookupsQuery(enabled: boolean) {
  const api = useApi();
  return useQuery({ queryKey: qk.lookups, queryFn: () => api.getLookups(), enabled, staleTime: 5 * 60_000 });
}

export const DEFAULT_REMINDER_SETTINGS: ReminderSettings = { noticeDays: [120, 60, 30], escalationDays: 14, obligationDays: 14 };

/**
 * The reminder settings every signed-in user may read (`GET reminder-settings`). Falls back to
 * the brief's defaults while loading or when the endpoint is not there.
 */
export function useReminderSettings(): ReminderSettings {
  const api = useApi();
  const q = useQuery({ queryKey: qk.reminderSettings, queryFn: () => api.getReminderSettings(), staleTime: 5 * 60_000, retry: false });
  return q.data ?? DEFAULT_REMINDER_SETTINGS;
}

/** "120, 60 and 30" */
export function listDays(days: number[]): string {
  const sorted = [...days].sort((a, b) => b - a).map(String);
  if (sorted.length <= 1) return sorted.join('');
  return `${sorted.slice(0, -1).join(', ')} and ${sorted[sorted.length - 1]}`;
}

/** What the signed-in user may do, derived from `me`. The API enforces the same rules. */
export function permissions(me: Me) {
  const isAdmin = me.roles.includes('Admin');
  return {
    isAdmin,
    canCreate: isAdmin || me.roles.includes('ContractOwner'),
    canSeeOverview: isAdmin || me.isGroupManagement || me.managedEntityIds.length > 0,
    canSeeSettings: isAdmin,
  };
}

export function accessLabel(me: Me, lookups: Lookups): string {
  if (me.isGroupManagement) return 'Group management';
  if (me.roles.includes('Admin')) return 'Administrator';
  const entity = lookups.entities.find((e) => me.managedEntityIds.includes(e.id));
  if (entity) return `${entity.name} management`;
  const departments = lookups.departments.filter((d) => me.departmentIds.includes(d.id)).map((d) => d.name);
  if (departments.length === 1) return departments[0] ?? '';
  if (departments.length > 1) return `${departments[0]} +${departments.length - 1}`;
  return me.roles.includes('Approver') ? 'Approver' : 'Reader';
}

// ----- Entity scope (top bar select; the register's entity filter) -----
interface Scope {
  entityId: string;
  setEntityId: (id: string) => void;
}
const ScopeContext = createContext<Scope | null>(null);

/**
 * The entity scope lives in the URL (`?entity=<id>`), so a reload or a shared link shows the
 * same list. It is carried along when the user moves between pages (see `ScopeLink` users:
 * the shell re-applies it on navigation).
 */
export function ScopeProvider({ children }: { children: ReactNode }) {
  const [params, setParams] = useSearchParams();
  const fromUrl = params.get('entity') ?? '';
  const [entityId, setLocal] = useState(fromUrl);
  // The URL wins when it names an entity (deep link, back/forward).
  const [seen, setSeen] = useState(fromUrl);
  if (fromUrl !== seen) {
    setSeen(fromUrl);
    if (fromUrl) setLocal(fromUrl);
  }
  // Navigation inside the app drops the query string; put the scope back so a reload keeps it.
  const missing = !!entityId && !fromUrl;
  useEffect(() => {
    if (!missing) return;
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.set('entity', entityId);
        return next;
      },
      { replace: true },
    );
  }, [missing, entityId, setParams]);
  const value = useMemo(
    () => ({
      entityId,
      setEntityId: (id: string) => {
        setLocal(id);
        setSeen(id);
        setParams(
          (prev) => {
            const next = new URLSearchParams(prev);
            if (id) next.set('entity', id);
            else next.delete('entity');
            next.delete('page');
            return next;
          },
          { replace: true },
        );
      },
    }),
    [entityId, setParams],
  );
  return <ScopeContext.Provider value={value}>{children}</ScopeContext.Provider>;
}

export function useScope(): Scope {
  const s = useContext(ScopeContext);
  if (!s) throw new Error('useScope must be used inside ScopeProvider');
  return s;
}
