// Import engine for the mock (docs/CONTRACT_V3.md section 11): file parsing, column mapping,
// value parsing and row validation for counterparties, users and contracts. Pure: the mock
// applies the plans it returns.
import type {
  CounterpartyKind,
  CustomFieldDef,
  CustomFieldValue,
  FieldRules,
  Id,
  ImportFieldDef,
  ImportKind,
  ImportMapping,
  ImportOptions,
  ImportRowAction,
  ImportRowPreview,
  ReminderRecipient,
  Role,
} from '../types';

// ----- file parsing -----

/** RFC 4180 CSV with the separator detected from the header line (`,`, `;` or tab). */
export function parseCsv(input: string): string[][] {
  const text = input.replace(/^\uFEFF/, '');
  const firstLine = text.split(/\r?\n/, 1)[0] ?? '';
  const counts = [',', ';', '\t'].map((sep) => ({ sep, n: firstLine.split(sep).length - 1 }));
  const sep = counts.sort((a, b) => b.n - a.n)[0]?.n ? (counts[0]?.sep ?? ',') : ',';
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = '';
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quoted) {
      if (ch === '"') {
        if (text[i + 1] === '"') {
          cell += '"';
          i++;
        } else quoted = false;
      } else cell += ch;
      continue;
    }
    if (ch === '"' && cell === '') quoted = true;
    else if (ch === sep) {
      row.push(cell);
      cell = '';
    } else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && text[i + 1] === '\n') i++;
      row.push(cell);
      rows.push(row);
      row = [];
      cell = '';
    } else cell += ch;
  }
  if (cell !== '' || row.length > 0) {
    row.push(cell);
    rows.push(row);
  }
  return rows.map((r) => r.map((c) => c.trim()));
}

async function inflateRaw(data: Uint8Array): Promise<Uint8Array> {
  const ds = new DecompressionStream('deflate-raw');
  const stream = new Blob([data as BlobPart]).stream().pipeThrough(ds);
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

/** Reads the entries of a zip file (stored or deflated) into a name -> bytes map. */
async function unzip(buf: ArrayBuffer): Promise<Map<string, Uint8Array>> {
  const bytes = new Uint8Array(buf);
  const view = new DataView(buf);
  let eocd = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 65_557); i--) {
    if (view.getUint32(i, true) === 0x06054b50) {
      eocd = i;
      break;
    }
  }
  if (eocd < 0) throw new Error('Not a zip file');
  const count = view.getUint16(eocd + 10, true);
  let p = view.getUint32(eocd + 16, true);
  const out = new Map<string, Uint8Array>();
  for (let n = 0; n < count; n++) {
    const method = view.getUint16(p + 10, true);
    const size = view.getUint32(p + 20, true);
    const nameLen = view.getUint16(p + 28, true);
    const extraLen = view.getUint16(p + 30, true);
    const commentLen = view.getUint16(p + 32, true);
    const local = view.getUint32(p + 42, true);
    const name = new TextDecoder().decode(bytes.subarray(p + 46, p + 46 + nameLen));
    const dataStart = local + 30 + view.getUint16(local + 26, true) + view.getUint16(local + 28, true);
    const raw = bytes.subarray(dataStart, dataStart + size);
    out.set(name, method === 8 ? await inflateRaw(raw) : raw);
    p += 46 + nameLen + extraLen + commentLen;
  }
  return out;
}

const colIndex = (ref: string): number => {
  const letters = /^[A-Z]+/.exec(ref)?.[0] ?? 'A';
  let n = 0;
  for (const ch of letters) n = n * 26 + (ch.charCodeAt(0) - 64);
  return n - 1;
};

/** Excel serial date (1900 system) -> yyyy-MM-dd. */
export function excelDate(serial: number): string {
  const ms = Math.round((serial - 25569) * 86_400_000);
  return new Date(ms).toISOString().slice(0, 10);
}

