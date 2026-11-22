import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query';
import { ApiError } from '../api/api';
import { isConflict, isUnauthorized } from '../lib/errors';
import { noteSessionEnded } from './signin';

/** Query keys that describe the session itself; they survive a refresh after a conflict. */
const SESSION_KEYS = new Set(['me', 'dev-config', 'dev-users', 'lookups']);

/** The sign-in screens ask these themselves and handle their own 401. */
const SIGN_IN_KEYS = new Set(['me', 'dev-config', 'dev-users']);
const ME_KEY = ['me'] as const;

/**
 * The app's query client. Besides the query defaults it holds two rules for every call:
 * - a 409 on a mutation means what is on screen may no longer be true, so all data queries are
 *   refetched. The message for the user comes from `errorMessage` (lib/errors.ts) at the call site.
 * - a 401 on any query or mutation while the app believes someone is signed in means the session
 *   has ended (review finding M7): `me` is asked again, its 401 puts the sign-in screen up (Dev:
 *   access code and user picker; Entra: the redirect, with the current path as the return address).
 *   This happens once: until someone is signed in again, further 401s change nothing, and the
 *   Entra screen has its own guard against redirect loops (app/signin.ts).
 */
export function createQueryClient(options: { retry?: boolean } = {}): QueryClient {
  const sessionEnded = (error: unknown, key?: readonly unknown[]) => {
    if (!isUnauthorized(error)) return;
    if (key && SIGN_IN_KEYS.has(String(key[0]))) return;
    if (client.getQueryState(ME_KEY)?.status !== 'success') return;
    // The sign-in screen says why it is back ("Your session has ended", or that the cookie was not kept).
    noteSessionEnded();
    void client.resetQueries({ queryKey: ME_KEY });
  };
  const client: QueryClient = new QueryClient({
    queryCache: new QueryCache({
      onError: (error, query) => sessionEnded(error, query.queryKey),
    }),
    mutationCache: new MutationCache({
      onError: (error) => {
        if (isConflict(error)) void client.invalidateQueries({ predicate: (q) => !SESSION_KEYS.has(String(q.queryKey[0])) });
        sessionEnded(error);
      },
    }),
    defaultOptions: {
      queries: {
        staleTime: 15_000,
        refetchOnWindowFocus: false,
        // Do not retry what will not change: validation, permission and not-found answers.
        retry: options.retry === false ? false : (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2,
      },
    },
  });
  return client;
}
