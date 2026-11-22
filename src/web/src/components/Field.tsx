import { useId, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from 'react';

export interface FieldRenderProps {
  id: string;
  'aria-invalid': true | undefined;
  'aria-describedby': string | undefined;
}

export interface FieldProps {
  label: string;
  /** Marks the label "(optional)". Required fields are the default and are not marked. */
  optional?: boolean;
  error?: string;
  hint?: string;
  /** Shown next to the label, e.g. a "Suggested" pill. */
  tag?: ReactNode;
  /** Shown to the right of the control, e.g. an accept button. */
  after?: ReactNode;
  className?: string;
  children: (props: FieldRenderProps) => ReactNode;
}

/** Label + control + hint/error, wired with ids so screen readers announce the error. */
export function Field({ label, optional, error, hint, tag, after, className, children }: FieldProps) {
  const id = useId();
  const errorId = `${id}-error`;
  const hintId = `${id}-hint`;
  const describedBy = [error && errorId, hint && hintId].filter(Boolean).join(' ') || undefined;
  return (
    <div className={`field${className ? ` ${className}` : ''}`}>
      <div className="field__top">
        <label className="field__label" htmlFor={id}>
          {label}
          {optional ? <span className="field__optional"> (optional)</span> : null}
        </label>
        {tag}
      </div>
      <div className="field__row">
        {children({ id, 'aria-invalid': error ? true : undefined, 'aria-describedby': describedBy })}
        {after}
      </div>
      {error ? (
        <span className="field__error" id={errorId} role="alert">
          {error}
        </span>
      ) : null}
      {hint ? (
        <span className="field__hint" id={hintId}>
          {hint}
        </span>
      ) : null}
    </div>
  );
}

function stateClass(base: string, invalid: unknown, suggested?: boolean, className?: string): string {
  return [base, invalid ? `${base}--invalid` : '', suggested ? `${base}--suggested` : '', className ?? ''].filter(Boolean).join(' ');
}

export function TextInput({ suggested, className, ...rest }: InputHTMLAttributes<HTMLInputElement> & { suggested?: boolean }) {
  return <input type="text" className={stateClass('input', rest['aria-invalid'], suggested, className)} {...rest} />;
}

export function Textarea({ className, ...rest }: TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea className={stateClass('textarea', rest['aria-invalid'], false, className)} {...rest} />;
}

export interface SelectOption {
  value: string;
  label: string;
}

export interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  options: SelectOption[];
  /** Label of the empty option; omit to have no empty option. */
  placeholder?: string;
  suggested?: boolean;
}

export function Select({ options, placeholder, suggested, className, ...rest }: SelectProps) {
  return (
    <select className={stateClass('select', rest['aria-invalid'], suggested, className)} {...rest}>
      {placeholder !== undefined ? <option value="">{placeholder}</option> : null}
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}

/** Pill-shaped filter select: "Entity: All". */
export function ChipSelect({ label, allLabel = 'All', options, value, onChange }: { label: string; allLabel?: string; options: SelectOption[]; value: string; onChange: (value: string) => void }) {
  return (
    <select className={`chip${value ? ' chip--on' : ''}`} aria-label={label} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">{`${label}: ${allLabel}`}</option>
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}
