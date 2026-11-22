import { useEffect } from 'react';

/**
 * Sets `data-<name>` on <body> while a page is in a state the shell has to follow in CSS
 * (for example: the phone approval view hides the top bar and the tab bar). A plain attribute
 * works in every browser, unlike a `:has()` selector on the shell.
 */
export function useBodyFlag(name: string, on: boolean): void {
  useEffect(() => {
    if (!on) return;
    document.body.setAttribute(`data-${name}`, '');
    return () => document.body.removeAttribute(`data-${name}`);
  }, [name, on]);
}
