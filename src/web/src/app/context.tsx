/* eslint-disable react-refresh/only-export-components */
import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';
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

export function ScopeProvider({ children }: { children: ReactNode }) {
  const [entityId, setEntityId] = useState('');
  const value = useMemo(() => ({ entityId, setEntityId }), [entityId]);
  return <ScopeContext.Provider value={value}>{children}</ScopeContext.Provider>;
}

export function useScope(): Scope {
  const s = useContext(ScopeContext);
  if (!s) throw new Error('useScope must be used inside ScopeProvider');
  return s;
}
