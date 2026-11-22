// Complete in-browser implementation of the Api interface, with state in memory.
// It enforces the access model for the selected demo user (see access.ts) so screens built
// on it behave like they will against the real API.
import type { Api } from '../api';
import { conflict, forbidden, invalid, notFound, problem } from './problems';
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
import { canCreate, canDecideRenewal, canEdit, canSee, isAdmin, isOnContract } from './access';
import { newId, type ApprovalRec, type ContractRec, type Db, type DocumentRec, type ObligationRec, type StepRec, type UserRec } from './db';
import { makePdf, makeRtf } from './files';
import { filterRows, queryRows, sortRows, toCsv, type RegisterRow } from './query';
import { createSeed } from './seed';
import { createR3, type MockCtx } from './r3';
import { createR4 } from './r4';
import { validateCustomFields, rulesForType, validateStandardRules, enforcesRequired, displayValue } from '../../lib/customFields';
import { matchesAll, terms as searchTerms } from '../../lib/search';
import type { NotificationEvent, RenewalItem } from '../types';

export interface MockOptions {
  /** Fixed "today" (ISO date) for deterministic tests. Defaults to the real date. */
  today?: string;
  /** Simulated network latency. */
  latencyMs?: number;
  /** Initially signed-in demo user. Defaults to the remembered user, then the first seeded user. */
  userId?: Id;
  /** Remember the selected demo user in localStorage. */
  rememberUser?: boolean;
  /**
   * Shared access code for the test site. When set, `dev/config` reports
   * `accessCodeRequired`, `dev/users` and `dev/login` need the code, and the mock starts
   * signed out (unless `userId` is given).
   */
  accessCode?: string;
}

export type MockApi = Api & {
  /** The in-memory database, exposed for tests. */
  readonly db: Db;
};

const USER_KEY = 'dafa.mock.userId';
const SIGNED_OUT = 'signed-out';
const MAX_WRONG_CODES = 10;
const LOCK_LABELS: Record<string, string> = {
  title: 'title', typeId: 'contract type', supplierId: 'supplier', counterpartyName: 'counterparty', entityId: 'legal entity', departmentId: 'department',
  startDate: 'start date', endDate: 'end date', noticePeriodDays: 'notice period', autoRenew: 'automatic renewal', renewalMonths: 'renewal period',
  annualValue: 'annual value', totalValue: 'total value', currency: 'currency', liabilityCap: 'liability cap', exclusivity: 'exclusivity',
  changeOfControl: 'change of control', governingLaw: 'governing law', priceAdjustment: 'price adjustment', priceAdjustmentDate: 'next price adjustment',
};
const ROLE_LABEL: Record<ApproverRole, string> = {
  DepartmentHead: 'Department head',
  EntityManager: 'Entity management',
  GroupCFO: 'Group CFO',
  GroupCEO: 'Group CEO',
  Legal: 'Legal',
};
// Same list as the backend's ApprovedTerms.LockedFields (which also reports `openEnded`, the form's checkbox for endDate = null).
const LOCKABLE = ['title', 'typeId', 'supplierId', 'counterpartyName', 'entityId', 'departmentId', 'startDate', 'endDate', 'noticePeriodDays', 'autoRenew', 'renewalMonths', 'annualValue', 'totalValue', 'currency', 'liabilityCap', 'exclusivity', 'changeOfControl', 'governingLaw', 'priceAdjustment', 'priceAdjustmentDate'] as const;
const LOCKED_MESSAGE = 'Approved terms are locked. Register an amendment as a linked contract, or ask an administrator.';
const UPLOAD_TYPES = /\.(pdf|docx|doc|xlsx|xls|pptx|msg|eml|txt|png|jpg|jpeg)$/i;
const READ_LIMIT_BYTES = 20 * 1024 * 1024;
const DECISION_TEXT = { Renew: 'Renew', Renegotiate: 'Renegotiate', GiveNotice: 'Give notice', LetExpire: 'Let it expire' } as const;

/** `undefined`: nothing stored. `null`: the user signed out, so a reload stays signed out. */
function remembered(): Id | null | undefined {
  try {
    const v = globalThis.localStorage?.getItem(USER_KEY);
    return v === SIGNED_OUT ? null : (v ?? undefined);
  } catch {
    return undefined;
  }
}
function remember(id: Id | null): void {
  try {
    globalThis.localStorage?.setItem(USER_KEY, id ?? SIGNED_OUT);
  } catch {
    /* storage unavailable */
  }
}

