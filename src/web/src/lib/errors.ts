import { ApiError } from '../api/api';
import { PROBLEM } from '../api/types';

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

/**
 * Turns any thrown value into a sentence for the user.
 * - 409 `stale` has one fixed sentence; the caller refetches.
 * - Every other 409 (`no-approval-rule`, `no-approver`, `locked-fields`, `duplicate`,
 *   `invalid-state`) shows the server's `detail`, which is written for the user.
 * - 429 has one fixed sentence.
 */
export function errorMessage(error: unknown, fallback = 'Something went wrong. Try again.'): string {
  if (error instanceof ApiError) {
    if (error.status === 0) return 'The server could not be reached. Check your connection and try again.';
    if (isStale(error)) return STALE_MESSAGE;
    if (isThrottled(error)) return THROTTLED_MESSAGE;
    return error.problem.detail ?? error.problem.title ?? fallback;
  }
  return fallback;
}
