import { describe, expect, it } from 'vitest';
import { ApiError } from '../api';
import type { ContractInput, Id } from '../types';
import { canSee } from './access';
import { createMockApi, type MockApi } from './mockApi';
import { DEP, ENT, TYP, USER, departmentId, entityId, typeId } from './seed';
const SUPPLIER_1 = '00000005-0000-4000-8000-000000000001';

const TODAY = '2026-11-22';
const as = (userId: Id): MockApi => createMockApi({ today: TODAY, userId });

async function allVisible(api: MockApi) {
  return (await api.getContracts({ pageSize: 200 })).items;
}
const idOf = (api: MockApi, title: string): Id => {
  const c = api.db.contracts.find((x) => x.title === title);
  if (!c) throw new Error(`No seeded contract "${title}"`);
  return c.id;
};
async function status(p: Promise<unknown>): Promise<number> {
  try {
    await p;
    return 200;
  } catch (e) {
    if (e instanceof ApiError) return e.status;
    throw e;
  }
}

function newContract(overrides: Partial<ContractInput> = {}): ContractInput {
  return {
    title: 'Test supply agreement',
    typeId: typeId(TYP.direct),
    supplierId: SUPPLIER_1,
    counterpartyName: 'Test Supplier ApS',
    entityId: entityId(ENT.dafa),
    departmentId: departmentId(DEP.purchasing),
    ownerId: USER.jon,
    delegateId: null,
    responsibleId: null,
    reminderRecipient: 'Owner',
    startDate: '2027-01-01',
    endDate: '2028-12-31',
    noticePeriodDays: 90,
    autoRenew: true,
    renewalMonths: 12,
    annualValue: 400_000,
    totalValue: null,
    currency: 'DKK',
    paymentTerms: null,
    priceAdjustment: null,
    priceAdjustmentDate: null,
    governingLaw: null,
    liabilityCap: null,
    exclusivity: null,
    changeOfControl: false,
    summary: null,
    ...overrides,
  };
}

describe('seed data', () => {
  const api = as(USER.ras);
  it('has the 9 legal entities, 13 departments and 10 contract types from the brief', async () => {
    const lookups = await api.getLookups();
    expect(lookups.entities).toHaveLength(9);
    expect(lookups.departments).toHaveLength(13);
    expect(lookups.contractTypes).toHaveLength(10);
    expect(lookups.entities.map((e) => e.name)).toContain('DAFA Sealing Technology (Tianjin) Co., Ltd.');
    expect(lookups.users.length).toBeGreaterThanOrEqual(8);
  });
  it('has about 40 contracts with unique contract numbers', () => {
    expect(api.db.contracts.length).toBeGreaterThanOrEqual(40);
    const numbers = api.db.contracts.map((c) => c.contractNo);
    expect(new Set(numbers).size).toBe(numbers.length);
    expect(numbers.every((n) => /^C-\d{4}-\d{4}$/.test(n))).toBe(true);
  });
  it('reproduces the design example: 23 days to the notice deadline on 15 Dec 2026', async () => {
    const c = await api.getContract(idOf(api, 'Raw material supply – rubber compounds'));
    expect(c.contractNo).toBe('C-2024-0187');
    expect(c.noticeDeadline).toBe('2026-12-15');
    expect(c.daysToNotice).toBe(23);
    expect(c.endDate).toBe('2027-02-28');
    expect(c.reminders.filter((r) => r.sentAt).map((r) => r.kind)).toEqual(['Notice120', 'Notice60', 'Notice30']);
    expect(c.reminders.find((r) => r.kind === 'Escalation')).toMatchObject({ dueDate: '2026-12-01', sentAt: null });
    expect(c.nextReminderDate).toBe('2026-12-01');
  });
});

