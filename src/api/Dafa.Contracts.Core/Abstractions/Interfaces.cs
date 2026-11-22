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

public sealed record Notification(Guid RecipientId, string Kind, string Subject, string Body, Guid? ContractId);

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
