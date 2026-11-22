using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dafa.Contracts.Core.Dtos;

// Release 3 API shapes. They mirror src/web/src/api/types.ts, section "Release 3" (the normative
// JSON), with camelCase names, enums as strings, DateOnly as yyyy-MM-dd and DateTime as ISO UTC.

public class TagRefDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "gray";
}

public class LookupEntityDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Country { get; set; }
    public string? Currency { get; set; }
    public bool IsActive { get; set; }
}

public class NamedRefV3Dto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}

// ---------- shared filter (parsed from the query string or a JSON object) ----------

/// <summary>ContractFilter (CONTRACT_V3 section 2). Lists are "any of"; null/empty = no restriction.</summary>
public class ContractFilter
{
    public string? Q { get; set; }
    public List<Guid> Ids { get; set; } = new();
    public List<Guid> EntityIds { get; set; } = new();
    public List<Guid> DepartmentIds { get; set; } = new();
    public List<Guid> TypeIds { get; set; } = new();
    public List<string> Statuses { get; set; } = new();
    public List<Guid> CounterpartyIds { get; set; } = new();
    public List<Guid> OwnerIds { get; set; } = new();
    public Guid? PersonId { get; set; }
    public List<string> ValueBands { get; set; } = new();
    public decimal? MinValueDkk { get; set; }
    public string? DateField { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public int? NoticeWithinDays { get; set; }
    public bool? Decided { get; set; }
    public List<Guid> TagIds { get; set; } = new();
    public bool? ChangeOfControl { get; set; }
    public bool? Exclusivity { get; set; }
    public bool? AutoRenew { get; set; }
    public bool? MissingData { get; set; }
    public string? MissingField { get; set; }
    public bool? MissingSignedDocument { get; set; }
    public bool? Watching { get; set; }
    public bool? IncludeEnded { get; set; }
    /// <summary>cf.&lt;key&gt; filters: key (without "cf.") -> value.</summary>
    public Dictionary<string, string> Custom { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The release 1 register view (all, due90, approval, mine, missing).</summary>
    public string? View { get; set; }
    public string? Sort { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
    /// <summary>Export only: register column keys or cf.&lt;key&gt;.</summary>
    public List<string> Columns { get; set; } = new();
}

// ---------- overview analytics ----------

public class BucketDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public decimal AnnualValueDkk { get; set; }
    public decimal TotalValueDkk { get; set; }
    public decimal SharePct { get; set; }
}

public class NoticeMonthDto
{
    public string Month { get; set; } = "";
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
    public int Decided { get; set; }
    public decimal AnnualValueDkk { get; set; }
}

public class ExpiryQuarterDto
{
    public string Quarter { get; set; } = "";
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public int Count { get; set; }
    public int AutoRenew { get; set; }
    public decimal AnnualValueDkk { get; set; }
}

public class DataQualityRowDto
{
    public Guid EntityId { get; set; }
    public string Name { get; set; } = "";
    public int Contracts { get; set; }
    public int Complete { get; set; }
    public int Score { get; set; }
    public Dictionary<string, int> Missing { get; set; } = new();
}

public class DueDiligenceItemDto
{
    public Guid Id { get; set; }
    public string ContractNo { get; set; } = "";
    public string Title { get; set; } = "";
    public string Counterparty { get; set; } = "";
    public string EntityName { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal AnnualValueDkk { get; set; }
    public decimal TotalValueDkk { get; set; }
    public string? Note { get; set; }
}

public class AnalyticsScopeDto
{
    public string Label { get; set; } = "";
    public int VisibleContracts { get; set; }
}

public class AnalyticsKpisDto
{
    public int Count { get; set; }
    public decimal AnnualValueDkk { get; set; }
    public decimal TotalValueDkk { get; set; }
    public int Notice30 { get; set; }
    public int Notice90 { get; set; }
    public int InApproval { get; set; }
    public int MissingData { get; set; }
    public int AutoRenewCount { get; set; }
    public decimal AutoRenewSharePct { get; set; }
}

public class DueDiligenceDto
{
    public decimal ThresholdDkk { get; set; }
    public List<DueDiligenceItemDto> ChangeOfControl { get; set; } = new();
    public List<DueDiligenceItemDto> Exclusivity { get; set; } = new();
    public List<DueDiligenceItemDto> AboveThreshold { get; set; } = new();
    public List<DueDiligenceItemDto> MissingSignedDocument { get; set; } = new();
}

public class OverviewAnalyticsDto
{
    public DateTime AsOf { get; set; }
    public AnalyticsScopeDto Scope { get; set; } = new();
    public AnalyticsKpisDto Kpis { get; set; } = new();
    public List<BucketDto> ByEntity { get; set; } = new();
    public List<BucketDto> ByDepartment { get; set; } = new();
    public List<BucketDto> ByType { get; set; } = new();
    public List<BucketDto> TopCounterparties { get; set; } = new();
    public decimal Top10SharePct { get; set; }
    public List<NoticeMonthDto> NoticeByMonth { get; set; } = new();
    public List<ExpiryQuarterDto> ExpiringByQuarter { get; set; } = new();
    public List<BucketDto> StatusFunnel { get; set; } = new();
    public List<DataQualityRowDto> DataQuality { get; set; } = new();
    public DueDiligenceDto DueDiligence { get; set; } = new();
}

// ---------- search ----------

public class SnippetDto
{
    public string Text { get; set; } = "";
    /// <summary>[start, end) offsets into Text.</summary>
    public List<int[]> Highlights { get; set; } = new();
}

public class ContractHitDto
{
    public ContractListItemDto Item { get; set; } = new();
    public List<string> MatchedIn { get; set; } = new();
    public SnippetDto? Snippet { get; set; }
}

public class DocumentHitDto
{
    public Guid DocumentId { get; set; }
    public Guid ContractId { get; set; }
    public string ContractNo { get; set; } = "";
    public string ContractTitle { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Version { get; set; }
    public long Size { get; set; }
    public DateTime UploadedAt { get; set; }
    public string UploadedBy { get; set; } = "";
    public List<string> MatchedIn { get; set; } = new();
    public SnippetDto? Snippet { get; set; }
}

public class PersonHitDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Initials { get; set; } = "";
    public string? Title { get; set; }
    public List<string> DepartmentNames { get; set; } = new();
    public int ContractCount { get; set; }
}

public class CounterpartyHitDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Country { get; set; }
    public int ContractCount { get; set; }
}

public class SearchGroupDto<T>
{
    public int Total { get; set; }
    public List<T> Items { get; set; } = new();
}

public class FullSearchResultDto
{
    public string Q { get; set; } = "";
    public SearchGroupDto<ContractHitDto> Contracts { get; set; } = new();
    public SearchGroupDto<DocumentHitDto> Documents { get; set; } = new();
    public SearchGroupDto<PersonHitDto> People { get; set; } = new();
    public SearchGroupDto<CounterpartyHitDto> Counterparties { get; set; } = new();
}

public class FullSearchQuery
{
    public string? Q { get; set; }
    public string? Group { get; set; }
    public Guid? PersonId { get; set; }
    public Guid? EntityId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? TypeId { get; set; }
    public string? DocumentKind { get; set; }
    public DateOnly? UploadedFrom { get; set; }
    public DateOnly? UploadedTo { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}

// ---------- person and counterparty pages ----------

public class PersonContractDto : ContractListItemDto
{
    public List<string> Roles { get; set; } = new();
}

public class PersonInfoDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Initials { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Title { get; set; }
    public List<string> DepartmentNames { get; set; } = new();
    public bool IsActive { get; set; }
}

public class PersonCountsDto
{
    public int Owner { get; set; }
    public int Delegate { get; set; }
    public int Responsible { get; set; }
    public int Total { get; set; }
    public int Active { get; set; }
}

public class PersonPageDto
{
    public PersonInfoDto Person { get; set; } = new();
    public PersonCountsDto Counts { get; set; } = new();
    public decimal AnnualValueDkk { get; set; }
    public List<PersonContractDto> Contracts { get; set; } = new();
}

public class CounterpartyDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Country { get; set; }
    public string? VatNo { get; set; }
    public string Kind { get; set; } = "Supplier";
    public string? ExternalId { get; set; }
    public string? City { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; }
    public string Source { get; set; } = "Manual";
    public int ContractCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CounterpartyInput
{
    public string? Name { get; set; }
    public string? Country { get; set; }
    public string? VatNo { get; set; }
    public string? Kind { get; set; }
    public string? ExternalId { get; set; }
    public string? City { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool? IsActive { get; set; }
}

public class CounterpartySummaryDto
{
    public int Contracts { get; set; }
    public int Live { get; set; }
    public decimal AnnualValueDkk { get; set; }
    public decimal TotalValueDkk { get; set; }
    public int AutoRenewing { get; set; }
    public DateOnly? NextNoticeDeadline { get; set; }
}

public class CounterpartyPageDto
{
    public CounterpartyDto Counterparty { get; set; } = new();
    public CounterpartySummaryDto Summary { get; set; } = new();
    public List<ContractListItemDto> Contracts { get; set; } = new();
    public List<RenewalDto> Renewals { get; set; } = new();
    public List<DocumentHitDto> Documents { get; set; } = new();
}

public class DuplicateGroupDto
{
    public string Reason { get; set; } = "";
    public List<CounterpartyDto> Items { get; set; } = new();
}

public class MergeInput
{
    public Guid? TargetId { get; set; }
    public List<Guid>? SourceIds { get; set; }
}

public class MergeResultDto
{
    public CounterpartyDto Target { get; set; } = new();
    public int MovedContracts { get; set; }
    public int Deactivated { get; set; }
}

// ---------- imports ----------

public class ImportFieldDefDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Required { get; set; }
    /// <summary>text, number, money, date, bool, choice, multi, person, entity, department, type, counterparty, email, role.</summary>
    public string Type { get; set; } = "text";
    public string? Help { get; set; }
    public bool IsKey { get; set; }
    public string? Example { get; set; }
    public List<string>? Options { get; set; }
}

public class ImportOptionsDto
{
    public string? Mode { get; set; }
    public string? ContractStatus { get; set; }
    public bool? CreateMissingCounterparties { get; set; }
    public string? DateFormat { get; set; }
    public string? DecimalSeparator { get; set; }
}

public class ImportSessionDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public List<List<string>> Sample { get; set; } = new();
    public int RowCount { get; set; }
    public Dictionary<string, string?> SuggestedMapping { get; set; } = new();
    public DateTime ExpiresAt { get; set; }
}

public class ImportRunInput
{
    public Dictionary<string, string?>? Mapping { get; set; }
    public ImportOptionsDto? Options { get; set; }
}

public class ImportRowMessageDto
{
    public string? Field { get; set; }
    public string Message { get; set; } = "";
}

public class ImportRowPreviewDto
{
    public int RowNo { get; set; }
    public string Action { get; set; } = "";
    public string? Match { get; set; }
    public Dictionary<string, string> Values { get; set; } = new();
    public List<ImportRowMessageDto> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class ImportPreviewDto
{
    public Guid SessionId { get; set; }
    public int RowCount { get; set; }
    public int Create { get; set; }
    public int Update { get; set; }
    public int Skip { get; set; }
    public int Error { get; set; }
    public List<ImportRowPreviewDto> Rows { get; set; } = new();
    public List<string> MappingErrors { get; set; } = new();
}

public class ImportErrorDto
{
    public int RowNo { get; set; }
    public string Message { get; set; } = "";
}

public class ImportResultDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime RanAt { get; set; }
    public string RanBy { get; set; } = "";
    public ImportOptionsDto Options { get; set; } = new();
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<ImportErrorDto> Errors { get; set; } = new();
    public string Source { get; set; } = "File";
}

// ---------- sync sources ----------

public class ScheduleDto
{
    public string Frequency { get; set; } = "Off";
    public string Time { get; set; } = "06:00";
    /// <summary>1 = Monday ... 7 = Sunday; Weekly only.</summary>
    public int? DayOfWeek { get; set; }
}

public class SyncConnectionDto
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
}

public class PowerBiSourceDto
{
    public string? WorkspaceId { get; set; }
    public string? DatasetId { get; set; }
    public string? DaxQuery { get; set; }
    public string? Table { get; set; }
}

public class SqlSourceDto
{
    public string? ConnectionName { get; set; }
    public string? Query { get; set; }
}

public class SyncRunDto
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public string Trigger { get; set; } = "Manual";
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Status { get; set; } = "";
    public int Read { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class SyncSourceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Kind { get; set; } = "";
    public PowerBiSourceDto? PowerBi { get; set; }
    public SqlSourceDto? Sql { get; set; }
    public Dictionary<string, string?> Mapping { get; set; } = new();
    public ImportOptionsDto Options { get; set; } = new();
    public ScheduleDto Schedule { get; set; } = new();
    public bool IsActive { get; set; }
    public SyncRunDto? LastRun { get; set; }
    public DateTime? NextRunAt { get; set; }
}

public class SyncSourceInput
{
    public string? Name { get; set; }
    public string? Target { get; set; }
    public string? Kind { get; set; }
    public PowerBiSourceDto? PowerBi { get; set; }
    public SqlSourceDto? Sql { get; set; }
    public Dictionary<string, string?>? Mapping { get; set; }
    public ImportOptionsDto? Options { get; set; }
    public ScheduleDto? Schedule { get; set; }
    public bool? IsActive { get; set; }
}

public class SyncTestResultDto
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public List<List<string>> Sample { get; set; } = new();
    public int? RowCount { get; set; }
}

