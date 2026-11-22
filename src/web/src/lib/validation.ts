// New-contract / edit-details form model and validation.
// Required to save (docs/BUILD_BRIEF.md section 4): title, type, counterparty, entity, department,
// owner, reminderRecipient, startDate, endDate-or-openEnded, noticePeriodDays, autoRenew, currency,
// annualValue or totalValue, changeOfControl.
import type { ContractFields, ContractInput, CustomFieldDef, CustomFieldValue, FieldRules, ReminderRecipient } from '../api/types';
import { rulesForType, validateCustomFields, validateStandardRules } from './customFields';
import { isIsoDate } from './dates';
import { toDkk } from './format';

/** Form state: text inputs are strings, the two required yes/no answers start unanswered (null). */
export interface ContractDraft {
  title: string;
  counterpartyName: string;
  supplierId: string | null;
  typeId: string;
  entityId: string;
  departmentId: string;
  ownerId: string;
  delegateId: string;
  responsibleId: string;
  reminderRecipient: ReminderRecipient | '';
  startDate: string;
  endDate: string;
  openEnded: boolean;
  noticePeriodDays: string;
  autoRenew: boolean | null;
  renewalMonths: string;
  annualValue: string;
  totalValue: string;
  currency: string;
  paymentTerms: string;
  priceAdjustment: string;
  priceAdjustmentDate: string;
  governingLaw: string;
  liabilityCap: string;
  exclusivity: string;
  changeOfControl: boolean | null;
  summary: string;
  /** Release 3: admin-defined fields (by key) and tags. */
  customFields: Record<string, CustomFieldValue>;
  tagIds: string[];
}

export type DraftField = keyof ContractDraft;
/** Custom-field errors are keyed `customFields.<key>`, as the API returns them. */
export type DraftErrors = Partial<Record<DraftField | `customFields.${string}`, string>>;

/** Release 3 rules that depend on admin settings. `enforceRequired` is true on create and while a draft. */
export interface DraftRules {
  customFields: CustomFieldDef[];
  fieldRules: FieldRules;
  enforceRequired: boolean;
}

export function emptyDraft(defaults: Partial<ContractDraft> = {}): ContractDraft {
  return {
    title: '',
    counterpartyName: '',
    supplierId: null,
    typeId: '',
    entityId: '',
    departmentId: '',
    ownerId: '',
    delegateId: '',
    responsibleId: '',
    reminderRecipient: 'Owner',
    startDate: '',
    endDate: '',
    openEnded: false,
    noticePeriodDays: '',
    autoRenew: null,
    renewalMonths: '',
    annualValue: '',
    totalValue: '',
    currency: 'DKK',
    paymentTerms: '',
    priceAdjustment: '',
    priceAdjustmentDate: '',
    governingLaw: '',
    liabilityCap: '',
    exclusivity: '',
    changeOfControl: null,
    summary: '',
    customFields: {},
    tagIds: [],
    ...defaults,
  };
}

/** Parses "1,250,000", "1 250 000" or "1250000.50". Returns null when empty, NaN when invalid. */
export function parseAmount(text: string): number | null {
  const t = text.replace(/[\s,]/g, '');
  if (t === '') return null;
  if (!/^\d+(\.\d+)?$/.test(t)) return Number.NaN;
  return Number(t);
}

/** Parses a non-negative whole number. Returns null when empty, NaN when invalid. */
export function parseWhole(text: string): number | null {
  const t = text.trim();
  if (t === '') return null;
  if (!/^\d+$/.test(t)) return Number.NaN;
  return Number(t);
}

/**
 * Validates the form. `pendingSuggestions` are fields that still hold an AI suggestion the
 * user has neither accepted nor changed; each one blocks saving.
 */
