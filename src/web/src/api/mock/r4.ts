// Release 4 endpoints of the mock API (docs/CONTRACT_V4.md): header-filter values, person and
// counterparty pickers, template categories, branding, SharePoint/Entra links, e-signature.
// Fields release 4 adds to existing payloads are put on by `decorate`, which wraps the finished
// mock so the release 1–3 code stays as it was.
import type { Api } from '../api';
import type {
  AccessMapping,
  AdminUser,
  Branding,
  ContractDetail,
  ContractFilter,
  Counterparty,
  CounterpartyCreation,
  DepartmentAdmin,
  DistinctColumn,
  DistinctValue,
  ESignSettings,
  Id,
  LegalEntity,
  LinkStatus,
  LinkTestResult,
  LogoVariant,
  PersonOption,
  ReconciliationItem,
  ReconciliationReport,
  StorageHealth,
  SignaturePermissions,
  SignatureRequest,
  Template,
  TemplateCategory,
  TemplateLanguage,
  UserV4,
} from '../types';
import { statusLabel } from '../../lib/format';
import { newId, seedId, type ContractRec, type DocumentRec, type UserRec } from './db';
import { conflict, forbidden, invalid, notFound, problem } from './problems';
import { matchesFilter } from './query';
import type { MockCtx } from './r3';
import { DEP, ENT, TYP, USER, departmentId, entityId, typeId } from './seed';

interface CategoryRec {
  id: Id;
  name: string;
  description: string;
  order: number;
  isActive: boolean;
}
interface TemplateMeta {
  categoryId: Id | null;
  typeIds: Id[];
  language: TemplateLanguage | null;
  isActive: boolean;
  updatedAt: string;
}
interface EntityLink {
  sharePointSiteUrl: string | null;
  sharePointSiteId: string | null;
  managementGroupId: string | null;
  managementGroupName: string | null;
  link: LinkStatus;
}
interface DeptLink {
  documentLibrary: string | null;
  groupId: string | null;
  groupName: string | null;
  link: LinkStatus;
}
export interface R4State {
  categories: CategoryRec[];
  templateMeta: Record<Id, TemplateMeta>;
  branding: { full: string | null; compact: string | null; dark?: string | null; version: number };
  entityLinks: Record<Id, EntityLink>;
  deptLinks: Record<Id, DeptLink>;
  esign: ESignSettings;
  requests: SignatureRequest[];
  users: Record<Id, Required<UserV4>>;
  counterpartyCreation: CounterpartyCreation;
  reconciliation?: ReconciliationReport;
}

const NOT_LINKED: LinkStatus = { state: 'NotLinked', checkedAt: null, message: null };
const LOGO_TYPES = /^image\/(png|svg\+xml|jpeg|webp)$/;
const LOGO_EXT = /\.(png|svg|jpe?g|webp)$/i;
const SITE_URL = /^https:\/\/[a-z0-9-]+\.sharepoint\.com\/sites\/[A-Za-z0-9._-]+\/?$/;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

function initialState(today: string): R4State {
  const cat = (n: number) => seedId(30, n);
  return {
    categories: [
      { id: cat(1), name: 'Purchasing', description: 'Supply, framework and service agreements with suppliers.', order: 0, isActive: true },
      { id: cat(2), name: 'Confidentiality', description: 'NDAs and confidentiality undertakings.', order: 1, isActive: true },
      { id: cat(3), name: 'IT and services', description: 'Software, SaaS and consultancy.', order: 2, isActive: true },
    ],
    templateMeta: {
      [seedId(14, 1)]: { categoryId: cat(1), typeIds: [typeId(TYP.direct), typeId(TYP.indirect)], language: 'en', isActive: true, updatedAt: `${today}T08:00:00Z` },
      [seedId(14, 2)]: { categoryId: cat(2), typeIds: [typeId(TYP.nda)], language: 'en', isActive: true, updatedAt: `${today}T08:00:00Z` },
    },
    branding: { full: null, compact: null, version: 0 },
    entityLinks: {
      [entityId(ENT.group)]: { sharePointSiteUrl: 'https://dafa.sharepoint.com/sites/contracts-group', sharePointSiteId: 'dafa.sharepoint.com,4c1e…,group', managementGroupId: '6f1a2b3c-0000-4000-8000-000000000001', managementGroupName: 'DAFA Group management', link: { state: 'Linked', checkedAt: `${today}T07:00:00Z`, message: null } },
      [entityId(ENT.dafa)]: { sharePointSiteUrl: 'https://dafa.sharepoint.com/sites/contracts-dafa', sharePointSiteId: null, managementGroupId: null, managementGroupName: null, link: { state: 'Error', checkedAt: `${today}T07:00:00Z`, message: 'Site not found' } },
    },
    deptLinks: {
      [departmentId(DEP.purchasing)]: { documentLibrary: 'Purchasing contracts', groupId: '6f1a2b3c-0000-4000-8000-000000000101', groupName: 'DAFA Purchasing', link: { state: 'Linked', checkedAt: `${today}T07:00:00Z`, message: null } },
    },
    esign: {
      provider: 'Simulated',
      defaultMessage: 'Please sign the attached agreement.',
      defaultOrder: 'Sequential',
      reminderDays: [3, 7],
      expiryDays: 30,
      requireApproved: true,
      authorisedSignatories: [{ entityId: entityId(ENT.group), userIds: [USER.ras, USER.kar], externalNames: [] }],
      restrictToAuthorised: false,
      providerAccount: null,
    },
    requests: [],
    users: {
      [USER.ras]: { entityId: entityId(ENT.group), canSendForSignature: true, signatureEntityIds: [] },
      [USER.kar]: { entityId: entityId(ENT.group), canSendForSignature: true, signatureEntityIds: [] },
      [USER.met]: { entityId: entityId(ENT.dafa), canSendForSignature: true, signatureEntityIds: [entityId(ENT.dafa), entityId(ENT.group)] },
    },
    counterpartyCreation: 'ContractOwners',
  };
}

function readDataUrl(file: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result));
    r.onerror = () => reject(r.error);
    r.readAsDataURL(file);
  });
}

