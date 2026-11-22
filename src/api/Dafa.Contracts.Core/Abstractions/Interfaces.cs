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

/// <summary>Where a contract's files belong: site per entity, library per department, folder per contract.</summary>
public sealed record DocumentLocation(Guid ContractId, string ContractNo, string EntityName, string DepartmentName);

public sealed record StoredDocument(string StoreKey, long Size);

public interface IDocumentStore
{
    Task<StoredDocument> SaveAsync(DocumentLocation location, string fileName, int version, Stream content, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string storeKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default);
    /// <summary>Link to the contract's folder in the store, or null when the store has no web UI.</summary>
    Task<string?> GetFolderUrlAsync(DocumentLocation location, CancellationToken cancellationToken = default);
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

public interface ISignatureProvider
{
    string Name { get; }
    /// <summary>False means contracts are marked as signed manually.</summary>
    bool IsEnabled { get; }
    /// <summary>Starts an e-signature flow and returns the provider's envelope id.</summary>
    Task<string> SendForSignatureAsync(Guid contractId, string documentName, Stream document, CancellationToken cancellationToken = default);
}

/// <summary>Fills Word templates. Implemented with DocumentFormat.OpenXml in Infrastructure.</summary>
public interface ITemplateEngine
{
    IReadOnlyList<string> GetPlaceholders(byte[] template);
    /// <summary>A minimal .docx with the standard placeholders, for a template created without a file.</summary>
    byte[] CreateStarter(string title);
    byte[] Fill(byte[] template, IReadOnlyDictionary<string, string> fields);
}
