// Search helpers shared by the mock (matching, snippets) and the UI (highlighting).
import type { Snippet } from '../api/types';

/** Whitespace-separated, lower-cased search terms. */
export function terms(q: string): string[] {
  return q
    .trim()
    .toLowerCase()
    .split(/\s+/)
    .filter((t) => t.length > 0);
}

/** True when every term occurs somewhere in the combined texts (contract rule: all terms must match). */
export function matchesAll(texts: (string | null | undefined)[], ts: string[]): boolean {
  if (ts.length === 0) return false;
  const hay = texts.filter(Boolean).join('\n').toLowerCase();
  return ts.every((t) => hay.includes(t));
}

/** Which of the texts contain at least one term. */
export function containsAny(text: string | null | undefined, ts: string[]): boolean {
  if (!text) return false;
  const lower = text.toLowerCase();
  return ts.some((t) => lower.includes(t));
}

/** [start, end) ranges of every term occurrence, merged and sorted. */
export function highlightRanges(text: string, ts: string[]): [number, number][] {
  const lower = text.toLowerCase();
  const ranges: [number, number][] = [];
  for (const t of ts) {
    if (!t) continue;
    let i = lower.indexOf(t);
    while (i >= 0) {
      ranges.push([i, i + t.length]);
      i = lower.indexOf(t, i + t.length);
    }
  }
  ranges.sort((a, b) => a[0] - b[0]);
  const merged: [number, number][] = [];
  for (const r of ranges) {
    const last = merged[merged.length - 1];
    if (last && r[0] <= last[1]) last[1] = Math.max(last[1], r[1]);
    else merged.push([r[0], r[1]]);
  }
  return merged;
}

/**
 * Excerpt of at most `width` characters around the first term, cut on word boundaries with
 * an ellipsis, with highlight offsets relative to the excerpt. Null when no term occurs.
 */
export function makeSnippet(text: string, ts: string[], width = 200): Snippet | null {
  const lower = text.toLowerCase();
  const first = Math.min(...ts.map((t) => lower.indexOf(t)).filter((i) => i >= 0));
  if (!Number.isFinite(first)) return null;
  let start = Math.max(0, first - Math.floor(width / 3));
  let end = Math.min(text.length, start + width);
  if (end - start < width) start = Math.max(0, end - width);
  if (start > 0) {
    const space = text.indexOf(' ', start);
    if (space >= 0 && space < first) start = space + 1;
  }
  if (end < text.length) {
    const space = text.lastIndexOf(' ', end);
    if (space > first) end = space;
  }
  const prefix = start > 0 ? '…' : '';
  const suffix = end < text.length ? '…' : '';
  const body = `${prefix}${text.slice(start, end)}${suffix}`;
  return { text: body, highlights: highlightRanges(body, ts) };
}

/** Splits text into plain and highlighted parts for rendering. */
export function segments(text: string, highlights: [number, number][]): { text: string; hit: boolean }[] {
  const out: { text: string; hit: boolean }[] = [];
  let at = 0;
  for (const [s, e] of highlights) {
    if (s < at || s >= text.length) continue;
    if (s > at) out.push({ text: text.slice(at, s), hit: false });
    out.push({ text: text.slice(s, Math.min(e, text.length)), hit: true });
    at = Math.min(e, text.length);
  }
  if (at < text.length) out.push({ text: text.slice(at), hit: false });
  return out;
}
