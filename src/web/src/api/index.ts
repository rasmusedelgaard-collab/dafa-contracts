import type { Api } from './api';
import { createHttpApi } from './client';
import { createMockApi } from './mock/mockApi';

export type ApiMode = 'mock' | 'real';

/** `VITE_API_MODE`; `npm run dev` defaults to mock, a production build to real. */
export const apiMode: ApiMode = __API_MODE__ === 'real' ? 'real' : 'mock';

export function createApi(mode: ApiMode = apiMode): Api {
  return mode === 'real' ? createHttpApi() : createMockApi({ latencyMs: 180, rememberUser: true });
}

export { ApiError } from './api';
export type { Api } from './api';