/** First worksheet of an .xlsx as text cells. Dates in date-formatted cells become yyyy-MM-dd. */
export async function readXlsx(buf: ArrayBuffer): Promise<string[][]> {
  const files = await unzip(buf);
  const xml = (name: string) => {
    const f = files.get(name);
    return f ? new DOMParser().parseFromString(new TextDecoder().decode(f), 'application/xml') : null;
  };
  const shared = xml('xl/sharedStrings.xml');
  const strings = shared ? [...shared.getElementsByTagName('si')].map((si) => [...si.getElementsByTagName('t')].map((t) => t.textContent ?? '').join('')) : [];
  const styles = xml('xl/styles.xml');
  const dateStyles = new Set<number>();
  if (styles) {
    const custom = new Map<number, string>();
    for (const f of [...styles.getElementsByTagName('numFmt')]) custom.set(Number(f.getAttribute('numFmtId')), f.getAttribute('formatCode') ?? '');
    const xfs = styles.getElementsByTagName('cellXfs')[0];
    [...(xfs?.getElementsByTagName('xf') ?? [])].forEach((xf, i) => {
      const id = Number(xf.getAttribute('numFmtId') ?? 0);
      if ((id >= 14 && id <= 22) || /[dy]/i.test(custom.get(id) ?? '')) dateStyles.add(i);
    });
  }
  const sheetName = [...files.keys()].filter((k) => /^xl\/worksheets\/sheet\d+\.xml$/.test(k)).sort()[0];
  const sheet = sheetName ? xml(sheetName) : null;
  if (!sheet) throw new Error('The workbook has no worksheet');
  const rows: string[][] = [];
  for (const r of [...sheet.getElementsByTagName('row')]) {
    const out: string[] = [];
    for (const c of [...r.getElementsByTagName('c')]) {
      const idx = colIndex(c.getAttribute('r') ?? 'A1');
      const t = c.getAttribute('t');
      const v = c.getElementsByTagName('v')[0]?.textContent ?? '';
      let text: string;
      if (t === 's') text = strings[Number(v)] ?? '';
      else if (t === 'inlineStr') text = [...c.getElementsByTagName('t')].map((x) => x.textContent ?? '').join('');
      else if (t === 'b') text = v === '1' ? 'true' : 'false';
      else if (v !== '' && dateStyles.has(Number(c.getAttribute('s') ?? -1))) text = excelDate(Number(v));
      else text = v;
      while (out.length < idx) out.push('');
      out[idx] = text.trim();
    }
    rows.push(out);
  }
  return rows;
}

/** Header + data rows; empty rows dropped. */
export function splitTable(rows: string[][]): { columns: string[]; data: string[][] } {
  const [head = [], ...rest] = rows;
  const columns = head.map((h) => h.trim());
  const data = rest.filter((r) => r.some((c) => c.trim() !== '')).map((r) => columns.map((_, i) => (r[i] ?? '').trim()));
  return { columns, data };
}

// ----- fields -----

const f = (key: string, label: string, type: ImportFieldDef['type'], extra: Partial<ImportFieldDef> = {}): ImportFieldDef => ({ key, label, type, required: false, help: null, isKey: false, example: null, ...extra });
const ROLES: Role[] = ['Reader', 'ContractOwner', 'Approver', 'Admin'];
const KINDS: CounterpartyKind[] = ['Supplier', 'Customer', 'Partner', 'Authority', 'Other'];