// ---------- custom fields and field rules ----------

public class CustomFieldDefDto
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "";
    public List<Guid> ContractTypeIds { get; set; } = new();
    public bool Required { get; set; }
    public string? HelpText { get; set; }
    public int Order { get; set; }
    public List<string> Options { get; set; } = new();
    public bool ShowInRegister { get; set; }
    public bool ShowInFilters { get; set; }
    public bool IsActive { get; set; }
}

public class CustomFieldInput
{
    public string? Key { get; set; }
    public string? Label { get; set; }
    public string? Type { get; set; }
    public List<Guid>? ContractTypeIds { get; set; }
    public bool? Required { get; set; }
    public string? HelpText { get; set; }
    public int? Order { get; set; }
    public List<string>? Options { get; set; }
    public bool? ShowInRegister { get; set; }
    public bool? ShowInFilters { get; set; }
    public bool? IsActive { get; set; }
}

public class IdsInput
{
    public List<Guid>? Ids { get; set; }
}

public class FieldRuleDefaultsDto
{
    public string? Currency { get; set; }
    public int? NoticePeriodDays { get; set; }
    public string? ReminderRecipient { get; set; }
    public bool? AutoRenew { get; set; }
    public int? RenewalMonths { get; set; }
}

public class TypeFieldRulesDto
{
    public Guid TypeId { get; set; }
    public List<string> Required { get; set; } = new();
    public FieldRuleDefaultsDto Defaults { get; set; } = new();
}

