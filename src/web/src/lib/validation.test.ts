import { describe, expect, it } from 'vitest';
import { draftToInput, emptyDraft, hasErrors, parseAmount, parseWhole, validateDraft, validateInput, type ContractDraft } from './validation';

function validDraft(overrides: Partial<ContractDraft> = {}): ContractDraft {
  return emptyDraft({
    title: 'Supply agreement',
    counterpartyName: 'Nordpolymer Compounds ApS',
    typeId: 'type-1',
    entityId: 'entity-1',
    departmentId: 'dept-1',
    ownerId: 'user-1',
    reminderRecipient: 'Owner',
    startDate: '2027-01-01',
    endDate: '2028-12-31',
    noticePeriodDays: '90',
    autoRenew: true,
    renewalMonths: '12',
    annualValue: '1,250,000',
    currency: 'DKK',
    changeOfControl: false,
    ...overrides,
  });
}

describe('new contract validation', () => {
  it('accepts a complete draft', () => {
    expect(validateDraft(validDraft())).toEqual({});
  });

  it('requires every field the brief lists as required to save', () => {
    const errors = validateDraft(emptyDraft({ currency: '', reminderRecipient: '' }));
    expect(Object.keys(errors).sort()).toEqual(
      [
        'title', 'counterpartyName', 'typeId', 'entityId', 'departmentId', 'ownerId', 'reminderRecipient',
        'startDate', 'endDate', 'noticePeriodDays', 'autoRenew', 'currency', 'annualValue', 'changeOfControl',
      ].sort(),
    );
  });

  it('accepts open-ended instead of an end date', () => {
    const errors = validateDraft(validDraft({ endDate: '', openEnded: true, autoRenew: false, renewalMonths: '' }));
    expect(errors).toEqual({});
  });

  it('rejects an end date on or before the start date', () => {
    expect(validateDraft(validDraft({ endDate: '2027-01-01' })).endDate).toMatch(/after the start date/);
    expect(validateDraft(validDraft({ endDate: '2026-06-30' })).endDate).toBeDefined();
  });

  it('accepts either the annual value or the total value, including 0', () => {
    expect(validateDraft(validDraft({ annualValue: '', totalValue: '500000' }))).toEqual({});
    expect(validateDraft(validDraft({ annualValue: '0', totalValue: '' }))).toEqual({});
    expect(validateDraft(validDraft({ annualValue: '', totalValue: '' })).annualValue).toBeDefined();
    expect(validateDraft(validDraft({ annualValue: 'about 1m' })).annualValue).toMatch(/amount/);
  });

  it('accepts a notice period of 0 but not a blank or fractional one', () => {
    expect(validateDraft(validDraft({ noticePeriodDays: '0' }))).toEqual({});
    expect(validateDraft(validDraft({ noticePeriodDays: '' })).noticePeriodDays).toBeDefined();
    expect(validateDraft(validDraft({ noticePeriodDays: '1.5' })).noticePeriodDays).toBeDefined();
    expect(validateDraft(validDraft({ noticePeriodDays: '3 months' })).noticePeriodDays).toBeDefined();
  });

  it('needs explicit answers to auto-renewal and change of control', () => {
    const errors = validateDraft(validDraft({ autoRenew: null, changeOfControl: null }));
    expect(errors.autoRenew).toBeDefined();
    expect(errors.changeOfControl).toBeDefined();
    // "No" is a valid answer to both.
    expect(validateDraft(validDraft({ autoRenew: false, changeOfControl: false, renewalMonths: '' }))).toEqual({});
  });

  it('needs renewal months when the contract renews automatically', () => {
    expect(validateDraft(validDraft({ renewalMonths: '' })).renewalMonths).toBeDefined();
    expect(validateDraft(validDraft({ renewalMonths: '0' })).renewalMonths).toBeDefined();
  });

  it('needs the person the reminders go to', () => {
    expect(validateDraft(validDraft({ reminderRecipient: 'Responsible' })).responsibleId).toBeDefined();
    expect(validateDraft(validDraft({ reminderRecipient: 'Delegate' })).delegateId).toBeDefined();
    expect(validateDraft(validDraft({ reminderRecipient: 'Responsible', responsibleId: 'user-2' }))).toEqual({});
  });

  it('blocks saving while AI suggestions are not accepted or changed', () => {
    const errors = validateDraft(validDraft(), ['counterpartyName', 'endDate']);
    expect(errors.counterpartyName).toBe('Accept or change the suggestion');
    expect(errors.endDate).toBe('Accept or change the suggestion');
    expect(hasErrors(errors)).toBe(true);
  });

  it('keeps the more specific error when a pending suggestion is also invalid', () => {
    const errors = validateDraft(validDraft({ endDate: '2026-01-01' }), ['endDate']);
    expect(errors.endDate).toMatch(/after the start date/);
  });
});

describe('amount parsing', () => {
  it('reads thousands separators and decimals', () => {
    expect(parseAmount('1,250,000')).toBe(1250000);
    expect(parseAmount('1 250 000.50')).toBe(1250000.5);
    expect(parseAmount('')).toBeNull();
    expect(parseAmount('-5')).toBeNaN();
    expect(parseWhole('90')).toBe(90);
    expect(parseWhole('9.5')).toBeNaN();
  });
});

describe('draft to API body', () => {
  it('converts strings to typed values and blanks to null', () => {
    const input = draftToInput(validDraft({ delegateId: '', paymentTerms: ' 30 days net ', totalValue: '' }));
    expect(input.annualValue).toBe(1250000);
    expect(input.totalValue).toBeNull();
    expect(input.noticePeriodDays).toBe(90);
    expect(input.renewalMonths).toBe(12);
    expect(input.delegateId).toBeNull();
    expect(input.paymentTerms).toBe('30 days net');
    expect(input.changeOfControl).toBe(false);
    expect(validateInput(input)).toEqual({});
  });

  it('sends a null end date for open-ended contracts and drops renewal months without auto-renewal', () => {
    const input = draftToInput(validDraft({ openEnded: true, endDate: '2028-12-31', autoRenew: false, renewalMonths: '12' }));
    expect(input.endDate).toBeNull();
    expect(input.renewalMonths).toBeNull();
  });

  it('server-side validation rejects a body that misses required fields', () => {
    const errors = validateInput({ title: 'x' });
    expect(Object.keys(errors)).toEqual(expect.arrayContaining(['typeId', 'counterpartyName', 'entityId', 'departmentId', 'ownerId', 'startDate', 'noticePeriodDays', 'autoRenew', 'changeOfControl', 'annualValue', 'currency', 'reminderRecipient']));
  });
});
