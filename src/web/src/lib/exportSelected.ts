// "Export selected" sends the chosen contract ids as repeated `id` query parameters
// (`GET contracts/export`; the API has no POST variant). A long selection would exceed the
// web server's default limit for a URL (IIS: 2048 characters for the query string), so the ids
// are sent in several requests that each stay under 2000 characters, and the CSV answers are
// joined into one file.
import type { Api } from '../api/api';
import type { ContractQuery, FileDownload, Id } from '../api/types';

/** Longest URL (origin, path and query string) one export request may have. */
export const EXPORT_URL_LIMIT = 2000;
const EXPORT_PATH = '/api/contracts/export?';
const BOM = String.fromCharCode(0xfeff);

const paramLength = (key: string, value: string) => key.length + 1 + encodeURIComponent(value).length + 1;

/** Splits `ids` so that each export URL stays under `limit` characters. */
export function chunkIdsForExport(ids: Id[], columns: string[], originLength = 0, limit = EXPORT_URL_LIMIT): Id[][] {
  const fixed = originLength + EXPORT_PATH.length + columns.reduce((n, c) => n + paramLength('columns', c), 0);
  const chunks: Id[][] = [];
  let current: Id[] = [];
  let length = fixed;
  for (const id of ids) {
    const add = paramLength('id', id);
    if (current.length > 0 && length + add > limit) {
      chunks.push(current);
      current = [];
      length = fixed;
    }
    current.push(id);
    length += add;
  }
  if (current.length > 0) chunks.push(current);
  return chunks;
}

/** The CSV text without its first record (the header row); quoted line breaks are respected. */
export function withoutCsvHeader(csv: string): string {
  let quoted = false;
  for (let i = 0; i < csv.length; i++) {
    const ch = csv[i];
    if (ch === '"') quoted = !quoted;
    else if (!quoted && (ch === '\n' || ch === '\r')) return csv.slice(ch === '\r' && csv[i + 1] === '\n' ? i + 2 : i + 1);
  }
  return '';
}

/** Joins CSV files with the same columns into one: the first keeps its header, the others lose theirs. */
export function joinCsv(parts: string[]): string {
  const bodies = parts.map((p, i) => {
    const text = p.startsWith(BOM) ? p.slice(1) : p;
    return (i === 0 ? text : withoutCsvHeader(text)).replace(/(\r?\n)+$/, '');
  });
  return BOM + bodies.filter((b, i) => i === 0 || b !== '').join('\r\n') + '\r\n';
}

/** Exports the selected contracts as one CSV file, in as many requests as the URL limit needs. */
export async function exportSelected(api: Pick<Api, 'exportContracts'>, ids: Id[], columns: string[]): Promise<FileDownload> {
  const origin = typeof window === 'undefined' ? 0 : window.location.origin.length;
  const chunks = chunkIdsForExport(ids, columns, origin);
  const files: FileDownload[] = [];
  // One after the other: the order of the file follows the selection, and the server is not flooded.
  for (const chunk of chunks) files.push(await api.exportContracts({ id: chunk, columns } as ContractQuery));
  const first = files[0];
  if (!first) throw new Error('Nothing to export');
  if (files.length === 1) return first;
  const texts = await Promise.all(files.map((f) => f.blob.text()));
  return { blob: new Blob([joinCsv(texts)], { type: first.blob.type || 'text/csv;charset=utf-8' }), fileName: first.fileName };
}
