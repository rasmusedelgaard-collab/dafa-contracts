/* eslint-disable react-refresh/only-export-components */
// Small chart set for Overview, in plain HTML/CSS (no chart library). Every bar, segment and
// tile is a real button that drills down; every value is also readable as text (aria-label and
// the tooltip), and the matching contract list below the charts is the table view.
import type { ReactNode } from 'react';
import { formatMoneyCompact } from '../lib/format';

export const dkk = (n: number) => formatMoneyCompact(n, 'DKK');

export interface BarRow {
  key: string;
  label: string;
  value: number;
  /** Value as text, e.g. "DKK 4.2m". */
  text: string;
  /** Second line in the tooltip, e.g. "12 contracts · 18%". */
  detail?: string;
  active?: boolean;
}

/** Horizontal bar list; each row is a drill-down button. */
export function BarList({ rows, label, empty, onPick, max }: { rows: BarRow[]; label: string; empty: string; onPick?: (row: BarRow) => void; max?: number }) {
  if (rows.length === 0) return <p className="secondary">{empty}</p>;
  const top = max ?? Math.max(1, ...rows.map((r) => r.value));
  return (
    <ol className="blist" aria-label={label}>
      {rows.map((r) => {
        const body = (
          <>
            <span className="blist__name" title={r.label}>
              {r.label}
            </span>
            <span className="blist__track" aria-hidden="true">
              <span className="blist__fill" style={{ width: `${Math.max(r.value > 0 ? 1.5 : 0, (r.value / top) * 100)}%` }} />
            </span>
            <span className="blist__value">{r.text}</span>
          </>
        );
        return (
          <li key={r.key || r.label}>
            {onPick ? (
              <button type="button" className={`blist__row${r.active ? ' blist__row--on' : ''}`} data-tip={r.detail ? `${r.label}: ${r.text} · ${r.detail}` : `${r.label}: ${r.text}`} aria-label={`${r.label}: ${r.text}${r.detail ? `, ${r.detail}` : ''}. Show these contracts`} aria-pressed={r.active || undefined} onClick={() => onPick(r)}>
                {body}
              </button>
            ) : (
              <div className="blist__row">{body}</div>
            )}
          </li>
        );
      })}
    </ol>
  );
}

export interface StackSegment {
  key: string;
  label: string;
  value: number;
  /** CSS class with the fill colour (see r3.css: .seg-high, .seg-medium, .seg-low, .seg-done). */
  className: string;
}
export interface StackColumn {
  key: string;
  /** Axis label, e.g. "Jan". */
  label: string;
  /** Long label for screen readers and tooltips, e.g. "January 2027". */
  long: string;
  /** Shown under the month label on January, e.g. "2027". */
  sub?: string;
  segments: StackSegment[];
}

/** Vertical stacked columns (notice deadlines by month). Segments are buttons. */
export function StackedColumns({ columns, legend, label, onPick, height = 168 }: { columns: StackColumn[]; legend: { label: string; className: string }[]; label: string; onPick: (col: StackColumn, seg: StackSegment | null) => void; height?: number }) {
  const max = Math.max(1, ...columns.map((c) => c.segments.reduce((s, x) => s + x.value, 0)));
  const step = max <= 5 ? 1 : max <= 10 ? 2 : Math.ceil(max / 5);
  const ticks = Array.from({ length: Math.floor(max / step) + 1 }, (_, i) => i * step);
  return (
    <figure className="cols" aria-label={label}>
      <ul className="chart-legend" aria-label="Legend">
        {legend.map((l) => (
          <li key={l.label}>
            <span className={`chart-legend__swatch ${l.className}`} aria-hidden="true" />
            {l.label}
          </li>
        ))}
      </ul>
      <div className="cols__plot" style={{ height }}>
        <div className="cols__grid" aria-hidden="true">
          {ticks.map((t) => (
            <span key={t} style={{ bottom: `${(t / max) * 100}%` }}>
              <em>{t}</em>
            </span>
          ))}
        </div>
        <ol className="cols__bars">
          {columns.map((c) => {
            const total = c.segments.reduce((s, x) => s + x.value, 0);
            return (
              <li key={c.key} className="cols__col">
                <div className="cols__stack" style={{ height: `${(total / max) * 100}%` }}>
                  {c.segments
                    .filter((s) => s.value > 0)
                    .map((s) => (
                      <button
                        key={s.key}
                        type="button"
                        className={`cols__seg ${s.className}`}
                        style={{ flexGrow: s.value }}
                        data-tip={`${c.long}: ${s.value} ${s.label.toLowerCase()}`}
                        aria-label={`${c.long}: ${s.value} ${s.label.toLowerCase()}. Show these contracts`}
                        onClick={() => onPick(c, s)}
                      />
                    ))}
                </div>
                <button type="button" className="cols__label" aria-label={`${c.long}: ${total} in total. Show these contracts`} onClick={() => onPick(c, null)}>
                  {c.label}
                  {c.sub ? <small>{c.sub}</small> : null}
                </button>
              </li>
            );
          })}
        </ol>
      </div>
    </figure>
  );
}

