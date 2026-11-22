import { ApiError, type Api } from './api';
import type {
  ContractQuery,
  DocumentKind,
  FileDownload,
  HolidayCoverInput,
  Id,
  ProblemDetails,
} from './types';

const BASE = '/api';

type QueryValue = string | number | boolean | null | undefined;

function qs(params?: object): string {
  if (!params) return '';
  const sp = new URLSearchParams();
  for (const [k, v] of Object.entries(params as Record<string, QueryValue>)) {
    if (v !== undefined && v !== null && v !== '') sp.set(k, String(v));
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
      headers: { Accept: 'application/json', ...init?.headers },
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

    getContracts: (query?: ContractQuery) => get(`contracts${qs(query)}`),
    exportContracts: (query?: ContractQuery) => download(`contracts/export${qs(query)}`, 'contracts.csv'),
    getContract: (id: Id) => get(`contracts/${id}`),
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
      createTemplate: templates.create,
      updateTemplate: templates.update,
      deleteTemplate: templates.remove,
      getReminderSettings: () => get('admin/reminder-settings'),
      updateReminderSettings: (input: unknown) => put('admin/reminder-settings', input),
      getAudit: (query?: object) => get(`admin/audit${qs(query)}`),
      getOutbox: () => get('admin/outbox'),
      runJobs: () => post('admin/jobs/run'),
      getHolidayCover: () => get('admin/holiday-cover'),
      updateHolidayCover: (input: HolidayCoverInput[]) => put('admin/holiday-cover', input),
    },

    getDevUsers: () => get('dev/users'),
    devLogin: (userId: Id) => post('dev/login', { userId }),
    logout: () => post('auth/logout'),
  };
  return api as Api;
}
