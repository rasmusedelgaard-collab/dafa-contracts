// In-memory "database" record types for the mock API. These are storage shapes;
// the API DTOs in ../types.ts are projected from them in mockApi.ts.
import type {
  ApprovalRule,
  ApproverRole,
  ContractFields,
  ContractStatus,
  Deviation,
  DocumentKind,
  Id,
  LinkKind,
  NamedRef,
  ReminderSettings,
  RenewalDecision,
  RetentionAction,
  Role,
  Supplier,
} from '../types';

export interface UserRec {
  id: Id;
  name: string;
  email: string;
  initials: string;
  /** Job title, shown in the Dev-mode user switcher. */
  title: string;
  roles: Role[];
  departmentIds: Id[];
  managedEntityIds: Id[];
  isGroupManagement: boolean;
}

export interface ContractRec extends ContractFields {
  id: Id;
  contractNo: string;
  status: ContractStatus;
  signedDate: string | null;
  signingMethod: string | null;
  renewalDecision: RenewalDecision | null;
  renewalDecidedBy: Id | null;
  renewalDecidedAt: string | null;
  renewalComment: string | null;
  createdAt: string;
  createdBy: Id;
  approvedAt: string | null;
  deviations: Deviation[];
}

export interface LinkRec {
  id: Id;
  fromId: Id;
  toId: Id;
  kind: LinkKind;
}

export interface ObligationRec {
  id: Id;
  contractId: Id;
  description: string;
  dueDate: string | null;
  frequencyMonths: number | null;
  responsibleId: Id;
  status: 'Open' | 'Done';
  lastCompletedAt: string | null;
}

export interface DocumentRec {
  id: Id;
  contractId: Id;
  name: string;
  kind: DocumentKind;
  storeKey: string;
  version: number;
  size: number;
  uploadedBy: Id;
  uploadedAt: string;
  /** Uploaded bytes. Seeded documents have none and are generated on download. */
  blob: Blob | null;
}

export interface StepRec {
  id: Id;
  approverId: Id;
  roleLabel: string;
  status: 'Waiting' | 'Pending' | 'Approved' | 'Rejected' | 'Delegated';
  decidedAt: string | null;
  comment: string | null;
  /** ISO date the step became Pending; drives "waiting n days". */
  pendingSince: string | null;
}

export interface ApprovalRec {
  id: Id;
  contractId: Id;
  status: 'Pending' | 'Approved' | 'Rejected';
  currentStage: number;
  startedAt: string;
  stages: { order: number; steps: StepRec[] }[];
}

export interface ReminderRec {
  id: Id;
  contractId: Id;
  kind: string;
  dueDate: string;
  sentAt: string | null;
  recipientId: Id;
}

export interface AuditRec {
  id: Id;
  at: string;
  userId: Id | null;
  action: string;
  contractId: Id | null;
  text: string;
}

export interface OutboxRec {
  id: Id;
  createdAt: string;
  toUserId: Id;
  subject: string;
  body: string;
  channel: 'Mail' | 'Teams';
  contractId: Id | null;
}

export interface UploadRec {
  id: Id;
  fileName: string;
  size: number;
  blob: Blob;
  kind: DocumentKind;
  deviations: Deviation[];
  summary: string | null;
}

export interface TemplateRec {
  id: Id;
  name: string;
  description: string;
  typeId: Id | null;
  fileName: string;
  placeholders: string[];
  /** Body text with `{{placeholders}}`. */
  body: string;
}

export interface RetentionRec {
  id: Id;
  typeId: Id;
  years: number | null;
  action: RetentionAction;
  reviewerDepartmentId: Id | null;
}

export interface HolidayCoverRec {
  id: Id;
  userId: Id;
  deputyId: Id;
  from: string;
  to: string;
}

export interface Db {
  entities: NamedRef[];
  departments: NamedRef[];
  contractTypes: NamedRef[];
  currencies: string[];
  users: UserRec[];
  suppliers: Supplier[];
  contracts: ContractRec[];
  links: LinkRec[];
  obligations: ObligationRec[];
  documents: DocumentRec[];
  approvals: ApprovalRec[];
  approvalRules: ApprovalRule[];
  reminders: ReminderRec[];
  reminderSettings: ReminderSettings;
  retentionRules: RetentionRec[];
  templates: TemplateRec[];
  audit: AuditRec[];
  outbox: OutboxRec[];
  holidayCover: HolidayCoverRec[];
  uploads: UploadRec[];
  /** Who fills each role resolver. DepartmentHead is per department. */
  roleHolders: Record<Exclude<ApproverRole, 'DepartmentHead' | 'EntityManager'>, Id>;
  departmentHeads: Record<Id, Id>;
  /** Recently opened contract ids per user, newest first. */
  recent: Record<Id, Id[]>;
  nextContractSeq: number;
}

/** Deterministic GUID-shaped id for seeded rows, so tests and deep links are stable. */
export function seedId(group: number, n: number): Id {
  return `${String(group).padStart(8, '0')}-0000-4000-8000-${String(n).padStart(12, '0')}`;
}

let counter = 0;
export function newId(): Id {
  const c = globalThis.crypto;
  if (c && typeof c.randomUUID === 'function') return c.randomUUID();
  counter += 1;
  const rand = Math.floor(Math.random() * 0xffffffff).toString(16).padStart(8, '0');
  return `${rand}-0000-4000-8000-${String(Date.now() + counter).padStart(12, '0').slice(-12)}`;
}
