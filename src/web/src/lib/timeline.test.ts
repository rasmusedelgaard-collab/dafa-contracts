import { describe, expect, it } from 'vitest';
import type { RenewalItem } from '../api/types';
import { daysBetween } from './dates';
import { buildLanes, markerUrgency, onAxis, positionPct, timelineAxis } from './timeline';

const TODAY = '2026-11-22';
const axis = timelineAxis(TODAY);

let n = 0;
function item(noticeDeadline: string, overrides: Partial<RenewalItem> = {}): RenewalItem {
  n += 1;
  return { id: `c${n}`, title: `Contract ${n}`, entityId: 'e1', entityName: 'DAFA A/S', departmentName: 'Purchasing', noticeDeadline, daysToNotice: daysBetween(TODAY, noticeDeadline), autoRenew: true, annualValue: 100, currency: 'DKK', ...overrides };
}

describe('timeline axis', () => {
  it('starts on the first day of the current month and covers 12 months', () => {
    expect(axis.start).toBe('2026-11-01');
    expect(axis.end).toBe('2027-11-01');
    expect(axis.months).toHaveLength(12);
    expect(axis.months[0]).toBe('2026-11');
    expect(axis.months[11]).toBe('2027-10');
  });

  it('places today inside the first month', () => {
    // 21 of 30 November days have passed: (21 / 30) / 12 = 5.83%
    expect(axis.todayPct).toBeCloseTo(5.83, 2);
  });
});

describe('marker placement', () => {
  it('places a date by month column plus the share of the month that has passed', () => {
    expect(positionPct('2026-11-01', axis)).toBe(0);
    // 15 December: second column, 14 of 31 days in -> (1 + 14/31) / 12
    expect(positionPct('2026-12-15', axis)).toBeCloseTo(((1 + 14 / 31) / 12) * 100, 1);
    // 1 May is exactly the start of the seventh column
    expect(positionPct('2027-05-01', axis)).toBe(50);
    expect(positionPct('2027-10-31', axis)).toBeGreaterThan(99);
    expect(positionPct('2027-10-31', axis)).toBeLessThan(100);
  });

  it('clamps dates outside the axis', () => {
    expect(positionPct('2026-01-01', axis)).toBe(0);
    expect(positionPct('2028-01-01', axis)).toBe(100);
  });

  it('colors by urgency: red within 30 days, amber within 90, blue later', () => {
    expect(markerUrgency(0)).toBe('red');
    expect(markerUrgency(30)).toBe('red');
    expect(markerUrgency(31)).toBe('amber');
    expect(markerUrgency(90)).toBe('amber');
    expect(markerUrgency(91)).toBe('blue');
    expect(markerUrgency(-5)).toBe('red');
  });

  it('keeps only deadlines the axis shows, plus overdue ones', () => {
    const inside = item('2027-10-31');
    const after = item('2027-11-01');
    const overdue = item('2026-10-20');
    expect(onAxis([inside, after, overdue], axis).map((i) => i.id)).toEqual([inside.id, overdue.id]);
  });

  it('pins an overdue deadline at the left edge, in red', () => {
    const overdue = item('2026-11-10');
    expect(overdue.daysToNotice).toBe(-12);
    const [lane] = buildLanes([overdue], axis);
    expect(lane?.markers[0]).toMatchObject({ leftPct: 0, urgency: 'red' });
  });
});

describe('lanes', () => {
  const a = item('2026-12-15', { entityId: 'e1', entityName: 'DAFA A/S', departmentName: 'Purchasing' });
  const b = item('2027-03-01', { entityId: 'e2', entityName: 'DAFA Sverige AB', departmentName: 'Purchasing' });
  const c = item('2027-06-10', { entityId: 'e1', entityName: 'DAFA A/S', departmentName: 'IT' });

  it('makes one lane per legal entity, sorted by name', () => {
    const lanes = buildLanes([c, b, a], axis, 'entity');
    expect(lanes.map((l) => l.name)).toEqual(['DAFA A/S', 'DAFA Sverige AB']);
    expect(lanes[0]?.markers.map((m) => m.item.id)).toEqual([a.id, c.id]);
    expect(lanes[1]?.markers.map((m) => m.item.id)).toEqual([b.id]);
  });

  it('groups by department instead when asked', () => {
    const lanes = buildLanes([a, b, c], axis, 'department');
    expect(lanes.map((l) => l.name)).toEqual(['IT', 'Purchasing']);
    expect(lanes[1]?.markers.map((m) => m.item.id)).toEqual([a.id, b.id]);
  });

  it('moves a marker down a row when it would overlap another in the same lane', () => {
    const first = item('2027-02-10', { title: 'A' });
    const sameWeek = item('2027-02-12', { title: 'C' });
    const sameDay = item('2027-02-10', { title: 'B' });
    const later = item('2027-04-20');
    const [lane] = buildLanes([first, sameWeek, sameDay, later], axis);
    const row = (id: string) => lane?.markers.find((m) => m.item.id === id)?.row;
    expect(row(first.id)).toBe(0);
    expect(row(sameDay.id)).toBe(1);
    expect(row(sameWeek.id)).toBe(2);
    expect(row(later.id)).toBe(0);
    expect(lane?.rows).toBe(3);
  });

  it('gives every marker a position between 0 and 100', () => {
    const lanes = buildLanes([a, b, c, item('2026-11-22'), item('2027-10-31')], axis);
    for (const m of lanes.flatMap((l) => l.markers)) {
      expect(m.leftPct).toBeGreaterThanOrEqual(0);
      expect(m.leftPct).toBeLessThanOrEqual(100);
    }
  });
});
