import type {
  ApprovalDecisionInput,
  ApprovalPreview,
  ApprovalPreviewQuery,
  ApprovalRule,
  ApprovalRuleInput,
  AuditEvent,
  AuditQuery,
  ContractDetail,
  ContractInput,
  ContractListItem,
  ContractQuery,
  DevConfig,
  DevUser,
  DocumentKind,
  ExtractResult,
  FileDownload,
  FromTemplateInput,
  FromTemplateResult,
  GetContractOptions,
  HolidayCover,
  HolidayCoverInput,
  HomeData,
  Id,
  JobRunResult,
  LinkInput,
  Lookups,
  Me,
  MyApproval,
  NamedRef,
  NameInput,
  Obligation,
  ObligationInput,
  OutboxMessage,
  OverviewData,
  Paged,
  ProblemDetails,
  ReminderSettings,
  RenewalDecisionInput,
  RenewalItem,
  RetentionRule,
  RetentionRuleInput,
  SearchResult,
  SignInput,
  Supplier,
  Template,
  TemplateInput,
} from './types';

/** Error thrown by both API implementations; carries the RFC 7807 problem. */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;

  constructor(problem: ProblemDetails) {
    super(problem.detail ?? problem.title);
    this.name = 'ApiError';
    this.status = problem.status;
    this.problem = problem;
  }
}

/**
 * The one interface the UI talks to. `client.ts` implements it over fetch against the
 * .NET API; `mock/mockApi.ts` implements it in the browser. One method per endpoint in
 * docs/BUILD_BRIEF.md section 5.
 */
export interface Api {
  // Session and reference data
  getMe(): Promise<Me>;
  getLookups(): Promise<Lookups>;
  getSuppliers(q?: string): Promise<Supplier[]>;
  getHome(): Promise<HomeData>;
  search(q: string): Promise<SearchResult[]>;
  /** Read-only reminder settings for every signed-in user (`GET reminder-settings`). */
  getReminderSettings(): Promise<ReminderSettings>;

  // Contracts
  getContracts(query?: ContractQuery): Promise<Paged<ContractListItem>>;
  exportContracts(query?: ContractQuery): Promise<FileDownload>;
  /** `peek` reads the contract without recording it as recently opened. */
  getContract(id: Id, options?: GetContractOptions): Promise<ContractDetail>;
  createContract(input: ContractInput): Promise<ContractDetail>;
  updateContract(id: Id, input: ContractInput): Promise<ContractDetail>;
  submitContract(id: Id): Promise<ContractDetail>;
  signContract(id: Id, input: SignInput): Promise<ContractDetail>;
  decideRenewal(id: Id, input: RenewalDecisionInput): Promise<ContractDetail>;

  // Documents
  uploadDocument(contractId: Id, file: File, kind: DocumentKind): Promise<ContractDetail>;
  getDocumentContent(documentId: Id): Promise<FileDownload>;

  // Obligations
  getObligations(contractId: Id): Promise<Obligation[]>;
  createObligation(contractId: Id, input: ObligationInput): Promise<Obligation>;
  updateObligation(id: Id, input: ObligationInput): Promise<Obligation>;
  deleteObligation(id: Id): Promise<void>;
  completeObligation(id: Id): Promise<Obligation>;

  // Links
  createLink(contractId: Id, input: LinkInput): Promise<ContractDetail>;
  deleteLink(id: Id): Promise<void>;

  // Intake
  extract(file: File): Promise<ExtractResult>;
  getTemplates(): Promise<Template[]>;
  fromTemplate(input: FromTemplateInput): Promise<FromTemplateResult>;

  // Approvals
  getMyApprovals(): Promise<MyApproval[]>;
  decideApprovalStep(stepId: Id, input: ApprovalDecisionInput): Promise<void>;
  delegateApprovalStep(stepId: Id, userId: Id): Promise<void>;
  previewApproval(query: ApprovalPreviewQuery): Promise<ApprovalPreview>;

  // Renewals and overview
  getRenewals(months?: number): Promise<RenewalItem[]>;
  getOverview(): Promise<OverviewData>;

  // Admin (role Admin)
  admin: {
    getApprovalRules(): Promise<ApprovalRule[]>;
    createApprovalRule(input: ApprovalRuleInput): Promise<ApprovalRule>;
    updateApprovalRule(id: Id, input: ApprovalRuleInput): Promise<ApprovalRule>;
    deleteApprovalRule(id: Id): Promise<void>;

    getRetentionRules(): Promise<RetentionRule[]>;
    createRetentionRule(input: RetentionRuleInput): Promise<RetentionRule>;
    updateRetentionRule(id: Id, input: RetentionRuleInput): Promise<RetentionRule>;
    deleteRetentionRule(id: Id): Promise<void>;

    getDepartments(): Promise<NamedRef[]>;
    createDepartment(input: NameInput): Promise<NamedRef>;
    updateDepartment(id: Id, input: NameInput): Promise<NamedRef>;
    deleteDepartment(id: Id): Promise<void>;

    getContractTypes(): Promise<NamedRef[]>;
    createContractType(input: NameInput): Promise<NamedRef>;
    updateContractType(id: Id, input: NameInput): Promise<NamedRef>;
    deleteContractType(id: Id): Promise<void>;

    getTemplates(): Promise<Template[]>;
    /** Multipart: the Word file plus the name, description and type. */
    createTemplate(input: TemplateInput, file: File): Promise<Template>;
    getTemplateContent(id: Id): Promise<FileDownload>;
    updateTemplate(id: Id, input: TemplateInput): Promise<Template>;
    deleteTemplate(id: Id): Promise<void>;

    getReminderSettings(): Promise<ReminderSettings>;
    updateReminderSettings(input: ReminderSettings): Promise<ReminderSettings>;

    getAudit(query?: AuditQuery): Promise<Paged<AuditEvent>>;
    getOutbox(): Promise<OutboxMessage[]>;
    runJobs(): Promise<JobRunResult>;

    getHolidayCover(): Promise<HolidayCover[]>;
    /** Replaces the whole list. */
    updateHolidayCover(input: HolidayCoverInput[]): Promise<HolidayCover[]>;
  };

  // Dev mode sign-in
  /** Anonymous. Tells the app whether to show the demo-user picker or redirect to Entra sign-in. */
  getDevConfig(): Promise<DevConfig>;
  /** The access code (when the site requires one) is sent as header `X-Dev-Access-Code`. */
  getDevUsers(accessCode?: string): Promise<DevUser[]>;
  /** The access code is sent as `accessCode` in the body. */
  devLogin(userId: Id, accessCode?: string): Promise<Me>;
  logout(): Promise<void>;
}