describe('access model', () => {
  it('group management sees every contract', async () => {
    const api = as(USER.ras);
    expect(await allVisible(api)).toHaveLength(api.db.contracts.length);
  });

  it('an admin sees every contract', async () => {
    const api = as(USER.pet);
    expect(await allVisible(api)).toHaveLength(api.db.contracts.length);
  });

  it('a department member sees the department across all legal entities, plus contracts they are on', async () => {
    const api = as(USER.jon);
    const items = await allVisible(api);
    expect(items.length).toBeGreaterThan(5);
    expect(items.length).toBeLessThan(api.db.contracts.length);
    for (const item of items) {
      const c = api.db.contracts.find((x) => x.id === item.id)!;
      const onIt = [c.ownerId, c.delegateId, c.responsibleId].includes(USER.jon);
      expect(c.departmentId === departmentId(DEP.purchasing) || onIt).toBe(true);
    }
    // Purchasing cuts across entities.
    expect(new Set(items.map((i) => i.entityName)).size).toBeGreaterThan(3);
    // Nothing from IT or Finance.
    expect(items.some((i) => i.departmentName === 'IT' || i.departmentName === 'Finance')).toBe(false);
  });

  it('entity management sees everything in the managed entity', async () => {
    const api = as(USER.ann);
    const items = await allVisible(api);
    const sverige = api.db.contracts.filter((c) => c.entityId === entityId(ENT.sverige));
    expect(sverige.length).toBeGreaterThan(1);
    for (const c of sverige) expect(items.some((i) => i.id === c.id)).toBe(true);
    expect(items.every((i) => i.entityName === 'DAFA Sverige AB')).toBe(true);
  });

  it('a person on the contract sees it even outside their department', async () => {
    const api = as(USER.emm); // Quality; responsible on the rubber supplier quality audit contract
    const items = await allVisible(api);
    const quality = items.find((i) => i.title === 'Quality agreement – Nordpolymer');
    expect(quality).toBeDefined();
    expect(items.some((i) => i.title === 'Raw material supply – rubber compounds')).toBe(false);
  });

  it('an approver without departments sees only contracts with an open step of theirs', async () => {
    const api = as(USER.lou); // Legal
    const items = await allVisible(api);
    expect(items.map((i) => i.title)).toEqual(['ERP software subscription']);
  });

  it('hides a contract again once the approval step is closed', async () => {
    const api = as(USER.lou);
    const [mine] = await api.getMyApprovals();
    expect(mine?.contract.title).toBe('ERP software subscription');
    await api.decideApprovalStep(mine!.stepId, { decision: 'Approve', comment: '' });
    expect(await allVisible(api)).toHaveLength(0);
    expect(await status(api.getContract(mine!.contract.id))).toBe(404);
  });

  it('answers 404 for detail, documents, obligations and export of contracts the user cannot see', async () => {
    const api = as(USER.emm);
    const erp = idOf(api, 'ERP software subscription');
    const doc = api.db.documents.find((d) => d.contractId === erp)!;
    expect(await status(api.getContract(erp))).toBe(404);
    expect(await status(api.getDocumentContent(doc.id))).toBe(404);
    expect(await status(api.getObligations(erp))).toBe(404);
    const csv = await (await api.exportContracts()).blob.text();
    expect(csv).not.toContain('ERP software subscription');
    expect(csv).toContain('Quality agreement');
  });

  it('search, renewals and home only use visible contracts', async () => {
    const api = as(USER.emm);
    // Suppliers are master data everyone may search; contracts are access-filtered.
    expect((await api.search('ERP')).filter((r) => r.kind === 'Contract')).toEqual([]);
    const all = as(USER.ras);
    expect((await all.search('ERP')).some((r) => r.kind === 'Contract')).toBe(true);
    const renewals = await api.getRenewals(12);
    const visibleIds = new Set((await allVisible(api)).map((i) => i.id));
    expect(renewals.every((r) => visibleIds.has(r.id))).toBe(true);
    expect(renewals.length).toBeLessThan((await all.getRenewals(12)).length);
  });

  it('does not leak linked contracts the user cannot see', async () => {
    const reader = as(USER.emm);
    const quality = await reader.getContract(idOf(reader, 'Quality agreement – Nordpolymer'));
    expect(quality.links).toEqual([]);
    const cfo = as(USER.ras);
    const same = await cfo.getContract(quality.id);
    expect(same.links.map((l) => l.title)).toEqual(['Raw material supply – rubber compounds']);
    expect(same.links[0]?.direction).toBe('in');
  });

  it('only lets the people on the contract or an admin edit it', async () => {
    const title = 'Raw material supply – rubber compounds';
    const member = as(USER.jon); // responsible on the contract
    const detail = await member.getContract(idOf(member, title));
    expect(detail.canEdit).toBe(true);
    expect(detail.canDecideRenewal).toBe(true);

    const cfo = as(USER.ras); // sees it, but is not on it
    const seen = await cfo.getContract(detail.id);
    expect(seen.canEdit).toBe(false);
    expect(await status(cfo.updateContract(detail.id, newContract()))).toBe(403);
    expect(await status(cfo.decideRenewal(detail.id, { decision: 'Renew', comment: '' }))).toBe(403);

    const adminApi = as(USER.pet);
    expect((await adminApi.getContract(detail.id)).canEdit).toBe(true);
  });

  it('readers cannot create contracts', async () => {
    expect(await status(as(USER.emm).createContract(newContract()))).toBe(403);
    expect(await status(as(USER.jon).createContract(newContract()))).toBe(200);
  });

  it('keeps Settings to administrators; Overview is open to everyone (release 3)', async () => {
    expect(await status(as(USER.jon).getOverview())).toBe(200);
    expect(await status(as(USER.ann).getOverview())).toBe(200);
    expect(await status(as(USER.ras).getOverview())).toBe(200);
    expect(await status(as(USER.ras).admin.getApprovalRules())).toBe(403);
    expect(await status(as(USER.pet).admin.getApprovalRules())).toBe(200);
    expect(await status(as(USER.jon).admin.getAudit())).toBe(403);
  });

  it('switching demo user changes what the same API instance returns', async () => {
    const api = as(USER.ras);
    const before = (await allVisible(api)).length;
    await api.devLogin(USER.emm);
    expect((await api.getMe()).name).toBe('Emma Nygaard');
    expect((await allVisible(api)).length).toBeLessThan(before);
    await api.logout();
    expect(await status(api.getMe())).toBe(401);
  });

  it('canSee matches the five rules one by one', () => {
    const api = as(USER.ras);
    const c = api.db.contracts.find((x) => x.title === 'Warehouse lease – Jönköping')!;
    const user = (id: Id) => api.db.users.find((u) => u.id === id)!;
    expect(canSee(user(USER.tho), c, [])).toBe(true); // department + responsible
    expect(canSee(user(USER.ann), c, [])).toBe(true); // entity management + owner
    expect(canSee(user(USER.kar), c, [])).toBe(true); // group management
    expect(canSee(user(USER.pet), c, [])).toBe(true); // admin
    expect(canSee(user(USER.met), c, [])).toBe(false); // Purchasing, not on it
    expect(canSee(user(USER.lou), c, [])).toBe(false);
  });
});

