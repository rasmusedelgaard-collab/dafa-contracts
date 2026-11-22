// RFC 7807 errors raised by the mock, with the backend's problem types.
import { ApiError } from '../api';

export const PROBLEM_TYPES = {
  stale: 'urn:dafa:problem:stale',
  'no-approval-rule': 'urn:dafa:problem:no-approval-rule',
  'no-approver': 'urn:dafa:problem:no-approver',
  'locked-fields': 'urn:dafa:problem:locked-fields',
  duplicate: 'urn:dafa:problem:duplicate',
  'invalid-state': 'urn:dafa:problem:invalid-state',
  'in-use': 'urn:dafa:problem:in-use',
  'synced-field': 'urn:dafa:problem:synced-field',
  'import-state': 'urn:dafa:problem:import-state',
  'signature-state': 'urn:dafa:problem:signature-state',
} as const;
export type ConflictKind = keyof typeof PROBLEM_TYPES;

export function problem(status: number, title: string, detail?: string, errors?: Record<string, string[]>, type = 'about:blank'): ApiError {
  return new ApiError({ type, title, status, detail, errors });
}
export const notFound = (what = 'Contract') => problem(404, 'Not found', `${what} was not found, or you do not have access to it.`);
export const forbidden = (detail = 'You do not have permission to do this.') => problem(403, 'Forbidden', detail);
/** Every 409 carries one of the backend's problem types; `invalid-state` is the catch-all. */
export const conflict = (detail: string, kind: ConflictKind = 'invalid-state') => problem(409, 'Conflict', detail, undefined, PROBLEM_TYPES[kind]);
export const invalid = (detail: string, errors?: Record<string, string[]>) => problem(400, 'Validation failed', detail, errors);
