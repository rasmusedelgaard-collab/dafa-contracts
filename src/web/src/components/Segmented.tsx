import { useRef, type KeyboardEvent } from 'react';

export interface SegmentedOption<T extends string> {
  value: T;
  label: string;
}

export interface SegmentedProps<T extends string> {
  options: SegmentedOption<T>[];
  value: T | null;
  onChange: (value: T) => void;
  ariaLabel: string;
  /**
   * `toggle` (default): a group of pressed buttons, for filters and views.
   * `tabs`: a tablist with arrow-key navigation, for sections of a page.
   * `radio`: a radiogroup, for a form answer.
   */
  mode?: 'toggle' | 'tabs' | 'radio';
  size?: 'md' | 'sm';
  block?: boolean;
  invalid?: boolean;
  /** Prefix for tab ids (`${idBase}-tab-${value}`), to pair with tab panels. */
  idBase?: string;
  describedBy?: string;
}

export function Segmented<T extends string>({ options, value, onChange, ariaLabel, mode = 'toggle', size = 'md', block, invalid, idBase, describedBy }: SegmentedProps<T>) {
  const ref = useRef<HTMLDivElement>(null);
  const roving = mode !== 'toggle';

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (!roving) return;
    const keys = ['ArrowRight', 'ArrowLeft', 'ArrowDown', 'ArrowUp', 'Home', 'End'];
    if (!keys.includes(e.key)) return;
    e.preventDefault();
    const current = Math.max(0, options.findIndex((o) => o.value === value));
    const last = options.length - 1;
    const next =
      e.key === 'Home' ? 0 : e.key === 'End' ? last : e.key === 'ArrowRight' || e.key === 'ArrowDown' ? (current === last ? 0 : current + 1) : current === 0 ? last : current - 1;
    const option = options[next];
    if (!option) return;
    onChange(option.value);
    ref.current?.querySelectorAll<HTMLButtonElement>('button')[next]?.focus();
  };

  const cls = ['seg', size === 'sm' && 'seg--sm', block && 'seg--block', invalid && 'seg--invalid'].filter(Boolean).join(' ');
  const role = mode === 'tabs' ? 'tablist' : mode === 'radio' ? 'radiogroup' : 'group';
  const tabStopValue = value ?? options[0]?.value;

  return (
    <div ref={ref} role={role} aria-label={ariaLabel} aria-describedby={describedBy} aria-invalid={mode === 'radio' && invalid ? true : undefined} className={cls} onKeyDown={onKeyDown}>
      {options.map((o) => {
        const on = o.value === value;
        const state = mode === 'tabs' ? { role: 'tab', 'aria-selected': on } : mode === 'radio' ? { role: 'radio', 'aria-checked': on } : { 'aria-pressed': on };
        return (
          <button
            key={o.value}
            type="button"
            className="seg__btn"
            id={idBase && mode === 'tabs' ? `${idBase}-tab-${o.value}` : undefined}
            aria-controls={idBase && mode === 'tabs' ? `${idBase}-panel-${o.value}` : undefined}
            tabIndex={roving ? (o.value === tabStopValue ? 0 : -1) : undefined}
            onClick={() => onChange(o.value)}
            {...state}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}
