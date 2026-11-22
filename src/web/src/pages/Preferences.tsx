/* eslint-disable react-refresh/only-export-components */
import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi, useLookups, useMe } from '../app/context';
import type { ThemePreference, DigestFrequency, LandingPage, UserPreferences } from '../api/types';
import { Button, Card, CardTitle, ErrorState, Field, Segmented, Select, SkeletonRows, useToast } from '../components';
import { applyTheme } from '../lib/theme';

export const LANDING_OPTIONS: { value: LandingPage; label: string }[] = [
  { value: 'home', label: 'My work' },
  { value: 'contracts', label: 'Contracts' },
  { value: 'renewals', label: 'Renewals' },
  { value: 'approvals', label: 'Approvals' },
  { value: 'overview', label: 'Overview' },
];

/** Personal settings: notifications, digest, default entity scope and landing page. */
export function Preferences() {
  const api = useApi();
  const me = useMe();
  const lookups = useLookups();
  const toast = useToast();
  const client = useQueryClient();
  const q = useQuery({ queryKey: qk.preferences, queryFn: () => api.getPreferences() });
  const [draft, setDraft] = useState<UserPreferences | null>(null);
  useEffect(() => {
    if (q.data && !draft) setDraft(q.data);
  }, [q.data, draft]);
  const save = useMutation({
    mutationFn: (p: UserPreferences) => api.updatePreferences(p),
    onSuccess: async (p) => {
      setDraft(p);
      client.setQueryData(qk.preferences, p);
      await client.invalidateQueries({ queryKey: qk.appConfig });
      toast.show('Your settings are saved');
    },
    onError: (e) => toast.error(e),
  });

  const themeSave = useMutation({
    mutationFn: (p: UserPreferences) => api.updatePreferences(p),
    onSuccess: (p) => {
      client.setQueryData(qk.preferences, p);
      toast.show(`Theme: ${p.theme === 'dark' ? 'dark' : p.theme === 'system' ? 'follows this device' : 'light'}`);
    },
    onError: (e) => toast.error(e, 'The theme could not be saved for your account; it applies on this device.'),
  });
  const set = (patch: Partial<UserPreferences>) => setDraft((d) => (d ? { ...d, ...patch } : d));
  const setEvent = (event: string, patch: Partial<UserPreferences['events'][number]>) =>
    setDraft((d) => (d ? { ...d, events: d.events.map((e) => (e.event === event ? { ...e, ...patch } : e)) } : d));

  return (
    <div className="page rise" style={{ paddingTop: 10 }}>
      <div className="page-head">
        <div className="page-head__text">
          <h1 className="h1">My settings</h1>
          <span className="page-sub">
            {me.name} · {me.email}
          </span>
        </div>
        <div className="spacer" />
        <Button loading={save.isPending} disabled={!draft} onClick={() => draft && save.mutate(draft)}>
          Save changes
        </Button>
      </div>
      {q.isError ? (
        <Card>
          <ErrorState onRetry={() => void q.refetch()} />
        </Card>
      ) : !draft ? (
        <Card>
          <SkeletonRows rows={6} label="Loading your settings" />
        </Card>
      ) : (
        <div className="settings-grid">
          <Card pad className="stack" style={{ gap: 16 }}>
            <CardTitle>Appearance</CardTitle>
            <div className="stack" style={{ gap: 8 }}>
              <span className="field__label" id="theme-label">
                Theme
              </span>
              <Segmented
                mode="radio"
                ariaLabel="Theme"
                options={[
                  { value: 'light', label: 'Light' },
                  { value: 'dark', label: 'Dark' },
                  { value: 'system', label: 'System' },
                ]}
                value={draft.theme ?? 'light'}
                onChange={(v) => {
                  const theme = v as ThemePreference;
                  // Applies at once and is saved at once: no need to press Save for the theme.
                  applyTheme(theme);
                  set({ theme });
                  themeSave.mutate({ ...draft, theme });
                }}
              />
              <span className="field__hint">System follows the light or dark setting of this device.</span>
            </div>
          </Card>
          <Card pad className="stack" style={{ gap: 16 }}>
            <CardTitle>Start and scope</CardTitle>
            <Field label="Open the app on" hint="Leave on the default to follow what the administrator set for your role.">
              {(p) => <Select {...p} value={draft.landingPage ?? ''} placeholder="Default for my role" options={LANDING_OPTIONS} onChange={(e) => set({ landingPage: (e.target.value || null) as LandingPage | null })} />}
            </Field>
            <Field label="Default legal entity scope" hint="Applied when you open the app without a scope in the link.">
              {(p) => <Select {...p} value={draft.defaultEntityId ?? ''} placeholder="All entities" options={lookups.entities.filter((e) => e.isActive !== false).map((e) => ({ value: e.id, label: e.name }))} onChange={(e) => set({ defaultEntityId: e.target.value || null })} />}
            </Field>
            <label className="check">
              <input type="checkbox" checked={draft.autoWatch} onChange={(e) => set({ autoWatch: e.target.checked })} />
              Watch the contracts I create or am added to
            </label>
          </Card>
          <Card pad className="stack" style={{ gap: 16 }}>
            <CardTitle>Digest</CardTitle>
            <p className="secondary prose">A summary e-mail of your tasks, notice deadlines and changes on watched contracts.</p>
            <Segmented mode="radio" ariaLabel="Digest" options={[{ value: 'Off', label: 'Off' }, { value: 'Daily', label: 'Daily' }, { value: 'Weekly', label: 'Weekly' }]} value={draft.digest} onChange={(v) => set({ digest: v as DigestFrequency })} />
          </Card>
          <Card clip className="settings-grid__wide">
            <div className="card-list-head card-list-head--row">
              <h2 className="card-title">Notifications</h2>
              <span className="caption">Channels an administrator has turned off for an event cannot be turned on here.</span>
            </div>
            <div className="table-wrap">
              <table className="table">
                <caption className="sr-only">Notification preferences</caption>
                <thead>
                  <tr>
                    <th scope="col">Event</th>
                    <th scope="col">E-mail</th>
                    <th scope="col">Teams</th>
                    <th scope="col">In the app</th>
                  </tr>
                </thead>
                <tbody>
                  {draft.events.map((e) => (
                    <tr key={e.event}>
                      <th scope="row" style={{ fontWeight: 600 }}>
                        {e.label}
                      </th>
                      {(['email', 'teams', 'inApp'] as const).map((ch) => (
                        <td key={ch}>
                          <input type="checkbox" className="check__box" aria-label={`${e.label}: ${ch === 'inApp' ? 'in the app' : ch === 'email' ? 'e-mail' : 'Teams'}`} checked={e[ch]} onChange={(x) => setEvent(e.event, { [ch]: x.target.checked })} />
                        </td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Card>
        </div>
      )}
    </div>
  );
}
