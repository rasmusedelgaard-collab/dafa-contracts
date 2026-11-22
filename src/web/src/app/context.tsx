/* eslint-disable react-refresh/only-export-components */
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useLocation, useSearchParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { Api } from '../api/api';
import type { AppConfig, Lookups, Me, ReminderSettings } from '../api/types';
import { setDisplayFormats } from '../lib/displayFormat';

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
  // Release 3
  appConfig: ['app-config'] as const,
  analytics: (filter: object) => ['overview', 'analytics', filter] as const,
  searchFull: (query: object) => ['search', 'full', query] as const,
  person: (id: string) => ['contracts', 'person', id] as const,
  counterparty: (id: string) => ['contracts', 'counterparty', id] as const,
  counterparties: (q: string) => ['counterparties', q] as const,
  watching: ['contracts', 'watching'] as const,
  comments: (id: string) => ['contracts', 'comments', id] as const,
  versions: (id: string) => ['contracts', 'versions', id] as const,
  compare: (id: string, from: string, to: string) => ['contracts', 'compare', id, from, to] as const,
  notifications: ['notifications'] as const,
  unread: ['notifications', 'unread'] as const,
  preferences: ['preferences'] as const,
  views: ['views'] as const,
  dataRoom: ['data-room'] as const,
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
      client.invalidateQueries({ queryKey: qk.notifications }),
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

const EMPTY_CONFIG: AppConfig = {
  appName: 'DAFA Contracts',
  dateFormat: 'd MMM yyyy',
  numberFormat: 'en-GB',
  features: { aiSuggestions: true, templates: true, eSign: false },
  sessionTimeoutMinutes: 480,
  landingPage: 'home',
  defaultEntityId: null,
  dataQualityFields: [],
  ddThresholdDkk: 5_000_000,
  customFields: [],
  fieldRules: { types: [] },
  tags: [],
  rates: {},
  canExportDataRoom: false,
};

/**
 * `GET app-config` (release 3): custom fields, field rules, tags, feature toggles. Falls back to
 * empty settings while loading or against an API without the endpoint, so forms still work.
 */
export function useAppConfig(): AppConfig & { loaded: boolean } {
  const api = useApi();
  const q = useQuery({ queryKey: qk.appConfig, queryFn: () => api.getAppConfig(), staleTime: 5 * 60_000, retry: false });
  // Release 4: the General settings formats apply to every number and date shown.
  setDisplayFormats(q.data?.dateFormat, q.data?.numberFormat);
  return useMemo(() => ({ ...(q.data ?? EMPTY_CONFIG), loaded: !!q.data }), [q.data]);
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
    /** Release 3: every user has Overview, scoped to what they can see. */
    canSeeOverview: true,
    canExportDataRoom: isAdmin || me.isGroupManagement || me.managedEntityIds.length > 0,
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

// ----- Entity scope (top bar select; the register's and Overview's entity filter) -----
interface Scope {
  /** The one legal entity in `?entity=`, or '' (none, or several from a link or saved view). */
  entityId: string;
  /** Every `?entity=` value (a link or saved view can name several). */
  entityIds: string[];
  /** Sets or clears (`''`) the scope by writing the URL; every `entity` parameter is replaced. */
  setEntityId: (id: string) => void;
}
const ScopeContext = createContext<Scope | null>(null);

/** The `entity` values in a query string, without empty ones. */
export function entityParams(params: URLSearchParams): string[] {
  return params.getAll('entity').filter((v) => v !== '');
}

/**
 * The entity scope lives in the URL (`?entity=<id>`), so a reload or a shared link shows the same
 * list, and the URL is the only place it is kept: removing the parameter (a breadcrumb, Clear
 * all, a select set back to All, the browser's back button) removes the scope.
 *
 * Navigation inside the app drops the query string, so the scope is carried over to the next
 * page: the last value seen on a page is remembered together with that page's path, and put back
 * only when the user arrives on a different path without one. (Release 3 kept a second copy in
 * component state and wrote it back whenever the parameter went missing, also on the same page,
 * so an entity filter on Overview could not be removed: owner's bug report, 2026-11-22.)
 */
export function ScopeProvider({ children }: { children: ReactNode }) {
  const [params, setParams] = useSearchParams();
  const { pathname } = useLocation();
  const fromUrl = entityParams(params);
  const key = fromUrl.join(',');
  const [seen, setSeen] = useState({ path: pathname, key });
  const samePage = seen.path === pathname;
  // Same page: follow the URL, also when the parameter is removed. Another page: take over what it
  // names, or carry the remembered scope over (below) when it names nothing.
  if (samePage ? seen.key !== key : key !== '' || seen.key === '') setSeen({ path: pathname, key });
  const carry = !samePage && key === '' ? seen.key : '';
  useEffect(() => {
    if (!carry) return;
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.delete('entity');
        for (const id of carry.split(',')) next.append('entity', id);
        return next;
      },
      { replace: true },
    );
  }, [carry, setParams]);
  const current = carry || key;
  const value = useMemo(() => {
    const entityIds = current ? current.split(',') : [];
    return {
      entityId: entityIds.length === 1 ? (entityIds[0] ?? '') : '',
      entityIds,
      setEntityId: (id: string) => {
        setParams(
          (prev) => {
            const next = new URLSearchParams(prev);
            next.delete('entity');
            if (id) next.set('entity', id);
            next.delete('page');
            return next;
          },
          { replace: true },
        );
      },
    };
  }, [current, setParams]);
  return <ScopeContext.Provider value={value}>{children}</ScopeContext.Provider>;
}

export function useScope(): Scope {
  const s = useContext(ScopeContext);
  if (!s) throw new Error('useScope must be used inside ScopeProvider');
  return s;
}
