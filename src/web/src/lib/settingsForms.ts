// Form models and validation for the Settings screens. Pure, so the rules are unit-tested.
import type { ApprovalRule, ApprovalRuleInput, ApproverRole, HolidayCoverInput, Lookups, ReminderSettings, RetentionAction, RetentionRuleInput } from '../api/types';
import { isIsoDate } from './dates';
import { formatMoneyCompact } from './format';
import { parseAmount, parseWhole } from './validation';

export const APPROVER_ROLES: { value: ApproverRole; label: string; hint: string }[] = [
  { value: 'DepartmentHead', label: 'Department head', hint: "Head of the contract's department" },
  { value: 'EntityManager', label: 'Entity management', hint: "Management of the contract's legal entity" },
  { value: 'GroupCFO', label: 'Group CFO', hint: 'Group CFO' },
  { value: 'GroupCEO', label: 'Group CEO', hint: 'Group CEO' },
  { value: 'Legal', label: 'Legal', hint: 'Legal' },
];
export const roleLabel = (role: ApproverRole | null): string => APPROVER_ROLES.find((r) => r.value === role)?.label ?? 'Approver';

// ----- Approval rule -----

export interface ApproverDraft {
  /** A role resolved per contract, or one named user. */
  kind: 'role' | 'user';
  role: ApproverRole | '';
  userId: string;
}
export interface StageDraft {
  /** Stable key for React; not sent to the API. */
  key: number;
  approvers: ApproverDraft[];
}
export interface RuleDraft {
  name: string;
  entityId: string;
  typeId: string;
  departmentId: string;
  minValue: string;
  maxValue: string;
  priority: string;
  stages: StageDraft[];
}
export interface RuleErrors {
  name?: string;
  minValue?: string;
  maxValue?: string;
  priority?: string;
  /** About the list of stages as a whole. */
  stages?: string;
  /** Per stage, by index: the stage has no approvers. */
  stage: Record<number, string>;
  /** Per approver, by `${stageIndex}-${approverIndex}`. */
  approver: Record<string, string>;
}

let nextKey = 1;
export const newStage = (approvers: ApproverDraft[] = [newApprover()]): StageDraft => ({ key: nextKey++, approvers });
export function newApprover(): ApproverDraft {
  return { kind: 'role', role: '', userId: '' };
}

export function emptyRuleDraft(): RuleDraft {
  return { name: '', entityId: '', typeId: '', departmentId: '', minValue: '', maxValue: '', priority: '0', stages: [newStage()] };
}

export function ruleToDraft(rule: ApprovalRule): RuleDraft {
  return {
    name: rule.name,
    entityId: rule.entityId ?? '',
    typeId: rule.typeId ?? '',
    departmentId: rule.departmentId ?? '',
    minValue: rule.minValue === null ? '' : String(rule.minValue),
    maxValue: rule.maxValue === null ? '' : String(rule.maxValue),
    priority: String(rule.priority),
    stages: [...rule.stages]
      .sort((a, b) => a.order - b.order)
      .map((s) => newStage(s.approvers.map((a): ApproverDraft => (a.userId ? { kind: 'user', role: '', userId: a.userId } : { kind: 'role', role: a.role ?? '', userId: '' })))),
  };
}

