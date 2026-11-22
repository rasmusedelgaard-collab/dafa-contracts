// Custom fields and per-type field rules: which fields apply, defaults, validation, display.
import type { ContractStatus, CustomFieldDef, CustomFieldValue, FieldRules, Id, Lookups, OptionalStandardField, TypeFieldRules } from '../api/types';
import { formatNumber } from './displayFormat';

/** Active definitions that apply to a contract type, in admin order. */
export function fieldsForType(defs: CustomFieldDef[], typeId: Id | null | undefined): CustomFieldDef[] {
  return defs
    .filter((d) => d.isActive && (d.contractTypeIds.length === 0 || (!!typeId && d.contractTypeIds.includes(typeId))))
    .sort((a, b) => a.order - b.order);
}

export function rulesForType(rules: FieldRules | undefined, typeId: Id | null | undefined): TypeFieldRules | undefined {
  return typeId ? rules?.types.find((r) => r.typeId === typeId) : undefined;
}

export const STANDARD_FIELD_LABELS: Record<OptionalStandardField, string> = {
  delegateId: 'Delegate',
  responsibleId: 'Responsible',
  endDate: 'End date (no open-ended)',
  annualValue: 'Annual value',
  totalValue: 'Total value',
  paymentTerms: 'Payment terms',
  priceAdjustment: 'Price adjustment',
  governingLaw: 'Governing law',
  liabilityCap: 'Liability cap',
  exclusivity: 'Exclusivity',
  summary: 'Summary',
};

export const isEmptyValue = (v: CustomFieldValue | undefined): boolean =>
  v === undefined || v === null || v === '' || (Array.isArray(v) && v.length === 0);

/**
 * Checks one value against its definition. Returns an error message or undefined.
 * `required` is checked only when `enforceRequired` (create, draft edit, submit).
 */
export function validateCustomValue(def: CustomFieldDef, v: CustomFieldValue | undefined, enforceRequired: boolean, isUser?: (id: Id) => boolean, currencies?: string[]): string | undefined {
  if (isEmptyValue(v)) return enforceRequired && def.required ? `${def.label} is required` : undefined;
  switch (def.type) {
    case 'Text':
      return typeof v === 'string' ? (v.length > 2000 ? 'At most 2000 characters' : undefined) : 'Must be text';
    case 'Number':
      return typeof v === 'number' && Number.isFinite(v) ? undefined : 'Must be a number';
    case 'Money': {
      if (!v || typeof v !== 'object' || Array.isArray(v) || !Number.isFinite(v.amount)) return 'Enter an amount';
      if (currencies && !currencies.includes(v.currency)) return 'Unknown currency';
      return undefined;
    }
    case 'Date':
      return typeof v === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(v) ? undefined : 'Must be a date';
    case 'YesNo':
      return typeof v === 'boolean' ? undefined : 'Must be yes or no';
    case 'Choice':
      return typeof v === 'string' && def.options.includes(v) ? undefined : 'Choose one of the options';
    case 'MultiChoice':
      return Array.isArray(v) && v.every((x) => def.options.includes(x)) ? undefined : 'Choose from the options';
    case 'Person':
      return typeof v === 'string' && (!isUser || isUser(v)) ? undefined : 'Choose a person';
  }
}

/** Errors keyed `customFields.<key>`, as the API returns them. Values for fields that do not apply are errors too. */
export function validateCustomFields(
  defs: CustomFieldDef[],
  typeId: Id,
  values: Record<string, CustomFieldValue> | undefined,
  enforceRequired: boolean,
  isUser?: (id: Id) => boolean,
  currencies?: string[],
): Record<string, string> {
  const errors: Record<string, string> = {};
  const applicable = fieldsForType(defs, typeId);
  for (const def of applicable) {
    const e = validateCustomValue(def, values?.[def.key], enforceRequired, isUser, currencies);
    if (e) errors[`customFields.${def.key}`] = e;
  }
  for (const [key, v] of Object.entries(values ?? {})) {
    if (isEmptyValue(v)) continue;
    if (!applicable.some((d) => d.key === key)) errors[`customFields.${key}`] = defs.some((d) => d.key === key) ? 'This field does not apply to the contract type' : 'Unknown field';
  }
  return errors;
}

/** Drops values of fields that do not apply to the type (used when the user changes the type). */
export function pruneValues(defs: CustomFieldDef[], typeId: Id, values: Record<string, CustomFieldValue>): Record<string, CustomFieldValue> {
  const keys = new Set(fieldsForType(defs, typeId).map((d) => d.key));
  return Object.fromEntries(Object.entries(values).filter(([k, v]) => keys.has(k) && !isEmptyValue(v)));
}

/** Readable text for a value. */
export function displayValue(def: Pick<CustomFieldDef, 'type'>, v: CustomFieldValue | undefined, lookups?: Pick<Lookups, 'users'>, formatDate?: (iso: string) => string): string {
  if (isEmptyValue(v) || v === undefined || v === null) return '–';
  switch (def.type) {
    case 'YesNo':
      return v ? 'Yes' : 'No';
    case 'MultiChoice':
      return Array.isArray(v) ? v.join(', ') : String(v);
    case 'Money':
      return typeof v === 'object' && !Array.isArray(v) ? `${v.currency} ${formatNumber(v.amount)}` : String(v);
    case 'Number':
      return typeof v === 'number' ? formatNumber(v) : String(v);
    case 'Date':
      return typeof v === 'string' && formatDate ? formatDate(v) : String(v);
    case 'Person':
      return lookups?.users.find((u) => u.id === v)?.name ?? String(v);
    default:
      return String(v);
  }
}

/** Standard fields a type requires beyond the always-required ones; returns field -> message. */
export function validateStandardRules(
  rules: TypeFieldRules | undefined,
  values: Partial<Record<OptionalStandardField, unknown>> & { openEnded?: boolean },
): Partial<Record<OptionalStandardField, string>> {
  const errors: Partial<Record<OptionalStandardField, string>> = {};
  for (const f of rules?.required ?? []) {
    if (f === 'endDate') {
      if (values.openEnded || !values.endDate) errors.endDate = 'This contract type needs an end date';
      continue;
    }
    const v = values[f];
    if (v === null || v === undefined || v === '') errors[f] = `${STANDARD_FIELD_LABELS[f]} is required for this contract type`;
  }
  return errors;
}

/** Required fields are enforced on create, while the contract is a draft, and on submit. */
export const enforcesRequired = (status: ContractStatus | null): boolean => status === null || status === 'Draft';
