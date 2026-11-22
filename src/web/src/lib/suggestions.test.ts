import { describe, expect, it } from 'vitest';
import type { Lookups } from '../api/types';
import { applySuggestions } from './suggestions';
import { emptyDraft } from './validation';

const lookups: Lookups = {
  entities: [{ id: 'e1', name: 'DAFA A/S' }],
  departments: [{ id: 'd1', name: 'Purchasing' }],
  contractTypes: [{ id: 't1', name: 'Logistics' }],
  users: [{ id: 'u1', name: 'Mette Kjær', initials: 'MK', departmentIds: ['d1'] }],
  currencies: ['DKK'],
};

describe('applying AI suggestions to the form', () => {
  it('fills text, lookup (by id or by name) and yes/no fields and reports them as pending', () => {
    const { draft, fields } = applySuggestions(
      emptyDraft({ openEnded: true }),
      [
        { field: 'counterpartyName', value: 'ScanFreight', confidence: 0.9 },
        { field: 'typeId', value: 't1', confidence: 0.8 },
        { field: 'entityId', value: 'dafa a/s', confidence: 0.8 },
        { field: 'endDate', value: '2028-12-31', confidence: 0.8 },
        { field: 'autoRenew', value: 'true', confidence: 0.7 },
        { field: 'changeOfControl', value: 'No', confidence: 0.7 },
      ],
      lookups,
    );
    expect(draft).toMatchObject({ counterpartyName: 'ScanFreight', typeId: 't1', entityId: 'e1', endDate: '2028-12-31', openEnded: false, autoRenew: true, changeOfControl: false });
    expect(fields).toEqual(['counterpartyName', 'typeId', 'entityId', 'endDate', 'autoRenew', 'changeOfControl']);
  });

  it('drops unknown fields, unknown lookup values and empty values', () => {
    const before = emptyDraft({ title: 'Keep me' });
    const { draft, fields } = applySuggestions(
      before,
      [
        { field: 'somethingElse', value: 'x', confidence: 1 },
        { field: 'departmentId', value: 'Legal', confidence: 1 },
        { field: 'title', value: '  ', confidence: 1 },
        { field: 'autoRenew', value: 'maybe', confidence: 1 },
      ],
      lookups,
    );
    expect(fields).toEqual([]);
    expect(draft).toEqual(before);
  });
});
