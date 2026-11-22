import { useState } from 'react';
import { collectDiagnostics, type DiagnosticLine } from '../lib/diagnostics';
import { useApi } from './context';

/**
 * A discreet link under every sign-in screen. Opens a short list of facts about this browser and
 * the site's answers (no secrets), so a tester whose sign-in fails can read us the result.
 */
export function SignInHelp() {
  const api = useApi();
  const [open, setOpen] = useState(false);
  const [lines, setLines] = useState<DiagnosticLine[]>();
  const check = () => {
    setLines(undefined);
    collectDiagnostics(api, __BUILD_ID__).then(setLines, () => setLines([{ label: 'Check', value: 'could not run in this browser', ok: false }]));
  };
  return (
    <div className="signin-help">
      <button
        type="button"
        className="signin-help__toggle"
        aria-expanded={open}
        onClick={() => {
          if (!open) check();
          setOpen(!open);
        }}
      >
        Having trouble signing in?
      </button>
      {open ? (
        <div className="diag" role="region" aria-label="Sign-in check">
          <p className="secondary">Read or send these lines to the person who gave you access. They contain no password or access code.</p>
          {lines ? (
            <dl className="diag__list">
              {lines.map((line) => (
                <div key={line.label} className={`diag__row${line.ok === false ? ' diag__row--bad' : ''}`}>
                  <dt>{line.label}</dt>
                  <dd>{line.value}</dd>
                </div>
              ))}
            </dl>
          ) : (
            <p className="secondary" role="status">
              Checking…
            </p>
          )}
          <button type="button" className="signin-help__toggle" onClick={check}>
            Check again
          </button>
        </div>
      ) : null}
    </div>
  );
}
