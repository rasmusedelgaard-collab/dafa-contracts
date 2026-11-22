import { describe, expect, it } from 'vitest';
import { ApiError } from '../api';
import type { ContractInput, Id } from '../types';
import { addDays } from '../../lib/dates';
import { createMockApi, type MockApi } from './mockApi';
import { DEP, ENT, TYP, USER, departmentId, entityId, typeId } from './seed';
const SUPPLIER_1 = '00000005-0000-4000-8000-000000000001';

const TODAY = '2026-11-22';
const as = (userId: Id, extra: Parameters<typeof createMockApi>[0] = {}): MockApi => createMockApi({ today: TODAY, userId, ...extra });
const ERP = 'ERP software subscription';

async function problem(p: Promise<unknown>): Promise<{ status: number; detail?: string }> {
  try {
    await p;
    return { status: 200 };
  } catch (e) {
    if (e instanceof ApiError) return { status: e.status, detail: e.problem.detail };
    throw e;
  }
}
const contractOf = (api: MockApi, title: string) => {
  const c = api.db.contracts.find((x) => x.title === title);
  if (!c) throw new Error(`No seeded contract "${title}"`);
  return c;
};
const approvalOf = (api: MockApi, title: string) => {
  const a = api.db.approvals.find((x) => x.contractId === contractOf(api, title).id);
  if (!a) throw new Error(`No approval for "${title}"`);
  return a;
};
const stepOf = (api: MockApi, title: string, userId: Id) => {
  const s = approvalOf(api, title).stages.flatMap((x) => x.steps).find((x) => x.approverId === userId && x.status !== 'Delegated');
  if (!s) throw new Error('No step');
  return s;
};

function input(overrides: Partial<ContractInput> = {}): ContractInput {
  return {
    title: 'Phase 2 test agreement', typeId: typeId(TYP.direct), supplierId: SUPPLIER_1, counterpartyName: 'Test Supplier ApS', entityId: entityId(ENT.dafa), departmentId: departmentId(DEP.purchasing),
    ownerId: USER.jon, delegateId: null, responsibleId: null, reminderRecipient: 'Owner', startDate: '2027-01-01', endDate: '2027-12-31', noticePeriodDays: 90, autoRenew: false, renewalMonths: null,
    annualValue: 400_000, totalValue: null, currency: 'DKK', paymentTerms: null, priceAdjustment: null, priceAdjustmentDate: null, governingLaw: null, liabilityCap: null, exclusivity: null, changeOfControl: false, summary: null,
    ...overrides,
  };
}

