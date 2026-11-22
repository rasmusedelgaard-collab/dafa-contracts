using Dafa.Contracts.Core.Domain;

namespace Dafa.Contracts.Core.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
    DateOnly Today { get; }
}

/// <summary>
/// Timestamps are UTC. "Today" (notice deadlines, reminders, expiry) is the calendar date in the
/// business time zone, Europe/Copenhagen, so a deadline does not move at 01:00 or 02:00 at night.
/// Falls back to the UTC date when the host does not know the time zone.
/// </summary>
public sealed class UtcClock : IClock
{
    private static readonly TimeZoneInfo BusinessZone = FindBusinessZone();

    private static TimeZoneInfo FindBusinessZone()
    {
        foreach (var id in new[] { "Europe/Copenhagen", "Romance Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception)
            {
                // Not known on this host; try the next name.
            }
        }
        return TimeZoneInfo.Utc;
    }

    /// <summary>Europe/Copenhagen (or UTC when the host does not know it); used for schedules.</summary>
    public static TimeZoneInfo BusinessTimeZone => BusinessZone;

    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BusinessZone));
}

/// <summary>The signed-in user. Outside a web request (jobs, seeding) UserId is null.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? UserName { get; }

    /// <summary>Throws UnauthorizedException when there is no known, active user.</summary>
    Task<AccessProfile> GetProfileAsync(CancellationToken cancellationToken = default);
}

/// <summary>The single place where the access model (brief section 3) is enforced.</summary>
public interface IContractAccessService
{
    /// <summary>Restricts a contract query to what the current user may see.</summary>
    Task<IQueryable<Contract>> FilterAsync(IQueryable<Contract> query, CancellationToken cancellationToken = default);

    Task<bool> CanReadAsync(Contract contract, CancellationToken cancellationToken = default);

    /// <summary>Loads a contract; NotFoundException when missing, ForbiddenException when not visible.</summary>
    Task<Contract> GetReadableAsync(Guid contractId, bool tracked = false, CancellationToken cancellationToken = default);

