import type { HTMLAttributes, ReactNode } from 'react';

export interface CardProps extends HTMLAttributes<HTMLElement> {
  as?: 'section' | 'div' | 'aside' | 'article';
  /** Standard 16/18 padding. Leave off for cards that hold full-width rows or tables. */
  pad?: boolean;
  clip?: boolean;
  tone?: 'surface' | 'tint' | 'primary';
  children?: ReactNode;
}

export function Card({ as: Tag = 'section', pad, clip, tone = 'surface', className, children, ...rest }: CardProps) {
  const cls = ['card', pad && 'card--pad', clip && 'card--clip', tone !== 'surface' && `card--${tone}`, className].filter(Boolean).join(' ');
  return (
    <Tag className={cls} {...rest}>
      {children}
    </Tag>
  );
}

export function CardTitle({ children, action, id }: { children: ReactNode; action?: ReactNode; id?: string }) {
  return (
    <div className="card__head">
      <h2 className="card-title" id={id}>
        {children}
      </h2>
      {action}
    </div>
  );
}