describe('approvals decision flow', () => {
  it('approve: the stage stays open until every parallel approver has approved, then the next stage opens', async () => {
    const api = as(USER.ras);
    const [mine] = await api.getMyApprovals();
    expect(mine?.contract.title).toBe(ERP);
    expect(mine?.waitingDays).toBe(2);
    await api.decideApprovalStep(mine!.stepId, { decision: 'Approve', comment: 'Within budget.' });

    const a = approvalOf(api, ERP);
    expect(stepOf(api, ERP, USER.ras)).toMatchObject({ status: 'Approved', comment: 'Within budget.' });
    expect(a.currentStage).toBe(2);
    expect(stepOf(api, ERP, USER.lou).status).toBe('Pending');
    expect(stepOf(api, ERP, USER.kar).status).toBe('Waiting');
    expect(await api.getMyApprovals()).toHaveLength(0);

    await api.devLogin(USER.lou);
    await api.decideApprovalStep(stepOf(api, ERP, USER.lou).id, { decision: 'Approve', comment: '' });
    expect(a.currentStage).toBe(3);
    expect(stepOf(api, ERP, USER.kar).status).toBe('Pending');
    expect(contractOf(api, ERP).status).toBe('InApproval');

    await api.devLogin(USER.kar);
    await api.decideApprovalStep(stepOf(api, ERP, USER.kar).id, { decision: 'Approve', comment: '' });
    expect(a.status).toBe('Approved');
    expect(contractOf(api, ERP).status).toBe('Approved');
    expect(api.db.documents.some((d) => d.contractId === a.contractId && d.kind === 'ApprovalLog')).toBe(true);
  });

  it('reject: needs a comment, returns the contract to Draft and tells the owner', async () => {
    const api = as(USER.ras);
    const stepId = stepOf(api, ERP, USER.ras).id;
    expect((await problem(api.decideApprovalStep(stepId, { decision: 'Reject', comment: '  ' }))).status).toBe(400);
    expect(approvalOf(api, ERP).status).toBe('Pending');

    await api.decideApprovalStep(stepId, { decision: 'Reject', comment: 'Liability cap is too low.' });
    expect(approvalOf(api, ERP).status).toBe('Rejected');
    expect(stepOf(api, ERP, USER.ras)).toMatchObject({ status: 'Rejected', comment: 'Liability cap is too low.' });
    expect(contractOf(api, ERP).status).toBe('Draft');
    expect(api.db.outbox.some((m) => m.toUserId === USER.sor && m.subject === `Rejected: ${ERP}`)).toBe(true);
    // The other approver has nothing left to decide.
    await api.devLogin(USER.lou);
    expect((await api.getMyApprovals()).some((x) => x.contract.title === ERP)).toBe(false);
    expect((await problem(api.decideApprovalStep(stepOf(api, ERP, USER.lou).id, { decision: 'Approve', comment: '' }))).status).toBe(409);
  });

  it('delegate: the step moves to the delegate and the original is marked Delegated', async () => {
    const api = as(USER.ras);
    const stepId = stepOf(api, ERP, USER.ras).id;
    await api.delegateApprovalStep(stepId, USER.kar);
    const stage = approvalOf(api, ERP).stages.find((s) => s.order === 2)!;
    expect(stage.steps.find((s) => s.id === stepId)).toMatchObject({ status: 'Delegated', comment: 'Delegated to Karen Lindberg' });
    expect(stage.steps.find((s) => s.approverId === USER.kar)).toMatchObject({ status: 'Pending', roleLabel: 'Group CFO (delegated by Rasmus Holm)' });
    expect(await api.getMyApprovals()).toHaveLength(0);
    await api.devLogin(USER.kar);
    expect((await api.getMyApprovals()).some((x) => x.contract.title === ERP)).toBe(true);
  });

  it('delegate: not to yourself, the owner, or someone without the Approver role', async () => {
    const api = as(USER.ras);
    const stepId = stepOf(api, ERP, USER.ras).id;
    expect((await problem(api.delegateApprovalStep(stepId, USER.ras))).status).toBe(400);
    expect((await problem(api.delegateApprovalStep(stepId, USER.sor))).status).toBe(400); // owner of the contract
    expect((await problem(api.delegateApprovalStep(stepId, USER.emm))).status).toBe(400); // reader
    expect((await problem(api.delegateApprovalStep(stepId, USER.lou))).status).toBe(400); // already an approver in this stage
    expect(stepOf(api, ERP, USER.ras).status).toBe('Pending');
  });

  it('only the assigned approver can decide', async () => {
    const api = as(USER.kar);
    expect((await problem(api.decideApprovalStep(stepOf(api, ERP, USER.ras).id, { decision: 'Approve', comment: '' }))).status).toBe(403);
  });

  it('the owner or submitter never approves their own contract: a substitute takes the step', async () => {
    const api = as(USER.met); // Purchasing department head, owner and submitter here
    const draft = await api.createContract(input({ ownerId: USER.met }));
    const submitted = await api.submitContract(draft.id);
    const steps = submitted.approval!.stages.flatMap((s) => s.steps);
    expect(steps.length).toBeGreaterThan(0);
    expect(steps.some((s) => s.approverId === USER.met)).toBe(false);
    expect(steps[0]?.roleLabel).toMatch(/\(instead of Mette Kjær, who owns or submitted the contract\)$/);
    expect(submitted.submittedById).toBe(USER.met);
  });
});

describe('sign-in configuration and access code', () => {
  it('reports Dev mode without an access code by default', async () => {
    expect(await as(USER.ras).getDevConfig()).toMatchObject({ authMode: 'Dev', accessCodeRequired: false });
  });

  it('with an access code: starts signed out and needs the code for dev/users and dev/login', async () => {
    const api = createMockApi({ today: TODAY, accessCode: 'dafa-test' });
    expect(await api.getDevConfig()).toMatchObject({ authMode: 'Dev', accessCodeRequired: true });
    expect((await problem(api.getMe())).status).toBe(401);
    expect((await problem(api.getDevUsers())).status).toBe(401);
    expect((await problem(api.getDevUsers('wrong'))).status).toBe(401);
    expect((await api.getDevUsers('dafa-test')).length).toBe(12);
    expect((await problem(api.devLogin(USER.ras))).status).toBe(401);
    expect((await problem(api.devLogin(USER.ras, 'nope'))).status).toBe(401);
    expect((await api.devLogin(USER.ras, 'dafa-test')).name).toBe('Rasmus Holm');
    expect((await api.getMe()).id).toBe(USER.ras);
  });
});