    /// <summary>As GetReadableAsync (tracked), and ForbiddenException unless the user may edit.</summary>
    Task<Contract> GetEditableAsync(Guid contractId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where a contract's files belong: site per entity, library per department, folder per contract.
/// Release 4: the SharePoint mapping comes from the administration (LegalEntity.SharePointSiteUrl /
/// SharePointSiteId, Department.LibraryName) and travels with the location, so the document store
/// never guesses a site or a library from a naming convention or from configuration.
/// </summary>
public sealed record DocumentLocation(
    Guid ContractId, string ContractNo, string EntityName, string DepartmentName,
    string? SiteUrl = null, string? SiteId = null, string? LibraryName = null);

/// <summary>
/// The result of storing a file. Besides the key the store reports where the file went, so the
/// document row can carry a tenant-independent address (relative path, hash) next to the ids.
/// </summary>
public sealed record StoredDocument(string StoreKey, long Size)
{
    public string? SiteId { get; init; }
    public string? DriveId { get; init; }
    public string? ItemId { get; init; }
    /// <summary>Library and path inside it, e.g. "Purchasing/C-2026-0012/Agreement v2.pdf".</summary>
    public string? RelativePath { get; init; }
    public string? ETag { get; init; }
    public string? CTag { get; init; }
    /// <summary>The contract's folder, when the store has folders (created once, idempotently).</summary>
    public StoreFolder? Folder { get; init; }
}

/// <summary>A contract's folder in the store: ids are stored on the contract so no lookup is needed at read time.</summary>
public sealed record StoreFolder(string DriveId, string ItemId, string? WebUrl);

/// <summary>An item the store reports for reconciliation.</summary>
public sealed record StoreItem(string ItemId, string Name, long Size, string? ETag, string? CTag, string? ParentItemId, string? WebUrl);

/// <summary>The answer of a cheap "is the store reachable" call.</summary>
public sealed record StoreProbe(bool Ok, string? Detail);

public interface IDocumentStore
{
    /// <summary>
    /// Stores a new file. Never overwrites: a name that already exists in the contract's folder
    /// makes the store choose another name. Throws StorageNotConfiguredException when the location
    /// has no mapped site or library, StorageUnavailableException when the store cannot be reached.
    /// </summary>
    Task<StoredDocument> SaveAsync(DocumentLocation location, string fileName, int version, Stream content, CancellationToken cancellationToken = default);
    /// <summary>Opens the file as a stream (not buffered). Throws StorageFileMissingException or FileNotFoundException when it is gone.</summary>
    Task<Stream> OpenReadAsync(string storeKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default);
    /// <summary>
    /// Link to the contract's folder in the store, or null when the store has no web UI. Calls the
    /// store, so it is NOT used on the contract page: the link is stored on the contract at upload.
    /// </summary>
    Task<string?> GetFolderUrlAsync(DocumentLocation location, CancellationToken cancellationToken = default);

    /// <summary>The stored item behind a key (size, tags), or null when the store cannot tell. Throws when the file is gone.</summary>
    Task<StoreItem?> GetItemAsync(string storeKey, CancellationToken cancellationToken = default) => Task.FromResult<StoreItem?>(null);

    /// <summary>The files directly in a contract folder, for the reconciliation report. Empty when the store has no folders.</summary>
    Task<IReadOnlyList<StoreItem>> ListFolderAsync(string driveId, string folderItemId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoreItem>>(Array.Empty<StoreItem>());

    /// <summary>A cheap call that tells whether the store answers (storage health).</summary>
    Task<StoreProbe> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new StoreProbe(true, null));
}

/// <summary>
/// What the Graph client has seen lately: last success, last failure, recent failures and the
/// circuit breaker. One instance per app instance (singleton); in test mode nothing writes to it.
/// </summary>
public sealed class StorageHealthState
{
    private readonly object _lock = new();
    private readonly Queue<DateTime> _failures = new();

    public DateTime? LastSuccessAt { get; private set; }
    public DateTime? LastFailureAt { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>Closed, Open or HalfOpen.</summary>
    public string BreakerState { get; private set; } = "Closed";
    public DateTime? BreakerOpenUntil { get; private set; }
    /// <summary>Calls in a row that failed after all their retries (maintained by the Graph client).</summary>
    public int ConsecutiveFailures { get; set; }

    public void RecordSuccess(DateTime utcNow)
    {
        lock (_lock)
        {
            LastSuccessAt = utcNow;
        }
    }

    public void RecordFailure(DateTime utcNow, string? error)
    {
        lock (_lock)
        {
            LastFailureAt = utcNow;
            LastError = error is null ? null : (error.Length > 500 ? error.Substring(0, 500) : error);
            _failures.Enqueue(utcNow);
            Trim(utcNow);
        }
    }

    public void SetBreaker(string state, DateTime? openUntil)
    {
        lock (_lock)
        {
            BreakerState = state;
            BreakerOpenUntil = openUntil;
        }
    }

    /// <summary>Failures in the 24 hours before <paramref name="utcNow"/>.</summary>
    public int RecentFailures(DateTime utcNow)
    {
        lock (_lock)
        {
            Trim(utcNow);
            return _failures.Count;
        }
    }

    private void Trim(DateTime utcNow)
    {
        while (_failures.Count > 0 && (utcNow - _failures.Peek() > TimeSpan.FromHours(24) || _failures.Count > 10000))
        {
            _failures.Dequeue();
        }
    }
}

public sealed class FieldSuggestion
{
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
    public double Confidence { get; set; }
}

public sealed class Deviation
{
    public DeviationSeverity Severity { get; set; }
    public string Text { get; set; } = "";
    public string Standard { get; set; } = "";
}

public sealed class ContractAnalysis
{
    public int Pages { get; set; }
    public List<FieldSuggestion> Suggestions { get; set; } = new();
    public string Summary { get; set; } = "";
    public List<Deviation> Deviations { get; set; } = new();
    /// <summary>Set when the file could not be read within the extraction limits; there are then no suggestions.</summary>
    public string? Note { get; set; }
}

public interface IContractAnalyzer
{
    Task<ContractAnalysis> AnalyzeAsync(string fileName, byte[] content, CancellationToken cancellationToken = default);
}

public sealed record SupplierRecord(string ExternalId, string Name, string? Country, string? VatNo);

public interface ISupplierSource
{
    Task<IReadOnlyList<SupplierRecord>> GetSuppliersAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A notification to one natural recipient. Release 3: <paramref name="Data"/> carries extra
/// template values (deadline, days, step.role, comment, ...); the dispatcher adds the contract's.
/// </summary>
public sealed record Notification(Guid RecipientId, string Kind, string Subject, string Body, Guid? ContractId, IReadOnlyDictionary<string, string>? Data = null);

/// <summary>
/// Release 3: the delivery channel for e-mail (Outbox in test mode, Graph mail in production).
/// INotifier is the NotificationDispatcher, which applies the notification settings and
/// preferences and then hands e-mails to this channel.
/// </summary>
public interface IMailChannel
{
    Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken = default);
}

/// <summary>Release 3: where data room zips are kept (local folder in every mode).</summary>
public interface IExportStorage
{
    Task<Stream> CreateAsync(string key, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Release 3: plain text of a stored document, for search (bounded, never throws for a bad file).</summary>
public interface IDocumentTextExtractor
{
    Task<DocumentTextResult> ExtractAsync(string fileName, byte[] content, CancellationToken cancellationToken = default);
}

public sealed record DocumentTextResult(string Text, string? Note);

/// <summary>Release 3: reads the first worksheet of an .xlsx or a .csv file into rows of text.</summary>
public interface ITabularFileReader
{
    TabularData Read(string fileName, byte[] content);
}

public sealed class TabularData
{
    public List<string> Columns { get; set; } = new();
    public List<string[]> Rows { get; set; } = new();
}

/// <summary>Release 3: runs a sync source query (Power BI DAX or SQL) and returns rows of text.</summary>
public interface ISyncSourceReader
{
    /// <summary>Connection references known on the server (Sync:Connections:*), never with secrets.</summary>
    IReadOnlyList<(string Name, string Kind, string Description)> GetConnections();
    Task<TabularData> ReadAsync(SyncQuery query, int maxRows, CancellationToken cancellationToken = default);
}

public sealed class SyncQuery
{
    public string Kind { get; set; } = "";
    /// <summary>counterparties, users or contracts: used by the demo reader to pick demo rows.</summary>
    public string Target { get; set; } = "";
    public string? WorkspaceId { get; set; }
    public string? DatasetId { get; set; }
    public string? DaxQuery { get; set; }
    public string? Table { get; set; }
    public string? ConnectionName { get; set; }
    public string? SqlQuery { get; set; }
}

/// <summary>Release 3: links in notifications (App:BaseUrl).</summary>
public sealed class AppLinks
{
    public string BaseUrl { get; set; } = "";
}

/// <summary>
/// Sends or queues a notification. Implementations add rows to the current unit of work and
/// leave SaveChanges to the caller. Returns false when delivery was attempted and failed
/// (Graph mail); queueing in the outbox counts as success.
/// </summary>
public interface INotifier
{
    Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken = default);
}

public interface IDirectoryService
{
    /// <summary>Brings users and group memberships up to date. Returns the number of changed users.</summary>
    Task<int> SyncAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Release 4: an e-signature provider (Simulated, DocuSign, Penneo). The provider in use is chosen
/// by the administrator in the e-sign settings; credentials are server configuration
/// (Signing:&lt;Provider&gt;:*) and never pass through the API. All state the application needs is kept
/// in SignatureRequest / Signatory rows; a provider only talks to its service.
/// </summary>
public interface ISignatureProvider
{
    /// <summary>Simulated, DocuSign or Penneo (SignatureProviderNames).</summary>
    string Name { get; }
    /// <summary>False when the server configuration for this provider is missing; it then cannot be selected.</summary>
    bool IsConfigured { get; }
    /// <summary>The account the provider is configured with (an id, never a secret), for display.</summary>
    string? Account { get; }

    /// <summary>Creates the envelope and invites the first signer(s). Returns the provider's reference.</summary>
    Task<string> SendAsync(SignatureEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>What happened since the envelope was sent (polling). An empty list = nothing new to report.</summary>
    Task<IReadOnlyList<SignatureProviderEvent>> GetEventsAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>The signed document of a completed envelope.</summary>
    Task<SignedFile> GetSignedDocumentAsync(SignatureEnvelope envelope, string reference, CancellationToken cancellationToken = default);

    /// <summary>Reminds the signers who have been invited and have not signed.</summary>
    Task RemindAsync(string reference, CancellationToken cancellationToken = default);

    Task CancelAsync(string reference, string reason, CancellationToken cancellationToken = default);

    /// <summary>Turns a webhook body into events. Unknown or irrelevant bodies give an empty list.</summary>
    IReadOnlyList<SignatureProviderEvent> ParseWebhook(string body);
}

public sealed class SignatureEnvelope
{
    public Guid RequestId { get; set; }
    public Guid ContractId { get; set; }
    public string ContractNo { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Sequential { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public List<SignatureEnvelopeDocument> Documents { get; set; } = new();
    public List<SignatureEnvelopeSigner> Signers { get; set; } = new();
}

public sealed class SignatureEnvelopeDocument
{
    public string Name { get; set; } = "";
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

public sealed class SignatureEnvelopeSigner
{
    public Guid SignatoryId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public int Order { get; set; }
    public DateTime? SignedAt { get; set; }
}

/// <summary>Something a provider reports about an envelope (webhook or polling).</summary>
public sealed class SignatureProviderEvent
{
    public const string Viewed = "Viewed";
    public const string Signed = "Signed";
    public const string Declined = "Declined";
    /// <summary>The whole envelope is completed (every signer has signed).</summary>
    public const string Completed = "Completed";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";

    public string Reference { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid? SignatoryId { get; set; }
    public string? SignerEmail { get; set; }
    public DateTime? At { get; set; }
    public string? Reason { get; set; }
}

public sealed record SignedFile(string FileName, byte[] Content);

/// <summary>Release 4: finds a provider by name among the registered ones.</summary>
public sealed class SignatureProviderRegistry
{
    private readonly List<ISignatureProvider> _providers;

    public SignatureProviderRegistry(IEnumerable<ISignatureProvider> providers)
    {
        _providers = providers.ToList();
    }

    public IReadOnlyList<ISignatureProvider> All => _providers;

    public ISignatureProvider? Find(string? name) =>
        _providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// Release 4: how this installation runs, for the parts of Core that must tell test mode from
/// production mode. Registered by the host from configuration (Documents:Provider, Directory:Provider).
/// </summary>
public sealed class RuntimeModes
{
    /// <summary>Local or SharePoint (Documents:Provider).</summary>
    public string StorageMode { get; set; } = "Local";
    /// <summary>Seeded or Graph (Directory:Provider).</summary>
    public string DirectoryMode { get; set; } = "Seeded";

    public bool IsSharePoint => string.Equals(StorageMode, "SharePoint", StringComparison.OrdinalIgnoreCase);
    public bool IsGraphDirectory => string.Equals(DirectoryMode, "Graph", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A SharePoint site as Graph knows it.</summary>
public sealed record SiteInfo(string SiteId, string? WebUrl, string? DisplayName);

/// <summary>
/// Release 4: the lookups behind "test link". The Graph implementation is used when documents live
/// in SharePoint or the directory is Entra ID; the test-mode implementation answers nothing and
/// the link test then only validates formats.
/// </summary>
public interface IOrgLinkProbe
{
    /// <summary>Resolves https://tenant.sharepoint.com/sites/name to a site; null when it does not exist.</summary>
    Task<SiteInfo?> ResolveSiteAsync(string siteUrl, CancellationToken cancellationToken = default);
    /// <summary>Names of the document libraries of a site.</summary>
    Task<IReadOnlyList<string>> GetLibraryNamesAsync(string siteId, CancellationToken cancellationToken = default);
    /// <summary>Display name of an Entra group; null when it does not exist.</summary>
    Task<string?> GetGroupNameAsync(string groupId, CancellationToken cancellationToken = default);
}

/// <summary>Fills Word templates. Implemented with DocumentFormat.OpenXml in Infrastructure.</summary>
public interface ITemplateEngine
{
    IReadOnlyList<string> GetPlaceholders(byte[] template);
    /// <summary>A minimal .docx with the standard placeholders, for a template created without a file.</summary>
    byte[] CreateStarter(string title);
    byte[] Fill(byte[] template, IReadOnlyDictionary<string, string> fields);
}
