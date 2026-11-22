// Pure layout for the renewals timeline: where each marker sits on a 12-month axis.
import type { RenewalItem } from '../api/types';
import { addMonths, daysBetween } from './dates';

export type MarkerUrgency = 'red' | 'amber' | 'blue';
export type LaneGrouping = 'entity' | 'department';

export interface TimelineAxis {
  /** First day shown (the 1st of the current month). */
  start: string;
  /** First day after the axis (the 1st, `months` later). */
  end: string;
  /** `yyyy-MM` per column. */
  months: string[];
  /** Where today sits on the axis, in percent. */
  todayPct: number;
}

export interface Marker {
  item: RenewalItem;
  /** Horizontal position in percent of the lane width (0–100). */
  leftPct: number;
  /** 0 for the first row; markers that would overlap move down a row. */
  row: number;
  urgency: MarkerUrgency;
}

export interface Lane {
  key: string;
  name: string;
  markers: Marker[];
  rows: number;
}

/** Red within 30 days of the notice deadline (and when it has passed), amber within 90, blue later. */
export function markerUrgency(daysToNotice: number): MarkerUrgency {
  return daysToNotice <= 30 ? 'red' : daysToNotice <= 90 ? 'amber' : 'blue';
}

/** A 12-month axis that starts on the first day of the month `today` is in. */
export function timelineAxis(today: string, months = 12): TimelineAxis {
  const start = `${today.slice(0, 7)}-01`;
  const end = addMonths(start, months);
  return {
    start,
    end,
    months: Array.from({ length: months }, (_, i) => addMonths(start, i).slice(0, 7)),
    todayPct: positionPct(today, { start, end, months: months }),
  };
}

/**
 * Position of a date on the axis in percent. Every month column has the same width, so the
 * position is the month index plus the share of that month that has passed.
 */
export function positionPct(date: string, axis: { start: string; end: string; months: number | string[] }): number {
  const count = typeof axis.months === 'number' ? axis.months : axis.months.length;
  if (date <= axis.start) return 0;
  if (date >= axis.end) return 100;
  const monthIndex = (Number(date.slice(0, 4)) - Number(axis.start.slice(0, 4))) * 12 + (Number(date.slice(5, 7)) - Number(axis.start.slice(5, 7)));
  const monthStart = `${date.slice(0, 7)}-01`;
  const daysInMonth = daysBetween(monthStart, addMonths(monthStart, 1));
  const share = daysBetween(monthStart, date) / daysInMonth;
  return Math.round(((monthIndex + share) / count) * 10000) / 100;
}

/** A deadline that passed without a decision (the API reports a negative `daysToNotice`). */
export const isOverdue = (item: RenewalItem): boolean => item.daysToNotice < 0;

/**
 * Deadlines the axis can show: from its start up to (not including) its end, plus every
 * overdue one, however old, because those still need a decision.
 */
export function onAxis(items: RenewalItem[], axis: TimelineAxis): RenewalItem[] {
  return items.filter((i) => isOverdue(i) || (i.noticeDeadline >= axis.start && i.noticeDeadline < axis.end));
}

/**
 * Groups the renewals into lanes (one per legal entity, or per department) and places a marker
 * per contract. Markers closer than `minGapPct` to one already in the same row move down a row,
 * so two deadlines in the same week stay separately clickable.
 */
export function buildLanes(items: RenewalItem[], axis: TimelineAxis, grouping: LaneGrouping = 'entity', minGapPct = 2.6): Lane[] {
  const lanes = new Map<string, Lane>();
  const sorted = onAxis(items, axis).sort((a, b) => a.noticeDeadline.localeCompare(b.noticeDeadline) || a.title.localeCompare(b.title));
  for (const item of sorted) {
    const key = grouping === 'entity' ? item.entityId : item.departmentName;
    const name = grouping === 'entity' ? item.entityName : item.departmentName;
    let lane = lanes.get(key);
    if (!lane) {
      lane = { key, name, markers: [], rows: 1 };
      lanes.set(key, lane);
    }
    // Overdue deadlines are pinned at the left edge (their own column), whatever their date.
    const overdue = isOverdue(item);
    const leftPct = overdue ? 0 : positionPct(item.noticeDeadline, axis);
    let row = 0;
    // Overdue markers only stack with each other: they are not on the month axis.
    while (lane.markers.some((m) => m.row === row && isOverdue(m.item) === overdue && Math.abs(m.leftPct - leftPct) < minGapPct)) row += 1;
    lane.markers.push({ item, leftPct, row, urgency: markerUrgency(item.daysToNotice) });
    lane.rows = Math.max(lane.rows, row + 1);
  }
  return [...lanes.values()].sort((a, b) => a.name.localeCompare(b.name));
}
