import { ApiError } from '../api/api';
import { PROBLEM, STORAGE_PROBLEMS } from '../api/types';

export const STORAGE_BUSY_MESSAGE = 'SharePoint is busy. Try again in a moment.';
export const FILE_MISSING_MESSAGE = 'This file can no longer be found in storage. An administrator has been notified.';

export type StorageProblem = 'unavailable' | 'fileMissing' | 'notConfigured';
/** Which storage problem (CONTRACT_V4 §9a) an error is, if any. */
export function storageProblem(error: unknown): StorageProblem | null {
  if (!(error instanceof ApiError)) return null;
  const t = error.problem.type;
  return t === STORAGE_PROBLEMS.unavailable ? 'unavailable' : t === STORAGE_PROBLEMS.fileMissing ? 'fileMissing' : t === STORAGE_PROBLEMS.notConfigured ? 'notConfigured' : null;
}

export const STALE_MESSAGE = 'This was just updated by someone else.';
export const THROTTLED_MESSAGE = 'Too many attempts. Try again in a few minutes.';

/** 409 `urn:dafa:problem:stale`: someone else changed the same thing at the same time. */
export function isStale(error: unknown): boolean {
  return error instanceof ApiError && error.status === 409 && error.problem.type === PROBLEM.stale;
}

/** Any 409: what is on screen may be out of date, so the caller refetches. */
export function isConflict(error: unknown): boolean {
  return error instanceof ApiError && error.status === 409;
}

/** 429: the access code was wrong too many times (also returned for the right code for a while). */
export function isThrottled(error: unknown): boolean {
  return error instanceof ApiError && error.status === 429;
}

export const SESSION_ENDED_MESSAGE = 'Your session has ended. Sign in again.';
export const NO_ACCESS_MESSAGE = 'Your account does not have access to DAFA Contracts. Contact an administrator.';

/** 401: nobody is signed in (any more). */
export function isUnauthorized(error: unknown): boolean {
  return error instanceof ApiError && error.status === 401;
}

/**
 * The form-field name for a key of a problem's `errors`. The API names fields in several ways:
 * `signers[0].userId` (services), `$.signers[0].userId` and `Signers[0].UserId` (model binding).
 * The forms use `signers.0.userId`.
 */
export function fieldKey(key: string): string {
  const parts = key
    .replace(/^\$\.?/, '')
    .replace(/\[(\d+)\]/g, '.$1')
    .replace(/^\./, '')
    .split('.');
  // Only a PascalCase path from model binding is lowered; `cf.<key>` custom-field keys keep their spelling.
  if (!/^[A-Z]/.test(parts[0] ?? '')) return parts.join('.');
  return parts.map((part) => part.charAt(0).toLowerCase() + part.slice(1)).join('.');
}

/**
 * Field errors of a 400 problem, keyed by form-field name, first message per field.
 * `aliases` maps API field names onto the form's own names (e.g. `supplierId` -> `counterpartyName`).
 */
export function fieldErrors(error: unknown, aliases: Record<string, string> = {}): Record<string, string> {
  if (!(error instanceof ApiError) || !error.problem.errors) return {};
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(error.problem.errors)) {
    const message = (Array.isArray(v) ? v : [v]).find((m) => typeof m === 'string' && m.trim() !== '');
    if (!message) continue;
    const key = fieldKey(k);
    const name = aliases[key] ?? key;
    if (!(name in out)) out[name] = message;
  }
  return out;
}

/** The message for one field of a 400 problem, if the API named that field. */
export function fieldError(error: unknown, field: string): string | undefined {
  return fieldErrors(error)[field];
}

/**
 * The sentences of a 400 problem. The API's `detail` is always "One or more fields are invalid.";
 * the reason is in `errors[field][0]`. Returns the first message of each field (at most three,
 * without repeats), or undefined when the problem names no field.
 */
export function validationMessage(error: unknown): string | undefined {
  const messages = [...new Set(Object.values(fieldErrors(error)).map((m) => m.trim()))];
  if (messages.length === 0) return undefined;
  const shown = messages.slice(0, 3).map((m) => (/[.!?]$/.test(m) ? m : `${m}.`));
  return shown.join(' ') + (messages.length > shown.length ? ` (and ${messages.length - shown.length} more)` : '');
}

/**
 * Turns any thrown value into a sentence for the user.
 * - 400 with `errors`: the real validation sentence(s), not the generic `detail`.
 * - 401: the session has ended (the app also returns to sign-in, see app/queryClient.ts).
 * - 409 `stale` has one fixed sentence; the caller refetches.
 * - Every other 409 (`no-approval-rule`, `no-approver`, `locked-fields`, `duplicate`,
 *   `invalid-state`) shows the server's `detail`, which is written for the user.
 * - 429 has one fixed sentence.
 */
export function errorMessage(error: unknown, fallback = 'Something went wrong. Try again.'): string {
  if (error instanceof ApiError) {
    if (error.status === 0) return 'The server could not be reached. Check your connection and try again.';
    if (error.problem.type === STORAGE_PROBLEMS.unavailable) return STORAGE_BUSY_MESSAGE;
    if (error.problem.type === STORAGE_PROBLEMS.fileMissing) return FILE_MISSING_MESSAGE;
    if (isStale(error)) return STALE_MESSAGE;
    if (isThrottled(error)) return THROTTLED_MESSAGE;
    if (isUnauthorized(error)) return SESSION_ENDED_MESSAGE;
    if (error.status === 400) {
      const found = validationMessage(error);
      if (found) return found;
    }
    return error.problem.detail ?? error.problem.title ?? fallback;
  }
  return fallback;
}
