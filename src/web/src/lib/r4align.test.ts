// Release 4 alignment with the real API (docs/REVIEW_ALIGNMENT_R4.md): the pure rules.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/api';
import { createHttpApi, userBody } from '../api/client';
import { createMockApi } from '../api/mock/mockApi';
import { GENERIC_INVALID } from '../api/mock/problems';
import { DEP, USER, departmentId } from '../api/mock/seed';
import { errorMessage, fieldError, fieldErrors, fieldKey, NO_ACCESS_MESSAGE, SESSION_ENDED_MESSAGE, validationMessage } from './errors';
import { chunkIdsForExport, EXPORT_URL_LIMIT, exportSelected, joinCsv, withoutCsvHeader } from './exportSelected';
import { canManageSignatureRequest, canRecordManualSignature, sendableDocuments, sendForSignatureOffer, statusAllowsSending } from './signature';
import { intakeUploadError, uploadError } from './uploads';
import { emptyDraft, validateDraft } from './validation';

const TODAY = '2026-11-22';
/** A 400 exactly as the API writes it: generic `detail`, the sentence in `errors`. */
const api400 = (errors: Record<string, string[]>) => new ApiError({ type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1', title: 'Validation failed', status: 400, detail: GENERIC_INVALID, errors });

describe('M1: the real validation sentence of a 400', () => {
  it('shows the first message of the field instead of the generic detail', () => {
    const e = api400({ file: ['The logo contains scripts or links and cannot be used.'] });
    expect(errorMessage(e)).toBe('The logo contains scripts or links and cannot be used.');
    expect(e.message).toBe('The logo contains scripts or links and cannot be used.');
    expect(errorMessage(e)).not.toContain('One or more fields');
  });

  it('joins the first message of each field, without repeats, and ends each with a full stop', () => {
    const e = api400({ name: ['Name is required.', 'ignored'], country: ['Use two letters'], city: ['Use two letters'] });
    expect(validationMessage(e)).toBe('Name is required. Use two letters.');
    const many = api400({ a: ['One.'], b: ['Two.'], c: ['Three.'], d: ['Four.'], e: ['Five.'] });
    expect(errorMessage(many)).toBe('One. Two. Three. (and 2 more)');
  });

  it('falls back to the detail when a 400 names no field, and keeps the detail of other statuses', () => {
    expect(errorMessage(new ApiError({ title: 'Feature off', status: 400, detail: 'Templates are turned off in Settings.' }))).toBe('Templates are turned off in Settings.');
    expect(errorMessage(new ApiError({ title: 'Conflict', status: 409, detail: 'Only a draft can be submitted.', errors: { x: ['no'] } }))).toBe('Only a draft can be submitted.');
    expect(errorMessage(new ApiError({ title: 'Unauthorized', status: 401 }))).toBe(SESSION_ENDED_MESSAGE);
    expect(NO_ACCESS_MESSAGE).toBe('Your account does not have access to DAFA Contracts. Contact an administrator.');
  });

  it('maps the API field names onto the form field names', () => {
    expect(fieldKey('signers[0].userId')).toBe('signers.0.userId');
    expect(fieldKey('signers.0.userId')).toBe('signers.0.userId');
    expect(fieldKey('$.signers[2].email')).toBe('signers.2.email');
    expect(fieldKey('Signers[1].UserId')).toBe('signers.1.userId');
    expect(fieldKey('$.entityId')).toBe('entityId');
    expect(fieldKey('cf.SupplierRating')).toBe('cf.SupplierRating');
    const e = api400({ supplierId: ['Choose a counterparty from the list.'], 'signers[1].email': ['Enter a valid e-mail address.'] });
    expect(fieldErrors(e)).toEqual({ supplierId: 'Choose a counterparty from the list.', 'signers.1.email': 'Enter a valid e-mail address.' });
    expect(fieldErrors(e, { supplierId: 'counterpartyName' })).toHaveProperty('counterpartyName', 'Choose a counterparty from the list.');
    expect(fieldError(e, 'signers.1.email')).toBe('Enter a valid e-mail address.');
    expect(fieldErrors(new Error('x'))).toEqual({});
  });

  it('the mock answers a 400 as the API does: generic detail, sentence at the field', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.pet });
    const e = await api.admin.createTemplate({ name: 'X', description: '', typeId: null }, new File(['x'], 'x.dotx')).catch((x: unknown) => x);
    expect(e).toBeInstanceOf(ApiError);
    expect((e as ApiError).problem.detail).toBe(GENERIC_INVALID);
    expect((e as ApiError).problem.errors).toEqual({ file: ['A template must be a Word document (.docx).'] });
    expect(errorMessage(e)).toBe('A template must be a Word document (.docx).');
  });
});

