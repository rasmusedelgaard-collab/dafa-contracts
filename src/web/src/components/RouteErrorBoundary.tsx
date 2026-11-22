import { Component, type ReactNode } from 'react';
import { isChunkLoadError } from '../lib/lazyPage';
import { Button } from './Button';
import { Card } from './Card';
import { ErrorState } from './EmptyState';

interface Props {
  children: ReactNode;
}
interface State {
  error: unknown;
}

/**
 * Catches a page that failed to load or render, so the shell and the navigation stay usable.
 * Keyed on the path by the shell: moving to another page clears it.
 */
export class RouteErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: unknown): State {
    return { error };
  }

  render() {
    const { error } = this.state;
    if (!error) return this.props.children;
    if (isChunkLoadError(error)) {
      return (
        <div className="page" style={{ paddingTop: 10 }}>
          <Card>
            <div className="empty" role="alert">
              <h2 className="h2">A new version is available</h2>
              <p className="secondary">DAFA Contracts was updated while this page was open. Reload to continue.</p>
              <Button onClick={() => window.location.reload()}>Reload</Button>
            </div>
          </Card>
        </div>
      );
    }
    return (
      <div className="page" style={{ paddingTop: 10 }}>
        <Card>
          <ErrorState title="This page could not be shown" text="Try again. If it keeps happening, reload the browser." onRetry={() => this.setState({ error: null })} />
        </Card>
      </div>
    );
  }
}
