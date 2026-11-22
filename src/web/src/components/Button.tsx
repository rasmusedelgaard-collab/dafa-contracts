import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { Link, type LinkProps } from 'react-router-dom';
import { Icon, type IconName } from './Icon';

export type ButtonVariant = 'primary' | 'secondary' | 'tint' | 'ghost' | 'danger' | 'on-dark' | 'on-dark-ghost';
export type ButtonSize = 'lg' | 'md' | 'sm';

interface Look {
  variant?: ButtonVariant;
  size?: ButtonSize;
  icon?: IconName;
  block?: boolean;
  /** Icon-only button; `aria-label` is then required. */
  iconOnly?: boolean;
  children?: ReactNode;
}

function classes({ variant = 'primary', size = 'lg', block, iconOnly }: Look, extra?: string): string {
  return ['btn', variant !== 'primary' && `btn--${variant}`, size !== 'lg' && `btn--${size}`, block && 'btn--block', iconOnly && 'btn--icon', extra]
    .filter(Boolean)
    .join(' ');
}

const iconSize = (size: ButtonSize | undefined) => (size === 'lg' || size === undefined ? 16 : 15);

export interface ButtonProps extends Look, Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'> {
  loading?: boolean;
}

export function Button({ variant, size, icon, block, iconOnly, loading, children, className, disabled, type = 'button', ...rest }: ButtonProps) {
  return (
    <button type={type} className={classes({ variant, size, block, iconOnly }, className)} disabled={disabled || loading} aria-busy={loading || undefined} {...rest}>
      {loading ? <span className="btn__spinner" aria-hidden="true" /> : icon ? <Icon name={icon} size={iconSize(size)} stroke={2.2} /> : null}
      {children}
    </button>
  );
}

export interface ButtonLinkProps extends Look, Omit<LinkProps, 'children'> {}

/** A router link that looks like a button (navigation stays a real link). */
export function ButtonLink({ variant, size, icon, block, iconOnly, children, className, ...rest }: ButtonLinkProps) {
  return (
    <Link className={classes({ variant, size, block, iconOnly }, className)} {...rest}>
      {icon ? <Icon name={icon} size={iconSize(size)} stroke={2.2} /> : null}
      {children}
    </Link>
  );
}
