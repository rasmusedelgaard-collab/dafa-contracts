// API contract types. The first block mirrors docs/BUILD_BRIEF.md section 5 exactly.
// The second block ("Shapes the brief leaves open") is this frontend's reading of endpoints
// whose payloads the brief only names; they are recorded in docs/DECISIONS.md.

export type Id = string; // GUID

export type Role = 'Reader' | 'ContractOwner' | 'Approver' | 'Admin';
export type ContractStatus =
  | 'Draft'
  | 'InApproval'
  | 'Approved'
  | 'Active'
  | 'NoticeGiven'
  | 'Expired'
  | 'Terminated';
export type ReminderRecipient = 'Owner' | 'Delegate' | 'Responsible';
export type RenewalDecision = 'Renew' | 'Renegotiate' | 'GiveNotice' | 'LetExpire';
export type LinkKind = 'Amendment' | 'Appendix' | 'Framework' | 'Related';
export type DocumentKind = 'Main' | 'Appendix' | 'Amendment' | 'ApprovalLog' | 'Other';

export interface Me {
  id: Id;
  name: string;
  email: string;
  initials: string;
  roles: Role[];
  departmentIds: Id[];
  managedEntityIds: Id[];
  isGroupManagement: boolean;
  authMode: 'Dev' | 'Entra';
}

export interface NamedRef {
  id: Id;
  name: string;
}

export interface LookupUser {
  id: Id;
  name: string;
  initials: string;
  departmentIds: Id[];
  /** Lets the delegate picker offer only Approver/Admin users. Optional so an older API still works. */
  roles?: Role[];
}

export interface Lookups {
  entities: NamedRef[];
  departments: NamedRef[];
  contractTypes: NamedRef[];
  users: LookupUser[];
  currencies: string[];
}

export interface ContractListItem {
  id: Id;
  contractNo: string;
  title: string;
  counterparty: string;
  typeName: string;
  entityName: string;
  departmentName: string;
  ownerName: string;
  ownerInitials: string;
  noticeDeadline: string | null;
  daysToNotice: number | null;
  annualValue: number | null;
  currency: string;
  status: string;
  missingData: string[];
}

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

/** The editable contract fields from section 4 of the brief, by the same names. */
export interface ContractFields {
  title: string;
  typeId: Id;
  supplierId: Id | null;
  counterpartyName: string;
  entityId: Id;
  departmentId: Id;
  ownerId: Id;
  delegateId: Id | null;
  responsibleId: Id | null;
  reminderRecipient: ReminderRecipient;
  startDate: string;
  /** null = open-ended */
  endDate: string | null;
  /** null only on imported contracts that are missing the notice period */
  noticePeriodDays: number | null;
  autoRenew: boolean;
  renewalMonths: number | null;
  annualValue: number | null;
  totalValue: number | null;
  currency: string;
  paymentTerms: string | null;
  priceAdjustment: string | null;
  priceAdjustmentDate: string | null;
  governingLaw: string | null;
  liabilityCap: string | null;
  exclusivity: string | null;
  changeOfControl: boolean;
  summary: string | null;
}

export interface LifecycleStep {
  name: string;
  date: string | null;
  state: 'done' | 'current' | 'upcoming' | 'alert';
}

export interface ContractPerson {
  role: 'Owner' | 'Delegate' | 'Responsible';
  userId: Id;
  name: string;
  initials: string;
  getsReminders: boolean;
}

export interface ContractDocument {
  id: Id;
  name: string;
  kind: string;
  version: number;
  size: number;
  uploadedAt: string;
  uploadedBy: string;
}

export interface ContractLink {
  id: Id;
  contractId: Id;
  title: string;
  kind: string;
  direction: 'out' | 'in';
}

export interface ContractReminder {
  kind: string;
  dueDate: string;
  sentAt: string | null;
}

export interface ActivityEntry {
  at: string;
  who: string;
  text: string;
}

export interface Obligation {
  id: Id;
  description: string;
  dueDate: string | null;
  frequencyMonths: number | null;
  responsibleId: Id;
  responsibleName: string;
  status: 'Open' | 'Done';
}

export interface Deviation {
  severity: 'High' | 'Medium' | 'Low' | 'None';
  text: string;
  standard: string;
}

export type ApprovalStepStatus = 'Waiting' | 'Pending' | 'Approved' | 'Rejected' | 'Delegated';

export interface ApprovalStep {
  id: Id;
  approverId: Id;
  approverName: string;
  initials: string;
  roleLabel: string;
  status: ApprovalStepStatus;
  decidedAt: string | null;
  comment: string | null;
}

export interface ApprovalStage {
  order: number;
  steps: ApprovalStep[];
}

export interface ApprovalInstance {
  id: Id;
  contractId: Id;
  contractTitle: string;
  status: 'Pending' | 'Approved' | 'Rejected';
  currentStage: number;
  stages: ApprovalStage[];
}

