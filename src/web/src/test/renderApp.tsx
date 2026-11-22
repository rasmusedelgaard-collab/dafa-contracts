import { render } from '@testing-library/react';
import { QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { App } from '../App';
import type { Api } from '../api/api';
import { ApiProvider } from '../app/context';
import { createQueryClient } from '../app/queryClient';
import { createMockApi, type MockApi, type MockOptions } from '../api/mock/mockApi';
import { USER } from '../api/mock/seed';
import { ToastProvider } from '../components';

export const TODAY = '2026-11-22';

/** Renders the whole app at `path` against any Api implementation. */
export function renderWith(api: Api, path: string): void {
  // The app's own client (with its conflict rule), without retries so failures show at once.
  const client = createQueryClient({ retry: false });
  render(
    <QueryClientProvider client={client}>
      <ApiProvider api={api}>
        <ToastProvider>
          <MemoryRouter initialEntries={[path]}>
            <App />
          </MemoryRouter>
        </ToastProvider>
      </ApiProvider>
    </QueryClientProvider>,
  );
}

/** Renders the app against a fresh mock with a fixed date, signed in as `userId`. */
export function renderApp(path: string, userId: string = USER.ras, options: MockOptions = {}): MockApi {
  const api = createMockApi({ today: TODAY, userId, ...options });
  renderWith(api, path);
  return api;
}