describe('M2: signer row errors', () => {
  it('the mock names signer fields as the API does and the form finds them', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.ras });
    const c = api.db.contracts.find((x) => x.status === 'Approved')!;
    const doc = api.db.documents.find((d) => d.contractId === c.id && d.kind === 'Main')!;
    const e = await api.createSignatureRequest(c.id, { documentIds: [doc.id], order: 'Sequential', message: '', signers: [{ kind: 'External', name: 'Klaus', email: 'nope', order: 1 }, { kind: 'Internal', userId: null, order: 2 }] }).catch((x: unknown) => x);
    expect(Object.keys((e as ApiError).problem.errors ?? {})).toEqual(['signers[0].email', 'signers[1].userId']);
    expect(fieldErrors(e)).toEqual({ 'signers.0.email': 'Enter a valid e-mail address.', 'signers.1.userId': 'Choose an active colleague.' });
  });
});

describe('H2: counterparty on Edit details', () => {
  const base = emptyDraft({ title: 'T', counterpartyName: 'Old name without a master record' });
  it('a new contract, and a contract that has a counterparty, needs one from the list', () => {
    expect(validateDraft(base).counterpartyName).toBe('Pick the counterparty from the list');
    expect(validateDraft({ ...base, counterpartyName: '' }).counterpartyName).toBe('Choose the counterparty');
    expect(validateDraft(base, [], { customFields: [], fieldRules: {} as never, enforceRequired: false, counterpartyOptional: false }).counterpartyName).toBeDefined();
  });
  it('a contract without a master counterparty can be saved without one', () => {
    expect(validateDraft(base, [], { customFields: [], fieldRules: {} as never, enforceRequired: false, counterpartyOptional: true }).counterpartyName).toBeUndefined();
  });
});

describe('M3 and L3: file types', () => {
  it('New contract reads PDF and .docx only; Documents keeps the wide list', () => {
    for (const name of ['a.doc', 'a.xlsx', 'a.msg', 'a.png', 'a']) {
      expect(intakeUploadError({ name, size: 10 })).toMatch(/Only PDF and Word \(\.docx\) files can be read here/);
    }
    expect(intakeUploadError({ name: 'a.PDF', size: 10 })).toBeUndefined();
    expect(intakeUploadError({ name: 'a.docx', size: 10 })).toBeUndefined();
    expect(intakeUploadError({ name: 'a.pdf', size: 0 })).toBe('The file is empty');
    for (const name of ['a.doc', 'a.xlsx', 'a.msg', 'a.png']) expect(uploadError({ name, size: 10 })).toBeUndefined();
  });
  it('the mock refuses a .doc on intake/extract like the API', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.jon });
    await expect(api.extract(new File(['x'], 'old.doc'))).rejects.toMatchObject({ status: 400 });
  });
});