export interface ContractDetail extends ContractListItem, Omit<ContractFields, 'changeOfControl'> {
  /** null when it was never filled in (then "Change of control" is in `missingData`). */
  changeOfControl: boolean | null;
  /**
   * camelCase names of the fields the signed-in user may not change. From submission on (every
   * status except Draft) the approved terms are locked for owners, but only the ones that have a
   * value: an empty locked field is not listed and can be filled in once. `annualValue` and
   * `totalValue` are listed together as soon as either is set. Changing a listed field returns
   * 409 `locked-fields`. Empty for administrators and in Draft.
   */
  lockedFields: string[];
  /** Who submitted the current or most recent approval. */
  submittedById?: Id | null;
  status: ContractStatus;
  signedDate: string | null;
  signingMethod: string | null;
  renewalDecision: RenewalDecision | null;
  renewalDecidedBy: string | null;
  renewalDecidedAt: string | null;
  renewalComment: string | null;
  nextReminderDate: string | null;
  lifecycle: LifecycleStep[];
  people: ContractPerson[];
  documents: ContractDocument[];
  obligations: Obligation[];
  links: ContractLink[];
  approval: ApprovalInstance | null;
  reminders: ContractReminder[];
  activity: ActivityEntry[];
  access: string[];
  deviations: Deviation[];
  canEdit: boolean;
  canDecideRenewal: boolean;
  storeUrl: string | null;
}

export type TaskKind = 'Renewal' | 'Approval' | 'Obligation' | 'MissingData';
export type Urgency = 'high' | 'medium' | 'low' | 'info';

export interface HomeTask {
  kind: TaskKind;
  title: string;
  meta: string;
  pill: string;
  urgency: Urgency;
  contractId: Id | null;
  approvalStepId: Id | null;
  obligationId: Id | null;
}

export interface HomeData {
  decisionsThisWeek: number;
  renewalsThisWeek: number;
  approvalsWaiting: number;
  renewingIn90: { count: number; valueDkk: number };
  myContracts: number;
  tasks: HomeTask[];
  noticeByMonth: { month: string; count: number }[];
  recent: { id: Id; title: string }[];
}

// ---------------------------------------------------------------------------
// Shapes the brief leaves open (see docs/DECISIONS.md)
// ---------------------------------------------------------------------------

export type ContractView = 'all' | 'due90' | 'approval' | 'mine' | 'missing';

/** `sort` is a field name, prefixed with `-` for descending. */
export type ContractSortField = 'title' | 'entity' | 'owner' | 'notice' | 'status' | 'value';
export type ContractSort = ContractSortField | `-${ContractSortField}`;

export interface ContractQuery {
  view?: ContractView;
  q?: string;
  entityId?: Id;
  departmentId?: Id;
  typeId?: Id;
  status?: ContractStatus;
  sort?: ContractSort;
  page?: number;
  pageSize?: number;
}

/** Body of `POST contracts` and `PUT contracts/{id}`. */
export interface ContractInput extends ContractFields {
  /** Only on create: the file from `intake/extract` or `intake/from-template`. */
  uploadId?: Id | null;
}

export interface Supplier {
  id: Id;
  name: string;
  country: string | null;
  vatNo: string | null;
}

export interface SearchResult {
  kind: 'Contract' | 'Supplier';
  id: Id;
  title: string;
  subtitle: string;
}

export interface SignInput {
  signedDate: string;
  method: string;
}

export interface RenewalDecisionInput {
  decision: RenewalDecision;
  comment: string;
  /** Required for `Renew` on a contract that does not renew automatically. */
  newEndDate?: string | null;
}

export interface ObligationInput {
  description: string;
  dueDate: string | null;
  frequencyMonths: number | null;
  responsibleId: Id;
}

export interface LinkInput {
  toContractId: Id;
  kind: LinkKind;
}

export interface Suggestion {
  /** A key of ContractFields. For *Id fields the value is the lookup id (a matching name is also accepted). */
  field: string;
  value: string;
  confidence: number;
}

export interface ExtractResult {
  uploadId: Id;
  fileName: string;
  size: number;
  pages: number;
  suggestions: Suggestion[];
  summary: string;
  deviations: Deviation[];
  /** Set when the file was too large or too slow to read; then there are no suggestions. */
  note?: string | null;
}

export interface Template {
  id: Id;
  name: string;
  description: string;
  typeId: Id | null;
  fileName: string;
  /** Names of the `{{placeholders}}` the template fills. */
  placeholders: string[];
}

export interface FromTemplateInput {
  templateId: Id;
  fields: Record<string, string>;
}

export interface FromTemplateResult {
  uploadId: Id;
  fileName: string;
}

export interface MyApproval {
  instance: ApprovalInstance;
  stepId: Id;
  waitingDays: number;
  contract: ContractDetail;
}

export interface ApprovalDecisionInput {
  decision: 'Approve' | 'Reject';
  comment: string;
}

export interface ApprovalPreviewQuery {
  entityId?: Id;
  typeId?: Id;
  departmentId?: Id;
  /** Contract value in DKK: the larger of total and annual value. */
  value?: number;
}

