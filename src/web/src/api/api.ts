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
import type {
  AccessMapping,
  Branding,
  ContractFilter as ContractFilterR4,
  CounterpartyCreateInput,
  DepartmentAdmin,
  DepartmentInputV4,
  DistinctColumn,
  DistinctValues,
  ESignSettings,
  LinkTestResult,
  LogoVariant,
  PeopleQuery,
  PersonOption,
  SignaturePermissions,
  SignatureRequest,
  SignatureRequestInput,
  SimulateSignatureInput,
  TemplateCategory,
  TemplateCategoryInput,
  TemplatePreviewInfo,
  StorageHealth,
  ReconciliationReport,
} from './types';
import type {
  AdminUser,
  AdminUserInput,
  AppConfig,
  AppSettings,
  BulkInput,
  BulkResult,
  Comment,
  CommentInput,
  ContractComparison,
  ContractFilter,
  ContractVersion,
  Counterparty,
  CounterpartyInput,
  CounterpartyPage,
  CounterpartyQuery,
  Currency,
  CustomFieldDef,
  CustomFieldInput,
  DataRoomExport,
  DataRoomInput,
  DuplicateGroup,
  EffectiveAccess,
  FieldRules,
  FullSearchQuery,
  FullSearchResult,
  ImportFieldDef,
  ImportKind,
  ImportPreview,
  ImportResult,
  ImportRunInput,
  ImportSession,
  JobInfo,
  JobKey,
  LegalEntity,
  LegalEntityInput,
  MergeInput,
  MergeResult,
  NameInputV3,
  NamedRefV3,
  NotificationPage,
  NotificationSettings,
  OverviewAnalytics,
  PersonPage,
  ReassignInput,
  ReassignPreview,
  ReassignResult,
  SavedView,
  SavedViewInput,
  Schedule,
  SyncConnection,
  SyncRun,
  SyncSource,
  SyncSourceInput,
  SyncTestResult,
  Tag,
  TagInput,
  TagRef,
  TemplatePreview,
  TemplatePreviewInput,
  UserPreferences,
} from './types';

function firstFieldMessage(problem: ProblemDetails): string | undefined {
  for (const v of Object.values(problem.errors ?? {})) {
    const m = (Array.isArray(v) ? v : [v]).find((x) => typeof x === 'string' && x.trim() !== '');
    if (m) return m;
  }
  return undefined;
}

