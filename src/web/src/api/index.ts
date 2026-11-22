import type { Api } from './api';
import { createHttpApi } from './client';

export type ApiMode = 'mock' | 'real';

/** `VITE_API_MODE`; `npm run dev` defaults to mock, a production build to real. */
export const apiMode: ApiMode = __API_MODE__ === 'real' ? 'real' : 'mock';

/**
 * Creates the API the app talks to. The mock (and its seed data) is loaded with a dynamic
 * import behind a build-time constant, so a real-mode build does not contain it at all.
 */
export async function loadApi(): Promise<Api> {
  if (__API_MODE__ !== 'real') {
    const { createMockApi } = await import('./mock/mockApi');
    // Set VITE_MOCK_ACCESS_CODE to try the access-code screen against the mock.
    const accessCode = import.meta.env.VITE_MOCK_ACCESS_CODE || undefined;
    return createMockApi({ latencyMs: 180, rememberUser: true, accessCode });
  }
  return createHttpApi();
}

export { ApiError } from './api';
export type { Api } from './api';
