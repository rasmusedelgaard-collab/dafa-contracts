import { Suspense, useEffect, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { apiMode } from '../api';
import { accessLabel, permissions, qk, useApi, useAppConfig, useLookups, useMe, useScope } from '../app/context';
import { Avatar, ButtonLink, Icon, RouteErrorBoundary, Select, Skeleton, type IconName } from '../components';
import { GlobalSearch } from './GlobalSearch';
import { NotificationBell } from './NotificationBell';
import { UserSwitcher } from './UserSwitcher';

interface NavItem {
  to: string;
  label: string;
  icon: IconName;
  badge?: number;
  end?: boolean;
}

function useNavCounts() {
  const api = useApi();
  const home = useQuery({ queryKey: qk.home, queryFn: () => api.getHome() });
  return { tasks: home.data?.tasks.length ?? 0, approvals: home.data?.approvalsWaiting ?? 0 };
}

function Sidebar({ work, manage, onUser }: { work: NavItem[]; manage: NavItem[]; onUser: () => void }) {
  const me = useMe();
  const lookups = useLookups();
  const link = (item: NavItem) => (
    <NavLink key={item.to} to={item.to} end={item.end} className="nav-link">
      <Icon name={item.icon} />
      {item.label}
      {item.badge ? (
        <span className="nav-badge">
          {item.badge}
          <span className="sr-only"> open</span>
        </span>
      ) : null}
    </NavLink>
  );
  return (
    <nav className="sidebar" aria-label="Main">
      <Link to="/" className="brand">
        <span className="brand__mark" aria-hidden="true">
          <Icon name="doc" size={18} />
        </span>
        <span className="stack">
          <span className="brand__name">DAFA Contracts</span>
          <span className="caption">Group workspace</span>
        </span>
      </Link>
      <span className="nav-group">Work</span>
      {work.map(link)}
      {manage.length > 0 ? <span className="nav-group">Manage</span> : null}
      {manage.map(link)}
      <div style={{ flex: '1 1 auto', minHeight: 24 }} />
      <button type="button" className="user-card" onClick={onUser} aria-haspopup="dialog" aria-label={`${me.name}, ${accessLabel(me, lookups)}. ${me.authMode === 'Dev' ? 'Switch demo user' : 'Account'}`}>
        <Avatar initials={me.initials} solid />
        <span className="stack" style={{ minWidth: 0 }}>
          <span className="user-card__name truncate">{me.name}</span>
          <span className="caption truncate">{accessLabel(me, lookups)}</span>
        </span>
        <span className="user-card__chev" aria-hidden="true">
          <Icon name="chevronDown" size={14} stroke={2.4} />
        </span>
      </button>
      {me.authMode === 'Dev' ? <span className="mode-note">{apiMode === 'mock' ? 'Dev mode · sample data in the browser' : 'Dev mode · test data'}</span> : null}
    </nav>
  );
}

/** Top-bar scope value while a link or saved view names several legal entities. */
const SEVERAL = '\u0000several';

function TopBar({ onUser }: { onUser: () => void }) {
  const me = useMe();
  const lookups = useLookups();
  const scope = useScope();
  return (
    <header className="topbar">
      <GlobalSearch />
      <div className="spacer" />
      <Select
        className="scope-select"
        aria-label="Legal entity scope"
        value={scope.entityIds.length > 1 ? SEVERAL : scope.entityId}
        onChange={(e) => (e.target.value === SEVERAL ? undefined : scope.setEntityId(e.target.value))}
        placeholder="All entities"
        options={[...(scope.entityIds.length > 1 ? [{ value: SEVERAL, label: `${scope.entityIds.length} legal entities` }] : []), ...lookups.entities.map((e) => ({ value: e.id, label: e.name }))]}
      />
      <NotificationBell />
      {/* Pages add their own actions here, beside "New contract" (the register's Export). */}
      <div id="topbar-actions" className="topbar__actions" />
      {permissions(me).canCreate ? (
        <ButtonLink to="/contracts/new" icon="plus" aria-label="New contract">
          <span>New contract</span>
        </ButtonLink>
      ) : null}
      <button type="button" className="topbar__avatar" onClick={onUser} aria-haspopup="dialog" aria-label={`${me.name}. ${me.authMode === 'Dev' ? 'Switch demo user' : 'Account'}`}>
        <Avatar initials={me.initials} solid size="lg" />
      </button>
    </header>
  );
}

function TabBar({ items }: { items: NavItem[] }) {
  return (
    <nav className="tabbar" aria-label="Main, phone">
      {items.map((item) => (
        <NavLink key={item.to} to={item.to} end={item.end} className="tabbar__link">
          <Icon name={item.icon} size={22} stroke={1.9} />
          {item.label}
          {item.badge ? (
            <span className="tabbar__badge">
              {item.badge}
              <span className="sr-only"> open</span>
            </span>
          ) : null}
        </NavLink>
      ))}
    </nav>
  );
}

const STARTED = 'dafa.started';

/**
 * Once per browser session: open the user's landing page (personal setting, else the role
 * default from General settings) and apply their default entity scope when the link has none.
 */
function useStartPreferences() {
  const config = useAppConfig();
  const me = useMe();
  const scope = useScope();
  const navigate = useNavigate();
  const location = useLocation();
  const done = useRef(false);
  useEffect(() => {
    if (done.current || !config.loaded) return;
    done.current = true;
    let started = false;
    try {
      started = sessionStorage.getItem(STARTED) === '1';
      sessionStorage.setItem(STARTED, '1');
    } catch {
      /* storage unavailable */
    }
    if (started) return;
    // The scope lives only in the URL, so the landing page navigation must carry it itself.
    // Order: the link's own scope, else the user's last scope on this device, else their default.
    const stored = readStoredScope(me.id);
    const entity = scope.entityIds.length > 0 ? null : stored !== null ? stored || null : config.defaultEntityId || null;
    if (location.pathname === '/' && config.landingPage !== 'home') navigate(`/${config.landingPage}${entity ? `?entity=${encodeURIComponent(entity)}` : location.search}`, { replace: true });
    else if (entity) scope.setEntityId(entity);
  }, [config.loaded, config.defaultEntityId, config.landingPage, scope, navigate, location.pathname, location.search, me.id]);

  // Remember the scope per user on this device (restored at the next session start).
  const current = scope.entityIds.length === 1 ? (scope.entityIds[0] ?? '') : '';
  useEffect(() => {
    if (!done.current) return;
    try {
      localStorage.setItem(`${SCOPE_KEY}.${me.id}`, current);
    } catch {
      /* storage unavailable */
    }
  }, [current, me.id]);
}

const SCOPE_KEY = 'dafa.scope';
/** The user's last scope on this device: an id, '' (explicitly all entities) or null (never set). */
function readStoredScope(userId: string): string | null {
  try {
    return localStorage.getItem(`${SCOPE_KEY}.${userId}`);
  } catch {
    return null;
  }
}

/** Sidebar + top bar + routed page; the sidebar becomes a bottom tab bar under 720px. */
export function AppShell() {
  const me = useMe();
  const perms = permissions(me);
  const counts = useNavCounts();
  const [switching, setSwitching] = useState(false);
  const location = useLocation();
  const mainRef = useRef<HTMLElement>(null);

  // Move focus to the page on navigation so keyboard and screen-reader users land on new content.
  const first = useRef(true);
  useEffect(() => {
    if (first.current) {
      first.current = false;
      return;
    }
    window.scrollTo(0, 0);
    mainRef.current?.focus({ preventScroll: true });
  }, [location.pathname]);

  useStartPreferences();

  const work: NavItem[] = [
    { to: '/', label: 'My work', icon: 'home', badge: counts.tasks, end: true },
    { to: '/contracts', label: 'Contracts', icon: 'doc' },
    { to: '/renewals', label: 'Renewals', icon: 'calendar' },
    { to: '/approvals', label: 'Approvals', icon: 'check', badge: counts.approvals },
  ];
  const tabs = work;
  const workNav: NavItem[] = [...work, { to: '/search', label: 'Search', icon: 'search' }];
  const manage: NavItem[] = [
    ...(perms.canSeeOverview ? [{ to: '/overview', label: 'Overview', icon: 'chart' as const }] : []),
    ...(perms.canSeeSettings ? [{ to: '/settings', label: 'Settings', icon: 'gear' as const }] : []),
  ];

  return (
    <div className="shell">
      <a href="#main-content" className="skip-link" onClick={(e) => { e.preventDefault(); mainRef.current?.focus(); }}>
        Skip to content
      </a>
      <Sidebar work={workNav} manage={manage} onUser={() => setSwitching(true)} />
      <div className="main">
        <TopBar onUser={() => setSwitching(true)} />
        <main ref={mainRef} id="main-content" tabIndex={-1} style={{ outline: 'none' }}>
          <Suspense
            fallback={
              <div className="page" style={{ paddingTop: 10 }} role="status" aria-label="Loading page">
                <Skeleton width={220} height={30} />
                <Skeleton height={180} radius={18} />
              </div>
            }
          >
            <RouteErrorBoundary key={location.pathname}>
              <Outlet context={{ openUser: () => setSwitching(true) }} />
            </RouteErrorBoundary>
          </Suspense>
        </main>
      </div>
      <TabBar items={tabs} />
      {switching ? <UserSwitcher onClose={() => setSwitching(false)} /> : null}
    </div>
  );
}