describe('M4, M5, L1, L6: who is offered which signature action', () => {
  const me = { id: 'u1', name: 'Me', roles: ['ContractOwner'] as never[], canSendForSignature: false, signatureEntityIds: [] as string[] };
  it('M4: Record manual signature needs edit rights as well as being owner, delegate or admin', () => {
    expect(canRecordManualSignature({ ownerId: 'u1', delegateId: null, canEdit: true }, me)).toBe(true);
    expect(canRecordManualSignature({ ownerId: 'u1', delegateId: null, canEdit: false }, me)).toBe(false);
    expect(canRecordManualSignature({ ownerId: 'x', delegateId: 'u1', canEdit: false }, me)).toBe(false);
    expect(canRecordManualSignature({ ownerId: 'x', delegateId: null, canEdit: true }, me)).toBe(false);
    expect(canRecordManualSignature({ ownerId: 'x', delegateId: null, canEdit: true }, { ...me, roles: ['Admin'] as never[] })).toBe(true);
  });
  it('M5: requireApproved=false offers Send for signature on drafts and active contracts', () => {
    expect(statusAllowsSending('Approved', true)).toBe(true);
    expect(statusAllowsSending('Draft', true)).toBe(false);
    expect(statusAllowsSending('Draft', undefined)).toBe(false);
    expect(statusAllowsSending('Draft', false)).toBe(true);
    expect(statusAllowsSending('Active', false)).toBe(true);
    expect(statusAllowsSending('InApproval', false)).toBe(false);
    expect(statusAllowsSending('Expired', false)).toBe(false);
    const on = { enabled: true, requireApproved: true };
    const open = { enabled: true, requireApproved: false };
    expect(sendForSignatureOffer({ status: 'Draft', signature: null, canSendForSignature: false }, on)).toBeNull();
    expect(sendForSignatureOffer({ status: 'Draft', signature: null, canSendForSignature: false }, open)).toBe('ask');
    expect(sendForSignatureOffer({ status: 'Active', signature: null, canSendForSignature: true }, open)).toBe('send');
    // An API that does not report `requireApproved` still says so through `canSendForSignature`.
    expect(sendForSignatureOffer({ status: 'Draft', signature: null, canSendForSignature: true }, { enabled: true })).toBe('send');
    expect(sendForSignatureOffer({ status: 'Approved', signature: null, canSendForSignature: false }, on)).toBe('ask');
    expect(sendForSignatureOffer({ status: 'Approved', signature: { status: 'Sent' } as never, canSendForSignature: true }, on)).toBeNull();
    expect(sendForSignatureOffer({ status: 'Approved', signature: null, canSendForSignature: true }, { enabled: false })).toBeNull();
    expect(sendForSignatureOffer({ status: 'Approved', signature: null, canSendForSignature: true }, undefined)).toBeNull();
  });
  it('L1: resend, cancel and simulate are for the sender, colleagues with the signing right for the entity, and admins', () => {
    const r = { createdById: 'sender', createdBy: 'Sender' };
    const c = { entityId: 'e1' };
    expect(canManageSignatureRequest(r, c, me)).toBe(false);
    expect(canManageSignatureRequest(r, c, { ...me, id: 'sender' })).toBe(true);
    expect(canManageSignatureRequest({ createdBy: 'Me' }, c, me)).toBe(true);
    expect(canManageSignatureRequest(r, c, { ...me, roles: ['Admin'] as never[] })).toBe(true);
    expect(canManageSignatureRequest(r, c, { ...me, canSendForSignature: true })).toBe(true);
    expect(canManageSignatureRequest(r, c, { ...me, canSendForSignature: true, signatureEntityIds: ['e2'] })).toBe(false);
    expect(canManageSignatureRequest(r, c, { ...me, canSendForSignature: true, signatureEntityIds: ['e1'] })).toBe(true);
  });
  it('L6: the approval log is not a sendable document', () => {
    expect(sendableDocuments([{ kind: 'Main' }, { kind: 'ApprovalLog' }, { kind: 'Appendix' }] as never[]).map((d: { kind: string }) => d.kind)).toEqual(['Main', 'Appendix']);
  });
});

describe('H1: the mock clears a department link when the fields are left out, like the API', () => {
  it('a name-only update unlinks; an update that repeats the fields keeps the link', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.pet });
    const id = departmentId(DEP.purchasing);
    const before = (await api.admin.getDepartmentsV4()).find((d) => d.id === id)!;
    expect(before).toMatchObject({ documentLibrary: 'Purchasing contracts', link: { state: 'Linked' } });
    const kept = await api.admin.updateDepartment(id, { name: 'Purchasing', documentLibrary: before.documentLibrary, groupId: before.groupId } as never);
    expect(kept).toMatchObject({ documentLibrary: 'Purchasing contracts', groupId: before.groupId, link: { state: 'Linked' } });
    const wiped = await api.admin.updateDepartmentV3(id, { name: 'Purchasing', isActive: true });
    expect(wiped).toMatchObject({ documentLibrary: null, groupId: null, link: { state: 'NotLinked' } });
  });
});