export function validateRuleDraft(d: RuleDraft): RuleErrors {
  const errors: RuleErrors = { stage: {}, approver: {} };
  if (!d.name.trim()) errors.name = 'Enter a name for the rule';

  const min = parseAmount(d.minValue);
  const max = parseAmount(d.maxValue);
  if (Number.isNaN(min)) errors.minValue = 'Enter an amount in DKK, or leave it empty for any value';
  if (Number.isNaN(max)) errors.maxValue = 'Enter an amount in DKK, or leave it empty for any value';
  if (min !== null && max !== null && !Number.isNaN(min) && !Number.isNaN(max) && min >= max) errors.maxValue = 'The upper value must be above the lower value';

  const priority = parseWhole(d.priority);
  if (priority === null || Number.isNaN(priority)) errors.priority = 'Enter a whole number, 0 or higher';

  if (d.stages.length === 0) errors.stages = 'Add at least one stage';
  d.stages.forEach((stage, si) => {
    if (stage.approvers.length === 0) {
      errors.stage[si] = 'Add at least one approver, or remove the stage';
      return;
    }
    const seen = new Set<string>();
    stage.approvers.forEach((a, ai) => {
      const id = a.kind === 'role' ? (a.role ? `role:${a.role}` : '') : a.userId ? `user:${a.userId}` : '';
      if (!id) errors.approver[`${si}-${ai}`] = a.kind === 'role' ? 'Choose a role' : 'Choose a person';
      else if (seen.has(id)) errors.approver[`${si}-${ai}`] = 'Already an approver in this stage';
      seen.add(id);
    });
  });
  return errors;
}

export function ruleHasErrors(e: RuleErrors): boolean {
  return !!(e.name || e.minValue || e.maxValue || e.priority || e.stages) || Object.keys(e.stage).length > 0 || Object.keys(e.approver).length > 0;
}

/** Only call with a draft that passed `validateRuleDraft`. Stage order follows the list. */
export function draftToRuleInput(d: RuleDraft): ApprovalRuleInput {
  return {
    name: d.name.trim(),
    entityId: d.entityId || null,
    typeId: d.typeId || null,
    departmentId: d.departmentId || null,
    minValue: parseAmount(d.minValue),
    maxValue: parseAmount(d.maxValue),
    priority: parseWhole(d.priority) ?? 0,
    stages: d.stages.map((s, i) => ({
      order: i + 1,
      approvers: s.approvers.map((a) => (a.kind === 'user' ? { userId: a.userId, role: null } : { userId: null, role: a.role as ApproverRole })),
    })),
  };
}

/** "DAFA A/S · IT and SaaS · DKK 1m to 5m", or "Any contract". */
export function describeRuleCriteria(rule: Pick<ApprovalRule, 'entityId' | 'typeId' | 'departmentId' | 'minValue' | 'maxValue'>, lookups: Lookups): string {
  const name = (list: { id: string; name: string }[], id: string | null) => (id ? (list.find((x) => x.id === id)?.name ?? 'Unknown') : null);
  const money = (n: number) => formatMoneyCompact(n, 'DKK');
  const band =
    rule.minValue !== null && rule.maxValue !== null
      ? `${money(rule.minValue)} to ${money(rule.maxValue)}`
      : rule.minValue !== null
        ? `${money(rule.minValue)} or more`
        : rule.maxValue !== null
          ? `Under ${money(rule.maxValue)}`
          : null;
  const parts = [name(lookups.entities, rule.entityId), name(lookups.contractTypes, rule.typeId), name(lookups.departments, rule.departmentId), band].filter((x): x is string => !!x);
  return parts.length > 0 ? parts.join(' · ') : 'Any contract';
}

export function describeApprover(a: { userId: string | null; role: ApproverRole | null }, lookups: Lookups): string {
  if (a.userId) return lookups.users.find((u) => u.id === a.userId)?.name ?? 'Unknown user';
  return roleLabel(a.role);
}

// ----- Reminders -----

export interface ReminderDraft {
  noticeDays: number[];
  escalationDays: string;
  obligationDays: string;
}
export interface ReminderErrors {
  noticeDays?: string;
  escalationDays?: string;
  obligationDays?: string;
}

/** Validates a "days before" entry to add to the list. Returns the number or an error. */
export function parseNoticeDay(text: string, existing: number[]): { value: number } | { error: string } {
  const n = parseWhole(text);
  if (n === null) return { error: 'Enter a number of days' };
  if (Number.isNaN(n) || n < 1) return { error: 'Enter a whole number of days, 1 or more' };
  if (n > 730) return { error: 'Use 730 days or fewer' };
  if (existing.includes(n)) return { error: `A reminder ${n} days before is already in the list` };
  return { value: n };
}