describe('contract lifecycle', () => {
  it('creates a draft, submits it, routes by value band and activates it after signing', async () => {
    const api = as(USER.jon);
    const draft = await api.createContract(newContract({ annualValue: null, totalValue: 2_400_000 }));
    expect(draft.status).toBe('Draft');
    expect(draft.contractNo).toMatch(/^C-2026-\d{4}$/);

    const preview = await api.previewApproval({ entityId: draft.entityId, typeId: draft.typeId, departmentId: draft.departmentId, value: 2_400_000 });
    expect(preview.stages.map((s) => s.approvers)).toEqual([['Department head · Mette Kjær'], ['Group CFO · Rasmus Holm']]);

    const submitted = await api.submitContract(draft.id);
    expect(submitted.status).toBe('InApproval');
    expect(submitted.approval?.stages).toHaveLength(2);
    expect(submitted.approval?.stages[0]?.steps[0]).toMatchObject({ approverName: 'Mette Kjær', status: 'Pending' });
    expect(submitted.approval?.stages[1]?.steps[0]?.status).toBe('Waiting');
    expect(await status(api.updateContract(draft.id, newContract()))).toBe(409);

    await api.devLogin(USER.met);
    const metStep = (await api.getMyApprovals()).find((a) => a.contract.id === draft.id)!;
    await api.decideApprovalStep(metStep.stepId, { decision: 'Approve', comment: 'OK' });

    await api.devLogin(USER.ras);
    const rasStep = (await api.getMyApprovals()).find((a) => a.contract.id === draft.id)!;
    expect(rasStep.instance.currentStage).toBe(2);
    await api.decideApprovalStep(rasStep.stepId, { decision: 'Approve', comment: '' });

    await api.devLogin(USER.jon);
    const approved = await api.getContract(draft.id);
    expect(approved.status).toBe('Approved');
    expect(approved.documents.some((d) => d.kind === 'ApprovalLog')).toBe(true);
    const log = await api.getDocumentContent(approved.documents.find((d) => d.kind === 'ApprovalLog')!.id);
    expect(await log.blob.text()).toMatch(/^%PDF-1\.4/);

    expect(await status(api.signContract(draft.id, { signedDate: '2027-01-01', method: 'Wet ink' }))).toBe(400);
    const active = await api.signContract(draft.id, { signedDate: TODAY, method: 'Signed by both parties' });
    expect(active.status).toBe('Active');
    expect(active.signedDate).toBe(TODAY);
  });

  it('a rejection needs a comment and returns the contract to draft', async () => {
    const api = as(USER.met);
    const mine = (await api.getMyApprovals()).find((a) => a.contract.title === 'Stretch film supply – 2027')!;
    expect(await status(api.decideApprovalStep(mine.stepId, { decision: 'Reject', comment: ' ' }))).toBe(400);
    await api.decideApprovalStep(mine.stepId, { decision: 'Reject', comment: 'Payment terms are too short.' });
    const c = await api.getContract(mine.contract.id);
    expect(c.status).toBe('Draft');
    expect(c.approval?.status).toBe('Rejected');
    expect(c.activity[0]?.text).toContain('Payment terms are too short.');
  });

  it('parallel approvers must all approve before the next stage opens', async () => {
    const api = as(USER.ras);
    const erp = idOf(api, 'ERP software subscription');
    const mine = (await api.getMyApprovals()).find((a) => a.contract.id === erp)!;
    await api.decideApprovalStep(mine.stepId, { decision: 'Approve', comment: '' });
    let approval = (await api.getContract(erp)).approval!;
    expect(approval.currentStage).toBe(2); // Legal still pending
    expect(await status(api.decideApprovalStep(mine.stepId, { decision: 'Approve', comment: '' }))).toBe(409);

    await api.devLogin(USER.lou);
    const legal = (await api.getMyApprovals())[0]!;
    await api.decideApprovalStep(legal.stepId, { decision: 'Approve', comment: '' });
    await api.devLogin(USER.ras);
    approval = (await api.getContract(erp)).approval!;
    expect(approval.currentStage).toBe(3);
    expect(approval.stages[2]?.steps[0]).toMatchObject({ approverName: 'Karen Lindberg', status: 'Pending' });
  });

  it('delegating hands the step to another user', async () => {
    const api = as(USER.ras);
    const mine = (await api.getMyApprovals())[0]!;
    expect(await status(api.decideApprovalStep('missing', { decision: 'Approve', comment: '' }))).toBe(404);
    await api.delegateApprovalStep(mine.stepId, USER.kar);
    expect((await api.getMyApprovals()).some((a) => a.contract.id === mine.contract.id)).toBe(false);
    await api.devLogin(USER.kar);
    const delegated = (await api.getMyApprovals()).find((a) => a.contract.id === mine.contract.id)!;
    const step = delegated.instance.stages.flatMap((s) => s.steps).find((s) => s.id === delegated.stepId)!;
    expect(step.roleLabel).toBe('Group CFO (delegated by Rasmus Holm)');
  });

  it('active holiday cover reroutes a new step to the deputy', async () => {
    const api = as(USER.pet);
    await api.admin.updateHolidayCover([{ userId: USER.met, deputyId: USER.hen, from: '2026-11-20', to: '2026-11-30' }]);
    await api.devLogin(USER.jon);
    const draft = await api.createContract(newContract());
    const submitted = await api.submitContract(draft.id);
    expect(submitted.approval?.stages[0]?.steps[0]).toMatchObject({ approverName: 'Henrik Madsen', roleLabel: 'Department head (cover for Mette Kjær)' });
  });

  it('rejects a contract body that misses required fields', async () => {
    const api = as(USER.jon);
    const p = api.createContract(newContract({ title: ' ', annualValue: null, totalValue: null, endDate: '2026-01-01' }));
    await expect(p).rejects.toMatchObject({ status: 400 });
    const errors = await p.catch((e: ApiError) => e.problem.errors);
    expect(Object.keys(errors ?? {})).toEqual(expect.arrayContaining(['title', 'annualValue', 'endDate']));
  });
});

