import type { ReactNode } from 'react';
import { statusLabel, statusTone, type Tone } from '../lib/format';
import { noticeLabel, noticeTone } from '../lib/dates';

export function Pill({ tone = 'gray', small, children }: { tone?: Tone; small?: boolean; children: ReactNode }) {
  return <span className={`pill tone-${tone}${small ? ' pill--sm' : ''}`}>{children}</span>;
}

export function StatusPill({ status }: { status: string }) {
  return <Pill tone={statusTone(status)}>{statusLabel(status)}</Pill>;
}

const DOT: Record<Tone, string> = { green: 'green', amber: 'amber', blue: 'blue', red: 'red', gray: 'gray' };

/** Dot + label, as in the register's status column. */
export function StatusDot({ status }: { status: string }) {
  return <span className={`status-dot status-dot--${DOT[statusTone(status)]}`}>{statusLabel(status)}</span>;
}

/** Notice countdown pill: red within 30 days, amber within 90, blue later, gray when there is none. */
export function NoticePill({ days }: { days: number | null }) {
  return <Pill tone={noticeTone(days)}>{noticeLabel(days)}</Pill>;
}
