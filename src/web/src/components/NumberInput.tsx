import { useLayoutEffect, useRef, useState, type InputHTMLAttributes } from 'react';
import { canonicalAmount, caretAfterFormat, displayAmount } from '../lib/displayFormat';

type Props = Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'type'> & {
  /** Canonical text: digits with "." as decimal separator ("1250000.5"), or "". */
  value: string;
  onValueChange: (canonical: string) => void;
  /** Fraction digits allowed; 0 for whole numbers. */
  decimals?: number;
  suggested?: boolean;
};

/**
 * Number field with thousand separators while typing and when filled, in the number format of
 * General settings (owner item D). Keeps the caret after the same digit when separators are
 * added or removed. The form state keeps canonical text; callers send numbers to the API.
 */
export function NumberInput({ value, onValueChange, decimals = 2, suggested, className, ...rest }: Props) {
  const ref = useRef<HTMLInputElement>(null);
  const caret = useRef<number | null>(null);
  const shown = displayAmount(value);
  useLayoutEffect(() => {
    if (caret.current !== null && ref.current && document.activeElement === ref.current) {
      ref.current.setSelectionRange(caret.current, caret.current);
      caret.current = null;
    }
  });
  return (
    <input
      {...rest}
      ref={ref}
      type="text"
      inputMode={decimals > 0 ? 'decimal' : 'numeric'}
      autoComplete="off"
      className={['input', 'input--number', rest['aria-invalid'] ? 'input--invalid' : '', suggested ? 'input--suggested' : '', className ?? ''].filter(Boolean).join(' ')}
      value={shown}
      onChange={(e) => {
        const raw = e.target.value;
        const next = canonicalAmount(raw, decimals);
        caret.current = caretAfterFormat(raw, e.target.selectionStart ?? raw.length, displayAmount(next));
        onValueChange(next);
      }}
    />
  );
}

const toText = (n: number | null | undefined) => (n === null || n === undefined || !Number.isFinite(n) ? '' : String(n));

/** NumberInput over a number value (custom fields, thresholds): keeps the typed text while editing. */
export function NumberValueInput({ value, onNumberChange, ...rest }: Omit<Props, 'value' | 'onValueChange'> & { value: number | null | undefined; onNumberChange: (n: number | null) => void }) {
  const [text, setText] = useState(toText(value));
  const [seen, setSeen] = useState(value);
  if (value !== seen) {
    setSeen(value);
    if (Number(text) !== value) setText(toText(value));
  }
  return (
    <NumberInput
      {...rest}
      value={text}
      onValueChange={(t) => {
        setText(t);
        const n = t === '' || t === '-' ? null : Number(t);
        setSeen(n);
        onNumberChange(n);
      }}
    />
  );
}