describe('renewal decisions', () => {
  const title = 'Raw material supply – rubber compounds';

  it('Renew on an auto-renew contract rolls the end date forward and clears the decision', async () => {
    const api = as(USER.met);
    const after = await api.decideRenewal(idOf(api, title), { decision: 'Renew', comment: '' });
    expect(after.endDate).toBe('2028-02-28');
    expect(after.noticeDeadline).toBe('2027-12-15');
    expect(after.renewalDecision).toBeNull();
    expect(after.status).toBe('Active');
  });

  it('Give notice needs a comment, sets the status and stops reminders', async () => {
    const api = as(USER.met);
    const id = idOf(api, title);
    expect(await status(api.decideRenewal(id, { decision: 'GiveNotice', comment: '' }))).toBe(400);
    const after = await api.decideRenewal(id, { decision: 'GiveNotice', comment: 'Moving volume to another supplier.' });
    expect(after.status).toBe('NoticeGiven');
    expect(after.renewalDecision).toBe('GiveNotice');
    expect(after.renewalDecidedBy).toBe('Mette Kjær');
    expect(after.reminders.every((r) => r.sentAt !== null)).toBe(true);
    expect(after.nextReminderDate).toBeNull();
    expect(after.canDecideRenewal).toBe(false);
  });

  it('a recorded decision removes the renewal task from Home', async () => {
    const api = as(USER.met);
    const id = idOf(api, title);
    expect((await api.getHome()).tasks.some((t) => t.kind === 'Renewal' && t.contractId === id)).toBe(true);
    await api.decideRenewal(id, { decision: 'Renegotiate', comment: 'Asking for a lower index.' });
    expect((await api.getHome()).tasks.some((t) => t.kind === 'Renewal' && t.contractId === id)).toBe(false);
  });
});

