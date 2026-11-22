import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Route, Routes } from 'react-router-dom';
import { ApiError } from './api/api';
import { qk, ScopeProvider, SessionProvider, useApi, useLookupsQuery, useMeQuery } from './app/context';
import { Avatar, Card, ErrorState, Icon, Skeleton, SkeletonRows, useToast } from './components';
import { Home } from './pages/Home';
import { NewContract } from './pages/newcontract/NewContract';
import { ApprovalsPage, NotFound, OverviewPage, RenewalsPage, SettingsPage } from './pages/Placeholder';
import { Record } from './pages/record/Record';
import { Register } from './pages/Register';
import { AppShell } from './shell/AppShell';

function Splash() {
  return (
    <div className="center-page" role="status" aria-label="Loading DAFA Contracts">
      <div className="stack" style={{ gap: 14, alignItems: 'center' }}>
        <span className="brand__mark" aria-hidden="true">
          <Icon name="doc" size={18} />
        </span>
        <Skeleton width={160} height={12} />
      </div>
    </div>
  );
}

/** Shown when the API answers 401: pick a seeded demo user (Auth:Mode=Dev). */
function SignIn() {
  const api = useApi();
  const client = useQueryClient();
  const toast = useToast();
  const users = useQuery({ queryKey: qk.devUsers, queryFn: () => api.getDevUsers(), retry: false });
  const login = useMutation({
    mutationFn: (userId: string) => api.devLogin(userId),
    onSuccess: async () => {
      client.clear();
      await client.invalidateQueries();
    },
    onError: (e) => toast.error(e),
  });
  return (
    <div className="center-page">
      <Card className="center-card">
        <div className="brand" style={{ padding: 0 }}>
          <span className="brand__mark" aria-hidden="true">
            <Icon name="doc" size={18} />
          </span>
          <span className="stack">
            <span className="brand__name">DAFA Contracts</span>
            <span className="caption">Group workspace</span>
          </span>
        </div>
        <h1 className="h2">Sign in</h1>
        {users.isLoading ? (
          <SkeletonRows rows={4} label="Loading demo users" />
        ) : users.isError ? (
          <p className="secondary">Sign-in with a demo user is not available on this site. Reload the page to sign in with your DAFA account, or contact an administrator.</p>
        ) : (
          <>
            <p className="secondary">Test environment. Choose a demo user; each one has different access.</p>
            <div className="user-list">
              {(users.data ?? []).map((u) => (
                <button key={u.id} type="button" className="user-opt" disabled={login.isPending} onClick={() => login.mutate(u.id)}>
                  <Avatar initials={u.initials} />
                  <span className="kv">
                    <span className="user-opt__name">{u.name}</span>
                    <span className="kv__k">{u.title}</span>
                  </span>
                </button>
              ))}
            </div>
          </>
        )}
      </Card>
    </div>
  );
}

export function App() {
  const me = useMeQuery();
  const lookups = useLookupsQuery(!!me.data);

  if (me.isLoading) return <Splash />;
  if (me.isError || !me.data) {
    if (me.error instanceof ApiError && me.error.status === 401) return <SignIn />;
    return (
      <div className="center-page">
        <Card className="center-card">
          <ErrorState title="DAFA Contracts could not start" text="The server did not answer. Check your connection and try again." onRetry={() => void me.refetch()} />
        </Card>
      </div>
    );
  }
  if (lookups.isError) {
    return (
      <div className="center-page">
        <Card className="center-card">
          <ErrorState title="DAFA Contracts could not start" text="The reference data could not be loaded." onRetry={() => void lookups.refetch()} />
        </Card>
      </div>
    );
  }
  if (!lookups.data) return <Splash />;

  return (
    <SessionProvider me={me.data} lookups={lookups.data}>
      <ScopeProvider>
        <Routes>
          <Route path="/contracts/new" element={<NewContract />} />
          <Route element={<AppShell />}>
            <Route index element={<Home />} />
            <Route path="/contracts" element={<Register />} />
            <Route path="/contracts/:id" element={<Record />} />
            <Route path="/approvals" element={<ApprovalsPage />} />
            <Route path="/renewals" element={<RenewalsPage />} />
            <Route path="/overview" element={<OverviewPage />} />
            <Route path="/settings" element={<SettingsPage />} />
            <Route path="*" element={<NotFound />} />
          </Route>
        </Routes>
      </ScopeProvider>
    </SessionProvider>
  );
}
