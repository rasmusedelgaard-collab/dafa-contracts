// Saved register views: a view is the register's URL state (filters, sort) plus its columns.
import { REGISTER_COLUMNS, type ContractView, type CustomFieldDef, type RegisterColumn, type SavedView, type SavedViewInput } from '../api/types';
import { filterToParams, parseFilter } from './drill';

const VIEWS: ContractView[] = ['all', 'due90', 'approval', 'mine', 'missing'];

/** Only columns that exist: standard keys, and custom fields that are active and shown in the register. */
export function sanitizeColumns(columns: string[], customFields: CustomFieldDef[]): RegisterColumn[] {
  const custom = new Set(customFields.filter((d) => d.isActive && d.showInRegister).map((d) => `cf.${d.key}`));
  const out = columns.filter((c, i) => columns.indexOf(c) === i && ((REGISTER_COLUMNS as readonly string[]).includes(c) || custom.has(c))) as RegisterColumn[];
  // The contract column is what opens a contract; it is always first.
  return ['contract', ...out.filter((c) => c !== 'contract')];
}

/** The query a view stores, read from the register URL. */
export function queryFromParams(params: URLSearchParams): SavedView['query'] {
  const view = params.get('view');
  const sort = params.get('sort');
  return {
    ...parseFilter(params),
    ...(view && VIEWS.includes(view as ContractView) && view !== 'all' ? { view: view as ContractView } : {}),
    ...(sort ? { sort } : {}),
  };
}

/** Register URL parameters for a view (`sv` remembers which view is applied). */
export function paramsFromView(view: Pick<SavedView, 'id' | 'query' | 'columns'>): URLSearchParams {
  const { view: v, sort, ...filter } = view.query;
  const p = filterToParams(filter);
  if (v && v !== 'all') p.set('view', v);
  if (sort && sort !== 'notice') p.set('sort', sort);
  p.set('cols', view.columns.join(','));
  p.set('sv', view.id);
  return p;
}

/** The input to save the current register state as a view. */
export function viewInput(name: string, scope: SavedView['scope'], params: URLSearchParams, columns: RegisterColumn[], isDefault: boolean): SavedViewInput {
  return { name: name.trim(), scope, columns, query: queryFromParams(params), isDefault: scope === 'Personal' && isDefault };
}

/** True when the URL still matches the applied view (otherwise the UI offers "Save changes"). */
export function viewMatches(view: Pick<SavedView, 'query' | 'columns'>, params: URLSearchParams, columns: string[]): boolean {
  const norm = (o: object) => JSON.stringify(Object.entries(o).filter(([, v]) => v !== undefined).sort(([a], [b]) => a.localeCompare(b)));
  const { sort: s1, ...a } = view.query;
  const { sort: s2, ...b } = queryFromParams(params);
  return norm(a) === norm(b) && (s1 ?? 'notice') === (s2 ?? 'notice') && view.columns.join(',') === columns.join(',');
}