/** Error thrown by both API implementations; carries the RFC 7807 problem. */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;

  constructor(problem: ProblemDetails) {
    // A 400 from the API has a generic `detail`; the sentence for the user is the first field error.
    super((problem.status === 400 ? firstFieldMessage(problem) : undefined) ?? problem.detail ?? problem.title);
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
/** Top-bar entity scope for a request: the ids are sent as repeated `entityId`. */
export type Scope = Id[];

export interface Api {
  // Session and reference data
  getMe(): Promise<Me>;
  getLookups(): Promise<Lookups>;
  getSuppliers(q?: string): Promise<Supplier[]>;
  /** Release 4: `scope` = the top-bar entity scope, sent as repeated `entityId`. */
  getHome(scope?: Scope): Promise<HomeData>;
  search(q: string, scope?: Scope): Promise<SearchResult[]>;
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
  getMyApprovals(scope?: Scope): Promise<MyApproval[]>;
  decideApprovalStep(stepId: Id, input: ApprovalDecisionInput): Promise<void>;
  delegateApprovalStep(stepId: Id, userId: Id): Promise<void>;
  previewApproval(query: ApprovalPreviewQuery): Promise<ApprovalPreview>;

  // Renewals and overview
  getRenewals(months?: number, scope?: Scope): Promise<RenewalItem[]>;
  /** Release 3: every signed-in user, scoped to what they can see (no 403 any more). */
  getOverview(scope?: Scope): Promise<OverviewData>;

  // ----- Release 3 (docs/CONTRACT_V3.md) -----
  /** `GET app-config`: settings, custom fields, field rules, tags and rates every page needs. */
  getAppConfig(): Promise<AppConfig>;
  /** `GET overview/analytics?<filter>&ddThresholdDkk=` */
  getOverviewAnalytics(filter: ContractFilter, ddThresholdDkk?: number): Promise<OverviewAnalytics>;
  /** `GET search/full` */
  searchFull(query: FullSearchQuery): Promise<FullSearchResult>;
  /** `GET people/{id}` */
  getPerson(userId: Id): Promise<PersonPage>;
  /** `GET counterparties/{id}` */
  getCounterparty(id: Id): Promise<CounterpartyPage>;
  /** `GET counterparties?q=` (active master records, any signed-in user; supersedes `suppliers`). */
  getCounterparties(q?: string): Promise<Counterparty[]>;
  /** `GET tags` */
  getTags(): Promise<TagRef[]>;
  /** `PUT contracts/{id}/tags {tagIds}` (replaces the contract's tags). */
  setContractTags(contractId: Id, tagIds: Id[]): Promise<ContractDetail>;
  /** `PUT contracts/{id}/watch` / `DELETE contracts/{id}/watch` */
  watchContract(contractId: Id): Promise<void>;
  unwatchContract(contractId: Id): Promise<void>;
  /** `GET watching` */
  getWatching(): Promise<ContractListItem[]>;
  /** `GET contracts/{id}/comments` (oldest first) */
  getComments(contractId: Id): Promise<Comment[]>;
  addComment(contractId: Id, input: CommentInput): Promise<Comment>;
  updateComment(id: Id, input: CommentInput): Promise<Comment>;
  deleteComment(id: Id): Promise<void>;
  /** `GET notifications?unreadOnly=&page=&pageSize=` */
  getNotifications(query?: { unreadOnly?: boolean; page?: number; pageSize?: number }): Promise<NotificationPage>;
  /** `GET notifications/unread-count` */
  getUnreadCount(): Promise<{ count: number }>;
  markNotificationRead(id: Id): Promise<void>;
  markAllNotificationsRead(): Promise<void>;
  /** `GET|PUT me/preferences` */
  getPreferences(): Promise<UserPreferences>;
  updatePreferences(input: UserPreferences): Promise<UserPreferences>;
  /** `GET contracts/{id}/versions` (newest first; the first is 'current'). */
  getContractVersions(contractId: Id): Promise<ContractVersion[]>;
  /** `GET contracts/{id}/compare?from=&to=` */
  compareContract(contractId: Id, from: string, to: string): Promise<ContractComparison>;
  /** `POST contracts/bulk` */
  bulk(input: BulkInput): Promise<BulkResult>;
  /** `GET views` (mine + shared), `POST views`, `PUT views/{id}`, `DELETE views/{id}` */
  getViews(): Promise<SavedView[]>;
  createView(input: SavedViewInput): Promise<SavedView>;
  updateView(id: Id, input: SavedViewInput): Promise<SavedView>;
  deleteView(id: Id): Promise<void>;
  /** `POST data-room/exports`, `GET data-room/exports`, `GET data-room/exports/{id}`, `GET data-room/exports/{id}/content` */
  createDataRoomExport(input: DataRoomInput): Promise<DataRoomExport>;
  getDataRoomExports(): Promise<DataRoomExport[]>;
  getDataRoomExport(id: Id): Promise<DataRoomExport>;
  getDataRoomContent(id: Id): Promise<FileDownload>;

  // ----- Release 4 (docs/CONTRACT_V4.md) -----
  /** `GET contracts/distinct?column=&q=&limit=&<ContractFilter>` */
  getDistinct(column: DistinctColumn, filter: ContractFilterR4, q?: string, limit?: number): Promise<DistinctValues>;
  /** `GET people?q=&limit=&role=&includeInactive=` */
  searchPeople(query: PeopleQuery): Promise<PersonOption[]>;
  /** `POST counterparties` (non-admin, per `AppSettings.counterpartyCreation`). */
  createCounterpartyQuick(input: CounterpartyCreateInput): Promise<Counterparty>;
  /** `GET template-categories` (active, any user) */
  getTemplateCategories(): Promise<TemplateCategory[]>;
  /** `GET templates/{id}/preview` */
  getTemplatePreview(id: Id): Promise<TemplatePreviewInfo>;
  /** `GET branding` (anonymous) */
  getBranding(): Promise<Branding>;
  /** Absolute URL for a relative asset path returned by the API (logo). */
  assetUrl(path: string): string;
  getSignaturePermissions(contractId: Id): Promise<SignaturePermissions>;
  getSignatureRequests(contractId: Id): Promise<SignatureRequest[]>;
  createSignatureRequest(contractId: Id, input: SignatureRequestInput): Promise<SignatureRequest>;
  resendSignatureRequest(id: Id): Promise<SignatureRequest>;
  cancelSignatureRequest(id: Id): Promise<SignatureRequest>;
  /** Simulated provider only. */
  simulateSignature(id: Id, input: SimulateSignatureInput): Promise<SignatureRequest>;

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

    // ----- Release 3 -----
    /** Release 3: `isActive` in the body deactivates instead of deleting. */
    updateDepartmentV3(id: Id, input: NameInputV3): Promise<NamedRefV3>;
    updateContractTypeV3(id: Id, input: NameInputV3): Promise<NamedRefV3>;

    getCounterparties(query?: CounterpartyQuery): Promise<Paged<Counterparty>>;
    createCounterparty(input: CounterpartyInput): Promise<Counterparty>;
    updateCounterparty(id: Id, input: CounterpartyInput): Promise<Counterparty>;
    getCounterpartyDuplicates(): Promise<DuplicateGroup[]>;
    mergeCounterparties(input: MergeInput): Promise<MergeResult>;

    getImportFields(kind: ImportKind): Promise<ImportFieldDef[]>;
    /** Multipart `file` (.csv, .xlsx; max 10 MB, 5000 rows). */
    uploadImport(kind: ImportKind, file: File): Promise<ImportSession>;
    previewImport(sessionId: Id, input: ImportRunInput): Promise<ImportPreview>;
    runImport(sessionId: Id, input: ImportRunInput): Promise<ImportResult>;
    getImportHistory(kind?: ImportKind): Promise<ImportResult[]>;
    /** CSV with the header row of the import fields and one example row. */
    getImportTemplate(kind: ImportKind): Promise<FileDownload>;

    getSyncConnections(): Promise<SyncConnection[]>;
    getSyncSources(): Promise<SyncSource[]>;
    createSyncSource(input: SyncSourceInput): Promise<SyncSource>;
    updateSyncSource(id: Id, input: SyncSourceInput): Promise<SyncSource>;
    deleteSyncSource(id: Id): Promise<void>;
    /** Tests an unsaved definition: connects, reads up to 5 rows. Never writes. */
    testSyncSource(input: SyncSourceInput): Promise<SyncTestResult>;
    runSyncSource(id: Id): Promise<SyncRun>;
    getSyncRuns(id: Id): Promise<SyncRun[]>;

    getCustomFields(): Promise<CustomFieldDef[]>;
    createCustomField(input: CustomFieldInput): Promise<CustomFieldDef>;
    updateCustomField(id: Id, input: CustomFieldInput): Promise<CustomFieldDef>;
    deleteCustomField(id: Id): Promise<void>;
    /** `PUT admin/custom-fields/order {ids}` */
    reorderCustomFields(ids: Id[]): Promise<CustomFieldDef[]>;

    getFieldRules(): Promise<FieldRules>;
    updateFieldRules(input: FieldRules): Promise<FieldRules>;

    getEntities(): Promise<LegalEntity[]>;
    createEntity(input: LegalEntityInput): Promise<LegalEntity>;
    updateEntity(id: Id, input: LegalEntityInput): Promise<LegalEntity>;

    getCurrencies(): Promise<Currency[]>;
    /** Replaces the list. DKK must stay, with rate 1. */
    updateCurrencies(input: Currency[]): Promise<Currency[]>;

    getTags(): Promise<Tag[]>;
    createTag(input: TagInput): Promise<Tag>;
    updateTag(id: Id, input: TagInput): Promise<Tag>;
    deleteTag(id: Id): Promise<void>;

    getUsers(query?: { q?: string; active?: boolean }): Promise<AdminUser[]>;
    createUser(input: AdminUserInput): Promise<AdminUser>;
    updateUser(id: Id, input: AdminUserInput): Promise<AdminUser>;
    getEffectiveAccess(userId: Id, query?: { q?: string; page?: number; pageSize?: number }): Promise<EffectiveAccess>;
    previewReassign(input: ReassignInput): Promise<ReassignPreview>;
    reassign(input: ReassignInput): Promise<ReassignResult>;

    getNotificationSettings(): Promise<NotificationSettings>;
    updateNotificationSettings(input: NotificationSettings): Promise<NotificationSettings>;
    previewNotificationTemplate(input: TemplatePreviewInput): Promise<TemplatePreview>;

    getJobs(): Promise<JobInfo[]>;
    updateJobSchedule(key: JobKey, schedule: Schedule): Promise<JobInfo>;
    runJob(key: JobKey): Promise<JobInfo>;

    getAppSettings(): Promise<AppSettings>;
    updateAppSettings(input: AppSettings): Promise<AppSettings>;

    // ----- Release 4 -----
    getTemplateCategories(): Promise<TemplateCategory[]>;
    createTemplateCategory(input: TemplateCategoryInput): Promise<TemplateCategory>;
    updateTemplateCategory(id: Id, input: TemplateCategoryInput): Promise<TemplateCategory>;
    deleteTemplateCategory(id: Id): Promise<void>;
    orderTemplateCategories(ids: Id[]): Promise<TemplateCategory[]>;
    uploadLogo(variant: LogoVariant, file: File): Promise<Branding>;
    deleteLogo(variant: LogoVariant): Promise<Branding>;
    /** `GET admin/departments` with the release 4 link fields. */
    getDepartmentsV4(): Promise<DepartmentAdmin[]>;
    updateDepartmentV4(id: Id, input: DepartmentInputV4): Promise<DepartmentAdmin>;
    testEntityLink(id: Id): Promise<LinkTestResult>;
    testDepartmentLink(id: Id): Promise<LinkTestResult>;
    getAccessMapping(): Promise<AccessMapping>;
    getESignSettings(): Promise<ESignSettings>;
    updateESignSettings(input: ESignSettings): Promise<ESignSettings>;
    getStorageHealth(): Promise<StorageHealth>;
    getReconciliation(): Promise<ReconciliationReport | null>;
    runReconciliation(): Promise<ReconciliationReport>;
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
