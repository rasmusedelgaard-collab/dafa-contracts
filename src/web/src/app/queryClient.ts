import { MutationCache, QueryClient } from '@tanstack/react-query';
import { ApiError } from '../api/api';
import { isConflict } from '../lib/errors';

/** Query keys that describe the session itself; they survive a refresh after a conflict. */
const SESSION_KEYS = new Set(['me', 'dev-config', 'dev-users', 'lookups']);

/**
 * The app's query client. Besides the query defaults it holds one rule for every mutation:
 * a 409 means what is on screen may no longer be true, so all data queries are refetched.
 * The message for the user comes from `errorMessage` (lib/errors.ts) at the call site.
 */
export function createQueryClient(options: { retry?: boolean } = {}): QueryClient {
  const client: QueryClient = new QueryClient({
    mutationCache: new MutationCache({
      onError: (error) => {
        if (isConflict(error)) void client.invalidateQueries({ predicate: (q) => !SESSION_KEYS.has(String(q.queryKey[0])) });
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