export function createR4(ctx: MockCtx) {
  const { db, run } = ctx;
  const state = (): R4State => (db.r4 ??= initialState(ctx.today()));
  const byId = <T extends { id: Id }>(list: T[], id: Id | null | undefined): T | undefined => (id ? list.find((x) => x.id === id) : undefined);
  const userExtra = (id: Id): Required<UserV4> => state().users[id] ?? { entityId: null, canSendForSignature: false, signatureEntityIds: [] };

  // ----- People -----
  const personOption = (u: UserRec): PersonOption => {
    const extra = userExtra(u.id);
    return {
      id: u.id,
      name: u.name,
      initials: u.initials,
      email: u.email,
      title: u.title || null,
      departmentName: u.departmentIds[0] ? ctx.nameOf(db.departments, u.departmentIds[0]) : null,
      entityName: extra.entityId ? ctx.nameOf(db.entities, extra.entityId) : u.managedEntityIds[0] ? ctx.nameOf(db.entities, u.managedEntityIds[0]) : null,
      isActive: u.isActive,
    };
  };

  // ----- Templates -----
  const templateFields = (t: Template): Template => {
    const meta = state().templateMeta[t.id] ?? { categoryId: null, typeIds: t.typeId ? [t.typeId] : [], language: null, isActive: true, updatedAt: ctx.now() };
    const category = byId(state().categories, meta.categoryId);
    return { ...t, categoryId: meta.categoryId, categoryName: category?.name ?? null, typeIds: [...meta.typeIds], language: meta.language, isActive: meta.isActive, updatedAt: meta.updatedAt };
  };
  const toCategory = (c: CategoryRec): TemplateCategory => ({ ...c, templateCount: db.templates.filter((t) => state().templateMeta[t.id]?.categoryId === c.id).length });
  const saveCategory = (input: { name: string; description: string; isActive: boolean }, id?: Id): TemplateCategory => {
    const u = ctx.admin();
    const name = input.name?.trim() ?? '';
    if (!name || name.length > 80) throw invalid('Enter a name (up to 80 characters).', { name: ['Required'] });
    if (state().categories.some((c) => c.id !== id && c.name.toLowerCase() === name.toLowerCase())) throw conflict('A category with that name already exists.', 'duplicate');
    let rec = byId(state().categories, id);
    if (id && !rec) throw notFound('Template category');
    if (rec) Object.assign(rec, { name, description: input.description?.trim() ?? '', isActive: !!input.isActive });
    else {
      rec = { id: newId(), name, description: input.description?.trim() ?? '', isActive: input.isActive !== false, order: state().categories.length };
      state().categories.push(rec);
    }
    ctx.audit('settings.template-categories', null, `Template category "${name}" ${id ? 'changed' : 'added'}`, u.id);
    return toCategory(rec);
  };

  // ----- Branding -----
  const branding = (): Branding => {
    const b = state().branding;
    return { logoUrl: b.full, compactLogoUrl: b.compact, darkLogoUrl: b.dark ?? null, version: String(b.version) };
  };

  // ----- Links -----
  const entityLink = (id: Id): EntityLink => state().entityLinks[id] ?? { sharePointSiteUrl: null, sharePointSiteId: null, managementGroupId: null, managementGroupName: null, link: NOT_LINKED };
  const deptLink = (id: Id): DeptLink => state().deptLinks[id] ?? { documentLibrary: null, groupId: null, groupName: null, link: NOT_LINKED };
  const withEntityLink = (e: LegalEntity): LegalEntity => ({ ...e, ...entityLink(e.id) });
  const toDept = (d: { id: Id; name: string; isActive?: boolean }): DepartmentAdmin => ({ id: d.id, name: d.name, isActive: d.isActive, ...deptLink(d.id) });

  // ----- E-signature -----
  const statusOk = (c: ContractRec) => (state().esign.requireApproved ? c.status === 'Approved' : ['Draft', 'Approved', 'Active'].includes(c.status));
  function permission(u: UserRec, c: ContractRec): { ok: boolean; reason: string | null } {
    const s = state().esign;
    if (s.provider === 'None') return { ok: false, reason: 'E-signature is turned off in Settings.' };
    const extra = userExtra(u.id);
    if (!extra.canSendForSignature) return { ok: false, reason: 'You do not have the right to send contracts for signature.' };
    if (extra.signatureEntityIds.length && !extra.signatureEntityIds.includes(c.entityId)) return { ok: false, reason: `Your signing right does not cover ${ctx.nameOf(db.entities, c.entityId)}.` };
    if (!statusOk(c)) return { ok: false, reason: s.requireApproved ? `The contract must be approved first (it is ${statusLabel(c.status)}).` : 'Ended contracts cannot be sent for signature.' };
    if (open(c.id)) return { ok: false, reason: 'A signature request is already open.' };
    return { ok: true, reason: null };
  }
  const open = (contractId: Id) => state().requests.find((r) => r.contractId === contractId && r.status === 'Sent');
  const latest = (contractId: Id) => state().requests.filter((r) => r.contractId === contractId).sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0] ?? null;
  const permittedUsers = (c: ContractRec) =>
    db.users
      .filter((x) => x.isActive && ctx.sees(x, c))
      .filter((x) => {
        const e = userExtra(x.id);
        return e.canSendForSignature && (e.signatureEntityIds.length === 0 || e.signatureEntityIds.includes(c.entityId));
      })
      .map(personOption);
  const requestFor = (id: Id, u: UserRec) => {
    const r = state().requests.find((x) => x.id === id);
    if (!r) throw notFound('Signature request');
    const c = ctx.contractFor(r.contractId, u);
    const isSender = r.createdBy === u.name;
    const e = userExtra(u.id);
    if (!isSender && !e.canSendForSignature && !u.roles.includes('Admin')) throw problem(403, 'Forbidden', 'Only the sender, colleagues with signing rights or an administrator can do this.', undefined, 'urn:dafa:problem:not-permitted');
    return { r, c };
  };
  function complete(r: SignatureRequest, c: ContractRec, u: UserRec) {
    const source = db.documents.find((d) => d.id === r.documentIds[0]);
    const base = (source?.name ?? c.title).replace(/\.[a-z0-9]+$/i, '');
    const doc: DocumentRec = {
      id: newId(),
      contractId: c.id,
      name: `${base} – signed.pdf`,
      kind: 'Main',
      storeKey: `signed/${c.id}`,
      version: 1,
      size: source?.size ?? 48_000,
      uploadedBy: u.id,
      uploadedAt: ctx.now(),
      blob: source?.blob ?? null,
      text: `${source?.text ?? ''}\nSigned electronically by ${r.signers.map((s) => s.name).join(', ')}.`,
    };
    db.documents.push(doc);
    r.status = 'Completed';
    r.completedAt = ctx.now();
    r.signedDocumentId = doc.id;
    c.status = 'Active';
    c.signedDate = ctx.today();
    c.signingMethod = 'E-signature';
    ctx.audit('signature.completed', c.id, `Signed by all parties; ${doc.name} stored and the contract is active`, u.id);
    for (const id of [c.ownerId, c.delegateId].filter((x): x is Id => !!x)) ctx.notify(id, `Signed: ${c.title}`, `Every party has signed "${c.title}". The signed document is stored on the contract.`, c.id, 'Mail', 'SignatureCompleted' as never, `/contracts/${c.id}`);
  }
  function advance(r: SignatureRequest) {
    if (r.order !== 'Sequential') return;
    const waiting = r.signers.filter((s) => s.status === 'Waiting');
    if (r.signers.some((s) => s.status === 'Sent' || s.status === 'Viewed') || waiting.length === 0) return;
    const next = Math.min(...waiting.map((s) => s.order));
    for (const s of waiting) if (s.order === next) s.status = 'Sent';
  }

  function decorateDetail(d: ContractDetail, u: UserRec): ContractDetail {
    const c = byId(db.contracts, d.id);
    if (!c) return d;
    return { ...d, signature: latest(c.id), canSendForSignature: permission(u, c).ok };
  }

  // ----- Distinct values -----
  const COLUMN_PARAM: Record<string, keyof ContractFilter> = { counterparty: 'counterpartyId', type: 'typeId', entity: 'entityId', department: 'departmentId', owner: 'ownerId', delegate: 'delegateId', responsible: 'responsibleId', status: 'status', currency: 'currency', tags: 'tagId' };
  function distinct(column: DistinctColumn, filter: ContractFilter, q?: string, limit = 50) {
    const u = ctx.me();
    const isCf = column.startsWith('cf.');
    const key = isCf ? column.slice(3) : '';
    if (!isCf && !COLUMN_PARAM[column]) throw invalid('Unknown column.', { column: ['Unknown column'] });
    const def = isCf ? db.customFields.find((d) => d.key === key) : undefined;
    if (isCf && !def) throw invalid('Unknown column.', { column: ['Unknown custom field'] });
    // The column's own filter is left out, so the list does not collapse to what is selected.
    const f: ContractFilter = { ...filter };
    if (isCf) {
      const cf = { ...(f.cf ?? {}) };
      delete cf[key];
      f.cf = cf;
    } else delete (f as Record<string, unknown>)[COLUMN_PARAM[column] as string];
    const rows = ctx.visible(u).map((c) => ({ c, row: ctx.toRow(c, u) })).filter(({ row }) => matchesFilter(row, f, { userId: u.id, today: ctx.today(), includeEndedDefault: true, customFields: db.customFields }));
    const counts = new Map<string, DistinctValue>();
    const add = (value: string | null | undefined, label: string) => {
      if (!value) return;
      const hit = counts.get(value);
      if (hit) hit.count += 1;
      else counts.set(value, { value, label, count: 1 });
    };
    for (const { c } of rows) {
      switch (column) {
        case 'counterparty':
          add(c.supplierId, byId(db.suppliers, c.supplierId)?.name ?? c.counterpartyName);
          break;
        case 'type':
          add(c.typeId, ctx.nameOf(db.contractTypes, c.typeId));
          break;
        case 'entity':
          add(c.entityId, ctx.nameOf(db.entities, c.entityId));
          break;
        case 'department':
          add(c.departmentId, ctx.nameOf(db.departments, c.departmentId));
          break;
        case 'owner':
          add(c.ownerId, ctx.userName(c.ownerId));
          break;
        case 'delegate':
          add(c.delegateId, ctx.userName(c.delegateId));
          break;
        case 'responsible':
          add(c.responsibleId, ctx.userName(c.responsibleId));
          break;
        case 'status':
          add(c.status, statusLabel(c.status));
          break;
        case 'currency':
          add(c.currency, c.currency);
          break;
        case 'tags':
          for (const t of c.tagIds) add(t, byId(db.tags, t)?.name ?? t);
          break;
        default: {
          const v = c.customFields[key];
          if (Array.isArray(v)) for (const x of v) add(x, x);
          else if (typeof v === 'string') add(v, def?.type === 'Person' ? ctx.userName(v) : v);
          else if (typeof v === 'boolean') add(String(v), v ? 'Yes' : 'No');
        }
      }
    }
    const needle = q?.trim().toLowerCase() ?? '';
    const all = [...counts.values()].filter((v) => !needle || v.label.toLowerCase().includes(needle)).sort((a, b) => b.count - a.count || a.label.localeCompare(b.label));
    const max = Math.min(Math.max(1, limit), 200);
    return { column, values: all.slice(0, max), truncated: all.length > max };
  }

  const top: Pick<
    Api,
    | 'getDistinct'
    | 'searchPeople'
    | 'createCounterpartyQuick'
    | 'getTemplateCategories'
    | 'getTemplatePreview'
    | 'getBranding'
    | 'assetUrl'
    | 'getSignaturePermissions'
    | 'getSignatureRequests'
    | 'createSignatureRequest'
    | 'resendSignatureRequest'
    | 'cancelSignatureRequest'
    | 'simulateSignature'
  > = {
    getDistinct: (column, filter, q, limit) => run(() => distinct(column, filter, q, limit)),

    searchPeople: (query) =>
      run(() => {
        ctx.me();
        const needle = query.q?.trim().toLowerCase() ?? '';
        const limit = Math.min(Math.max(1, query.limit ?? 20), 50);
        return db.users
          .filter((u) => (query.includeInactive || u.isActive) && (!query.role || u.roles.includes(query.role) || u.roles.includes('Admin')))
          .filter((u) => !needle || [u.name, u.email, u.initials].some((v) => v.toLowerCase().includes(needle)))
          .sort((a, b) => a.name.localeCompare(b.name))
          .slice(0, limit)
          .map(personOption);
      }),

    createCounterpartyQuick: (input) =>
      run((): Counterparty => {
        const u = ctx.me();
        const rule = state().counterpartyCreation;
        const allowed = u.roles.includes('Admin') || rule === 'AllUsers' || (rule === 'ContractOwners' && u.roles.includes('ContractOwner'));
        if (!allowed) throw problem(403, 'Forbidden', 'Only administrators can create counterparties. Ask an administrator, or pick an existing one.', undefined, 'urn:dafa:problem:not-permitted');
        const name = input.name?.trim() ?? '';
        if (!name || name.length > 200) throw invalid('Enter the counterparty name.', { name: ['Required'] });
        const vat = input.vatNo?.trim() || null;
        const dup = db.suppliers.find((s) => s.isActive && (s.name.trim().toLowerCase() === name.toLowerCase() || (vat && s.vatNo === vat)));
        if (dup) {
          const err = conflict(`"${dup.name}" already exists. Pick it from the list.`, 'duplicate');
          (err.problem as unknown as Record<string, unknown>).existingId = dup.id;
          throw err;
        }
        const country = input.country?.trim().toUpperCase() || null;
        if (country && !/^[A-Z]{2}$/.test(country)) throw invalid('Use a two-letter country code.', { country: ['Two letters, e.g. DK'] });
        const email = input.email?.trim() || null;
        if (email && !EMAIL.test(email)) throw invalid('Check the e-mail address.', { email: ['Not an e-mail address'] });
        const rec = { id: newId(), name, country, vatNo: vat, kind: input.kind ?? 'Supplier', externalId: null, city: input.city?.trim() || null, email, phone: input.phone?.trim() || null, isActive: true, source: 'Manual' as const, updatedAt: ctx.now() };
        db.suppliers.push(rec);
        ctx.audit('counterparty.created', null, `Counterparty ${name} created`, u.id);
        return { ...rec, contractCount: 0 };
      }),

    getTemplateCategories: () =>
      run(() => {
        ctx.me();
        return state()
          .categories.filter((c) => c.isActive)
          .sort((a, b) => a.order - b.order)
          .map((c) => ({ ...toCategory(c), templateCount: db.templates.filter((t) => state().templateMeta[t.id]?.categoryId === c.id && state().templateMeta[t.id]?.isActive !== false).length }));
      }),

    getTemplatePreview: (id) =>
      run(() => {
        ctx.me();
        const t = byId(db.templates, id);
        if (!t) throw notFound('Template');
        const sample: Record<string, string> = { title: 'Supply of rubber compounds', entity: 'DAFA A/S', counterparty: 'Nordpolymer Compounds ApS', startDate: '2027-01-01', endDate: '2029-12-31', noticePeriodDays: '90', currency: 'DKK', paymentTerms: 'Net 30', governingLaw: 'Danish law' };
        const label = (n: string) => n.replace(/([A-Z])/g, ' $1').replace(/^./, (x) => x.toUpperCase());
        return { id: t.id, name: t.name, description: t.description, placeholders: t.placeholders.map((n) => ({ name: n, label: label(n), sample: sample[n] ?? '' })), excerpt: t.body.slice(0, 1000) };
      }),

    getBranding: () => run(() => branding()),
    assetUrl: (path) => path,

    getSignaturePermissions: (contractId) =>
      run((): SignaturePermissions => {
        const u = ctx.me();
        const c = ctx.contractFor(contractId, u);
        const p = permission(u, c);
        return { canSend: p.ok, reason: p.reason, permittedUsers: permittedUsers(c) };
      }),

    getSignatureRequests: (contractId) =>
      run(() => {
        const u = ctx.me();
        ctx.contractFor(contractId, u);
        return state()
          .requests.filter((r) => r.contractId === contractId)
          .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
          .map((r) => structuredClone(r));
      }),

    createSignatureRequest: (contractId, input) =>
      run(() => {
        const u = ctx.me();
        const c = ctx.contractFor(contractId, u);
        const s = state().esign;
        if (s.provider === 'None') throw problem(400, 'Feature off', 'E-signature is turned off in Settings.', undefined, 'urn:dafa:problem:feature-off');
        if (open(c.id)) throw conflict('A signature request is already open for this contract.', 'signature-state');
        const p = permission(u, c);
        if (!p.ok) throw problem(403, 'Forbidden', p.reason ?? 'Not permitted.', undefined, 'urn:dafa:problem:not-permitted');
        const errors: Record<string, string[]> = {};
        const docs = (input.documentIds ?? []).filter((id) => db.documents.some((d) => d.id === id && d.contractId === c.id));
        if (docs.length === 0 || docs.length !== (input.documentIds ?? []).length) errors.documentIds = ['Choose at least one document of this contract'];
        if (!input.signers?.length) errors.signers = ['Add at least one signatory'];
        const signers = (input.signers ?? []).map((x, i) => {
          if (x.kind === 'Internal') {
            const person = ctx.userOf(x.userId ?? '');
            if (!person || !person.isActive) errors[`signers.${i}.userId`] = ['Choose an active user'];
            return { id: newId(), kind: 'Internal' as const, userId: person?.id ?? null, name: person?.name ?? '', email: person?.email ?? '', order: x.order, status: 'Waiting' as const, at: null, declineReason: null };
          }
          if (!x.name?.trim()) errors[`signers.${i}.name`] = ['Enter the name'];
          if (!EMAIL.test(x.email?.trim() ?? '')) errors[`signers.${i}.email`] = ['Enter a valid e-mail'];
          return { id: newId(), kind: 'External' as const, userId: null, name: x.name?.trim() ?? '', email: x.email?.trim() ?? '', order: x.order, status: 'Waiting' as const, at: null, declineReason: null };
        });
        if (s.restrictToAuthorised) {
          const allowed = s.authorisedSignatories.find((a) => a.entityId === c.entityId)?.userIds ?? [];
          if (!signers.some((x) => x.userId && allowed.includes(x.userId))) errors.signers = [`Add an authorised signatory of ${ctx.nameOf(db.entities, c.entityId)}`];
        }
        if (Object.keys(errors).length) throw invalid('Check the signature request.', errors);
        const order = input.order ?? s.defaultOrder;
        const r: SignatureRequest = {
          id: newId(),
          contractId: c.id,
          status: 'Sent',
          provider: s.provider,
          order,
          message: input.message?.trim() || s.defaultMessage,
          documentIds: docs,
          createdBy: u.name,
          createdAt: ctx.now(),
          sentAt: ctx.now(),
          completedAt: null,
          expiresAt: `${addDaysIso(ctx.today(), s.expiryDays)}T23:59:59Z`,
          signers: signers.map((x) => ({ ...x, status: order === 'Parallel' ? ('Sent' as const) : ('Waiting' as const) })),
          signedDocumentId: null,
        };
        advance(r);
        state().requests.push(r);
        ctx.audit('signature.sent', c.id, `Sent for signature to ${r.signers.map((x) => x.name).join(', ')}`, u.id);
        for (const x of r.signers) if (x.userId && x.userId !== u.id) ctx.notify(x.userId, `Please sign: ${c.title}`, `${u.name} asks you to sign "${c.title}".`, c.id, 'Mail', 'SignatureRequested' as never, `/contracts/${c.id}`);
        return structuredClone(r);
      }),

    resendSignatureRequest: (id) =>
      run(() => {
        const u = ctx.me();
        const { r, c } = requestFor(id, u);
        if (r.status !== 'Sent') throw conflict('Only an open request can be sent again.', 'signature-state');
        ctx.audit('signature.resent', c.id, 'Signature request sent again', u.id);
        return structuredClone(r);
      }),

    cancelSignatureRequest: (id) =>
      run(() => {
        const u = ctx.me();
        const { r, c } = requestFor(id, u);
        if (r.status !== 'Sent') throw conflict('Only an open request can be cancelled.', 'signature-state');
        r.status = 'Cancelled';
        ctx.audit('signature.cancelled', c.id, 'Signature request cancelled', u.id);
        return structuredClone(r);
      }),

    simulateSignature: (id, input) =>
      run(() => {
        const u = ctx.me();
        if (state().esign.provider !== 'Simulated') throw problem(400, 'Feature off', 'Simulation is only available with the Simulated provider.', undefined, 'urn:dafa:problem:feature-off');
        const { r, c } = requestFor(id, u);
        if (r.status !== 'Sent') throw conflict('The request is not open.', 'signature-state');
        const s = r.signers.find((x) => x.id === input.signerId);
        if (!s) throw notFound('Signer');
        if (s.status !== 'Sent' && s.status !== 'Viewed') throw conflict(r.order === 'Sequential' && s.status === 'Waiting' ? 'It is not this signatory’s turn yet.' : 'This signatory has already acted.', 'signature-state');
        s.at = ctx.now();
        if (input.action === 'View') s.status = 'Viewed';
        else if (input.action === 'Decline') {
          s.status = 'Declined';
          s.declineReason = input.reason?.trim() || null;
          r.status = 'Declined';
          ctx.audit('signature.declined', c.id, `${s.name} declined to sign`, u.id);
        } else {
          s.status = 'Signed';
          ctx.audit('signature.signed', c.id, `${s.name} signed`, u.id);
          advance(r);
          if (r.signers.every((x) => x.status === 'Signed')) complete(r, c, u);
        }
        return structuredClone(r);
      }),
  };

  const admin: Pick<
    Api['admin'],
    | 'getTemplateCategories'
    | 'createTemplateCategory'
    | 'updateTemplateCategory'
    | 'deleteTemplateCategory'
    | 'orderTemplateCategories'
    | 'uploadLogo'
    | 'deleteLogo'
    | 'getDepartmentsV4'
    | 'updateDepartmentV4'
    | 'testEntityLink'
    | 'testDepartmentLink'
    | 'getAccessMapping'
    | 'getESignSettings'
    | 'updateESignSettings'
    | 'getStorageHealth'
    | 'getReconciliation'
    | 'runReconciliation'
  > = {
    getStorageHealth: () =>
      run((): StorageHealth => {
        ctx.admin();
        return { mode: 'Local', state: 'Healthy', lastSuccessAt: ctx.now(), lastFailureAt: null, lastError: null, checkedAt: ctx.now(), recentFailures: 0, details: ['Test mode: files are kept in local storage on the server.'] };
      }),
    getReconciliation: () => run(() => (ctx.admin(), state().reconciliation ? structuredClone(state().reconciliation as ReconciliationReport) : null)),
    runReconciliation: () =>
      run((): ReconciliationReport => {
        const u = ctx.admin();
        const items: ReconciliationItem[] = [];
        for (const d of db.documents) {
          const c = byId(db.contracts, d.contractId);
          if (!c) items.push({ kind: 'Orphan', contractId: null, contractNo: null, documentId: d.id, path: d.storeKey, detail: 'File without a contract' });
          else if (d.size === 0) items.push({ kind: 'Mismatch', contractId: c.id, contractNo: c.contractNo, documentId: d.id, path: d.storeKey, detail: 'Size 0 in storage' });
        }
        const report: ReconciliationReport = { id: newId(), startedAt: ctx.now(), finishedAt: ctx.now(), status: 'Completed', error: null, checkedDocuments: db.documents.length, checkedFolders: new Set(db.documents.map((d) => d.contractId)).size, items };
        state().reconciliation = report;
        ctx.audit('storage.reconciled', null, `Storage check: ${items.length} findings`, u.id);
        return structuredClone(report);
      }),
    getTemplateCategories: () => run(() => (ctx.admin(), [...state().categories].sort((a, b) => a.order - b.order).map(toCategory))),
    createTemplateCategory: (input) => run(() => saveCategory(input)),
    updateTemplateCategory: (id, input) => run(() => saveCategory(input, id)),
    deleteTemplateCategory: (id) =>
      run(() => {
        const u = ctx.admin();
        const c = byId(state().categories, id);
        if (!c) throw notFound('Template category');
        if (db.templates.some((t) => state().templateMeta[t.id]?.categoryId === id)) throw conflict('Templates use this category. Move them first, or make it inactive.', 'in-use');
        state().categories = state().categories.filter((x) => x.id !== id);
        state().categories.forEach((x, i) => (x.order = i));
        ctx.audit('settings.template-categories', null, `Template category "${c.name}" deleted`, u.id);
      }),
    orderTemplateCategories: (ids) =>
      run(() => {
        ctx.admin();
        const cats = state().categories;
        if (ids.length !== cats.length || !cats.every((c) => ids.includes(c.id))) throw invalid('List every category once.', { ids: ['Every category once'] });
        for (const c of cats) c.order = ids.indexOf(c.id);
        return [...cats].sort((a, b) => a.order - b.order).map(toCategory);
      }),

    uploadLogo: (variant, file) =>
      run(async () => {
        const u = ctx.admin();
        if (!(LOGO_TYPES.test(file.type) || LOGO_EXT.test(file.name))) throw invalid('Use a PNG, SVG, JPG or WebP image.', { file: ['PNG, SVG, JPG or WebP'] });
        if (file.size > 1024 * 1024) throw invalid('The logo can be at most 1 MB.', { file: ['Max 1 MB'] });
        const url = await readDataUrl(file);
        const b = state().branding;
        if (variant === 'compact') b.compact = url;
        else if (variant === 'dark') b.dark = url;
        else b.full = url;
        b.version += 1;
        ctx.audit('settings.branding', null, `${variant === 'compact' ? 'Compact icon' : variant === 'dark' ? 'Dark-theme logo' : 'Logo'} uploaded`, u.id);
        return branding();
      }),
    deleteLogo: (variant: LogoVariant) =>
      run(() => {
        const u = ctx.admin();
        const b = state().branding;
        if (variant === 'compact') b.compact = null;
        else if (variant === 'dark') b.dark = null;
        else b.full = null;
        b.version += 1;
        ctx.audit('settings.branding', null, `${variant === 'compact' ? 'Compact icon' : 'Logo'} removed`, u.id);
        return branding();
      }),

    getDepartmentsV4: () => run(() => (ctx.admin(), db.departments.map(toDept))),
    updateDepartmentV4: (id, input) =>
      run(() => {
        const u = ctx.admin();
        const d = byId(db.departments, id);
        if (!d) throw notFound('Department');
        const name = input.name?.trim() ?? '';
        if (!name) throw invalid('Enter a name.', { name: ['Required'] });
        const groupId = input.groupId?.trim() || null;
        if (groupId && !GUID.test(groupId)) throw invalid('The Entra group id is a GUID.', { groupId: ['Not a GUID'] });
        const before = deptLink(id);
        Object.assign(d, { name, isActive: input.isActive ?? d.isActive });
        const library = input.documentLibrary?.trim() || null;
        const changed = library !== before.documentLibrary || groupId !== before.groupId;
        state().deptLinks[id] = { documentLibrary: library, groupId, groupName: changed ? null : before.groupName, link: changed ? NOT_LINKED : before.link };
        ctx.audit('settings.departments', null, `Department ${name} changed`, u.id);
        return toDept(d);
      }),
    testEntityLink: (id) =>
      run((): LinkTestResult => {
        const u = ctx.admin();
        const e = byId(db.entities, id);
        if (!e) throw notFound('Legal entity');
        const l = entityLink(id);
        const details: string[] = [];
        let ok = true;
        if (!l.sharePointSiteUrl) {
          ok = false;
          details.push('No SharePoint site URL.');
        }
        else if (!SITE_URL.test(l.sharePointSiteUrl)) {
          ok = false;
          details.push('The site URL is not https://<tenant>.sharepoint.com/sites/<name>.');
        }
        if (l.managementGroupId && !GUID.test(l.managementGroupId)) {
          ok = false;
          details.push('The Entra group id is not a GUID.');
        }
        details.push('Test mode: saved, used in SharePoint mode.');
        const siteId = ok && l.sharePointSiteUrl ? `dafa.sharepoint.com,${id.slice(0, 8)}` : null;
        const groupName = ok && l.managementGroupId ? `${e.name} management` : null;
        const link: LinkStatus = { state: ok ? 'Linked' : 'Error', checkedAt: ctx.now(), message: ok ? null : (details[0] ?? null) };
        state().entityLinks[id] = { ...l, sharePointSiteId: siteId, managementGroupName: groupName, link };
        ctx.audit('entity.link-tested', null, `Link of ${e.name} tested: ${link.state}`, u.id);
        return { link, siteId, groupName, details };
      }),
    testDepartmentLink: (id) =>
      run((): LinkTestResult => {
        const u = ctx.admin();
        const d = byId(db.departments, id);
        if (!d) throw notFound('Department');
        const l = deptLink(id);
        const details: string[] = [];
        let ok = true;
        if (!l.documentLibrary) {
          ok = false;
          details.push('No document library.');
        }
        if (l.groupId && !GUID.test(l.groupId)) {
          ok = false;
          details.push('The Entra group id is not a GUID.');
        }
        details.push('Test mode: saved, used in SharePoint mode.');
        const groupName = ok && l.groupId ? `DAFA ${d.name}` : null;
        const link: LinkStatus = { state: ok ? 'Linked' : 'Error', checkedAt: ctx.now(), message: ok ? null : (details[0] ?? null) };
        state().deptLinks[id] = { ...l, groupName, link };
        ctx.audit('department.link-tested', null, `Link of ${d.name} tested: ${link.state}`, u.id);
        return { link, siteId: null, groupName, details };
      }),
    getAccessMapping: () =>
      run((): AccessMapping => {
        ctx.admin();
        const cells: AccessMapping['cells'] = [];
        const warnings: string[] = [];
        for (const e of db.entities.filter((x) => x.isActive)) {
          const el = entityLink(e.id);
          if (!el.sharePointSiteUrl) warnings.push(`${e.name}: no SharePoint site`);
          for (const d of db.departments.filter((x) => x.isActive !== false)) {
            const count = db.contracts.filter((c) => c.entityId === e.id && c.departmentId === d.id).length;
            const dl = deptLink(d.id);
            if (count === 0 && !dl.documentLibrary) continue;
            const w: string[] = [];
            if (!el.sharePointSiteUrl && count) w.push(`${count} contracts have no site`);
            if (!dl.documentLibrary) w.push('No document library');
            if (!dl.groupId) w.push('No department group');
            cells.push({ entityId: e.id, departmentId: d.id, siteUrl: el.sharePointSiteUrl, library: dl.documentLibrary, entityGroupName: el.managementGroupName, departmentGroupName: dl.groupName, contractCount: count, warnings: w });
          }
        }
        for (const d of db.departments.filter((x) => x.isActive !== false)) if (!deptLink(d.id).documentLibrary) warnings.push(`${d.name}: no document library`);
        return { mode: 'Local', entities: db.entities.filter((x) => x.isActive).map((x) => ({ id: x.id, name: x.name })), departments: db.departments.filter((x) => x.isActive !== false).map((x) => ({ id: x.id, name: x.name })), cells, warnings };
      }),

    getESignSettings: () => run(() => (ctx.admin(), structuredClone(state().esign))),
    updateESignSettings: (input) =>
      run(() => {
        const u = ctx.admin();
        const errors: Record<string, string[]> = {};
        if (!['None', 'Simulated', 'DocuSign', 'Penneo'].includes(input.provider)) errors.provider = ['Choose a provider'];
        if ((input.reminderDays ?? []).some((d) => !Number.isInteger(d) || d < 1 || d > 60) || (input.reminderDays ?? []).length > 5) errors.reminderDays = ['Up to 5 values, 1–60 days'];
        if (!Number.isInteger(input.expiryDays) || input.expiryDays < 1 || input.expiryDays > 180) errors.expiryDays = ['1–180 days'];
        for (const [i, a] of (input.authorisedSignatories ?? []).entries()) {
          if (!byId(db.entities, a.entityId)) errors[`authorisedSignatories.${i}.entityId`] = ['Unknown legal entity'];
          if (a.userIds.some((id) => !ctx.userOf(id))) errors[`authorisedSignatories.${i}.userIds`] = ['Unknown user'];
        }
        if (Object.keys(errors).length) throw invalid('Check the e-signature settings.', errors);
        state().esign = { ...structuredClone(input), defaultMessage: input.defaultMessage?.trim() ?? '', reminderDays: [...new Set(input.reminderDays ?? [])].sort((a, b) => a - b) };
        db.appSettings.features.eSign = input.provider !== 'None';
        ctx.audit('settings.esign', null, `E-signature settings changed (${input.provider})`, u.id);
        return structuredClone(state().esign);
      }),
  };

  /**
   * Adds the release 4 fields to release 1–3 responses and the release 4 rules to release 1–3
   * routes (templates, users, entities, contracts, app settings, sign-in screen).
   */
  function decorate<T extends Api>(api: T): T {
    const me = () => ctx.me();
    const detail = <A extends unknown[]>(fn: (...a: A) => Promise<ContractDetail>) => async (...a: A) => decorateDetail(await fn(...a), me());
    const user = (u: AdminUser): AdminUser => ({ ...u, ...userExtra(u.id) });
    const saveUserExtra = (id: Id, input: UserV4) => {
      if (input.entityId && !db.entities.some((e) => e.id === input.entityId && e.isActive)) throw invalid('Choose an active legal entity.', { entityId: ['Unknown or inactive legal entity'] });
      const prev = userExtra(id);
      state().users[id] = {
        entityId: input.entityId === undefined ? prev.entityId : input.entityId,
        canSendForSignature: input.canSendForSignature ?? prev.canSendForSignature,
        signatureEntityIds: input.signatureEntityIds ?? prev.signatureEntityIds,
      };
    };
    const a = api.admin;
    const supplierRequired = (input: { supplierId: Id | null }) => {
      if (!input.supplierId || !db.suppliers.some((s) => s.id === input.supplierId && s.isActive)) throw invalid('Choose a counterparty from the list.', { supplierId: ['Choose a counterparty from the list'] });
      return db.suppliers.find((s) => s.id === input.supplierId)?.name ?? '';
    };
    const activePeople = (input: { ownerId: Id; delegateId: Id | null; responsibleId: Id | null }) => {
      const errors: Record<string, string[]> = {};
      for (const k of ['ownerId', 'delegateId', 'responsibleId'] as const) {
        const id = input[k];
        if (id && ctx.userOf(id) && !ctx.userOf(id)?.isActive) errors[k] = ['This person is no longer active'];
      }
      if (Object.keys(errors).length) throw invalid('Choose active people.', errors);
    };
    return {
      ...api,
      getMe: async () => ({ ...(await api.getMe()), ...userExtra(me().id) }),
      getLookups: async () => {
        const l = await api.getLookups();
        return { ...l, users: l.users.map((u) => ({ ...u, ...userExtra(u.id) })) };
      },
      getAppConfig: async () => {
        const cfg = await api.getAppConfig();
        const u = me();
        const rule = state().counterpartyCreation;
        return {
          ...cfg,
          branding: branding(),
          storageMode: 'Local' as const,
          eSign: { provider: state().esign.provider, enabled: state().esign.provider !== 'None' },
          canCreateCounterparty: u.roles.includes('Admin') || rule === 'AllUsers' || (rule === 'ContractOwners' && u.roles.includes('ContractOwner')),
        };
      },
      getDevConfig: async () => ({ ...(await api.getDevConfig()), branding: branding() }),
      getTemplates: async () => {
        const list = (await api.getTemplates()).map(templateFields).filter((t) => t.isActive !== false && (!t.categoryId || byId(state().categories, t.categoryId)?.isActive));
        const order = (t: Template) => byId(state().categories, t.categoryId)?.order ?? 999;
        return list.sort((x, y) => order(x) - order(y) || x.name.localeCompare(y.name));
      },
      getContract: detail(api.getContract),
      createContract: (input) => {
        try {
          const u = me();
          // Role check first (the release 1 rule), then the release 4 counterparty rule.
          if (!u.roles.some((r) => r === 'ContractOwner' || r === 'Admin')) return detail(api.createContract)(input);
          const name = supplierRequired(input);
          activePeople(input);
          return detail(api.createContract)({ ...input, counterpartyName: name });
        } catch (e) {
          return run(() => {
            throw e;
          });
        }
      },
      updateContract: (id, input) => {
        try {
          const c = byId(db.contracts, id);
          if (c?.supplierId && input.supplierId === null) throw invalid('Choose a counterparty from the list.', { supplierId: ['Choose a counterparty from the list'] });
          activePeople(input);
          const name = input.supplierId ? (db.suppliers.find((s) => s.id === input.supplierId)?.name ?? input.counterpartyName) : input.counterpartyName;
          return detail(api.updateContract)(id, { ...input, counterpartyName: name });
        } catch (e) {
          return run(() => {
            throw e;
          });
        }
      },
      submitContract: detail(api.submitContract),
      signContract: (id, input) => {
        const u = me();
        const c = byId(db.contracts, id);
        if (c && ctx.sees(u, c) && c.ownerId !== u.id && c.delegateId !== u.id && !u.roles.includes('Admin'))
          return run(() => {
            throw forbidden('Only the owner, the delegate or an administrator can record a manual signature.');
          });
        return detail(api.signContract)(id, input);
      },
      decideRenewal: detail(api.decideRenewal),
      uploadDocument: detail(api.uploadDocument),
      createLink: detail(api.createLink),
      admin: {
        ...a,
        getTemplates: async () => (await a.getTemplates()).map(templateFields),
        createTemplate: async (input, file) => {
          const t = await a.createTemplate(input, file);
          state().templateMeta[t.id] = { categoryId: input.categoryId ?? null, typeIds: input.typeIds ?? (input.typeId ? [input.typeId] : []), language: input.language ?? null, isActive: input.isActive ?? true, updatedAt: ctx.now() };
          return templateFields(t);
        },
        updateTemplate: async (id, input) => {
          if (input.categoryId && !byId(state().categories, input.categoryId)) throw invalid('Unknown category.', { categoryId: ['Unknown category'] });
          const t = await a.updateTemplate(id, { ...input, typeId: input.typeIds?.[0] ?? input.typeId });
          const prev = state().templateMeta[id];
          state().templateMeta[id] = { categoryId: input.categoryId === undefined ? (prev?.categoryId ?? null) : input.categoryId, typeIds: input.typeIds ?? prev?.typeIds ?? (input.typeId ? [input.typeId] : []), language: input.language === undefined ? (prev?.language ?? null) : input.language, isActive: input.isActive ?? prev?.isActive ?? true, updatedAt: ctx.now() };
          return templateFields(t);
        },
        getUsers: async (q) => (await a.getUsers(q)).map(user),
        createUser: async (input) => {
          const created = await a.createUser(input);
          saveUserExtra(created.id, input);
          return user(created);
        },
        updateUser: async (id, input) => {
          saveUserExtra(id, input);
          return user(await a.updateUser(id, input));
        },
        getEntities: async () => (await a.getEntities()).map(withEntityLink),
        createEntity: async (input) => {
          if (input.sharePointSiteUrl && !SITE_URL.test(input.sharePointSiteUrl.trim())) throw invalid('Use https://<tenant>.sharepoint.com/sites/<name>.', { sharePointSiteUrl: ['Not a SharePoint site URL'] });
          const e = await a.createEntity(input);
          state().entityLinks[e.id] = { sharePointSiteUrl: input.sharePointSiteUrl?.trim() || null, sharePointSiteId: null, managementGroupId: input.managementGroupId?.trim() || null, managementGroupName: null, link: NOT_LINKED };
          return withEntityLink(e);
        },
        updateEntity: async (id, input) => {
          const url = input.sharePointSiteUrl?.trim() || null;
          const group = input.managementGroupId?.trim() || null;
          if (url && !SITE_URL.test(url)) throw invalid('Use https://<tenant>.sharepoint.com/sites/<name>.', { sharePointSiteUrl: ['Not a SharePoint site URL'] });
          if (group && !GUID.test(group)) throw invalid('The Entra group id is a GUID.', { managementGroupId: ['Not a GUID'] });
          const e = await a.updateEntity(id, input);
          const prev = entityLink(id);
          const changed = url !== prev.sharePointSiteUrl || group !== prev.managementGroupId;
          state().entityLinks[id] = changed ? { sharePointSiteUrl: url, sharePointSiteId: null, managementGroupId: group, managementGroupName: null, link: NOT_LINKED } : prev;
          return withEntityLink(e);
        },
        getAppSettings: async () => ({ ...(await a.getAppSettings()), counterpartyCreation: state().counterpartyCreation }),
        updateAppSettings: async (input) => {
          if (input.counterpartyCreation && !['AdminOnly', 'ContractOwners', 'AllUsers'].includes(input.counterpartyCreation)) throw invalid('Choose who may create counterparties.', { counterpartyCreation: ['Unknown value'] });
          const saved = await a.updateAppSettings(input);
          if (input.counterpartyCreation) state().counterpartyCreation = input.counterpartyCreation;
          return { ...saved, counterpartyCreation: state().counterpartyCreation };
        },
      },
    };
  }

  return { top, admin, decorate };
}

function addDaysIso(iso: string, days: number): string {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}
