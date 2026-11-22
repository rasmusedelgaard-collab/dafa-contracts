import type { KeyboardEvent, ReactNode } from 'react';
import { Icon } from './Icon';

export interface Column<T> {
  key: string;
  header: ReactNode;
  /** Makes the header a sort button; the value is the sort field sent to the API. */
  sortKey?: string;
  render: (row: T) => ReactNode;
  className?: string;
  /** Column width (e.g. `42%`) when the table has `fixed` layout. */
  width?: string;
  /** Release 4: a header filter control (register), shown after the header text. */
  filter?: ReactNode;
}

export interface TableProps<T> {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => string;
  caption: string;
  /** Current sort, e.g. `notice` or `-notice`. */
  sort?: string;
  onSort?: (sort: string) => void;
  onRowClick?: (row: T) => void;
  selectedKey?: string | null;
  minWidth?: number;
  /** Fixed layout: columns keep their `width`, cell content is clipped instead of widening the column. */
  fixed?: boolean;
}

export function Table<T>({ columns, rows, rowKey, caption, sort, onSort, onRowClick, selectedKey, minWidth, fixed }: TableProps<T>) {
  const desc = sort?.startsWith('-') ?? false;
  const field = desc ? sort?.slice(1) : sort;

  return (
    <div className="table-wrap">
      <table className={`table${fixed ? ' table--fixed' : ''}`} style={minWidth ? { minWidth } : undefined}>
        <caption className="sr-only">{caption}</caption>
        {fixed ? (
          <colgroup>
            {columns.map((c) => (
              <col key={c.key} className={c.className} style={c.width ? { width: c.width } : undefined} />
            ))}
          </colgroup>
        ) : null}
        <thead>
          <tr>
            {columns.map((c) => {
              const active = !!c.sortKey && c.sortKey === field;
              return (
                <th key={c.key} scope="col" className={c.className} aria-sort={active ? (desc ? 'descending' : 'ascending') : undefined}>
                  <span className={c.filter ? 'th-inner' : undefined}>
                    {c.sortKey && onSort ? (
                      <button type="button" className="table__sort" data-active={active} onClick={() => onSort(active && !desc ? `-${c.sortKey}` : (c.sortKey ?? ''))}>
                        {c.header}
                        <Icon name={active && desc ? 'chevronUp' : 'chevronDown'} size={12} stroke={2.4} />
                      </button>
                    ) : (
                      c.header
                    )}
                    {c.filter}
                  </span>
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => {
            const key = rowKey(row);
            const selected = selectedKey === key;
            const cls = [onRowClick && 'table__row--click', selected && 'table__row--selected'].filter(Boolean).join(' ') || undefined;
            const onKeyDown = onRowClick
              ? (e: KeyboardEvent<HTMLTableRowElement>) => {
                  if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) {
                    e.preventDefault();
                    onRowClick(row);
                  }
                }
              : undefined;
            return (
              <tr key={key} className={cls} aria-current={selected ? 'true' : undefined} onClick={onRowClick ? () => onRowClick(row) : undefined} onKeyDown={onKeyDown}>
                {columns.map((c) => (
                  <td key={c.key} className={c.className}>
                    {c.render(row)}
                  </td>
                ))}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