describe('phase 2 contract additions', () => {
  it('peek reads a contract without marking it as recently opened', async () => {
    const api = as(USER.emm);
    const [first] = (await api.getContracts({ pageSize: 5 })).items;
    const before = (await api.getHome()).recent.map((r) => r.id);
    await api.getContract(first!.id, { peek: true });
    expect((await api.getHome()).recent.map((r) => r.id)).toEqual(before);
    await api.getContract(first!.id);
    expect((await api.getHome()).recent[0]?.id).toBe(first!.id);
  });

  it('every signed-in user can read the reminder settings; only an admin can change them', async () => {
    const api = as(USER.emm);
    expect(await api.getReminderSettings()).toEqual({ noticeDays: [120, 60, 30], escalationDays: 14, obligationDays: 14 });
    expect((await problem(api.admin.getReminderSettings())).status).toBe(403);
    await api.devLogin(USER.pet);
    await api.admin.updateReminderSettings({ noticeDays: [90, 45], escalationDays: 10, obligationDays: 7 });
    await api.devLogin(USER.emm);
    expect((await api.getReminderSettings()).noticeDays).toEqual([90, 45]);
  });

  it('the approval preview names the rule that matched', async () => {
    const api = as(USER.jon);
    const preview = await api.previewApproval({ entityId: entityId(ENT.dafa), typeId: typeId(TYP.direct), departmentId: departmentId(DEP.purchasing), value: 400_000 });
    expect(preview.stages.length).toBeGreaterThan(0);
    expect(api.db.approvalRules.find((r) => r.id === preview.ruleId)?.name).toBe(preview.ruleName);
  });

  it('templates: upload stores the file, download returns it, other file types are refused', async () => {
    const api = as(USER.pet);
    const file = new File(['docx bytes'], 'Service agreement.docx');
    const created = await api.admin.createTemplate({ name: 'Service agreement', description: 'Standard services', typeId: null }, file);
    expect(created.fileName).toBe('Service agreement.docx');
    const content = await api.admin.getTemplateContent(created.id);
    expect(content.fileName).toBe('Service agreement.docx');
    expect(content.blob.size).toBe(file.size);
    expect((await problem(api.admin.createTemplate({ name: 'Bad', description: '', typeId: null }, new File(['x'], 'notes.txt')))).status).toBe(400);
    expect((await problem(api.admin.createTemplate({ name: 'service agreement', description: '', typeId: null }, file))).status).toBe(409);
    expect((await api.getTemplates()).some((t) => t.id === created.id)).toBe(true);
  });

  it('approved terms are locked for the owner, not for an administrator', async () => {
    const admin = as(USER.pet);
    const rec = admin.db.contracts.find((c) => c.status === 'Active' && admin.db.users.find((u) => u.id === c.ownerId)?.roles.includes('ContractOwner') && c.ownerId !== USER.pet)!;
    expect((await admin.getContract(rec.id)).lockedFields).toEqual([]);

    const owner = as(rec.ownerId);
    const detail = await owner.getContract(rec.id);
    expect(detail.lockedFields).toEqual(expect.arrayContaining(['title', 'counterpartyName', 'startDate', 'openEnded', 'currency', 'autoRenew']));
    expect(detail.lockedFields).not.toContain('paymentTerms');
    expect(detail.lockedFields).not.toContain('ownerId');
    const { lockedFields: _l, ...rest } = detail;
    const body: ContractInput = { ...(rest as unknown as ContractInput), changeOfControl: detail.changeOfControl ?? false };
    // Unlocked fields can still change.
    expect((await owner.updateContract(rec.id, { ...body, paymentTerms: '45 days net' })).paymentTerms).toBe('45 days net');
    const locked = await problem(owner.updateContract(rec.id, { ...body, currency: detail.currency === 'EUR' ? 'DKK' : 'EUR' }));
    expect(locked).toEqual({ status: 409, detail: 'Approved terms are locked. Register an amendment as a linked contract, or ask an administrator. Locked: currency.' });
  });

  it('a draft has no locked fields, and a missing change-of-control answer is missing data', async () => {
    const api = as(USER.jon);
    const draft = await api.createContract(input());
    expect(draft.lockedFields).toEqual([]);
    api.db.contracts.find((c) => c.id === draft.id)!.changeOfControl = null;
    const again = await api.getContract(draft.id);
    expect(again.changeOfControl).toBeNull();
    expect(again.missingData).toContain('Change of control');
  });

  it('Renew on a fixed-term contract needs a new end date and moves the end date', async () => {
    const seed = as(USER.pet);
    const rec = seed.db.contracts.find((c) => c.status === 'Active' && !c.autoRenew && c.endDate && c.renewalDecision === null)!;
    const api = as(rec.ownerId);
    const end = api.db.contracts.find((c) => c.id === rec.id)!.endDate!;
    expect((await problem(api.decideRenewal(rec.id, { decision: 'Renew', comment: '' }))).status).toBe(400);
    expect((await problem(api.decideRenewal(rec.id, { decision: 'Renew', comment: '', newEndDate: end }))).status).toBe(400);
    const renewed = await api.decideRenewal(rec.id, { decision: 'Renew', comment: '', newEndDate: addDays(end, 365) });
    expect(renewed.endDate).toBe(addDays(end, 365));
    expect(renewed.renewalDecision).toBeNull();
  });

  it('an open-ended contract can only record GiveNotice', async () => {
    const api = as(USER.pet);
    const rec = api.db.contracts.find((c) => c.status === 'Active')!;
    rec.endDate = null;
    expect((await api.getContract(rec.id)).canDecideRenewal).toBe(true);
    expect((await problem(api.decideRenewal(rec.id, { decision: 'Renegotiate', comment: '' }))).status).toBe(409);
    expect((await api.decideRenewal(rec.id, { decision: 'GiveNotice', comment: 'Supplier replaced.' })).status).toBe('NoticeGiven');
  });

  it('renewals include a deadline that passed without a decision, first and with negative days', async () => {
    const api = as(USER.ras);
    const before = await api.getRenewals(12);
    const rec = api.db.contracts.find((c) => c.id === before.find((r) => r.daysToNotice > 60)!.id)!;
    rec.endDate = addDays(TODAY, 10);
    rec.noticePeriodDays = 400;
    const after = await api.getRenewals(12);
    expect(after[0]).toMatchObject({ id: rec.id, daysToNotice: -390, noticeDeadline: addDays(TODAY, -390) });
    // Once a decision is recorded, a passed deadline no longer shows.
    rec.renewalDecision = 'Renegotiate';
    expect((await api.getRenewals(12)).some((r) => r.id === rec.id)).toBe(false);
  });

  it('uploads: only the allowed file types; the main document is admin-only after approval', async () => {
    const seed = as(USER.pet);
    const rec = seed.db.contracts.find((c) => c.status === 'Active' && seed.db.documents.some((d) => d.contractId === c.id && d.kind === 'Main') && seed.db.users.find((u) => u.id === c.ownerId)?.roles.includes('ContractOwner') && c.ownerId !== USER.pet)!;
    const owner = as(rec.ownerId);
    expect((await problem(owner.uploadDocument(rec.id, new File(['x'], 'tool.exe'), 'Other'))).status).toBe(400);
    expect((await problem(owner.uploadDocument(rec.id, new File(['x'], 'Signed copy.pdf'), 'Main'))).status).toBe(409);
    expect((await owner.uploadDocument(rec.id, new File(['x'], 'Amendment 1.pdf'), 'Amendment')).documents.some((d) => d.name === 'Amendment 1.pdf')).toBe(true);
    expect((await problem(seed.uploadDocument(rec.id, new File(['x'], 'Signed copy.pdf'), 'Main'))).status).toBe(200);
  });

  it('overview is open to every user, scoped to what they can see (release 3)', async () => {
    const emm = await as(USER.emm).getOverview();
    const ras = await as(USER.ras).getOverview();
    expect(emm.activeContracts).toBeGreaterThan(0);
    expect(emm.activeContracts).toBeLessThan(ras.activeContracts);
    expect((await problem(as(USER.jon).getOverview())).status).toBe(200);
    expect((await as(USER.ann).getOverview()).activeContracts).toBeGreaterThan(0);
    expect((await as(USER.pet).getOverview()).activeContracts).toBeGreaterThan(0);
  });
});