/** Simple vertical columns with the value on top (contracts ending by quarter). */
export function Columns({ columns, label, onPick }: { columns: { key: string; label: string; long: string; value: number; text: string; detail?: string }[]; label: string; onPick: (key: string) => void }) {
  const max = Math.max(1, ...columns.map((c) => c.value));
  return (
    <ol className="qcols" aria-label={label}>
      {columns.map((c) => (
        <li key={c.key}>
          <button type="button" className="qcols__btn" data-tip={`${c.long}: ${c.text}${c.detail ? ` · ${c.detail}` : ''}`} aria-label={`${c.long}: ${c.text}${c.detail ? `, ${c.detail}` : ''}. Show these contracts`} onClick={() => onPick(c.key)}>
            <span className="qcols__value">{c.value}</span>
            <span className="qcols__bar" style={{ height: `${Math.max(c.value > 0 ? 3 : 0, (c.value / max) * 100)}%` }} aria-hidden="true" />
            <span className="qcols__label">{c.label}</span>
          </button>
        </li>
      ))}
    </ol>
  );
}

/** Score 0–100 as a bar with the number (data quality per entity). */
export function ScoreList({ rows, onPick, label }: { rows: { key: string; label: string; score: number; detail: string }[]; onPick: (key: string) => void; label: string }) {
  if (rows.length === 0) return <p className="secondary">No contracts in this selection.</p>;
  return (
    <ol className="blist" aria-label={label}>
      {rows.map((r) => (
        <li key={r.key}>
          <button type="button" className="blist__row" data-tip={`${r.label}: ${r.score}% complete · ${r.detail}`} aria-label={`${r.label}: ${r.score}% complete, ${r.detail}. Show the contracts with missing data`} onClick={() => onPick(r.key)}>
            <span className="blist__name" title={r.label}>
              {r.label}
            </span>
            <span className="blist__track" aria-hidden="true">
              <span className={`blist__fill ${r.score >= 90 ? 'blist__fill--good' : r.score >= 70 ? 'blist__fill--warn' : 'blist__fill--bad'}`} style={{ width: `${Math.max(2, r.score)}%` }} />
            </span>
            <span className="blist__value">{r.score}%</span>
          </button>
        </li>
      ))}
    </ol>
  );
}

/** A KPI tile; clickable when it drills down. */
export function KpiTile({ label, value, sub, onClick, tone, active }: { label: string; value: ReactNode; sub?: string; onClick?: () => void; tone?: 'amber' | 'red'; active?: boolean }) {
  const body = (
    <>
      <span className="kpi__label">{label}</span>
      <span className={`kpi__value${tone ? ` fg-${tone}` : ''}`}>{value}</span>
      {sub ? <span className="kpi__sub">{sub}</span> : null}
    </>
  );
  return onClick ? (
    <button type="button" className={`kpi kpi--btn lift${active ? ' kpi--on' : ''}`} aria-pressed={active || undefined} onClick={onClick}>
      {body}
    </button>
  ) : (
    <div className="kpi">{body}</div>
  );
}
