namespace Dafa.Contracts.Core.Domain;

// Persistence model. Relations are plain Guid foreign-key columns without navigation properties:
// the data set is small, and this keeps the schema identical and cascade-free on Sqlite and SQL Server.

/// <summary>
/// Rows with optimistic concurrency. <see cref="Version"/> is a concurrency token that the
/// DbContext replaces with a new Guid on every insert and update (portable across Sqlite and
/// SQL Server, unlike rowversion). A save based on a stale copy fails with
/// DbUpdateConcurrencyException, which the API reports as 409.
/// </summary>
public interface IVersioned
{
    Guid Version { get; set; }
}

public class LegalEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    /// <summary>Release 3: ISO country code or name, editable in admin.</summary>
    public string? Country { get; set; }
    /// <summary>Release 3: functional currency of the entity.</summary>
    public string? Currency { get; set; }
    public bool IsActive { get; set; } = true;

    // Release 4: how the entity is linked to SharePoint and Entra ID (docs/RUNBOOK.md, "Linking a legal entity").
    /// <summary>The entity's SharePoint site, as an admin pastes it: https://tenant.sharepoint.com/sites/name.</summary>
    public string? SharePointSiteUrl { get; set; }
    /// <summary>The Graph site id resolved from the URL by "test link" (SharePoint mode). Null until resolved.</summary>
    public string? SharePointSiteId { get; set; }
    /// <summary>Object id of the Entra group whose members are entity management for this entity.</summary>
    public string? ManagementGroupId { get; set; }
    public string? ManagementGroupName { get; set; }
    /// <summary>NotLinked (or null), Linked, Error: the result of the last link test (LinkStates).</summary>
    public string? LinkStatus { get; set; }
    public DateTime? LinkCheckedAt { get; set; }
    public string? LinkMessage { get; set; }
}

public class Department
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    // Release 4: departments cut across legal entities, so the mapping is by name: every linked
    // entity site holds a document library with this name, and one Entra group holds the members.
    /// <summary>Name of the document library on each entity site. Null = the department name.</summary>
    public string? LibraryName { get; set; }
    /// <summary>Object id of the Entra group whose members are members of this department.</summary>
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
    /// <summary>NotLinked (or null), Linked, Error: the result of the last link test.</summary>
    public string? LinkStatus { get; set; }
    public DateTime? LinkCheckedAt { get; set; }
    public string? LinkMessage { get; set; }
}

public class ContractType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public class AppUser
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Initials { get; set; } = "";
    public string? JobTitle { get; set; }
    /// <summary>Object id in the external directory (Entra), when known.</summary>
    public string? ExternalId { get; set; }
    public AppRole Roles { get; set; } = AppRole.Reader;
    public bool IsGroupManagement { get; set; }
    public GroupFunction GroupFunction { get; set; } = GroupFunction.None;
    public bool IsActive { get; set; } = true;
    /// <summary>Release 3: Manual, Import, Seed, Entra. Null on older rows (Seed, or Entra when ExternalId is set).</summary>
    public string? Source { get; set; }
    public DateTime? LastSignInAt { get; set; }
    /// <summary>Release 4: may send contracts for e-signature (see SignatureEntityIdsJson for the limit).</summary>
    public bool CanSendForSignature { get; set; }
    /// <summary>Release 4: JSON array of legal entity ids the permission is limited to; null or empty = every entity.</summary>
    public string? SignatureEntityIdsJson { get; set; }
    /// <summary>Release 4: the user's home legal entity (shown in person pickers). Not part of the access model.</summary>
    public Guid? HomeEntityId { get; set; }
}

public class UserDepartment
{
    public Guid UserId { get; set; }
    public Guid DepartmentId { get; set; }
    public bool IsHead { get; set; }
}

public class UserEntityManagement
{
    public Guid UserId { get; set; }
    public Guid EntityId { get; set; }
}

public class HolidayCover
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeputyId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
}

