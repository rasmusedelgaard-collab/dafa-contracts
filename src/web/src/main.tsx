import './polyfills';
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
import './styles/r3.css';
import './styles/r4.css';
import { loadApi } from './api';
import { App } from './App';
import { ApiProvider } from './app/context';
import { createQueryClient } from './app/queryClient';
import { ToastProvider } from './components';

const queryClient = createQueryClient();

const root = document.getElementById('root');
if (!root) throw new Error('Root element missing');
const container: HTMLElement = root;

/** Nothing could be shown: say so in plain HTML (no React, no styles needed) instead of a blank page. */
function showStartError(error: unknown): void {
  const box = document.createElement('div');
  box.setAttribute('role', 'alert');
  box.style.cssText = 'max-width:460px;margin:15vh auto;padding:24px;font:16px/1.5 system-ui,sans-serif';
  const title = document.createElement('h1');
  title.style.cssText = 'font-size:20px;margin:0 0 8px';
  title.textContent = 'DAFA Contracts could not start';
  const text = document.createElement('p');
  text.textContent = `Reload the page. If it keeps happening, tell us this message: ${error instanceof Error ? `${error.name}: ${error.message}` : String(error)}`;
  box.append(title, text);
  container.replaceChildren(box);
}

// The API is created first: in mock mode this loads the mock chunk, in real mode it is immediate.
// A promise chain, not top-level await: Safari 14 does not have it, and a module that fails to
// parse there shows nothing at all.
loadApi()
  .then((api) => {
    createRoot(container).render(
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
  })
  .catch(showStartError);
