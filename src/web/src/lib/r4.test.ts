import { describe, expect, it } from 'vitest';
import { canonicalAmount, caretAfterFormat, displayAmount, formatDateIn, formatNumber, parseDisplayAmount } from './displayFormat';
import { isChunkLoadError, reloadOnce, type ReloadHost } from './lazyPage';

describe('number formats (owner item D)', () => {
  it('formats with the separators of the General settings format', () => {
    expect(formatNumber(1250000.5, 2, 'en-GB')).toBe('1,250,000.5');
    expect(formatNumber(1250000.5, 2, 'da-DK')).toBe('1.250.000,5');
    expect(formatNumber(1234567.891, 2, 'de-DE')).toBe('1.234.567,89');
    expect(formatNumber(-950, 0, 'en-US')).toBe('-950');
  });

  it('keeps canonical text while typing and shows it grouped, Danish and English', () => {
    expect(canonicalAmount('1.234.567,89', 2, 'da-DK')).toBe('1234567.89');
    expect(displayAmount('1234567.89', 'da-DK')).toBe('1.234.567,89');
    expect(canonicalAmount('1,234,567.89', 2, 'en-GB')).toBe('1234567.89');
    expect(displayAmount('1234567.', 'en-GB')).toBe('1,234,567.');
    expect(canonicalAmount('12,345', 0, 'da-DK')).toBe('12345');
    expect(canonicalAmount('1,2,3', 2, 'da-DK')).toBe('1.23');
    expect(canonicalAmount('1.999', 2, 'en-GB')).toBe('1.99');
    expect(parseDisplayAmount('1.250.000,50', 'da-DK')).toBe(1250000.5);
    expect(Number.isNaN(parseDisplayAmount('12a', 'en-GB'))).toBe(true);
  });

  it('puts the caret after the same digit when separators appear', () => {
    // "1234|" typed -> "1,234|"
    expect(caretAfterFormat('1234', 4, '1,234', 'en-GB')).toBe(5);
    // "12|345" (caret after 2) -> "12|,345"
    expect(caretAfterFormat('12345', 2, '12,345', 'en-GB')).toBe(2);
    // deleting a digit in "1.234.5|67" (Danish) keeps the caret after the 5
    expect(caretAfterFormat('1.234.567', 7, '1.234.567', 'da-DK')).toBe(7);
  });

  it('formats dates in the General settings format', () => {
    const d = new Date('2027-03-05T00:00:00Z');
    expect(formatDateIn(d, 'd MMM yyyy')).toBe('5 Mar 2027');
    expect(formatDateIn(d, 'dd-MM-yyyy')).toBe('05-03-2027');
    expect(formatDateIn(d, 'dd.MM.yyyy')).toBe('05.03.2027');
    expect(formatDateIn(d, 'yyyy-MM-dd')).toBe('2027-03-05');
    expect(formatDateIn(d, 'MM/dd/yyyy')).toBe('03/05/2027');
  });
});

describe('lazy pages after a deploy (owner item A4)', () => {
  const host = (stored: string | null, now = 100_000) => {
    const h = { value: stored, reloads: 0 };
    const api: ReloadHost = { now: () => now, get: () => h.value, set: (v) => (h.value = v), reload: () => (h.reloads += 1) };
    return { h, api };
  };
  it('recognises failed chunk imports from Chrome, Firefox and Safari', () => {
    expect(isChunkLoadError(new TypeError('Failed to fetch dynamically imported module: https://x/assets/Settings-abc.js'))).toBe(true);
    expect(isChunkLoadError(new TypeError('error loading dynamically imported module'))).toBe(true);
    expect(isChunkLoadError(new TypeError('Importing a module script failed.'))).toBe(true);
    expect(isChunkLoadError(new Error('Cannot read properties of undefined'))).toBe(false);
  });
  it('reloads once, and not again within a minute (no reload loop)', () => {
    const first = host(null);
    expect(reloadOnce(first.api)).toBe(true);
    expect(first.h.reloads).toBe(1);
    const again = host('90000', 100_000);
    expect(reloadOnce(again.api)).toBe(false);
    expect(again.h.reloads).toBe(0);
    const later = host('10000', 100_000);
    expect(reloadOnce(later.api)).toBe(true);
  });
});

describe('themes (owner item I)', () => {
  it('the light and dark token sets define the same keys, and no other stylesheet hard-codes a color', async () => {
    const { readFileSync, readdirSync } = await import('node:fs');
    const { join } = await import('node:path');
    const dir = join(__dirname, '..', 'styles');
    const css = readFileSync(join(dir, 'tokens.css'), 'utf8');
    const block = (selector: string) => {
      const start = css.indexOf(`${selector} {`);
      const end = css.indexOf('\n}', start);
      return new Set<string>([...css.slice(start, end).matchAll(/(--[a-z0-9-]+)\s*:/g)].map((m) => m[1] ?? ''));
    };
    const light = block(':root');
    const dark = block(":root[data-theme='dark']");
    const layout = ['--r-card', '--r-card-lg', '--r-hero', '--r-control', '--r-control-sm', '--r-pill', '--font', '--sidebar', '--gutter', '--ease'];
    const colorKeys = [...light].filter((k) => !layout.includes(k));
    expect(colorKeys.filter((k) => !dark.has(k))).toEqual([]);
    expect([...dark].filter((k) => !light.has(k))).toEqual([]);
    for (const f of readdirSync(dir).filter((x) => x.endsWith('.css') && x !== 'tokens.css')) {
      const text = readFileSync(join(dir, f), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
      expect(text.match(/#[0-9a-fA-F]{3,8}\b|rgba?\(|%23[0-9a-fA-F]{3,6}/g) ?? [], f).toEqual([]);
    }
  });

  it('resolves system to the device setting', async () => {
    const { resolveTheme } = await import('./theme');
    expect(resolveTheme('system', true)).toBe('dark');
    expect(resolveTheme('system', false)).toBe('light');
    expect(resolveTheme('dark', false)).toBe('dark');
    expect(resolveTheme('light', true)).toBe('light');
  });
});
