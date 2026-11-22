import { describe, expect, it } from 'vitest';
import type { ApprovalRule } from '../api/types';
import { delegateCandidates } from '../features/Approval';
import {
  draftToRuleInput,
  emptyRuleDraft,
  newStage,
  parseNoticeDay,
  ruleHasErrors,
  ruleToDraft,
  validateCover,
  validateName,
  validateReminders,
  validateRetention,
  validateRuleDraft,
  type RuleDraft,
} from './settingsForms';
import { uploadError } from './uploads';

function validDraft(overrides: Partial<RuleDraft> = {}): RuleDraft {
  return { ...emptyRuleDraft(), name: 'IT contracts over 1m', minValue: '1,000,000', maxValue: '5000000', priority: '2', stages: [newStage([{ kind: 'role', role: 'DepartmentHead', userId: '' }]), newStage([{ kind: 'role', role: 'GroupCFO', userId: '' }, { kind: 'user', role: '', userId: 'u-legal' }])], ...overrides };
}

describe('approval rule form validation', () => {
  it('accepts a complete rule', () => {
    expect(ruleHasErrors(validateRuleDraft(validDraft()))).toBe(false);
  });

  it('requires a name and at least one complete approver', () => {
    const errors = validateRuleDraft(emptyRuleDraft());
    expect(errors.name).toBe('Enter a name for the rule');
    expect(errors.approver['0-0']).toBe('Choose a role');
    expect(ruleHasErrors(errors)).toBe(true);
  });

  it('requires at least one stage, and an approver in every stage', () => {
    expect(validateRuleDraft(validDraft({ stages: [] })).stages).toBe('Add at least one stage');
    const errors = validateRuleDraft(validDraft({ stages: [newStage([{ kind: 'role', role: 'Legal', userId: '' }]), newStage([])] }));
    expect(errors.stage[1]).toMatch(/at least one approver/);
    expect(errors.stage[0]).toBeUndefined();
  });

  it('asks for a person when the approver is a specific user', () => {
    const errors = validateRuleDraft(validDraft({ stages: [newStage([{ kind: 'user', role: '', userId: '' }])] }));
    expect(errors.approver['0-0']).toBe('Choose a person');
  });

  it('rejects the same approver twice in one stage, but allows it in different stages', () => {
    const twice = validateRuleDraft(validDraft({ stages: [newStage([{ kind: 'role', role: 'Legal', userId: '' }, { kind: 'role', role: 'Legal', userId: '' }])] }));
    expect(twice.approver['0-1']).toBe('Already an approver in this stage');
    const apart = validateRuleDraft(validDraft({ stages: [newStage([{ kind: 'role', role: 'Legal', userId: '' }]), newStage([{ kind: 'role', role: 'Legal', userId: '' }])] }));
    expect(ruleHasErrors(apart)).toBe(false);
  });

  it('checks the value band and the priority', () => {
    expect(validateRuleDraft(validDraft({ minValue: 'abc' })).minValue).toMatch(/amount in DKK/);
    expect(validateRuleDraft(validDraft({ minValue: '5000000', maxValue: '1000000' })).maxValue).toBe('The upper value must be above the lower value');
    expect(validateRuleDraft(validDraft({ minValue: '100', maxValue: '100' })).maxValue).toBeDefined();
    expect(validateRuleDraft(validDraft({ minValue: '', maxValue: '' })).maxValue).toBeUndefined();
    expect(validateRuleDraft(validDraft({ priority: '' })).priority).toBeDefined();
    expect(validateRuleDraft(validDraft({ priority: '-1' })).priority).toBeDefined();
  });

  it('turns the draft into the API input, numbering stages by their position', () => {
    const input = draftToRuleInput(validDraft({ entityId: 'e1' }));
    expect(input).toMatchObject({ name: 'IT contracts over 1m', entityId: 'e1', typeId: null, departmentId: null, minValue: 1_000_000, maxValue: 5_000_000, priority: 2 });
    expect(input.stages).toEqual([
      { order: 1, approvers: [{ userId: null, role: 'DepartmentHead' }] },
      { order: 2, approvers: [{ userId: null, role: 'GroupCFO' }, { userId: 'u-legal', role: null }] },
    ]);
  });

  it('round-trips an existing rule through the form', () => {
    const rule: ApprovalRule = { id: 'r1', name: 'Band 2', entityId: null, typeId: 't1', departmentId: null, minValue: 1_000_000, maxValue: null, priority: 3, stages: [{ order: 2, approvers: [{ userId: 'u1', role: null }] }, { order: 1, approvers: [{ userId: null, role: 'Legal' }] }] };
    const input = draftToRuleInput(ruleToDraft(rule));
    expect(input).toMatchObject({ name: 'Band 2', typeId: 't1', minValue: 1_000_000, maxValue: null, priority: 3 });
    expect(input.stages.map((s) => s.approvers[0])).toEqual([{ userId: null, role: 'Legal' }, { userId: 'u1', role: null }]);
  });
});

