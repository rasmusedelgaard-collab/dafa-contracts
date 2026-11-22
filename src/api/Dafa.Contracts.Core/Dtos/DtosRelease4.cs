namespace Dafa.Contracts.Core.Dtos;

// Release 4 API shapes. They mirror src/web/src/api/types.ts, section "Release 4" (the normative
// JSON; docs/CONTRACT_V4.md has the rules). Fields added to earlier shapes are in Dtos.cs and
// DtosRelease3.cs, marked "Release 4".

// ---------- register header filters ----------

public class DistinctValueDto
{
    /// <summary>What goes into the filter parameter: an id, a status key, a currency code, a text.</summary>
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
}

public class DistinctValuesDto
{
    public string Column { get; set; } = "";
    public List<DistinctValueDto> Values { get; set; } = new();
    public bool Truncated { get; set; }
}

// ---------- pickers ----------

public class PersonOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Initials { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>The first department (by the departments' order), or null.</summary>
    public string? DepartmentName { get; set; }
    /// <summary>The home legal entity, or null.</summary>
    public string? EntityName { get; set; }
    public bool IsActive { get; set; }
    /// <summary>Extra (not in types.ts): every department of the user, in the departments' order.</summary>
    public List<string> DepartmentNames { get; set; } = new();
}

// ---------- templates ----------

public class TemplateCategoryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Order { get; set; }
    public bool IsActive { get; set; }
    public int TemplateCount { get; set; }
}

public class TemplateCategoryInput
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool? IsActive { get; set; }
}

public class TemplatePlaceholderDto
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Sample { get; set; } = "";
}

public class TemplatePreviewInfoDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<TemplatePlaceholderDto> Placeholders { get; set; } = new();
    public string Excerpt { get; set; } = "";
}

// ---------- branding ----------

public class BrandingDto
{
    /// <summary>Relative to the API base, e.g. branding/logo?variant=full&amp;v=1a2b3c; null = the default mark.</summary>
    public string? LogoUrl { get; set; }
    public string? CompactLogoUrl { get; set; }
    /// <summary>The logo for the dark theme (variant dark); null = use the normal logo.</summary>
    public string? DarkLogoUrl { get; set; }
    public string Version { get; set; } = "0";
}

// ---------- SharePoint / Entra link ----------

public class LinkStatusDto
{
    /// <summary>NotLinked, Linked or Error.</summary>
    public string State { get; set; } = "NotLinked";
    public DateTime? CheckedAt { get; set; }
    public string? Message { get; set; }
}

public class LinkTestResultDto
{
    public LinkStatusDto Link { get; set; } = new();
    public string? SiteId { get; set; }
    public string? GroupName { get; set; }
    public List<string> Details { get; set; } = new();
}