export function validateDraft(d: ContractDraft, pendingSuggestions: Iterable<string> = [], rules?: DraftRules): DraftErrors {
  const e: DraftErrors = {};
  const req = (field: DraftField, message: string) => {
    const v = d[field];
    if (typeof v !== 'string' || v.trim() === '') e[field] = message;
  };

  req('title', 'Enter a title');
  req('counterpartyName', 'Enter the counterparty');
  req('typeId', 'Choose a contract type');
  req('entityId', 'Choose a legal entity');
  req('departmentId', 'Choose a department');
  req('ownerId', 'Choose an owner');
  req('currency', 'Choose a currency');

  if (!d.reminderRecipient) e.reminderRecipient = 'Choose who gets the reminders';
  else if (d.reminderRecipient === 'Delegate' && !d.delegateId) e.delegateId = 'Choose a delegate, or send reminders to someone else';
  else if (d.reminderRecipient === 'Responsible' && !d.responsibleId) e.responsibleId = 'Choose a responsible person, or send reminders to someone else';

  if (!d.startDate) e.startDate = 'Enter the start date';
  else if (!isIsoDate(d.startDate)) e.startDate = 'Enter a valid date';

  if (!d.openEnded) {
    if (!d.endDate) e.endDate = 'Enter the end date, or mark the contract as open-ended';
    else if (!isIsoDate(d.endDate)) e.endDate = 'Enter a valid date';
    else if (isIsoDate(d.startDate) && d.endDate <= d.startDate) e.endDate = 'The end date must be after the start date';
  }

  const notice = parseWhole(d.noticePeriodDays);
  if (notice === null) e.noticePeriodDays = 'Enter the notice period in days (0 if none)';
  else if (Number.isNaN(notice)) e.noticePeriodDays = 'Enter a whole number of days';

  if (d.autoRenew === null) e.autoRenew = 'Choose whether the contract renews automatically';
  else if (d.autoRenew) {
    const months = parseWhole(d.renewalMonths);
    if (months === null || Number.isNaN(months) || months < 1) e.renewalMonths = 'Enter the renewal period in months';
    if (d.openEnded) e.autoRenew = 'An open-ended contract cannot renew automatically';
  }

  const annual = parseAmount(d.annualValue);
  const total = parseAmount(d.totalValue);
  if (Number.isNaN(annual)) e.annualValue = 'Enter an amount, for example 1,250,000';
  if (Number.isNaN(total)) e.totalValue = 'Enter an amount, for example 1,250,000';
  if (annual === null && total === null) e.annualValue = 'Enter the annual value or the total value (0 if none)';

  if (d.changeOfControl === null) e.changeOfControl = 'Choose whether there is a change-of-control clause';

  if (d.priceAdjustmentDate && !isIsoDate(d.priceAdjustmentDate)) e.priceAdjustmentDate = 'Enter a valid date';

  for (const field of pendingSuggestions) {
    if (field in d && !e[field as DraftField]) e[field as DraftField] = 'Accept or change the suggestion';
  }

  if (rules && d.typeId) {
    Object.assign(e, validateCustomFields(rules.customFields, d.typeId, d.customFields, rules.enforceRequired));
    if (rules.enforceRequired) {
      const std = validateStandardRules(rulesForType(rules.fieldRules, d.typeId), {
        delegateId: d.delegateId,
        responsibleId: d.responsibleId,
        endDate: d.endDate,
        openEnded: d.openEnded,
        annualValue: d.annualValue,
        totalValue: d.totalValue,
        paymentTerms: d.paymentTerms,
        priceAdjustment: d.priceAdjustment,
        governingLaw: d.governingLaw,
        liabilityCap: d.liabilityCap,
        exclusivity: d.exclusivity,
        summary: d.summary,
      });
      for (const [k, v] of Object.entries(std)) if (!e[k as DraftField]) e[k as DraftField] = v;
    }
  }
  return e;
}

export function hasErrors(errors: DraftErrors): boolean {
  return Object.keys(errors).length > 0;
}

const orNull = (s: string): string | null => (s.trim() === '' ? null : s.trim());

/** Converts a VALID draft to the API body. Call validateDraft first. */
export function draftToInput(d: ContractDraft): ContractInput {
  const autoRenew = d.autoRenew === true;
  return {
    title: d.title.trim(),
    typeId: d.typeId,
    supplierId: d.supplierId,
    counterpartyName: d.counterpartyName.trim(),
    entityId: d.entityId,
    departmentId: d.departmentId,
    ownerId: d.ownerId,
    delegateId: orNull(d.delegateId),
    responsibleId: orNull(d.responsibleId),
    reminderRecipient: d.reminderRecipient || 'Owner',
    startDate: d.startDate,
    endDate: d.openEnded ? null : d.endDate,
    noticePeriodDays: parseWhole(d.noticePeriodDays) ?? 0,
    autoRenew,
    renewalMonths: autoRenew ? parseWhole(d.renewalMonths) : null,
    annualValue: parseAmount(d.annualValue),
    totalValue: parseAmount(d.totalValue),
    currency: d.currency,
    paymentTerms: orNull(d.paymentTerms),
    priceAdjustment: orNull(d.priceAdjustment),
    priceAdjustmentDate: orNull(d.priceAdjustmentDate),
    governingLaw: orNull(d.governingLaw),
    liabilityCap: orNull(d.liabilityCap),
    exclusivity: orNull(d.exclusivity),
    changeOfControl: d.changeOfControl === true,
    summary: orNull(d.summary),
    customFields: Object.fromEntries(Object.entries(d.customFields).filter(([, v]) => !(v === null || v === '' || (Array.isArray(v) && v.length === 0)))),
    tagIds: d.tagIds,
  };
}

