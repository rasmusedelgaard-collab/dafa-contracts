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

export interface Me extends UserV4 {
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

export interface LookupUser extends UserV4 {
  id: Id;
  name: string;
  initials: string;
  departmentIds: Id[];
  /** Release 3. Inactive users are included so names still resolve; pickers hide them. */
  isActive?: boolean;
  email?: string;
  title?: string | null;
  /** Lets the delegate picker offer only Approver/Admin users. Optional so an older API still works. */
  roles?: Role[];
}

export interface Lookups {
  /** Release 3: entities also carry country, currency and isActive; inactive rows are included. */
  entities: (NamedRef & { country?: string; currency?: string; isActive?: boolean })[];
  departments: NamedRefV3[];
  contractTypes: NamedRefV3[];
  users: LookupUser[];
  currencies: string[];
}

export interface ContractListItem extends ContractListItemV3 {
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

export interface ContractDetail
  extends Omit<ContractListItem, keyof ContractListItemV3>,
    Omit<ContractFields, 'changeOfControl'>,
    ContractDetailV3,
    ContractDetailV4,
    Pick<ContractListItemV3, 'annualValueDkk' | 'totalValueDkk' | 'delegateName' | 'responsibleName' | 'counterpartyId' | 'createdAt' | 'hasSignedDocument'> {
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

export interface ContractQuery extends Omit<ContractFilter, 'entityId' | 'departmentId' | 'typeId' | 'status'> {
  view?: ContractView;
  q?: string;
  /** Release 3: repeatable (see ContractFilter). */
  entityId?: Id | Id[];
  departmentId?: Id | Id[];
  typeId?: Id | Id[];
  status?: ContractStatus | ContractStatus[];
  /** Release 3 adds the ContractSortFieldV3 fields. */
  sort?: ContractSort | ContractSortFieldV3 | `-${ContractSortFieldV3}`;
  page?: number;
  pageSize?: number;
}

/** Body of `POST contracts` and `PUT contracts/{id}`. */
export interface ContractInput extends ContractFields, ContractInputV3 {
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
  /** Release 3 adds 'Document' (with contractId) and 'Person'. */
  kind: 'Contract' | 'Supplier' | 'Document' | 'Person';
  id: Id;
  title: string;
  subtitle: string;
  /** Release 3: for a Document hit, the contract it belongs to. */
  contractId?: Id | null;
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

export interface Template extends TemplateV4 {
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

export interface TemplateInput extends TemplateV4 {
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
  /** Release 4: logo for the sign-in screen. */
  branding?: Branding;
  authMode: 'Dev' | 'Entra';
  /** Dev mode only: a shared access code must be entered before the demo-user picker. */
  accessCodeRequired: boolean;
  /**
   * The session cookie is `Secure`: sign-in only works on https. On a plain http page the app
   * moves to the https address by itself. Absent on an API from before this field.
   */
  httpsRequired?: boolean;
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

// ===========================================================================
// Release 3 (docs/CONTRACT_V3.md is the spec; keep both in step).
// Everything below is ADDED; earlier shapes only gain optional fields.
// Every endpoint that returns contract data is scoped by the access model
// (BUILD_BRIEF section 3): a user never receives a contract, document, comment
// or aggregate built from a contract they cannot see.
// ===========================================================================

export const PROBLEM_V3 = {
  /** 409: the row is used elsewhere (a tag, custom field, entity, counterparty with contracts...). */
  inUse: 'urn:dafa:problem:in-use',
  /** 409: the field is synced from Entra ID / a sync source and cannot be changed here. */
  syncedField: 'urn:dafa:problem:synced-field',
  /** 409: the import session expired or was already run. */
  importState: 'urn:dafa:problem:import-state',
  /** 400: a feature is turned off in General settings. */
  featureOff: 'urn:dafa:problem:feature-off',
} as const;

// ----- Shared contract filter (register, overview drill-down, export, data room) -----

/** Value band of max(totalValue, annualValue) in DKK. Bounds: min inclusive, max exclusive. */
export type ValueBand = 'lt1m' | '1to5m' | '5to20m' | 'gte20m';
export const VALUE_BANDS: { key: ValueBand; label: string; min: number | null; max: number | null }[] = [
  { key: 'lt1m', label: 'Under DKK 1m', min: null, max: 1_000_000 },
  { key: '1to5m', label: 'DKK 1–5m', min: 1_000_000, max: 5_000_000 },
  { key: '5to20m', label: 'DKK 5–20m', min: 5_000_000, max: 20_000_000 },
  { key: 'gte20m', label: 'DKK 20m and above', min: 20_000_000, max: null },
];
/** Which date `from`/`to` apply to. `notice` = the computed notice deadline. */
export type DateField = 'notice' | 'end' | 'start' | 'created' | 'signed';

/**
 * Filters shared by `GET contracts`, `contracts/export`, `overview/analytics`, `data-room/exports`.
 * A parameter given several times (`entityId=a&entityId=b`) matches ANY of its values; different
 * parameters must ALL match. Unknown ids simply match nothing.
 */
export interface ContractFilter extends ContractFilterV4 {
  q?: string;
  /** Explicit contract ids (e.g. "export selected"). */
  id?: Id[];
  entityId?: Id | Id[];
  departmentId?: Id | Id[];
  typeId?: Id | Id[];
  status?: ContractStatus | ContractStatus[];
  /** Counterparty master record id (= `supplierId` on the contract). */
  counterpartyId?: Id | Id[];
  /** Owner role only. */
  ownerId?: Id | Id[];
  /** Owner, delegate or responsible (any role). */
  personId?: Id;
  valueBand?: ValueBand | ValueBand[];
  /** Inclusive lower bound on max(total, annual) in DKK. */
  minValueDkk?: number;
  dateField?: DateField;
  /** ISO date, inclusive. */
  from?: string;
  /** ISO date, inclusive. */
  to?: string;
  /** Active contracts with no renewal decision whose notice deadline is between today and today + N days. */
  noticeWithinDays?: number;
  /** true: only contracts with a recorded renewal decision; false: only without. */
  decided?: boolean;
  tagId?: Id | Id[];
  changeOfControl?: boolean;
  /** true: `exclusivity` has a value other than empty / "None". */
  exclusivity?: boolean;
  autoRenew?: boolean;
  /** true: `missingData` is not empty (uses the data-quality fields from General settings). */
  missingData?: boolean;
  /** One data-quality field key (see `DataQualityField`); contracts missing that field. */
  missingField?: string;
  /** Live contract (Active, NoticeGiven, Approved) with no document of kind Main whose name or kind marks it signed (see spec). */
  missingSignedDocument?: boolean;
  /** Contracts the signed-in user watches. */
  watching?: boolean;
  /**
   * Include Expired and Terminated. `GET contracts`/export default true (unchanged behaviour);
   * `overview/analytics` defaults false. An explicit `status` always wins.
   */
  includeEnded?: boolean;
  /** Custom-field filters, sent as `cf.<key>=<value>`; MultiChoice matches if the value is one of the selected options; YesNo uses "true"/"false". */
  cf?: Record<string, string | string[]>;
}

export type ContractSortFieldV3 =
  | ContractSortField
  | 'contractNo'
  | 'counterparty'
  | 'type'
  | 'department'
  | 'start'
  | 'end'
  | 'totalValue'
  | 'created';

export interface TagRef {
  id: Id;
  name: string;
  color: TagColor;
}
export type TagColor = 'gray' | 'blue' | 'green' | 'amber' | 'red' | 'purple';

/** Value of one custom field. Text/Choice/Date(ISO)/Person(user id) = string; Number = number; Money = {amount,currency}; YesNo = boolean; MultiChoice = string[]. */
export type CustomFieldValue = string | number | boolean | string[] | { amount: number; currency: string } | null;

/**
 * Release 3 additions to `ContractListItem` (and therefore `ContractDetail`). Optional in the
 * type so older payloads still parse; the release 3 API sends all of them.
 */
export interface ContractListItemV3 {
  entityId?: Id;
  departmentId?: Id;
  typeId?: Id;
  ownerId?: Id;
  delegateName?: string | null;
  responsibleName?: string | null;
  counterpartyId?: Id | null;
  startDate?: string;
  endDate?: string | null;
  totalValue?: number | null;
  /** annualValue converted to DKK (0 when null). */
  annualValueDkk?: number;
  /** totalValue converted to DKK (0 when null). */
  totalValueDkk?: number;
  autoRenew?: boolean;
  changeOfControl?: boolean | null;
  exclusivity?: string | null;
  createdAt?: string;
  signedDate?: string | null;
  hasSignedDocument?: boolean;
  tags?: TagRef[];
  watching?: boolean;
  customFields?: Record<string, CustomFieldValue>;
}

// ----- Overview analytics: GET overview/analytics?<ContractFilter>&ddThresholdDkk= -----

/** One bar or slice. `key` is the value to put into the matching filter parameter to drill down. */
export interface Bucket {
  key: string;
  label: string;
  count: number;
  annualValueDkk: number;
  totalValueDkk: number;
  /** Share of the page's annualValueDkk, 0–100, one decimal. */
  sharePct: number;
}

export interface NoticeMonth {
  /** 'yyyy-MM' */
  month: string;
  /** Undecided, notice deadline within 30 days of today (or overdue). */
  high: number;
  /** Undecided, 31–90 days. */
  medium: number;
  /** Undecided, more than 90 days. */
  low: number;
  /** A renewal decision is recorded. */
  decided: number;
  annualValueDkk: number;
}

export interface ExpiryQuarter {
  /** 'yyyy-Qn' */
  quarter: string;
  /** First and last day of the quarter (ISO), for the `dateField=end` drill-down. */
  from: string;
  to: string;
  count: number;
  autoRenew: number;
  annualValueDkk: number;
}

export interface DataQualityRow {
  entityId: Id;
  name: string;
  contracts: number;
  complete: number;
  /** complete / contracts × 100, rounded; 100 when there are no contracts. */
  score: number;
  /** Data-quality field key -> number of contracts missing it. */
  missing: Record<string, number>;
}

export interface DueDiligenceItem {
  id: Id;
  contractNo: string;
  title: string;
  counterparty: string;
  entityName: string;
  status: ContractStatus;
  annualValueDkk: number;
  totalValueDkk: number;
  /** Why it is listed, e.g. the exclusivity text or "No signed document". */
  note: string | null;
}

export interface OverviewAnalytics {
  /** ISO timestamp. */
  asOf: string;
  /** How many contracts the user can see at all (before filters), and a label for the scope. */
  scope: { label: string; visibleContracts: number };
  kpis: {
    count: number;
    annualValueDkk: number;
    totalValueDkk: number;
    /** Active, undecided, notice deadline within 30 / 90 days (0..N, overdue not included). */
    notice30: number;
    notice90: number;
    inApproval: number;
    missingData: number;
    autoRenewCount: number;
    /** autoRenewCount / count × 100 (0 when count is 0). */
    autoRenewSharePct: number;
  };
  byEntity: Bucket[];
  byDepartment: Bucket[];
  byType: Bucket[];
  /** Top 10 by annual value; key = counterparty id ('' for contracts without a master record). */
  topCounterparties: Bucket[];
  /** Share of the page's annual value that the top 10 counterparties hold, 0–100. */
  top10SharePct: number;
  /** Exactly 24 months from the current month. Overdue undecided deadlines count in the current month as `high`. */
  noticeByMonth: NoticeMonth[];
  /** Exactly 8 quarters from the current quarter, by end date; open-ended contracts are not counted. */
  expiringByQuarter: ExpiryQuarter[];
  /** One row per status in lifecycle order (Draft..Terminated), zero rows included; key = status. */
  statusFunnel: Bucket[];
  dataQuality: DataQualityRow[];
  dueDiligence: {
    thresholdDkk: number;
    changeOfControl: DueDiligenceItem[];
    exclusivity: DueDiligenceItem[];
    aboveThreshold: DueDiligenceItem[];
    missingSignedDocument: DueDiligenceItem[];
  };
}

// ----- Search: GET search/full -----

export type SearchGroup = 'contracts' | 'documents' | 'people' | 'counterparties';

export interface FullSearchQuery {
  q: string;
  /** Default: all groups. */
  group?: SearchGroup;
  /** Contracts where this person has any role (owner, delegate, responsible), or documents uploaded by them. */
  personId?: Id;
  entityId?: Id | Id[];
  departmentId?: Id;
  typeId?: Id;
  documentKind?: DocumentKind;
  /** ISO dates on the document's upload date. */
  uploadedFrom?: string;
  uploadedTo?: string;
  /** Paging applies to the group in `group`; without `group` each group returns its first `pageSize` hits. */
  page?: number;
  pageSize?: number;
}

/** A highlighted excerpt. `highlights` are [start, end) character offsets into `text`. No HTML. */
export interface Snippet {
  text: string;
  highlights: [number, number][];
}

export type ContractMatchField =
  | 'title'
  | 'contractNo'
  | 'counterparty'
  | 'owner'
  | 'delegate'
  | 'responsible'
  | 'summary'
  | 'obligation'
  | 'tag'
  | 'customField';

export interface ContractHit {
  item: ContractListItem & ContractListItemV3;
  matchedIn: ContractMatchField[];
  snippet: Snippet | null;
}

export interface DocumentHit {
  documentId: Id;
  contractId: Id;
  contractNo: string;
  contractTitle: string;
  ownerName: string;
  fileName: string;
  kind: DocumentKind;
  version: number;
  size: number;
  uploadedAt: string;
  uploadedBy: string;
  matchedIn: ('name' | 'text')[];
  snippet: Snippet | null;
}

export interface PersonHit {
  id: Id;
  name: string;
  initials: string;
  title: string | null;
  departmentNames: string[];
  /** Contracts visible to the caller where this person has a role. */
  contractCount: number;
}

export interface CounterpartyHit {
  id: Id;
  name: string;
  country: string | null;
  /** Contracts visible to the caller. */
  contractCount: number;
}

export interface SearchGroupResult<T> {
  total: number;
  items: T[];
}

export interface FullSearchResult {
  q: string;
  contracts: SearchGroupResult<ContractHit>;
  documents: SearchGroupResult<DocumentHit>;
  people: SearchGroupResult<PersonHit>;
  counterparties: SearchGroupResult<CounterpartyHit>;
}

// ----- Person and counterparty pages -----

export type ContractRole = 'Owner' | 'Delegate' | 'Responsible';

export interface PersonContract extends ContractListItem, ContractListItemV3 {
  roles: ContractRole[];
}

export interface PersonPage {
  person: {
    id: Id;
    name: string;
    initials: string;
    email: string;
    title: string | null;
    departmentNames: string[];
    isActive: boolean;
  };
  counts: { owner: number; delegate: number; responsible: number; total: number; active: number };
  /** Live contracts (Active, NoticeGiven) only. */
  annualValueDkk: number;
  /** Visible contracts where the person has any role, nearest notice first. */
  contracts: PersonContract[];
}

export type CounterpartyKind = 'Supplier' | 'Customer' | 'Partner' | 'Authority' | 'Other';
export type RecordSource = 'Manual' | 'Import' | 'PowerBi' | 'Sql' | 'Entra' | 'Seed';

/** Counterparty master record. Supersets `Supplier` (same id space as `supplierId`). */
export interface Counterparty {
  id: Id;
  name: string;
  country: string | null;
  vatNo: string | null;
  kind: CounterpartyKind;
  /** Id in the source system (ERP vendor no.), used to match on import/sync. */
  externalId: string | null;
  city: string | null;
  email: string | null;
  phone: string | null;
  isActive: boolean;
  source: RecordSource;
  /** All contracts with this counterparty (admin list: unscoped count; elsewhere: visible only). */
  contractCount: number;
  updatedAt: string;
}
export type CounterpartyInput = Pick<Counterparty, 'name' | 'country' | 'vatNo' | 'kind' | 'externalId' | 'city' | 'email' | 'phone' | 'isActive'>;

export interface CounterpartyPage {
  counterparty: Counterparty;
  summary: {
    contracts: number;
    live: number;
    annualValueDkk: number;
    totalValueDkk: number;
    autoRenewing: number;
    nextNoticeDeadline: string | null;
  };
  contracts: (ContractListItem & ContractListItemV3)[];
  /** Upcoming and overdue notice deadlines of these contracts (as `GET renewals` rows). */
  renewals: RenewalItem[];
  /** Latest version of every document on these contracts. */
  documents: DocumentHit[];
}

export interface DuplicateGroup {
  /** Why they look alike: same VAT number, same external id or same normalised name. */
  reason: 'VatNo' | 'ExternalId' | 'Name';
  items: Counterparty[];
}

export interface MergeInput {
  targetId: Id;
  sourceIds: Id[];
}
export interface MergeResult {
  target: Counterparty;
  movedContracts: number;
  /** Source records are deactivated (not deleted) and point to the target. */
  deactivated: number;
}

export interface CounterpartyQuery {
  q?: string;
  /** Default: all. */
  active?: boolean;
  kind?: CounterpartyKind;
  page?: number;
  pageSize?: number;
}

// ----- Imports (admin) -----

export type ImportKind = 'counterparties' | 'users' | 'contracts';
/** CreateOnly: duplicates are errors. CreateAndUpdate: duplicates are updated. SkipDuplicates: duplicates are skipped. */
export type ImportMode = 'CreateOnly' | 'CreateAndUpdate' | 'SkipDuplicates';
export type ImportFieldType = 'text' | 'number' | 'money' | 'date' | 'bool' | 'choice' | 'multi' | 'person' | 'entity' | 'department' | 'type' | 'counterparty' | 'email' | 'role';

export interface ImportFieldDef {
  key: string;
  label: string;
  required: boolean;
  type: ImportFieldType;
  help: string | null;
  /** Used to match rows to existing records (counterparties: externalId, vatNo, name; users: email; contracts: contractNo). */
  isKey: boolean;
  /** Example cell value. */
  example: string | null;
  /** For choice/role fields. */
  options?: string[];
}

export interface ImportOptions {
  mode: ImportMode;
  /** Contracts only: status for created records. `Active` requires a signed date or sets it to the start date (warning). */
  contractStatus?: 'Draft' | 'Active';
  /** Contracts only: create a counterparty master record when the name is unknown (else row error). */
  createMissingCounterparties?: boolean;
  /** Date format of the file's date cells when they are text: 'auto' tries yyyy-MM-dd, dd-MM-yyyy, dd.MM.yyyy, dd/MM/yyyy. */
  dateFormat?: 'auto' | 'yyyy-MM-dd' | 'dd-MM-yyyy' | 'dd.MM.yyyy' | 'dd/MM/yyyy' | 'MM/dd/yyyy';
  /** Decimal separator for number cells given as text. */
  decimalSeparator?: '.' | ',';
}

/** field key -> source column name (exact header text) or null (not mapped). */
export type ImportMapping = Record<string, string | null>;

export interface ImportSession {
  id: Id;
  kind: ImportKind;
  fileName: string;
  /** Header row of the first sheet / the CSV. */
  columns: string[];
  /** Up to 5 data rows, as text. */
  sample: string[][];
  rowCount: number;
  suggestedMapping: ImportMapping;
  /** Sessions expire after 24 hours (409 import-state afterwards). */
  expiresAt: string;
}

export type ImportRowAction = 'Create' | 'Update' | 'Skip' | 'Error';
export interface ImportRowPreview {
  /** 1-based data row number (the header is row 0). */
  rowNo: number;
  action: ImportRowAction;
  /** The matched record's display text for Update/Skip. */
  match: string | null;
  /** Parsed values by field key, as display text. */
  values: Record<string, string>;
  errors: { field: string | null; message: string }[];
  warnings: string[];
}

export interface ImportPreview {
  sessionId: Id;
  rowCount: number;
  create: number;
  update: number;
  skip: number;
  error: number;
  rows: ImportRowPreview[];
  /** Mapping problems that stop the run (a required field not mapped). */
  mappingErrors: string[];
}

export interface ImportRunInput {
  mapping: ImportMapping;
  options: ImportOptions;
}

export interface ImportResult {
  id: Id;
  kind: ImportKind;
  fileName: string;
  ranAt: string;
  ranBy: string;
  options: ImportOptions;
  created: number;
  updated: number;
  skipped: number;
  failed: number;
  /** Row errors (rows with errors are not imported; the others are). */
  errors: { rowNo: number; message: string }[];
  /** Origin: a file upload or a sync source run. */
  source: 'File' | 'Sync';
}

// ----- Sync sources (admin) -----

export type SyncSourceKind = 'PowerBi' | 'Sql';
export type ScheduleFrequency = 'Off' | 'Hourly' | 'Daily' | 'Weekly';

export interface Schedule {
  frequency: ScheduleFrequency;
  /** 'HH:mm', Europe/Copenhagen; ignored for Off and Hourly. */
  time: string;
  /** 1 = Monday … 7 = Sunday; Weekly only. */
  dayOfWeek: number | null;
}

/** A connection reference configured on the server (`Sync:Connections:<name>`); secrets never leave the server. */
export interface SyncConnection {
  name: string;
  kind: SyncSourceKind;
  description: string;
}

export interface SyncSource {
  id: Id;
  name: string;
  target: ImportKind;
  kind: SyncSourceKind;
  /** Power BI: workspace + dataset, and either a DAX query or a table name. */
  powerBi: { workspaceId: string; datasetId: string; daxQuery: string | null; table: string | null } | null;
  /** SQL: the server-side connection reference name and a SELECT query. */
  sql: { connectionName: string; query: string } | null;
  mapping: ImportMapping;
  options: ImportOptions;
  schedule: Schedule;
  isActive: boolean;
  lastRun: SyncRun | null;
  nextRunAt: string | null;
}
export type SyncSourceInput = Omit<SyncSource, 'id' | 'lastRun' | 'nextRunAt'>;

export interface SyncTestResult {
  ok: boolean;
  message: string;
  columns: string[];
  sample: string[][];
  rowCount: number | null;
}

export type RunStatus = 'Running' | 'Succeeded' | 'PartiallySucceeded' | 'Failed';
export interface SyncRun {
  id: Id;
  sourceId: Id;
  trigger: 'Manual' | 'Schedule';
  startedAt: string;
  finishedAt: string | null;
  status: RunStatus;
  read: number;
  created: number;
  updated: number;
  skipped: number;
  failed: number;
  errors: string[];
}

// ----- Custom fields and field rules -----

export type CustomFieldType = 'Text' | 'Number' | 'Money' | 'Date' | 'YesNo' | 'Choice' | 'MultiChoice' | 'Person';

export interface CustomFieldDef {
  id: Id;
  /** camelCase, unique, immutable after create; used in `customFields` and `cf.<key>`. */
  key: string;
  label: string;
  type: CustomFieldType;
  /** Empty = every contract type. */
  contractTypeIds: Id[];
  required: boolean;
  helpText: string | null;
  order: number;
  /** Choice / MultiChoice options. */
  options: string[];
  showInRegister: boolean;
  showInFilters: boolean;
  isActive: boolean;
}
export type CustomFieldInput = Omit<CustomFieldDef, 'id'>;

/** Standard fields an admin can make required per type (on top of the always-required ones). */
export const OPTIONAL_STANDARD_FIELDS = [
  'delegateId',
  'responsibleId',
  'endDate',
  'annualValue',
  'totalValue',
  'paymentTerms',
  'priceAdjustment',
  'governingLaw',
  'liabilityCap',
  'exclusivity',
  'summary',
] as const;
export type OptionalStandardField = (typeof OPTIONAL_STANDARD_FIELDS)[number];

export interface TypeFieldRules {
  typeId: Id;
  /** Extra required standard fields. `endDate` here means "open-ended is not allowed". */
  required: OptionalStandardField[];
  defaults: {
    currency: string | null;
    noticePeriodDays: number | null;
    reminderRecipient: ReminderRecipient | null;
    autoRenew: boolean | null;
    renewalMonths: number | null;
  };
}
export interface FieldRules {
  /** One row per contract type that has rules; a type without a row has none. */
  types: TypeFieldRules[];
}

// ----- Reference data admin -----

export interface LegalEntity extends LegalEntityLinkFields {
  id: Id;
  name: string;
  /** ISO 3166 alpha-2. */
  country: string;
  /** Functional currency, one of the currencies. */
  currency: string;
  isActive: boolean;
  contractCount: number;
}
export type LegalEntityInput = Pick<LegalEntity, 'name' | 'country' | 'currency' | 'isActive' | 'sharePointSiteUrl' | 'managementGroupId'>;

/** Release 3: departments and types can also be deactivated (inactive = not offered for new contracts). */
export interface NamedRefV3 extends NamedRef {
  isActive?: boolean;
}
export interface NameInputV3 extends NameInput {
  isActive?: boolean;
}

export interface Currency {
  code: string;
  name: string;
  /** DKK per 1 unit; used for every DKK figure (overview, value bands, approval value). */
  rateToDkk: number;
  isActive: boolean;
}

export interface Tag extends TagRef {
  description: string | null;
  /** Contracts with the tag (unscoped, admin) */
  usageCount: number;
}
export type TagInput = Pick<Tag, 'name' | 'color' | 'description'>;

// ----- Users and access (admin) -----

export interface AdminUser extends UserV4 {
  id: Id;
  name: string;
  email: string;
  initials: string;
  title: string | null;
  roles: Role[];
  departmentIds: Id[];
  managedEntityIds: Id[];
  isGroupManagement: boolean;
  isActive: boolean;
  source: RecordSource;
  /** Field names that come from Entra ID / a sync and are read-only here (e.g. name, email, title, isActive). */
  syncedFields: string[];
  externalId: string | null;
  lastSignInAt: string | null;
  /** Contracts where the user has any role (unscoped). */
  contractCount: number;
}
export type AdminUserInput = Pick<AdminUser, 'name' | 'email' | 'title' | 'roles' | 'departmentIds' | 'managedEntityIds' | 'isGroupManagement' | 'isActive' | 'entityId' | 'canSendForSignature' | 'signatureEntityIds'>;

export type AccessRule = 'Department' | 'EntityManagement' | 'GroupManagement' | 'OnContract' | 'Admin' | 'CurrentApprover';
export interface AccessReason {
  rule: AccessRule;
  /** e.g. "Member of Purchasing", "Manages DAFA Sverige AB", "Owner". */
  label: string;
}
export interface EffectiveAccess {
  user: AdminUser;
  total: number;
  /** How many visible contracts each rule grants (a contract can count under several rules). */
  rules: { rule: AccessRule; label: string; count: number }[];
  page: number;
  pageSize: number;
  contracts: { item: ContractListItem & ContractListItemV3; reasons: AccessReason[] }[];
}

export interface ReassignInput {
  fromUserId: Id;
  toUserId: Id;
  roles: ContractRole[];
  /** null = every contract where `fromUserId` has one of `roles`. */
  contractIds: Id[] | null;
  /** Also move Expired/Terminated contracts. */
  includeEnded: boolean;
  /** Also move open obligations where `fromUserId` is responsible (on the same contracts). */
  includeObligations: boolean;
}
export interface ReassignPreviewRow {
  contractId: Id;
  contractNo: string;
  title: string;
  status: ContractStatus;
  roles: ContractRole[];
  /** Set when the row will be skipped, e.g. "Bo is already the delegate" or "In approval". */
  skipReason: string | null;
}
export interface ReassignPreview {
  rows: ReassignPreviewRow[];
  contracts: number;
  obligations: number;
}
export interface ReassignResult {
  updated: number;
  obligationsMoved: number;
  skipped: { contractId: Id; reason: string }[];
}

// ----- Notifications -----

export type NotificationEvent =
  | 'NoticeReminder'
  | 'Escalation'
  | 'ObligationDue'
  | 'AnnualReview'
  | 'ApprovalRequested'
  | 'ApprovalDecided'
  | 'ApprovalDelegated'
  | 'RenewalDecision'
  | 'Mention'
  | 'WatchedContractChanged'
  | 'DataCompletionRequested'
  | 'ImportFinished'
  | 'SyncFailed';
export type NotificationChannel = 'Email' | 'Teams' | 'InApp';
export type RecipientRule = 'ReminderRecipient' | 'Owner' | 'Delegate' | 'Responsible' | 'Watchers' | 'Approver' | 'Mentioned' | 'Admins';
export type DigestFrequency = 'Off' | 'Daily' | 'Weekly';

export interface NotificationEventSetting {
  event: NotificationEvent;
  label: string;
  enabled: boolean;
  channels: NotificationChannel[];
  recipients: RecipientRule[];
  /** Extra fixed recipients (user ids). */
  extraRecipientIds: Id[];
  subject: string;
  body: string;
  /** Read-only: the `{{placeholders}}` this event fills, e.g. contract.title, contract.url, deadline, days, actor, comment. */
  placeholders: string[];
}

export interface TypeReminderSchedule {
  typeId: Id;
  noticeDays: number[];
  escalationDays: number;
}

export interface NotificationSettings {
  events: NotificationEventSetting[];
  digestDefault: DigestFrequency;
  /** Per contract type; a type without a row uses the global reminder settings. */
  reminderSchedules: TypeReminderSchedule[];
}

export interface TemplatePreviewInput {
  event: NotificationEvent;
  subject: string;
  body: string;
}
export interface TemplatePreview {
  subject: string;
  body: string;
  /** Placeholders in the input that this event does not know. */
  unknown: string[];
}

export interface AppNotification {
  id: Id;
  event: NotificationEvent;
  title: string;
  body: string;
  contractId: Id | null;
  /** App path to open, e.g. "/contracts/<id>#comments". */
  link: string | null;
  actorName: string | null;
  createdAt: string;
  readAt: string | null;
}
export interface NotificationPage extends Paged<AppNotification> {
  unread: number;
}

// ----- Jobs (admin) -----

export type JobKey = 'Reminders' | 'Obligations' | 'ContractEnd' | 'Digest' | 'CounterpartySync' | 'DirectorySync' | 'UploadCleanup' | 'DataRoomCleanup';
export interface JobInfo {
  key: JobKey;
  label: string;
  description: string;
  schedule: Schedule;
  lastRun: { at: string; status: RunStatus; message: string | null; durationMs: number } | null;
  nextRunAt: string | null;
}

// ----- General settings -----

export type LandingPage = 'home' | 'contracts' | 'renewals' | 'approvals' | 'overview';
export type DateFormat = 'd MMM yyyy' | 'dd-MM-yyyy' | 'dd.MM.yyyy' | 'yyyy-MM-dd' | 'MM/dd/yyyy';
export type NumberFormat = 'en-GB' | 'da-DK' | 'de-DE' | 'en-US';
/** Standard data-quality checks; custom fields are added as `cf.<key>`. */
export const DATA_QUALITY_FIELDS = [
  'noticePeriodDays',
  'contractValue',
  'signedDate',
  'changeOfControl',
  'signedDocument',
  'endDate',
  'responsibleId',
  'paymentTerms',
  'governingLaw',
] as const;
export type DataQualityField = (typeof DATA_QUALITY_FIELDS)[number] | `cf.${string}`;

export interface AppSettings extends AppSettingsV4 {
  appName: string;
  dateFormat: DateFormat;
  numberFormat: NumberFormat;
  /** Landing page per role; a user with several roles gets the first match in Admin, Approver, ContractOwner, Reader order. */
  landingPageByRole: Record<Role, LandingPage>;
  features: { aiSuggestions: boolean; templates: boolean; eSign: boolean };
  sessionTimeoutMinutes: number;
  /** Which fields count toward `missingData` and the data-quality score. */
  dataQualityFields: DataQualityField[];
  /** Overview due-diligence "above threshold" default, DKK. */
  ddThresholdDkk: number;
}

// ----- Saved views -----

export type ViewScope = 'Personal' | 'Shared';
/** Register column keys: standard keys below, or `cf.<key>` for custom fields shown in the register. */
export const REGISTER_COLUMNS = [
  'contract',
  'contractNo',
  'counterparty',
  'type',
  'entity',
  'department',
  'owner',
  'delegate',
  'responsible',
  'notice',
  'start',
  'end',
  'annualValue',
  'totalValue',
  'status',
  'tags',
  'autoRenew',
  'changeOfControl',
] as const;
export type RegisterColumn = (typeof REGISTER_COLUMNS)[number] | `cf.${string}`;

export interface SavedView {
  id: Id;
  name: string;
  scope: ViewScope;
  ownerId: Id;
  ownerName: string;
  columns: RegisterColumn[];
  /** The register's query, as sent to `GET contracts` (minus page). */
  query: Omit<ContractQuery, 'sort' | 'page'> & { sort?: string };
  /** Personal default view (one per user). */
  isDefault: boolean;
  /** The caller may change or delete it (owner, or admin for shared views). */
  canEdit: boolean;
  updatedAt: string;
}
export type SavedViewInput = Pick<SavedView, 'name' | 'scope' | 'columns' | 'query' | 'isDefault'>;

// ----- Bulk actions, tags, watch, comments -----

export type BulkAction = 'AddTag' | 'RemoveTag' | 'Reassign' | 'RequestDataCompletion';
export interface BulkInput {
  action: BulkAction;
  contractIds: Id[];
  tagId?: Id;
  /** Reassign: which role to move and to whom. */
  role?: ContractRole;
  toUserId?: Id;
  /** RequestDataCompletion: optional note added to the request. */
  message?: string;
}
export interface BulkResult {
  action: BulkAction;
  requested: number;
  succeeded: number;
  failed: { contractId: Id; title: string; reason: string }[];
}

export interface Comment {
  id: Id;
  contractId: Id;
  authorId: Id;
  authorName: string;
  authorInitials: string;
  /** Plain text. Mentions appear as "@Full Name" and are listed in `mentions`. */
  body: string;
  mentions: { userId: Id; name: string }[];
  createdAt: string;
  editedAt: string | null;
  canEdit: boolean;
  canDelete: boolean;
}
export interface CommentInput {
  body: string;
  mentionIds: Id[];
}

/** Release 3 additions to `ContractDetail` (optional so older payloads still parse). */
export interface ContractDetailV3 {
  watching?: boolean;
  watcherCount?: number;
  commentCount?: number;
  tags?: TagRef[];
  customFields?: Record<string, CustomFieldValue>;
  /** The caller may add/remove tags (= canEdit). */
  canTag?: boolean;
}

/** Release 3 additions to the create/update body. Omitted = unchanged on update, empty on create. */
export interface ContractInputV3 {
  customFields?: Record<string, CustomFieldValue>;
  tagIds?: Id[];
}

// ----- Version comparison -----

export interface ContractVersion {
  /** 'current', or the audit event id after which this version was in force. */
  id: string;
  at: string;
  who: string;
  label: string;
  changedFields: string[];
}
export interface ComparedField {
  field: string;
  label: string;
  from: string | null;
  to: string | null;
  changed: boolean;
}
export interface ContractComparison {
  from: ContractVersion;
  to: ContractVersion;
  fields: ComparedField[];
}

// ----- Data room -----

export interface DataRoomInput {
  filter: ContractFilter;
  documents: 'All' | 'MainOnly' | 'None';
  versions: 'Latest' | 'All';
  /** Shown in the file name and the index. */
  name: string;
}
export interface DataRoomExport {
  id: Id;
  name: string;
  status: 'Queued' | 'Running' | 'Ready' | 'Failed';
  contractCount: number;
  documentCount: number;
  sizeBytes: number | null;
  createdAt: string;
  createdBy: string;
  fileName: string;
  error: string | null;
  /** Ready exports are deleted after this time. */
  expiresAt: string | null;
}

// ----- Personal settings -----

export type ThemePreference = 'light' | 'dark' | 'system';

export interface UserPreferences {
  /** Release 4 (item I). Default 'light'. */
  theme?: ThemePreference;
  digest: DigestFrequency;
  /** Per event; events the user is not a recipient of by default may be listed too. */
  events: { event: NotificationEvent; label: string; email: boolean; teams: boolean; inApp: boolean }[];
  /** Entity scope applied when the app opens without `?entity=`. */
  defaultEntityId: Id | null;
  /** null = role default from General settings. */
  landingPage: LandingPage | null;
  /** Start watching contracts I create or am added to. */
  autoWatch: boolean;
}

/** `GET app-config` for every signed-in user: what forms and pages need from the admin settings. */
export interface AppConfig extends AppConfigV4 {
  appName: string;
  dateFormat: DateFormat;
  numberFormat: NumberFormat;
  features: AppSettings['features'];
  sessionTimeoutMinutes: number;
  /** Resolved for the caller (role default overridden by preferences). */
  landingPage: LandingPage;
  defaultEntityId: Id | null;
  dataQualityFields: DataQualityField[];
  ddThresholdDkk: number;
  customFields: CustomFieldDef[];
  fieldRules: FieldRules;
  tags: TagRef[];
  /** DKK rates of the active currencies. */
  rates: Record<string, number>;
  /** Shared data-room export allowed for the caller (Admin, group or entity management). */
  canExportDataRoom: boolean;
}

// =====================================================================================
// Release 4 (docs/CONTRACT_V4.md)
// =====================================================================================

/** Contract filter additions for register header filters (CONTRACT_V4 §2). */
export interface ContractFilterV4 {
  title?: string;
  contractNo?: string;
  counterpartyName?: string;
  delegateId?: Id | Id[];
  responsibleId?: Id | Id[];
  currency?: string | string[];
  /** DKK, inclusive. */
  annualValueMin?: number;
  annualValueMax?: number;
  totalValueMin?: number;
  totalValueMax?: number;
  noticeFrom?: string;
  noticeTo?: string;
  endFrom?: string;
  endTo?: string;
  startFrom?: string;
  startTo?: string;
  signedFrom?: string;
  signedTo?: string;
  createdFrom?: string;
  createdTo?: string;
}

export type DistinctColumn = 'counterparty' | 'type' | 'entity' | 'department' | 'owner' | 'delegate' | 'responsible' | 'status' | 'currency' | 'tags' | `cf.${string}`;
export interface DistinctValue {
  value: string;
  label: string;
  count: number;
}
export interface DistinctValues {
  column: DistinctColumn;
  values: DistinctValue[];
  truncated: boolean;
}

/** Type-ahead result for person pickers (`GET people?q=`). */
export interface PersonOption {
  id: Id;
  name: string;
  initials: string;
  email: string;
  title: string | null;
  departmentName: string | null;
  entityName: string | null;
  isActive: boolean;
}
export interface PeopleQuery {
  q?: string;
  limit?: number;
  role?: Exclude<Role, 'Reader'>;
  includeInactive?: boolean;
}

export type CounterpartyCreation = 'AdminOnly' | 'ContractOwners' | 'AllUsers';
export type CounterpartyCreateInput = Pick<Counterparty, 'name' | 'country' | 'vatNo' | 'kind' | 'city' | 'email' | 'phone'>;

// ----- Template categories (E) -----
export type TemplateLanguage = 'da' | 'en' | 'sv' | 'de' | 'pl' | 'it' | 'zh';
export interface TemplateCategory {
  id: Id;
  name: string;
  description: string;
  order: number;
  isActive: boolean;
  templateCount: number;
}
export type TemplateCategoryInput = Pick<TemplateCategory, 'name' | 'description' | 'isActive'>;
/** Fields release 4 adds to `Template` and `TemplateInput`. */
export interface TemplateV4 {
  categoryId?: Id | null;
  categoryName?: string | null;
  /** Empty = every contract type. */
  typeIds?: Id[];
  language?: TemplateLanguage | null;
  isActive?: boolean;
  updatedAt?: string;
}
export interface TemplatePlaceholder {
  name: string;
  label: string;
  sample: string;
}
export interface TemplatePreviewInfo {
  id: Id;
  name: string;
  description: string;
  placeholders: TemplatePlaceholder[];
  excerpt: string;
}

// ----- Branding (F) -----
export type LogoVariant = 'full' | 'compact' | 'dark';
export interface Branding {
  logoUrl: string | null;
  compactLogoUrl: string | null;
  /** Optional logo for the dark theme (item I); null = use `logoUrl`. */
  darkLogoUrl?: string | null;
  version: string;
}

// ----- SharePoint / Entra link (G) -----
export type LinkState = 'NotLinked' | 'Linked' | 'Error';
export interface LinkStatus {
  state: LinkState;
  checkedAt: string | null;
  message: string | null;
}
export interface LegalEntityLinkFields {
  sharePointSiteUrl?: string | null;
  sharePointSiteId?: string | null;
  managementGroupId?: string | null;
  managementGroupName?: string | null;
  link?: LinkStatus;
}
export interface DepartmentAdmin extends NamedRefV3 {
  documentLibrary?: string | null;
  groupId?: string | null;
  groupName?: string | null;
  link?: LinkStatus;
}
export interface DepartmentInputV4 extends NameInputV3 {
  documentLibrary?: string | null;
  groupId?: string | null;
}
export interface LinkTestResult {
  link: LinkStatus;
  siteId: string | null;
  groupName: string | null;
  details: string[];
}
export interface AccessMappingCell {
  entityId: Id;
  departmentId: Id;
  siteUrl: string | null;
  library: string | null;
  entityGroupName: string | null;
  departmentGroupName: string | null;
  contractCount: number;
  warnings: string[];
}
export interface AccessMapping {
  mode: 'Local' | 'SharePoint';
  entities: NamedRef[];
  departments: NamedRef[];
  cells: AccessMappingCell[];
  warnings: string[];
}

// ----- E-signature (H) -----
export type ESignProvider = 'None' | 'Simulated' | 'DocuSign' | 'Penneo';
export type SigningOrder = 'Sequential' | 'Parallel';
export interface AuthorisedSignatories {
  entityId: Id;
  userIds: Id[];
  externalNames: string[];
}
export interface ESignSettings {
  provider: ESignProvider;
  defaultMessage: string;
  defaultOrder: SigningOrder;
  reminderDays: number[];
  expiryDays: number;
  requireApproved: boolean;
  authorisedSignatories: AuthorisedSignatories[];
  restrictToAuthorised: boolean;
  providerAccount: string | null;
}
export type SignatureRequestStatus = 'Draft' | 'Sent' | 'Completed' | 'Declined' | 'Expired' | 'Cancelled';
export type SignerStatus = 'Waiting' | 'Sent' | 'Viewed' | 'Signed' | 'Declined' | 'Expired';
export interface Signer {
  id: Id;
  kind: 'Internal' | 'External';
  userId: Id | null;
  name: string;
  email: string;
  order: number;
  status: SignerStatus;
  at: string | null;
  declineReason: string | null;
}
export interface SignatureRequest {
  id: Id;
  contractId: Id;
  status: SignatureRequestStatus;
  provider: ESignProvider;
  order: SigningOrder;
  message: string;
  documentIds: Id[];
  createdBy: string;
  /** The sender's user id (the real API sends it; older answers may not). */
  createdById?: Id;
  createdAt: string;
  sentAt: string | null;
  completedAt: string | null;
  expiresAt: string | null;
  signers: Signer[];
  signedDocumentId: Id | null;
}
export interface SignerInput {
  kind: 'Internal' | 'External';
  userId?: Id | null;
  name?: string;
  email?: string;
  order: number;
}
export interface SignatureRequestInput {
  documentIds: Id[];
  signers: SignerInput[];
  order: SigningOrder;
  message: string;
}
export interface SimulateSignatureInput {
  signerId: Id;
  action: 'View' | 'Sign' | 'Decline';
  reason?: string;
}
export interface SignaturePermissions {
  canSend: boolean;
  reason: string | null;
  permittedUsers: PersonOption[];
}
/** Fields release 4 adds to users (`AdminUser`, `AdminUserInput`, `Me`, `LookupUser`). */
export interface UserV4 {
  entityId?: Id | null;
  canSendForSignature?: boolean;
  signatureEntityIds?: Id[];
}
/** Fields release 4 adds to `AppSettings`. */
export interface AppSettingsV4 {
  counterpartyCreation?: CounterpartyCreation;
}
/** Fields release 4 adds to `AppConfig`. */
export interface AppConfigV4 {
  branding?: Branding;
  storageMode?: 'Local' | 'SharePoint';
  /** `requireApproved` false: drafts and active contracts can be sent for signature too. Absent on older APIs. */
  eSign?: { provider: ESignProvider; enabled: boolean; requireApproved?: boolean };
  canCreateCounterparty?: boolean;
}
/** Fields release 4 adds to `ContractDetail`. */
export interface ContractDetailV4 {
  signature?: SignatureRequest | null;
  canSendForSignature?: boolean;
}

// ----- Storage (CONTRACT_V4 §9a) -----
export const STORAGE_PROBLEMS = {
  unavailable: 'urn:dafa:problem:storage-unavailable',
  fileMissing: 'urn:dafa:problem:file-missing',
  notConfigured: 'urn:dafa:problem:storage-not-configured',
} as const;
export type StorageState = 'Healthy' | 'Degraded' | 'Down' | 'NotConfigured';
export interface StorageHealth {
  mode: 'Local' | 'SharePoint';
  state: StorageState;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  lastError: string | null;
  checkedAt: string;
  recentFailures: number;
  details: string[];
}
export type ReconciliationKind = 'MissingFile' | 'Orphan' | 'Mismatch' | 'MisplacedFolder';
export interface ReconciliationItem {
  kind: ReconciliationKind;
  contractId: Id | null;
  contractNo: string | null;
  documentId: Id | null;
  path: string;
  detail: string;
}
export interface ReconciliationReport {
  id: Id;
  startedAt: string;
  finishedAt: string | null;
  status: 'Running' | 'Completed' | 'Failed';
  error: string | null;
  checkedDocuments: number;
  checkedFolders: number;
  items: ReconciliationItem[];
}