describe('obligations, links and documents', () => {
  it('creates, updates, completes and deletes obligations', async () => {
    const api = as(USER.tho);
    const id = idOf(api, 'Freight framework – Nordics');
    const created = await api.createObligation(id, { description: 'Quarterly service review', dueDate: '2027-01-15', frequencyMonths: 3, responsibleId: USER.tho });
    expect(await api.getObligations(id)).toHaveLength(2);
    const updated = await api.updateObligation(created.id, { description: 'Quarterly review', dueDate: '2027-01-31', frequencyMonths: 3, responsibleId: USER.tho });
    expect(updated.description).toBe('Quarterly review');
    // Recurring: completing moves the due date on and keeps it open.
    const done = await api.completeObligation(created.id);
    expect(done).toMatchObject({ status: 'Open', dueDate: '2027-04-30' });
    // One-off: completing closes it.
    const oneOff = await api.createObligation(id, { description: 'Send insurance certificate', dueDate: null, frequencyMonths: null, responsibleId: USER.tho });
    expect((await api.completeObligation(oneOff.id)).status).toBe('Done');
    expect(await status(api.completeObligation(oneOff.id))).toBe(409);
    await api.deleteObligation(created.id);
    expect((await api.getObligations(id)).some((o) => o.id === created.id)).toBe(false);
    expect(await status(api.createObligation(id, { description: '', dueDate: null, frequencyMonths: null, responsibleId: USER.tho }))).toBe(400);
  });

  it('links two contracts in both directions and removes the link', async () => {
    const api = as(USER.tho);
    const from = idOf(api, 'Freight framework – Nordics');
    const to = idOf(api, 'Customs brokerage services');
    const before = (await api.getContract(from)).links.length;
    const after = await api.createLink(from, { toContractId: to, kind: 'Related' });
    expect(after.links).toHaveLength(before + 1);
    const other = await api.getContract(to);
    expect(other.links[0]).toMatchObject({ contractId: from, direction: 'in', kind: 'Related' });
    expect(await status(api.createLink(from, { toContractId: to, kind: 'Related' }))).toBe(409);
    expect(await status(api.createLink(from, { toContractId: from, kind: 'Related' }))).toBe(400);
    await api.deleteLink(other.links[0]!.id);
    expect((await api.getContract(to)).links).toHaveLength(0);
  });

  it('uploads a document, versions a re-upload and serves the same bytes back', async () => {
    const api = as(USER.tho);
    const id = idOf(api, 'Freight framework – Nordics');
    const file = new File(['price list contents'], 'Price list 2027.pdf', { type: 'application/pdf' });
    const first = await api.uploadDocument(id, file, 'Appendix');
    const doc = first.documents.find((d) => d.name === 'Price list 2027.pdf')!;
    expect(doc).toMatchObject({ kind: 'Appendix', version: 1, uploadedBy: 'Thomas Riis' });
    const second = await api.uploadDocument(id, file, 'Appendix');
    expect(second.documents.filter((d) => d.name === 'Price list 2027.pdf').map((d) => d.version).sort()).toEqual([1, 2]);
    const content = await api.getDocumentContent(doc.id);
    expect(await content.blob.text()).toBe('price list contents');
    expect(content.fileName).toBe('Price list 2027.pdf');
  });
});

