import { describe, expect, it } from 'vitest';
import {
  addDays,
  addMonths,
  daysBetween,
  daysToNotice,
  formatDate,
  formatWeekdayLong,
  isIsoDate,
  nextReminderDate,
  noticeDeadline,
  noticeLabel,
  noticeTone,
  reminderSchedule,
} from './dates';

describe('date arithmetic', () => {
  it('adds days across month, year and leap-day boundaries', () => {
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
    expect(addDays('2028-02-28', 1)).toBe('2028-02-29');
    expect(addDays('2027-03-01', -1)).toBe('2027-02-28');
  });

  it('is not shifted by daylight saving changes', () => {
    expect(addDays('2026-03-28', 2)).toBe('2026-03-30');
    expect(daysBetween('2026-10-24', '2026-10-26')).toBe(2);
  });

  it('clamps month arithmetic to the end of the month', () => {
    expect(addMonths('2027-01-31', 1)).toBe('2027-02-28');
    expect(addMonths('2028-01-31', 1)).toBe('2028-02-29');
    expect(addMonths('2026-11-30', 12)).toBe('2027-11-30');
    expect(addMonths('2027-03-15', -3)).toBe('2026-12-15');
  });

  it('validates ISO dates', () => {
    expect(isIsoDate('2026-02-28')).toBe(true);
    expect(isIsoDate('2026-02-30')).toBe(false);
    expect(isIsoDate('28-02-2026')).toBe(false);
    expect(isIsoDate('')).toBe(false);
    expect(isIsoDate(null)).toBe(false);
  });
});

describe('notice deadline', () => {
  it('is the end date minus the notice period (the approved design example)', () => {
    // Term ends 28 Feb 2027 with 75 days' notice -> 15 Dec 2026, 23 days from 22 Nov 2026.
    expect(noticeDeadline('2027-02-28', 75)).toBe('2026-12-15');
    expect(daysToNotice('2027-02-28', 75, '2026-11-22')).toBe(23);
  });

  it('equals the end date when there is no notice period', () => {
    expect(noticeDeadline('2027-06-30', 0)).toBe('2027-06-30');
  });

  it('is null for open-ended contracts and when the notice period is missing', () => {
    expect(noticeDeadline(null, 90)).toBeNull();
    expect(noticeDeadline('2027-06-30', null)).toBeNull();
    expect(daysToNotice(null, 90, '2026-11-22')).toBeNull();
  });

  it('goes negative once the deadline has passed', () => {
    expect(daysToNotice('2026-12-01', 30, '2026-11-22')).toBe(-21);
  });

  it('bands the countdown: 30 days or less red, 90 or less amber, later blue, none gray', () => {
    expect(noticeTone(-3)).toBe('red');
    expect(noticeTone(30)).toBe('red');
    expect(noticeTone(31)).toBe('amber');
    expect(noticeTone(90)).toBe('amber');
    expect(noticeTone(91)).toBe('blue');
    expect(noticeTone(null)).toBe('gray');
  });

  it('labels the countdown', () => {
    expect(noticeLabel(23)).toBe('In 23 days');
    expect(noticeLabel(1)).toBe('In 1 day');
    expect(noticeLabel(0)).toBe('Today');
    expect(noticeLabel(-5)).toBe('5 days overdue');
    expect(noticeLabel(null)).toBe('None');
  });
});

describe('reminder schedule', () => {
  it('plans 120, 60 and 30 days before the deadline and the escalation at 14', () => {
    expect(reminderSchedule('2026-12-15', [30, 120, 60], 14)).toEqual([
      { kind: 'Notice120', dueDate: '2026-08-17' },
      { kind: 'Notice60', dueDate: '2026-10-16' },
      { kind: 'Notice30', dueDate: '2026-11-15' },
      { kind: 'Escalation', dueDate: '2026-12-01' },
    ]);
  });

  it('has no reminders without a deadline', () => {
    expect(reminderSchedule(null, [120, 60, 30], 14)).toEqual([]);
  });

  it('finds the next reminder on or after today', () => {
    const plan = reminderSchedule('2026-12-15', [120, 60, 30], 14);
    expect(nextReminderDate(plan, '2026-11-22')).toBe('2026-12-01');
    expect(nextReminderDate(plan, '2026-10-16')).toBe('2026-10-16');
    expect(nextReminderDate(plan, '2026-12-02')).toBeNull();
  });
});

describe('formatting', () => {
  it('formats dates the way the design shows them', () => {
    expect(formatDate('2026-12-15')).toBe('15 Dec 2026');
    expect(formatDate(null)).toBe('–');
    expect(formatWeekdayLong('2026-11-22')).toBe('Sunday 22 November');
  });
});
