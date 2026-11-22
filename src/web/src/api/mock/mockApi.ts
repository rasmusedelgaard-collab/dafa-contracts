// Complete in-browser implementation of the Api interface, with state in memory.
// It enforces the access model for the selected demo user (see access.ts) so screens built
// on it behave like they will against the real API.
import { ApiError, type Api } from '../api';
import type {
  ApprovalInstance,
  ApprovalPreview,
  ApprovalRule,
  ApprovalRuleInput,
  ApproverRole,
  AuditEvent,
  ContractDetail,
  ContractInput,
  ContractListItem,
  ContractReminder,
  Deviation,
  DocumentKind,
  ExtractResult,
  HolidayCover,
  HomeTask,
  Id,
  JobRunResult,
  LifecycleStep,
  Me,
  MyApproval,
  NamedRef,
  Obligation,
  ObligationInput,
  OutboxMessage,
  RetentionRule,
  Suggestion,
  Template,
  Urgency,
} from '../types';
import {
  addDays,
  addMonths,
  daysBetween,
  formatDate,
  monthKey,
  nextReminderDate,
  noticeDeadline,
  noticeTone,
  reminderSchedule,
  todayIso,
} from '../../lib/dates';
import { toDkk } from '../../lib/format';
import { validateInput } from '../../lib/validation';
import { canCreate, canDecideRenewal, canEdit, canSee, canSeeOverview, isAdmin, isOnContract } from './access';
import { newId, type ApprovalRec, type ContractRec, type Db, type DocumentRec, type ObligationRec, type StepRec, type UserRec } from './db';
import { makePdf, makeRtf } from './files';
import { filterRows, queryRows, sortRows, toCsv, type RegisterRow } from './query';
import { createSeed } from './seed';

export interface MockOptions {
  /** Fixed "today" (ISO date) for deterministic tests. Defaults to the real date. */
  today?: string;
  /** Simulated network latency. */
  latencyMs?: number;
  /** Initially signed-in demo user. Defaults to the remembered user, then the first seeded user. */
  userId?: Id;
  /** Remember the selected demo user in localStorage. */
  rememberUser?: boolean;
}

export type MockApi = Api & {
  /** The in-memory database, exposed for tests. */
  readonly db: Db;
};

const USER_KEY = 'dafa.mock.userId';
const ROLE_LABEL: Record<ApproverRole, string> = {
  DepartmentHead: 'Department head',
  EntityManager: 'Entity management',
  GroupCFO: 'Group CFO',
  GroupCEO: 'Group CEO',
  Legal: 'Legal',
};
const DECISION_TEXT = { Renew: 'Renew', Renegotiate: 'Renegotiate', GiveNotice: 'Give notice', LetExpire: 'Let it expire' } as const;

function problem(status: number, title: string, detail?: string, errors?: Record<string, string[]>): ApiError {
  return new ApiError({ type: 'about:blank', title, status, detail, errors });
}
const notFound = (what = 'Contract') => problem(404, 'Not found', `${what} was not found, or you do not have access to it.`);
const forbidden = (detail = 'You do not have permission to do this.') => problem(403, 'Forbidden', detail);
const conflict = (detail: string) => problem(409, 'Conflict', detail);
const invalid = (detail: string, errors?: Record<string, string[]>) => problem(400, 'Validation failed', detail, errors);

function remembered(): Id | null {
  try {
    return globalThis.localStorage?.getItem(USER_KEY) ?? null;
  } catch {
    return null;
  }
}
function remember(id: Id | null): void {
  try {
    if (id) globalThis.localStorage?.setItem(USER_KEY, id);
    else globalThis.localStorage?.removeItem(USER_KEY);
  } catch {
    /* storage unavailable */
  }
}

