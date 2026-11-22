import { useSyncExternalStore } from 'react';

/** True while the media query matches. False where `matchMedia` is missing (tests, old browsers). */
export function useMedia(query: string): boolean {
  return useSyncExternalStore(
    (onChange) => {
      const mq = window.matchMedia?.(query);
      mq?.addEventListener?.('change', onChange);
      return () => mq?.removeEventListener?.('change', onChange);
    },
    () => window.matchMedia?.(query).matches ?? false,
    () => false,
  );
}

/** Under 720px, where the shell shows the bottom tab bar. */
export const usePhone = (): boolean => useMedia('(max-width: 720px)');

/** Under 1000px (tablet and phone), where the top bar has no room for page actions. */
export const useNarrow = (): boolean => useMedia('(max-width: 1000px)');
