import { ApiError } from '../api/api';

/** Turns any thrown value into a sentence for the user. */
export function errorMessage(error: unknown, fallback = 'Something went wrong. Try again.'): string {
  if (error instanceof ApiError) {
    if (error.status === 0) return 'The server could not be reached. Check your connection and try again.';
    return error.problem.detail ?? error.problem.title ?? fallback;
  }
  return fallback;
}
