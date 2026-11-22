// Browser storage that never throws. Safari refuses storage in some modes (private browsing on
// older iPadOS: quota 0; "Block All Cookies": every access throws a SecurityError), and an error
// there must not stop sign-in. Every read and write in the app goes through these two stores;
// when the browser refuses, the value lives in memory until the page is reloaded.

export interface SafeStore {
  get(key: string): string | null;
  /** True when the browser kept the value (it survives a reload); false when only memory has it. */
  set(key: string, value: string): boolean;
  remove(key: string): void;
  /** Whether the browser's own storage accepts a write right now (for the diagnostics panel). */
  available(): boolean;
}

function createStore(kind: 'sessionStorage' | 'localStorage'): SafeStore {
  const memory = new Map<string, string>();
  const real = (): Storage | undefined => {
    try {
      return globalThis[kind] ?? undefined;
    } catch {
      return undefined;
    }
  };
  return {
    get(key) {
      try {
        const v = real()?.getItem(key);
        if (v !== null && v !== undefined) return v;
      } catch {
        /* fall through to memory */
      }
      return memory.get(key) ?? null;
    },
    set(key, value) {
      try {
        const s = real();
        if (s) {
          s.setItem(key, value);
          if (s.getItem(key) === value) {
            // Kept by the browser: memory holds a value only while the browser refuses it.
            memory.delete(key);
            return true;
          }
        }
      } catch {
        /* refused: memory below */
      }
      memory.set(key, value);
      return false;
    },
    remove(key) {
      memory.delete(key);
      try {
        real()?.removeItem(key);
      } catch {
        /* nothing stored */
      }
    },
    available() {
      const probe = 'dafa.probe';
      try {
        const s = real();
        if (!s) return false;
        s.setItem(probe, '1');
        const ok = s.getItem(probe) === '1';
        s.removeItem(probe);
        return ok;
      } catch {
        return false;
      }
    },
  };
}

/** Per browser tab; cleared when the tab closes. */
export const sessionStore = createStore('sessionStorage');
/** Per browser; kept between visits. */
export const localStore = createStore('localStorage');
