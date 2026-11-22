import { ApiError, type Api } from './api';
import type {
  ContractFilter,
  ContractQuery,
  ImportKind,
  JobKey,
  GetContractOptions,
  TemplateInput,
  DocumentKind,
  FileDownload,
  HolidayCoverInput,
  Id,
  ProblemDetails,
} from './types';

const BASE = '/api';

type QueryValue = string | number | boolean | null | undefined;

/**
 * Query string. Arrays repeat the key (`entityId=a&entityId=b`); the `cf` object becomes
 * `cf.<key>=<value>` (release 3 contract filter).
 */
export function qs(params?: object): string {
  if (!params) return '';
  const sp = new URLSearchParams();
  const add = (k: string, v: QueryValue) => {
    if (v !== undefined && v !== null && v !== '') sp.append(k, String(v));
  };
  for (const [k, v] of Object.entries(params as Record<string, unknown>)) {
    if (Array.isArray(v)) for (const x of v) add(k, x as QueryValue);
    else if (k === 'cf' && v && typeof v === 'object') for (const [ck, cv] of Object.entries(v as Record<string, QueryValue>)) add(`cf.${ck}`, cv);
    else add(k, v as QueryValue);
  }
  const s = sp.toString();
  return s ? `?${s}` : '';
}

async function toProblem(res: Response): Promise<ProblemDetails> {
  const fallback: ProblemDetails = { title: res.statusText || 'Request failed', status: res.status };
  const type = res.headers.get('content-type') ?? '';
  if (!type.includes('json')) return fallback;
  try {
    const body = (await res.json()) as Partial<ProblemDetails>;
    return { ...fallback, ...body, status: res.status, title: body.title ?? fallback.title };
  } catch {
    return fallback;
  }
}

async function send(path: string, init?: RequestInit): Promise<Response> {
  let res: Response;
  try {
    res = await fetch(`${BASE}/${path}`, {
      credentials: 'include',
      ...init,
      // The API rejects state-changing requests without this header (CSRF protection).
      headers: { Accept: 'application/json', 'X-Requested-With': 'dafa-contracts', ...init?.headers },
    });
  } catch {
    throw new ApiError({ title: 'Network error', status: 0, detail: 'The server could not be reached.' });
  }
  if (!res.ok) throw new ApiError(await toProblem(res));
  return res;
}

async function json<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await send(path, init);
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

const get = <T>(path: string) => json<T>(path);
const del = (path: string) => json<void>(path, { method: 'DELETE' });

function withBody<T>(method: 'POST' | 'PUT', path: string, body?: unknown): Promise<T> {
  return json<T>(path, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}
const post = <T>(path: string, body?: unknown) => withBody<T>('POST', path, body);
const put = <T>(path: string, body?: unknown) => withBody<T>('PUT', path, body);

function fileNameFrom(res: Response, fallback: string): string {
  const cd = res.headers.get('content-disposition') ?? '';
  const star = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(cd);
  if (star?.[1]) {
    try {
      return decodeURIComponent(star[1].trim().replace(/^"|"$/g, ''));
    } catch {
      /* fall through */
    }
  }
  const plain = /filename="?([^";]+)"?/i.exec(cd);
  return plain?.[1]?.trim() ?? fallback;
}

async function download(path: string, fallbackName: string): Promise<FileDownload> {
  const res = await send(path, { headers: { Accept: '*/*' } });
  return { blob: await res.blob(), fileName: fileNameFrom(res, fallbackName) };
}

function multipart<T>(path: string, file: File, extra?: Record<string, string>): Promise<T> {
  const form = new FormData();
  form.append('file', file, file.name);
  for (const [k, v] of Object.entries(extra ?? {})) form.append(k, v);
  return json<T>(path, { method: 'POST', body: form });
}

/** CRUD helper for the admin collections. */
function crud<T, TInput>(path: string) {
  return {
    list: () => get<T[]>(path),
    create: (input: TInput) => post<T>(path, input),
    update: (id: Id, input: TInput) => put<T>(`${path}/${id}`, input),
    remove: (id: Id) => del(`${path}/${id}`),
  };
}

