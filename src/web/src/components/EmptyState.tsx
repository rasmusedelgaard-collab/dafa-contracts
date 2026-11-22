import type { ReactNode } from 'react';
import { Icon, type IconName } from './Icon';

export function EmptyState({ icon, title, text, action, compact }: { icon?: IconName; title: string; text?: ReactNode; action?: ReactNode; compact?: boolean }) {
  return (
    <div className={`empty${compact ? ' empty--compact' : ''}`}>
      {icon && !compact ? (
        <span className="tile tile--lg" aria-hidden="true">
          <Icon name={icon} />
        </span>
      ) : null}
      <span className="empty__title">{title}</span>
      {text ? <span className="empty__text">{text}</span> : null}
      {action ? <div className="empty__action">{action}</div> : null}
    </div>
  );
}

/** Full-card error with a retry button, for failed page loads. */
export function ErrorState({ title = 'This could not be loaded', text, onRetry }: { title?: string; text?: string; onRetry?: () => void }) {
  return (
    <div className="empty" role="alert">
      <span className="tile tile--lg tone-red" aria-hidden="true">
        <Icon name="alert" />
      </span>
      <span className="empty__title">{title}</span>
      {text ? <span className="empty__text">{text}</span> : null}
      {onRetry ? (
        <div className="empty__action">
          <button type="button" className="btn btn--secondary btn--md" onClick={onRetry}>
            Try again
          </button>
        </div>
      ) : null}
    </div>
  );
}
