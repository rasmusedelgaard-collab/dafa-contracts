import { lazy, Suspense, useEffect, useState, type ReactNode } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Route, Routes } from 'react-router-dom';
import { ApiError } from './api/api';
import type { DevConfig } from './api/types';
import { qk, ScopeProvider, SessionProvider, useApi, useLookupsQuery, useMeQuery, useSessionActions } from './app/context';
import { browser, currentPath, entraLoginUrl, isSignedOut, markSignedOut, readAccessCode, redirectedRecently, storeAccessCode } from './app/signin';
import { isThrottled, THROTTLED_MESSAGE } from './lib/errors';
import { Avatar, Button, Card, ErrorState, Field, Icon, Skeleton, SkeletonRows, TextInput, useToast } from './components';
import { Home } from './pages/Home';
import { NotFound } from './pages/NotFound';
import { Register } from './pages/Register';
import { AppShell } from './shell/AppShell';

// Split by route: Home and the register load with the shell, everything else on demand.
const Record = lazy(() => import('./pages/record/Record').then((m) => ({ default: m.Record })));
const NewContract = lazy(() => import('./pages/newcontract/NewContract').then((m) => ({ default: m.NewContract })));
const Approvals = lazy(() => import('./pages/Approvals').then((m) => ({ default: m.Approvals })));
const Renewals = lazy(() => import('./pages/renewals/Renewals').then((m) => ({ default: m.Renewals })));
const Overview = lazy(() => import('./pages/overview/Overview').then((m) => ({ default: m.Overview })));
const Search = lazy(() => import('./pages/search/Search').then((m) => ({ default: m.Search })));
const PersonPage = lazy(() => import('./pages/search/People').then((m) => ({ default: m.PersonPage })));
const CounterpartyPage = lazy(() => import('./pages/search/People').then((m) => ({ default: m.CounterpartyPage })));
const Preferences = lazy(() => import('./pages/Preferences').then((m) => ({ default: m.Preferences })));
const Settings = lazy(() => import('./pages/settings/Settings').then((m) => ({ default: m.Settings })));

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

function SignInCard({ title, children }: { title: string; children: ReactNode }) {
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
        <h1 className="h2">{title}</h1>
        {children}
      </Card>
    </div>
  );
}

/** Entra mode: a 401 sends the browser to the API's sign-in endpoint and back to this page. */
function EntraSignIn() {
  const url = entraLoginUrl(currentPath());
  const [looping] = useState(() => redirectedRecently());
  const [signedOut] = useState(() => isSignedOut());
  useEffect(() => {
    if (!looping && !signedOut) browser.redirect(url);
  }, [looping, signedOut, url]);
  return (
    <SignInCard title={signedOut ? 'You are signed out' : looping ? 'Sign-in did not complete' : 'Signing you in'}>
      {signedOut ? (
        <>
          <p className="secondary">You have signed out of DAFA Contracts.</p>
          <a className="btn btn--block" href={url}>
            Sign in with your DAFA account
          </a>
        </>
      ) : looping ? (
        <>
          <p className="secondary">You were sent to sign in with your DAFA account, but the session was not started. Try again, or contact an administrator if it keeps happening.</p>
          <a className="btn btn--block" href={url}>
            Sign in with your DAFA account
          </a>
        </>
      ) : (
        <p className="secondary" role="status">
          Redirecting to sign in with your DAFA account. <a href={url}>Continue</a> if nothing happens.
        </p>
      )}
    </SignInCard>
  );
}

/** Test site with a shared access code: asked once per browser tab, before the demo-user picker. */
function AccessCodeForm({ notice, onAccepted }: { notice?: string; onAccepted: (code: string) => void }) {
  const api = useApi();
  const client = useQueryClient();
  const toast = useToast();
  const [code, setCode] = useState('');
  const [typedError, setError] = useState<string>();
  const [touched, setTouched] = useState(false);
  // The notice (a remembered code that stopped working) shows until the user starts typing.
  const error = typedError ?? (touched ? undefined : notice);
  const check = useMutation({
    mutationFn: (value: string) => api.getDevUsers(value),
    onSuccess: (users, value) => {
      storeAccessCode(value);
      client.setQueryData(qk.devUsers, users);
      onAccepted(value);
    },
    onError: (e) => {
      if (isThrottled(e)) setError(THROTTLED_MESSAGE);
      else if (e instanceof ApiError && (e.status === 401 || e.status === 403)) setError('The access code is not correct');
      else toast.error(e);
    },
  });
  const submit = () => {
    const value = code.trim();
    if (!value) setError('Enter the access code');
    else check.mutate(value);
  };
  return (
    <SignInCard title="Enter the access code">
      <p className="secondary">This is a test environment with test data only. Enter the access code you were given to continue.</p>
      <form
        className="stack"
        style={{ gap: 14 }}
        noValidate
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
      >
        <Field label="Access code" error={error}>
          {(p) => (
            <TextInput
              {...p}
              type="password"
              autoComplete="off"
              autoFocus
              value={code}
              onChange={(e) => {
                setCode(e.target.value);
                setTouched(true);
                if (typedError) setError(undefined);
              }}
            />
          )}
        </Field>
        <Button type="submit" block loading={check.isPending}>
          Continue
        </Button>
      </form>
    </SignInCard>
  );
}