export function createHttpApi(): Api {
  const rules = crud('admin/approval-rules');
  const retention = crud('admin/retention-rules');
  const departments = crud('admin/departments');
  const types = crud('admin/contract-types');
  const templates = crud('admin/templates');

  // The generic helper is untyped on purpose; the Api interface types every method.
  const api = {
    getMe: () => get('me'),
    getLookups: () => get('lookups'),
    getSuppliers: (q?: string) => get(`suppliers${qs({ q })}`),
    getHome: () => get('home'),
    search: (q: string) => get(`search${qs({ q })}`),
    getReminderSettings: () => get('reminder-settings'),

    getContracts: (query?: ContractQuery) => get(`contracts${qs(query)}`),
    exportContracts: (query?: ContractQuery) => download(`contracts/export${qs(query)}`, 'contracts.csv'),
    getContract: (id: Id, options?: GetContractOptions) => get(`contracts/${id}${qs({ peek: options?.peek ? true : undefined })}`),
    createContract: (input: unknown) => post('contracts', input),
    updateContract: (id: Id, input: unknown) => put(`contracts/${id}`, input),
    submitContract: (id: Id) => post(`contracts/${id}/submit`),
    signContract: (id: Id, input: unknown) => post(`contracts/${id}/sign`, input),
    decideRenewal: (id: Id, input: unknown) => post(`contracts/${id}/renewal-decision`, input),

    uploadDocument: (contractId: Id, file: File, kind: DocumentKind) =>
      multipart(`contracts/${contractId}/documents`, file, { kind }),
    getDocumentContent: (documentId: Id) => download(`documents/${documentId}/content`, 'document'),

    getObligations: (contractId: Id) => get(`contracts/${contractId}/obligations`),
    createObligation: (contractId: Id, input: unknown) => post(`contracts/${contractId}/obligations`, input),
    updateObligation: (id: Id, input: unknown) => put(`obligations/${id}`, input),
    deleteObligation: (id: Id) => del(`obligations/${id}`),
    completeObligation: (id: Id) => post(`obligations/${id}/complete`),

    createLink: (contractId: Id, input: unknown) => post(`contracts/${contractId}/links`, input),
    deleteLink: (id: Id) => del(`links/${id}`),

    extract: (file: File) => multipart('intake/extract', file),
    getTemplates: () => get('templates'),
    fromTemplate: (input: unknown) => post('intake/from-template', input),

    getMyApprovals: () => get('approvals/mine'),
    decideApprovalStep: (stepId: Id, input: unknown) => post(`approvals/steps/${stepId}/decision`, input),
    delegateApprovalStep: (stepId: Id, userId: Id) => post(`approvals/steps/${stepId}/delegate`, { userId }),
    previewApproval: (query: object) => get(`approvals/preview${qs(query)}`),

    getRenewals: (months = 12) => get(`renewals${qs({ months })}`),
    getOverview: () => get('overview'),

    getAppConfig: () => get('app-config'),
    getOverviewAnalytics: (filter: ContractFilter, ddThresholdDkk?: number) => get(`overview/analytics${qs({ ...filter, ddThresholdDkk })}`),
    searchFull: (query: object) => get(`search/full${qs(query)}`),
    getPerson: (id: Id) => get(`people/${id}`),
    getCounterparty: (id: Id) => get(`counterparties/${id}`),
    getCounterparties: (q?: string) => get(`counterparties${qs({ q })}`),
    getTags: () => get('tags'),
    setContractTags: (id: Id, tagIds: Id[]) => put(`contracts/${id}/tags`, { tagIds }),
    watchContract: (id: Id) => put(`contracts/${id}/watch`),
    unwatchContract: (id: Id) => del(`contracts/${id}/watch`),
    getWatching: () => get('watching'),
    getComments: (id: Id) => get(`contracts/${id}/comments`),
    addComment: (id: Id, input: unknown) => post(`contracts/${id}/comments`, input),
    updateComment: (id: Id, input: unknown) => put(`comments/${id}`, input),
    deleteComment: (id: Id) => del(`comments/${id}`),
    getNotifications: (query?: object) => get(`notifications${qs(query)}`),
    getUnreadCount: () => get('notifications/unread-count'),
    markNotificationRead: (id: Id) => post(`notifications/${id}/read`),
    markAllNotificationsRead: () => post('notifications/read-all'),
    getPreferences: () => get('me/preferences'),
    updatePreferences: (input: unknown) => put('me/preferences', input),
    getContractVersions: (id: Id) => get(`contracts/${id}/versions`),
    compareContract: (id: Id, from: string, to: string) => get(`contracts/${id}/compare${qs({ from, to })}`),
    bulk: (input: unknown) => post('contracts/bulk', input),
    getViews: () => get('views'),
    createView: (input: unknown) => post('views', input),
    updateView: (id: Id, input: unknown) => put(`views/${id}`, input),
    deleteView: (id: Id) => del(`views/${id}`),
    createDataRoomExport: (input: unknown) => post('data-room/exports', input),
    getDataRoomExports: () => get('data-room/exports'),
    getDataRoomExport: (id: Id) => get(`data-room/exports/${id}`),
    getDataRoomContent: (id: Id) => download(`data-room/exports/${id}/content`, 'data-room.zip'),

    admin: {
      getApprovalRules: rules.list,
      createApprovalRule: rules.create,
      updateApprovalRule: rules.update,
      deleteApprovalRule: rules.remove,
      getRetentionRules: retention.list,
      createRetentionRule: retention.create,
      updateRetentionRule: retention.update,
      deleteRetentionRule: retention.remove,
      getDepartments: departments.list,
      createDepartment: departments.create,
      updateDepartment: departments.update,
      deleteDepartment: departments.remove,
      getContractTypes: types.list,
      createContractType: types.create,
      updateContractType: types.update,
      deleteContractType: types.remove,
      getTemplates: templates.list,
      createTemplate: (input: TemplateInput, file: File) =>
        multipart('admin/templates', file, { name: input.name, description: input.description, ...(input.typeId ? { typeId: input.typeId } : {}) }),
      getTemplateContent: (id: Id) => download(`admin/templates/${id}/content`, 'template.docx'),
      updateTemplate: templates.update,
      deleteTemplate: templates.remove,
      getReminderSettings: () => get('admin/reminder-settings'),
      updateReminderSettings: (input: unknown) => put('admin/reminder-settings', input),
      getAudit: (query?: object) => get(`admin/audit${qs(query)}`),
      getOutbox: () => get('admin/outbox'),
      runJobs: () => post('admin/jobs/run'),
      getHolidayCover: () => get('admin/holiday-cover'),
      updateHolidayCover: (input: HolidayCoverInput[]) => put('admin/holiday-cover', input),

      updateDepartmentV3: departments.update,
      updateContractTypeV3: types.update,

      getCounterparties: (query?: object) => get(`admin/counterparties${qs(query)}`),
      createCounterparty: (input: unknown) => post('admin/counterparties', input),
      updateCounterparty: (id: Id, input: unknown) => put(`admin/counterparties/${id}`, input),
      getCounterpartyDuplicates: () => get('admin/counterparties/duplicates'),
      mergeCounterparties: (input: unknown) => post('admin/counterparties/merge', input),

      getImportFields: (kind: ImportKind) => get(`admin/imports/${kind}/fields`),
      uploadImport: (kind: ImportKind, file: File) => multipart(`admin/imports/${kind}/upload`, file),
      previewImport: (sessionId: Id, input: unknown) => post(`admin/imports/sessions/${sessionId}/preview`, input),
      runImport: (sessionId: Id, input: unknown) => post(`admin/imports/sessions/${sessionId}/run`, input),
      getImportHistory: (kind?: ImportKind) => get(`admin/imports/history${qs({ kind })}`),
      getImportTemplate: (kind: ImportKind) => download(`admin/imports/${kind}/template`, `${kind}-import.csv`),

      getSyncConnections: () => get('admin/sync-connections'),
      getSyncSources: () => get('admin/sync-sources'),
      createSyncSource: (input: unknown) => post('admin/sync-sources', input),
      updateSyncSource: (id: Id, input: unknown) => put(`admin/sync-sources/${id}`, input),
      deleteSyncSource: (id: Id) => del(`admin/sync-sources/${id}`),
      testSyncSource: (input: unknown) => post('admin/sync-sources/test', input),
      runSyncSource: (id: Id) => post(`admin/sync-sources/${id}/run`),
      getSyncRuns: (id: Id) => get(`admin/sync-sources/${id}/runs`),

      getCustomFields: () => get('admin/custom-fields'),
      createCustomField: (input: unknown) => post('admin/custom-fields', input),
      updateCustomField: (id: Id, input: unknown) => put(`admin/custom-fields/${id}`, input),
      deleteCustomField: (id: Id) => del(`admin/custom-fields/${id}`),
      reorderCustomFields: (ids: Id[]) => put('admin/custom-fields/order', { ids }),

      getFieldRules: () => get('admin/field-rules'),
      updateFieldRules: (input: unknown) => put('admin/field-rules', input),

      getEntities: () => get('admin/entities'),
      createEntity: (input: unknown) => post('admin/entities', input),
      updateEntity: (id: Id, input: unknown) => put(`admin/entities/${id}`, input),

      getCurrencies: () => get('admin/currencies'),
      updateCurrencies: (input: unknown) => put('admin/currencies', input),

      getTags: () => get('admin/tags'),
      createTag: (input: unknown) => post('admin/tags', input),
      updateTag: (id: Id, input: unknown) => put(`admin/tags/${id}`, input),
      deleteTag: (id: Id) => del(`admin/tags/${id}`),

      getUsers: (query?: object) => get(`admin/users${qs(query)}`),
      createUser: (input: unknown) => post('admin/users', input),
      updateUser: (id: Id, input: unknown) => put(`admin/users/${id}`, input),
      getEffectiveAccess: (userId: Id, query?: object) => get(`admin/users/${userId}/effective-access${qs(query)}`),
      previewReassign: (input: unknown) => post('admin/reassign/preview', input),
      reassign: (input: unknown) => post('admin/reassign', input),

      getNotificationSettings: () => get('admin/notification-settings'),
      updateNotificationSettings: (input: unknown) => put('admin/notification-settings', input),
      previewNotificationTemplate: (input: unknown) => post('admin/notification-settings/preview', input),

      getJobs: () => get('admin/jobs'),
      updateJobSchedule: (key: JobKey, schedule: unknown) => put(`admin/jobs/${key}`, { schedule }),
      runJob: (key: JobKey) => post(`admin/jobs/${key}/run`),

      getAppSettings: () => get('admin/app-settings'),
      updateAppSettings: (input: unknown) => put('admin/app-settings', input),
    },

    getDevConfig: () => get('dev/config'),
    getDevUsers: (accessCode?: string) => json('dev/users', accessCode ? { headers: { 'X-Dev-Access-Code': accessCode } } : undefined),
    devLogin: (userId: Id, accessCode?: string) => post('dev/login', accessCode ? { userId, accessCode } : { userId }),
    logout: () => post('auth/logout'),
  };
  return api as Api;
}
