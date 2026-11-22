using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;

namespace Dafa.Contracts.Core.Dtos;

// Shapes of the JSON API (brief section 5). Property names are serialized in camelCase,
// enums as strings and DateOnly as yyyy-MM-dd.

public class MeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Initials { get; set; } = "";
    public List<string> Roles { get; set; } = new();
    public List<Guid> DepartmentIds { get; set; } = new();
    public List<Guid> ManagedEntityIds { get; set; } = new();
    public bool IsGroupManagement { get; set; }
    public string AuthMode { get; set; } = "Dev";
    /// <summary>Release 4: home legal entity, or null.</summary>
    public Guid? EntityId { get; set; }
    /// <summary>Release 4: may send contracts for e-signature.</summary>
    public bool CanSendForSignature { get; set; }
    /// <summary>Release 4: entities the signing permission is limited to; empty = all.</summary>
    public List<Guid> SignatureEntityIds { get; set; } = new();
}

public class IdNameDto
{
    public IdNameDto() { }

    public IdNameDto(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class LookupUserDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Initials { get; set; } = "";
    public List<Guid> DepartmentIds { get; set; } = new();
    /// <summary>Release 3: inactive users are included so names resolve.</summary>
    public bool IsActive { get; set; }
    public string Email { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>Release 4: home legal entity, or null.</summary>
    public Guid? EntityId { get; set; }
    /// <summary>Reader, ContractOwner, Approver, Admin. Lets the UI offer only approvers as delegates.</summary>
    public List<string> Roles { get; set; } = new();
}

public class LookupsDto
{
    public List<LookupEntityDto> Entities { get; set; } = new();
    public List<NamedRefV3Dto> Departments { get; set; } = new();
    public List<NamedRefV3Dto> ContractTypes { get; set; } = new();
    public List<LookupUserDto> Users { get; set; } = new();
    public List<string> Currencies { get; set; } = new();
}

public class SupplierDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Country { get; set; }
    public string? VatNo { get; set; }
}

public class ContractListItemDto
{
    public Guid Id { get; set; }
    public string ContractNo { get; set; } = "";
    public string Title { get; set; } = "";
    public string Counterparty { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string EntityName { get; set; } = "";
    public string DepartmentName { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string OwnerInitials { get; set; } = "";
    public DateOnly? NoticeDeadline { get; set; }
    public int? DaysToNotice { get; set; }
    public decimal? AnnualValue { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> MissingData { get; set; } = new();

    // Release 3 (ContractListItemV3)
    public Guid EntityId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid TypeId { get; set; }
    public Guid OwnerId { get; set; }
    public string? DelegateName { get; set; }
    public string? ResponsibleName { get; set; }
    /// <summary>= supplierId.</summary>
    public Guid? CounterpartyId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public decimal? TotalValue { get; set; }
    public decimal AnnualValueDkk { get; set; }
    public decimal TotalValueDkk { get; set; }
    public bool AutoRenew { get; set; }
    public bool? ChangeOfControl { get; set; }
    public string? Exclusivity { get; set; }
    public DateOnly CreatedAt { get; set; }
    public DateOnly? SignedDate { get; set; }
    public bool HasSignedDocument { get; set; }
    public List<TagRefDto> Tags { get; set; } = new();
    public bool Watching { get; set; }
    public Dictionary<string, object?> CustomFields { get; set; } = new();
}

public class PagedDto<T>
{
    public List<T> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class LifecycleDto
{
    public string Name { get; set; } = "";
    public DateOnly? Date { get; set; }
    public string State { get; set; } = "";
}

public class PersonDto
{
    public string Role { get; set; } = "";
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Initials { get; set; } = "";
    public bool GetsReminders { get; set; }
}

public class DocumentDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Version { get; set; }
    public long Size { get; set; }
    public DateTime UploadedAt { get; set; }
    public string UploadedBy { get; set; } = "";
}

public class ObligationDto
{
    public Guid Id { get; set; }
    public string Description { get; set; } = "";
    public DateOnly? DueDate { get; set; }
    public int? FrequencyMonths { get; set; }
    public Guid ResponsibleId { get; set; }
    public string ResponsibleName { get; set; } = "";
    public string Status { get; set; } = "";
}

public class LinkDto
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>out = this contract points at the other one; in = the other way round.</summary>
    public string Direction { get; set; } = "out";
}

public class ApprovalStepDto
{
    public Guid Id { get; set; }
    public Guid ApproverId { get; set; }
    public string ApproverName { get; set; } = "";
    public string Initials { get; set; } = "";
    public string RoleLabel { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime? DecidedAt { get; set; }
    public string? Comment { get; set; }
}

public class ApprovalStageDto
{
    public int Order { get; set; }
    public List<ApprovalStepDto> Steps { get; set; } = new();
}

public class ApprovalInstanceDto
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public string ContractTitle { get; set; } = "";
    public string Status { get; set; } = "";
    public int CurrentStage { get; set; }
    public List<ApprovalStageDto> Stages { get; set; } = new();
}

public class ReminderDto
{
    public string Kind { get; set; } = "";
    public DateOnly DueDate { get; set; }
    public DateTime? SentAt { get; set; }
}

public class ActivityDto
{
    public DateTime At { get; set; }
    public string Who { get; set; } = "";
    public string Text { get; set; } = "";
}

public class ContractDetailDto : ContractListItemDto
{
    // typeId, entityId, departmentId, ownerId, startDate, endDate, totalValue, autoRenew,
    // exclusivity, changeOfControl and signedDate are inherited from the list item (release 3).
    public Guid? SupplierId { get; set; }
    public string CounterpartyName { get; set; } = "";
    public Guid? DelegateId { get; set; }
    public Guid? ResponsibleId { get; set; }
    public string ReminderRecipient { get; set; } = "";
    public bool OpenEnded { get; set; }
    public int? NoticePeriodDays { get; set; }
    public int? RenewalMonths { get; set; }
    public string? PaymentTerms { get; set; }
    public string? PriceAdjustment { get; set; }
    public DateOnly? PriceAdjustmentDate { get; set; }
    public string? GoverningLaw { get; set; }
    public string? LiabilityCap { get; set; }
    public string? Summary { get; set; }
    public string? SigningMethod { get; set; }
    /// <summary>Release 3.</summary>
    public int WatcherCount { get; set; }
    public int CommentCount { get; set; }
    public bool CanTag { get; set; }
    public string? RenewalDecision { get; set; }
    public Guid? RenewalDecidedById { get; set; }
    public string? RenewalDecidedBy { get; set; }
    public DateTime? RenewalDecidedAt { get; set; }
    public string? RenewalComment { get; set; }
    public DateOnly? NextReminderDate { get; set; }

    public List<LifecycleDto> Lifecycle { get; set; } = new();
    public List<PersonDto> People { get; set; } = new();
    public List<DocumentDto> Documents { get; set; } = new();
    public List<ObligationDto> Obligations { get; set; } = new();
    public List<LinkDto> Links { get; set; } = new();
    public ApprovalInstanceDto? Approval { get; set; }
    public List<ReminderDto> Reminders { get; set; } = new();
    public List<ActivityDto> Activity { get; set; } = new();
    public List<string> Access { get; set; } = new();
    public List<Deviation> Deviations { get; set; } = new();
    public bool CanEdit { get; set; }
    public bool CanDecideRenewal { get; set; }
    /// <summary>Who submitted the current or most recent approval; null when the contract was never submitted.</summary>
    public Guid? SubmittedById { get; set; }
    /// <summary>
    /// Names of the request fields this user may not change because the contract is approved
    /// (camelCase, as in the PUT body): the locked fields that currently have a value. A locked
    /// field that is still empty is not listed and may be filled in once. Empty in Draft and for
    /// administrators.
    /// </summary>
    public List<string> LockedFields { get; set; } = new();
    public string? StoreUrl { get; set; }
    /// <summary>Release 4: the open signature request, else the last finished one, else null.</summary>
    public SignatureRequestDto? Signature { get; set; }
    /// <summary>Release 4: the caller may send this contract for e-signature right now.</summary>
    public bool CanSendForSignature { get; set; }
}

public class RenewingDto
{
    public int Count { get; set; }
    public decimal ValueDkk { get; set; }
}

public class HomeTaskDto
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Meta { get; set; } = "";
    public string Pill { get; set; } = "";
    public string Urgency { get; set; } = "info";
    public Guid? ContractId { get; set; }
    public Guid? ApprovalStepId { get; set; }
    public Guid? ObligationId { get; set; }
}

public class MonthCountDto
{
    public string Month { get; set; } = "";
    public int Count { get; set; }
}

public class RecentDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
}

public class HomeDto
{
    public int DecisionsThisWeek { get; set; }
    public int RenewalsThisWeek { get; set; }
    public int ApprovalsWaiting { get; set; }
    public RenewingDto RenewingIn90 { get; set; } = new();
    public int MyContracts { get; set; }
    public List<HomeTaskDto> Tasks { get; set; } = new();
    public List<MonthCountDto> NoticeByMonth { get; set; } = new();
    public List<RecentDto> Recent { get; set; } = new();
}

public class SearchResultDto
{
    /// <summary>Contract or Supplier.</summary>
    public string Kind { get; set; } = "";
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>Release 3: the contract of a Document hit.</summary>
    public Guid? ContractId { get; set; }
}

public class MyApprovalDto
{
    public ApprovalInstanceDto Instance { get; set; } = new();
    public Guid StepId { get; set; }
    public int WaitingDays { get; set; }
    public ContractDetailDto Contract { get; set; } = new();
}

public class ApprovalPreviewStageDto
{
    public int Order { get; set; }
    public List<string> Approvers { get; set; } = new();
}

public class ApprovalPreviewDto
{
    public Guid? RuleId { get; set; }
    public string? RuleName { get; set; }
    public List<ApprovalPreviewStageDto> Stages { get; set; } = new();
}

public class RenewalDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public Guid EntityId { get; set; }
    public string EntityName { get; set; } = "";
    public string DepartmentName { get; set; } = "";
    public DateOnly NoticeDeadline { get; set; }
    public int DaysToNotice { get; set; }
    public bool AutoRenew { get; set; }
    public decimal? AnnualValue { get; set; }
    public string Currency { get; set; } = "";
}

