import type { ReactNode } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Navigate, NavLink, useNavigate, useParams } from 'react-router-dom';
import { apiMode } from '../../api';
import { permissions, useApi, useInvalidateContracts, useMe } from '../../app/context';
import { Button, Select, useToast } from '../../components';
import { plural } from '../../lib/format';
import { NoAccess, NotFound } from '../NotFound';
import { ApprovalRules } from './ApprovalRules';
import { AuditLog, Outbox } from './Logs';
import { ContractTypes, Departments } from './NamedLists';
import { Counterparties } from './Counterparties';
import { CustomFields, FieldRulesAdmin } from './Fields';
import { Imports } from './Imports';
import { CurrenciesAdmin, LegalEntitiesAdmin, TagsAdmin } from './Reference';
import { SyncSources } from './Sync';
import { GeneralAdmin, JobsAdmin, NotificationsAdmin } from './System';
import { UsersAdmin } from './Users';
import { HolidayCover, Reminders, Retention } from './Policies';
import { Templates } from './Templates';

const SECTIONS: { id: string; label: string; group: string; render: () => ReactNode }[] = [
  { id: 'entities', label: 'Legal entities', group: 'Organization', render: () => <LegalEntitiesAdmin /> },
  { id: 'departments', label: 'Departments', group: 'Organization', render: () => <Departments /> },
  { id: 'contract-types', label: 'Contract types', group: 'Organization', render: () => <ContractTypes /> },
  { id: 'users', label: 'Users and access', group: 'Organization', render: () => <UsersAdmin /> },
  { id: 'currencies', label: 'Currencies', group: 'Organization', render: () => <CurrenciesAdmin /> },
  { id: 'tags', label: 'Tags', group: 'Organization', render: () => <TagsAdmin /> },
  { id: 'counterparties', label: 'Counterparties', group: 'Data', render: () => <Counterparties /> },
  { id: 'imports', label: 'Imports', group: 'Data', render: () => <Imports /> },
  { id: 'sync', label: 'Sync sources', group: 'Data', render: () => <SyncSources /> },
  { id: 'custom-fields', label: 'Custom fields', group: 'Data', render: () => <CustomFields /> },
  { id: 'field-rules', label: 'Field rules', group: 'Data', render: () => <FieldRulesAdmin /> },
  { id: 'approval-rules', label: 'Approval rules', group: 'Rules', render: () => <ApprovalRules /> },
  { id: 'reminders', label: 'Reminders', group: 'Rules', render: () => <Reminders /> },
  { id: 'notifications', label: 'Notifications', group: 'Rules', render: () => <NotificationsAdmin /> },
  { id: 'retention', label: 'Retention', group: 'Rules', render: () => <Retention /> },
  { id: 'templates', label: 'Templates', group: 'Rules', render: () => <Templates /> },
  { id: 'holiday-cover', label: 'Holiday cover', group: 'Rules', render: () => <HolidayCover /> },
  { id: 'general', label: 'General', group: 'System', render: () => <GeneralAdmin /> },
  { id: 'jobs', label: 'Jobs', group: 'System', render: () => <JobsAdmin /> },
  { id: 'audit', label: 'Audit log', group: 'System', render: () => <AuditLog /> },
  { id: 'outbox', label: 'Notifications outbox', group: 'System', render: () => <Outbox /> },
];
const GROUPS = ['Organization', 'Data', 'Rules', 'System'];

function RunJobs() {
  const api = useApi();
  const client = useQueryClient();
  const toast = useToast();
  const invalidate = useInvalidateContracts();
  const run = useMutation({
    mutationFn: () => api.admin.runJobs(),
    onSuccess: async (r) => {
      await Promise.all([invalidate(), client.invalidateQueries({ queryKey: ['admin'] })]);
      toast.show(`Jobs run: ${plural(r.remindersCreated, 'reminder')}, ${plural(r.escalations, 'escalation')}, ${plural(r.obligationReminders, 'obligation reminder')}, ${plural(r.annualReviewReminders ?? 0, 'annual review reminder')}, ${plural(r.contractsExpired, 'contract')} expired`);
    },
    onError: (e) => toast.error(e, 'The jobs could not be run. Try again.'),
  });
  return (
    <Button variant="secondary" icon="refresh" loading={run.isPending} onClick={() => run.mutate()} title="Reminders, escalations, obligation reminders and expiry normally run every hour">
      Run jobs now
    </Button>
  );
}

export function Settings() {
  const me = useMe();
  const navigate = useNavigate();
  const { section: sectionId } = useParams();
  // Admin only. The API enforces the same rule on every admin endpoint.
  if (!permissions(me).canSeeSettings) return <NoAccess title="You do not have access to Settings" text="Settings are for administrators. Ask an administrator if a department, contract type, approval rule or template needs to change." />;

  if (!sectionId) return <Navigate to="/settings/entities" replace />;
  const section = SECTIONS.find((s) => s.id === sectionId);
  if (!section) return <NotFound />;

  return (
    <div className="page rise" style={{ paddingTop: 10 }}>
      <div className="page-head">
        <div className="page-head__text">
          <h1 className="h1">Settings</h1>
          <span className="page-sub">Organization, master data, rules and system settings</span>
        </div>
        <div className="spacer" />
        <RunJobs />
      </div>

      <div className="settings">
        <nav className="settings__nav" aria-label="Settings">
          {GROUPS.map((g) => (
            <div key={g} className="settings__group">
              <span className="nav-group">{g}</span>
              {SECTIONS.filter((s) => s.group === g).map((s) => (
                <NavLink key={s.id} to={`/settings/${s.id}`} className="nav-link">
                  {s.label}
                </NavLink>
              ))}
            </div>
          ))}
        </nav>
        {/* Under 1000px the side list becomes one select, which is easier to reach than ten links. */}
        <Select className="settings__select" aria-label="Settings section" value={section.id} onChange={(e) => navigate(`/settings/${e.target.value}`)} options={SECTIONS.map((s) => ({ value: s.id, label: s.label }))} />
        <div className="settings__main" key={section.id}>
          {section.render()}
        </div>
      </div>
      {apiMode === 'mock' ? <p className="caption">Sample data. Changes stay in the browser and reset when the page reloads.</p> : null}
    </div>
  );
}