export function createMockApi(options: MockOptions = {}): MockApi {
  const today = () => options.today ?? todayIso();
  const now = () => (options.today ? `${options.today}T12:00:00Z` : new Date().toISOString());
  const latency = options.latencyMs ?? 0;
  const db = createSeed(today());

  let currentUserId: Id | null =
    options.userId ?? (options.rememberUser ? remembered() : null) ?? db.users[0]?.id ?? null;
  if (currentUserId && !db.users.some((u) => u.id === currentUserId)) currentUserId = db.users[0]?.id ?? null;

  // ----- lookups -----
  const byId = <T extends { id: Id }>(list: T[], id: Id | null | undefined): T | undefined =>
    id ? list.find((x) => x.id === id) : undefined;
  const nameOf = (list: NamedRef[], id: Id | null | undefined) => byId(list, id)?.name ?? '–';
  const userOf = (id: Id | null | undefined) => byId(db.users, id);
  const userName = (id: Id | null | undefined) => userOf(id)?.name ?? 'System';

  function me(): UserRec {
    const u = userOf(currentUserId);
    if (!u) throw problem(401, 'Not signed in', 'Choose a demo user to continue.');
    return u;
  }
  function admin(): UserRec {
    const u = me();
    if (!isAdmin(u)) throw forbidden('Only administrators can open Settings.');
    return u;
  }

  function audit(action: string, contractId: Id | null, text: string, userId: Id | null = currentUserId, at = now()): void {
    db.audit.push({ id: newId(), at, userId, action, contractId, text });
  }
  function notify(toUserId: Id, subject: string, body: string, contractId: Id | null, channel: 'Mail' | 'Teams' = 'Mail'): void {
    db.outbox.push({ id: newId(), createdAt: now(), toUserId, subject, body, channel, contractId });
  }

  // ----- access -----
  const sees = (u: UserRec, c: ContractRec) => canSee(u, c, db.approvals);
  const visible = (u: UserRec) => db.contracts.filter((c) => sees(u, c));
  function contractFor(id: Id, u: UserRec): ContractRec {
    const c = byId(db.contracts, id);
    if (!c || !sees(u, c)) throw notFound();
    return c;
  }
  function editable(id: Id, u: UserRec): ContractRec {
    const c = contractFor(id, u);
    if (!canEdit(u, c)) throw forbidden('Only the people on the contract or an administrator can change it.');
    return c;
  }

  // ----- calculations -----
  /** The notice deadline stops mattering once notice is given or the contract has ended. */
  const deadlineOf = (c: ContractRec) =>
    c.status === 'NoticeGiven' || c.status === 'Expired' || c.status === 'Terminated' ? null : noticeDeadline(c.endDate, c.noticePeriodDays);
  const daysOf = (c: ContractRec) => {
    const d = deadlineOf(c);
    return d ? daysBetween(today(), d) : null;
  };
  const valueDkk = (c: Pick<ContractRec, 'totalValue' | 'annualValue' | 'currency'>) =>
    toDkk(c.totalValue ?? c.annualValue ?? 0, c.currency);
  const annualDkk = (c: ContractRec) => toDkk(c.annualValue ?? 0, c.currency);
  const isLive = (c: ContractRec) => c.status === 'Active' || c.status === 'NoticeGiven';

  function missingData(c: ContractRec): string[] {
    const missing: string[] = [];
    if (c.noticePeriodDays === null) missing.push('Notice period');
    if (c.annualValue === null && c.totalValue === null) missing.push('Contract value');
    if (isLive(c) && !c.signedDate) missing.push('Signed date');
    return missing;
  }

  function recipientOf(c: ContractRec): Id {
    if (c.reminderRecipient === 'Delegate' && c.delegateId) return c.delegateId;
    if (c.reminderRecipient === 'Responsible' && c.responsibleId) return c.responsibleId;
    return c.ownerId;
  }

  // ----- projections -----
  function toListItem(c: ContractRec): ContractListItem {
    const owner = userOf(c.ownerId);
    return {
      id: c.id,
      contractNo: c.contractNo,
      title: c.title,
      counterparty: c.counterpartyName,
      typeName: nameOf(db.contractTypes, c.typeId),
      entityName: nameOf(db.entities, c.entityId),
      departmentName: nameOf(db.departments, c.departmentId),
      ownerName: owner?.name ?? '–',
      ownerInitials: owner?.initials ?? '?',
      noticeDeadline: deadlineOf(c),
      daysToNotice: daysOf(c),
      annualValue: c.annualValue,
      currency: c.currency,
      status: c.status,
      missingData: missingData(c),
    };
  }

  const toRow = (c: ContractRec): RegisterRow => ({
    item: toListItem(c),
    entityId: c.entityId,
    departmentId: c.departmentId,
    typeId: c.typeId,
    peopleIds: [c.ownerId, c.delegateId, c.responsibleId].filter((x): x is Id => !!x),
  });

  function toApproval(a: ApprovalRec): ApprovalInstance {
    return {
      id: a.id,
      contractId: a.contractId,
      contractTitle: byId(db.contracts, a.contractId)?.title ?? '',
      status: a.status,
      currentStage: a.currentStage,
      stages: a.stages.map((s) => ({
        order: s.order,
        steps: s.steps.map((st) => {
          const u = userOf(st.approverId);
          return {
            id: st.id,
            approverId: st.approverId,
            approverName: u?.name ?? '–',
            initials: u?.initials ?? '?',
            roleLabel: st.roleLabel,
            status: st.status,
            decidedAt: st.decidedAt,
            comment: st.comment,
          };
        }),
      })),
    };
  }

  const latestApproval = (contractId: Id): ApprovalRec | undefined =>
    db.approvals.filter((a) => a.contractId === contractId).sort((a, b) => b.startedAt.localeCompare(a.startedAt))[0];

  function toObligation(o: ObligationRec): Obligation {
    return {
      id: o.id,
      description: o.description,
      dueDate: o.dueDate,
      frequencyMonths: o.frequencyMonths,
      responsibleId: o.responsibleId,
      responsibleName: userName(o.responsibleId),
      status: o.status,
    };
  }

  function reminderPlan(c: ContractRec) {
    return reminderSchedule(deadlineOf(c), db.reminderSettings.noticeDays, db.reminderSettings.escalationDays);
  }

  function remindersOf(c: ContractRec): ContractReminder[] {
    const sent = db.reminders.filter((r) => r.contractId === c.id && !r.kind.startsWith('Obligation'));
    const stopped = c.renewalDecision !== null || c.status !== 'Active';
    const plan = stopped ? [] : reminderPlan(c);
    const rows = new Map<string, ContractReminder>();
    for (const r of sent) rows.set(`${r.kind}|${r.dueDate}`, { kind: r.kind, dueDate: r.dueDate, sentAt: r.sentAt });
    for (const p of plan) {
      const key = `${p.kind}|${p.dueDate}`;
      if (!rows.has(key)) rows.set(key, { kind: p.kind, dueDate: p.dueDate, sentAt: null });
    }
    return [...rows.values()].sort((a, b) => a.dueDate.localeCompare(b.dueDate));
  }

  function lifecycleOf(c: ContractRec): LifecycleStep[] {
    const rank = { Draft: 0, InApproval: 1, Approved: 2, Active: 3, NoticeGiven: 3, Expired: 4, Terminated: 4 }[c.status];
    const state = (stepRank: number): LifecycleStep['state'] => (rank > stepRank ? 'done' : rank === stepRank ? 'current' : 'upcoming');
    const steps: LifecycleStep[] = [
      { name: 'Drafted', date: c.createdAt, state: rank === 0 ? 'current' : 'done' },
      { name: rank === 1 ? 'In approval' : 'Approved', date: c.approvedAt, state: rank >= 2 ? 'done' : state(1) },
      { name: 'Signed', date: c.signedDate, state: rank >= 3 ? 'done' : rank === 2 ? 'current' : 'upcoming' },
      { name: 'Active', date: c.startDate, state: rank >= 3 ? 'done' : 'upcoming' },
    ];
    const deadline = deadlineOf(c);
    const days = daysOf(c);
    if (rank === 4) {
      steps.push({ name: c.status === 'Terminated' ? 'Terminated' : 'Expired', date: c.endDate, state: 'done' });
      return steps;
    }
    if (!c.endDate) {
      steps.push({ name: 'Open-ended', date: null, state: 'upcoming' });
      return steps;
    }
    if (c.status === 'NoticeGiven') {
      steps.push({ name: 'Notice given', date: c.renewalDecidedAt?.slice(0, 10) ?? null, state: 'done' });
      steps.push({ name: 'Ends', date: c.endDate, state: 'current' });
      return steps;
    }
    if (deadline) {
      const decided = c.renewalDecision !== null;
      const alert = rank === 3 && !decided && days !== null && days <= 90;
      steps.push({ name: 'Notice deadline', date: deadline, state: alert ? 'alert' : decided ? 'done' : 'upcoming' });
    }
    steps.push({ name: c.autoRenew ? 'Renewal' : 'End of term', date: c.autoRenew ? addDays(c.endDate, 1) : c.endDate, state: 'upcoming' });
    return steps;
  }

  function accessOf(c: ContractRec): string[] {
    const access = [
      `${nameOf(db.departments, c.departmentId)} (all entities)`,
      `${nameOf(db.entities, c.entityId)} management`,
      'Group management',
      'The people on this contract',
      'Administrators',
    ];
    const a = latestApproval(c.id);
    if (a?.status === 'Pending') access.push('Current approvers, while their step is open');
    return access;
  }

  function toDetail(c: ContractRec, u: UserRec): ContractDetail {
    const people: ContractDetail['people'] = [];
    const addPerson = (role: 'Owner' | 'Delegate' | 'Responsible', id: Id | null) => {
      const p = userOf(id);
      if (p) people.push({ role, userId: p.id, name: p.name, initials: p.initials, getsReminders: recipientOf(c) === p.id && c.reminderRecipient === role });
    };
    addPerson('Owner', c.ownerId);
    addPerson('Delegate', c.delegateId);
    addPerson('Responsible', c.responsibleId);
    if (!people.some((p) => p.getsReminders) && people[0]) people[0].getsReminders = true;

    const links: ContractDetail['links'] = [];
    for (const l of db.links) {
      const otherId = l.fromId === c.id ? l.toId : l.toId === c.id ? l.fromId : null;
      const other = byId(db.contracts, otherId);
      if (!other || !sees(u, other)) continue;
      links.push({ id: l.id, contractId: other.id, title: other.title, kind: l.kind, direction: l.fromId === c.id ? 'out' : 'in' });
    }

    const approval = latestApproval(c.id);
    const plan = c.renewalDecision === null && c.status === 'Active' ? reminderPlan(c) : [];
    const {
      id: _id, contractNo: _no, createdAt: _ca, createdBy: _cb, approvedAt: _aa, deviations, renewalDecidedBy, ...fields
    } = c;
    return {
      ...toListItem(c),
      ...fields,
      renewalDecidedBy: renewalDecidedBy ? userName(renewalDecidedBy) : null,
      nextReminderDate: nextReminderDate(plan, today()),
      lifecycle: lifecycleOf(c),
      people,
      documents: db.documents
        .filter((d) => d.contractId === c.id)
        .sort((a, b) => b.uploadedAt.localeCompare(a.uploadedAt))
        .map((d) => ({ id: d.id, name: d.name, kind: d.kind, version: d.version, size: d.size, uploadedAt: d.uploadedAt, uploadedBy: userName(d.uploadedBy) })),
      obligations: db.obligations.filter((o) => o.contractId === c.id).map(toObligation),
      links,
      approval: approval ? toApproval(approval) : null,
      reminders: remindersOf(c),
      activity: db.audit
        .filter((a) => a.contractId === c.id && a.action !== 'DocumentOpened')
        .sort((a, b) => b.at.localeCompare(a.at))
        .map((a) => ({ at: a.at, who: userName(a.userId), text: a.text })),
      access: accessOf(c),
      deviations: deviations.map((d) => ({ ...d })),
      canEdit: canEdit(u, c),
      canDecideRenewal: canDecideRenewal(u, c),
      storeUrl: null,
    };
  }

  // ----- approvals engine -----
  function matchRule(entityId: Id | undefined, typeId: Id | undefined, departmentId: Id | undefined, value: number): ApprovalRule | undefined {
    const specificity = (r: ApprovalRule) =>
      [r.entityId, r.typeId, r.departmentId, r.minValue, r.maxValue].filter((x) => x !== null).length;
    return db.approvalRules
      .filter(
        (r) =>
          (r.entityId === null || r.entityId === entityId) &&
          (r.typeId === null || r.typeId === typeId) &&
          (r.departmentId === null || r.departmentId === departmentId) &&
          (r.minValue === null || value >= r.minValue) &&
          (r.maxValue === null || value < r.maxValue),
      )
      .sort((a, b) => specificity(b) - specificity(a) || b.priority - a.priority)[0];
  }

  function resolveApprover(a: ApprovalRule['stages'][number]['approvers'][number], entityId: Id | undefined, departmentId: Id | undefined): { userId: Id | null; label: string } {
    if (a.userId) return { userId: a.userId, label: userOf(a.userId)?.title.split(' · ')[0] ?? 'Approver' };
    const role = a.role ?? 'GroupCFO';
    let userId: Id | null;
    if (role === 'DepartmentHead') userId = (departmentId && db.departmentHeads[departmentId]) || db.roleHolders.GroupCFO;
    else if (role === 'EntityManager') userId = db.users.find((u) => entityId && u.managedEntityIds.includes(entityId))?.id ?? db.roleHolders.GroupCEO;
    else userId = db.roleHolders[role];
    return { userId, label: ROLE_LABEL[role] };
  }

  function resolveStages(rule: ApprovalRule, entityId: Id | undefined, departmentId: Id | undefined) {
    return [...rule.stages]
      .sort((a, b) => a.order - b.order)
      .map((s, i) => {
        const seen = new Set<Id>();
        const approvers = s.approvers
          .map((a) => resolveApprover(a, entityId, departmentId))
          .filter((a): a is { userId: Id; label: string } => {
            if (!a.userId || seen.has(a.userId)) return false;
            seen.add(a.userId);
            return true;
          });
        return { order: i + 1, approvers };
      })
      .filter((s) => s.approvers.length > 0);
  }

  function activeDeputy(userId: Id): Id | null {
    const t = today();
    return db.holidayCover.find((h) => h.userId === userId && h.from <= t && t <= h.to)?.deputyId ?? null;
  }

  function activateStage(a: ApprovalRec, c: ContractRec): void {
    const stage = a.stages.find((s) => s.order === a.currentStage);
    if (!stage) return;
    for (const st of stage.steps) {
      if (st.status !== 'Waiting') continue;
      const deputy = activeDeputy(st.approverId);
      if (deputy) {
        st.roleLabel = `${st.roleLabel} (cover for ${userName(st.approverId)})`;
        st.approverId = deputy;
      }
      st.status = 'Pending';
      st.pendingSince = today();
      notify(st.approverId, `Approval needed: ${c.title}`, `${userName(c.ownerId)} asks for your approval as ${st.roleLabel}.`, c.id);
    }
  }

  function startApproval(c: ContractRec): ApprovalRec {
    const rule = matchRule(c.entityId, c.typeId, c.departmentId, valueDkk(c));
    if (!rule) throw conflict('No approval rule matches this contract. Ask an administrator to add one in Settings.');
    const stages = resolveStages(rule, c.entityId, c.departmentId);
    if (stages.length === 0) throw conflict('The matching approval rule has no approvers.');
    const a: ApprovalRec = {
      id: newId(),
      contractId: c.id,
      status: 'Pending',
      currentStage: 1,
      startedAt: now(),
      stages: stages.map((s) => ({
        order: s.order,
        steps: s.approvers.map((ap): StepRec => ({ id: newId(), approverId: ap.userId, roleLabel: ap.label, status: 'Waiting', decidedAt: null, comment: null, pendingSince: null })),
      })),
    };
    db.approvals.push(a);
    activateStage(a, c);
    return a;
  }

  function approvalLog(a: ApprovalRec, c: ContractRec): string[] {
    const lines = ['Approval log', '', `Contract: ${c.title} (${c.contractNo})`, `Counterparty: ${c.counterpartyName}`, `Legal entity: ${nameOf(db.entities, c.entityId)}`, `Submitted: ${formatDate(a.startedAt.slice(0, 10))}`, ''];
    for (const s of a.stages) {
      lines.push(`Stage ${s.order}`);
      for (const st of s.steps) {
        lines.push(`  ${st.roleLabel}: ${userName(st.approverId)} - ${st.status}${st.decidedAt ? ` on ${formatDate(st.decidedAt.slice(0, 10))}` : ''}${st.comment ? ` - "${st.comment}"` : ''}`);
      }
    }
    lines.push('', 'Generated by DAFA Contracts.');
    return lines;
  }

  function addDocument(c: ContractRec, name: string, kind: DocumentKind, blob: Blob, uploadedBy: Id): DocumentRec {
    const version = Math.max(0, ...db.documents.filter((d) => d.contractId === c.id && d.name === name).map((d) => d.version)) + 1;
    const doc: DocumentRec = { id: newId(), contractId: c.id, name, kind, storeKey: `mock/${c.id}/${name}`, version, size: blob.size, uploadedBy, uploadedAt: now(), blob };
    db.documents.push(doc);
    return doc;
  }

  function findStep(stepId: Id): { a: ApprovalRec; stage: ApprovalRec['stages'][number]; step: StepRec } {
    for (const a of db.approvals) {
      for (const stage of a.stages) {
        const step = stage.steps.find((s) => s.id === stepId);
        if (step) return { a, stage, step };
      }
    }
    throw notFound('Approval step');
  }

  function myPendingSteps(u: UserRec) {
    const out: { a: ApprovalRec; step: StepRec; stage: ApprovalRec['stages'][number]; c: ContractRec }[] = [];
    for (const a of db.approvals) {
      if (a.status !== 'Pending') continue;
      const stage = a.stages.find((s) => s.order === a.currentStage);
      const c = byId(db.contracts, a.contractId);
      if (!stage || !c) continue;
      for (const step of stage.steps) if (step.status === 'Pending' && step.approverId === u.id) out.push({ a, step, stage, c });
    }
    return out;
  }

  // ----- background jobs -----
  function runJobs(historical: boolean): JobRunResult {
    const t = today();
    const result: JobRunResult = { ranAt: now(), remindersCreated: 0, escalations: 0, obligationReminders: 0, contractsExpired: 0 };
    const has = (contractId: Id, kind: string, dueDate: string) =>
      db.reminders.some((r) => r.contractId === contractId && r.kind === kind && r.dueDate === dueDate);

    if (!historical) {
      for (const c of db.contracts) {
        if (!c.endDate || c.endDate >= t) continue;
        if (c.status === 'NoticeGiven') {
          c.status = 'Terminated';
          audit('ContractEnded', c.id, 'Contract ended after notice was given', null);
          result.contractsExpired += 1;
        } else if (c.status === 'Active') {
          if (c.autoRenew && c.renewalMonths && c.renewalDecision !== 'LetExpire') {
            c.endDate = addMonths(c.endDate, c.renewalMonths);
            c.renewalDecision = null;
            c.renewalDecidedAt = null;
            c.renewalDecidedBy = null;
            c.renewalComment = null;
            audit('ContractRenewed', c.id, `Renewed automatically for ${c.renewalMonths} months`, null);
          } else {
            c.status = 'Expired';
            audit('ContractEnded', c.id, 'Contract expired', null);
            result.contractsExpired += 1;
          }
        }
      }
    }

    for (const c of db.contracts) {
      if (c.status !== 'Active' || c.renewalDecision !== null) continue;
      for (const p of reminderPlan(c)) {
        if (p.dueDate > t || has(c.id, p.kind, p.dueDate)) continue;
        const escalation = p.kind === 'Escalation';
        const recipientId = escalation ? c.ownerId : recipientOf(c);
        const sentAt = historical ? `${p.dueDate}T07:00:00Z` : now();
        db.reminders.push({ id: newId(), contractId: c.id, kind: p.kind, dueDate: p.dueDate, sentAt, recipientId });
        const days = p.kind.replace('Notice', '');
        const text = escalation
          ? `Escalated to the owner: no renewal decision ${db.reminderSettings.escalationDays} days before the notice deadline`
          : `Reminder sent to ${userName(recipientId)} (${days} days before the notice deadline)`;
        audit('ReminderSent', c.id, text, null, sentAt);
        if (!historical) {
          notify(recipientId, `${escalation ? 'Escalation' : 'Reminder'}: ${c.title}`, `The notice deadline is ${formatDate(deadlineOf(c))}. Record a renewal decision in DAFA Contracts.`, c.id);
        }
        if (escalation) result.escalations += 1;
        else result.remindersCreated += 1;
      }
    }

    for (const o of db.obligations) {
      if (o.status !== 'Open' || !o.dueDate) continue;
      const kind = `Obligation:${o.id}`;
      if (addDays(o.dueDate, -db.reminderSettings.obligationDays) > t || has(o.contractId, kind, o.dueDate)) continue;
      const sentAt = historical ? `${addDays(o.dueDate, -db.reminderSettings.obligationDays)}T07:00:00Z` : now();
      db.reminders.push({ id: newId(), contractId: o.contractId, kind, dueDate: o.dueDate, sentAt, recipientId: o.responsibleId });
      if (!historical) notify(o.responsibleId, `Obligation due ${formatDate(o.dueDate)}`, o.description, o.contractId);
      result.obligationReminders += 1;
    }
    return result;
  }
  runJobs(true);

  // ----- home -----
  function buildTasks(u: UserRec): HomeTask[] {
    const t = today();
    const tasks: { task: HomeTask; rank: number; days: number }[] = [];
    const urgencyRank: Record<Urgency, number> = { high: 0, medium: 1, low: 2, info: 3 };
    const push = (task: HomeTask, days: number) => tasks.push({ task, rank: urgencyRank[task.urgency], days });

    for (const { a, step, stage, c } of myPendingSteps(u)) {
      const others = stage.steps.filter((s) => s.id !== step.id && s.status === 'Pending').map((s) => s.roleLabel);
      const waiting = step.pendingSince ? daysBetween(step.pendingSince, t) : 0;
      push(
        {
          kind: 'Approval',
          title: c.title,
          meta: [nameOf(db.entities, c.entityId), nameOf(db.departments, c.departmentId), others.length ? `${others.join(' and ')} approving in parallel` : `Step ${a.currentStage} of ${a.stages.length}`].join(' · '),
          pill: 'Your approval',
          urgency: waiting >= 5 ? 'high' : 'medium',
          contractId: c.id,
          approvalStepId: step.id,
          obligationId: null,
        },
        -waiting,
      );
    }

    for (const c of db.contracts) {
      if (!isOnContract(u, c) || c.status !== 'Active' || c.renewalDecision !== null) continue;
      const days = daysOf(c);
      if (days === null || days > 120 || days < -60) continue;
      const tone = noticeTone(days);
      push(
        {
          kind: 'Renewal',
          title: c.title,
          meta: [nameOf(db.entities, c.entityId), nameOf(db.departments, c.departmentId), c.autoRenew && c.renewalMonths ? `auto-renews ${c.renewalMonths} months` : 'fixed term'].join(' · '),
          pill: days < 0 ? `Notice overdue by ${-days} days` : days === 0 ? 'Notice today' : `Notice in ${days} days`,
          urgency: tone === 'red' ? 'high' : tone === 'amber' ? 'medium' : 'low',
          contractId: c.id,
          approvalStepId: null,
          obligationId: null,
        },
        days,
      );
    }

    for (const o of db.obligations) {
      if (o.status !== 'Open' || o.responsibleId !== u.id || !o.dueDate) continue;
      const days = daysBetween(t, o.dueDate);
      const c = byId(db.contracts, o.contractId);
      if (days > 60 || !c || !sees(u, c)) continue;
      push(
        {
          kind: 'Obligation',
          title: o.description,
          meta: `Obligation · ${c.title} · due ${formatDate(o.dueDate)}`,
          pill: days < 0 ? `Overdue by ${-days} days` : days === 0 ? 'Due today' : `Due in ${days} days`,
          urgency: days <= 14 ? 'high' : 'medium',
          contractId: c.id,
          approvalStepId: null,
          obligationId: o.id,
        },
        days,
      );
    }

    const incomplete = db.contracts.filter((c) => sees(u, c) && canEdit(u, c) && missingData(c).length > 0);
    if (incomplete.length === 1 && incomplete[0]) {
      const c = incomplete[0];
      push({ kind: 'MissingData', title: `${c.title} is missing data`, meta: `${nameOf(db.entities, c.entityId)} · ${missingData(c).join(', ').toLowerCase()}`, pill: 'Missing data', urgency: 'info', contractId: c.id, approvalStepId: null, obligationId: null }, 0);
    } else if (incomplete.length > 1) {
      const entities = [...new Set(incomplete.map((c) => nameOf(db.entities, c.entityId)))];
      const allNotice = incomplete.every((c) => c.noticePeriodDays === null);
      push({ kind: 'MissingData', title: `${incomplete.length} contracts missing ${allNotice ? 'a notice period' : 'data'}`, meta: entities.slice(0, 2).join(' · ') + (entities.length > 2 ? ` and ${entities.length - 2} more` : ''), pill: 'Missing data', urgency: 'info', contractId: null, approvalStepId: null, obligationId: null }, 0);
    }

    // Urgent items first, approvals right after them, then by kind and by how soon they are due.
    const kindOrder = { Renewal: 0, Approval: 1, Obligation: 2, MissingData: 3 } as const;
    const rankOf = (x: (typeof tasks)[number]) => (x.task.kind === 'Approval' ? 0.5 : x.rank);
    return tasks
      .sort((a, b) => rankOf(a) - rankOf(b) || kindOrder[a.task.kind] - kindOrder[b.task.kind] || a.days - b.days)
      .map((x) => x.task);
  }

  // ----- intake -----
  function guessType(name: string): number {
    const n = name.toLowerCase();
    const rules: [RegExp, number][] = [
      [/nda|non-?disclosure|confidential/, 6],
      [/lease|rent|property|premises/, 4],
      [/freight|logistic|transport|warehous|courier|shipping/, 2],
      [/software|saas|subscription|licen[cs]e|hosting|cloud|it[ -]/, 3],
      [/insurance|bank|loan|credit|facility/, 5],
      [/development|research|r&d|partnership/, 7],
      [/sharehold|merger|acquisition|spa\b/, 8],
      [/raw material|supply|compound|material|component/, 0],
    ];
    return rules.find(([re]) => re.test(n))?.[1] ?? 1;
  }

  function cannedDeviations(typeIndex: number): Deviation[] {
    const base: Deviation[] = [
      { severity: 'Medium', text: 'Payment terms 30 days net', standard: '60 days net' },
      { severity: 'Low', text: 'Notice must be sent by registered mail', standard: 'Written notice, including email' },
    ];
    if (typeIndex === 3) return [{ severity: 'High', text: 'Liability cap at 6 months of fees', standard: '12 months' }, { severity: 'Medium', text: 'Auto-renewal for 36 months', standard: '12 months' }, ...base.slice(1)];
    if (typeIndex === 6) return [{ severity: 'Low', text: 'Confidentiality survives for 3 years', standard: '5 years' }];
    if (typeIndex === 4) return [{ severity: 'Medium', text: 'Rent indexation without a cap', standard: 'CPI, capped at 4% a year' }, ...base.slice(1)];
    return base;
  }

  function buildExtract(file: File, u: UserRec): ExtractResult {
    const ext = /\.([a-z0-9]+)$/i.exec(file.name)?.[1]?.toLowerCase() ?? '';
    if (!['pdf', 'docx', 'doc'].includes(ext)) throw invalid('Upload a PDF or Word file (.pdf, .docx).', { file: ['Unsupported file type'] });
    if (file.size > 25 * 1024 * 1024) throw invalid('The file is larger than 25 MB.', { file: ['File too large'] });

    const base = file.name.replace(/\.[a-z0-9]+$/i, '').replace(/[_]+/g, ' ');
    const title = base.replace(/\s*[-–(]?\s*\b(draft|signed|final|v\d+|copy)\b\)?/gi, '').replace(/\s{2,}/g, ' ').trim() || base;
    const typeIndex = guessType(base);
    const lower = base.toLowerCase();
    const named = db.suppliers.find((s) => lower.includes((s.name.split(' ')[0] ?? s.name).toLowerCase()));
    const supplier = named ?? db.suppliers[(file.size + base.length) % db.suppliers.length];
    const entity = byId(db.entities, u.managedEntityIds[0]) ?? db.entities[1] ?? db.entities[0];
    const t = today();
    const start = addMonths(`${t.slice(0, 8)}01`, 1);
    const termMonths = typeIndex === 3 ? 36 : typeIndex === 4 ? 60 : 24;
    const end = addDays(addMonths(start, termMonths), -1);
    const type = db.contractTypes[typeIndex];
    const notice = typeIndex === 6 ? 0 : typeIndex === 4 ? 180 : 90;
    const renews = typeIndex !== 6;

    const s = (field: string, value: string, confidence: number): Suggestion => ({ field, value, confidence });
    const suggestions: Suggestion[] = [
      s('title', title, 0.74),
      ...(supplier ? [s('counterpartyName', supplier.name, named ? 0.93 : 0.58)] : []),
      ...(type ? [s('typeId', type.id, 0.81)] : []),
      ...(entity ? [s('entityId', entity.id, 0.88)] : []),
      s('startDate', start, 0.9),
      s('endDate', end, 0.86),
      s('noticePeriodDays', String(notice), 0.83),
      s('autoRenew', String(renews), 0.79),
      ...(renews ? [s('renewalMonths', '12', 0.77)] : []),
      s('paymentTerms', '30 days net', 0.71),
      s('governingLaw', 'Danish law', 0.95),
    ];
    const deviations = cannedDeviations(typeIndex);
    const summary = `${type?.name ?? 'Contract'} with ${supplier?.name ?? 'the counterparty'} from ${formatDate(start)} to ${formatDate(end)}${renews ? ', renewing for 12 months at a time' : ''}. ${notice > 0 ? `Notice period ${notice} days.` : 'No notice period.'}`;
    const uploadId = newId();
    db.uploads.push({ id: uploadId, fileName: file.name, size: file.size, blob: file, kind: 'Main', deviations, summary });
    return { uploadId, fileName: file.name, size: file.size, pages: Math.max(1, Math.round(file.size / 130_000)), suggestions, summary, deviations };
  }

  // ----- admin helpers -----
  function namedCrud(list: NamedRef[], what: string, inUse: (id: Id) => boolean) {
    const check = (name: string, selfId?: Id) => {
      const n = name?.trim();
      if (!n) throw invalid(`Enter a name for the ${what}.`, { name: ['Required'] });
      if (list.some((x) => x.id !== selfId && x.name.toLowerCase() === n.toLowerCase())) throw conflict(`A ${what} with that name already exists.`);
      return n;
    };
    return {
      list: () => list.map((x) => ({ ...x })),
      create: (name: string): NamedRef => {
        const item = { id: newId(), name: check(name) };
        list.push(item);
        audit('SettingsChanged', null, `Added ${what} "${item.name}"`);
        return { ...item };
      },
      update: (id: Id, name: string): NamedRef => {
        const item = byId(list, id);
        if (!item) throw notFound(`The ${what}`);
        item.name = check(name, id);
        audit('SettingsChanged', null, `Renamed ${what} to "${item.name}"`);
        return { ...item };
      },
      remove: (id: Id): void => {
        const i = list.findIndex((x) => x.id === id);
        if (i < 0) throw notFound(`The ${what}`);
        if (inUse(id)) throw conflict(`This ${what} is used by contracts and cannot be deleted.`);
        const [gone] = list.splice(i, 1);
        audit('SettingsChanged', null, `Deleted ${what} "${gone?.name ?? ''}"`);
      },
    };
  }

  const toRetention = (r: Db['retentionRules'][number]): RetentionRule => ({ ...r, typeName: nameOf(db.contractTypes, r.typeId) });
  const toTemplate = (t: Db['templates'][number]): Template => ({ id: t.id, name: t.name, description: t.description, typeId: t.typeId, fileName: t.fileName, placeholders: [...t.placeholders] });
  const toCover = (h: Db['holidayCover'][number]): HolidayCover => ({ ...h, userName: userName(h.userId), deputyName: userName(h.deputyId) });
  const toMe = (u: UserRec): Me => ({ id: u.id, name: u.name, email: u.email, initials: u.initials, roles: [...u.roles], departmentIds: [...u.departmentIds], managedEntityIds: [...u.managedEntityIds], isGroupManagement: u.isGroupManagement, authMode: 'Dev' });

  function validateRule(input: ApprovalRuleInput): void {
    if (!input.name?.trim()) throw invalid('Enter a name for the rule.', { name: ['Required'] });
    if (!input.stages?.length || input.stages.some((s) => !s.approvers?.length)) throw invalid('Each stage needs at least one approver.', { stages: ['Required'] });
    if (input.minValue !== null && input.maxValue !== null && input.minValue >= input.maxValue) throw invalid('The minimum value must be below the maximum value.', { maxValue: ['Must be above the minimum'] });
  }

  function validateObligation(input: ObligationInput): void {
    const errors: Record<string, string[]> = {};
    if (!input.description?.trim()) errors.description = ['Required'];
    if (!userOf(input.responsibleId)) errors.responsibleId = ['Required'];
    if (input.frequencyMonths !== null && !(input.frequencyMonths >= 1)) errors.frequencyMonths = ['Must be at least 1 month'];
    if (Object.keys(errors).length) throw invalid('Check the obligation.', errors);
  }

  function checkRefs(input: ContractInput): void {
    const errors = validateInput(input);
    const ref = (field: string, ok: boolean) => {
      if (!ok && !errors[field]) errors[field] = ['Unknown value'];
    };
    ref('typeId', !!byId(db.contractTypes, input.typeId));
    ref('entityId', !!byId(db.entities, input.entityId));
    ref('departmentId', !!byId(db.departments, input.departmentId));
    ref('ownerId', !!userOf(input.ownerId));
    if (input.delegateId) ref('delegateId', !!userOf(input.delegateId));
    if (input.responsibleId) ref('responsibleId', !!userOf(input.responsibleId));
    if (input.currency) ref('currency', db.currencies.includes(input.currency));
    if (Object.keys(errors).length) throw invalid('Some fields are missing or not valid.', errors);
  }

  function applyInput(c: ContractRec, input: ContractInput): void {
    const supplier = byId(db.suppliers, input.supplierId) ?? db.suppliers.find((s) => s.name.toLowerCase() === input.counterpartyName.trim().toLowerCase());
    Object.assign(c, {
      title: input.title.trim(),
      typeId: input.typeId,
      supplierId: supplier?.id ?? null,
      counterpartyName: input.counterpartyName.trim(),
      entityId: input.entityId,
      departmentId: input.departmentId,
      ownerId: input.ownerId,
      delegateId: input.delegateId ?? null,
      responsibleId: input.responsibleId ?? null,
      reminderRecipient: input.reminderRecipient,
      startDate: input.startDate,
      endDate: input.endDate ?? null,
      noticePeriodDays: input.noticePeriodDays,
      autoRenew: input.autoRenew,
      renewalMonths: input.autoRenew ? input.renewalMonths : null,
      annualValue: input.annualValue ?? null,
      totalValue: input.totalValue ?? null,
      currency: input.currency,
      paymentTerms: input.paymentTerms ?? null,
      priceAdjustment: input.priceAdjustment ?? null,
      priceAdjustmentDate: input.priceAdjustmentDate ?? null,
      governingLaw: input.governingLaw ?? null,
      liabilityCap: input.liabilityCap ?? null,
      exclusivity: input.exclusivity ?? null,
      changeOfControl: input.changeOfControl,
      summary: input.summary ?? null,
    } satisfies Partial<ContractRec>);
  }

  function seededDocumentPdf(d: DocumentRec, c: ContractRec): Blob {
    return makePdf([
      d.name.replace(/\.[a-z0-9]+$/i, ''),
      '',
      `Contract: ${c.title} (${c.contractNo})`,
      `Parties: ${nameOf(db.entities, c.entityId)} and ${c.counterpartyName}`,
      `Term: ${formatDate(c.startDate)} to ${c.endDate ? formatDate(c.endDate) : 'open-ended'}`,
      `Document type: ${d.kind}, version ${d.version}`,
      '',
      'This is a placeholder file from the DAFA Contracts demo data. It stands in for the real',
      'document, which will be stored in SharePoint in production.',
    ]);
  }

  // ----- the Api -----
  const run = <T>(fn: () => T | Promise<T>): Promise<T> =>
    new Promise<T>((resolve, reject) => {
      const go = () => {
        try {
          Promise.resolve(fn()).then(resolve, reject);
        } catch (e) {
          reject(e as Error);
        }
      };
      if (latency > 0) setTimeout(go, latency);
      else go();
    });

  const api: MockApi = {
    db,

    getMe: () => run(() => toMe(me())),

    getLookups: () =>
      run(() => {
        me();
        return {
          entities: db.entities.map((x) => ({ ...x })),
          departments: db.departments.map((x) => ({ ...x })),
          contractTypes: db.contractTypes.map((x) => ({ ...x })),
          users: db.users.map((u) => ({ id: u.id, name: u.name, initials: u.initials, departmentIds: [...u.departmentIds] })),
          currencies: [...db.currencies],
        };
      }),

    getSuppliers: (q) =>
      run(() => {
        me();
        const needle = q?.trim().toLowerCase() ?? '';
        return db.suppliers.filter((s) => !needle || s.name.toLowerCase().includes(needle)).slice(0, 20).map((s) => ({ ...s }));
      }),

    getHome: () =>
      run(() => {
        const u = me();
        const t = today();
        const mine = visible(u);
        const tasks = buildTasks(u);
        const renewalsThisWeek = tasks.filter((x) => x.kind === 'Renewal' && x.urgency === 'high').length;
        const approvalsWaiting = tasks.filter((x) => x.kind === 'Approval').length;
        const due = mine.filter((c) => {
          const d = daysOf(c);
          return c.status === 'Active' && d !== null && d >= 0 && d <= 90;
        });
        const months = Array.from({ length: 6 }, (_, i) => monthKey(addMonths(`${t.slice(0, 8)}01`, i)));
        return {
          decisionsThisWeek: renewalsThisWeek + approvalsWaiting,
          renewalsThisWeek,
          approvalsWaiting,
          renewingIn90: { count: due.length, valueDkk: Math.round(due.reduce((sum, c) => sum + annualDkk(c), 0)) },
          myContracts: db.contracts.filter((c) => isOnContract(u, c) && c.status !== 'Expired' && c.status !== 'Terminated').length,
          tasks,
          noticeByMonth: months.map((month) => ({
            month,
            count: mine.filter((c) => c.status === 'Active' && deadlineOf(c)?.startsWith(month)).length,
          })),
          recent: (db.recent[u.id] ?? [])
            .map((id) => byId(db.contracts, id))
            .filter((c): c is ContractRec => !!c && sees(u, c))
            .slice(0, 3)
            .map((c) => ({ id: c.id, title: c.title })),
        };
      }),

    search: (q) =>
      run(() => {
        const u = me();
        const needle = q.trim().toLowerCase();
        if (needle.length < 2) return [];
        const contracts = visible(u)
          .filter((c) => [c.title, c.counterpartyName, c.contractNo].some((v) => v.toLowerCase().includes(needle)))
          .slice(0, 6)
          .map((c) => ({ kind: 'Contract' as const, id: c.id, title: c.title, subtitle: `${c.counterpartyName} · ${nameOf(db.entities, c.entityId)}` }));
        const suppliers = db.suppliers
          .filter((s) => s.name.toLowerCase().includes(needle))
          .map((s) => ({ kind: 'Supplier' as const, id: s.id, title: s.name, subtitle: `Supplier${s.country ? ` · ${s.country}` : ''}` }));
        return [...contracts, ...suppliers].slice(0, 8);
      }),

    getContracts: (query = {}) =>
      run(() => {
        const u = me();
        return queryRows(visible(u).map(toRow), query, u.id);
      }),

    exportContracts: (query = {}) =>
      run(() => {
        const u = me();
        const rows = sortRows(filterRows(visible(u).map(toRow), query, u.id), query.sort);
        audit('RegisterExported', null, `Exported ${rows.length} contracts to CSV`);
        const csv = String.fromCharCode(0xfeff) + toCsv(rows.map((r) => r.item));
        return { blob: new Blob([csv], { type: 'text/csv;charset=utf-8' }), fileName: `dafa-contracts-${today()}.csv` };
      }),

    getContract: (id) =>
      run(() => {
        const u = me();
        const c = contractFor(id, u);
        db.recent[u.id] = [c.id, ...(db.recent[u.id] ?? []).filter((x) => x !== c.id)].slice(0, 5);
        return toDetail(c, u);
      }),

    createContract: (input) =>
      run(() => {
        const u = me();
        if (!canCreate(u)) throw forbidden('Your role does not allow creating contracts.');
        checkRefs(input);
        const createdAt = today();
        const c: ContractRec = {
          id: newId(),
          contractNo: `C-${createdAt.slice(0, 4)}-${String(db.nextContractSeq++).padStart(4, '0')}`,
          status: 'Draft',
          signedDate: null,
          signingMethod: null,
          renewalDecision: null,
          renewalDecidedBy: null,
          renewalDecidedAt: null,
          renewalComment: null,
          createdAt,
          createdBy: u.id,
          approvedAt: null,
          deviations: [],
        } as unknown as ContractRec;
        applyInput(c, input);
        db.contracts.push(c);
        audit('ContractCreated', c.id, 'Draft created');
        if (input.uploadId) {
          const i = db.uploads.findIndex((x) => x.id === input.uploadId);
          const upload = db.uploads[i];
          if (!upload) throw invalid('The uploaded file is no longer available. Upload it again.', { uploadId: ['Unknown upload'] });
          db.uploads.splice(i, 1);
          addDocument(c, upload.fileName, upload.kind, upload.blob, u.id);
          c.deviations = upload.deviations;
          if (!c.summary && upload.summary) c.summary = upload.summary;
          audit('DocumentUploaded', c.id, `${upload.fileName} uploaded`);
        }
        return toDetail(c, u);
      }),

    updateContract: (id, input) =>
      run(() => {
        const u = me();
        const c = editable(id, u);
        if (c.status === 'InApproval') throw conflict('The contract is in approval and cannot be changed until the approval is finished.');
        checkRefs(input);
        applyInput(c, input);
        audit('ContractUpdated', c.id, 'Details updated');
        return toDetail(c, u);
      }),

    submitContract: (id) =>
      run(() => {
        const u = me();
        const c = editable(id, u);
        if (c.status !== 'Draft') throw conflict('Only a draft can be submitted for approval.');
        const missing = missingData(c);
        if (missing.length) throw invalid(`Complete the contract before you submit it: ${missing.join(', ').toLowerCase()}.`);
        startApproval(c);
        c.status = 'InApproval';
        audit('ApprovalStarted', c.id, 'Submitted for approval');
        return toDetail(c, u);
      }),

    signContract: (id, input) =>
      run(() => {
        const u = me();
        const c = editable(id, u);
        if (c.status !== 'Approved') throw conflict('Only an approved contract can be marked as signed.');
        if (!input.signedDate || !/^\d{4}-\d{2}-\d{2}$/.test(input.signedDate)) throw invalid('Enter the date the contract was signed.', { signedDate: ['Required'] });
        if (input.signedDate > today()) throw invalid('The signing date cannot be in the future.', { signedDate: ['Cannot be in the future'] });
        if (!input.method?.trim()) throw invalid('Choose how the contract was signed.', { method: ['Required'] });
        c.signedDate = input.signedDate;
        c.signingMethod = input.method.trim();
        c.status = 'Active';
        audit('ContractSigned', c.id, `Marked as signed (${c.signingMethod})`);
        return toDetail(c, u);
      }),

    decideRenewal: (id, input) =>
      run(() => {
        const u = me();
        const c = contractFor(id, u);
        if (!canDecideRenewal(u, c)) throw forbidden('Only the people on an active contract can record the renewal decision.');
        if (!(input.decision in DECISION_TEXT)) throw invalid('Choose a decision.', { decision: ['Required'] });
        if ((input.decision === 'GiveNotice' || input.decision === 'LetExpire') && !input.comment?.trim()) {
          throw invalid('Add a comment that explains why the contract ends.', { comment: ['Required for this decision'] });
        }
        const comment = input.comment?.trim() || null;
        audit('RenewalDecision', c.id, `Renewal decision: ${DECISION_TEXT[input.decision]}${comment ? ` – ${comment}` : ''}`);
        if (input.decision === 'Renew' && c.autoRenew && c.renewalMonths && c.endDate) {
          // Renew on an auto-renew contract rolls the end date forward and clears the decision.
          c.endDate = addMonths(c.endDate, c.renewalMonths);
          c.renewalDecision = null;
          c.renewalDecidedBy = null;
          c.renewalDecidedAt = null;
          c.renewalComment = null;
          audit('ContractRenewed', c.id, `Renewed for ${c.renewalMonths} months, new end date ${formatDate(c.endDate)}`);
        } else {
          c.renewalDecision = input.decision;
          c.renewalDecidedBy = u.id;
          c.renewalDecidedAt = now();
          c.renewalComment = comment;
          if (input.decision === 'GiveNotice') c.status = 'NoticeGiven';
        }
        if (c.ownerId !== u.id) notify(c.ownerId, `Renewal decision: ${c.title}`, `${u.name} recorded "${DECISION_TEXT[input.decision]}".`, c.id);
        return toDetail(c, u);
      }),

    uploadDocument: (contractId, file, kind) =>
      run(() => {
        const u = me();
        const c = editable(contractId, u);
        if (!file || file.size === 0) throw invalid('Choose a file to upload.', { file: ['Required'] });
        if (file.size > 25 * 1024 * 1024) throw invalid('The file is larger than 25 MB.', { file: ['File too large'] });
        const doc = addDocument(c, file.name, kind, file, u.id);
        audit('DocumentUploaded', c.id, `${doc.name} uploaded${doc.version > 1 ? ` (version ${doc.version})` : ''}`);
        return toDetail(c, u);
      }),

    getDocumentContent: (documentId) =>
      run(() => {
        const u = me();
        const d = byId(db.documents, documentId);
        const c = d ? byId(db.contracts, d.contractId) : undefined;
        if (!d || !c || !sees(u, c)) throw notFound('Document');
        audit('DocumentOpened', c.id, `${d.name} opened`);
        return { blob: d.blob ?? seededDocumentPdf(d, c), fileName: d.name };
      }),

    getObligations: (contractId) =>
      run(() => {
        const c = contractFor(contractId, me());
        return db.obligations.filter((o) => o.contractId === c.id).map(toObligation);
      }),

    createObligation: (contractId, input) =>
      run(() => {
        const c = editable(contractId, me());
        validateObligation(input);
        const o: ObligationRec = { id: newId(), contractId: c.id, description: input.description.trim(), dueDate: input.dueDate, frequencyMonths: input.frequencyMonths, responsibleId: input.responsibleId, status: 'Open', lastCompletedAt: null };
        db.obligations.push(o);
        audit('ObligationAdded', c.id, `Obligation added: ${o.description}`);
        return toObligation(o);
      }),

    updateObligation: (id, input) =>
      run(() => {
        const o = byId(db.obligations, id);
        if (!o) throw notFound('Obligation');
        const c = editable(o.contractId, me());
        validateObligation(input);
        Object.assign(o, { description: input.description.trim(), dueDate: input.dueDate, frequencyMonths: input.frequencyMonths, responsibleId: input.responsibleId });
        audit('ObligationUpdated', c.id, `Obligation updated: ${o.description}`);
        return toObligation(o);
      }),

    deleteObligation: (id) =>
      run(() => {
        const o = byId(db.obligations, id);
        if (!o) throw notFound('Obligation');
        const c = editable(o.contractId, me());
        db.obligations.splice(db.obligations.indexOf(o), 1);
        audit('ObligationDeleted', c.id, `Obligation deleted: ${o.description}`);
      }),

    completeObligation: (id) =>
      run(() => {
        const u = me();
        const o = byId(db.obligations, id);
        if (!o) throw notFound('Obligation');
        const c = contractFor(o.contractId, u);
        if (o.responsibleId !== u.id && !canEdit(u, c)) throw forbidden('Only the responsible person or the people on the contract can complete this.');
        if (o.status === 'Done') throw conflict('This obligation is already done.');
        o.lastCompletedAt = now();
        if (o.frequencyMonths) {
          o.dueDate = addMonths(o.dueDate ?? today(), o.frequencyMonths);
          audit('ObligationCompleted', c.id, `Obligation done: ${o.description}. Next due ${formatDate(o.dueDate)}`);
        } else {
          o.status = 'Done';
          audit('ObligationCompleted', c.id, `Obligation done: ${o.description}`);
        }
        return toObligation(o);
      }),

    createLink: (contractId, input) =>
      run(() => {
        const u = me();
        const c = editable(contractId, u);
        const target = byId(db.contracts, input.toContractId);
        if (!target || !sees(u, target)) throw invalid('Choose a contract to link to.', { toContractId: ['Unknown contract'] });
        if (target.id === c.id) throw invalid('A contract cannot be linked to itself.', { toContractId: ['Same contract'] });
        if (!['Amendment', 'Appendix', 'Framework', 'Related'].includes(input.kind)) throw invalid('Choose the kind of link.', { kind: ['Required'] });
        if (db.links.some((l) => (l.fromId === c.id && l.toId === target.id) || (l.fromId === target.id && l.toId === c.id))) throw conflict('These two contracts are already linked.');
        db.links.push({ id: newId(), fromId: c.id, toId: target.id, kind: input.kind });
        audit('LinkAdded', c.id, `Linked to ${target.title} (${input.kind.toLowerCase()})`);
        return toDetail(c, u);
      }),

    deleteLink: (id) =>
      run(() => {
        const u = me();
        const l = byId(db.links, id);
        if (!l) throw notFound('Link');
        const from = byId(db.contracts, l.fromId);
        const to = byId(db.contracts, l.toId);
        const mayEdit = [from, to].some((c) => c && sees(u, c) && canEdit(u, c));
        if (!mayEdit) throw forbidden();
        db.links.splice(db.links.indexOf(l), 1);
        audit('LinkRemoved', l.fromId, `Link to ${to?.title ?? 'contract'} removed`);
      }),

    extract: (file) =>
      run(() => {
        const u = me();
        if (!canCreate(u)) throw forbidden('Your role does not allow creating contracts.');
        return buildExtract(file, u);
      }),

    getTemplates: () =>
      run(() => {
        me();
        return db.templates.map(toTemplate);
      }),

    fromTemplate: (input) =>
      run(() => {
        const u = me();
        if (!canCreate(u)) throw forbidden('Your role does not allow creating contracts.');
        const t = byId(db.templates, input.templateId);
        if (!t) throw notFound('Template');
        const text = t.body.replace(/\{\{(\w+)\}\}/g, (_m, key: string) => input.fields[key]?.trim() || `[${key}]`);
        const blob = makeRtf(text);
        const party = input.fields.counterparty?.trim();
        const fileName = `${t.name}${party ? ` – ${party}` : ''} – draft.rtf`.replace(/[\\/:*?"<>|]/g, '');
        const uploadId = newId();
        db.uploads.push({ id: uploadId, fileName, size: blob.size, blob, kind: 'Main', deviations: [{ severity: 'None', text: 'Drafted from the DAFA template', standard: 'DAFA standard terms' }], summary: null });
        return { uploadId, fileName };
      }),

    getMyApprovals: () =>
      run(() => {
        const u = me();
        const t = today();
        return myPendingSteps(u)
          .map(({ a, step, c }): MyApproval => ({ instance: toApproval(a), stepId: step.id, waitingDays: step.pendingSince ? daysBetween(step.pendingSince, t) : 0, contract: toDetail(c, u) }))
          .sort((x, y) => y.waitingDays - x.waitingDays);
      }),

    decideApprovalStep: (stepId, input) =>
      run(() => {
        const u = me();
        const { a, stage, step } = findStep(stepId);
        const c = byId(db.contracts, a.contractId);
        if (!c) throw notFound();
        if (step.approverId !== u.id) throw forbidden('This approval step is assigned to someone else.');
        if (a.status !== 'Pending' || step.status !== 'Pending') throw conflict('This approval step is no longer open.');
        const comment = input.comment?.trim() || null;
        if (input.decision !== 'Approve' && input.decision !== 'Reject') throw invalid('Choose approve or reject.', { decision: ['Required'] });
        if (input.decision === 'Reject' && !comment) throw invalid('Add a comment that explains why you reject the contract.', { comment: ['Required when you reject'] });
        step.decidedAt = now();
        step.comment = comment;
        if (input.decision === 'Reject') {
          step.status = 'Rejected';
          a.status = 'Rejected';
          c.status = 'Draft';
          audit('ApprovalDecision', c.id, `Rejected as ${step.roleLabel}: ${comment}`);
          notify(c.ownerId, `Rejected: ${c.title}`, `${u.name} rejected the contract: ${comment}`, c.id);
          return;
        }
        step.status = 'Approved';
        audit('ApprovalDecision', c.id, `Approved as ${step.roleLabel}${comment ? `: ${comment}` : ''}`);
        const open = stage.steps.some((s) => s.status === 'Pending' || s.status === 'Waiting');
        if (open) return;
        const next = a.stages.find((s) => s.order === a.currentStage + 1);
        if (next) {
          a.currentStage = next.order;
          activateStage(a, c);
          return;
        }
        a.status = 'Approved';
        c.status = 'Approved';
        c.approvedAt = today();
        addDocument(c, 'Approval log.pdf', 'ApprovalLog', makePdf(approvalLog(a, c)), u.id);
        audit('ApprovalCompleted', c.id, 'Approved by all approvers. Approval log saved to the documents', null);
        notify(c.ownerId, `Approved: ${c.title}`, 'All approvers have approved. Mark the contract as signed when both parties have signed.', c.id);
      }),

    delegateApprovalStep: (stepId, userId) =>
      run(() => {
        const u = me();
        const { a, stage, step } = findStep(stepId);
        const c = byId(db.contracts, a.contractId);
        const to = userOf(userId);
        if (!c) throw notFound();
        if (step.approverId !== u.id) throw forbidden('This approval step is assigned to someone else.');
        if (a.status !== 'Pending' || step.status !== 'Pending') throw conflict('This approval step is no longer open.');
        if (!to) throw invalid('Choose who to delegate to.', { userId: ['Unknown user'] });
        if (to.id === u.id) throw invalid('You cannot delegate to yourself.', { userId: ['Same user'] });
        if (stage.steps.some((s) => s.approverId === to.id && s.status === 'Pending')) throw conflict(`${to.name} is already approving this stage.`);
        step.status = 'Delegated';
        step.decidedAt = now();
        step.comment = `Delegated to ${to.name}`;
        const base = step.roleLabel.replace(/ \(delegated by .*\)$/, '');
        stage.steps.push({ id: newId(), approverId: to.id, roleLabel: `${base} (delegated by ${u.name})`, status: 'Pending', decidedAt: null, comment: null, pendingSince: today() });
        audit('ApprovalDelegated', c.id, `${step.roleLabel} step delegated to ${to.name}`);
        notify(to.id, `Approval delegated to you: ${c.title}`, `${u.name} delegated their approval step to you.`, c.id);
      }),

    previewApproval: (query) =>
      run((): ApprovalPreview => {
        me();
        const rule = matchRule(query.entityId, query.typeId, query.departmentId, query.value ?? 0);
        if (!rule) return { stages: [] };
        return {
          stages: resolveStages(rule, query.entityId, query.departmentId).map((s) => ({
            order: s.order,
            approvers: s.approvers.map((a) => `${a.label} · ${userName(a.userId)}`),
          })),
        };
      }),

    getRenewals: (months = 12) =>
      run(() => {
        const u = me();
        const t = today();
        const until = addMonths(t, months);
        return visible(u)
          .filter((c) => c.status === 'Active')
          .map((c) => ({ c, deadline: deadlineOf(c) }))
          .filter((x): x is { c: ContractRec; deadline: string } => !!x.deadline && x.deadline >= t && x.deadline <= until)
          .sort((a, b) => a.deadline.localeCompare(b.deadline))
          .map(({ c, deadline }) => ({
            id: c.id,
            title: c.title,
            entityId: c.entityId,
            entityName: nameOf(db.entities, c.entityId),
            departmentName: nameOf(db.departments, c.departmentId),
            noticeDeadline: deadline,
            daysToNotice: daysBetween(t, deadline),
            autoRenew: c.autoRenew,
            annualValue: c.annualValue,
            currency: c.currency,
          }));
      }),

    getOverview: () =>
      run(() => {
        const u = me();
        if (!canSeeOverview(u)) throw forbidden('Overview is for entity management, group management and administrators.');
        const all = visible(u);
        const live = all.filter(isLive);
        const t = today();
        const group = <K extends string>(items: ContractRec[], key: (c: ContractRec) => K, value: (c: ContractRec) => number) => {
          const map = new Map<K, number>();
          for (const c of items) map.set(key(c), (map.get(key(c)) ?? 0) + value(c));
          return [...map.entries()];
        };
        return {
          activeContracts: live.length,
          annualValueDkk: Math.round(live.reduce((s, c) => s + annualDkk(c), 0)),
          dueIn90: live.filter((c) => {
            const d = daysOf(c);
            return c.status === 'Active' && d !== null && d >= 0 && d <= 90;
          }).length,
          inApproval: all.filter((c) => c.status === 'InApproval').length,
          missingData: all.filter((c) => missingData(c).length > 0).length,
          valueByEntity: group(live, (c) => nameOf(db.entities, c.entityId), annualDkk)
            .map(([name, valueDkk]) => ({ name, valueDkk: Math.round(valueDkk) }))
            .sort((a, b) => b.valueDkk - a.valueDkk),
          countByType: group(live, (c) => nameOf(db.contractTypes, c.typeId), () => 1)
            .map(([name, count]) => ({ name, count }))
            .sort((a, b) => b.count - a.count),
          changeOfControl: live.filter((c) => c.changeOfControl).map((c) => ({ id: c.id, title: c.title, entityName: nameOf(db.entities, c.entityId) })),
          expiringNoRenewal: live
            .filter((c) => c.status === 'Active' && !c.autoRenew && c.endDate !== null && c.endDate >= t && c.endDate <= addDays(t, 365) && c.renewalDecision === null)
            .sort((a, b) => (a.endDate ?? '').localeCompare(b.endDate ?? ''))
            .map(toListItem),
        };
      }),

    admin: {
      getApprovalRules: () => run(() => (admin(), db.approvalRules.map((r) => structuredClone(r)))),
      createApprovalRule: (input) =>
        run(() => {
          admin();
          validateRule(input);
          const rule: ApprovalRule = { ...structuredClone(input), id: newId() };
          db.approvalRules.push(rule);
          audit('SettingsChanged', null, `Approval rule "${rule.name}" added`);
          return structuredClone(rule);
        }),
      updateApprovalRule: (id, input) =>
        run(() => {
          admin();
          const i = db.approvalRules.findIndex((r) => r.id === id);
          if (i < 0) throw notFound('Approval rule');
          validateRule(input);
          const rule: ApprovalRule = { ...structuredClone(input), id };
          db.approvalRules[i] = rule;
          audit('SettingsChanged', null, `Approval rule "${rule.name}" changed`);
          return structuredClone(rule);
        }),
      deleteApprovalRule: (id) =>
        run(() => {
          admin();
          const i = db.approvalRules.findIndex((r) => r.id === id);
          if (i < 0) throw notFound('Approval rule');
          const [gone] = db.approvalRules.splice(i, 1);
          audit('SettingsChanged', null, `Approval rule "${gone?.name ?? ''}" deleted`);
        }),

      getRetentionRules: () => run(() => (admin(), db.retentionRules.map(toRetention))),
      createRetentionRule: (input) =>
        run(() => {
          admin();
          if (!byId(db.contractTypes, input.typeId)) throw invalid('Choose a contract type.', { typeId: ['Required'] });
          if (db.retentionRules.some((r) => r.typeId === input.typeId)) throw conflict('This contract type already has a retention rule.');
          const rule = { id: newId(), typeId: input.typeId, years: input.years, action: input.action, reviewerDepartmentId: input.reviewerDepartmentId };
          db.retentionRules.push(rule);
          audit('SettingsChanged', null, `Retention rule for ${nameOf(db.contractTypes, rule.typeId)} added`);
          return toRetention(rule);
        }),
      updateRetentionRule: (id, input) =>
        run(() => {
          admin();
          const rule = byId(db.retentionRules, id);
          if (!rule) throw notFound('Retention rule');
          if (input.years !== null && !(input.years >= 0)) throw invalid('Enter the number of years.', { years: ['Must be 0 or more'] });
          Object.assign(rule, { typeId: input.typeId, years: input.years, action: input.action, reviewerDepartmentId: input.reviewerDepartmentId });
          audit('SettingsChanged', null, `Retention rule for ${nameOf(db.contractTypes, rule.typeId)} changed`);
          return toRetention(rule);
        }),
      deleteRetentionRule: (id) =>
        run(() => {
          admin();
          const i = db.retentionRules.findIndex((r) => r.id === id);
          if (i < 0) throw notFound('Retention rule');
          db.retentionRules.splice(i, 1);
          audit('SettingsChanged', null, 'Retention rule deleted');
        }),

      getDepartments: () => run(() => (admin(), departmentsCrud.list())),
      createDepartment: (input) => run(() => (admin(), departmentsCrud.create(input.name))),
      updateDepartment: (id, input) => run(() => (admin(), departmentsCrud.update(id, input.name))),
      deleteDepartment: (id) => run(() => (admin(), departmentsCrud.remove(id))),

      getContractTypes: () => run(() => (admin(), typesCrud.list())),
      createContractType: (input) => run(() => (admin(), typesCrud.create(input.name))),
      updateContractType: (id, input) => run(() => (admin(), typesCrud.update(id, input.name))),
      deleteContractType: (id) => run(() => (admin(), typesCrud.remove(id))),

      getTemplates: () => run(() => (admin(), db.templates.map(toTemplate))),
      createTemplate: (input) =>
        run(() => {
          admin();
          if (!input.name?.trim()) throw invalid('Enter a name for the template.', { name: ['Required'] });
          const t = { id: newId(), name: input.name.trim(), description: input.description?.trim() ?? '', typeId: input.typeId, fileName: `${input.name.trim()}.docx`, placeholders: ['title', 'entity', 'counterparty', 'startDate', 'endDate'], body: `${input.name.trim().toUpperCase()}\n\nbetween {{entity}} and {{counterparty}}\n\n{{title}}\n\nTerm: {{startDate}} to {{endDate}}.` };
          db.templates.push(t);
          audit('SettingsChanged', null, `Template "${t.name}" added`);
          return toTemplate(t);
        }),
      updateTemplate: (id, input) =>
        run(() => {
          admin();
          const t = byId(db.templates, id);
          if (!t) throw notFound('Template');
          if (!input.name?.trim()) throw invalid('Enter a name for the template.', { name: ['Required'] });
          Object.assign(t, { name: input.name.trim(), description: input.description?.trim() ?? '', typeId: input.typeId });
          audit('SettingsChanged', null, `Template "${t.name}" changed`);
          return toTemplate(t);
        }),
      deleteTemplate: (id) =>
        run(() => {
          admin();
          const i = db.templates.findIndex((t) => t.id === id);
          if (i < 0) throw notFound('Template');
          const [gone] = db.templates.splice(i, 1);
          audit('SettingsChanged', null, `Template "${gone?.name ?? ''}" deleted`);
        }),

      getReminderSettings: () => run(() => (admin(), structuredClone(db.reminderSettings))),
      updateReminderSettings: (input) =>
        run(() => {
          admin();
          const days = [...new Set(input.noticeDays ?? [])].filter((d) => Number.isInteger(d) && d > 0).sort((a, b) => b - a);
          if (days.length === 0) throw invalid('Add at least one reminder.', { noticeDays: ['Required'] });
          if (!(input.escalationDays >= 0) || !(input.obligationDays >= 0)) throw invalid('Days must be 0 or more.');
          db.reminderSettings = { noticeDays: days, escalationDays: Math.floor(input.escalationDays), obligationDays: Math.floor(input.obligationDays) };
          audit('SettingsChanged', null, `Reminder settings changed to ${days.join(', ')} days`);
          return structuredClone(db.reminderSettings);
        }),

      getAudit: (query = {}) =>
        run(() => {
          admin();
          const pageSize = 50;
          const page = Math.max(1, query.page ?? 1);
          const rows = db.audit
            .filter((a) => !query.contractId || a.contractId === query.contractId)
            .sort((a, b) => b.at.localeCompare(a.at));
          return {
            items: rows.slice((page - 1) * pageSize, page * pageSize).map((a): AuditEvent => ({ id: a.id, at: a.at, who: userName(a.userId), action: a.action, contractId: a.contractId, contractTitle: byId(db.contracts, a.contractId)?.title ?? null, text: a.text })),
            total: rows.length,
            page,
            pageSize,
          };
        }),
      getOutbox: () =>
        run(() => {
          admin();
          return [...db.outbox]
            .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
            .map((m): OutboxMessage => {
              const to = userOf(m.toUserId);
              return { id: m.id, createdAt: m.createdAt, to: to ? `${to.name} <${to.email}>` : '–', subject: m.subject, body: m.body, channel: m.channel, contractId: m.contractId };
            });
        }),
      runJobs: () =>
        run(() => {
          admin();
          const result = runJobs(false);
          audit('JobsRun', null, `Background jobs run: ${result.remindersCreated} reminders, ${result.escalations} escalations, ${result.obligationReminders} obligation reminders`);
          return result;
        }),

      getHolidayCover: () => run(() => (admin(), db.holidayCover.map(toCover))),
      updateHolidayCover: (input) =>
        run(() => {
          admin();
          for (const h of input) {
            if (!userOf(h.userId) || !userOf(h.deputyId)) throw invalid('Choose a user and a deputy.', { userId: ['Required'] });
            if (h.userId === h.deputyId) throw invalid('The deputy must be someone else.', { deputyId: ['Same user'] });
            if (!h.from || !h.to || h.to < h.from) throw invalid('The end date must be on or after the start date.', { to: ['Before the start date'] });
          }
          db.holidayCover = input.map((h) => ({ id: newId(), userId: h.userId, deputyId: h.deputyId, from: h.from, to: h.to }));
          audit('SettingsChanged', null, 'Holiday cover changed');
          return db.holidayCover.map(toCover);
        }),
    },

    getDevUsers: () => run(() => db.users.map((u) => ({ id: u.id, name: u.name, initials: u.initials, title: u.title }))),

    devLogin: (userId) =>
      run(() => {
        const u = userOf(userId);
        if (!u) throw notFound('User');
        currentUserId = u.id;
        if (options.rememberUser) remember(u.id);
        return toMe(u);
      }),

    logout: () =>
      run(() => {
        currentUserId = null;
        if (options.rememberUser) remember(null);
      }),
  };

  const departmentsCrud = namedCrud(db.departments, 'department', (id) => db.contracts.some((c) => c.departmentId === id) || db.users.some((u) => u.departmentIds.includes(id)));
  const typesCrud = namedCrud(db.contractTypes, 'contract type', (id) => db.contracts.some((c) => c.typeId === id));

  return api;
}
