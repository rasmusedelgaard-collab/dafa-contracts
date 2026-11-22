// Settings > E-signature (owner item H): provider, defaults, authorised signatories per entity.
import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk, useApi, useLookups } from '../../app/context';
import type { ESignProvider, ESignSettings } from '../../api/types';
import { Button, Card, ErrorState, Field, NumberValueInput, Pill, Select, SkeletonRows, Textarea, TextInput, useToast } from '../../components';
import { PersonPicker } from '../../features/Pickers';
import { SectionHead } from './common';
import { problemErrors } from './Reference';

const PROVIDERS: { value: ESignProvider; label: string }[] = [
  { value: 'None', label: 'None (turned off)' },
  { value: 'Simulated', label: 'Simulated (test site)' },
  { value: 'DocuSign', label: 'DocuSign' },
  { value: 'Penneo', label: 'Penneo' },
];

export function ESignAdmin() {
  const api = useApi();
  const lookups = useLookups();
  const client = useQueryClient();
  const toast = useToast();
  const q = useQuery({ queryKey: qk.admin('esign'), queryFn: () => api.admin.getESignSettings() });
  const [s, setS] = useState<ESignSettings | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [adding, setAdding] = useState<Record<string, string | null>>({});
  useEffect(() => {
    if (q.data && !s) setS(q.data);
  }, [q.data, s]);
  const save = useMutation({
    mutationFn: (x: ESignSettings) => api.admin.updateESignSettings(x),
    onSuccess: async (x) => {
      setS(x);
      setErrors({});
      await Promise.all([client.invalidateQueries({ queryKey: qk.appConfig }), client.invalidateQueries({ queryKey: ['contracts'] })]);
      toast.show('E-signature settings saved');
    },
    onError: (e) => {
      setErrors(problemErrors(e));
      toast.error(e);
    },
  });
  if (!s) return q.isError ? <ErrorState text="The e-signature settings could not be loaded." onRetry={() => void q.refetch()} /> : <SkeletonRows rows={6} />;
  const row = (entityId: string) => s.authorisedSignatories.find((a) => a.entityId === entityId) ?? { entityId, userIds: [], externalNames: [] };
  const setRow = (entityId: string, patch: Partial<ESignSettings['authorisedSignatories'][number]>) =>
    setS({ ...s, authorisedSignatories: [...s.authorisedSignatories.filter((a) => a.entityId !== entityId), { ...row(entityId), ...patch }].filter((a) => a.userIds.length || a.externalNames.length) });
  return (
    <>
      <SectionHead title="E-signature" text="Sending approved contracts for signature outside DAFA. Who may send is set per person in Users and access." action={<Button loading={save.isPending} onClick={() => save.mutate(s)}>Save changes</Button>} />
      <div className="settings-grid">
        <Card pad className="stack" style={{ gap: 14 }}>
          <h3 className="card-title">Provider</h3>
          <Field label="Signing service" error={errors.provider} hint={s.provider === 'Simulated' ? 'Nothing is sent: the record gets a "Simulate signing" control for demonstrations.' : s.provider === 'None' ? 'The "Send for signature" action is hidden.' : 'The API keys are server configuration.'}>
            {(p) => <Select {...p} value={s.provider} options={PROVIDERS} onChange={(e) => setS({ ...s, provider: e.target.value as ESignProvider })} />}
          </Field>
          {s.provider === 'DocuSign' || s.provider === 'Penneo' ? (
            <Field label="Account" optional hint="The provider account id, if the server serves several.">
              {(p) => <TextInput {...p} value={s.providerAccount ?? ''} onChange={(e) => setS({ ...s, providerAccount: e.target.value || null })} />}
            </Field>
          ) : null}
          <label className="check">
            <input type="checkbox" checked={s.requireApproved} onChange={(e) => setS({ ...s, requireApproved: e.target.checked })} />
            Only approved contracts can be sent
          </label>
        </Card>
        <Card pad className="stack" style={{ gap: 14 }}>
          <h3 className="card-title">Defaults</h3>
          <Field label="Default message" optional>
            {(p) => <Textarea {...p} rows={3} value={s.defaultMessage} onChange={(e) => setS({ ...s, defaultMessage: e.target.value })} />}
          </Field>
          <Field label="Default signing order">
            {(p) => <Select {...p} value={s.defaultOrder} options={[{ value: 'Sequential', label: 'In order' }, { value: 'Parallel', label: 'All at once' }]} onChange={(e) => setS({ ...s, defaultOrder: e.target.value as ESignSettings['defaultOrder'] })} />}
          </Field>
          <Field label="Reminders after (days)" optional error={errors.reminderDays} hint="Comma-separated, up to 5 values, e.g. 3, 7">
            {(p) => <TextInput {...p} value={s.reminderDays.join(', ')} onChange={(e) => setS({ ...s, reminderDays: e.target.value.split(',').map((x) => Number(x.trim())).filter((n) => Number.isFinite(n) && n > 0) })} />}
          </Field>
          <Field label="Expires after (days)" error={errors.expiryDays}>
            {(p) => <NumberValueInput {...p} decimals={0} value={s.expiryDays} onNumberChange={(n) => setS({ ...s, expiryDays: n ?? 0 })} />}
          </Field>
        </Card>
        <Card pad className="stack settings-grid__full" style={{ gap: 14 }}>
          <h3 className="card-title">Authorised signatories per legal entity</h3>
          <label className="check">
            <input type="checkbox" checked={s.restrictToAuthorised} onChange={(e) => setS({ ...s, restrictToAuthorised: e.target.checked })} />
            Require at least one authorised signatory of the contract's entity on every request
          </label>
          {lookups.entities.filter((e) => e.isActive !== false).map((e) => {
            const r = row(e.id);
            return (
              <div key={e.id} className="signatory-row">
                <strong>{e.name}</strong>
                <span className="row-flex" style={{ gap: 6, flexWrap: 'wrap' }}>
                  {r.userIds.map((id) => (
                    <Pill key={id} tone="blue" small>
                      {lookups.users.find((u) => u.id === id)?.name ?? id}{' '}
                      <button type="button" className="pill__x" aria-label={`Remove ${lookups.users.find((u) => u.id === id)?.name ?? ''} from ${e.name}`} onClick={() => setRow(e.id, { userIds: r.userIds.filter((x) => x !== id) })}>
                        ×
                      </button>
                    </Pill>
                  ))}
                  {r.userIds.length === 0 ? <span className="caption">None</span> : null}
                </span>
                <PersonPicker
                  ariaLabel={`Add authorised signatory for ${e.name}`}
                  placeholder="Add a person"
                  value={adding[e.id] ?? null}
                  filter={(u) => !r.userIds.includes(u.id)}
                  onChange={(id) => {
                    if (id) setRow(e.id, { userIds: [...r.userIds, id] });
                    setAdding({ ...adding, [e.id]: null });
                  }}
                />
              </div>
            );
          })}
        </Card>
      </div>
    </>
  );
}