export function importFields(kind: ImportKind, customFields: CustomFieldDef[] = []): ImportFieldDef[] {
  if (kind === 'counterparties') {
    return [
      f('name', 'Name', 'text', { required: true, isKey: true, example: 'Nordic Seals ApS' }),
      f('externalId', 'External id', 'text', { isKey: true, help: 'Vendor number in the ERP system; matched first.', example: 'V10500' }),
      f('vatNo', 'VAT number', 'text', { isKey: true, example: 'DK12345678' }),
      f('country', 'Country', 'text', { help: 'Two-letter code, e.g. DK, SE, DE.', example: 'DK' }),
      f('city', 'City', 'text', { example: 'Vejle' }),
      f('kind', 'Kind', 'choice', { options: KINDS, help: 'Default Supplier.', example: 'Supplier' }),
      f('email', 'E-mail', 'email', { example: 'sales@example.com' }),
      f('phone', 'Phone', 'text'),
      f('isActive', 'Active', 'bool', { help: 'Default yes.', example: 'yes' }),
    ];
  }
  if (kind === 'users') {
    return [
      f('email', 'E-mail', 'email', { required: true, isKey: true, example: 'anna.berg@dafa.example' }),
      f('name', 'Name', 'text', { required: true, example: 'Anna Berg' }),
      f('title', 'Job title', 'text', { example: 'Buyer' }),
      f('roles', 'Roles', 'role', { options: ROLES, help: 'Separate several with ";". Default Reader.', example: 'ContractOwner; Approver' }),
      f('departments', 'Departments', 'department', { help: 'Department names, separated by ";".', example: 'Purchasing' }),
      f('managedEntities', 'Managed entities', 'entity', { help: 'Legal entity names, separated by ";".' }),
      f('groupManagement', 'Group management', 'bool', { example: 'no' }),
      f('isActive', 'Active', 'bool', { help: 'Default yes.', example: 'yes' }),
    ];
  }
  return [
    f('contractNo', 'Contract no.', 'text', { isKey: true, help: 'Empty: a new number is generated.', example: 'C-2021-0042' }),
    f('title', 'Title', 'text', { required: true, example: 'Supply of gaskets' }),
    f('counterparty', 'Counterparty', 'counterparty', { required: true, help: 'Name, VAT number or external id.', example: 'Nordpolymer Compounds ApS' }),
    f('type', 'Contract type', 'type', { required: true, example: 'Supplier – direct materials' }),
    f('entity', 'Legal entity', 'entity', { required: true, example: 'DAFA A/S' }),
    f('department', 'Department', 'department', { required: true, example: 'Purchasing' }),
    f('owner', 'Owner', 'person', { required: true, help: 'E-mail or full name.', example: 'mette.kjaer@dafa.example' }),
    f('delegate', 'Delegate', 'person'),
    f('responsible', 'Responsible', 'person'),
    f('reminderRecipient', 'Reminder recipient', 'choice', { options: ['Owner', 'Delegate', 'Responsible'], help: 'Default Owner.' }),
    f('startDate', 'Start date', 'date', { required: true, example: '2024-01-01' }),
    f('endDate', 'End date', 'date', { help: 'Empty: open-ended.', example: '2026-12-31' }),
    f('noticePeriodDays', 'Notice period (days)', 'number', { example: '90' }),
    f('autoRenew', 'Auto-renew', 'bool', { example: 'yes' }),
    f('renewalMonths', 'Renewal period (months)', 'number', { example: '12' }),
    f('annualValue', 'Annual value', 'money', { example: '250000' }),
    f('totalValue', 'Total value', 'money'),
    f('currency', 'Currency', 'text', { help: 'Default: the type default, then the entity currency.', example: 'DKK' }),
    f('paymentTerms', 'Payment terms', 'text', { example: '30 days net' }),
    f('governingLaw', 'Governing law', 'text', { example: 'Danish law' }),
    f('liabilityCap', 'Liability cap', 'text'),
    f('exclusivity', 'Exclusivity', 'text'),
    f('changeOfControl', 'Change of control', 'bool', { help: 'Empty = not known (missing data).' }),
    f('signedDate', 'Signed date', 'date'),
    f('summary', 'Summary', 'text'),
    f('tags', 'Tags', 'text', { help: 'Tag names, separated by ";".' }),
    ...customFields
      .filter((d) => d.isActive)
      .map((d) =>
        f(`cf.${d.key}`, d.label, d.type === 'YesNo' ? 'bool' : d.type === 'Number' ? 'number' : d.type === 'Money' ? 'money' : d.type === 'Date' ? 'date' : d.type === 'Person' ? 'person' : d.type === 'MultiChoice' ? 'multi' : d.type === 'Choice' ? 'choice' : 'text', {
          help: d.helpText,
          options: d.options.length ? d.options : undefined,
        }),
      ),
  ];
}

const norm = (s: string) => s.toLowerCase().replace(/[\s_\-.]/g, '');

/** Matches headers to field keys or labels, ignoring case, spaces, `-`, `_` and dots. */
export function suggestMapping(columns: string[], fields: ImportFieldDef[]): ImportMapping {
  const mapping: ImportMapping = {};
  const used = new Set<string>();
  for (const fd of fields) {
    const hit = columns.find((c) => !used.has(c) && (norm(c) === norm(fd.key) || norm(c) === norm(fd.label) || norm(c) === norm(fd.key.replace(/^cf\./, ''))));
    mapping[fd.key] = hit ?? null;
    if (hit) used.add(hit);
  }
  return mapping;
}

// ----- value parsing -----

export function parseBool(text: string): boolean | null | undefined {
  const t = text.trim().toLowerCase();
  if (t === '') return null;
  if (['true', 'yes', 'ja', '1', 'x', 'y'].includes(t)) return true;
  if (['false', 'no', 'nej', '0', 'n'].includes(t)) return false;
  return undefined;
}