export function validateReminders(d: ReminderDraft): ReminderErrors {
  const errors: ReminderErrors = {};
  if (d.noticeDays.length === 0) errors.noticeDays = 'Add at least one reminder';
  const esc = parseWhole(d.escalationDays);
  if (esc === null || Number.isNaN(esc)) errors.escalationDays = 'Enter a whole number of days, 0 or more';
  else if (d.noticeDays.length > 0 && esc >= Math.max(...d.noticeDays)) errors.escalationDays = 'The escalation must come after the first reminder';
  const obl = parseWhole(d.obligationDays);
  if (obl === null || Number.isNaN(obl)) errors.obligationDays = 'Enter a whole number of days, 0 or more';
  return errors;
}

export function remindersToInput(d: ReminderDraft): ReminderSettings {
  return { noticeDays: [...d.noticeDays].sort((a, b) => b - a), escalationDays: parseWhole(d.escalationDays) ?? 0, obligationDays: parseWhole(d.obligationDays) ?? 0 };
}

// ----- Retention -----

export interface RetentionDraft {
  years: string;
  action: RetentionAction;
  reviewerDepartmentId: string;
}
export interface RetentionErrors {
  years?: string;
  reviewerDepartmentId?: string;
}
export const RETENTION_ACTIONS: { value: RetentionAction; label: string }[] = [
  { value: 'ReviewThenDelete', label: 'Review, then delete' },
  { value: 'DeleteAutomatically', label: 'Delete automatically' },
  { value: 'Keep', label: 'Keep' },
];

export function validateRetention(d: RetentionDraft): RetentionErrors {
  const errors: RetentionErrors = {};
  const years = parseWhole(d.years);
  if (d.action === 'Keep') {
    if (Number.isNaN(years)) errors.years = 'Enter whole years, or leave it empty';
  } else if (years === null || Number.isNaN(years)) errors.years = 'Enter whole years';
  else if (years > 100) errors.years = 'Use 100 years or fewer';
  if (d.action === 'ReviewThenDelete' && !d.reviewerDepartmentId) errors.reviewerDepartmentId = 'Choose who reviews';
  return errors;
}

export function retentionToInput(typeId: string, d: RetentionDraft): RetentionRuleInput {
  return { typeId, years: parseWhole(d.years), action: d.action, reviewerDepartmentId: d.reviewerDepartmentId || null };
}

// ----- Holiday cover -----

export interface CoverErrors {
  userId?: string;
  deputyId?: string;
  from?: string;
  to?: string;
}

export function validateCover(d: HolidayCoverInput, others: HolidayCoverInput[] = []): CoverErrors {
  const errors: CoverErrors = {};
  if (!d.userId) errors.userId = 'Choose who is away';
  if (!d.deputyId) errors.deputyId = 'Choose the deputy';
  else if (d.deputyId === d.userId) errors.deputyId = 'The deputy must be someone else';
  if (!isIsoDate(d.from)) errors.from = 'Choose the first day';
  if (!isIsoDate(d.to)) errors.to = 'Choose the last day';
  else if (isIsoDate(d.from) && d.to < d.from) errors.to = 'The last day must be on or after the first day';
  if (!errors.userId && !errors.from && !errors.to && others.some((o) => o.userId === d.userId && o.from <= d.to && d.from <= o.to)) errors.from = 'This person already has cover in that period';
  return errors;
}

/** Name for a department, contract type or template: required, and unique in its list. */
export function validateName(name: string, existing: { id: string; name: string }[], selfId?: string): string | undefined {
  const n = name.trim();
  if (!n) return 'Enter a name';
  if (n.length > 80) return 'Use 80 characters or fewer';
  if (existing.some((x) => x.id !== selfId && x.name.trim().toLowerCase() === n.toLowerCase())) return 'That name is already in use';
  return undefined;
}

/** Lookup rows as select options. */
export const refs = (list: { id: string; name: string }[]) => list.map((x) => ({ value: x.id, label: x.name }));