export interface ApprovalPreview {
  stages: { order: number; approvers: string[] }[];
  /** The rule that matched (phase 2 addition; optional so an older API still works). */
  ruleId?: Id | null;
  ruleName?: string | null;
}

export interface RenewalItem {
  id: Id;
  title: string;
  entityId: Id;
  entityName: string;
  departmentName: string;
  noticeDeadline: string;
  /** Negative when the deadline has passed without a decision. */
  daysToNotice: number;
  autoRenew: boolean;
  annualValue: number | null;
  currency: string;
}

export interface OverviewData {
  activeContracts: number;
  annualValueDkk: number;
  dueIn90: number;
  inApproval: number;
  missingData: number;
  valueByEntity: { name: string; valueDkk: number }[];
  countByType: { name: string; count: number }[];
  changeOfControl: { id: Id; title: string; entityName: string }[];
  expiringNoRenewal: ContractListItem[];
}

// ----- Admin -----

export type ApproverRole = 'DepartmentHead' | 'EntityManager' | 'GroupCFO' | 'GroupCEO' | 'Legal';

/** One approver slot in a stage: either a specific user or a role resolver. */
export interface RuleApprover {
  userId: Id | null;
  role: ApproverRole | null;
}

export interface ApprovalRule {
  id: Id;
  name: string;
  entityId: Id | null;
  typeId: Id | null;
  departmentId: Id | null;
  /** DKK, inclusive lower bound; null = any */
  minValue: number | null;
  /** DKK, exclusive upper bound; null = any */
  maxValue: number | null;
  priority: number;
  stages: { order: number; approvers: RuleApprover[] }[];
}
export type ApprovalRuleInput = Omit<ApprovalRule, 'id'>;

export type RetentionAction = 'ReviewThenDelete' | 'DeleteAutomatically' | 'Keep';
export interface RetentionRule {
  id: Id;
  typeId: Id;
  typeName: string;
  years: number | null;
  action: RetentionAction;
  reviewerDepartmentId: Id | null;
}
export type RetentionRuleInput = Omit<RetentionRule, 'id' | 'typeName'>;

export interface NameInput {
  name: string;
}

export interface TemplateInput {
  name: string;
  description: string;
  typeId: Id | null;
}

export interface ReminderSettings {
  /** Days before the notice deadline, e.g. [120, 60, 30]. */
  noticeDays: number[];
  /** Escalate to the owner this many days before the deadline when no decision is recorded. */
  escalationDays: number;
  /** Remind this many days before an obligation is due. */
  obligationDays: number;
}

export interface AuditEvent {
  id: Id;
  at: string;
  who: string;
  action: string;
  contractId: Id | null;
  contractTitle: string | null;
  text: string;
}

export interface AuditQuery {
  contractId?: Id;
  page?: number;
}

export interface OutboxMessage {
  id: Id;
  createdAt: string;
  to: string;
  subject: string;
  body: string;
  channel: 'Mail' | 'Teams';
  contractId: Id | null;
}

export interface JobRunResult {
  ranAt: string;
  remindersCreated: number;
  escalations: number;
  obligationReminders: number;
  /** Yearly review reminders for open-ended contracts (kind `AnnualReview`). */
  annualReviewReminders: number;
  contractsExpired: number;
}

export interface HolidayCover {
  id: Id;
  userId: Id;
  userName: string;
  deputyId: Id;
  deputyName: string;
  from: string;
  to: string;
}
export type HolidayCoverInput = Pick<HolidayCover, 'userId' | 'deputyId' | 'from' | 'to'>;

/** `GET dev/config` (anonymous): how this site signs users in. */
export interface DevConfig {
  authMode: 'Dev' | 'Entra';
  /** Dev mode only: a shared access code must be entered before the demo-user picker. */
  accessCodeRequired: boolean;
}

export interface GetContractOptions {
  /** `?peek=true`: read without recording the contract as "recently opened" (quick look, dialogs). */
  peek?: boolean;
}

export interface DevUser {
  id: Id;
  name: string;
  initials: string;
  /** Short description of the access the demo user has, e.g. "Group CFO". */
  title: string;
}

/**
 * `type` of a 409 problem. Only `stale` means "someone else changed this at the same time";
 * for every other type the server's `detail` is the message to show.
 */
export const PROBLEM = {
  stale: 'urn:dafa:problem:stale',
  noApprovalRule: 'urn:dafa:problem:no-approval-rule',
  noApprover: 'urn:dafa:problem:no-approver',
  lockedFields: 'urn:dafa:problem:locked-fields',
  duplicate: 'urn:dafa:problem:duplicate',
  invalidState: 'urn:dafa:problem:invalid-state',
} as const;

/** RFC 7807 problem details. */
export interface ProblemDetails {
  type?: string;
  title: string;
  status: number;
  detail?: string;
  errors?: Record<string, string[]>;
}

export interface FileDownload {
  blob: Blob;
  fileName: string;
}