export function createMockApi(options: MockOptions = {}): MockApi {
  const today = () => options.today ?? todayIso();
  const now = () => (options.today ? `${options.today}T12:00:00Z` : new Date().toISOString());
  const latency = options.latencyMs ?? 0;
  const db = createSeed(today());

  const stored = options.rememberUser ? remembered() : undefined;
  let currentUserId: Id | null = options.userId ?? (options.accessCode || stored === null ? null : (stored ?? db.users[0]?.id ?? null));
  let wrongCodes = 0;
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

  function checkAccessCode(code: string | undefined): void {
    if (!options.accessCode) return;
    // Like the API: after too many wrong codes every attempt is refused for a while, the right code included.
    if (wrongCodes >= MAX_WRONG_CODES) throw problem(429, 'Too many attempts', 'Too many wrong access codes. Try again in a few minutes.');
    if (code === options.accessCode) return;
    if (code) wrongCodes += 1;
    throw problem(401, 'Access code required', code ? 'The access code is not correct.' : 'Enter the access code for this test site.');
  }

  function audit(action: string, contractId: Id | null, text: string, userId: Id | null = currentUserId, at = now(), changes?: { field: string; from: string | null; to: string | null }[]): void {
    db.audit.push({ id: newId(), at, userId, action, contractId, text, ...(changes ? { changes } : {}) });
  }
  function notify(toUserId: Id, subject: string, body: string, contractId: Id | null, channel: 'Mail' | 'Teams' = 'Mail', event?: NotificationEvent, link?: string | null): void {
    const setting = event ? db.notificationSettings.events.find((e) => e.event === event) : undefined;
    if (setting && !setting.enabled) return;
    db.outbox.push({ id: newId(), createdAt: now(), toUserId, subject, body, channel, contractId });
    // Release 3: the in-app notification centre, when the event has the InApp channel and the user has not turned it off.
    if (event && setting?.channels.includes('InApp')) {
      const pref = db.preferences[toUserId]?.events.find((e) => e.event === event);
      if (!pref || pref.inApp) {
        db.notifications.push({ id: newId(), userId: toUserId, event, title: subject, body, contractId, link: link ?? (contractId ? `/contracts/${contractId}` : null), actorId: currentUserId, createdAt: now(), readAt: null });
      }
    }
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
  /** Release 3: the admin-editable rates (fall back to the indicative ones). */
  const rateDkk = (amount: number, currency: string) => {
    const rate = db.currencyTable.find((x) => x.code === currency)?.rateToDkk;
    return rate ? amount * rate : toDkk(amount, currency);
  };
  const valueDkk = (c: Pick<ContractRec, 'totalValue' | 'annualValue' | 'currency'>) =>
    rateDkk(Math.max(c.totalValue ?? 0, c.annualValue ?? 0), c.currency);
  const annualDkk = (c: ContractRec) => rateDkk(c.annualValue ?? 0, c.currency);
  const isLive = (c: ContractRec) => c.status === 'Active' || c.status === 'NoticeGiven';

  const DQ_LABEL: Record<string, string> = {
    noticePeriodDays: 'Notice period',
    contractValue: 'Contract value',
    signedDate: 'Signed date',
    changeOfControl: 'Change of control',
    signedDocument: 'Signed document',
    endDate: 'End date',
    responsibleId: 'Responsible',
    paymentTerms: 'Payment terms',
    governingLaw: 'Governing law',
  };
  const dqLabel = (key: string) => DQ_LABEL[key] ?? db.customFields.find((d) => `cf.${d.key}` === key)?.label ?? key;
  const hasMainDoc = (c: ContractRec) => db.documents.some((d) => d.contractId === c.id && d.kind === 'Main');

  /** Data-quality keys (General settings) the contract is missing. */
  function missingKeys(c: ContractRec): string[] {
    const out: string[] = [];
    for (const key of db.appSettings.dataQualityFields) {
      let missing = false;
      if (key === 'noticePeriodDays') missing = c.noticePeriodDays === null;
      else if (key === 'contractValue') missing = c.annualValue === null && c.totalValue === null;
      else if (key === 'signedDate') missing = isLive(c) && !c.signedDate;
      else if (key === 'changeOfControl') missing = c.changeOfControl === null;
      else if (key === 'signedDocument') missing = isLive(c) && !hasMainDoc(c);
      else if (key === 'endDate') missing = !c.endDate;
      else if (key === 'responsibleId') missing = !c.responsibleId;
      else if (key === 'paymentTerms') missing = !c.paymentTerms;
      else if (key === 'governingLaw') missing = !c.governingLaw;
      else if (key.startsWith('cf.')) {
        const def = db.customFields.find((d) => d.key === key.slice(3) && d.isActive);
        const applies = def && (def.contractTypeIds.length === 0 || def.contractTypeIds.includes(c.typeId));
        const v = def ? c.customFields[def.key] : undefined;
        missing = !!applies && (v === undefined || v === null || v === '' || (Array.isArray(v) && v.length === 0));
      }
      if (missing) out.push(key);
    }
    return out;
  }
  function missingData(c: ContractRec): string[] {
    return missingKeys(c).map(dqLabel);
  }

  /**
   * From submission on (every status except Draft) the approved terms are locked for everyone
   * but administrators. Only fields that have a value are locked: an empty one can be filled in
   * once. Annual and total value lock together as soon as either is set.
   */
  function lockedFieldsOf(c: ContractRec, u: UserRec): (typeof LOCKABLE)[number][] {
    if (isAdmin(u) || c.status === 'Draft') return [];
    const hasValue = c.annualValue !== null || c.totalValue !== null;
    return LOCKABLE.filter((f) => {
      if (f === 'annualValue' || f === 'totalValue') return hasValue;
      const v = c[f];
      return v !== null && v !== undefined && v !== '';
    });
  }

  function recipientOf(c: ContractRec): Id {
    if (c.reminderRecipient === 'Delegate' && c.delegateId) return c.delegateId;
    if (c.reminderRecipient === 'Responsible' && c.responsibleId) return c.responsibleId;
    return c.ownerId;
  }

  // ----- projections -----
  function toListItem(c: ContractRec, viewer?: UserRec): ContractListItem {
    const owner = userOf(c.ownerId);
    return {
      entityId: c.entityId,
      departmentId: c.departmentId,
      typeId: c.typeId,
      ownerId: c.ownerId,
      delegateName: c.delegateId ? userName(c.delegateId) : null,
      responsibleName: c.responsibleId ? userName(c.responsibleId) : null,
      counterpartyId: c.supplierId,
      startDate: c.startDate,
      endDate: c.endDate,
      totalValue: c.totalValue,
      annualValueDkk: Math.round(rateDkk(c.annualValue ?? 0, c.currency)),
      totalValueDkk: Math.round(rateDkk(c.totalValue ?? 0, c.currency)),
      autoRenew: c.autoRenew,
      changeOfControl: c.changeOfControl,
      exclusivity: c.exclusivity,
      createdAt: c.createdAt,
      signedDate: c.signedDate,
      hasSignedDocument: hasMainDoc(c),
      tags: c.tagIds.map((id) => db.tags.find((t) => t.id === id)).filter((t): t is NonNullable<typeof t> => !!t).map((t) => ({ id: t.id, name: t.name, color: t.color })),
      watching: viewer ? db.watchers.some((w) => w.contractId === c.id && w.userId === viewer.id) : false,
      customFields: structuredClone(c.customFields),
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

  const toRow = (c: ContractRec, viewer?: UserRec): RegisterRow => ({
    item: toListItem(c, viewer),
    ownerId: c.ownerId,
    supplierId: c.supplierId,
    valueDkk: valueDkk(c),
    dates: { notice: deadlineOf(c), end: c.endDate, start: c.startDate, created: c.createdAt, signed: c.signedDate },
    tagIds: c.tagIds,
    watching: viewer ? db.watchers.some((w) => w.contractId === c.id && w.userId === viewer.id) : false,
    customFields: c.customFields,
    missingKeys: missingKeys(c),
    hasMainDoc: hasMainDoc(c),
    exclusivity: c.exclusivity,
    changeOfControl: c.changeOfControl,
    autoRenew: c.autoRenew,
    decided: c.renewalDecision !== null,
    entityId: c.entityId,
    departmentId: c.departmentId,
    typeId: c.typeId,
    peopleIds: [c.ownerId, c.delegateId, c.responsibleId].filter((x): x is Id => !!x),
    delegateId: c.delegateId,
    responsibleId: c.responsibleId,
    currency: c.currency,
    annualDkk: c.annualValue === null ? 0 : rateDkk(c.annualValue, c.currency),
    totalDkk: c.totalValue === null ? 0 : rateDkk(c.totalValue, c.currency),
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
      id: _id, contractNo: _no, createdAt: _ca, createdBy: _cb, approvedAt: _aa, deviations, renewalDecidedBy, tagIds: _tags, customFields, ...fields
    } = c;
    return {
      ...toListItem(c, u),
      ...fields,
      customFields: structuredClone(customFields),
      watching: db.watchers.some((w) => w.contractId === c.id && w.userId === u.id),
      watcherCount: db.watchers.filter((w) => w.contractId === c.id).length,
      commentCount: db.comments.filter((x) => x.contractId === c.id).length,
      canTag: canEdit(u, c),
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
      // An open-ended contract has no end date, but `openEnded` itself is a set value and locks.
      lockedFields: [...lockedFieldsOf(c, u), ...(!isAdmin(u) && c.status !== 'Draft' ? ['openEnded'] : [])],
      submittedById: c.submittedBy ?? null,
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
      notify(st.approverId, `Approval needed: ${c.title}`, `${userName(c.ownerId)} asks for your approval as ${st.roleLabel}.`, c.id, 'Mail', 'ApprovalRequested', '/approvals');
    }
  }

  /** The owner or submitter never approves their own contract: another approver takes the step. */
  function substituteFor(taken: Set<Id>, blocked: Set<Id>): Id | null {
    const candidates = [db.roleHolders.GroupCFO, db.roleHolders.GroupCEO, ...db.users.filter(isAdmin).map((x) => x.id), ...db.users.filter((x) => x.roles.includes('Approver')).map((x) => x.id)];
    return candidates.find((id) => !blocked.has(id) && !taken.has(id)) ?? null;
  }

  function startApproval(c: ContractRec, submitterId: Id): ApprovalRec {
    const rule = matchRule(c.entityId, c.typeId, c.departmentId, valueDkk(c));
    if (!rule) throw conflict('No approval rule matches this contract. Ask an administrator to add one in Settings.', 'no-approval-rule');
    const blocked = new Set<Id>([c.ownerId, submitterId]);
    const stages = resolveStages(rule, c.entityId, c.departmentId)
      .map((s) => {
        const taken = new Set(s.approvers.filter((a) => !blocked.has(a.userId)).map((a) => a.userId));
        const approvers: { userId: Id; label: string }[] = [];
        for (const a of s.approvers) {
          if (!blocked.has(a.userId)) {
            approvers.push(a);
            continue;
          }
          const substitute = substituteFor(taken, blocked);
          if (!substitute) continue;
          taken.add(substitute);
          approvers.push({ userId: substitute, label: `${a.label} (instead of ${userName(a.userId)}, who owns or submitted the contract)` });
        }
        return { order: s.order, approvers };
      })
      .filter((s) => s.approvers.length > 0);
    if (rule.stages.length === 0) throw conflict('The matching approval rule has no stages. Ask an administrator to add approvers in Settings.', 'no-approval-rule');
    if (stages.length === 0) throw conflict('No approver who is independent of the owner and the submitter could be found.', 'no-approver');
    c.submittedBy = submitterId;
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
    const doc: DocumentRec = { id: newId(), contractId: c.id, name, kind, storeKey: `mock/${c.id}/${name}`, version, size: blob.size, uploadedBy, uploadedAt: now(), blob, text: '' };
    // Plain-text uploads are searchable in the mock; other files match on their name.
    if (/\.txt$/i.test(name) && typeof blob.text === 'function') void blob.text().then((t) => (doc.text = t.slice(0, 400_000)));
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
    const result: JobRunResult = { ranAt: now(), remindersCreated: 0, escalations: 0, obligationReminders: 0, annualReviewReminders: 0, contractsExpired: 0 };
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
          notify(recipientId, `${escalation ? 'Escalation' : 'Reminder'}: ${c.title}`, `The notice deadline is ${formatDate(deadlineOf(c))}. Record a renewal decision in DAFA Contracts.`, c.id, 'Mail', escalation ? 'Escalation' : 'NoticeReminder');
        }
        if (escalation) result.escalations += 1;
        else result.remindersCreated += 1;
      }
    }

    // Open-ended contracts: a yearly review reminder 60 days before each anniversary of the start date.
    for (const c of db.contracts) {
      if (c.status !== 'Active' || c.endDate || !c.startDate) continue;
      let anniversary = `${t.slice(0, 4)}${c.startDate.slice(4)}`;
      if (anniversary < t) anniversary = addMonths(anniversary, 12);
      const due = addDays(anniversary, -60);
      if (due > t || anniversary <= c.startDate || has(c.id, 'AnnualReview', due)) continue;
      const recipientId = recipientOf(c);
      const sentAt = historical ? `${due}T07:00:00Z` : now();
      db.reminders.push({ id: newId(), contractId: c.id, kind: 'AnnualReview', dueDate: due, sentAt, recipientId });
      audit('ReminderSent', c.id, `Annual review reminder sent to ${userName(recipientId)} (open-ended contract)`, null, sentAt);
      if (!historical) notify(recipientId, `Annual review: ${c.title}`, 'This open-ended contract has its anniversary in 60 days. Check that it is still needed, or give notice.', c.id, 'Mail', 'AnnualReview');
      result.annualReviewReminders += 1;
    }

    for (const o of db.obligations) {
      if (o.status !== 'Open' || !o.dueDate) continue;
      const kind = `Obligation:${o.id}`;
      if (addDays(o.dueDate, -db.reminderSettings.obligationDays) > t || has(o.contractId, kind, o.dueDate)) continue;
      const sentAt = historical ? `${addDays(o.dueDate, -db.reminderSettings.obligationDays)}T07:00:00Z` : now();
      db.reminders.push({ id: newId(), contractId: o.contractId, kind, dueDate: o.dueDate, sentAt, recipientId: o.responsibleId });
      if (!historical) notify(o.responsibleId, `Obligation due ${formatDate(o.dueDate)}`, o.description, o.contractId, 'Mail', 'ObligationDue');
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
    // Like the API's reading limits: a very large file is stored, but not read for suggestions.
    if (file.size > READ_LIMIT_BYTES) {
      const id = newId();
      db.uploads.push({ id, fileName: file.name, size: file.size, blob: file, kind: 'Main', deviations: [], summary: null });
      return { uploadId: id, fileName: file.name, size: file.size, pages: 0, suggestions: [], summary: '', deviations: [], note: 'The file was too large to read, so there are no suggestions. Fill in the details yourself.' };
    }

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
    return { uploadId, fileName: file.name, size: file.size, pages: Math.max(1, Math.round(file.size / 130_000)), suggestions, summary, deviations, note: null };
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

  function checkRefs(input: ContractInput, status: ContractRec['status'] | null): void {
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
    // Release 3: custom fields, per-type required fields, tags.
    const enforce = enforcesRequired(status);
    const cfErrors = validateCustomFields(db.customFields, input.typeId, input.customFields, enforce, (id) => !!userOf(id), db.currencies);
    for (const [k, v] of Object.entries(cfErrors)) errors[k] = [v];
    if (enforce) {
      const ruleErrors = validateStandardRules(rulesForType(db.fieldRules, input.typeId), { ...input, openEnded: input.endDate === null });
      for (const [k, v] of Object.entries(ruleErrors)) if (!errors[k]) errors[k] = [v as string];
    }
    if (input.tagIds?.some((id) => !db.tags.some((t) => t.id === id))) errors.tagIds = ['Unknown tag'];
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
    // Omitted = unchanged on update, empty on create.
    if (input.customFields !== undefined) c.customFields = Object.fromEntries(Object.entries(input.customFields).filter(([, v]) => !(v === null || v === '' || (Array.isArray(v) && v.length === 0))));
    else c.customFields ??= {};
    if (input.tagIds !== undefined) c.tagIds = [...new Set(input.tagIds)];
    else c.tagIds ??= [];
  }

  /** Key terms with display values, for audit history and version comparison. */
  function keyTerms(c: ContractRec): { field: string; label: string; value: string | null }[] {
    const num = (n: number | null) => (n === null ? null : String(n));
    const yes = (b: boolean | null) => (b === null ? null : b ? 'Yes' : 'No');
    const base: { field: string; label: string; value: string | null }[] = [
      { field: 'title', label: 'Title', value: c.title },
      { field: 'counterpartyName', label: 'Counterparty', value: c.counterpartyName },
      { field: 'typeId', label: 'Contract type', value: nameOf(db.contractTypes, c.typeId) },
      { field: 'entityId', label: 'Legal entity', value: nameOf(db.entities, c.entityId) },
      { field: 'departmentId', label: 'Department', value: nameOf(db.departments, c.departmentId) },
      { field: 'ownerId', label: 'Owner', value: userName(c.ownerId) },
      { field: 'startDate', label: 'Start date', value: c.startDate },
      { field: 'endDate', label: 'End date', value: c.endDate ?? 'Open-ended' },
      { field: 'noticePeriodDays', label: 'Notice period (days)', value: num(c.noticePeriodDays) },
      { field: 'autoRenew', label: 'Automatic renewal', value: yes(c.autoRenew) },
      { field: 'renewalMonths', label: 'Renewal period (months)', value: num(c.renewalMonths) },
      { field: 'annualValue', label: 'Annual value', value: num(c.annualValue) },
      { field: 'totalValue', label: 'Total value', value: num(c.totalValue) },
      { field: 'currency', label: 'Currency', value: c.currency },
      { field: 'paymentTerms', label: 'Payment terms', value: c.paymentTerms },
      { field: 'priceAdjustment', label: 'Price adjustment', value: c.priceAdjustment },
      { field: 'priceAdjustmentDate', label: 'Next price adjustment', value: c.priceAdjustmentDate },
      { field: 'governingLaw', label: 'Governing law', value: c.governingLaw },
      { field: 'liabilityCap', label: 'Liability cap', value: c.liabilityCap },
      { field: 'exclusivity', label: 'Exclusivity', value: c.exclusivity },
      { field: 'changeOfControl', label: 'Change of control', value: yes(c.changeOfControl) },
      { field: 'status', label: 'Status', value: c.status },
    ];
    for (const d of db.customFields.filter((x) => x.isActive)) {
      const v = c.customFields[d.key];
      base.push({ field: `cf.${d.key}`, label: d.label, value: v === undefined || v === null || (Array.isArray(v) && v.length === 0) ? null : displayValue(d, v, { users: db.users }) });
    }
    return base;
  }

  function diffTerms(before: ReturnType<typeof keyTerms>, after: ReturnType<typeof keyTerms>) {
    return after
      .map((a) => ({ field: a.field, from: before.find((b) => b.field === a.field)?.value ?? null, to: a.value }))
      .filter((x) => x.from !== x.to);
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

  function renewalItem(c: ContractRec): RenewalItem | null {
    const t = today();
    const deadline = deadlineOf(c);
    if (c.status !== 'Active' || !deadline) return null;
    if (deadline < t && (c.renewalDecision !== null || (c.endDate ?? '') < t)) return null;
    return { id: c.id, title: c.title, entityId: c.entityId, entityName: nameOf(db.entities, c.entityId), departmentName: nameOf(db.departments, c.departmentId), noticeDeadline: deadline, daysToNotice: daysBetween(t, deadline), autoRenew: c.autoRenew, annualValue: c.annualValue, currency: c.currency };
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

  const ctx: MockCtx = {
    db,
    today,
    now,
    me,
    admin,
    sees,
    visible,
    contractFor,
    editable,
    toListItem,
    toRow,
    toDetail,
    audit,
    notify,
    userOf,
    userName,
    nameOf,
    deadlineOf,
    daysOf,
    valueDkk,
    toDkk: rateDkk,
    missingKeys,
    dqLabel,
    documentBlob: (d, c) => d.blob ?? seededDocumentPdf(d, c),
    keyTerms,
    renewalItem,
    run,
    nextContractNo: () => `C-${today().slice(0, 4)}-${String(db.nextContractSeq++).padStart(4, '0')}`,
    listMissing: missingData,
    runJobs: () => runJobs(false),
  };
  const r3 = createR3(ctx);
  const r4 = createR4(ctx);
  /** Release 4: the top-bar entity scope (`entityId`, repeatable); empty = no filter. */
  const inScope = (cs: ContractRec[], scope?: Id[]) => (scope?.length ? cs.filter((c) => scope.includes(c.entityId)) : cs);

  const api: MockApi = {
    db,
    ...r3.top,
    ...r4.top,

    getMe: () => run(() => toMe(me())),

    getReminderSettings: () => run(() => (me(), structuredClone(db.reminderSettings))),

    getLookups: () =>
      run(() => {
        me();
        return {
          entities: db.entities.map((x) => ({ ...x })),
          departments: db.departments.map((x) => ({ ...x })),
          contractTypes: db.contractTypes.map((x) => ({ ...x })),
          users: db.users.map((u) => ({ id: u.id, name: u.name, initials: u.initials, departmentIds: [...u.departmentIds], roles: [...u.roles], isActive: u.isActive, email: u.email, title: u.title || null })),
          currencies: [...db.currencies],
        };
      }),

    getSuppliers: (q) =>
      run(() => {
        me();
        const needle = q?.trim().toLowerCase() ?? '';
        return db.suppliers.filter((s) => s.isActive && (!needle || s.name.toLowerCase().includes(needle))).slice(0, 20).map((s) => ({ id: s.id, name: s.name, country: s.country, vatNo: s.vatNo }));
      }),

    getHome: (scope) =>
      run(() => {
        const u = me();
        const t = today();
        const mine = inScope(visible(u), scope);
        const tasks = buildTasks(u).filter((x) => !scope?.length || !x.contractId || scope.includes(byId(db.contracts, x.contractId)?.entityId ?? ''));
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
          myContracts: inScope(db.contracts, scope).filter((c) => isOnContract(u, c) && c.status !== 'Expired' && c.status !== 'Terminated').length,
          tasks,
          noticeByMonth: months.map((month) => ({
            month,
            count: mine.filter((c) => c.status === 'Active' && deadlineOf(c)?.startsWith(month)).length,
          })),
          recent: (db.recent[u.id] ?? [])
            .map((id) => byId(db.contracts, id))
            .filter((c): c is ContractRec => !!c && sees(u, c) && (!scope?.length || scope.includes(c.entityId)))
            .slice(0, 3)
            .map((c) => ({ id: c.id, title: c.title })),
        };
      }),

    search: (q, scope) =>
      run(() => {
        const u = me();
        const needle = q.trim().toLowerCase();
        if (needle.length < 2) return [];
        const contracts = inScope(visible(u), scope)
          .filter((c) => [c.title, c.counterpartyName, c.contractNo].some((v) => v.toLowerCase().includes(needle)))
          .slice(0, 6)
          .map((c) => ({ kind: 'Contract' as const, id: c.id, title: c.title, subtitle: `${c.counterpartyName} · ${nameOf(db.entities, c.entityId)}` }));
        const ts = searchTerms(q);
        const contractList = contracts.slice(0, 4);
        const visibleIds = new Map(inScope(visible(u), scope).map((c) => [c.id, c]));
        const docs = db.documents
          .filter((d) => visibleIds.has(d.contractId) && matchesAll([d.name, d.text], ts))
          .sort((a, b) => b.uploadedAt.localeCompare(a.uploadedAt))
          .slice(0, 2)
          .map((d) => ({ kind: 'Document' as const, id: d.id, title: d.name, subtitle: visibleIds.get(d.contractId)?.title ?? '', contractId: d.contractId }));
        const people = db.users
          .filter((p) => p.isActive && matchesAll([p.name, p.email], ts))
          .slice(0, 2)
          .map((p) => ({ kind: 'Person' as const, id: p.id, title: p.name, subtitle: p.title.split(' · ')[0] ?? '' }));
        const suppliers = db.suppliers
          .filter((s) => s.isActive && s.name.toLowerCase().includes(needle))
          .map((s) => ({ kind: 'Supplier' as const, id: s.id, title: s.name, subtitle: `Supplier${s.country ? ` · ${s.country}` : ''}` }));
        return [...contractList, ...docs, ...people, ...suppliers].slice(0, 8);
      }),

    getContracts: (query = {}) =>
      run(() => {
        const u = me();
        return queryRows(visible(u).map((c) => toRow(c, u)), query, u.id, { today: today(), customFields: db.customFields });
      }),

    exportContracts: (query = {}) =>
      run(() => {
        const u = me();
        const rows = sortRows(filterRows(visible(u).map((c) => toRow(c, u)), query, u.id, { today: today(), customFields: db.customFields }), query.sort);
        audit('RegisterExported', null, `Exported ${rows.length} contracts to CSV`);
        const columns = (query as { columns?: string[] }).columns;
        const csv = String.fromCharCode(0xfeff) + toCsv(rows.map((r) => r.item), columns, db.customFields);
        return { blob: new Blob([csv], { type: 'text/csv;charset=utf-8' }), fileName: `dafa-contracts-${today()}.csv` };
      }),

    getContract: (id, opts) =>
      run(() => {
        const u = me();
        const c = contractFor(id, u);
        if (opts?.peek) return toDetail(c, u);
        db.recent[u.id] = [c.id, ...(db.recent[u.id] ?? []).filter((x) => x !== c.id)].slice(0, 5);
        return toDetail(c, u);
      }),

    createContract: (input) =>
      run(() => {
        const u = me();
        if (!canCreate(u)) throw forbidden('Your role does not allow creating contracts.');
        checkRefs(input, null);
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
          tagIds: [],
          customFields: {},
        } as unknown as ContractRec;
        applyInput(c, input);
        db.contracts.push(c);
        audit('ContractCreated', c.id, 'Draft created');
        // Personal setting: watch the contracts I create or am added to.
        for (const pid of [c.ownerId, c.delegateId, c.responsibleId]) {
          if (pid && db.preferences[pid]?.autoWatch && !db.watchers.some((w) => w.contractId === c.id && w.userId === pid)) db.watchers.push({ contractId: c.id, userId: pid });
        }
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
        checkRefs(input, c.status);
        const changed = lockedFieldsOf(c, u).filter((f) => (input[f] ?? null) !== (c[f] ?? null));
        if (changed.length > 0) throw conflict(`${LOCKED_MESSAGE} Locked: ${changed.map((f) => LOCK_LABELS[f] ?? f).join(', ')}.`, 'locked-fields');
        const before = keyTerms(c);
        applyInput(c, input);
        const changes = diffTerms(before, keyTerms(c));
        audit('ContractUpdated', c.id, 'Details updated', currentUserId, now(), changes.length ? changes : undefined);
        r3.watchersChanged(c, u, 'updated the details');
        return toDetail(c, u);
      }),

    submitContract: (id) =>
      run(() => {
        const u = me();
        const c = editable(id, u);
        if (c.status !== 'Draft') throw conflict('Only a draft can be submitted for approval.');
        const missing = missingData(c);
        if (missing.length) throw invalid(`Complete the contract before you submit it: ${missing.join(', ').toLowerCase()}.`);
        const cfErrors = validateCustomFields(db.customFields, c.typeId, c.customFields, true, (id) => !!userOf(id), db.currencies);
        const ruleErrors = validateStandardRules(rulesForType(db.fieldRules, c.typeId), { ...c, openEnded: c.endDate === null });
        const all = [...Object.values(cfErrors), ...Object.values(ruleErrors)];
        if (all.length) throw invalid(`Complete the contract before you submit it: ${all.join(', ').toLowerCase()}.`, Object.fromEntries(Object.entries({ ...cfErrors, ...ruleErrors }).map(([k, v]) => [k, [v as string]])));
        startApproval(c, u.id);
        c.status = 'InApproval';
        audit('ApprovalStarted', c.id, 'Submitted for approval');
        r3.watchersChanged(c, u, 'submitted the contract for approval');
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
        r3.watchersChanged(c, u, 'marked the contract as signed');
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
        if (!c.endDate && input.decision !== 'GiveNotice') throw conflict('An open-ended contract can only be ended by giving notice.');
        if (input.decision === 'LetExpire' && c.autoRenew) throw invalid('A contract that renews automatically cannot be left to expire. Give notice instead.', { decision: ['Not available for an auto-renew contract'] });
        if (input.decision === 'Renew') {
          // Renewing is possible from the first reminder of the term (the largest notice offset).
          const deadline = deadlineOf(c);
          const opens = deadline ? addDays(deadline, -Math.max(0, ...db.reminderSettings.noticeDays)) : null;
          if (opens && today() < opens) throw conflict(`It is too early to renew this contract. Renewal opens on ${formatDate(opens)}, when the first reminder is sent.`);
        }
        // A new end date is required on a fixed-term contract and overrides the renewal period on an auto-renew one.
        const extend = input.decision === 'Renew' && !!c.endDate && (!(c.autoRenew && c.renewalMonths) || !!input.newEndDate);
        if (extend) {
          if (!input.newEndDate || !/^\d{4}-\d{2}-\d{2}$/.test(input.newEndDate)) throw invalid('Enter the new end date.', { newEndDate: ['Required when the contract does not renew automatically'] });
          if (input.newEndDate <= (c.endDate ?? '')) throw invalid('The new end date must be after the current end date.', { newEndDate: ['Must be after the current end date'] });
        }
        const comment = input.comment?.trim() || null;
        audit('RenewalDecision', c.id, `Renewal decision: ${DECISION_TEXT[input.decision]}${comment ? ` – ${comment}` : ''}`);
        if (extend && input.newEndDate) {
          // Renew on a fixed-term contract moves the end date to the one given and clears the decision.
          c.endDate = input.newEndDate;
          c.renewalDecision = null;
          c.renewalDecidedBy = null;
          c.renewalDecidedAt = null;
          c.renewalComment = null;
          audit('ContractRenewed', c.id, `Renewed, new end date ${formatDate(c.endDate)}`);
        } else if (input.decision === 'Renew' && c.autoRenew && c.renewalMonths && c.endDate) {
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
          if (input.decision === 'GiveNotice') {
            c.status = 'NoticeGiven';
            // Open-ended: the contract now ends when the notice period has run.
            if (!c.endDate) c.endDate = addDays(today(), c.noticePeriodDays ?? 0);
          }
        }
        if (c.ownerId !== u.id) notify(c.ownerId, `Renewal decision: ${c.title}`, `${u.name} recorded "${DECISION_TEXT[input.decision]}".`, c.id, 'Mail', 'RenewalDecision');
        r3.watchersChanged(c, u, `recorded the renewal decision "${DECISION_TEXT[input.decision]}"`);
        return toDetail(c, u);
      }),

    uploadDocument: (contractId, file, kind) =>
      run(() => {
        const u = me();
        const c = editable(contractId, u);
        if (!file || file.size === 0) throw invalid('Choose a file to upload.', { file: ['Required'] });
        if (file.size > 25 * 1024 * 1024) throw invalid('The file is larger than 25 MB.', { file: ['File too large'] });
        if (!UPLOAD_TYPES.test(file.name)) throw invalid('This file type cannot be uploaded.', { file: ['File type not allowed'] });
        const replacesMain = db.documents.some((d) => d.contractId === c.id && d.kind === 'Main' && d.name === file.name);
        if ((kind === 'Main' || replacesMain) && !isAdmin(u) && c.status !== 'Draft') {
          throw conflict('The main document is locked once the contract is submitted. Upload the file as an amendment, or ask an administrator.', 'locked-fields');
        }
        const doc = addDocument(c, file.name, kind, file, u.id);
        audit('DocumentUploaded', c.id, `${doc.name} uploaded${doc.version > 1 ? ` (version ${doc.version})` : ''}`);
        r3.watchersChanged(c, u, `uploaded ${doc.name}`);
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
        if (db.links.some((l) => (l.fromId === c.id && l.toId === target.id) || (l.fromId === target.id && l.toId === c.id))) throw conflict('These two contracts are already linked.', 'duplicate');
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
        if (file.size > 25 * 1024 * 1024) throw invalid('The file is larger than 25 MB.', { file: ['File too large'] });
        if (!UPLOAD_TYPES.test(file.name)) throw invalid('This file type cannot be uploaded.', { file: ['File type not allowed'] });
        const result = buildExtract(file, u);
        if (!db.appSettings.features.aiSuggestions) return { ...result, suggestions: [], summary: '', deviations: [], note: 'AI suggestions are turned off.' };
        return result;
      }),

    getTemplates: () =>
      run(() => {
        me();
        return db.appSettings.features.templates ? db.templates.map(toTemplate) : [];
      }),

    fromTemplate: (input) =>
      run(() => {
        const u = me();
        if (!canCreate(u)) throw forbidden('Your role does not allow creating contracts.');
        if (!db.appSettings.features.templates) throw problem(400, 'Feature off', 'Templates are turned off in Settings.', undefined, 'urn:dafa:problem:feature-off');
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

    getMyApprovals: (scope) =>
      run(() => {
        const u = me();
        const t = today();
        return myPendingSteps(u)
          .filter(({ c }) => !scope?.length || scope.includes(c.entityId))
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
          notify(c.ownerId, `Rejected: ${c.title}`, `${u.name} rejected the contract: ${comment}`, c.id, 'Mail', 'ApprovalDecided');
          r3.watchersChanged(c, u, 'rejected the contract');
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
        notify(c.ownerId, `Approved: ${c.title}`, 'All approvers have approved. Mark the contract as signed when both parties have signed.', c.id, 'Mail', 'ApprovalDecided');
        r3.watchersChanged(c, u, 'approval completed');
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
        if (to.id === c.ownerId || to.id === c.submittedBy) throw invalid('The owner or submitter of the contract cannot approve it.', { userId: ['Owner or submitter'] });
        if (!to.roles.includes('Approver') && !isAdmin(to)) throw invalid(`${to.name} does not have the Approver role.`, { userId: ['Not an approver'] });
        if (stage.steps.some((s) => s.approverId === to.id && s.status !== 'Delegated')) throw invalid(`${to.name} is already an approver in this stage.`, { userId: ['Already an approver in this stage'] });
        step.status = 'Delegated';
        step.decidedAt = now();
        step.comment = `Delegated to ${to.name}`;
        const base = step.roleLabel.replace(/ \(delegated by .*\)$/, '');
        stage.steps.push({ id: newId(), approverId: to.id, roleLabel: `${base} (delegated by ${u.name})`, status: 'Pending', decidedAt: null, comment: null, pendingSince: today() });
        audit('ApprovalDelegated', c.id, `${step.roleLabel} step delegated to ${to.name}`);
        notify(to.id, `Approval delegated to you: ${c.title}`, `${u.name} delegated their approval step to you.`, c.id, 'Mail', 'ApprovalDelegated', '/approvals');
      }),

    previewApproval: (query) =>
      run((): ApprovalPreview => {
        me();
        // `value` is the larger of total and annual value, in DKK (the caller sends it that way).
        const rule = matchRule(query.entityId, query.typeId, query.departmentId, query.value ?? 0);
        if (!rule) return { stages: [], ruleId: null, ruleName: null };
        return {
          ruleId: rule.id,
          ruleName: rule.name,
          stages: resolveStages(rule, query.entityId, query.departmentId).map((s) => ({
            order: s.order,
            approvers: s.approvers.map((a) => `${a.label} · ${userName(a.userId)}`),
          })),
        };
      }),

    getRenewals: (months = 12, scope) =>
      run(() => {
        const u = me();
        const t = today();
        const until = addMonths(t, months);
        return inScope(visible(u), scope)
          .filter((c) => c.status === 'Active')
          .map((c) => ({ c, deadline: deadlineOf(c) }))
          // Also the deadlines that passed without a decision while the contract is still running.
          .filter((x): x is { c: ContractRec; deadline: string } => !!x.deadline && x.deadline <= until && (x.deadline >= t || (x.c.renewalDecision === null && (x.c.endDate ?? '') >= t)))
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

    getOverview: (scope) =>
      run(() => {
        const u = me();
        const all = inScope(visible(u), scope);
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
            .map((c) => toListItem(c, u)),
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
      createTemplate: (input, file) =>
        run(() => {
          admin();
          if (!input.name?.trim()) throw invalid('Enter a name for the template.', { name: ['Required'] });
          if (!file) throw invalid('Choose the Word file for the template.', { file: ['Required'] });
          if (!/\.(docx|dotx)$/i.test(file.name)) throw invalid('Templates must be Word files (.docx or .dotx).', { file: ['Must be a Word file'] });
          if (db.templates.some((x) => x.name.toLowerCase() === input.name.trim().toLowerCase())) throw conflict('A template with that name already exists.');
          const t = { id: newId(), name: input.name.trim(), description: input.description?.trim() ?? '', typeId: input.typeId, fileName: file.name, blob: file as Blob, placeholders: ['title', 'entity', 'counterparty', 'startDate', 'endDate'], body: `${input.name.trim().toUpperCase()}\n\nbetween {{entity}} and {{counterparty}}\n\n{{title}}\n\nTerm: {{startDate}} to {{endDate}}.` };
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
      getTemplateContent: (id) =>
        run(() => {
          admin();
          const t = byId(db.templates, id);
          if (!t) throw notFound('Template');
          // Uploaded templates keep their bytes; the seeded ones are generated as .rtf in the mock.
          if (t.blob) return { blob: t.blob, fileName: t.fileName };
          return { blob: makeRtf(t.body), fileName: t.fileName.replace(/\.[a-z]+$/i, '.rtf') };
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
          audit('JobsRun', null, `Background jobs run: ${result.remindersCreated} reminders, ${result.escalations} escalations, ${result.obligationReminders} obligation reminders, ${result.annualReviewReminders} annual review reminders`);
          return result;
        }),

      ...r3.admin,
      ...r4.admin,
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

    getDevConfig: () => run(() => ({ authMode: 'Dev' as const, accessCodeRequired: !!options.accessCode })),

    getDevUsers: (accessCode) =>
      run(() => {
        checkAccessCode(accessCode);
        return db.users.filter((u) => u.isActive).map((u) => ({ id: u.id, name: u.name, initials: u.initials, title: u.title }));
      }),

    devLogin: (userId, accessCode) =>
      run(() => {
        checkAccessCode(accessCode);
        const u = userOf(userId);
        if (!u || !u.isActive) throw notFound('User');
        currentUserId = u.id;
        u.lastSignInAt = now();
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

  return r4.decorate(api);
}