/** Dev mode: pick a seeded demo user (after the access code, when the site requires one). */
function DevSignIn({ config }: { config: DevConfig }) {
  const api = useApi();
  const client = useQueryClient();
  const session = useSessionActions();
  const toast = useToast();
  const [code, setCode] = useState(() => readAccessCode());
  const [notice, setNotice] = useState<string>();
  const needsCode = config.accessCodeRequired && !code;

  const users = useQuery({ queryKey: qk.devUsers, queryFn: () => api.getDevUsers(code), retry: false, enabled: !needsCode });
  const login = useMutation({
    mutationFn: (userId: string) => api.devLogin(userId, code),
    onSuccess: (me) => {
      // `dev/login` returns Me: show the app right away and drop anything loaded while signed out.
      markSignedOut(false);
      session.signedIn(me);
    },
    onError: (e) => toast.error(e),
  });

  // A remembered code the server no longer accepts: forget it and ask again.
  const rejected = config.accessCodeRequired && !!code && users.error instanceof ApiError && (users.error.status === 401 || users.error.status === 403);
  useEffect(() => {
    if (!rejected) return;
    storeAccessCode(null);
    client.removeQueries({ queryKey: qk.devUsers });
    setNotice('The access code is no longer valid. Enter it again');
    setCode(undefined);
  }, [rejected, client]);

  if (needsCode || rejected) {
    return (
      <AccessCodeForm
        notice={notice}
        onAccepted={(value) => {
          setNotice(undefined);
          setCode(value);
        }}
      />
    );
  }

  return (
    <SignInCard title="Sign in">
      {users.isLoading ? (
        <SkeletonRows rows={4} label="Loading demo users" />
      ) : users.isError ? (
        <p className="secondary" role="alert">
          {isThrottled(users.error) ? THROTTLED_MESSAGE : 'Sign-in with a demo user is not available on this site. Reload the page to sign in with your DAFA account, or contact an administrator.'}
        </p>
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
    </SignInCard>
  );
}

function StartError({ text, onRetry }: { text: string; onRetry: () => void }) {
  return (
    <div className="center-page">
      <Card className="center-card">
        <ErrorState title="DAFA Contracts could not start" text={text} onRetry={onRetry} />
      </Card>
    </div>
  );
}

export function App() {
  const api = useApi();
  // Anonymous, asked on start: decides between the demo-user picker and the Entra redirect.
  const config = useQuery({ queryKey: qk.devConfig, queryFn: () => api.getDevConfig(), retry: false, staleTime: Infinity });
  const me = useMeQuery();
  const lookups = useLookupsQuery(!!me.data);

  if (me.isLoading) return <Splash />;
  if (me.isError || !me.data) {
    if (me.error instanceof ApiError && me.error.status === 401) {
      if (config.isLoading) return <Splash />;
      // An API without `dev/config` behaves as before: offer the demo-user picker.
      const mode: DevConfig = config.data ?? { authMode: 'Dev', accessCodeRequired: false };
      return mode.authMode === 'Entra' ? <EntraSignIn /> : <DevSignIn config={mode} />;
    }
    return <StartError text="The server did not answer. Check your connection and try again." onRetry={() => void me.refetch()} />;
  }
  if (lookups.isError) return <StartError text="The reference data could not be loaded." onRetry={() => void lookups.refetch()} />;
  if (!lookups.data) return <Splash />;

  return (
    // Keyed on the user: switching demo user rebuilds the shell, navigation and every page.
    <SessionProvider key={me.data.id} me={me.data} lookups={lookups.data}>
      <ScopeProvider>
        <Suspense fallback={<Splash />}>
          <Routes>
            <Route path="/contracts/new" element={<NewContract />} />
            <Route element={<AppShell />}>
              <Route index element={<Home />} />
              <Route path="/contracts" element={<Register />} />
              <Route path="/contracts/:id" element={<Record />} />
              <Route path="/approvals" element={<Approvals />} />
              <Route path="/renewals" element={<Renewals />} />
              <Route path="/overview" element={<Overview />} />
              <Route path="/search" element={<Search />} />
              <Route path="/people/:id" element={<PersonPage />} />
              <Route path="/counterparties/:id" element={<CounterpartyPage />} />
              <Route path="/preferences" element={<Preferences />} />
              <Route path="/settings" element={<Settings />} />
              <Route path="/settings/:section" element={<Settings />} />
              <Route path="*" element={<NotFound />} />
            </Route>
          </Routes>
        </Suspense>
      </ScopeProvider>
    </SessionProvider>
  );
}