describe('intake', () => {
  it('extracts suggestions from an upload and attaches the file to the new draft', async () => {
    const api = as(USER.sor);
    const file = new File([new Uint8Array(260_000)], 'Hosting agreement – draft.pdf', { type: 'application/pdf' });
    const result = await api.extract(file);
    expect(result.pages).toBe(2);
    expect(result.suggestions.find((s) => s.field === 'title')?.value).toBe('Hosting agreement');
    expect(result.suggestions.find((s) => s.field === 'counterpartyName')?.value).toBe('Hosting Partner Nord A/S');
    expect(result.suggestions.find((s) => s.field === 'typeId')?.value).toBe(typeId(TYP.it));
    expect(result.deviations.length).toBeGreaterThan(0);

    const created = await api.createContract(newContract({ ownerId: USER.sor, departmentId: departmentId(DEP.it), uploadId: result.uploadId }));
    expect(created.documents.map((d) => d.name)).toEqual(['Hosting agreement – draft.pdf']);
    expect(created.deviations).toEqual(result.deviations);
    expect(created.summary).toBe(result.summary);
  });

  it('refuses files that are not PDF or Word', async () => {
    const api = as(USER.sor);
    expect(await status(api.extract(new File(['x'], 'photo.png', { type: 'image/png' })))).toBe(400);
  });

  it('fills a template and returns an upload for the draft', async () => {
    const api = as(USER.jon);
    const templates = await api.getTemplates();
    expect(templates.map((t) => t.name)).toEqual(['Supply agreement', 'Mutual NDA']);
    const result = await api.fromTemplate({ templateId: templates[0]!.id, fields: { counterparty: 'Test Supplier ApS', entity: 'DAFA A/S', title: 'Gaskets' } });
    expect(result.fileName).toBe('Supply agreement – Test Supplier ApS – draft.rtf');
    const created = await api.createContract(newContract({ uploadId: result.uploadId }));
    const content = await api.getDocumentContent(created.documents[0]!.id);
    const text = await content.blob.text();
    expect(text).toContain('Test Supplier ApS');
    expect(text).toContain('[startDate]');
  });
});