describe('other settings forms', () => {
  it('validates a reminder day before it is added', () => {
    expect(parseNoticeDay('45', [120, 60, 30])).toEqual({ value: 45 });
    expect(parseNoticeDay('60', [120, 60, 30])).toHaveProperty('error');
    expect(parseNoticeDay('0', [])).toHaveProperty('error');
    expect(parseNoticeDay('1.5', [])).toHaveProperty('error');
    expect(parseNoticeDay('', [])).toHaveProperty('error');
  });

  it('validates the reminder settings', () => {
    expect(validateReminders({ noticeDays: [120, 60, 30], escalationDays: '14', obligationDays: '14' })).toEqual({});
    expect(validateReminders({ noticeDays: [], escalationDays: '14', obligationDays: '14' }).noticeDays).toBeDefined();
    expect(validateReminders({ noticeDays: [30], escalationDays: '45', obligationDays: '14' }).escalationDays).toMatch(/after the first reminder/);
    expect(validateReminders({ noticeDays: [30], escalationDays: 'x', obligationDays: '' })).toMatchObject({ escalationDays: expect.any(String), obligationDays: expect.any(String) });
  });

  it('validates retention: years unless kept, reviewer when reviewed', () => {
    expect(validateRetention({ years: '5', action: 'ReviewThenDelete', reviewerDepartmentId: 'd1' })).toEqual({});
    expect(validateRetention({ years: '', action: 'ReviewThenDelete', reviewerDepartmentId: '' })).toMatchObject({ years: 'Enter whole years', reviewerDepartmentId: 'Choose who reviews' });
    expect(validateRetention({ years: '', action: 'Keep', reviewerDepartmentId: '' })).toEqual({});
    expect(validateRetention({ years: '5', action: 'DeleteAutomatically', reviewerDepartmentId: '' })).toEqual({});
  });

  it('validates holiday cover', () => {
    const ok = { userId: 'a', deputyId: 'b', from: '2026-12-01', to: '2026-12-10' };
    expect(validateCover(ok)).toEqual({});
    expect(validateCover({ ...ok, deputyId: 'a' }).deputyId).toBe('The deputy must be someone else');
    expect(validateCover({ ...ok, to: '2026-11-30' }).to).toMatch(/on or after/);
    expect(validateCover({ userId: '', deputyId: '', from: '', to: '' })).toMatchObject({ userId: expect.any(String), deputyId: expect.any(String), from: expect.any(String), to: expect.any(String) });
    expect(validateCover(ok, [{ userId: 'a', deputyId: 'c', from: '2026-12-05', to: '2026-12-20' }]).from).toMatch(/already has cover/);
    expect(validateCover(ok, [{ userId: 'a', deputyId: 'c', from: '2026-12-11', to: '2026-12-20' }])).toEqual({});
  });

  it('validates names: required and unique, ignoring case and the row being renamed', () => {
    const list = [{ id: '1', name: 'Finance' }, { id: '2', name: 'IT' }];
    expect(validateName(' ', list)).toBe('Enter a name');
    expect(validateName('finance', list)).toBe('That name is already in use');
    expect(validateName('Finance', list, '1')).toBeUndefined();
    expect(validateName('Legal', list)).toBeUndefined();
  });
});

describe('upload checks', () => {
  it('accepts the allowed types up to 25 MB', () => {
    expect(uploadError({ name: 'Contract.PDF', size: 1000 })).toBeUndefined();
    expect(uploadError({ name: 'mail.msg', size: 25 * 1024 * 1024 })).toBeUndefined();
  });
  it('explains what is wrong with a file', () => {
    expect(uploadError(null)).toBe('Choose a file');
    expect(uploadError({ name: 'setup.exe', size: 10 })).toMatch(/cannot be uploaded/);
    expect(uploadError({ name: 'noextension', size: 10 })).toMatch(/cannot be uploaded/);
    expect(uploadError({ name: 'big.pdf', size: 25 * 1024 * 1024 + 1 })).toBe('The file is larger than 25 MB');
    expect(uploadError({ name: 'empty.pdf', size: 0 })).toBe('The file is empty');
  });
});

describe('delegate candidates', () => {
  const users = [
    { id: 'me', name: 'Me', initials: 'M', departmentIds: [], roles: ['Approver' as const] },
    { id: 'owner', name: 'Owner', initials: 'O', departmentIds: [], roles: ['ContractOwner' as const, 'Approver' as const] },
    { id: 'submitter', name: 'Submitter', initials: 'S', departmentIds: [], roles: ['Approver' as const] },
    { id: 'reader', name: 'Reader', initials: 'R', departmentIds: [], roles: ['Reader' as const] },
    { id: 'approver', name: 'Approver', initials: 'A', departmentIds: [], roles: ['Approver' as const] },
    { id: 'admin', name: 'Admin', initials: 'AD', departmentIds: [], roles: ['Admin' as const] },
  ];
  it('offers only approvers and admins who are not you, the owner or the submitter', () => {
    expect(delegateCandidates(users, 'me', { ownerId: 'owner', submittedById: 'submitter' }).map((u) => u.id)).toEqual(['approver', 'admin']);
  });
  it('leaves the role check to the server when the API sends no roles', () => {
    const bare = users.map(({ roles: _roles, ...u }) => u);
    expect(delegateCandidates(bare, 'me', { ownerId: 'owner' }).map((u) => u.id)).toEqual(['submitter', 'reader', 'approver', 'admin']);
  });
});
