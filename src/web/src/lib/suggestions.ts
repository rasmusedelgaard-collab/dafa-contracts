import type { Lookups, Suggestion } from '../api/types';
import type { ContractDraft, DraftField } from './validation';

/** Draft fields an AI suggestion may fill, and how the value is read. */
const FIELD_KIND: Partial<Record<DraftField, 'text' | 'bool' | 'type' | 'entity' | 'department' | 'user'>> = {
  title: 'text',
  counterpartyName: 'text',
  typeId: 'type',
  entityId: 'entity',
  departmentId: 'department',
  ownerId: 'user',
  delegateId: 'user',
  responsibleId: 'user',
  startDate: 'text',
  endDate: 'text',
  noticePeriodDays: 'text',
  autoRenew: 'bool',
  renewalMonths: 'text',
  annualValue: 'text',
  totalValue: 'text',
  currency: 'text',
  paymentTerms: 'text',
  priceAdjustment: 'text',
  priceAdjustmentDate: 'text',
  governingLaw: 'text',
  liabilityCap: 'text',
  exclusivity: 'text',
  changeOfControl: 'bool',
  summary: 'text',
};

function resolveRef(list: { id: string; name: string }[], value: string): string | null {
  const v = value.trim().toLowerCase();
  return list.find((x) => x.id.toLowerCase() === v)?.id ?? list.find((x) => x.name.toLowerCase() === v)?.id ?? null;
}

export interface AppliedSuggestions {
  draft: ContractDraft;
  /** Fields that now hold a suggestion waiting for the user to accept or change it. */
  fields: DraftField[];
}

/**
 * Writes the analyzer's suggestions into the form. Id fields accept either the lookup id or its
 * name; suggestions for unknown fields or unknown lookup values are dropped.
 */
export function applySuggestions(draft: ContractDraft, suggestions: Suggestion[], lookups: Lookups): AppliedSuggestions {
  const next: ContractDraft = { ...draft };
  const fields: DraftField[] = [];
  const set = <K extends DraftField>(field: K, value: ContractDraft[K]) => {
    next[field] = value;
    fields.push(field);
  };

  for (const s of suggestions) {
    const field = s.field as DraftField;
    const kind = FIELD_KIND[field];
    if (!kind || s.value === null || s.value === undefined || String(s.value).trim() === '') continue;
    const value = String(s.value).trim();
    if (kind === 'bool') {
      const lower = value.toLowerCase();
      if (['true', 'yes'].includes(lower)) set(field as 'autoRenew' | 'changeOfControl', true);
      else if (['false', 'no'].includes(lower)) set(field as 'autoRenew' | 'changeOfControl', false);
    } else if (kind === 'text') {
      set(field as 'title', value);
    } else {
      const list = kind === 'type' ? lookups.contractTypes : kind === 'entity' ? lookups.entities : kind === 'department' ? lookups.departments : lookups.users;
      const id = resolveRef(list, value);
      if (id) set(field as 'typeId', id);
    }
  }
  if (fields.includes('endDate')) next.openEnded = false;
  return { draft: next, fields: [...new Set(fields)] };
}
