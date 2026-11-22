import { formatDateIn } from './displayFormat';
// Date helpers. All contract dates are ISO `yyyy-MM-dd` strings and are treated as
// calendar dates (UTC arithmetic, no time zones).

const DAY_MS = 86_400_000;

const MONTHS_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const MONTHS_LONG = [
  'January', 'February', 'March', 'April', 'May', 'June',
  'July', 'August', 'September', 'October', 'November', 'December',
];
const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

export function isIsoDate(value: string | null | undefined): value is string {
  if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const d = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === value;
}

function toUtc(iso: string): Date {
  return new Date(`${iso.slice(0, 10)}T00:00:00Z`);
}

function fromUtc(d: Date): string {
  return d.toISOString().slice(0, 10);
}

/** Today's date in the user's local calendar, as ISO. */
export function todayIso(now: Date = new Date()): string {
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

export function addDays(iso: string, days: number): string {
  return fromUtc(new Date(toUtc(iso).getTime() + days * DAY_MS));
}

/** Adds calendar months, clamping to the last day of the target month (31 Jan + 1 = 28/29 Feb). */
export function addMonths(iso: string, months: number): string {
  const d = toUtc(iso);
  const day = d.getUTCDate();
  const target = new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + months, 1));
  const lastDay = new Date(Date.UTC(target.getUTCFullYear(), target.getUTCMonth() + 1, 0)).getUTCDate();
  target.setUTCDate(Math.min(day, lastDay));
  return fromUtc(target);
}

/** Whole days from `from` to `to` (positive when `to` is later). */
export function daysBetween(from: string, to: string): number {
  return Math.round((toUtc(to).getTime() - toUtc(from).getTime()) / DAY_MS);
}

/** noticeDeadline = endDate − noticePeriodDays. Null when open-ended or the notice period is unknown. */
export function noticeDeadline(endDate: string | null, noticePeriodDays: number | null): string | null {
  if (!endDate || noticePeriodDays === null || noticePeriodDays < 0) return null;
  return addDays(endDate, -noticePeriodDays);
}

export function daysToNotice(
  endDate: string | null,
  noticePeriodDays: number | null,
  today: string,
): number | null {
  const deadline = noticeDeadline(endDate, noticePeriodDays);
  return deadline ? daysBetween(today, deadline) : null;
}

export interface PlannedReminder {
  kind: string;
  dueDate: string;
}

/** Reminder dates: one per `noticeDays` entry before the deadline, plus the owner escalation. */
export function reminderSchedule(
  deadline: string | null,
  noticeDays: number[],
  escalationDays: number,
): PlannedReminder[] {
  if (!deadline) return [];
  const plan = [...noticeDays]
    .sort((a, b) => b - a)
    .map((n) => ({ kind: `Notice${n}`, dueDate: addDays(deadline, -n) }));
  plan.push({ kind: 'Escalation', dueDate: addDays(deadline, -escalationDays) });
  return plan;
}

/** The first planned reminder on or after today, if any. */
export function nextReminderDate(plan: PlannedReminder[], today: string): string | null {
  const upcoming = plan.map((p) => p.dueDate).filter((d) => d >= today).sort();
  return upcoming[0] ?? null;
}

export type NoticeTone = 'red' | 'amber' | 'blue' | 'gray';

/** Color band used everywhere a notice countdown is shown: ≤30 red, ≤90 amber, later blue. */
export function noticeTone(days: number | null): NoticeTone {
  if (days === null) return 'gray';
  if (days <= 30) return 'red';
  if (days <= 90) return 'amber';
  return 'blue';
}

export function noticeLabel(days: number | null): string {
  if (days === null) return 'None';
  if (days < 0) return days === -1 ? '1 day overdue' : `${-days} days overdue`;
  if (days === 0) return 'Today';
  return days === 1 ? 'In 1 day' : `In ${days} days`;
}

/** "15 Dec 2026" */
export function formatDate(iso: string | null | undefined, fallback = '–'): string {
  if (!iso) return fallback;
  const d = toUtc(iso);
  if (Number.isNaN(d.getTime())) return fallback;
  // Release 4: the date format from General settings.
  return formatDateIn(d);
}

/** "15 December 2026" */
export function formatDateLong(iso: string | null | undefined, fallback = '–'): string {
  if (!iso) return fallback;
  const d = toUtc(iso);
  if (Number.isNaN(d.getTime())) return fallback;
  return `${d.getUTCDate()} ${MONTHS_LONG[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
}

/** "17 Aug" */
export function formatDayMonth(iso: string): string {
  const d = toUtc(iso);
  return `${d.getUTCDate()} ${MONTHS_SHORT[d.getUTCMonth()]}`;
}

/** "Sunday 22 November" */
export function formatWeekdayLong(iso: string): string {
  const d = toUtc(iso);
  return `${WEEKDAYS[d.getUTCDay()]} ${d.getUTCDate()} ${MONTHS_LONG[d.getUTCMonth()]}`;
}

/** "Dec" for a `yyyy-MM` or ISO date string. */
export function monthShort(yearMonth: string): string {
  const m = Number(yearMonth.slice(5, 7));
  return MONTHS_SHORT[m - 1] ?? yearMonth;
}

/** `yyyy-MM` key of a date. */
export function monthKey(iso: string): string {
  return iso.slice(0, 7);
}

/** "22 Nov 2026, 14:05" in the browser's time zone, for audit and outbox timestamps. */
export function formatDateTime(isoDateTime: string | null | undefined, fallback = '–'): string {
  if (!isoDateTime) return fallback;
  const d = new Date(isoDateTime);
  if (Number.isNaN(d.getTime())) return fallback;
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getDate()} ${MONTHS_SHORT[d.getMonth()]} ${d.getFullYear()}, ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** The length of a term for a tile: "36 months", "45 days". Inclusive of both dates. */
export function termLength(startDate: string, endDate: string): string {
  if (!isIsoDate(startDate) || !isIsoDate(endDate) || endDate < startDate) return '–';
  const days = daysBetween(startDate, endDate) + 1;
  if (days < 45) return `${days} ${days === 1 ? 'day' : 'days'}`;
  let months = 0;
  while (addMonths(startDate, months + 1) <= addDays(endDate, 1)) months += 1;
  // Round to the nearest month when the term does not end on a month boundary.
  if (daysBetween(addMonths(startDate, months), addDays(endDate, 1)) > 15) months += 1;
  return `${months} ${months === 1 ? 'month' : 'months'}`;
}