public class FieldRulesDto
{
    public List<TypeFieldRulesDto> Types { get; set; } = new();
}

// ---------- reference data admin ----------

public class LegalEntityDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
    public string Currency { get; set; } = "";
    public bool IsActive { get; set; }
    public int ContractCount { get; set; }
}

public class LegalEntityInput
{
    public string? Name { get; set; }
    public string? Country { get; set; }
    public string? Currency { get; set; }
    public bool? IsActive { get; set; }
}

public class CurrencyDto
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public decimal? RateToDkk { get; set; }
    public bool? IsActive { get; set; }
}

public class TagDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "gray";
    public string? Description { get; set; }
    public int UsageCount { get; set; }
}

public class TagInput
{
    public string? Name { get; set; }
    public string? Color { get; set; }
    public string? Description { get; set; }
}

public class ContractTagsInput
{
    public List<Guid>? TagIds { get; set; }
}

// ---------- users and access ----------

public class AdminUserDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Initials { get; set; } = "";
    public string? Title { get; set; }
    public List<string> Roles { get; set; } = new();
    public List<Guid> DepartmentIds { get; set; } = new();
    public List<Guid> ManagedEntityIds { get; set; } = new();
    public bool IsGroupManagement { get; set; }
    public bool IsActive { get; set; }
    public string Source { get; set; } = "Manual";
    public List<string> SyncedFields { get; set; } = new();
    public string? ExternalId { get; set; }
    public DateTime? LastSignInAt { get; set; }
    public int ContractCount { get; set; }
}

