import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter } from 'react-router-dom';
import '@fontsource/inter/latin-400.css';
import '@fontsource/inter/latin-ext-400.css';
import '@fontsource/inter/latin-500.css';
import '@fontsource/inter/latin-ext-500.css';
import '@fontsource/inter/latin-600.css';
import '@fontsource/inter/latin-ext-600.css';
import '@fontsource/inter/latin-700.css';
import '@fontsource/inter/latin-ext-700.css';
import './styles/tokens.css';
import './styles/base.css';
import './styles/components.css';
import './styles/shell.css';
import './styles/pages.css';
import './styles/screens.css';
import { loadApi } from './api';
import { App } from './App';
import { ApiProvider } from './app/context';
import { createQueryClient } from './app/queryClient';
import { ToastProvider } from './components';

const queryClient = createQueryClient();

const root = document.getElementById('root');
if (!root) throw new Error('Root element missing');

// The API is created first: in mock mode this loads the mock chunk, in real mode it is immediate.
const api = await loadApi();

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <ApiProvider api={api}>
        <ToastProvider>
          <BrowserRouter>
            <App />
          </BrowserRouter>
        </ToastProvider>
      </ApiProvider>
    </QueryClientProvider>
  </StrictMode>,
);
