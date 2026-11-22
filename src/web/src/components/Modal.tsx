import { useEffect, useId, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { usePhone } from '../lib/media';
import { Icon } from './Icon';

export interface ModalProps {
  title: string;
  subtitle?: ReactNode;
  onClose: () => void;
  children: ReactNode;
  /** Buttons, right-aligned. */
  footer?: ReactNode;
  wide?: boolean;
  /** Wraps body and footer in a form with this submit handler. */
  onSubmit?: () => void;
}

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/**
 * Where focus goes when a dialog opens: an element marked `data-autofocus`, else the first
 * control in the body. When that first control is disabled (locked fields at the top of a
 * form), focus goes to the dialog itself, so it opens at the top instead of scrolling past the
 * locked part to the first field that can be edited.
 */
// eslint-disable-next-line react-refresh/only-export-components
export function initialFocus(dialog: HTMLElement | null): HTMLElement | null {
  if (!dialog) return null;
  const marked = dialog.querySelector<HTMLElement>('[data-autofocus]');
  if (marked) return marked;
  const first = dialog.querySelector<HTMLInputElement>('.modal__body input, .modal__body select, .modal__body textarea, .modal__body button, .modal__body a[href]');
  return first && !first.disabled ? first : dialog;
}

/**
 * Dialog on desktop, bottom sheet under 720px. Traps focus, closes on Escape and on a click
 * on the backdrop, and returns focus to the element that opened it.
 */
export function Modal({ title, subtitle, onClose, children, footer, wide, onSubmit }: ModalProps) {
  const titleId = useId();
  const ref = useRef<HTMLDivElement>(null);
  const closeRef = useRef(onClose);
  closeRef.current = onClose;
  // On a phone a long subtitle scrolls with the body instead of taking a fixed share of the sheet.
  const phone = usePhone();

  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    const node = ref.current;
    initialFocus(node)?.focus();
    const overflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        closeRef.current();
        return;
      }
      if (e.key !== 'Tab' || !node) return;
      const items = [...node.querySelectorAll<HTMLElement>(FOCUSABLE)];
      const firstItem = items[0];
      const lastItem = items[items.length - 1];
      if (!firstItem || !lastItem) return;
      if (e.shiftKey && document.activeElement === firstItem) {
        e.preventDefault();
        lastItem.focus();
      } else if (!e.shiftKey && document.activeElement === lastItem) {
        e.preventDefault();
        firstItem.focus();
      }
    };
    document.addEventListener('keydown', onKey, true);
    return () => {
      document.removeEventListener('keydown', onKey, true);
      document.body.style.overflow = overflow;
      opener?.focus?.();
    };
  }, []);

  const inner = (
    <>
      <div className="modal__body">
        {subtitle && phone ? <p className="modal__sub modal__sub--body">{subtitle}</p> : null}
        {children}
      </div>
      {footer ? <div className="modal__foot">{footer}</div> : null}
    </>
  );

  return createPortal(
    <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div ref={ref} className={`modal${wide ? ' modal--wide' : ''}`} role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1}>
        <div className="modal__head">
          <div>
            <h2 className="modal__title" id={titleId}>
              {title}
            </h2>
            {subtitle && !phone ? <p className="modal__sub">{subtitle}</p> : null}
          </div>
          <button type="button" className="modal__close" aria-label="Close" onClick={onClose}>
            <Icon name="x" size={18} stroke={2.2} />
          </button>
        </div>
        {onSubmit ? (
          <form
            noValidate
            style={{ display: 'contents' }}
            onSubmit={(e) => {
              e.preventDefault();
              onSubmit();
            }}
          >
            {inner}
          </form>
        ) : (
          inner
        )}
      </div>
    </div>,
    document.body,
  );
}
