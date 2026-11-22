import { formatNumber } from './displayFormat';
import type { ContractStatus } from '../api/types';

/** Indicative rates to DKK, used only for roll-ups and approval value bands. */
export const FX_TO_DKK: Record<string, number> = {
  DKK: 1,
  EUR: 7.46,
  SEK: 0.65,
  USD: 6.9,
  PLN: 1.74,
  CNY: 0.96,
  GBP: 8.7,
  NOK: 0.63,
};

export function toDkk(amount: number, currency: string): number {
  return amount * (FX_TO_DKK[currency] ?? 1);
}

// Release 4: numbers follow the number format in General settings (lib/displayFormat.ts).
const nf = { format: (n: number) => formatNumber(n, 0) };
const nf1 = { format: (n: number) => formatNumber(n, 1) };

/** "DKK 1,250,000" */
export function formatMoney(amount: number | null | undefined, currency: string, fallback = '–'): string {
  if (amount === null || amount === undefined) return fallback;
  return `${currency} ${nf.format(amount)}`;
}

/** "DKK 4.2m", "DKK 850k" */
export function formatMoneyCompact(amount: number | null | undefined, currency: string, fallback = '–'): string {
  if (amount === null || amount === undefined) return fallback;
  const abs = Math.abs(amount);
  if (abs >= 1_000_000) return `${currency} ${nf1.format(amount / 1_000_000)}m`;
  if (abs >= 1_000) return `${currency} ${nf.format(amount / 1_000)}k`;
  return `${currency} ${nf.format(amount)}`;
}

export function formatFileSize(bytes: number): string {
  if (bytes >= 1_048_576) return `${nf1.format(bytes / 1_048_576)} MB`;
  if (bytes >= 1024) return `${nf.format(bytes / 1024)} KB`;
  return `${bytes} B`;
}

export function plural(n: number, one: string, many = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`;
}

export type Tone = 'red' | 'amber' | 'green' | 'blue' | 'gray';

const STATUS_LABEL: Record<string, string> = {
  Draft: 'Draft',
  InApproval: 'In approval',
  Approved: 'Approved',
  Active: 'Active',
  NoticeGiven: 'Notice given',
  Expired: 'Expired',
  Terminated: 'Terminated',
};

const STATUS_TONE: Record<string, Tone> = {
  Draft: 'gray',
  InApproval: 'blue',
  Approved: 'amber',
  Active: 'green',
  NoticeGiven: 'amber',
  Expired: 'gray',
  Terminated: 'gray',
};

export const ALL_STATUSES: ContractStatus[] = [
  'Draft', 'InApproval', 'Approved', 'Active', 'NoticeGiven', 'Expired', 'Terminated',
];

export function statusLabel(status: string): string {
  return STATUS_LABEL[status] ?? status;
}

export function statusTone(status: string): Tone {
  return STATUS_TONE[status] ?? 'gray';
}

export function initialsOf(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const first = parts[0]?.[0] ?? '';
  const last = parts.length > 1 ? (parts[parts.length - 1]?.[0] ?? '') : '';
  return (first + last).toUpperCase();
}

export function fileExtension(name: string): string {
  const m = /\.([a-z0-9]+)$/i.exec(name);
  return (m?.[1] ?? 'file').toUpperCase().slice(0, 4);
}

/** Saves a blob through a temporary link (CSV export, document download). */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