public class NameValueDto
{
    public string Name { get; set; } = "";
    public decimal ValueDkk { get; set; }
}

public class NameCountDto
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public class ChangeOfControlDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string EntityName { get; set; } = "";
}

public class OverviewDto
{
    public int ActiveContracts { get; set; }
    public decimal AnnualValueDkk { get; set; }
    public int DueIn90 { get; set; }
    public int InApproval { get; set; }
    public int MissingData { get; set; }
    public List<NameValueDto> ValueByEntity { get; set; } = new();
    public List<NameCountDto> CountByType { get; set; } = new();
    public List<ChangeOfControlDto> ChangeOfControl { get; set; } = new();
    public List<ContractListItemDto> ExpiringNoRenewal { get; set; } = new();
}

public class ExtractResultDto
{
    public Guid UploadId { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public int Pages { get; set; }
    public List<FieldSuggestion> Suggestions { get; set; } = new();
    public string Summary { get; set; } = "";
    public List<Deviation> Deviations { get; set; } = new();
    /// <summary>Why there are no suggestions, when the file was too large or too slow to read. Usually null.</summary>
    public string? Note { get; set; }
}

public class TemplateDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid? TypeId { get; set; }
    public string FileName { get; set; } = "";
    public List<string> Placeholders { get; set; } = new();
    public bool IsActive { get; set; }
    // Release 4
    public Guid? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    /// <summary>Empty = every contract type. TypeId stays the first of them for older clients.</summary>
    public List<Guid> TypeIds { get; set; } = new();
    public string? Language { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class FromTemplateResultDto
{
    public Guid UploadId { get; set; }
    public string FileName { get; set; } = "";
}

public class DevUserDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Initials { get; set; } = "";
    /// <summary>Short description shown in the user picker, e.g. "Group CFO".</summary>
    public string Title { get; set; } = "";
    public List<string> Roles { get; set; } = new();
    public string Access { get; set; } = "";
}

// ---------- Requests ----------

/// <summary>Body of POST/PUT contracts. Everything is nullable so missing fields give field errors.</summary>
public class ContractInput
{
    public string? Title { get; set; }
    public Guid? TypeId { get; set; }
    public Guid? SupplierId { get; set; }
    public string? CounterpartyName { get; set; }
    public Guid? EntityId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? OwnerId { get; set; }
    public Guid? DelegateId { get; set; }
    public Guid? ResponsibleId { get; set; }
    public ReminderRecipient? ReminderRecipient { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    /// <summary>Optional. When sent as false, endDate is required.</summary>
    public bool? OpenEnded { get; set; }
    public int? NoticePeriodDays { get; set; }
    public bool? AutoRenew { get; set; }
    public int? RenewalMonths { get; set; }
    public decimal? AnnualValue { get; set; }
    public decimal? TotalValue { get; set; }
    public string? Currency { get; set; }
    public string? PaymentTerms { get; set; }
    public string? PriceAdjustment { get; set; }
    public DateOnly? PriceAdjustmentDate { get; set; }
    public string? GoverningLaw { get; set; }
    public string? LiabilityCap { get; set; }
    public string? Exclusivity { get; set; }
    public bool? ChangeOfControl { get; set; }
    public string? Summary { get; set; }
    public Guid? UploadId { get; set; }
    /// <summary>Release 3: custom field values by key. Omitted = unchanged (update); given = the full set.</summary>
    public Dictionary<string, System.Text.Json.JsonElement>? CustomFields { get; set; }
    /// <summary>Release 3: tag ids. Omitted = unchanged.</summary>
    public List<Guid>? TagIds { get; set; }
}

public class SignInput
{
    public DateOnly? SignedDate { get; set; }
    public string? Method { get; set; }
}

public class RenewalDecisionInput
{
    public RenewalDecision? Decision { get; set; }
    public string? Comment { get; set; }
    /// <summary>Only for Renew: the end date of the new term. Required unless the contract renews automatically.</summary>
    public DateOnly? NewEndDate { get; set; }
}

public class ObligationInput
{
    public string? Description { get; set; }
    public DateOnly? DueDate { get; set; }
    public int? FrequencyMonths { get; set; }
    public Guid? ResponsibleId { get; set; }
    public ObligationStatus? Status { get; set; }
}

public class LinkInput
{
    public Guid? ToContractId { get; set; }
    public LinkKind? Kind { get; set; }
}

public class FromTemplateInput
{
    public Guid? TemplateId { get; set; }
    public Dictionary<string, string?>? Fields { get; set; }
}

public class StepDecisionInput
{
    public ApprovalDecision? Decision { get; set; }
    public string? Comment { get; set; }
}

public class DelegateInput
{
    public Guid? UserId { get; set; }
}

public class DevLoginInput
{
    public Guid? UserId { get; set; }
    /// <summary>Only checked when Auth:Dev:AccessCode is configured.</summary>
    public string? AccessCode { get; set; }
}

public class DevConfigDto
{
    public string AuthMode { get; set; } = "Dev";
    public bool AccessCodeRequired { get; set; }
    /// <summary>Release 4: logo for the sign-in screen.</summary>
    public BrandingDto Branding { get; set; } = new();
}

// ---------- Admin ----------

public class ApprovalRuleDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public Guid? EntityId { get; set; }
    public Guid? TypeId { get; set; }
    public Guid? DepartmentId { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public int Priority { get; set; }
    /// <summary>Optional in requests; a rule is active unless this is sent as false.</summary>
    public bool? IsActive { get; set; }
    public List<RuleStageDto>? Stages { get; set; }
}

public class RuleStageDto
{
    public int Order { get; set; }
    public List<RuleApproverDto>? Approvers { get; set; }
}

/// <summary>One approver slot: a specific user (userId) or a role resolver (role), never both.</summary>
public class RuleApproverDto
{
    public Guid? UserId { get; set; }
    /// <summary>DepartmentHead, EntityManager, GroupCFO, GroupCEO or Legal.</summary>
    public ApproverKind? Role { get; set; }
}

public class RetentionRuleDto
{
    public Guid Id { get; set; }
    public Guid? TypeId { get; set; }
    public string? TypeName { get; set; }
    public int? Years { get; set; }
    public RetentionAction Action { get; set; } = RetentionAction.ReviewThenDelete;
    public Guid? ReviewerDepartmentId { get; set; }
}

/// <summary>Department or contract type. Requests need only name; isActive and sortOrder are optional.</summary>
public class NamedItemDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public bool? IsActive { get; set; }
    public int? SortOrder { get; set; }
    // Release 4, departments only (null for contract types): SharePoint library and Entra group.
    public string? DocumentLibrary { get; set; }
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
    /// <summary>Output only.</summary>
    public LinkStatusDto? Link { get; set; }
}

/// <summary>JSON body of POST/PUT admin/templates.</summary>
public class TemplateInput
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Guid? TypeId { get; set; }
    public bool? IsActive { get; set; }
    // Release 4. On PUT an omitted (null) typeIds means "unchanged"; categoryId and language are set as given.
    public Guid? CategoryId { get; set; }
    public List<Guid>? TypeIds { get; set; }
    public string? Language { get; set; }
}

