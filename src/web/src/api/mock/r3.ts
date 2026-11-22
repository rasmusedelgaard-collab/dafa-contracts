// Release 3 endpoints of the mock API (docs/CONTRACT_V3.md). Every read that returns contract
// data goes through the same access filter as the rest of the mock (`ctx.sees`).
import type { Api } from '../api';
import {
  DATA_QUALITY_FIELDS,
  OPTIONAL_STANDARD_FIELDS,
  REGISTER_COLUMNS,
  type AccessReason,
  type AccessRule,
  type AdminUser,
  type AppConfig,
  type AppSettings,
  type BulkResult,
  type Comment,
  type ContractComparison,
  type ContractDetail,
  type ContractListItem,
  type ContractRole,
  type ContractStatus,
  type ContractVersion,
  type Counterparty,
  type CounterpartyInput,
  type CustomFieldDef,
  type CustomFieldInput,
  type DataRoomExport,
  type DocumentHit,
  type DuplicateGroup,
  type FileDownload,
  type Id,
  type ImportKind,
  type ImportOptions,
  type ImportResult,
  type ImportRunInput,
  type LandingPage,
  type NotificationEvent,
  type Paged,
  type PersonContract,
  type RenewalItem,
  type Role,
  type SavedView,
  type SavedViewInput,
  type SyncRun,
  type SyncSource,
  type SyncSourceInput,
  type Tag,
  type UserPreferences,
} from '../types';
import { addDays } from '../../lib/dates';
import { isEmptyValue } from '../../lib/customFields';
import { containsAny, makeSnippet, matchesAll, terms } from '../../lib/search';
import { computeAnalytics } from './analytics';
import { canEdit, isAdmin, isCurrentApprover } from './access';
import { newId, type ContractRec, type Db, type DocumentRec, type NotificationRec, type UserRec, type ViewRec } from './db';
import { importFields, normName, parseCsv, planImport, readXlsx, splitTable, suggestMapping, type ImportRefs, type RowPlan } from './importer';
import { conflict, forbidden, invalid, notFound } from './problems';
import { filterRows, matchesFilter, pageRows, sortRows, toCsv, type RegisterRow } from './query';
import { EVENT_DEFS } from './seedR3';
import { makeZip } from './zip';

export interface MockCtx {
  db: Db;
  today(): string;
  now(): string;
  me(): UserRec;
  admin(): UserRec;
  sees(u: UserRec, c: ContractRec): boolean;
  visible(u: UserRec): ContractRec[];
  contractFor(id: Id, u: UserRec): ContractRec;
  editable(id: Id, u: UserRec): ContractRec;
  toListItem(c: ContractRec, u?: UserRec): ContractListItem;
  toRow(c: ContractRec, u?: UserRec): RegisterRow;
  toDetail(c: ContractRec, u: UserRec): ContractDetail;
  audit(action: string, contractId: Id | null, text: string, userId?: Id | null, at?: string, changes?: { field: string; from: string | null; to: string | null }[]): void;
  notify(toUserId: Id, subject: string, body: string, contractId: Id | null, channel?: 'Mail' | 'Teams', event?: NotificationEvent, link?: string | null): void;
  userOf(id: Id | null | undefined): UserRec | undefined;
  userName(id: Id | null | undefined): string;
  nameOf(list: { id: Id; name: string }[], id: Id | null | undefined): string;
  deadlineOf(c: ContractRec): string | null;
  daysOf(c: ContractRec): number | null;
  valueDkk(c: Pick<ContractRec, 'totalValue' | 'annualValue' | 'currency'>): number;
  toDkk(amount: number, currency: string): number;
  missingKeys(c: ContractRec): string[];
  dqLabel(key: string): string;
  documentBlob(d: DocumentRec, c: ContractRec): Blob;
  keyTerms(c: ContractRec): { field: string; label: string; value: string | null }[];
  renewalItem(c: ContractRec): RenewalItem | null;
  run<T>(fn: () => T | Promise<T>): Promise<T>;
  nextContractNo(): string;
  /** Missing-data labels of a contract. */
  listMissing(c: ContractRec): string[];
  runJobs(): { remindersCreated: number; escalations: number; obligationReminders: number; contractsExpired: number };
}

type R3Top = Pick<
  Api,
  | 'getAppConfig'
  | 'getOverviewAnalytics'
  | 'searchFull'
  | 'getPerson'
  | 'getCounterparty'
  | 'getCounterparties'
  | 'getTags'
  | 'setContractTags'
  | 'watchContract'
  | 'unwatchContract'
  | 'getWatching'
  | 'getComments'
  | 'addComment'
  | 'updateComment'
  | 'deleteComment'
  | 'getNotifications'
  | 'getUnreadCount'
  | 'markNotificationRead'
  | 'markAllNotificationsRead'
  | 'getPreferences'
  | 'updatePreferences'
  | 'getContractVersions'
  | 'compareContract'
  | 'bulk'
  | 'getViews'
  | 'createView'
  | 'updateView'
  | 'deleteView'
  | 'createDataRoomExport'
  | 'getDataRoomExports'
  | 'getDataRoomExport'
  | 'getDataRoomContent'
>;
type R3Admin = Omit<Api['admin'], keyof ReturnType<typeof _legacyAdminKeys> | R4AdminKeys>;
type R4AdminKeys = 'getTemplateCategories' | 'createTemplateCategory' | 'updateTemplateCategory' | 'deleteTemplateCategory' | 'orderTemplateCategories' | 'uploadLogo' | 'deleteLogo' | 'getDepartmentsV4' | 'updateDepartmentV4' | 'testEntityLink' | 'testDepartmentLink' | 'getAccessMapping' | 'getESignSettings' | 'updateESignSettings' | 'getStorageHealth' | 'getReconciliation' | 'runReconciliation';
// Only a type helper: the release 1–2 admin methods stay in mockApi.ts.
declare function _legacyAdminKeys(): Pick<
  Api['admin'],
  | 'getApprovalRules' | 'createApprovalRule' | 'updateApprovalRule' | 'deleteApprovalRule'
  | 'getRetentionRules' | 'createRetentionRule' | 'updateRetentionRule' | 'deleteRetentionRule'
  | 'getDepartments' | 'createDepartment' | 'updateDepartment' | 'deleteDepartment'
  | 'getContractTypes' | 'createContractType' | 'updateContractType' | 'deleteContractType'
  | 'getTemplates' | 'createTemplate' | 'getTemplateContent' | 'updateTemplate' | 'deleteTemplate'
  | 'getReminderSettings' | 'updateReminderSettings' | 'getAudit' | 'getOutbox' | 'runJobs' | 'getHolidayCover' | 'updateHolidayCover'
>;

const SYNCED_FIELDS = ['name', 'email', 'title', 'isActive'];
const LANDING: LandingPage[] = ['home', 'contracts', 'renewals', 'approvals', 'overview'];

function browserTheme(): 'light' | 'dark' | 'system' {
  try {
    const v = globalThis.localStorage?.getItem('dafa.theme');
    return v === 'dark' || v === 'system' ? v : 'light';
  } catch {
    return 'light';
  }
}