describe('Needs a live check: made safe from the client', () => {
  afterEach(() => vi.unstubAllGlobals());
  const stubFetch = () => {
    const calls: { url: string; init: RequestInit }[] = [];
    vi.stubGlobal('fetch', (url: string, init: RequestInit) => {
      calls.push({ url, init });
      return Promise.resolve(new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } }));
    });
    return calls;
  };

  it('a user without a home legal entity is sent as entityId "" and never as JSON null', async () => {
    expect(userBody({ name: 'A', entityId: null })).toEqual({ name: 'A', entityId: '' });
    expect(userBody({ name: 'A', entityId: 'e1' })).toEqual({ name: 'A', entityId: 'e1' });
    expect(userBody({ name: 'A' })).toEqual({ name: 'A' });
    const calls = stubFetch();
    const api = createHttpApi();
    await api.admin.updateUser('u1', { name: 'A', entityId: null } as never);
    await api.admin.createUser({ name: 'B', entityId: null } as never);
    expect(calls.map((c) => [c.init.method, c.url, c.init.body])).toEqual([
      ['PUT', '/api/admin/users/u1', '{"name":"A","entityId":""}'],
      ['POST', '/api/admin/users', '{"name":"B","entityId":""}'],
    ]);
  });

  it('the mock clears the home entity for "" as for null', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.pet });
    const list = async () => await api.admin.getUsers();
    const u = (await list()).find((x) => x.entityId)!;
    const input = { name: u.name, email: u.email, title: u.title, roles: u.roles, departmentIds: u.departmentIds, managedEntityIds: u.managedEntityIds, isGroupManagement: u.isGroupManagement, isActive: u.isActive };
    await api.admin.updateUser(u.id, { ...input, entityId: '' } as never);
    expect((await list()).find((x) => x.id === u.id)!.entityId ?? null).toBeNull();
  });

  it('a template upload is one multipart POST to admin/templates (the action that consumes multipart/form-data)', async () => {
    const calls = stubFetch();
    const api = createHttpApi();
    await api.admin.createTemplate({ name: 'NDA', description: 'Short', typeId: null, categoryId: 'c1', typeIds: ['t1', 't2'], language: 'en' }, new File(['x'], 'nda.docx'));
    expect(calls).toHaveLength(1);
    const { url, init } = calls[0]!;
    expect(url).toBe('/api/admin/templates');
    expect(init.method).toBe('POST');
    // No Content-Type of our own: the browser adds multipart/form-data with the boundary.
    expect(Object.keys(init.headers as Record<string, string>).map((h) => h.toLowerCase())).not.toContain('content-type');
    const form = init.body as FormData;
    expect(form).toBeInstanceOf(FormData);
    expect((form.get('file') as File).name).toBe('nda.docx');
    expect(form.get('name')).toBe('NDA');
    expect(form.get('categoryId')).toBe('c1');
    expect(form.getAll('typeIds')).toEqual(['t1', 't2']);
    expect(form.get('language')).toBe('en');
    expect(form.has('typeId')).toBe(false);
  });

  it('Export selected keeps every URL under 2000 characters and returns one CSV', async () => {
    const columns = ['contract', 'contractNo', 'counterparty', 'entity', 'department', 'owner', 'notice', 'end', 'annualValue', 'totalValue', 'status', 'tags'];
    const ids = Array.from({ length: 120 }, (_, i) => `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`);
    const origin = 'https://dafa-contracts.example.com'.length;
    const chunks = chunkIdsForExport(ids, columns, origin);
    expect(chunks.flat()).toEqual(ids);
    expect(chunks.length).toBeGreaterThan(1);
    for (const chunk of chunks) {
      const url = `https://dafa-contracts.example.com/api/contracts/export?${new URLSearchParams([...chunk.map((id) => ['id', id]), ...columns.map((c) => ['columns', c])]).toString()}`;
      expect(url.length).toBeLessThanOrEqual(EXPORT_URL_LIMIT);
    }
    expect(chunkIdsForExport(ids.slice(0, 3), columns, origin)).toHaveLength(1);
    expect(chunkIdsForExport([], columns)).toEqual([]);

    expect(withoutCsvHeader('a,b\r\n1,2\r\n')).toBe('1,2\r\n');
    expect(withoutCsvHeader('"a\nx",b\n1,2')).toBe('1,2');
    expect(joinCsv(['\ufeffa,b\r\n1,2\r\n', '\ufeffa,b\r\n3,4', '\ufeffa,b\r\n'])).toBe('\ufeffa,b\r\n1,2\r\n3,4\r\n');
  });

  it('the mock refuses an over-long export URL like the web server, and exportSelected gets every selected row', async () => {
    const api = createMockApi({ today: TODAY, userId: USER.pet });
    const real = api.db.contracts.map((c) => c.id);
    // Pad the selection with ids that match nothing, to the size of a large selection.
    const ids = [...real, ...Array.from({ length: 80 }, (_, i) => `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`)];
    const columns = ['contract', 'contractNo', 'status'];
    await expect(api.exportContracts({ id: ids, columns } as never)).rejects.toMatchObject({ status: 404 });
    const file = await exportSelected(api, ids, columns);
    const lines = (await file.blob.text()).replace('\ufeff', '').trim().split('\r\n');
    expect(lines).toHaveLength(real.length + 1);
    expect(lines.filter((l) => l === lines[0])).toHaveLength(1);
  });
});