public class AdminUserInput
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Title { get; set; }
    public List<string>? Roles { get; set; }
    public List<Guid>? DepartmentIds { get; set; }
    public List<Guid>? ManagedEntityIds { get; set; }
    public bool? IsGroupManagement { get; set; }
    public bool? IsActive { get; set; }
}

public class AccessReasonDto
{
    public string Rule { get; set; } = "";
    public string Label { get; set; } = "";
}

public class AccessRuleCountDto
{
    public string Rule { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
}

public class EffectiveAccessContractDto
{
    public ContractListItemDto Item { get; set; } = new();
    public List<AccessReasonDto> Reasons { get; set; } = new();
}

public class EffectiveAccessDto
{
    public AdminUserDto User { get; set; } = new();
    public int Total { get; set; }
    public List<AccessRuleCountDto> Rules { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<EffectiveAccessContractDto> Contracts { get; set; } = new();
}

public class ReassignInput
{
    public Guid? FromUserId { get; set; }
    public Guid? ToUserId { get; set; }
    public List<string>? Roles { get; set; }
    public List<Guid>? ContractIds { get; set; }
    public bool? IncludeEnded { get; set; }
    public bool? IncludeObligations { get; set; }
}

public class ReassignPreviewRowDto
{
    public Guid ContractId { get; set; }
    public string ContractNo { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> Roles { get; set; } = new();
    public string? SkipReason { get; set; }
}

public class ReassignPreviewDto
{
    public List<ReassignPreviewRowDto> Rows { get; set; } = new();
    public int Contracts { get; set; }
    public int Obligations { get; set; }
}

public class ReassignSkipDto
{
    public Guid ContractId { get; set; }
    public string Reason { get; set; } = "";
}

public class ReassignResultDto
{
    public int Updated { get; set; }
    public int ObligationsMoved { get; set; }
    public List<ReassignSkipDto> Skipped { get; set; } = new();
}

// ---------- notifications ----------

public class NotificationEventSettingDto
{
    public string Event { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; }
    public List<string> Channels { get; set; } = new();
    public List<string> Recipients { get; set; } = new();
    public List<Guid> ExtraRecipientIds { get; set; } = new();
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public List<string> Placeholders { get; set; } = new();
}

public class TypeReminderScheduleDto
{
    public Guid TypeId { get; set; }
    public List<int> NoticeDays { get; set; } = new();
    public int EscalationDays { get; set; }
}

public class NotificationSettingsDto
{
    public List<NotificationEventSettingDto> Events { get; set; } = new();
    public string DigestDefault { get; set; } = "Weekly";
    public List<TypeReminderScheduleDto> ReminderSchedules { get; set; } = new();
}

public class TemplatePreviewInput
{
    public string? Event { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
}

public class TemplatePreviewDto
{
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public List<string> Unknown { get; set; } = new();
}

public class AppNotificationDto
{
    public Guid Id { get; set; }
    public string Event { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public Guid? ContractId { get; set; }
    public string? Link { get; set; }
    public string? ActorName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public class NotificationPageDto : PagedDto<AppNotificationDto>
{
    public int Unread { get; set; }
}

public class CountDto
{
    public int Count { get; set; }
}

// ---------- jobs ----------

public class JobLastRunDto
{
    public DateTime At { get; set; }
    public string Status { get; set; } = "";
    public string? Message { get; set; }
    public int DurationMs { get; set; }
}

public class JobInfoDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public ScheduleDto Schedule { get; set; } = new();
    public JobLastRunDto? LastRun { get; set; }
    public DateTime? NextRunAt { get; set; }
}

public class JobScheduleInput
{
    public ScheduleDto? Schedule { get; set; }
}

// ---------- general settings ----------

public class FeatureTogglesDto
{
    public bool AiSuggestions { get; set; } = true;
    public bool Templates { get; set; } = true;
    [JsonPropertyName("eSign")]
    public bool ESign { get; set; }
}

public class AppSettingsDto
{
    public string AppName { get; set; } = "DAFA Contracts";
    public string DateFormat { get; set; } = "d MMM yyyy";
    public string NumberFormat { get; set; } = "en-GB";
    public Dictionary<string, string> LandingPageByRole { get; set; } = new();
    public FeatureTogglesDto Features { get; set; } = new();
    public int SessionTimeoutMinutes { get; set; } = 720;
    public List<string> DataQualityFields { get; set; } = new();
    public decimal DdThresholdDkk { get; set; } = 5_000_000m;
}

public class AppConfigDto
{
    public string AppName { get; set; } = "";
    public string DateFormat { get; set; } = "";
    public string NumberFormat { get; set; } = "";
    public FeatureTogglesDto Features { get; set; } = new();
    public int SessionTimeoutMinutes { get; set; }
    public string LandingPage { get; set; } = "home";
    public Guid? DefaultEntityId { get; set; }
    public List<string> DataQualityFields { get; set; } = new();
    public decimal DdThresholdDkk { get; set; }
    public List<CustomFieldDefDto> CustomFields { get; set; } = new();
    public FieldRulesDto FieldRules { get; set; } = new();
    public List<TagRefDto> Tags { get; set; } = new();
    public Dictionary<string, decimal> Rates { get; set; } = new();
    public bool CanExportDataRoom { get; set; }
}

// ---------- saved views ----------

public class SavedViewDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Scope { get; set; } = "Personal";
    public Guid OwnerId { get; set; }
    public string OwnerName { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public JsonElement Query { get; set; }
    public bool IsDefault { get; set; }
    public bool CanEdit { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SavedViewInput
{
    public string? Name { get; set; }
    public string? Scope { get; set; }
    public List<string>? Columns { get; set; }
    public JsonElement? Query { get; set; }
    public bool? IsDefault { get; set; }
}

// ---------- bulk, comments ----------

public class BulkInput
{
    public string? Action { get; set; }
    public List<Guid>? ContractIds { get; set; }
    public Guid? TagId { get; set; }
    public string? Role { get; set; }
    public Guid? ToUserId { get; set; }
    public string? Message { get; set; }
}

public class BulkFailureDto
{
    public Guid ContractId { get; set; }
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
}

public class BulkResultDto
{
    public string Action { get; set; } = "";
    public int Requested { get; set; }
    public int Succeeded { get; set; }
    public List<BulkFailureDto> Failed { get; set; } = new();
}

public class CommentMentionDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
}

public class CommentDto
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid AuthorId { get; set; }
    public string AuthorName { get; set; } = "";
    public string AuthorInitials { get; set; } = "";
    public string Body { get; set; } = "";
    public List<CommentMentionDto> Mentions { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
}

public class CommentInput
{
    public string? Body { get; set; }
    public List<Guid>? MentionIds { get; set; }
}

// ---------- versions ----------

public class ContractVersionDto
{
    public string Id { get; set; } = "";
    public DateTime At { get; set; }
    public string Who { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> ChangedFields { get; set; } = new();
}

public class ComparedFieldDto
{
    public string Field { get; set; } = "";
    public string Label { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
    public bool Changed { get; set; }
}

public class ContractComparisonDto
{
    public ContractVersionDto From { get; set; } = new();
    public ContractVersionDto To { get; set; } = new();
    public List<ComparedFieldDto> Fields { get; set; } = new();
}

/// <summary>One field change stored in AuditEvent.DataJson of contract updates.</summary>
public class FieldChangeDto
{
    public string Field { get; set; } = "";
    public string Label { get; set; } = "";
    public string Old { get; set; } = "";
    public string New { get; set; } = "";
}

// ---------- data room ----------

public class DataRoomInput
{
    public JsonElement? Filter { get; set; }
    public string? Documents { get; set; }
    public string? Versions { get; set; }
    public string? Name { get; set; }
}

public class DataRoomExportDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public int ContractCount { get; set; }
    public int DocumentCount { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? Error { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

// ---------- preferences ----------

public class PreferenceEventDto
{
    public string Event { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Email { get; set; }
    public bool Teams { get; set; }
    public bool InApp { get; set; }
}

public class UserPreferencesDto
{
    public string Digest { get; set; } = "Weekly";
    public List<PreferenceEventDto> Events { get; set; } = new();
    public Guid? DefaultEntityId { get; set; }
    public string? LandingPage { get; set; }
    public bool AutoWatch { get; set; }
}