public class Supplier
{
    public Guid Id { get; set; }
    public string? ExternalId { get; set; }
    public string Name { get; set; } = "";
    public string? Country { get; set; }
    public string? VatNo { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Release 3: set when this supplier was merged into another one (and deactivated).</summary>
    public Guid? MergedIntoId { get; set; }
    /// <summary>Release 3 (counterparty master): kind, contact data, where the record came from.</summary>
    public CounterpartyKind Kind { get; set; } = CounterpartyKind.Supplier;
    public string? City { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    /// <summary>Manual, Import, PowerBi, Sql, Seed. Null on rows from before release 3 (= Seed/PowerBi sync).</summary>
    public string? Source { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class Contract : IVersioned
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
    public string ContractNo { get; set; } = "";
    /// <summary>Release 3: id of the contract in a legacy register or source system (import match key).</summary>
    public string? ExternalRef { get; set; }
    public string Title { get; set; } = "";
    public Guid TypeId { get; set; }
    public Guid? SupplierId { get; set; }
    public string CounterpartyName { get; set; } = "";
    public Guid EntityId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid? DelegateId { get; set; }
    public Guid? ResponsibleId { get; set; }
    public ReminderRecipient ReminderRecipient { get; set; } = ReminderRecipient.Owner;
    public DateOnly StartDate { get; set; }
    /// <summary>Null means open-ended.</summary>
    public DateOnly? EndDate { get; set; }
    public int? NoticePeriodDays { get; set; }
    public bool AutoRenew { get; set; }
    public int? RenewalMonths { get; set; }
    public decimal? AnnualValue { get; set; }
    public decimal? TotalValue { get; set; }
    public string Currency { get; set; } = "DKK";
    public string? PaymentTerms { get; set; }
    public string? PriceAdjustment { get; set; }
    public DateOnly? PriceAdjustmentDate { get; set; }
    public string? GoverningLaw { get; set; }
    public string? LiabilityCap { get; set; }
    public string? Exclusivity { get; set; }
    public bool? ChangeOfControl { get; set; }
    public string? Summary { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Draft;
    public DateOnly? SignedDate { get; set; }
    public string? SigningMethod { get; set; }
    public RenewalDecision? RenewalDecision { get; set; }
    public Guid? RenewalDecidedById { get; set; }
    public DateTime? RenewalDecidedAt { get; set; }
    public string? RenewalComment { get; set; }
    /// <summary>
    /// The notice deadline of the term that was last renewed (Renew decision or automatic roll-forward).
    /// A term can be renewed only once: Renew is refused while the current deadline equals this value.
    /// </summary>
    public DateOnly? LastRenewedDeadline { get; set; }
    /// <summary>JSON array of deviations found by the analyzer at intake.</summary>
    public string? DeviationsJson { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // SharePoint hardening (schema step 4): the contract's folder, stored once when it is created,
    // so "Open in SharePoint" needs no call to SharePoint when a contract is read.
    public string? StoreDriveId { get; set; }
    public string? StoreFolderId { get; set; }
    public string? StoreFolderUrl { get; set; }
}

public class ContractLink
{
    public Guid Id { get; set; }
    public Guid FromId { get; set; }
    public Guid ToId { get; set; }
    public LinkKind Kind { get; set; }
}

public class Obligation
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public string Description { get; set; } = "";
    public DateOnly? DueDate { get; set; }
    public int? FrequencyMonths { get; set; }
    public Guid ResponsibleId { get; set; }
    public ObligationStatus Status { get; set; } = ObligationStatus.Open;
    public DateTime? LastCompletedAt { get; set; }
}

public class ContractDocument
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public string Name { get; set; } = "";
    public DocumentKind Kind { get; set; } = DocumentKind.Other;
    public string StoreKey { get; set; } = "";
    public int Version { get; set; } = 1;
    public long Size { get; set; }
    public Guid? UploadedById { get; set; }
    public DateTime UploadedAt { get; set; }

    // SharePoint hardening (schema step 4): where the file is, independent of the store key, and
    // a fingerprint of what was uploaded. All null on rows from before, and in Local mode except the hash.
    public string? SiteId { get; set; }
    public string? DriveId { get; set; }
    public string? ItemId { get; set; }
    /// <summary>Library and path inside it, e.g. "Purchasing/C-2026-0012/Agreement v2.pdf": survives a tenant move.</summary>
    public string? RelativePath { get; set; }
    public string? ETag { get; set; }
    public string? CTag { get; set; }
    /// <summary>Hex SHA-256 of the uploaded bytes.</summary>
    public string? ContentHash { get; set; }
}

public class ApprovalRule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? EntityId { get; set; }
    public Guid? TypeId { get; set; }
    public Guid? DepartmentId { get; set; }
    /// <summary>Inclusive lower bound in DKK. Null = any.</summary>
    public decimal? MinValue { get; set; }
    /// <summary>Exclusive upper bound in DKK. Null = any.</summary>
    public decimal? MaxValue { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>JSON array of <see cref="ApprovalStageDefinition"/>.</summary>
    public string StagesJson { get; set; } = "[]";
}

public class ApprovalStageDefinition
{
    public int Order { get; set; }
    public List<ApproverDefinition> Approvers { get; set; } = new();
}

public class ApproverDefinition
{
    public ApproverKind Kind { get; set; }
    /// <summary>Only used when Kind is User.</summary>
    public Guid? UserId { get; set; }
}

public class ApprovalInstance : IVersioned
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
    public Guid ContractId { get; set; }
    public Guid? RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public int CurrentStage { get; set; }
    public Guid? StartedById { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>
    /// SharePoint hardening (schema step 4): the approval is final but its approval-log document
    /// could not be stored yet (the store was down). The UploadCleanup job writes it and clears this.
    /// </summary>
    public bool LogPending { get; set; }
}

public class ApprovalStep : IVersioned
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
    public Guid InstanceId { get; set; }
    public Guid ContractId { get; set; }
    public int StageOrder { get; set; }
    public Guid ApproverId { get; set; }
    /// <summary>Set when the step was rerouted by holiday cover or created by delegation.</summary>
    public Guid? OriginalApproverId { get; set; }
    public string RoleLabel { get; set; } = "";
    public ApprovalStepStatus Status { get; set; } = ApprovalStepStatus.Waiting;
    public DateTime? ActivatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Comment { get; set; }
}

public class Reminder
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid? ObligationId { get; set; }
    /// <summary>Notice{days}, Escalation, AnnualReview or Obligation.</summary>
    public string Kind { get; set; } = "";
    /// <summary>
    /// What the reminder is about, so one is sent once per term: the notice deadline (yyyy-MM-dd) for
    /// Notice and Escalation, the anniversary for AnnualReview, obligation id and due date for Obligation.
    /// Unique together with ContractId and Kind.
    /// </summary>
    public string TermKey { get; set; } = "";
    public DateOnly DueDate { get; set; }
    /// <summary>Null while the notification could not be delivered (Graph mode); retried by the job.</summary>
    public DateTime? SentAt { get; set; }
    /// <summary>Number of delivery attempts so far.</summary>
    public int Attempts { get; set; }
    public Guid RecipientId { get; set; }
}

public class RetentionRule
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public int? Years { get; set; }
    public RetentionAction Action { get; set; } = RetentionAction.ReviewThenDelete;
    public Guid? ReviewerDepartmentId { get; set; }
}