public class AccessMappingCellDto
{
    public Guid EntityId { get; set; }
    public Guid DepartmentId { get; set; }
    public string? SiteUrl { get; set; }
    public string? Library { get; set; }
    public string? EntityGroupName { get; set; }
    public string? DepartmentGroupName { get; set; }
    public int ContractCount { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public class AccessMappingDto
{
    /// <summary>Local or SharePoint.</summary>
    public string Mode { get; set; } = "Local";
    public List<IdNameDto> Entities { get; set; } = new();
    public List<IdNameDto> Departments { get; set; } = new();
    public List<AccessMappingCellDto> Cells { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

// ---------- e-signature ----------

public class AuthorisedSignatoriesDto
{
    public Guid EntityId { get; set; }
    public List<Guid> UserIds { get; set; } = new();
    public List<string> ExternalNames { get; set; } = new();
}

public class ESignSettingsDto
{
    /// <summary>None, Simulated, DocuSign or Penneo.</summary>
    public string Provider { get; set; } = "None";
    public string DefaultMessage { get; set; } = "Please review and sign the attached contract.";
    /// <summary>Sequential or Parallel.</summary>
    public string DefaultOrder { get; set; } = "Parallel";
    /// <summary>Days after sending on which signatories who have not signed are reminded (0 to 5 values, each 1 to 60).</summary>
    public List<int> ReminderDays { get; set; } = new() { 3, 7 };
    public int ExpiryDays { get; set; } = 30;
    public bool RequireApproved { get; set; } = true;
    public List<AuthorisedSignatoriesDto> AuthorisedSignatories { get; set; } = new();
    public bool RestrictToAuthorised { get; set; }
    /// <summary>Read-only: the provider account configured on the server (never a secret), or null.</summary>
    public string? ProviderAccount { get; set; }
}

public class ESignConfigDto
{
    public string Provider { get; set; } = "None";
    public bool Enabled { get; set; }
    /// <summary>False: drafts and active contracts can be sent for signature too (the setting in Settings > E-signature).</summary>
    public bool RequireApproved { get; set; } = true;
}

public class SignerDto
{
    public Guid Id { get; set; }
    /// <summary>Internal or External.</summary>
    public string Kind { get; set; } = "External";
    public Guid? UserId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public int Order { get; set; }
    /// <summary>Waiting, Sent, Viewed, Signed, Declined or Expired.</summary>
    public string Status { get; set; } = "Waiting";
    /// <summary>When the signer reached the current status.</summary>
    public DateTime? At { get; set; }
    public string? DeclineReason { get; set; }
}

public class SignatureRequestDto
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    /// <summary>Draft, Sent, Completed, Declined, Expired or Cancelled (a partially signed request is still Sent).</summary>
    public string Status { get; set; } = "Draft";
    public string Provider { get; set; } = "";
    /// <summary>Sequential or Parallel.</summary>
    public string Order { get; set; } = "Parallel";
    public string Message { get; set; } = "";
    public List<Guid> DocumentIds { get; set; } = new();
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public List<SignerDto> Signers { get; set; } = new();
    public Guid? SignedDocumentId { get; set; }
    // Extras (not in types.ts)
    public Guid CreatedById { get; set; }
    /// <summary>How many signers have signed so far (the stored status PartiallySigned is reported as Sent).</summary>
    public int SignedCount { get; set; }
    /// <summary>Why the request ended (decline or cancel reason), or null.</summary>
    public string? StatusMessage { get; set; }
}

public class SignerInput
{
    public string? Kind { get; set; }
    public Guid? UserId { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public int? Order { get; set; }
}

public class SignatureRequestInput
{
    public List<Guid>? DocumentIds { get; set; }
    public List<SignerInput>? Signers { get; set; }
    public string? Order { get; set; }
    public string? Message { get; set; }
}

public class SimulateSignatureInput
{
    public Guid? SignerId { get; set; }
    /// <summary>View, Sign or Decline.</summary>
    public string? Action { get; set; }
    public string? Reason { get; set; }
}

public class CancelSignatureInput
{
    public string? Reason { get; set; }
}

public class SignaturePermissionsDto
{
    public bool CanSend { get; set; }
    public string? Reason { get; set; }
    public List<PersonOptionDto> PermittedUsers { get; set; } = new();
}

// ---------- storage (CONTRACT_V4 section 9a) ----------

public class StorageHealthDto
{
    /// <summary>Local or SharePoint.</summary>
    public string Mode { get; set; } = "Local";
    /// <summary>Healthy, Degraded, Down or NotConfigured.</summary>
    public string State { get; set; } = "Healthy";
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastFailureAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CheckedAt { get; set; }
    /// <summary>Failed calls in the last 24 hours.</summary>
    public int RecentFailures { get; set; }
    public List<string> Details { get; set; } = new();
}

public class ReconciliationItemDto
{
    /// <summary>MissingFile, Orphan, Mismatch or MisplacedFolder.</summary>
    public string Kind { get; set; } = "";
    public Guid? ContractId { get; set; }
    public string? ContractNo { get; set; }
    public Guid? DocumentId { get; set; }
    public string Path { get; set; } = "";
    public string Detail { get; set; } = "";
}

public class ReconciliationReportDto
{
    public Guid Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>Running, Completed or Failed.</summary>
    public string Status { get; set; } = "Running";
    public string? Error { get; set; }
    public int CheckedDocuments { get; set; }
    public int CheckedFolders { get; set; }
    public List<ReconciliationItemDto> Items { get; set; } = new();
}