describe('home', () => {
  it('builds the CFO task list from approvals, renewals and obligations', async () => {
    const home = await as(USER.ras).getHome();
    expect(home.approvalsWaiting).toBe(1);
    expect(home.tasks.find((t) => t.kind === 'Approval')).toMatchObject({ title: 'ERP software subscription', pill: 'Your approval' });
    expect(home.tasks.find((t) => t.kind === 'Approval')?.meta).toContain('Legal approving in parallel');
    const renewal = home.tasks.find((t) => t.title === 'Group insurance programme');
    expect(renewal).toMatchObject({ kind: 'Renewal', pill: 'Notice in 27 days', urgency: 'high' });
    expect(home.renewalsThisWeek).toBe(1);
    expect(home.decisionsThisWeek).toBe(2);
    expect(home.tasks.some((t) => t.kind === 'Obligation' && t.pill === 'Due in 11 days')).toBe(true);
    expect(home.noticeByMonth.map((m) => m.month)).toEqual(['2026-11', '2026-12', '2027-01', '2027-02', '2027-03', '2027-04']);
    expect(home.recent).toHaveLength(3);
    expect(home.renewingIn90.count).toBeGreaterThan(3);
  });

  it('marking an obligation done from Home removes the task', async () => {
    const api = as(USER.ras);
    const task = (await api.getHome()).tasks.find((t) => t.kind === 'Obligation' && t.pill === 'Due in 11 days')!;
    await api.completeObligation(task.obligationId!);
    expect((await api.getHome()).tasks.some((t) => t.obligationId === task.obligationId && t.pill === 'Due in 11 days')).toBe(false);
  });

  it('gives a reader with nothing to do an empty task list', async () => {
    const api = as(USER.lou);
    await api.decideApprovalStep((await api.getMyApprovals())[0]!.stepId, { decision: 'Approve', comment: '' });
    expect((await api.getHome()).tasks).toEqual([]);
  });
});

describe('admin', () => {
  it('runs the reminder job idempotently and writes to the outbox', async () => {
    const api = as(USER.pet);
    const first = await api.admin.runJobs();
    expect(first.remindersCreated).toBe(0); // seeded history is already sent
    const before = api.db.reminders.length;
    await api.admin.updateReminderSettings({ noticeDays: [150, 120, 60, 30], escalationDays: 14, obligationDays: 14 });
    const second = await api.admin.runJobs();
    expect(second.remindersCreated).toBeGreaterThan(0);
    expect(api.db.reminders.length).toBe(before + second.remindersCreated + second.escalations + second.obligationReminders);
    expect((await api.admin.getOutbox()).length).toBeGreaterThanOrEqual(second.remindersCreated);
    const third = await api.admin.runJobs();
    expect(third.remindersCreated + third.escalations + third.obligationReminders).toBe(0);
  });

  it('supports CRUD on departments and protects the ones in use', async () => {
    const api = as(USER.pet);
    const legal = await api.admin.createDepartment({ name: 'Legal' });
    expect((await api.getLookups()).departments).toHaveLength(14);
    expect((await api.admin.updateDepartment(legal.id, { name: 'Legal and compliance' })).name).toBe('Legal and compliance');
    await api.admin.deleteDepartment(legal.id);
    expect(await status(api.admin.deleteDepartment(departmentId(DEP.purchasing)))).toBe(409);
    expect(await status(api.admin.createDepartment({ name: 'purchasing' }))).toBe(409);
  });

  it('a changed approval rule changes the previewed route', async () => {
    const api = as(USER.pet);
    const rules = await api.admin.getApprovalRules();
    const small = rules.find((r) => r.name === 'Up to DKK 1m')!;
    await api.admin.updateApprovalRule(small.id, { ...small, stages: [...small.stages, { order: 2, approvers: [{ userId: USER.kar, role: null }] }] });
    const preview = await api.previewApproval({ entityId: entityId(ENT.dafa), typeId: typeId(TYP.direct), departmentId: departmentId(DEP.purchasing), value: 100_000 });
    expect(preview.stages).toHaveLength(2);
    // The more specific NDA rule still wins for NDAs.
    const nda = await api.previewApproval({ typeId: typeId(TYP.nda), departmentId: departmentId(DEP.it), value: 0 });
    expect(nda.stages.map((s) => s.approvers)).toEqual([['Department head · Søren Dahl']]);
  });

  it('pages the audit log and filters it by contract', async () => {
    const api = as(USER.pet);
    const all = await api.admin.getAudit();
    expect(all.items).toHaveLength(50);
    expect(all.total).toBeGreaterThan(50);
    const id = idOf(api, 'Raw material supply – rubber compounds');
    const one = await api.admin.getAudit({ contractId: id });
    expect(one.items.every((a) => a.contractId === id)).toBe(true);
  });
});