public class ContractTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Contract type the template is meant for, if any.</summary>
    public Guid? TypeId { get; set; }
    public string FileName { get; set; } = "";
    public byte[] Content { get; set; } = Array.Empty<byte>();
    /// <summary>Comma separated placeholder names found in the document.</summary>
    public string Placeholders { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
    /// <summary>Release 4: the template category, if any.</summary>
    public Guid? CategoryId { get; set; }
    /// <summary>Release 4: JSON array of contract type ids the template is meant for; null or empty = TypeId (or every type).</summary>
    public string? ContractTypeIdsJson { get; set; }
    /// <summary>Release 4: language of the template text, e.g. "en", "da", "de". Null = not stated.</summary>
    public string? Language { get; set; }
}

public class AuditEvent
{
    public Guid Id { get; set; }
    public DateTime At { get; set; }
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public Guid? ContractId { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Release 3: structured details (for contract updates: JSON array of field changes).</summary>
    public string? DataJson { get; set; }
}

public class OutboxMessage
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? RecipientId { get; set; }
    public string RecipientEmail { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public Guid? ContractId { get; set; }
    /// <summary>Set by providers that really deliver (Graph mail). Null in Outbox mode.</summary>
    public DateTime? DeliveredAt { get; set; }
    /// <summary>Release 3: Mail (null on older rows) or Teams (stub: Teams messages are only recorded here).</summary>
    public string? Channel { get; set; }
}

public class ReminderSettings
{
    public Guid Id { get; set; }
    /// <summary>Comma separated days before the notice deadline, e.g. "120,60,30".</summary>
    public string NoticeDays { get; set; } = "120,60,30";
    public int EscalationDays { get; set; } = 14;
    public int ObligationDays { get; set; } = 14;

    public List<int> GetNoticeDays()
    {
        var result = new List<int>();
        foreach (var part in (NoticeDays ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var days) && days > 0 && !result.Contains(days))
            {
                result.Add(days);
            }
        }
        result.Sort();
        result.Reverse();
        return result;
    }
}

/// <summary>A file uploaded during intake that is not attached to a contract yet.</summary>
public class PendingUpload
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string? Summary { get; set; }
    public string? DeviationsJson { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
}
