import { ApiError } from '../api/api';

/** Turns any thrown value into a sentence for the user. */
export function errorMessage(error: unknown, fallback = 'Something went wrong. Try again.'): string {
  if (error instanceof ApiError) {
    if (error.status === 0) return 'The server could not be reached. Check your connection and try again.';
    return error.problem.detail ?? error.problem.title ?? fallback;
  }
  return fallback;
}

/**
 * For a 409 on approve or submit: someone else acted first. The server's own explanation, when
 * it gives one, follows the standard sentence.
 */
export function staleMessage(error: unknown): string {
  const base = 'This was just updated by someone else.';
  const detail = error instanceof ApiError ? error.problem.detail : undefined;
  return detail && detail !== base ? `${base} ${detail}` : base;
}
