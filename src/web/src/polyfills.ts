// The few newer library functions the app uses that Safari 14 / 15.0-15.3 do not have. Syntax is
// lowered by the build (`build.target` in vite.config.ts); functions are not, so they are added
// here. Imported first in main.tsx. Each one is only installed when the browser lacks it.

function clone<T>(value: T, seen: Map<unknown, unknown>): T {
  if (value === null || typeof value !== 'object') return value;
  if (seen.has(value)) return seen.get(value) as T;
  if (value instanceof Date) return new Date(value.getTime()) as T;
  // Files and binary data are immutable or shared on purpose: the same object is fine.
  if (typeof Blob !== 'undefined' && value instanceof Blob) return value;
  if (value instanceof ArrayBuffer || ArrayBuffer.isView(value)) return value;
  if (Array.isArray(value)) {
    const out: unknown[] = [];
    seen.set(value, out);
    for (const item of value) out.push(clone(item, seen));
    return out as T;
  }
  if (value instanceof Map) {
    const out = new Map();
    seen.set(value, out);
    value.forEach((v, k) => out.set(clone(k, seen), clone(v, seen)));
    return out as T;
  }
  if (value instanceof Set) {
    const out = new Set();
    seen.set(value, out);
    value.forEach((v) => out.add(clone(v, seen)));
    return out as T;
  }
  const out: Record<string, unknown> = {};
  seen.set(value, out);
  for (const key of Object.keys(value)) out[key] = clone((value as Record<string, unknown>)[key], seen);
  return out as T;
}

/** `structuredClone` for the data the app clones: plain objects, arrays, dates, maps, sets, files. */
export function structuredCloneFallback<T>(value: T): T {
  return clone(value, new Map());
}

export function installPolyfills(target: typeof globalThis = globalThis): void {
  try {
    if (typeof target.structuredClone !== 'function') {
      (target as { structuredClone: unknown }).structuredClone = structuredCloneFallback;
    }
  } catch {
    /* a frozen global: the app still starts, only the demo data would miss the function */
  }
}

installPolyfills();