public class ReminderSettingsDto
{
    public List<int> NoticeDays { get; set; } = new();
    public int EscalationDays { get; set; }
    public int ObligationDays { get; set; }
}

public class AuditEventDto
{
    public Guid Id { get; set; }
    public DateTime At { get; set; }
    public string Who { get; set; } = "";
    public string Action { get; set; } = "";
    public Guid? ContractId { get; set; }
    public string? ContractNo { get; set; }
    public string? ContractTitle { get; set; }
    public string Text { get; set; } = "";
}

public class OutboxMessageDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Recipient as shown in admin: "Name &lt;email&gt;".</summary>
    public string To { get; set; } = "";
    /// <summary>Mail or Teams. Only Mail is produced for now.</summary>
    public string Channel { get; set; } = "Mail";
    public string Recipient { get; set; } = "";
    public string RecipientEmail { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public Guid? ContractId { get; set; }
    public DateTime? DeliveredAt { get; set; }
}

public class HolidayCoverDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public Guid? DeputyId { get; set; }
    public string? DeputyName { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public class JobRunResultDto
{
    public DateTime RanAt { get; set; }
    /// <summary>Same as noticeReminders (name used by the UI).</summary>
    public int RemindersCreated => NoticeReminders;
    /// <summary>Same as contractsEnded (name used by the UI).</summary>
    public int ContractsExpired => ContractsEnded;
    public int NoticeReminders { get; set; }
    public int Escalations { get; set; }
    public int ObligationReminders { get; set; }
    /// <summary>Yearly review reminders for open-ended contracts.</summary>
    public int AnnualReviewReminders { get; set; }
    public int ContractsRenewed { get; set; }
    public int ContractsEnded { get; set; }
    public int SuppliersSynced { get; set; }
    public int DirectoryChanges { get; set; }
    public int StepsRerouted { get; set; }
    public List<string> Errors { get; set; } = new();
}
