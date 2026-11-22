// Number and date display formats from General settings (owner item D). The settings are global
// for the app; `setDisplayFormats` is called when app-config arrives, before pages render.
// Numbers are always sent to the API as JSON numbers; this module only formats and parses text.
import type { DateFormat, NumberFormat } from '../api/types';

let numberFormat: NumberFormat = 'en-GB';
let dateFormat: DateFormat = 'd MMM yyyy';

export function setDisplayFormats(date: DateFormat | undefined, number: NumberFormat | undefined): void {
  if (date) dateFormat = date;
  if (number) numberFormat = number;
}
export const currentDateFormat = (): DateFormat => dateFormat;
export const currentNumberFormat = (): NumberFormat => numberFormat;

export interface Separators {
  group: string;
  decimal: string;
}
export function separators(fmt: NumberFormat = numberFormat): Separators {
  return fmt === 'da-DK' || fmt === 'de-DE' ? { group: '.', decimal: ',' } : { group: ',', decimal: '.' };
}

function group(digits: string, sep: string): string {
  return digits.replace(/\B(?=(\d{3})+(?!\d))/g, sep);
}

/** 1250000.5 -> "1,250,000.5" / "1.250.000,5". `maxFraction` rounds. */
export function formatNumber(n: number, maxFraction = 0, fmt: NumberFormat = numberFormat): string {
  if (!Number.isFinite(n)) return String(n);
  const { group: g, decimal } = separators(fmt);
  const fixed = Math.abs(n).toFixed(maxFraction);
  const [int = '0', frac = ''] = fixed.split('.');
  const trimmed = frac.replace(/0+$/, '');
  return `${n < 0 && Number(fixed) !== 0 ? '-' : ''}${group(int, g)}${trimmed ? decimal + trimmed : ''}`;
}

/**
 * Canonical text ("1250000.5", what the form state keeps) -> display text in the format, while
 * typing too: a trailing decimal separator and trailing zeros after it are kept.
 */
export function displayAmount(canonical: string, fmt: NumberFormat = numberFormat): string {
  if (!canonical) return '';
  const { group: g, decimal } = separators(fmt);
  const neg = canonical.startsWith('-');
  const [int = '', frac] = canonical.replace('-', '').split('.');
  const intPart = int === '' && frac !== undefined ? '0' : group(int.replace(/^0+(?=\d)/, ''), g);
  return `${neg ? '-' : ''}${intPart}${frac !== undefined ? decimal + frac : ''}`;
}

/**
 * Display text as typed -> canonical text. Group separators and spaces are dropped; the format's
 * decimal separator becomes "."; anything else that is not a digit is dropped. `decimals` = how
 * many fraction digits are allowed (0 for whole numbers).
 */
export function canonicalAmount(text: string, decimals = 2, fmt: NumberFormat = numberFormat): string {
  const { decimal } = separators(fmt);
  let out = '';
  let seenDecimal = false;
  let frac = 0;
  for (const [i, ch] of [...text].entries()) {
    if (ch >= '0' && ch <= '9') {
      if (seenDecimal) {
        if (frac >= decimals) continue;
        frac += 1;
      }
      out += ch;
    } else if (ch === decimal && decimals > 0 && !seenDecimal) {
      seenDecimal = true;
      out += '.';
    } else if (ch === '-' && i === 0) out += '-';
  }
  return out;
}

/** Parses display text in the format to a number (null when empty, NaN when invalid). */
export function parseDisplayAmount(text: string, fmt: NumberFormat = numberFormat): number | null {
  const t = text.trim();
  if (t === '') return null;
  const { group: g, decimal } = separators(fmt);
  const plain = t.split(g).join('').replace(/\s/g, '').replace(decimal, '.');
  if (!/^-?\d+(\.\d+)?$/.test(plain)) return Number.NaN;
  return Number(plain);
}

/**
 * Where the caret belongs after reformatting: after the same number of significant characters
 * (digits, decimal separator, minus) as before it in the raw text.
 */
export function caretAfterFormat(raw: string, rawCaret: number, formatted: string, fmt: NumberFormat = numberFormat): number {
  const { decimal } = separators(fmt);
  const significant = (ch: string) => (ch >= '0' && ch <= '9') || ch === decimal || ch === '-';
  let count = 0;
  for (let i = 0; i < rawCaret && i < raw.length; i += 1) if (significant(raw[i] as string)) count += 1;
  if (count === 0) return 0;
  let seen = 0;
  for (let i = 0; i < formatted.length; i += 1) {
    if (significant(formatted[i] as string)) seen += 1;
    if (seen === count) return i + 1;
  }
  return formatted.length;
}

const MONTHS_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const pad = (n: number) => String(n).padStart(2, '0');

/** A UTC date in the General settings date format. */
export function formatDateIn(d: Date, fmt: DateFormat = dateFormat): string {
  const day = d.getUTCDate();
  const month = d.getUTCMonth();
  const year = d.getUTCFullYear();
  switch (fmt) {
    case 'dd-MM-yyyy':
      return `${pad(day)}-${pad(month + 1)}-${year}`;
    case 'dd.MM.yyyy':
      return `${pad(day)}.${pad(month + 1)}.${year}`;
    case 'yyyy-MM-dd':
      return `${year}-${pad(month + 1)}-${pad(day)}`;
    case 'MM/dd/yyyy':
      return `${pad(month + 1)}/${pad(day)}/${year}`;
    default:
      return `${day} ${MONTHS_SHORT[month]} ${year}`;
  }
}