export function parseNumber(text: string, decimal: '.' | ',' = '.'): number | null | undefined {
  let t = text.trim().replace(/\s/g, '').replace(/^(dkk|eur|sek|usd|pln|cny|gbp|nok)/i, '');
  if (t === '') return null;
  const thousands = decimal === '.' ? ',' : '.';
  t = t.split(thousands).join('');
  if (decimal === ',') t = t.replace(',', '.');
  const n = Number(t);
  return Number.isFinite(n) ? n : undefined;
}

const DATE_PATTERNS: Record<string, RegExp> = {
  'yyyy-MM-dd': /^(\d{4})-(\d{1,2})-(\d{1,2})$/,
  'dd-MM-yyyy': /^(\d{1,2})-(\d{1,2})-(\d{4})$/,
  'dd.MM.yyyy': /^(\d{1,2})\.(\d{1,2})\.(\d{4})$/,
  'dd/MM/yyyy': /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/,
  'MM/dd/yyyy': /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/,
};

function isoOf(y: number, m: number, d: number): string | undefined {
  const date = new Date(Date.UTC(y, m - 1, d));
  if (date.getUTCFullYear() !== y || date.getUTCMonth() !== m - 1 || date.getUTCDate() !== d) return undefined;
  return date.toISOString().slice(0, 10);
}

export function parseDate(text: string, format: ImportOptions['dateFormat'] = 'auto'): string | null | undefined {
  const t = text.trim();
  if (t === '') return null;
  const tryFormat = (fmt: string): string | undefined => {
    const m = DATE_PATTERNS[fmt]?.exec(t);
    if (!m) return undefined;
    const [a, b, c] = [Number(m[1]), Number(m[2]), Number(m[3])];
    if (fmt === 'yyyy-MM-dd') return isoOf(a, b, c);
    if (fmt === 'MM/dd/yyyy') return isoOf(c, a, b);
    return isoOf(c, b, a);
  };
  if (format && format !== 'auto') return tryFormat(format);
  for (const fmt of ['yyyy-MM-dd', 'dd-MM-yyyy', 'dd.MM.yyyy', 'dd/MM/yyyy']) {
    const v = tryFormat(fmt);
    if (v) return v;
  }
  return undefined;
}

const splitList = (text: string) => text.split(/[;|]/).map((x) => x.trim()).filter(Boolean);

// ----- row planning -----

export interface ImportRefs {
  entities: { id: Id; name: string; currency: string; isActive: boolean }[];
  departments: { id: Id; name: string }[];
  types: { id: Id; name: string }[];
  users: { id: Id; name: string; email: string; roles: Role[]; isActive: boolean }[];
  counterparties: { id: Id; name: string; vatNo: string | null; externalId: string | null; isActive: boolean }[];
  contracts: { id: Id; contractNo: string; title: string }[];
  currencies: string[];
  tags: { id: Id; name: string }[];
  customFields: CustomFieldDef[];
  fieldRules: FieldRules;
  today: string;
}

export interface RowPlan {
  preview: ImportRowPreview;
  /** Parsed, resolved values for the mock to apply (only for Create/Update). */
  data: Record<string, unknown>;
  matchId: Id | null;
}

const byName = <T extends { name: string }>(list: T[], name: string) => list.find((x) => x.name.toLowerCase() === name.trim().toLowerCase());
const normName = (s: string) => s.toLowerCase().replace(/\b(a\/s|aps|ab|gmbh|ltd|llc|inc|s\.r\.l\.|sp\. z o\.o\.|b\.v\.|n\.v\.|oü|s\.a\.|s\.p\.a\.)/g, '').replace(/[^a-z0-9æøåäöü]/g, '');
export { normName };

export function findCounterparty(refs: Pick<ImportRefs, 'counterparties'>, text: string) {
  const t = text.trim().toLowerCase();
  if (!t) return undefined;
  return (
    refs.counterparties.find((c) => c.externalId?.toLowerCase() === t) ??
    refs.counterparties.find((c) => c.vatNo?.toLowerCase() === t) ??
    refs.counterparties.find((c) => c.name.toLowerCase() === t)
  );
}

function findUser(refs: ImportRefs, text: string) {
  const t = text.trim().toLowerCase();
  return refs.users.find((u) => u.email.toLowerCase() === t) ?? refs.users.find((u) => u.name.toLowerCase() === t);
}

