namespace Dafa.Contracts.Core.Domain;

// Release 4 persistence model (docs/CONTRACT_V4.md). Same rules as the earlier files: plain Guid
// foreign keys, no navigation properties, no cascades, DateTime in UTC. Everything here is also
// created on existing databases by schema step 3 in Infrastructure/Data/SchemaUpgrader.cs.
// New columns on existing tables (LegalEntity, Department, AppUser, ContractTemplate) are in Entities.cs.

/// <summary>
/// Status of an e-signature request. Stored as int. The first three are the ACTIVE statuses: the
/// filtered unique index "one active request per contract" relies on them being 0, 1 and 2, so
/// never reorder; append only.
/// </summary>
public enum SignatureRequestStatus { Draft, Sent, PartiallySigned, Completed, Declined, Cancelled, Expired }

/// <summary>Parallel: everyone is invited at once. Sequential: one signatory at a time, in order.</summary>
public enum SignatureOrderMode { Parallel, Sequential }

/// <summary>
/// Status of one signatory. Stored as int; append only. Waiting = not invited yet (a later
/// signatory of a Sequential request, or a request that is still a Draft).
/// </summary>
public enum SignatoryStatus { Waiting, Sent, Viewed, Signed, Declined }

/// <summary>
/// Who may create counterparties from the contract form (general setting counterpartyCreation).
/// API names are the member names.
/// </summary>
public enum CounterpartyCreation { AdminOnly, ContractOwners, AllUsers }

/// <summary>Names of the e-signature providers (e-sign setting provider).</summary>
public static class SignatureProviderNames
{
    public const string None = "None";
    public const string Simulated = "Simulated";
    public const string DocuSign = "DocuSign";
    public const string Penneo = "Penneo";

    public static readonly string[] All = { None, Simulated, DocuSign, Penneo };
}

/// <summary>Link state of a legal entity or department (LinkStatus column; API LinkStatus.state).</summary>
public static class LinkStates
{
    /// <summary>Nothing has been entered, or the mapping changed since the last test.</summary>
    public const string NotLinked = "NotLinked";
    /// <summary>
    /// SharePoint mode: the site, the libraries and the group were found. Local mode: the values
    /// are well-formed and saved; they are used once the app runs in SharePoint mode.
    /// </summary>
    public const string Linked = "Linked";
    /// <summary>The last test failed (site not found, library or group missing, malformed value).</summary>
    public const string Error = "Error";

    public static string Normalize(string? stored) =>
        stored == Linked || stored == Error ? stored : NotLinked;
}

public class TemplateCategory
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// The organisation's logo ("logo") or compact icon ("icon"). One row per kind; the bytes are small
/// (max 1 MB) and live in the database so every app instance and every deployment serves the same file.
/// </summary>
public class BrandingAsset
{
    /// <summary>logo or icon.</summary>
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    /// <summary>image/png, image/jpeg, image/webp or image/svg+xml: decided from the content, never from the upload's own header.</summary>
    public string ContentType { get; set; } = "";
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public long Size { get; set; }
    /// <summary>Hex SHA-256 of the content (first 32 characters): the ETag and the cache-busting version.</summary>
    public string ETag { get; set; } = "";
    public Guid? UpdatedById { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// SharePoint hardening: one run of the storage reconciliation (report only, nothing is repaired).
/// ItemsJson is a JSON array of { kind, contractId, contractNo, documentId, path, detail }.
/// </summary>
public class StorageReconciliationReport
{
    public Guid Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>Running, Completed or Failed.</summary>
    public string Status { get; set; } = "Running";
    public string? Error { get; set; }
    public int CheckedDocuments { get; set; }
    public int CheckedFolders { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string StartedByName { get; set; } = "";
}

/// <summary>One e-signature round of a contract. At most one active (Draft, Sent, PartiallySigned) per contract.</summary>
public class SignatureRequest : IVersioned
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
    public Guid ContractId { get; set; }
    /// <summary>Simulated, DocuSign or Penneo: the provider the request was sent with.</summary>
    public string Provider { get; set; } = "";
    /// <summary>The provider's envelope / case file id.</summary>
    public string? ProviderReference { get; set; }
    public SignatureRequestStatus Status { get; set; } = SignatureRequestStatus.Draft;
    public SignatureOrderMode OrderMode { get; set; } = SignatureOrderMode.Parallel;
    public string? Message { get; set; }
    /// <summary>JSON array of the ids of the contract documents that were sent.</summary>
    public string DocumentIdsJson { get; set; } = "[]";
    public Guid CreatedById { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>When the request expires if it is not completed.</summary>
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastReminderAt { get; set; }
    public DateTime? LastPolledAt { get; set; }
    /// <summary>The Document created from the signed file when the request completed.</summary>
    public Guid? SignedDocumentId { get; set; }
    /// <summary>Why the request ended (decline reason, cancel reason, "Expired"), or the last provider error.</summary>
    public string? StatusMessage { get; set; }
}

/// <summary>One person who signs: an internal user (UserId set) or an external name and e-mail address.</summary>
public class Signatory
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid ContractId { get; set; }
    public Guid? UserId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>1-based signing order (meaningful for Sequential; display order for Parallel).</summary>
    public int SortOrder { get; set; }
    public SignatoryStatus Status { get; set; } = SignatoryStatus.Waiting;
    public DateTime? SentAt { get; set; }
    public DateTime? ViewedAt { get; set; }
    public DateTime? SignedAt { get; set; }
    public DateTime? DeclinedAt { get; set; }
    public string? DeclineReason { get; set; }
}