export function createR3(ctx: MockCtx): { top: R3Top; admin: R3Admin; watchersChanged: (c: ContractRec, actor: UserRec, change: string) => void } {
  const { db, run } = ctx;
  const byId = <T extends { id: Id }>(list: T[], id: Id | null | undefined): T | undefined => (id ? list.find((x) => x.id === id) : undefined);
  const isWatching = (u: UserRec, c: ContractRec) => db.watchers.some((w) => w.contractId === c.id && w.userId === u.id);
  const peopleOf = (c: ContractRec) => [c.ownerId, c.delegateId, c.responsibleId].filter((x): x is Id => !!x);
  const rolesOf = (c: ContractRec, userId: Id): ContractRole[] => [
    ...(c.ownerId === userId ? (['Owner'] as const) : []),
    ...(c.delegateId === userId ? (['Delegate'] as const) : []),
    ...(c.responsibleId === userId ? (['Responsible'] as const) : []),
  ];
  const entityName = (id: Id) => ctx.nameOf(db.entities, id);
  const isLive = (c: ContractRec) => c.status === 'Active' || c.status === 'NoticeGiven';
  const canExportDataRoom = (u: UserRec) => isAdmin(u) || u.isGroupManagement || u.managedEntityIds.length > 0;
  const activeCustomFields = () => db.customFields.filter((d) => d.isActive).sort((a, b) => a.order - b.order);

  function scopeLabel(u: UserRec): string {
    if (u.isGroupManagement || isAdmin(u)) return 'All contracts';
    const parts = [
      ...u.managedEntityIds.map(entityName),
      ...u.departmentIds.map((d) => ctx.nameOf(db.departments, d)),
    ];
    return parts.length ? parts.join(', ') : 'Your contracts';
  }

  // ----- preferences -----
  function prefsOf(userId: Id): UserPreferences {
    const stored = db.preferences[userId];
    const events = EVENT_DEFS.map((d) => {
      const s = stored?.events.find((e) => e.event === d.event);
      return { event: d.event, label: d.label, email: s?.email ?? true, teams: s?.teams ?? true, inApp: s?.inApp ?? true };
    });
    return {
      digest: stored?.digest ?? db.notificationSettings.digestDefault,
      events,
      defaultEntityId: stored?.defaultEntityId ?? null,
      landingPage: stored?.landingPage ?? null,
      autoWatch: stored?.autoWatch ?? false,
      // The demo mock keeps data in memory only; without a saved choice it reports the theme this
      // browser last used, so a reload of the demo site keeps the user's theme (the API persists it).
      theme: stored?.theme ?? browserTheme(),
    };
  }

  function landingFor(u: UserRec): LandingPage {
    const pref = db.preferences[u.id]?.landingPage;
    if (pref) return pref;
    const order: Role[] = ['Admin', 'Approver', 'ContractOwner', 'Reader'];
    const role = order.find((r) => u.roles.includes(r)) ?? 'Reader';
    return db.appSettings.landingPageByRole[role] ?? 'home';
  }

  // ----- notifications -----
  function inApp(userId: Id, event: NotificationEvent, title: string, body: string, contractId: Id | null, link: string | null, actorId: Id | null): void {
    const setting = db.notificationSettings.events.find((e) => e.event === event);
    if (setting && (!setting.enabled || !setting.channels.includes('InApp')) && event !== 'Mention') return;
    const pref = prefsOf(userId).events.find((e) => e.event === event);
    if (pref && !pref.inApp && event !== 'Mention') return;
    const rec: NotificationRec = { id: newId(), userId, event, title, body, contractId, link, actorId, createdAt: ctx.now(), readAt: null };
    db.notifications.push(rec);
  }

  function watchersChanged(c: ContractRec, actor: UserRec, change: string): void {
    for (const w of db.watchers.filter((x) => x.contractId === c.id && x.userId !== actor.id)) {
      inApp(w.userId, 'WatchedContractChanged', `${c.title} changed`, `${actor.name}: ${change}`, c.id, `/contracts/${c.id}`, actor.id);
    }
  }

  // ----- search -----
  function documentHit(d: DocumentRec, c: ContractRec, ts: string[]): DocumentHit {
    const nameHit = containsAny(d.name, ts);
    const textHit = matchesAll([d.text], ts) || containsAny(d.text, ts);
    return {
      documentId: d.id,
      contractId: c.id,
      contractNo: c.contractNo,
      contractTitle: c.title,
      ownerName: ctx.userName(c.ownerId),
      fileName: d.name,
      kind: d.kind,
      version: d.version,
      size: d.size,
      uploadedAt: d.uploadedAt,
      uploadedBy: ctx.userName(d.uploadedBy),
      matchedIn: [...(nameHit ? (['name'] as const) : []), ...(textHit ? (['text'] as const) : [])],
      snippet: textHit ? makeSnippet(d.text, ts) : null,
    };
  }

  /** Latest version per document name, plus older versions that match on their own. */
  function latestDocs(contractIds: Set<Id>): DocumentRec[] {
    const latest = new Map<string, DocumentRec>();
    for (const d of db.documents) {
      if (!contractIds.has(d.contractId)) continue;
      const key = `${d.contractId}|${d.name}`;
      const cur = latest.get(key);
      if (!cur || d.version > cur.version) latest.set(key, d);
    }
    return [...latest.values()];
  }

  function contractSearchTexts(c: ContractRec): { field: ContractMatch; text: string | null | undefined }[] {
    const obligations = db.obligations.filter((o) => o.contractId === c.id).map((o) => o.description).join(' · ');
    const custom = activeCustomFields()
      .filter((d) => d.type === 'Text' || d.type === 'Choice')
      .map((d) => c.customFields[d.key])
      .filter((v): v is string => typeof v === 'string')
      .join(' ');
    return [
      { field: 'title', text: c.title },
      { field: 'contractNo', text: c.contractNo },
      { field: 'counterparty', text: c.counterpartyName },
      { field: 'owner', text: ctx.userName(c.ownerId) },
      { field: 'delegate', text: c.delegateId ? ctx.userName(c.delegateId) : null },
      { field: 'responsible', text: c.responsibleId ? ctx.userName(c.responsibleId) : null },
      { field: 'summary', text: c.summary },
      { field: 'obligation', text: obligations },
      { field: 'tag', text: c.tagIds.map((id) => byId(db.tags, id)?.name ?? '').join(' ') },
      { field: 'customField', text: custom },
    ];
  }
  type ContractMatch = 'title' | 'contractNo' | 'counterparty' | 'owner' | 'delegate' | 'responsible' | 'summary' | 'obligation' | 'tag' | 'customField';

  // ----- versions -----
  function versionsOf(c: ContractRec): { versions: ContractVersion[]; states: Map<string, Record<string, string | null>> } {
    const current = Object.fromEntries(ctx.keyTerms(c).map((k) => [k.field, k.value]));
    const events = db.audit.filter((a) => a.contractId === c.id && a.changes && a.changes.length > 0).sort((a, b) => b.at.localeCompare(a.at));
    const states = new Map<string, Record<string, string | null>>();
    states.set('current', { ...current });
    const versions: ContractVersion[] = [{ id: 'current', at: ctx.now(), who: '', label: 'Current', changedFields: [] }];
    const state = { ...current };
    events.forEach((e, i) => {
      if (i > 0) {
        states.set(e.id, { ...state });
        versions.push({ id: e.id, at: e.at, who: ctx.userName(e.userId), label: e.text, changedFields: (e.changes ?? []).map((x) => x.field) });
      }
      for (const ch of e.changes ?? []) state[ch.field] = ch.from;
    });
    const createdAt = db.audit.find((a) => a.contractId === c.id && a.action === 'ContractCreated')?.at ?? `${c.createdAt}T09:00:00Z`;
    states.set('created', { ...state });
    versions.push({ id: 'created', at: createdAt, who: ctx.userName(c.createdBy), label: 'As created', changedFields: [] });
    return { versions, states };
  }

  // ----- views -----
  function toView(v: ViewRec, u: UserRec): SavedView {
    return { ...structuredClone(v), ownerName: ctx.userName(v.ownerId), canEdit: v.ownerId === u.id || (v.scope === 'Shared' && isAdmin(u)) };
  }
  function checkView(input: SavedViewInput, u: UserRec, selfId?: Id): void {
    const name = input.name?.trim();
    if (!name || name.length > 80) throw invalid('Enter a name of at most 80 characters.', { name: ['Required'] });
    if (input.scope !== 'Personal' && input.scope !== 'Shared') throw invalid('Choose personal or shared.', { scope: ['Required'] });
    if (input.scope === 'Shared' && !isAdmin(u) && !u.roles.includes('ContractOwner')) throw forbidden('Only contract owners and administrators can share views.');
    if (input.isDefault && input.scope !== 'Personal') throw invalid('Only a personal view can be your default.', { isDefault: ['Personal views only'] });
    const cfKeys = new Set(db.customFields.map((d) => `cf.${d.key}`));
    const bad = (input.columns ?? []).filter((c) => !(REGISTER_COLUMNS as readonly string[]).includes(c) && !cfKeys.has(c));
    if (bad.length) throw invalid(`Unknown column: ${bad.join(', ')}.`, { columns: ['Unknown column'] });
    if (db.views.some((v) => v.id !== selfId && v.ownerId === u.id && v.scope === input.scope && v.name.toLowerCase() === name.toLowerCase())) throw conflict('You already have a view with that name.', 'duplicate');
  }

  // ----- imports -----
  function importRefs(): ImportRefs {
    return {
      entities: db.entities,
      departments: db.departments,
      types: db.contractTypes,
      users: db.users,
      counterparties: db.suppliers,
      contracts: db.contracts,
      currencies: db.currencyTable.filter((c) => c.isActive).map((c) => c.code),
      tags: db.tags,
      customFields: db.customFields,
      fieldRules: db.fieldRules,
      today: ctx.today(),
    };
  }

  function counterpartyOut(r: Db['suppliers'][number], u?: UserRec): Counterparty {
    const contracts = db.contracts.filter((c) => c.supplierId === r.id && (!u || ctx.sees(u, c)));
    return { ...r, contractCount: contracts.length };
  }

  function applyCounterparty(data: Record<string, unknown>, matchId: Id | null, source: Counterparty['source']): void {
    const values = data as unknown as CounterpartyInput;
    if (matchId) {
      const rec = byId(db.suppliers, matchId);
      if (!rec) return;
      for (const [k, v] of Object.entries(values)) if (v !== null && v !== undefined && v !== '') (rec as unknown as Record<string, unknown>)[k] = v;
      rec.updatedAt = ctx.now();
      for (const c of db.contracts) if (c.supplierId === rec.id) c.counterpartyName = rec.name;
    } else {
      db.suppliers.push({ id: newId(), ...values, source, updatedAt: ctx.now() });
    }
  }

  function applyUser(data: Record<string, unknown>, matchId: Id | null): void {
    const d = data as { email: string; name: string; title: string | null; roles: Role[] | null; departmentIds: Id[] | null; managedEntityIds: Id[] | null; isGroupManagement: boolean | null; isActive: boolean | null };
    if (matchId) {
      const u = byId(db.users, matchId);
      if (!u) return;
      const entra = u.source === 'Entra';
      if (!entra) {
        u.name = d.name || u.name;
        if (d.title !== null) u.title = d.title;
        if (d.isActive !== null) u.isActive = d.isActive;
      }
      if (d.roles) u.roles = d.roles;
      if (d.departmentIds) u.departmentIds = d.departmentIds;
      if (d.managedEntityIds) u.managedEntityIds = d.managedEntityIds;
      if (d.isGroupManagement !== null) u.isGroupManagement = d.isGroupManagement;
      return;
    }
    const parts = d.name.split(' ');
    db.users.push({
      id: newId(),
      name: d.name,
      email: d.email,
      initials: `${parts[0]?.[0] ?? ''}${parts[parts.length - 1]?.[0] ?? ''}`.toUpperCase(),
      title: d.title ?? '',
      roles: d.roles ?? ['Reader'],
      departmentIds: d.departmentIds ?? [],
      managedEntityIds: d.managedEntityIds ?? [],
      isGroupManagement: d.isGroupManagement ?? false,
      isActive: d.isActive ?? true,
      source: 'Import',
      externalId: null,
      lastSignInAt: null,
    });
  }

  function applyContract(data: Record<string, unknown>, matchId: Id | null, options: ImportOptions, fileName: string, actor: UserRec): void {
    const d = data as Record<string, unknown> & { createCounterparty: boolean; counterpartyName: string; supplierId: Id | null };
    let supplierId = d.supplierId;
    if (!supplierId && d.createCounterparty) {
      const existing = db.suppliers.find((s) => s.name.toLowerCase() === d.counterpartyName.toLowerCase());
      if (existing) supplierId = existing.id;
      else {
        supplierId = newId();
        db.suppliers.push({ id: supplierId, name: d.counterpartyName, country: null, vatNo: null, kind: 'Supplier', externalId: null, city: null, email: null, phone: null, isActive: true, source: 'Import', updatedAt: ctx.now() });
      }
    }
    const fields = {
      title: d.title as string,
      typeId: d.typeId as Id,
      supplierId,
      counterpartyName: d.counterpartyName,
      entityId: d.entityId as Id,
      departmentId: d.departmentId as Id,
      ownerId: d.ownerId as Id,
      delegateId: (d.delegateId as Id | null) ?? null,
      responsibleId: (d.responsibleId as Id | null) ?? null,
      reminderRecipient: d.reminderRecipient as ContractRec['reminderRecipient'],
      startDate: d.startDate as string,
      endDate: (d.endDate as string | null) ?? null,
      noticePeriodDays: (d.noticePeriodDays as number | null) ?? null,
      autoRenew: !!d.autoRenew,
      renewalMonths: (d.renewalMonths as number | null) ?? null,
      annualValue: (d.annualValue as number | null) ?? null,
      totalValue: (d.totalValue as number | null) ?? null,
      currency: d.currency as string,
      paymentTerms: (d.paymentTerms as string | null) ?? null,
      priceAdjustment: null,
      priceAdjustmentDate: null,
      governingLaw: (d.governingLaw as string | null) ?? null,
      liabilityCap: (d.liabilityCap as string | null) ?? null,
      exclusivity: (d.exclusivity as string | null) ?? null,
      changeOfControl: (d.changeOfControl as boolean | null) ?? null,
      summary: (d.summary as string | null) ?? null,
      tagIds: (d.tagIds as Id[]) ?? [],
      customFields: (d.customFields as ContractRec['customFields']) ?? {},
    };
    if (matchId) {
      const c = byId(db.contracts, matchId);
      if (!c) return;
      for (const [k, v] of Object.entries(fields)) {
        if (v === null || v === undefined || v === '' || (Array.isArray(v) && v.length === 0)) continue;
        if (k === 'customFields') c.customFields = { ...c.customFields, ...(v as ContractRec['customFields']) };
        else (c as unknown as Record<string, unknown>)[k] = v;
      }
      if (d.signedDate) c.signedDate = d.signedDate as string;
      ctx.audit('import.contract-updated', c.id, `Contract updated from ${fileName}`, actor.id);
      return;
    }
    const t = ctx.today();
    const active = options.contractStatus === 'Active';
    const status: ContractStatus = !active ? 'Draft' : fields.endDate && fields.endDate < t ? 'Expired' : 'Active';
    const c: ContractRec = {
      id: newId(),
      contractNo: (d.contractNo as string | null) || ctx.nextContractNo(),
      ...fields,
      status,
      signedDate: active ? ((d.signedDate as string | null) ?? null) : null,
      signingMethod: active && d.signedDate ? 'Signed by both parties' : null,
      renewalDecision: null,
      renewalDecidedBy: null,
      renewalDecidedAt: null,
      renewalComment: null,
      createdAt: t,
      createdBy: actor.id,
      approvedAt: active ? t : null,
      deviations: [],
    };
    db.contracts.push(c);
    ctx.audit('import.contract-created', c.id, `Contract imported from ${fileName}`, actor.id);
  }

  function runPlans(kind: ImportKind, plans: RowPlan[], options: ImportOptions, fileName: string, actor: UserRec, source: ImportResult['source']): ImportResult {
    const result: ImportResult = { id: newId(), kind, fileName, ranAt: ctx.now(), ranBy: actor.name, options, created: 0, updated: 0, skipped: 0, failed: 0, errors: [], source };
    for (const p of plans) {
      const a = p.preview.action;
      if (a === 'Error') {
        result.failed += 1;
        result.errors.push({ rowNo: p.preview.rowNo, message: p.preview.errors.map((e) => e.message).join('; ') });
        continue;
      }
      if (a === 'Skip') {
        result.skipped += 1;
        continue;
      }
      if (kind === 'counterparties') applyCounterparty(p.data, a === 'Update' ? p.matchId : null, source === 'Sync' ? 'PowerBi' : 'Import');
      else if (kind === 'users') applyUser(p.data, a === 'Update' ? p.matchId : null);
      else applyContract(p.data, a === 'Update' ? p.matchId : null, options, fileName, actor);
      if (a === 'Create') result.created += 1;
      else result.updated += 1;
    }
    db.importHistory.unshift(result);
    ctx.audit('import.run', null, `Imported ${kind} from ${fileName}: ${result.created} created, ${result.updated} updated, ${result.skipped} skipped, ${result.failed} failed`, actor.id);
    inApp(actor.id, 'ImportFinished', `Import of ${fileName} finished`, `${result.created} created, ${result.updated} updated, ${result.failed} failed.`, null, '/settings/imports', actor.id);
    return result;
  }

  function checkImportOptions(input: ImportRunInput, columns: string[]): void {
    if (!input?.options || !['CreateOnly', 'CreateAndUpdate', 'SkipDuplicates'].includes(input.options.mode)) throw invalid('Choose what to do with duplicates.', { 'options.mode': ['Required'] });
    for (const [key, col] of Object.entries(input.mapping ?? {})) {
      if (col && !columns.includes(col)) throw invalid(`The column "${col}" is not in the file.`, { [`mapping.${key}`]: ['Unknown column'] });
    }
  }

  function session(id: Id) {
    const s = byId(db.importSessions, id);
    if (!s || s.expiresAt < ctx.now()) throw conflict('This import has expired. Upload the file again.', 'import-state');
    return s;
  }

  // ----- sync -----
  /** Rows a sync source "returns" in the mock: the counterparty master plus one new vendor, or the users. */
  function simulatedRows(src: SyncSourceInput): { columns: string[]; rows: string[][] } {
    if (src.target === 'users') {
      return { columns: ['Mail', 'DisplayName', 'JobTitle', 'Department'], rows: db.users.map((u) => [u.email, u.name, u.title, ctx.nameOf(db.departments, u.departmentIds[0])]) };
    }
    if (src.target === 'contracts') {
      return { columns: ['ContractNo', 'Title', 'Vendor', 'Type', 'Company', 'Department', 'Owner', 'Start', 'End', 'NoticeDays', 'AnnualValue', 'Currency'], rows: [] };
    }
    const rows = db.suppliers.filter((s) => s.source !== 'Import').map((s) => [s.externalId ?? '', s.name, s.vatNo ?? '', s.country ?? '']);
    rows.push(['V10999', 'Vejle Gummivarer ApS', 'DK40918273', 'DK']);
    return { columns: ['VendorNo', 'Name', 'VAT', 'Country'], rows };
  }

  function checkSyncInput(input: SyncSourceInput): void {
    const errors: Record<string, string[]> = {};
    if (!input.name?.trim()) errors.name = ['Required'];
    if (!['counterparties', 'users', 'contracts'].includes(input.target)) errors.target = ['Required'];
    if (input.kind === 'PowerBi') {
      const p = input.powerBi;
      if (!p?.workspaceId?.trim()) errors['powerBi.workspaceId'] = ['Required'];
      if (!p?.datasetId?.trim()) errors['powerBi.datasetId'] = ['Required'];
      if (!!p?.daxQuery?.trim() === !!p?.table?.trim()) errors['powerBi.daxQuery'] = ['Give either a DAX query or a table name'];
    } else if (input.kind === 'Sql') {
      const q = input.sql;
      if (!q?.connectionName || !db.syncConnections.some((c) => c.name === q.connectionName && c.kind === 'Sql')) errors['sql.connectionName'] = ['Choose a configured connection'];
      if (!/^\s*(select|with)\b/i.test(q?.query ?? '')) errors['sql.query'] = ['The query must start with SELECT or WITH'];
    } else errors.kind = ['Required'];
    const s = input.schedule;
    if (!s || !['Off', 'Hourly', 'Daily', 'Weekly'].includes(s.frequency)) errors['schedule.frequency'] = ['Required'];
    else if ((s.frequency === 'Daily' || s.frequency === 'Weekly') && !/^([01]\d|2[0-3]):[0-5]\d$/.test(s.time ?? '')) errors['schedule.time'] = ['Use HH:mm'];
    else if (s.frequency === 'Weekly' && !(s.dayOfWeek && s.dayOfWeek >= 1 && s.dayOfWeek <= 7)) errors['schedule.dayOfWeek'] = ['Choose a day'];
    if (Object.keys(errors).length) throw invalid('Check the sync source.', errors);
  }

  function nextRun(schedule: SyncSource['schedule'], active = true): string | null {
    if (!active || schedule.frequency === 'Off') return null;
    const t = ctx.today();
    if (schedule.frequency === 'Hourly') return ctx.now();
    if (schedule.frequency === 'Daily') return `${addDays(t, 1)}T${schedule.time}:00`;
    return `${addDays(t, 7)}T${schedule.time}:00`;
  }

  function toSource(s: Db['syncSources'][number]): SyncSource {
    const { runs, ...rest } = s;
    return { ...structuredClone(rest), lastRun: runs[0] ? structuredClone(runs[0]) : null, nextRunAt: nextRun(s.schedule, s.isActive) };
  }

  // ----- admin users -----
  function toAdminUser(u: UserRec): AdminUser {
    return {
      id: u.id,
      name: u.name,
      email: u.email,
      initials: u.initials,
      title: u.title || null,
      roles: [...u.roles],
      departmentIds: [...u.departmentIds],
      managedEntityIds: [...u.managedEntityIds],
      isGroupManagement: u.isGroupManagement,
      isActive: u.isActive,
      source: u.source,
      syncedFields: u.source === 'Entra' ? [...SYNCED_FIELDS] : [],
      externalId: u.externalId,
      lastSignInAt: u.lastSignInAt,
      contractCount: db.contracts.filter((c) => peopleOf(c).includes(u.id)).length,
    };
  }

  function reasonsFor(u: UserRec, c: ContractRec): AccessReason[] {
    const out: AccessReason[] = [];
    if (u.departmentIds.includes(c.departmentId)) out.push({ rule: 'Department', label: `Member of ${ctx.nameOf(db.departments, c.departmentId)}` });
    if (u.managedEntityIds.includes(c.entityId)) out.push({ rule: 'EntityManagement', label: `Manages ${entityName(c.entityId)}` });
    if (u.isGroupManagement) out.push({ rule: 'GroupManagement', label: 'Group management' });
    for (const r of rolesOf(c, u.id)) out.push({ rule: 'OnContract', label: r });
    if (isAdmin(u)) out.push({ rule: 'Admin', label: 'Administrator' });
    if (isCurrentApprover(u, c, db.approvals)) out.push({ rule: 'CurrentApprover', label: 'Current approver' });
    return out;
  }

  function reassignPlan(input: Parameters<Api['admin']['reassign']>[0]) {
    const from = ctx.userOf(input.fromUserId);
    const to = ctx.userOf(input.toUserId);
    if (!from) throw invalid('Choose the person to move contracts from.', { fromUserId: ['Required'] });
    if (!to) throw invalid('Choose the person to move contracts to.', { toUserId: ['Required'] });
    if (from.id === to.id) throw invalid('Choose two different people.', { toUserId: ['Same person'] });
    if (!to.isActive) throw invalid(`${to.name} is not active.`, { toUserId: ['Inactive'] });
    if (!input.roles?.length) throw invalid('Choose at least one role.', { roles: ['Required'] });
    const rows = db.contracts
      .filter((c) => (!input.contractIds || input.contractIds.includes(c.id)) && rolesOf(c, from.id).some((r) => input.roles.includes(r)))
      .map((c) => {
        const roles = rolesOf(c, from.id).filter((r) => input.roles.includes(r));
        let skipReason: string | null = null;
        if (c.status === 'InApproval') skipReason = 'In approval';
        else if ((c.status === 'Expired' || c.status === 'Terminated') && !input.includeEnded) skipReason = 'Ended';
        else if (roles.includes('Owner') && !to.roles.includes('ContractOwner') && !isAdmin(to)) skipReason = `${to.name} does not have the ContractOwner role`;
        return { c, roles, skipReason };
      });
    const obligations = input.includeObligations ? db.obligations.filter((o) => o.status === 'Open' && o.responsibleId === from.id && rows.some((r) => !r.skipReason && r.c.id === o.contractId)) : [];
    return { from, to, rows, obligations };
  }

  // ----- custom fields -----
  function checkCustomField(input: CustomFieldInput, selfId?: Id): void {
    const errors: Record<string, string[]> = {};
    if (!/^[a-z][a-zA-Z0-9]{1,39}$/.test(input.key ?? '')) errors.key = ['Start with a lower-case letter; letters and digits only, 2–40 characters'];
    else if (db.customFields.some((d) => d.id !== selfId && d.key === input.key)) errors.key = ['Already used by another field'];
    if (!input.label?.trim() || input.label.trim().length > 80) errors.label = ['Required, at most 80 characters'];
    if (!['Text', 'Number', 'Money', 'Date', 'YesNo', 'Choice', 'MultiChoice', 'Person'].includes(input.type)) errors.type = ['Required'];
    const choice = input.type === 'Choice' || input.type === 'MultiChoice';
    const opts = (input.options ?? []).map((o) => o.trim());
    if (choice && (opts.length === 0 || opts.some((o) => !o || o.length > 80) || new Set(opts.map((o) => o.toLowerCase())).size !== opts.length)) errors.options = ['Add at least one option; options must be unique'];
    if (!choice && opts.length > 0) errors.options = ['Only choice fields have options'];
    for (const t of input.contractTypeIds ?? []) if (!byId(db.contractTypes, t)) errors.contractTypeIds = ['Unknown contract type'];
    if (Object.keys(errors).length) throw invalid('Check the field.', errors);
  }
  const valuesOf = (key: string) => db.contracts.map((c) => c.customFields[key]).filter((v) => !isEmptyValue(v));

  // ----- data room -----
  function buildDataRoom(rec: Db['dataRoom'][number], u: UserRec): void {
    const rows = sortRows(filterRows(ctx.visible(u).map((c) => ctx.toRow(c, u)), rec.input.filter, u.id, { includeEndedDefault: true, today: ctx.today(), customFields: db.customFields }), 'contractNo');
    const enc = new TextEncoder();
    const entries: { path: string; data: Uint8Array }[] = [];
    const manifestDocs: { path: string; contractNo: string; kind: string; version: number; size: number }[] = [];
    const cfDefs = activeCustomFields();
    const docsFor = (c: ContractRec) => {
      let docs = db.documents.filter((d) => d.contractId === c.id);
      if (rec.input.documents === 'None') return [];
      if (rec.input.documents === 'MainOnly') docs = docs.filter((d) => d.kind === 'Main');
      if (rec.input.versions === 'Latest') {
        const latest = new Map<string, DocumentRec>();
        for (const d of docs) if ((latest.get(d.name)?.version ?? 0) < d.version) latest.set(d.name, d);
        docs = [...latest.values()];
      }
      return docs;
    };
    const indexItems: ContractListItem[] = [];
    const docNames: string[] = [];
    for (const r of rows) {
      const c = byId(db.contracts, r.item.id);
      if (!c) continue;
      indexItems.push(r.item);
      const folder = `documents/${c.contractNo} – ${c.title.replace(/[\\/:*?"<>|]/g, '')}`;
      const names: string[] = [];
      for (const d of docsFor(c)) {
        const file = rec.input.versions === 'All' ? `v${d.version} – ${d.name}` : d.name;
        const path = `${folder}/${file}`;
        entries.push({ path, data: new Uint8Array(0) });
        manifestDocs.push({ path, contractNo: c.contractNo, kind: d.kind, version: d.version, size: d.size });
        names.push(file);
      }
      docNames.push(names.join('; '));
    }
    const cols = [...REGISTER_COLUMNS.filter((x) => x !== 'notice'), 'notice', ...cfDefs.map((d) => `cf.${d.key}`)];
    const csv = toCsv(indexItems, cols, db.customFields).split('\r\n');
    const withDocs = csv.map((line, i) => `${line},${i === 0 ? 'Documents' : `"${(docNames[i - 1] ?? '').replace(/"/g, '""')}"`}`).join('\r\n');
    rec.contractCount = indexItems.length;
    rec.documentCount = manifestDocs.length;
    // Real bytes for the documents (the seeded ones are generated placeholder PDFs).
    const pending = entries.map(async (e, i) => {
      const m = manifestDocs[i];
      const c = db.contracts.find((x) => x.contractNo === m?.contractNo);
      const d = c ? db.documents.find((x) => x.contractId === c.id && e.path.endsWith(x.name) && x.version === m?.version) : undefined;
      if (d && c) e.data = new Uint8Array(await ctx.documentBlob(d, c).arrayBuffer());
    });
    return void Promise.all(pending).then(() => {
      const manifest = { name: rec.name, createdAt: rec.createdAt, createdBy: u.name, filter: rec.input.filter, contracts: indexItems.length, documents: manifestDocs.map((m, i) => ({ ...m, size: entries[i]?.data.length ?? m.size })) };
      const all = [{ path: 'index.csv', data: enc.encode(String.fromCharCode(0xfeff) + withDocs) }, { path: 'manifest.json', data: enc.encode(JSON.stringify(manifest, null, 2)) }, ...entries];
      rec.blob = makeZip(all);
      rec.sizeBytes = rec.blob.size;
      rec.status = 'Ready';
    });
  }
  const toExport = (r: Db['dataRoom'][number]): DataRoomExport => ({ id: r.id, name: r.name, status: r.status, contractCount: r.contractCount, documentCount: r.documentCount, sizeBytes: r.sizeBytes, createdAt: r.createdAt, createdBy: ctx.userName(r.createdById), fileName: r.fileName, error: r.error, expiresAt: r.expiresAt });

  // ===================================================================================
  const top: R3Top = {
    getAppConfig: () =>
      run((): AppConfig => {
        const u = ctx.me();
        const s = db.appSettings;
        return {
          appName: s.appName,
          dateFormat: s.dateFormat,
          numberFormat: s.numberFormat,
          features: { ...s.features },
          sessionTimeoutMinutes: s.sessionTimeoutMinutes,
          landingPage: landingFor(u),
          defaultEntityId: db.preferences[u.id]?.defaultEntityId ?? null,
          dataQualityFields: [...s.dataQualityFields],
          ddThresholdDkk: s.ddThresholdDkk,
          customFields: structuredClone(activeCustomFields()),
          fieldRules: structuredClone(db.fieldRules),
          tags: db.tags.map((t) => ({ id: t.id, name: t.name, color: t.color })).sort((a, b) => a.name.localeCompare(b.name)),
          rates: Object.fromEntries(db.currencyTable.filter((c) => c.isActive).map((c) => [c.code, c.rateToDkk])),
          canExportDataRoom: canExportDataRoom(u),
        };
      }),

    getOverviewAnalytics: (filter, threshold) =>
      run(() => {
        const u = ctx.me();
        const visible = ctx.visible(u);
        const rows = visible.map((c) => ctx.toRow(c, u)).filter((r) => matchesFilter(r, filter, { userId: u.id, today: ctx.today(), includeEndedDefault: false, customFields: db.customFields }));
        return computeAnalytics({
          rows,
          today: ctx.today(),
          visibleContracts: visible.length,
          scopeLabel: scopeLabel(u),
          thresholdDkk: threshold !== undefined && threshold !== null && Number(threshold) >= 0 ? Number(threshold) : db.appSettings.ddThresholdDkk,
          asOf: ctx.now(),
        });
      }),

    searchFull: (query) =>
      run(() => {
        const u = ctx.me();
        const ts = terms(query.q ?? '');
        const empty = { total: 0, items: [] };
        const q = (query.q ?? '').trim();
        if (q.length < 2) return { q, contracts: empty, documents: empty, people: empty, counterparties: empty };
        const size = Math.min(Math.max(1, query.pageSize ?? 10), 50);
        const page = (group: string) => (query.group === group ? Math.max(1, query.page ?? 1) : 1);
        const slice = <T,>(group: string, items: T[]) => {
          if (query.group && query.group !== group) return { total: items.length, items: [] };
          const p = page(group);
          return { total: items.length, items: items.slice((p - 1) * size, p * size) };
        };
        const visible = ctx.visible(u).filter(
          (c) =>
            (!query.entityId || [query.entityId].flat().includes(c.entityId)) &&
            (!query.departmentId || c.departmentId === query.departmentId) &&
            (!query.typeId || c.typeId === query.typeId),
        );

        const rank: Record<string, number> = { title: 0, contractNo: 1 };
        const contracts = visible
          .filter((c) => !query.personId || peopleOf(c).includes(query.personId))
          .map((c) => {
            const texts = contractSearchTexts(c);
            if (!matchesAll(texts.map((t) => t.text), ts)) return null;
            const matchedIn = texts.filter((t) => containsAny(t.text, ts)).map((t) => t.field);
            const deep = texts.find((t) => (t.field === 'summary' || t.field === 'obligation') && containsAny(t.text, ts));
            return { item: ctx.toListItem(c, u), matchedIn, snippet: deep?.text ? makeSnippet(deep.text, ts) : null };
          })
          .filter((x): x is NonNullable<typeof x> => !!x)
          .sort((a, b) => Math.min(...a.matchedIn.map((m) => rank[m] ?? 2)) - Math.min(...b.matchedIn.map((m) => rank[m] ?? 2)) || a.item.title.localeCompare(b.item.title));

        const visibleById = new Map(visible.map((c) => [c.id, c]));
        const documents = latestDocs(new Set(visibleById.keys()))
          .filter((d) => {
            const c = visibleById.get(d.contractId);
            if (!c) return false;
            if (query.personId && d.uploadedBy !== query.personId && !peopleOf(c).includes(query.personId)) return false;
            if (query.documentKind && d.kind !== query.documentKind) return false;
            const day = d.uploadedAt.slice(0, 10);
            if (query.uploadedFrom && day < query.uploadedFrom) return false;
            if (query.uploadedTo && day > query.uploadedTo) return false;
            return matchesAll([d.name, d.text], ts);
          })
          .map((d) => documentHit(d, visibleById.get(d.contractId) as ContractRec, ts))
          .sort((a, b) => Number(b.matchedIn.includes('name')) - Number(a.matchedIn.includes('name')) || b.uploadedAt.localeCompare(a.uploadedAt));

        const all = ctx.visible(u);
        const people = db.users
          .filter((p) => p.isActive && matchesAll([p.name, p.email, p.title], ts))
          .map((p) => ({ id: p.id, name: p.name, initials: p.initials, title: p.title || null, departmentNames: p.departmentIds.map((d) => ctx.nameOf(db.departments, d)), contractCount: all.filter((c) => peopleOf(c).includes(p.id)).length }))
          .sort((a, b) => a.name.localeCompare(b.name));

        const counterparties = db.suppliers
          .filter((s) => s.isActive && matchesAll([s.name, s.vatNo, s.externalId], ts))
          .map((s) => ({ id: s.id, name: s.name, country: s.country, contractCount: all.filter((c) => c.supplierId === s.id).length }))
          .sort((a, b) => b.contractCount - a.contractCount || a.name.localeCompare(b.name));

        return { q, contracts: slice('contracts', contracts), documents: slice('documents', documents), people: slice('people', people), counterparties: slice('counterparties', counterparties) };
      }),

    getPerson: (userId) =>
      run(() => {
        const u = ctx.me();
        const p = ctx.userOf(userId);
        if (!p) throw notFound('Person');
        const contracts: PersonContract[] = sortRows(
          ctx.visible(u).filter((c) => peopleOf(c).includes(p.id)).map((c) => ctx.toRow(c, u)),
          'notice',
        ).map((r) => {
          const c = byId(db.contracts, r.item.id) as ContractRec;
          return { ...r.item, roles: rolesOf(c, p.id) };
        });
        const count = (role: ContractRole) => contracts.filter((c) => c.roles.includes(role)).length;
        return {
          person: { id: p.id, name: p.name, initials: p.initials, email: p.email, title: p.title || null, departmentNames: p.departmentIds.map((d) => ctx.nameOf(db.departments, d)), isActive: p.isActive },
          counts: { owner: count('Owner'), delegate: count('Delegate'), responsible: count('Responsible'), total: contracts.length, active: contracts.filter((c) => c.status === 'Active').length },
          annualValueDkk: Math.round(contracts.filter((c) => c.status === 'Active' || c.status === 'NoticeGiven').reduce((s, c) => s + (c.annualValueDkk ?? 0), 0)),
          contracts,
        };
      }),

    getCounterparty: (id) =>
      run(() => {
        const u = ctx.me();
        const rec = byId(db.suppliers, id);
        if (!rec) throw notFound('Counterparty');
        const mine = ctx.visible(u).filter((c) => c.supplierId === id);
        const rows = sortRows(mine.map((c) => ctx.toRow(c, u)), 'notice');
        const live = mine.filter(isLive);
        const renewals = mine
          .map((c) => ctx.renewalItem(c))
          .filter((r): r is RenewalItem => !!r)
          .sort((a, b) => a.noticeDeadline.localeCompare(b.noticeDeadline));
        const next = renewals.filter((r) => r.daysToNotice >= 0)[0]?.noticeDeadline ?? null;
        const ids = new Set(mine.map((c) => c.id));
        const documents = latestDocs(ids)
          .sort((a, b) => b.uploadedAt.localeCompare(a.uploadedAt))
          .slice(0, 200)
          .map((d) => documentHit(d, byId(db.contracts, d.contractId) as ContractRec, []));
        return {
          counterparty: counterpartyOut(rec, u),
          summary: {
            contracts: mine.length,
            live: live.length,
            annualValueDkk: Math.round(live.reduce((s, c) => s + ctx.toDkk(c.annualValue ?? 0, c.currency), 0)),
            totalValueDkk: Math.round(mine.reduce((s, c) => s + ctx.toDkk(c.totalValue ?? 0, c.currency), 0)),
            autoRenewing: live.filter((c) => c.autoRenew).length,
            nextNoticeDeadline: next,
          },
          contracts: rows.map((r) => r.item),
          renewals,
          documents,
        };
      }),

    getCounterparties: (q) =>
      run(() => {
        const u = ctx.me();
        const needle = q?.trim().toLowerCase() ?? '';
        return db.suppliers
          .filter((s) => s.isActive && (!needle || [s.name, s.vatNo, s.externalId].some((v) => v?.toLowerCase().includes(needle))))
          .slice(0, 20)
          .map((s) => counterpartyOut(s, u));
      }),

    getTags: () => run(() => (ctx.me(), db.tags.map((t) => ({ id: t.id, name: t.name, color: t.color })).sort((a, b) => a.name.localeCompare(b.name)))),

    setContractTags: (contractId, tagIds) =>
      run(() => {
        const u = ctx.me();
        const c = ctx.editable(contractId, u);
        const unknown = (tagIds ?? []).filter((id) => !byId(db.tags, id));
        if (unknown.length) throw invalid('Unknown tag.', { tagIds: ['Unknown tag'] });
        c.tagIds = [...new Set(tagIds)];
        ctx.audit('contract.tags-changed', c.id, `Tags: ${c.tagIds.map((id) => byId(db.tags, id)?.name).join(', ') || 'none'}`, u.id);
        return ctx.toDetail(c, u);
      }),

    watchContract: (contractId) =>
      run(() => {
        const u = ctx.me();
        const c = ctx.contractFor(contractId, u);
        if (!isWatching(u, c)) db.watchers.push({ contractId: c.id, userId: u.id });
      }),
    unwatchContract: (contractId) =>
      run(() => {
        const u = ctx.me();
        const c = ctx.contractFor(contractId, u);
        db.watchers = db.watchers.filter((w) => !(w.contractId === c.id && w.userId === u.id));
      }),
    getWatching: () =>
      run(() => {
        const u = ctx.me();
        const rows = db.watchers.filter((w) => w.userId === u.id).map((w) => byId(db.contracts, w.contractId)).filter((c): c is ContractRec => !!c && ctx.sees(u, c));
        return sortRows(rows.map((c) => ctx.toRow(c, u)), 'notice').map((r) => r.item);
      }),

    getComments: (contractId) =>
      run(() => {
        const u = ctx.me();
        const c = ctx.contractFor(contractId, u);
        return db.comments.filter((x) => x.contractId === c.id).sort((a, b) => a.createdAt.localeCompare(b.createdAt)).map((x) => toComment(x, u));
      }),
    addComment: (contractId, input) =>
      run(() => {
        const u = ctx.me();
        const c = ctx.contractFor(contractId, u);
        const { body, mentions } = checkComment(input, c);
        const rec = { id: newId(), contractId: c.id, authorId: u.id, body, mentionIds: mentions.map((m) => m.id), createdAt: ctx.now(), editedAt: null };
        db.comments.push(rec);
        ctx.audit('comment.added', c.id, 'Comment added', u.id);
        for (const m of mentions) {
          if (m.id === u.id) continue;
          inApp(m.id, 'Mention', `${u.name} mentioned you on ${c.title}`, body.length > 160 ? `${body.slice(0, 157)}…` : body, c.id, `/contracts/${c.id}#comments`, u.id);
          ctx.notify(m.id, `${u.name} mentioned you on ${c.title}`, body, c.id, 'Mail');
        }
        watchersChanged(c, u, 'added a comment');
        return toComment(rec, u);
      }),
    updateComment: (id, input) =>
      run(() => {
        const u = ctx.me();
        const rec = byId(db.comments, id);
        if (!rec) throw notFound('Comment');
        const c = ctx.contractFor(rec.contractId, u);
        if (rec.authorId !== u.id) throw forbidden('Only the author can edit a comment.');
        const { body, mentions } = checkComment(input, c);
        const before = new Set(rec.mentionIds);
        rec.body = body;
        rec.mentionIds = mentions.map((m) => m.id);
        rec.editedAt = ctx.now();
        for (const m of mentions) if (!before.has(m.id) && m.id !== u.id) inApp(m.id, 'Mention', `${u.name} mentioned you on ${c.title}`, body, c.id, `/contracts/${c.id}#comments`, u.id);
        ctx.audit('comment.edited', c.id, 'Comment edited', u.id);
        return toComment(rec, u);
      }),
    deleteComment: (id) =>
      run(() => {
        const u = ctx.me();
        const rec = byId(db.comments, id);
        if (!rec) throw notFound('Comment');
        const c = ctx.contractFor(rec.contractId, u);
        if (rec.authorId !== u.id && !isAdmin(u)) throw forbidden('Only the author or an administrator can delete a comment.');
        db.comments = db.comments.filter((x) => x.id !== id);
        ctx.audit('comment.deleted', c.id, 'Comment deleted', u.id);
      }),

    getNotifications: (query = {}) =>
      run(() => {
        const u = ctx.me();
        const mine = db.notifications
          .filter((n) => n.userId === u.id)
          .filter((n) => {
            const c = byId(db.contracts, n.contractId);
            return !n.contractId || (c && ctx.sees(u, c));
          })
          .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
        const list = query.unreadOnly ? mine.filter((n) => !n.readAt) : mine;
        const paged = pageRows(list, query.page ?? 1, query.pageSize ?? 20);
        return {
          ...paged,
          items: paged.items.map((n) => ({ id: n.id, event: n.event, title: n.title, body: n.body, contractId: n.contractId, link: n.link, actorName: n.actorId ? ctx.userName(n.actorId) : null, createdAt: n.createdAt, readAt: n.readAt })),
          unread: mine.filter((n) => !n.readAt).length,
        };
      }),
    getUnreadCount: () =>
      run(() => {
        const u = ctx.me();
        return { count: db.notifications.filter((n) => n.userId === u.id && !n.readAt && (!n.contractId || ctx.sees(u, byId(db.contracts, n.contractId) as ContractRec))).length };
      }),
    markNotificationRead: (id) =>
      run(() => {
        const u = ctx.me();
        const n = db.notifications.find((x) => x.id === id && x.userId === u.id);
        if (!n) throw notFound('Notification');
        n.readAt ??= ctx.now();
      }),
    markAllNotificationsRead: () =>
      run(() => {
        const u = ctx.me();
        for (const n of db.notifications) if (n.userId === u.id && !n.readAt) n.readAt = ctx.now();
      }),

    getPreferences: () => run(() => prefsOf(ctx.me().id)),
    updatePreferences: (input) =>
      run(() => {
        const u = ctx.me();
        if (!['Off', 'Daily', 'Weekly'].includes(input.digest)) throw invalid('Choose a digest frequency.', { digest: ['Required'] });
        if (input.defaultEntityId && !db.entities.some((e) => e.id === input.defaultEntityId && e.isActive)) throw invalid('Choose an active legal entity.', { defaultEntityId: ['Unknown entity'] });
        if (input.landingPage && !LANDING.includes(input.landingPage)) throw invalid('Choose a landing page.', { landingPage: ['Unknown page'] });
        if (input.theme !== undefined && !['light', 'dark', 'system'].includes(input.theme)) throw invalid('Choose light, dark or system.', { theme: ['Unknown theme'] });
        const settings = db.notificationSettings.events;
        db.preferences[u.id] = {
          digest: input.digest,
          defaultEntityId: input.defaultEntityId ?? null,
          landingPage: input.landingPage ?? null,
          autoWatch: !!input.autoWatch,
          theme: input.theme ?? db.preferences[u.id]?.theme ?? 'light',
          events: EVENT_DEFS.map((d) => {
            const e = input.events?.find((x) => x.event === d.event);
            const s = settings.find((x) => x.event === d.event);
            return { event: d.event, label: d.label, email: !!e?.email && !!s?.channels.includes('Email'), teams: !!e?.teams && !!s?.channels.includes('Teams'), inApp: (e?.inApp ?? true) && !!s?.channels.includes('InApp') };
          }),
        };
        return prefsOf(u.id);
      }),

    getContractVersions: (contractId) =>
      run(() => {
        const u = ctx.me();
        return versionsOf(ctx.contractFor(contractId, u)).versions;
      }),
    compareContract: (contractId, fromId, toId) =>
      run((): ContractComparison => {
        const u = ctx.me();
        const c = ctx.contractFor(contractId, u);
        const { versions, states } = versionsOf(c);
        const from = versions.find((v) => v.id === fromId);
        const to = versions.find((v) => v.id === toId);
        const a = states.get(fromId);
        const b = states.get(toId);
        if (!from || !to || !a || !b) throw notFound('Version');
        return {
          from,
          to,
          fields: ctx.keyTerms(c).map((k) => ({ field: k.field, label: k.label, from: a[k.field] ?? null, to: b[k.field] ?? null, changed: (a[k.field] ?? null) !== (b[k.field] ?? null) })),
        };
      }),

    bulk: (input) =>
      run((): BulkResult => {
        const u = ctx.me();
        const ids = input?.contractIds ?? [];
        if (ids.length === 0 || ids.length > 500) throw invalid('Select between 1 and 500 contracts.', { contractIds: ['1 to 500'] });
        const result: BulkResult = { action: input.action, requested: ids.length, succeeded: 0, failed: [] };
        const tag = input.tagId ? byId(db.tags, input.tagId) : undefined;
        const to = ctx.userOf(input.toUserId);
        if ((input.action === 'AddTag' || input.action === 'RemoveTag') && !tag) throw invalid('Choose a tag.', { tagId: ['Required'] });
        if (input.action === 'Reassign') {
          if (!input.role || !['Owner', 'Delegate', 'Responsible'].includes(input.role)) throw invalid('Choose the role to reassign.', { role: ['Required'] });
          if (!to || !to.isActive) throw invalid('Choose an active person.', { toUserId: ['Required'] });
          if (input.role === 'Owner' && !to.roles.includes('ContractOwner') && !isAdmin(to)) throw invalid(`${to.name} does not have the ContractOwner role.`, { toUserId: ['Not a contract owner'] });
        }
        if (!['AddTag', 'RemoveTag', 'Reassign', 'RequestDataCompletion'].includes(input.action)) throw invalid('Choose an action.', { action: ['Required'] });
        for (const id of ids) {
          const c = byId(db.contracts, id);
          const fail = (reason: string) => result.failed.push({ contractId: id, title: c && ctx.sees(u, c) ? c.title : 'Contract', reason });
          if (!c || !ctx.sees(u, c)) {
            fail('Not found or no access');
            continue;
          }
          if (input.action === 'RequestDataCompletion') {
            const missing = ctx.listMissing(c);
            if (missing.length === 0) {
              fail('Nothing is missing');
              continue;
            }
            const text = `${u.name} asks you to fill in: ${missing.join(', ').toLowerCase()}.${input.message ? ` ${input.message}` : ''}`;
            for (const to2 of [c.ownerId, c.delegateId].filter((x): x is Id => !!x && x !== u.id)) {
              inApp(to2, 'DataCompletionRequested', `Please complete ${c.title}`, text, c.id, `/contracts/${c.id}`, u.id);
              ctx.notify(to2, `Please complete ${c.title}`, text, c.id, 'Mail');
            }
            ctx.audit('contract.data-completion-requested', c.id, `Data completion requested: ${missing.join(', ')}`, u.id);
            result.succeeded += 1;
            continue;
          }
          if (!canEdit(u, c)) {
            fail('You cannot change this contract');
            continue;
          }
          if (input.action === 'AddTag' && tag) {
            if (!c.tagIds.includes(tag.id)) c.tagIds.push(tag.id);
            ctx.audit('contract.bulk', c.id, `Tag "${tag.name}" added`, u.id);
          } else if (input.action === 'RemoveTag' && tag) {
            c.tagIds = c.tagIds.filter((t) => t !== tag.id);
            ctx.audit('contract.bulk', c.id, `Tag "${tag.name}" removed`, u.id);
          } else if (input.action === 'Reassign' && to && input.role) {
            if (c.status === 'InApproval') {
              fail('In approval');
              continue;
            }
            const field = input.role === 'Owner' ? 'ownerId' : input.role === 'Delegate' ? 'delegateId' : 'responsibleId';
            c[field] = to.id;
            ctx.audit('contract.reassigned', c.id, `${input.role} changed to ${to.name}`, u.id);
          }
          result.succeeded += 1;
        }
        return result;
      }),

    getViews: () =>
      run(() => {
        const u = ctx.me();
        return db.views
          .filter((v) => v.ownerId === u.id || v.scope === 'Shared')
          .sort((a, b) => (a.scope === b.scope ? a.name.localeCompare(b.name) : a.scope === 'Personal' ? -1 : 1))
          .map((v) => toView(v, u));
      }),
    createView: (input) =>
      run(() => {
        const u = ctx.me();
        checkView(input, u);
        if (input.isDefault) for (const v of db.views) if (v.ownerId === u.id) v.isDefault = false;
        const rec: ViewRec = { id: newId(), name: input.name.trim(), scope: input.scope, ownerId: u.id, columns: [...input.columns], query: structuredClone(input.query ?? {}), isDefault: !!input.isDefault, updatedAt: ctx.now() };
        db.views.push(rec);
        if (rec.scope === 'Shared') ctx.audit('view.shared-changed', null, `Shared view "${rec.name}" created`, u.id);
        return toView(rec, u);
      }),
    updateView: (id, input) =>
      run(() => {
        const u = ctx.me();
        const rec = byId(db.views, id);
        if (!rec || (rec.ownerId !== u.id && rec.scope !== 'Shared')) throw notFound('View');
        if (!toView(rec, u).canEdit) throw forbidden('Only the owner of the view or an administrator can change it.');
        checkView(input, u, id);
        if (input.isDefault) for (const v of db.views) if (v.ownerId === u.id) v.isDefault = false;
        Object.assign(rec, { name: input.name.trim(), scope: input.scope, columns: [...input.columns], query: structuredClone(input.query ?? {}), isDefault: !!input.isDefault, updatedAt: ctx.now() });
        return toView(rec, u);
      }),
    deleteView: (id) =>
      run(() => {
        const u = ctx.me();
        const rec = byId(db.views, id);
        if (!rec || (rec.ownerId !== u.id && rec.scope !== 'Shared')) throw notFound('View');
        if (!toView(rec, u).canEdit) throw forbidden('Only the owner of the view or an administrator can delete it.');
        db.views = db.views.filter((v) => v.id !== id);
      }),

    createDataRoomExport: (input) =>
      run(() => {
        const u = ctx.me();
        if (!canExportDataRoom(u)) throw forbidden('Data room exports are for administrators, group management and entity management.');
        const name = input?.name?.trim();
        if (!name || name.length > 80) throw invalid('Enter a name of at most 80 characters.', { name: ['Required'] });
        if (!['All', 'MainOnly', 'None'].includes(input.documents) || !['Latest', 'All'].includes(input.versions)) throw invalid('Choose which documents to include.', { documents: ['Required'] });
        const rec: Db['dataRoom'][number] = {
          id: newId(),
          name,
          status: 'Running',
          contractCount: 0,
          documentCount: 0,
          sizeBytes: null,
          createdAt: ctx.now(),
          createdById: u.id,
          fileName: `${name.replace(/[\\/:*?"<>|]/g, '')} – ${ctx.today()}.zip`,
          error: null,
          expiresAt: `${addDays(ctx.today(), 7)}T23:59:59Z`,
          input: structuredClone(input),
          blob: null,
        };
        db.dataRoom.unshift(rec);
        buildDataRoom(rec, u);
        ctx.audit('dataroom.exported', null, `Data room export "${name}" with ${rec.contractCount} contracts`, u.id);
        return toExport(rec);
      }),
    getDataRoomExports: () =>
      run(() => {
        const u = ctx.me();
        if (!canExportDataRoom(u)) throw forbidden();
        return db.dataRoom.filter((r) => isAdmin(u) || r.createdById === u.id).map(toExport);
      }),
    getDataRoomExport: (id) =>
      run(() => {
        const u = ctx.me();
        const r = db.dataRoom.find((x) => x.id === id && (isAdmin(u) || x.createdById === u.id));
        if (!r) throw notFound('Export');
        return toExport(r);
      }),
    getDataRoomContent: (id) =>
      run((): FileDownload => {
        const u = ctx.me();
        const r = db.dataRoom.find((x) => x.id === id && (isAdmin(u) || x.createdById === u.id));
        if (!r) throw notFound('Export');
        if (r.status !== 'Ready' || !r.blob) throw conflict('The export is not ready yet.');
        return { blob: r.blob, fileName: r.fileName };
      }),
  };

  function checkComment(input: { body: string; mentionIds: Id[] }, c: ContractRec) {
    const body = input?.body?.trim() ?? '';
    if (!body || body.length > 4000) throw invalid('Write a comment of at most 4000 characters.', { body: ['Required'] });
    const mentions = [...new Set(input.mentionIds ?? [])].map((id) => ctx.userOf(id));
    const errors: string[] = [];
    for (const [i, m] of mentions.entries()) {
      if (!m || !m.isActive) errors.push(`Unknown person (${input.mentionIds[i] ?? ''})`);
      else if (!ctx.sees(m, c)) errors.push(`${m.name} cannot see this contract`);
    }
    if (errors.length) throw invalid(errors.join('. '), { mentionIds: errors });
    return { body, mentions: mentions as UserRec[] };
  }

  function toComment(x: Db['comments'][number], u: UserRec): Comment {
    const a = ctx.userOf(x.authorId);
    return {
      id: x.id,
      contractId: x.contractId,
      authorId: x.authorId,
      authorName: a?.name ?? '–',
      authorInitials: a?.initials ?? '?',
      body: x.body,
      mentions: x.mentionIds.map((id) => ({ userId: id, name: ctx.userName(id) })),
      createdAt: x.createdAt,
      editedAt: x.editedAt,
      canEdit: x.authorId === u.id,
      canDelete: x.authorId === u.id || isAdmin(u),
    };
  }

  // ===================================================================================
  const namedV3 = (list: Db['departments'], what: string, inUse: (id: Id) => boolean) => (id: Id, input: { name: string; isActive?: boolean }) =>
    run(() => {
      const u = ctx.admin();
      const item = byId(list, id);
      if (!item) throw notFound(`The ${what}`);
      const n = input.name?.trim();
      if (!n) throw invalid(`Enter a name for the ${what}.`, { name: ['Required'] });
      if (list.some((x) => x.id !== id && x.name.toLowerCase() === n.toLowerCase())) throw conflict(`A ${what} with that name already exists.`, 'duplicate');
      item.name = n;
      if (input.isActive !== undefined) item.isActive = input.isActive;
      ctx.audit('SettingsChanged', null, `Changed ${what} "${n}"${input.isActive === false ? ' (inactive)' : ''}`, u.id);
      void inUse;
      return { ...item };
    });

  const admin: R3Admin = {
    updateDepartmentV3: namedV3(db.departments, 'department', () => false),
    updateContractTypeV3: namedV3(db.contractTypes, 'contract type', () => false),

    getCounterparties: (query = {}) =>
      run((): Paged<Counterparty> => {
        ctx.admin();
        const needle = query.q?.trim().toLowerCase() ?? '';
        const rows = db.suppliers
          .filter((s) => (query.active === undefined || String(s.isActive) === String(query.active)) && (!query.kind || s.kind === query.kind))
          .filter((s) => !needle || [s.name, s.vatNo, s.externalId, s.city, s.country].some((v) => v?.toLowerCase().includes(needle)))
          .sort((a, b) => a.name.localeCompare(b.name))
          .map((s) => counterpartyOut(s));
        return pageRows(rows, query.page ?? 1, query.pageSize ?? 25);
      }),
    createCounterparty: (input) => run(() => saveCounterparty(input)),
    updateCounterparty: (id, input) => run(() => saveCounterparty(input, id)),
    getCounterpartyDuplicates: () =>
      run((): DuplicateGroup[] => {
        ctx.admin();
        const active = db.suppliers.filter((s) => s.isActive);
        const groups: DuplicateGroup[] = [];
        const seen = new Set<string>();
        const add = (reason: DuplicateGroup['reason'], keyOf: (s: (typeof active)[number]) => string | null) => {
          const map = new Map<string, typeof active>();
          for (const s of active) {
            const k = keyOf(s);
            if (!k) continue;
            map.set(k, [...(map.get(k) ?? []), s]);
          }
          for (const items of map.values()) {
            if (items.length < 2) continue;
            const sig = items.map((x) => x.id).sort().join('|');
            if (seen.has(sig)) continue;
            seen.add(sig);
            groups.push({ reason, items: items.map((x) => counterpartyOut(x)) });
          }
        };
        add('VatNo', (s) => s.vatNo?.toLowerCase() ?? null);
        add('ExternalId', (s) => s.externalId?.toLowerCase() ?? null);
        add('Name', (s) => normName(s.name) || null);
        return groups;
      }),
    mergeCounterparties: (input) =>
      run(() => {
        const u = ctx.admin();
        const target = byId(db.suppliers, input?.targetId);
        if (!target || !target.isActive) throw invalid('Choose an active record to keep.', { targetId: ['Required'] });
        const sources = (input.sourceIds ?? []).map((id) => byId(db.suppliers, id));
        if (sources.length === 0 || sources.some((s) => !s) || input.sourceIds.includes(target.id)) throw invalid('Choose the duplicates to merge into the record you keep.', { sourceIds: ['Required'] });
        let moved = 0;
        for (const c of db.contracts) {
          if (c.supplierId && input.sourceIds.includes(c.supplierId)) {
            c.supplierId = target.id;
            c.counterpartyName = target.name;
            moved += 1;
          }
        }
        for (const s of sources) if (s) s.isActive = false;
        target.updatedAt = ctx.now();
        ctx.audit('counterparty.merged', null, `Merged ${sources.map((s) => s?.name).join(', ')} into ${target.name} (${moved} contracts moved)`, u.id);
        return { target: counterpartyOut(target), movedContracts: moved, deactivated: sources.length };
      }),

    getImportFields: (kind) => run(() => (ctx.admin(), checkKind(kind), importFields(kind, db.customFields))),
    getImportTemplate: (kind) =>
      run((): FileDownload => {
        ctx.admin();
        checkKind(kind);
        const fields = importFields(kind, db.customFields);
        const cell = (s: string) => (/[",;\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
        const csv = `${fields.map((f) => cell(f.label)).join(',')}\r\n${fields.map((f) => cell(f.example ?? '')).join(',')}\r\n`;
        return { blob: new Blob([String.fromCharCode(0xfeff) + csv], { type: 'text/csv;charset=utf-8' }), fileName: `${kind}-import-template.csv` };
      }),
    uploadImport: (kind, file) =>
      run(async () => {
        ctx.admin();
        checkKind(kind);
        if (!file || file.size === 0) throw invalid('Choose a file.', { file: ['Required'] });
        if (file.size > 10 * 1024 * 1024) throw invalid('The file is larger than 10 MB.', { file: ['File too large'] });
        let table: string[][];
        if (/\.csv$/i.test(file.name) || /\.txt$/i.test(file.name)) table = parseCsv(await file.text());
        else if (/\.xlsx$/i.test(file.name)) {
          try {
            table = await readXlsx(await file.arrayBuffer());
          } catch {
            throw invalid('The Excel file could not be read. Save it as .xlsx or CSV and try again.', { file: ['Unreadable'] });
          }
        } else throw invalid('Upload a CSV or Excel (.xlsx) file.', { file: ['Unsupported file type'] });
        const { columns, data } = splitTable(table);
        if (columns.length === 0 || columns.some((c) => !c)) throw invalid('The first row must contain a name for every column.', { file: ['Empty header'] });
        if (new Set(columns.map((c) => c.toLowerCase())).size !== columns.length) throw invalid('Two columns have the same name.', { file: ['Duplicate header'] });
        if (data.length > 5000) throw invalid('The file has more than 5000 rows.', { file: ['Too many rows'] });
        const fields = importFields(kind, db.customFields);
        const s = { id: newId(), kind, fileName: file.name, columns, sample: data.slice(0, 5), rowCount: data.length, suggestedMapping: suggestMapping(columns, fields), expiresAt: `${addDays(ctx.today(), 1)}T23:59:59Z`, rows: data, used: false };
        db.importSessions.push(s);
        const { rows: _rows, used: _used, ...out } = s;
        return out;
      }),
    previewImport: (sessionId, input) =>
      run(() => {
        ctx.admin();
        const s = session(sessionId);
        checkImportOptions(input, s.columns);
        const { plans, mappingErrors } = planImport(s.kind, s.columns, s.rows, input.mapping, input.options, importRefs());
        const count = (a: string) => plans.filter((p) => p.preview.action === a).length;
        return { sessionId, rowCount: plans.length, create: count('Create'), update: count('Update'), skip: count('Skip'), error: count('Error'), rows: plans.map((p) => p.preview), mappingErrors };
      }),
    runImport: (sessionId, input) =>
      run(() => {
        const u = ctx.admin();
        const s = session(sessionId);
        if (s.used) throw conflict('This import has already been run. Upload the file again to import it once more.', 'import-state');
        checkImportOptions(input, s.columns);
        const { plans, mappingErrors } = planImport(s.kind, s.columns, s.rows, input.mapping, input.options, importRefs());
        if (mappingErrors.length) throw invalid(mappingErrors.join(' '), { mapping: mappingErrors });
        s.used = true;
        return runPlans(s.kind, plans, input.options, s.fileName, u, 'File');
      }),
    getImportHistory: (kind) => run(() => (ctx.admin(), db.importHistory.filter((h) => !kind || h.kind === kind).slice(0, 100).map((h) => structuredClone(h)))),

    getSyncConnections: () => run(() => (ctx.admin(), db.syncConnections.map((c) => ({ ...c })))),
    getSyncSources: () => run(() => (ctx.admin(), db.syncSources.map(toSource))),
    createSyncSource: (input) =>
      run(() => {
        const u = ctx.admin();
        checkSyncInput(input);
        const rec = { ...structuredClone(input), id: newId(), name: input.name.trim(), runs: [] };
        db.syncSources.push(rec);
        ctx.audit('settings.sync', null, `Sync source "${rec.name}" added`, u.id);
        return toSource(rec);
      }),
    updateSyncSource: (id, input) =>
      run(() => {
        const u = ctx.admin();
        const rec = byId(db.syncSources, id);
        if (!rec) throw notFound('Sync source');
        checkSyncInput(input);
        Object.assign(rec, structuredClone(input), { name: input.name.trim() });
        ctx.audit('settings.sync', null, `Sync source "${rec.name}" changed`, u.id);
        return toSource(rec);
      }),
    deleteSyncSource: (id) =>
      run(() => {
        const u = ctx.admin();
        const rec = byId(db.syncSources, id);
        if (!rec) throw notFound('Sync source');
        db.syncSources = db.syncSources.filter((s) => s.id !== id);
        ctx.audit('settings.sync', null, `Sync source "${rec.name}" deleted`, u.id);
      }),
    testSyncSource: (input) =>
      run(() => {
        ctx.admin();
        if (input.kind === 'Sql' && !db.syncConnections.some((c) => c.name === input.sql?.connectionName)) {
          return { ok: false, message: `There is no connection named "${input.sql?.connectionName ?? ''}" in the server configuration.`, columns: [], sample: [], rowCount: null };
        }
        if (input.kind === 'Sql' && !/^\s*(select|with)\b/i.test(input.sql?.query ?? '')) return { ok: false, message: 'The query must start with SELECT or WITH.', columns: [], sample: [], rowCount: null };
        if (input.kind === 'PowerBi' && (!input.powerBi?.workspaceId || !input.powerBi.datasetId)) return { ok: false, message: 'Enter the workspace and the dataset.', columns: [], sample: [], rowCount: null };
        const { columns, rows } = simulatedRows(input);
        return { ok: true, message: `Connected. ${rows.length} rows returned (demo data).`, columns, sample: rows.slice(0, 5), rowCount: rows.length };
      }),
    runSyncSource: (id) =>
      run(() => {
        const u = ctx.admin();
        const rec = byId(db.syncSources, id);
        if (!rec) throw notFound('Sync source');
        const startedAt = ctx.now();
        const { columns, rows } = simulatedRows(rec);
        const { plans, mappingErrors } = planImport(rec.target, columns, rows, rec.mapping, rec.options, importRefs());
        let runRec: SyncRun;
        if (mappingErrors.length) {
          runRec = { id: newId(), sourceId: id, trigger: 'Manual', startedAt, finishedAt: ctx.now(), status: 'Failed', read: rows.length, created: 0, updated: 0, skipped: 0, failed: rows.length, errors: mappingErrors };
          for (const a of db.users.filter(isAdmin)) inApp(a.id, 'SyncFailed', `Sync failed: ${rec.name}`, mappingErrors.join(' '), null, '/settings/sync', null);
        } else {
          const result = runPlans(rec.target, plans, rec.options, rec.name, u, 'Sync');
          runRec = { id: newId(), sourceId: id, trigger: 'Manual', startedAt, finishedAt: ctx.now(), status: result.failed ? (result.created + result.updated + result.skipped ? 'PartiallySucceeded' : 'Failed') : 'Succeeded', read: rows.length, created: result.created, updated: result.updated, skipped: result.skipped, failed: result.failed, errors: result.errors.map((e) => `Row ${e.rowNo}: ${e.message}`) };
        }
        rec.runs.unshift(runRec);
        ctx.audit('sync.run', null, `Sync source "${rec.name}" run: ${runRec.status}`, u.id);
        return structuredClone(runRec);
      }),
    getSyncRuns: (id) =>
      run(() => {
        ctx.admin();
        const rec = byId(db.syncSources, id);
        if (!rec) throw notFound('Sync source');
        return structuredClone(rec.runs.slice(0, 50));
      }),

    getCustomFields: () => run(() => (ctx.admin(), structuredClone([...db.customFields].sort((a, b) => a.order - b.order)))),
    createCustomField: (input) =>
      run(() => {
        const u = ctx.admin();
        checkCustomField(input);
        const def: CustomFieldDef = { ...structuredClone(input), id: newId(), label: input.label.trim(), options: (input.options ?? []).map((o) => o.trim()), order: Math.max(-1, ...db.customFields.map((d) => d.order)) + 1, helpText: input.helpText?.trim() || null };
        db.customFields.push(def);
        ctx.audit('settings.custom-fields', null, `Custom field "${def.label}" added`, u.id);
        return structuredClone(def);
      }),
    updateCustomField: (id, input) =>
      run(() => {
        const u = ctx.admin();
        const def = byId(db.customFields, id);
        if (!def) throw notFound('Custom field');
        if (input.key !== def.key) throw invalid('The key cannot be changed.', { key: ['Cannot be changed'] });
        checkCustomField(input, id);
        const used = valuesOf(def.key);
        if (input.type !== def.type && used.length > 0) throw conflict(`${used.length} contracts have a value in this field, so its type cannot change.`, 'in-use');
        const removed = def.options.filter((o) => !input.options.includes(o));
        const usedRemoved = removed.filter((o) => used.some((v) => v === o || (Array.isArray(v) && v.includes(o))));
        if (usedRemoved.length) throw conflict(`The option ${usedRemoved.map((o) => `"${o}"`).join(', ')} is used by contracts.`, 'in-use');
        Object.assign(def, structuredClone(input), { id, order: def.order, label: input.label.trim(), helpText: input.helpText?.trim() || null });
        ctx.audit('settings.custom-fields', null, `Custom field "${def.label}" changed`, u.id);
        return structuredClone(def);
      }),
    deleteCustomField: (id) =>
      run(() => {
        const u = ctx.admin();
        const def = byId(db.customFields, id);
        if (!def) throw notFound('Custom field');
        const used = valuesOf(def.key).length;
        if (used > 0) throw conflict(`${used} contracts have a value in this field. Make it inactive instead.`, 'in-use');
        db.customFields = db.customFields.filter((d) => d.id !== id);
        ctx.audit('settings.custom-fields', null, `Custom field "${def.label}" deleted`, u.id);
      }),
    reorderCustomFields: (ids) =>
      run(() => {
        ctx.admin();
        const listed = ids.map((id) => byId(db.customFields, id)).filter((d): d is CustomFieldDef => !!d);
        const rest = db.customFields.filter((d) => !ids.includes(d.id)).sort((a, b) => a.order - b.order);
        [...listed, ...rest].forEach((d, i) => (d.order = i));
        return structuredClone([...db.customFields].sort((a, b) => a.order - b.order));
      }),

    getFieldRules: () => run(() => (ctx.admin(), structuredClone(db.fieldRules))),
    updateFieldRules: (input) =>
      run(() => {
        const u = ctx.admin();
        const errors: Record<string, string[]> = {};
        const seen = new Set<Id>();
        input.types.forEach((t, i) => {
          if (!byId(db.contractTypes, t.typeId) || seen.has(t.typeId)) errors[`types.${i}.typeId`] = ['Unknown or repeated contract type'];
          seen.add(t.typeId);
          if (t.required.some((f) => !(OPTIONAL_STANDARD_FIELDS as readonly string[]).includes(f))) errors[`types.${i}.required`] = ['Unknown field'];
          const d = t.defaults;
          if (d.currency && !db.currencyTable.some((c) => c.code === d.currency && c.isActive)) errors[`types.${i}.defaults.currency`] = ['Unknown currency'];
          if (d.noticePeriodDays !== null && !(d.noticePeriodDays >= 0 && d.noticePeriodDays <= 3650)) errors[`types.${i}.defaults.noticePeriodDays`] = ['0–3650 days'];
          if (d.renewalMonths !== null && !(d.renewalMonths >= 1 && d.renewalMonths <= 120)) errors[`types.${i}.defaults.renewalMonths`] = ['1–120 months'];
        });
        if (Object.keys(errors).length) throw invalid('Check the field rules.', errors);
        db.fieldRules = structuredClone({ types: input.types.filter((t) => t.required.length > 0 || Object.values(t.defaults).some((v) => v !== null)) });
        ctx.audit('settings.field-rules', null, 'Field rules changed', u.id);
        return structuredClone(db.fieldRules);
      }),

    getEntities: () => run(() => (ctx.admin(), db.entities.map((e) => ({ ...e, contractCount: db.contracts.filter((c) => c.entityId === e.id).length })))),
    createEntity: (input) => run(() => saveEntity(input)),
    updateEntity: (id, input) => run(() => saveEntity(input, id)),

    getCurrencies: () => run(() => (ctx.admin(), structuredClone(db.currencyTable))),
    updateCurrencies: (input) =>
      run(() => {
        const u = ctx.admin();
        const codes = input.map((c) => c.code?.trim().toUpperCase());
        const errors: Record<string, string[]> = {};
        input.forEach((c, i) => {
          if (!/^[A-Z]{3}$/.test(codes[i] ?? '')) errors[`${i}.code`] = ['Three letters, e.g. EUR'];
          if (!(c.rateToDkk > 0)) errors[`${i}.rateToDkk`] = ['Must be above 0'];
        });
        if (new Set(codes).size !== codes.length) errors.code = ['Each code only once'];
        const dkk = input.find((c) => c.code === 'DKK');
        if (!dkk || !dkk.isActive || dkk.rateToDkk !== 1) errors.DKK = ['DKK must stay active with rate 1'];
        if (Object.keys(errors).length) throw invalid('Check the currencies.', errors);
        const removed = db.currencyTable.filter((c) => !codes.includes(c.code)).map((c) => c.code);
        const used = removed.filter((code) => db.contracts.some((c) => c.currency === code) || db.entities.some((e) => e.currency === code));
        if (used.length) throw conflict(`${used.join(', ')} is used by contracts or legal entities. Make it inactive instead.`, 'in-use');
        db.currencyTable = input.map((c, i) => ({ code: codes[i] ?? c.code, name: c.name?.trim() || (codes[i] ?? c.code), rateToDkk: c.rateToDkk, isActive: c.isActive }));
        db.currencies = db.currencyTable.filter((c) => c.isActive).map((c) => c.code);
        ctx.audit('settings.currencies', null, 'Currencies changed', u.id);
        return structuredClone(db.currencyTable);
      }),

    getTags: () => run(() => (ctx.admin(), db.tags.map((t): Tag => ({ ...t, usageCount: db.contracts.filter((c) => c.tagIds.includes(t.id)).length })).sort((a, b) => a.name.localeCompare(b.name)))),
    createTag: (input) => run(() => saveTag(input)),
    updateTag: (id, input) => run(() => saveTag(input, id)),
    deleteTag: (id) =>
      run(() => {
        const u = ctx.admin();
        const t = byId(db.tags, id);
        if (!t) throw notFound('Tag');
        db.tags = db.tags.filter((x) => x.id !== id);
        for (const c of db.contracts) c.tagIds = c.tagIds.filter((x) => x !== id);
        ctx.audit('settings.tags', null, `Tag "${t.name}" deleted`, u.id);
      }),

    getUsers: (query = {}) =>
      run(() => {
        ctx.admin();
        const needle = query.q?.trim().toLowerCase() ?? '';
        return db.users
          .filter((u) => (query.active === undefined || String(u.isActive) === String(query.active)) && (!needle || [u.name, u.email, u.title].some((v) => v.toLowerCase().includes(needle))))
          .sort((a, b) => a.name.localeCompare(b.name))
          .map(toAdminUser);
      }),
    createUser: (input) =>
      run(() => {
        const u = ctx.admin();
        checkUserInput(input);
        if (db.users.some((x) => x.email.toLowerCase() === input.email.trim().toLowerCase())) throw conflict('A user with that e-mail already exists.', 'duplicate');
        const parts = input.name.trim().split(' ');
        const rec: UserRec = { id: newId(), name: input.name.trim(), email: input.email.trim().toLowerCase(), initials: `${parts[0]?.[0] ?? ''}${parts[parts.length - 1]?.[0] ?? ''}`.toUpperCase(), title: input.title?.trim() ?? '', roles: [...input.roles], departmentIds: [...input.departmentIds], managedEntityIds: [...input.managedEntityIds], isGroupManagement: input.isGroupManagement, isActive: input.isActive, source: 'Manual', externalId: null, lastSignInAt: null };
        db.users.push(rec);
        ctx.audit('settings.users', null, `User ${rec.name} added`, u.id);
        return toAdminUser(rec);
      }),
    updateUser: (id, input) =>
      run(() => {
        const me = ctx.admin();
        const rec = byId(db.users, id);
        if (!rec) throw notFound('User');
        checkUserInput(input);
        if (rec.source === 'Entra') {
          const changed = SYNCED_FIELDS.filter((f) => {
            const before = f === 'title' ? rec.title || null : (rec as unknown as Record<string, unknown>)[f];
            const after = f === 'title' ? input.title?.trim() || null : (input as unknown as Record<string, unknown>)[f];
            return before !== after;
          });
          if (changed.length) throw conflict(`${changed.join(', ')} come from Entra ID and cannot be changed here.`, 'synced-field');
        }
        if (rec.id === me.id && (!input.roles.includes('Admin') || !input.isActive)) throw conflict('You cannot remove your own administrator role or deactivate yourself.');
        if (db.users.some((x) => x.id !== id && x.email.toLowerCase() === input.email.trim().toLowerCase())) throw conflict('A user with that e-mail already exists.', 'duplicate');
        Object.assign(rec, { name: input.name.trim(), email: input.email.trim().toLowerCase(), title: input.title?.trim() ?? '', roles: [...input.roles], departmentIds: [...input.departmentIds], managedEntityIds: [...input.managedEntityIds], isGroupManagement: input.isGroupManagement, isActive: input.isActive });
        ctx.audit('settings.users', null, `User ${rec.name} changed`, me.id);
        return toAdminUser(rec);
      }),
    getEffectiveAccess: (userId, query = {}) =>
      run(() => {
        ctx.admin();
        const target = byId(db.users, userId);
        if (!target) throw notFound('User');
        const all = target.isActive ? ctx.visible(target) : [];
        const withReasons = all.map((c) => ({ c, reasons: reasonsFor(target, c) }));
        const rules = new Map<AccessRule, { rule: AccessRule; label: string; count: number }>();
        const ruleLabel: Record<AccessRule, string> = { Department: 'Department membership', EntityManagement: 'Entity management', GroupManagement: 'Group management', OnContract: 'Owner, delegate or responsible', Admin: 'Administrator', CurrentApprover: 'Current approver' };
        for (const x of withReasons) for (const r of new Set(x.reasons.map((y) => y.rule))) rules.set(r, { rule: r, label: ruleLabel[r], count: (rules.get(r)?.count ?? 0) + 1 });
        const filtered = sortRows(
          withReasons.map((x) => ctx.toRow(x.c, target)),
          'title',
        ).filter((r) => {
          const q = query.q?.trim().toLowerCase();
          return !q || [r.item.title, r.item.counterparty, r.item.contractNo].some((v) => v.toLowerCase().includes(q));
        });
        const paged = pageRows(filtered, query.page ?? 1, query.pageSize ?? 25);
        return {
          user: toAdminUser(target),
          total: all.length,
          rules: [...rules.values()],
          page: paged.page,
          pageSize: paged.pageSize,
          contracts: paged.items.map((r) => ({ item: r.item, reasons: withReasons.find((x) => x.c.id === r.item.id)?.reasons ?? [] })),
        };
      }),
    previewReassign: (input) =>
      run(() => {
        ctx.admin();
        const plan = reassignPlan(input);
        return {
          rows: plan.rows.map((r) => ({ contractId: r.c.id, contractNo: r.c.contractNo, title: r.c.title, status: r.c.status, roles: r.roles, skipReason: r.skipReason })),
          contracts: plan.rows.filter((r) => !r.skipReason).length,
          obligations: plan.obligations.length,
        };
      }),
    reassign: (input) =>
      run(() => {
        const me = ctx.admin();
        const plan = reassignPlan(input);
        let updated = 0;
        for (const r of plan.rows) {
          if (r.skipReason) continue;
          if (r.roles.includes('Owner')) r.c.ownerId = plan.to.id;
          if (r.roles.includes('Delegate')) r.c.delegateId = plan.to.id;
          if (r.roles.includes('Responsible')) r.c.responsibleId = plan.to.id;
          ctx.audit('contract.reassigned', r.c.id, `${r.roles.join(', ')} moved from ${plan.from.name} to ${plan.to.name}`, me.id);
          updated += 1;
        }
        for (const o of plan.obligations) o.responsibleId = plan.to.id;
        return { updated, obligationsMoved: plan.obligations.length, skipped: plan.rows.filter((r) => r.skipReason).map((r) => ({ contractId: r.c.id, reason: r.skipReason ?? '' })) };
      }),

    getNotificationSettings: () => run(() => (ctx.admin(), structuredClone(db.notificationSettings))),
    updateNotificationSettings: (input) =>
      run(() => {
        const u = ctx.admin();
        const errors: Record<string, string[]> = {};
        const events = input?.events ?? [];
        if (events.length !== EVENT_DEFS.length || EVENT_DEFS.some((d) => events.filter((e) => e.event === d.event).length !== 1)) errors.events = ['Every event exactly once'];
        for (const e of events) {
          const def = EVENT_DEFS.find((d) => d.event === e.event);
          if (!def) continue;
          if (!e.subject?.trim() || e.subject.length > 200) errors[`events.${e.event}.subject`] = ['1–200 characters'];
          if (!e.body?.trim() || e.body.length > 4000) errors[`events.${e.event}.body`] = ['1–4000 characters'];
          const unknown = [...`${e.subject} ${e.body}`.matchAll(/\{\{\s*([\w.]+)\s*\}\}/g)].map((m) => m[1] ?? '').filter((p) => !def.placeholders.includes(p));
          if (unknown.length) errors[`events.${e.event}.body`] = [`Unknown placeholder: ${[...new Set(unknown)].join(', ')}`];
        }
        const typeSeen = new Set<Id>();
        input.reminderSchedules?.forEach((r, i) => {
          if (!byId(db.contractTypes, r.typeId) || typeSeen.has(r.typeId)) errors[`reminderSchedules.${i}.typeId`] = ['Unknown or repeated contract type'];
          typeSeen.add(r.typeId);
          if (r.noticeDays.length < 1 || r.noticeDays.length > 5 || r.noticeDays.some((d) => !Number.isInteger(d) || d <= 0) || new Set(r.noticeDays).size !== r.noticeDays.length) errors[`reminderSchedules.${i}.noticeDays`] = ['1–5 different whole days'];
          if (!(r.escalationDays >= 0)) errors[`reminderSchedules.${i}.escalationDays`] = ['0 or more'];
        });
        if (!['Off', 'Daily', 'Weekly'].includes(input.digestDefault)) errors.digestDefault = ['Required'];
        if (Object.keys(errors).length) throw invalid('Check the notification settings.', errors);
        db.notificationSettings = {
          digestDefault: input.digestDefault,
          events: EVENT_DEFS.map((d) => {
            const e = events.find((x) => x.event === d.event) as (typeof events)[number];
            return { ...structuredClone(e), label: d.label, placeholders: [...d.placeholders] };
          }),
          reminderSchedules: (input.reminderSchedules ?? []).map((r) => ({ typeId: r.typeId, noticeDays: [...r.noticeDays].sort((a, b) => b - a), escalationDays: Math.floor(r.escalationDays) })),
        };
        ctx.audit('settings.notifications', null, 'Notification settings changed', u.id);
        return structuredClone(db.notificationSettings);
      }),
    previewNotificationTemplate: (input) =>
      run(() => {
        ctx.admin();
        const def = EVENT_DEFS.find((d) => d.event === input.event);
        if (!def) throw invalid('Unknown event.', { event: ['Unknown'] });
        const sample: Record<string, string> = {
          'contract.title': 'Raw material supply – rubber compounds',
          'contract.no': 'C-2024-0187',
          'contract.url': 'https://contracts.dafa.example/contracts/…',
          'contract.entity': 'DAFA A/S',
          'contract.counterparty': 'Nordpolymer Compounds ApS',
          'recipient.name': 'Mette Kjær',
          'actor.name': 'Rasmus Holm',
          'app.name': db.appSettings.appName,
          deadline: '15 Dec 2026',
          days: '30',
          'step.role': 'Group CFO',
          comment: 'Looks good.',
          decision: 'approved',
          change: 'uploaded a document',
          missing: 'notice period, signed date',
          message: 'Please do this before Friday.',
          'import.file': 'vendors.csv',
          'import.created': '12',
          'import.failed': '1',
          'source.name': 'ERP vendors (Power BI)',
        };
        const unknown = new Set<string>();
        const fill = (s: string) => s.replace(/\{\{\s*([\w.]+)\s*\}\}/g, (_m, k: string) => {
          if (!def.placeholders.includes(k)) unknown.add(k);
          return sample[k] ?? `{{${k}}}`;
        });
        return { subject: fill(input.subject ?? ''), body: fill(input.body ?? ''), unknown: [...unknown] };
      }),

    getJobs: () => run(() => (ctx.admin(), db.jobs.map((j) => ({ ...structuredClone(j), nextRunAt: nextRun(j.schedule) })))),
    updateJobSchedule: (key, schedule) =>
      run(() => {
        const u = ctx.admin();
        const j = db.jobs.find((x) => x.key === key);
        if (!j) throw notFound('Job');
        if (!['Off', 'Hourly', 'Daily', 'Weekly'].includes(schedule?.frequency)) throw invalid('Choose how often.', { 'schedule.frequency': ['Required'] });
        if ((schedule.frequency === 'Daily' || schedule.frequency === 'Weekly') && !/^([01]\d|2[0-3]):[0-5]\d$/.test(schedule.time ?? '')) throw invalid('Enter the time as HH:mm.', { 'schedule.time': ['Use HH:mm'] });
        if (schedule.frequency === 'Weekly' && !(schedule.dayOfWeek && schedule.dayOfWeek >= 1 && schedule.dayOfWeek <= 7)) throw invalid('Choose a day.', { 'schedule.dayOfWeek': ['Required'] });
        j.schedule = { frequency: schedule.frequency, time: schedule.time || '06:00', dayOfWeek: schedule.frequency === 'Weekly' ? schedule.dayOfWeek : null };
        ctx.audit('settings.jobs', null, `Schedule of "${j.label}" changed`, u.id);
        return { ...structuredClone(j), nextRunAt: nextRun(j.schedule) };
      }),
    runJob: (key) =>
      run(() => {
        const u = ctx.admin();
        const j = db.jobs.find((x) => x.key === key);
        if (!j) throw notFound('Job');
        const started = Date.now();
        let message: string;
        if (key === 'Reminders' || key === 'Obligations' || key === 'ContractEnd') {
          const r = ctx.runJobs();
          message = key === 'Reminders' ? `${r.remindersCreated} reminders, ${r.escalations} escalations` : key === 'Obligations' ? `${r.obligationReminders} obligation reminders` : `${r.contractsExpired} contracts ended`;
        } else if (key === 'CounterpartySync') {
          message = `${db.syncSources.filter((s) => s.isActive && s.schedule.frequency !== 'Off').length} sync sources checked`;
        } else if (key === 'DataRoomCleanup') {
          const before = db.dataRoom.length;
          db.dataRoom = db.dataRoom.filter((r) => !r.expiresAt || r.expiresAt >= ctx.now());
          message = `${before - db.dataRoom.length} expired exports deleted`;
        } else if (key === 'DirectorySync') {
          message = 'Directory sync is only available with Entra ID.';
        } else message = 'Done';
        j.lastRun = { at: ctx.now(), status: 'Succeeded', message, durationMs: Math.max(1, Date.now() - started) };
        ctx.audit('JobsRun', null, `Job "${j.label}" run: ${message}`, u.id);
        return { ...structuredClone(j), nextRunAt: nextRun(j.schedule) };
      }),

    getAppSettings: () => run(() => (ctx.admin(), structuredClone(db.appSettings))),
    updateAppSettings: (input) =>
      run(() => {
        const u = ctx.admin();
        const errors: Record<string, string[]> = {};
        if (!input.appName?.trim() || input.appName.trim().length > 60) errors.appName = ['1–60 characters'];
        if (!(input.sessionTimeoutMinutes >= 15 && input.sessionTimeoutMinutes <= 1440)) errors.sessionTimeoutMinutes = ['15–1440 minutes'];
        if (!(input.ddThresholdDkk >= 0)) errors.ddThresholdDkk = ['0 or more'];
        const cfKeys = new Set(db.customFields.filter((d) => d.isActive).map((d) => `cf.${d.key}`));
        if (input.dataQualityFields.some((f) => !(DATA_QUALITY_FIELDS as readonly string[]).includes(f) && !cfKeys.has(f))) errors.dataQualityFields = ['Unknown field'];
        for (const r of ['Admin', 'Approver', 'ContractOwner', 'Reader'] as Role[]) if (!LANDING.includes(input.landingPageByRole?.[r])) errors[`landingPageByRole.${r}`] = ['Required'];
        if (Object.keys(errors).length) throw invalid('Check the general settings.', errors);
        db.appSettings = { ...structuredClone(input), appName: input.appName.trim() } as AppSettings;
        ctx.audit('settings.general', null, 'General settings changed', u.id);
        return structuredClone(db.appSettings);
      }),
  };

  function checkKind(kind: ImportKind): void {
    if (!['counterparties', 'users', 'contracts'].includes(kind)) throw notFound('Import');
  }

  function checkUserInput(input: Parameters<Api['admin']['createUser']>[0]): void {
    const errors: Record<string, string[]> = {};
    if (!input.name?.trim()) errors.name = ['Required'];
    if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(input.email?.trim() ?? '')) errors.email = ['Enter a valid e-mail address'];
    if (!input.roles?.length || input.roles.some((r) => !['Reader', 'ContractOwner', 'Approver', 'Admin'].includes(r))) errors.roles = ['Choose at least one role'];
    if (input.departmentIds?.some((d) => !byId(db.departments, d))) errors.departmentIds = ['Unknown department'];
    if (input.managedEntityIds?.some((e) => !byId(db.entities, e))) errors.managedEntityIds = ['Unknown legal entity'];
    if (Object.keys(errors).length) throw invalid('Check the user.', errors);
  }

  function saveCounterparty(input: CounterpartyInput, id?: Id): Counterparty {
    const u = ctx.admin();
    const errors: Record<string, string[]> = {};
    const name = input.name?.trim() ?? '';
    if (!name || name.length > 300) errors.name = ['Required, at most 300 characters'];
    if (input.country && !/^[A-Z]{2}$/.test(input.country.trim().toUpperCase())) errors.country = ['Two-letter code'];
    if (input.email && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(input.email)) errors.email = ['Enter a valid e-mail address'];
    if (Object.keys(errors).length) throw invalid('Check the counterparty.', errors);
    const clash = db.suppliers.find((s) => s.id !== id && s.isActive && ((input.vatNo && s.vatNo?.toLowerCase() === input.vatNo.trim().toLowerCase()) || (input.externalId && s.externalId?.toLowerCase() === input.externalId.trim().toLowerCase())));
    if (clash) throw conflict(`${clash.name} already has this VAT number or external id.`, 'duplicate');
    const values = { name, country: input.country?.trim().toUpperCase() || null, vatNo: input.vatNo?.trim() || null, kind: input.kind ?? 'Supplier', externalId: input.externalId?.trim() || null, city: input.city?.trim() || null, email: input.email?.trim() || null, phone: input.phone?.trim() || null, isActive: input.isActive ?? true };
    if (id) {
      const rec = byId(db.suppliers, id);
      if (!rec) throw notFound('Counterparty');
      const renamed = rec.name !== name;
      Object.assign(rec, values, { updatedAt: ctx.now() });
      if (renamed) for (const c of db.contracts) if (c.supplierId === id) c.counterpartyName = name;
      ctx.audit(values.isActive ? 'counterparty.updated' : 'counterparty.deactivated', null, `Counterparty ${name} ${values.isActive ? 'changed' : 'deactivated'}`, u.id);
      return counterpartyOut(rec);
    }
    const rec = { id: newId(), ...values, source: 'Manual' as const, updatedAt: ctx.now() };
    db.suppliers.push(rec);
    ctx.audit('counterparty.created', null, `Counterparty ${name} added`, u.id);
    return counterpartyOut(rec);
  }

  function saveEntity(input: { name: string; country: string; currency: string; isActive: boolean }, id?: Id) {
    const u = ctx.admin();
    const errors: Record<string, string[]> = {};
    const name = input.name?.trim() ?? '';
    if (!name || name.length > 200) errors.name = ['Required'];
    else if (db.entities.some((e) => e.id !== id && e.name.toLowerCase() === name.toLowerCase())) errors.name = ['Another legal entity has this name'];
    if (!/^[A-Z]{2}$/.test(input.country?.trim().toUpperCase() ?? '')) errors.country = ['Two-letter code, e.g. DK'];
    if (!db.currencyTable.some((c) => c.code === input.currency && c.isActive)) errors.currency = ['Choose an active currency'];
    if (Object.keys(errors).length) throw invalid('Check the legal entity.', errors);
    const values = { name, country: input.country.trim().toUpperCase(), currency: input.currency, isActive: !!input.isActive };
    let rec = id ? byId(db.entities, id) : undefined;
    if (id && !rec) throw notFound('Legal entity');
    if (rec) Object.assign(rec, values);
    else {
      rec = { id: newId(), ...values };
      db.entities.push(rec);
    }
    ctx.audit('settings.entities', null, `Legal entity ${name} ${id ? 'changed' : 'added'}`, u.id);
    return { ...rec, contractCount: db.contracts.filter((c) => c.entityId === rec?.id).length };
  }

  function saveTag(input: { name: string; color: Tag['color']; description: string | null }, id?: Id): Tag {
    const u = ctx.admin();
    const name = input.name?.trim() ?? '';
    if (!name || name.length > 40) throw invalid('Enter a name of at most 40 characters.', { name: ['Required'] });
    if (!['gray', 'blue', 'green', 'amber', 'red', 'purple'].includes(input.color)) throw invalid('Choose a colour.', { color: ['Required'] });
    if (db.tags.some((t) => t.id !== id && t.name.toLowerCase() === name.toLowerCase())) throw conflict('A tag with that name already exists.', 'duplicate');
    let rec = id ? byId(db.tags, id) : undefined;
    if (id && !rec) throw notFound('Tag');
    if (rec) Object.assign(rec, { name, color: input.color, description: input.description?.trim() || null });
    else {
      rec = { id: newId(), name, color: input.color, description: input.description?.trim() || null };
      db.tags.push(rec);
    }
    ctx.audit('settings.tags', null, `Tag "${name}" ${id ? 'changed' : 'added'}`, u.id);
    return { ...rec, usageCount: db.contracts.filter((c) => c.tagIds.includes(rec?.id ?? '')).length };
  }

  return { top, admin, watchersChanged };
}