/** Plans every row: action, parsed values, errors and warnings. Nothing is written. */
export function planImport(kind: ImportKind, columns: string[], rows: string[][], mapping: ImportMapping, options: ImportOptions, refs: ImportRefs): { plans: RowPlan[]; mappingErrors: string[] } {
  const fields = importFields(kind, refs.customFields);
  const mappingErrors = fields.filter((fd) => fd.required && !mapping[fd.key]).map((fd) => `${fd.label} is required but not mapped to a column.`);
  const colOf = (key: string) => {
    const col = mapping[key];
    return col ? columns.indexOf(col) : -1;
  };
  const seenKeys = new Map<string, number>();
  const plans = rows.map((row, i): RowPlan => {
    const rowNo = i + 1;
    const errors: ImportRowPreview['errors'] = [];
    const warnings: string[] = [];
    const values: Record<string, string> = {};
    const cell = (key: string) => {
      const idx = colOf(key);
      return idx >= 0 ? (row[idx] ?? '').trim() : '';
    };
    for (const fd of fields) {
      const v = cell(fd.key);
      if (v) values[fd.key] = v;
    }
    const err = (field: string | null, message: string) => errors.push({ field, message });
    const data: Record<string, unknown> = {};
    let matchId: Id | null = null;
    let matchLabel: string | null = null;
    let key = '';

    if (mappingErrors.length > 0) {
      err(null, 'Required columns are not mapped.');
    } else if (kind === 'counterparties') {
      const name = cell('name');
      if (!name) err('name', 'Name is required');
      if (name.length > 300) err('name', 'At most 300 characters');
      const ext = cell('externalId');
      const vat = cell('vatNo');
      const existing =
        (ext && refs.counterparties.find((c) => c.externalId?.toLowerCase() === ext.toLowerCase())) ||
        (vat && refs.counterparties.find((c) => c.vatNo?.toLowerCase() === vat.toLowerCase())) ||
        refs.counterparties.find((c) => normName(c.name) === normName(name) && normName(name) !== '');
      if (existing) {
        matchId = existing.id;
        matchLabel = existing.name;
      }
      key = (ext || vat || normName(name)).toLowerCase();
      const country = cell('country').toUpperCase();
      if (country && !/^[A-Z]{2}$/.test(country)) err('country', `Country "${cell('country')}" is not a two-letter code`);
      const kindText = cell('kind');
      const k = kindText ? KINDS.find((x) => x.toLowerCase() === kindText.toLowerCase()) : 'Supplier';
      if (!k) err('kind', `Kind must be one of ${KINDS.join(', ')}`);
      const email = cell('email');
      if (email && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) err('email', 'Not a valid e-mail address');
      const active = parseBool(cell('isActive'));
      if (active === undefined) err('isActive', 'Use yes or no');
      Object.assign(data, { name, externalId: ext || null, vatNo: vat || null, country: country || null, city: cell('city') || null, kind: k ?? 'Supplier', email: email || null, phone: cell('phone') || null, isActive: active ?? true });
    } else if (kind === 'users') {
      const email = cell('email').toLowerCase();
      const name = cell('name');
      if (!email) err('email', 'E-mail is required');
      else if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) err('email', 'Not a valid e-mail address');
      if (!name) err('name', 'Name is required');
      const existing = refs.users.find((u) => u.email.toLowerCase() === email);
      if (existing) {
        matchId = existing.id;
        matchLabel = `${existing.name} <${existing.email}>`;
      }
      key = email;
      const roles = cell('roles') ? splitList(cell('roles')) : [];
      const badRoles = roles.filter((r) => !ROLES.some((x) => x.toLowerCase() === r.toLowerCase()));
      if (badRoles.length) err('roles', `Unknown role: ${badRoles.join(', ')}`);
      const deps = splitList(cell('departments')).map((n) => ({ n, d: byName(refs.departments, n) }));
      for (const x of deps) if (!x.d) err('departments', `Unknown department "${x.n}"`);
      const ents = splitList(cell('managedEntities')).map((n) => ({ n, e: byName(refs.entities, n) }));
      for (const x of ents) if (!x.e) err('managedEntities', `Unknown legal entity "${x.n}"`);
      const gm = parseBool(cell('groupManagement'));
      if (gm === undefined) err('groupManagement', 'Use yes or no');
      const active = parseBool(cell('isActive'));
      if (active === undefined) err('isActive', 'Use yes or no');
      Object.assign(data, {
        email,
        name,
        title: cell('title') || null,
        roles: roles.length ? roles.map((r) => ROLES.find((x) => x.toLowerCase() === r.toLowerCase())).filter(Boolean) : null,
        departmentIds: cell('departments') ? deps.map((x) => x.d?.id).filter(Boolean) : null,
        managedEntityIds: cell('managedEntities') ? ents.map((x) => x.e?.id).filter(Boolean) : null,
        isGroupManagement: gm ?? null,
        isActive: active ?? null,
      });
    } else {
      const no = cell('contractNo');
      key = no.toLowerCase();
      const existing = no ? refs.contracts.find((c) => c.contractNo.toLowerCase() === no.toLowerCase()) : undefined;
      if (existing) {
        matchId = existing.id;
        matchLabel = `${existing.contractNo} ${existing.title}`;
      }
      const title = cell('title');
      if (!title) err('title', 'Title is required');
      const cpText = cell('counterparty');
      const cp = findCounterparty(refs, cpText);
      if (!cpText) err('counterparty', 'Counterparty is required');
      else if (!cp && !options.createMissingCounterparties) err('counterparty', `Unknown counterparty "${cpText}". Turn on "Create missing counterparties" or import it first.`);
      else if (!cp) warnings.push(`New counterparty "${cpText}" will be created`);
      const type = byName(refs.types, cell('type'));
      if (!cell('type')) err('type', 'Contract type is required');
      else if (!type) err('type', `Unknown contract type "${cell('type')}"`);
      const entity = byName(refs.entities, cell('entity'));
      if (!cell('entity')) err('entity', 'Legal entity is required');
      else if (!entity) err('entity', `Unknown legal entity "${cell('entity')}"`);
      const dep = byName(refs.departments, cell('department'));
      if (!cell('department')) err('department', 'Department is required');
      else if (!dep) err('department', `Unknown department "${cell('department')}"`);
      const owner = findUser(refs, cell('owner'));
      if (!cell('owner')) err('owner', 'Owner is required');
      else if (!owner) err('owner', `Unknown user "${cell('owner')}"`);
      else if (!owner.roles.includes('ContractOwner') && !owner.roles.includes('Admin')) err('owner', `${owner.name} does not have the ContractOwner role`);
      const person = (k: string) => {
        if (!cell(k)) return null;
        const u = findUser(refs, cell(k));
        if (!u) err(k, `Unknown user "${cell(k)}"`);
        return u?.id ?? null;
      };
      const delegateId = person('delegate');
      const responsibleId = person('responsible');
      const date = (k: string, label: string) => {
        const v = parseDate(cell(k), options.dateFormat);
        if (v === undefined) err(k, `${label} "${cell(k)}" is not a date in the chosen format`);
        return v ?? null;
      };
      const startDate = date('startDate', 'Start date');
      if (!cell('startDate')) err('startDate', 'Start date is required');
      const endDate = date('endDate', 'End date');
      if (startDate && endDate && endDate < startDate) err('endDate', 'End date is before the start date');
      const signedDate = date('signedDate', 'Signed date');
      const num = (k: string, label: string) => {
        const v = parseNumber(cell(k), options.decimalSeparator);
        if (v === undefined) err(k, `${label} "${cell(k)}" is not a number`);
        return v ?? null;
      };
      const noticePeriodDays = num('noticePeriodDays', 'Notice period');
      const renewalMonths = num('renewalMonths', 'Renewal period');
      const annualValue = num('annualValue', 'Annual value');
      const totalValue = num('totalValue', 'Total value');
      const boolOf = (k: string) => {
        const v = parseBool(cell(k));
        if (v === undefined) err(k, 'Use yes or no');
        return v ?? null;
      };
      const rules = type ? refs.fieldRules.types.find((r) => r.typeId === type.id) : undefined;
      const autoRenewRaw = boolOf('autoRenew');
      const autoRenew = autoRenewRaw ?? rules?.defaults.autoRenew ?? false;
      const changeOfControl = boolOf('changeOfControl');
      let currency = cell('currency').toUpperCase() || rules?.defaults.currency || entity?.currency || 'DKK';
      if (!refs.currencies.includes(currency)) {
        err('currency', `Unknown currency "${currency}"`);
        currency = 'DKK';
      }
      const rr = cell('reminderRecipient') || rules?.defaults.reminderRecipient || 'Owner';
      const reminderRecipient = (['Owner', 'Delegate', 'Responsible'] as ReminderRecipient[]).find((x) => x.toLowerCase() === rr.toLowerCase());
      if (!reminderRecipient) err('reminderRecipient', 'Use Owner, Delegate or Responsible');
      if (noticePeriodDays === null && endDate) warnings.push('No notice period: the contract will be listed as missing data');
      if (annualValue === null && totalValue === null) warnings.push('No contract value: listed as missing data');
      if (changeOfControl === null) warnings.push('Change of control not known: listed as missing data');
      if (options.contractStatus === 'Active' && !signedDate) warnings.push('No signed date: listed as missing data');
      if (options.contractStatus === 'Active' && endDate && endDate < refs.today) warnings.push('End date has passed: imported as Expired');
      const tagNames = splitList(cell('tags'));
      const tagIds = tagNames.map((n) => byName(refs.tags, n)?.id).filter((x): x is Id => !!x);
      for (const n of tagNames) if (!byName(refs.tags, n)) warnings.push(`Unknown tag "${n}" ignored`);
      const customFields: Record<string, CustomFieldValue> = {};
      for (const d of refs.customFields.filter((x) => x.isActive)) {
        const raw = cell(`cf.${d.key}`);
        if (!raw) {
          if (d.required && type && (d.contractTypeIds.length === 0 || d.contractTypeIds.includes(type.id))) warnings.push(`${d.label} is empty`);
          continue;
        }
        if (type && d.contractTypeIds.length > 0 && !d.contractTypeIds.includes(type.id)) {
          warnings.push(`${d.label} does not apply to this contract type and is ignored`);
          continue;
        }
        let v: CustomFieldValue | undefined;
        if (d.type === 'YesNo') v = parseBool(raw) ?? undefined;
        else if (d.type === 'Number') v = parseNumber(raw, options.decimalSeparator) ?? undefined;
        else if (d.type === 'Money') {
          const n = parseNumber(raw, options.decimalSeparator);
          v = n === null || n === undefined ? undefined : { amount: n, currency };
        } else if (d.type === 'Date') v = parseDate(raw, options.dateFormat) ?? undefined;
        else if (d.type === 'Choice') v = d.options.find((o) => o.toLowerCase() === raw.toLowerCase());
        else if (d.type === 'MultiChoice') {
          const parts = splitList(raw).map((p) => d.options.find((o) => o.toLowerCase() === p.toLowerCase()));
          v = parts.every(Boolean) ? (parts as string[]) : undefined;
        } else if (d.type === 'Person') v = findUser(refs, raw)?.id;
        else v = raw;
        if (v === undefined) err(`cf.${d.key}`, `${d.label}: "${raw}" is not valid`);
        else customFields[d.key] = v;
      }
      Object.assign(data, {
        contractNo: no || null,
        title,
        counterpartyName: cp?.name ?? cpText,
        supplierId: cp?.id ?? null,
        createCounterparty: !cp && !!cpText,
        typeId: type?.id,
        entityId: entity?.id,
        departmentId: dep?.id,
        ownerId: owner?.id,
        delegateId,
        responsibleId,
        reminderRecipient: reminderRecipient ?? 'Owner',
        startDate,
        endDate,
        noticePeriodDays,
        autoRenew,
        renewalMonths: autoRenew ? (renewalMonths ?? rules?.defaults.renewalMonths ?? null) : null,
        annualValue,
        totalValue,
        currency,
        paymentTerms: cell('paymentTerms') || null,
        governingLaw: cell('governingLaw') || null,
        liabilityCap: cell('liabilityCap') || null,
        exclusivity: cell('exclusivity') || null,
        changeOfControl,
        signedDate,
        summary: cell('summary') || null,
        tagIds,
        customFields,
      });
    }

    // Duplicates inside the file.
    if (key && mappingErrors.length === 0) {
      const earlier = seenKeys.get(key);
      if (earlier !== undefined) err(null, `Duplicate of row ${earlier}`);
      else seenKeys.set(key, rowNo);
    }

    let action: ImportRowAction;
    if (errors.length > 0) action = 'Error';
    else if (matchId) {
      if (options.mode === 'CreateOnly') {
        err(null, `Already exists: ${matchLabel}`);
        action = 'Error';
      } else action = options.mode === 'SkipDuplicates' ? 'Skip' : 'Update';
    } else action = 'Create';

    return { preview: { rowNo, action, match: matchLabel, values, errors, warnings }, data, matchId };
  });
  return { plans, mappingErrors };
}