const str = (n: number | null): string => (n === null ? '' : String(n));

/** Loads an existing contract into the form (edit details). */
export function fieldsToDraft(c: Omit<ContractFields, 'changeOfControl'> & { changeOfControl: boolean | null; customFields?: Record<string, CustomFieldValue>; tags?: { id: string }[] }): ContractDraft {
  return {
    title: c.title,
    counterpartyName: c.counterpartyName,
    supplierId: c.supplierId,
    typeId: c.typeId,
    entityId: c.entityId,
    departmentId: c.departmentId,
    ownerId: c.ownerId,
    delegateId: c.delegateId ?? '',
    responsibleId: c.responsibleId ?? '',
    reminderRecipient: c.reminderRecipient,
    startDate: c.startDate,
    endDate: c.endDate ?? '',
    openEnded: c.endDate === null,
    noticePeriodDays: str(c.noticePeriodDays),
    autoRenew: c.autoRenew,
    renewalMonths: str(c.renewalMonths),
    annualValue: str(c.annualValue),
    totalValue: str(c.totalValue),
    currency: c.currency,
    paymentTerms: c.paymentTerms ?? '',
    priceAdjustment: c.priceAdjustment ?? '',
    priceAdjustmentDate: c.priceAdjustmentDate ?? '',
    governingLaw: c.governingLaw ?? '',
    liabilityCap: c.liabilityCap ?? '',
    exclusivity: c.exclusivity ?? '',
    changeOfControl: c.changeOfControl,
    summary: c.summary ?? '',
    customFields: { ...(c.customFields ?? {}) },
    tagIds: (c.tags ?? []).map((t) => t.id),
  };
}

/**
 * Server-side style validation of an API body, used by the mock so it rejects what the real
 * API would. Returns RFC 7807 `errors` (field -> messages).
 */
export function validateInput(i: Partial<ContractInput>): Record<string, string[]> {
  const errors: Record<string, string[]> = {};
  const add = (field: string, message: string) => {
    (errors[field] ??= []).push(message);
  };
  const text = (field: keyof ContractInput) => {
    const v = i[field];
    if (typeof v !== 'string' || v.trim() === '') add(field, 'Required');
  };
  (['title', 'typeId', 'counterpartyName', 'entityId', 'departmentId', 'ownerId', 'reminderRecipient', 'currency'] as const).forEach(text);
  if (!isIsoDate(i.startDate)) add('startDate', 'A valid start date is required');
  if (i.endDate !== null && i.endDate !== undefined) {
    if (!isIsoDate(i.endDate)) add('endDate', 'Not a valid date');
    else if (isIsoDate(i.startDate) && i.endDate <= i.startDate) add('endDate', 'Must be after the start date');
  }
  if (typeof i.noticePeriodDays !== 'number' || i.noticePeriodDays < 0 || !Number.isInteger(i.noticePeriodDays)) add('noticePeriodDays', 'Required, in whole days');
  if (typeof i.autoRenew !== 'boolean') add('autoRenew', 'Required');
  else if (i.autoRenew && !(typeof i.renewalMonths === 'number' && i.renewalMonths >= 1)) add('renewalMonths', 'Required when the contract renews automatically');
  if (typeof i.changeOfControl !== 'boolean') add('changeOfControl', 'Required');
  const hasValue = (v: unknown) => typeof v === 'number' && Number.isFinite(v) && v >= 0;
  if (!hasValue(i.annualValue) && !hasValue(i.totalValue)) add('annualValue', 'Annual value or total value is required');
  if (i.reminderRecipient === 'Delegate' && !i.delegateId) add('delegateId', 'Required when reminders go to the delegate');
  if (i.reminderRecipient === 'Responsible' && !i.responsibleId) add('responsibleId', 'Required when reminders go to the responsible person');
  return errors;
}

/** Contract value in DKK for the approval route: the larger of total and annual value, as the API uses. */
export function routeValue(d: Pick<ContractDraft, 'totalValue' | 'annualValue' | 'currency'>): number {
  const amount = (text: string) => {
    const n = parseAmount(text);
    return n !== null && !Number.isNaN(n) ? n : 0;
  };
  return Math.round(toDkk(Math.max(amount(d.totalValue), amount(d.annualValue)), d.currency));
}
