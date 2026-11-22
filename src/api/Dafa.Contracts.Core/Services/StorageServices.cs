using System.Diagnostics;
using System.Security.Cryptography;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// SharePoint hardening (CONTRACT_V4 section 9a): the storage health page and the reconciliation
/// report for administrators. Reconciliation only REPORTS; it never moves, renames or deletes.
/// </summary>
public class StorageService
{
    /// <summary>A run stops looking at more documents after this long and reports what it has.</summary>
    public static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(50);
    public const int MaxItems = 2000;

    private readonly IAppDbContext _db;
    private readonly IDocumentStore _store;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly ICurrentUser _currentUser;
    private readonly RuntimeModes _modes;
    private readonly StorageHealthState _health;

    public StorageService(
        IAppDbContext db, IDocumentStore store, IClock clock, AuditService audit, ICurrentUser currentUser,
        RuntimeModes? modes = null, StorageHealthState? health = null)
    {
        _db = db;
        _store = store;
        _clock = clock;
        _audit = audit;
        _currentUser = currentUser;
        _modes = modes ?? new RuntimeModes();
        _health = health ?? new StorageHealthState();
    }

    // ---------- health ----------

    /// <summary>GET admin/storage/health: what the Graph client has seen lately plus one cheap call right now.</summary>
    public async Task<StorageHealthDto> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var result = new StorageHealthDto
        {
            Mode = _modes.IsSharePoint ? "SharePoint" : "Local",
            CheckedAt = now,
            State = "Healthy"
        };
        if (!_modes.IsSharePoint)
        {
            result.Details.Add("Documents are stored on the server's own disk (test mode). SharePoint is not used.");
        }
        else
        {
            var entities = await _db.LegalEntities.AsNoTracking().Where(e => e.IsActive).ToListAsync(cancellationToken);
            var unlinked = entities.Where(e => string.IsNullOrWhiteSpace(e.SharePointSiteUrl)).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (unlinked.Count > 0)
            {
                result.Details.Add("No SharePoint site is linked to: " + string.Join(", ", unlinked) + ".");
            }
            if (unlinked.Count == entities.Count)
            {
                result.State = "NotConfigured";
            }
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var probe = await _store.ProbeAsync(timeout.Token);
            if (!string.IsNullOrWhiteSpace(probe.Detail))
            {
                result.Details.Add(probe.Detail!);
            }
            if (!probe.Ok && result.State != "NotConfigured")
            {
                result.State = "Down";
            }
        }
        catch (StorageNotConfiguredException ex)
        {
            result.State = "NotConfigured";
            result.Details.Add(ex.Message);
        }
        catch (Exception ex) when (ex is StorageUnavailableException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            result.State = "Down";
            result.Details.Add("The storage did not answer the health check.");
        }
        result.LastSuccessAt = _health.LastSuccessAt;
        result.LastFailureAt = _health.LastFailureAt;
        result.LastError = _health.LastError;
        result.RecentFailures = _health.RecentFailures(DateTime.UtcNow);
        if (_health.BreakerState != "Closed")
        {
            result.Details.Add("The circuit breaker is " + (_health.BreakerState == "Open" ? "open: calls are paused for a moment after repeated failures." : "half open: the next call decides."));
            if (result.State == "Healthy")
            {
                result.State = _health.BreakerState == "Open" ? "Down" : "Degraded";
            }
        }
        else if (result.State == "Healthy" && _health.LastFailureAt is not null
            && DateTime.UtcNow - _health.LastFailureAt.Value < TimeSpan.FromHours(1)
            && (_health.LastSuccessAt is null || _health.LastSuccessAt.Value < _health.LastFailureAt.Value))
        {
            // The most recent call failed within the last hour.
            result.State = "Degraded";
        }
        var pendingLogs = await _db.ApprovalInstances.AsNoTracking().CountAsync(i => i.LogPending, cancellationToken);
        if (pendingLogs > 0)
        {
            result.Details.Add(pendingLogs + (pendingLogs == 1 ? " approval log is" : " approval logs are") + " waiting to be stored.");
        }
        var retrying = await _db.DocumentTexts.AsNoTracking().CountAsync(t => t.Status == "Retry", cancellationToken);
        if (retrying > 0)
        {
            result.Details.Add(retrying + (retrying == 1 ? " document is" : " documents are") + " waiting to be read again for search.");
        }
        return result;
    }

    // ---------- reconciliation ----------

    private static ReconciliationReportDto ToDto(StorageReconciliationReport report) => new()
    {
        Id = report.Id,
        StartedAt = report.StartedAt,
        FinishedAt = report.FinishedAt,
        Status = report.Status,
        Error = report.Error,
        CheckedDocuments = report.CheckedDocuments,
        CheckedFolders = report.CheckedFolders,
        Items = JsonDefaults.ReadList<ReconciliationItemDto>(report.ItemsJson)
    };

    /// <summary>GET admin/storage/reconciliation: the latest report, or null when none has run.</summary>
    public async Task<ReconciliationReportDto?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var latest = await _db.StorageReconciliationReports.AsNoTracking().OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(cancellationToken);
        return latest is null ? null : ToDto(latest);
    }

    private static string PathOf(Contract? contract, ContractDocument document) =>
        !string.IsNullOrWhiteSpace(document.RelativePath)
            ? document.RelativePath!
            : (contract is null ? "" : contract.ContractNo + "/") + document.Name + (document.Version > 1 ? " (v" + document.Version + ")" : "");

    /// <summary>
    /// POST admin/storage/reconciliation/run and the nightly job. Compares the document rows with
    /// the store and reports:
    ///   MissingFile      a row whose file is gone;
    ///   Mismatch         size, content (hash, or the store's content tag) or name differs from the row;
    ///   Orphan           a file in a contract folder that no row points to;
    ///   MisplacedFolder  the contract's files are not on its legal entity's site / in its department's library.
    /// Nothing is repaired. The run is synchronous and bounded (RunBudget); what was not reached is
    /// said in the report's error text.
    /// </summary>
    public async Task<ReconciliationReportDto> RunAsync(CancellationToken cancellationToken = default)
    {
        var report = new StorageReconciliationReport
        {
            Id = Guid.NewGuid(),
            StartedAt = _clock.UtcNow,
            Status = "Running",
            StartedByName = string.IsNullOrWhiteSpace(_currentUser.UserName) ? "System" : _currentUser.UserName!
        };
        _db.StorageReconciliationReports.Add(report);
        await _db.SaveChangesAsync(cancellationToken);

        var items = new List<ReconciliationItemDto>();
        var watch = Stopwatch.StartNew();
        try
        {
            var contracts = (await _db.Contracts.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(c => c.Id);
            var entities = (await _db.LegalEntities.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(e => e.Id);
            var departments = (await _db.Departments.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(d => d.Id);
            var documents = (await _db.Documents.AsNoTracking().ToListAsync(cancellationToken))
                .OrderBy(d => d.UploadedAt).ToList();
            var stoppedEarly = false;

            foreach (var document in documents)
            {
                if (watch.Elapsed > RunBudget || items.Count >= MaxItems)
                {
                    stoppedEarly = true;
                    break;
                }
                contracts.TryGetValue(document.ContractId, out var contract);
                report.CheckedDocuments++;
                void Add(string kind, string detail) => items.Add(new ReconciliationItemDto
                {
                    Kind = kind,
                    ContractId = contract?.Id,
                    ContractNo = contract?.ContractNo,
                    DocumentId = document.Id,
                    Path = PathOf(contract, document),
                    Detail = detail
                });
                try
                {
                    var item = await _store.GetItemAsync(document.StoreKey, cancellationToken);
                    if (item is not null)
                    {
                        // The store knows the item: compare what it reports with the row.
                        if (item.Size != document.Size)
                            Add("Mismatch", "The file is " + item.Size + " bytes in storage but " + document.Size + " bytes were uploaded.");
                        else if (!string.IsNullOrEmpty(document.CTag) && !string.IsNullOrEmpty(item.CTag) && item.CTag != document.CTag)
                            Add("Mismatch", "The content of the file was changed in storage after it was uploaded.");
                        var expectedName = string.IsNullOrWhiteSpace(document.RelativePath) ? null : document.RelativePath!.Split('/').Last();
                        if (expectedName is not null && !string.Equals(expectedName, item.Name, StringComparison.OrdinalIgnoreCase))
                            Add("Mismatch", "The file was renamed in storage to \"" + item.Name + "\".");
                    }
                    else
                    {
                        // The store cannot describe items (local disk): read the file and compare size and hash.
                        await using var stream = await _store.OpenReadAsync(document.StoreKey, cancellationToken);
                        using var sha = SHA256.Create();
                        var buffer = new byte[81920];
                        long size = 0;
                        int read;
                        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                        {
                            sha.TransformBlock(buffer, 0, read, null, 0);
                            size += read;
                        }
                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        var hash = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
                        if (size != document.Size)
                            Add("Mismatch", "The file is " + size + " bytes in storage but " + document.Size + " bytes were uploaded.");
                        else if (!string.IsNullOrEmpty(document.ContentHash) && hash != document.ContentHash)
                            Add("Mismatch", "The content of the file differs from what was uploaded (SHA-256).");
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is StorageFileMissingException || ex is KeyNotFoundException)
                {
                    Add("MissingFile", "The document is registered but its file is not in storage.");
                }
            }

            // Where the files are against where the contract now belongs (SharePoint mode only: rows carry a site id).
            foreach (var group in documents.Where(d => !string.IsNullOrEmpty(d.SiteId)).GroupBy(d => d.ContractId))
            {
                if (!contracts.TryGetValue(group.Key, out var contract))
                {
                    continue;
                }
                entities.TryGetValue(contract.EntityId, out var entity);
                departments.TryGetValue(contract.DepartmentId, out var department);
                var expectedLibrary = department is null ? null : OrgLinkRules.LibraryOf(department);
                var wrongSite = group.Where(d => entity is not null && !string.IsNullOrEmpty(entity.SharePointSiteId) && d.SiteId != entity.SharePointSiteId).ToList();
                var wrongLibrary = group.Where(d => expectedLibrary is not null && !string.IsNullOrWhiteSpace(d.RelativePath)
                    && !string.Equals(d.RelativePath!.Split('/')[0], expectedLibrary, StringComparison.OrdinalIgnoreCase)).ToList();
                if (wrongSite.Count > 0 || wrongLibrary.Count > 0)
                {
                    var first = wrongSite.Concat(wrongLibrary).First();
                    items.Add(new ReconciliationItemDto
                    {
                        Kind = "MisplacedFolder",
                        ContractId = contract.Id,
                        ContractNo = contract.ContractNo,
                        DocumentId = null,
                        Path = string.IsNullOrWhiteSpace(first.RelativePath) ? contract.ContractNo : string.Join("/", first.RelativePath!.Split('/').Take(2)),
                        Detail = (wrongSite.Count + wrongLibrary.Count) + " file(s) of this contract are stored under "
                            + (wrongSite.Count > 0 ? "another legal entity's site" : "another department's library")
                            + " than the contract now belongs to (" + (entity?.Name ?? "?") + " / " + (expectedLibrary ?? "?") + "). Move them by hand if needed."
                    });
                }
            }

            // Files in contract folders that no row points to (stores with folders only).
            foreach (var contract in contracts.Values.Where(c => !string.IsNullOrEmpty(c.StoreDriveId) && !string.IsNullOrEmpty(c.StoreFolderId)))
            {
                if (watch.Elapsed > RunBudget || items.Count >= MaxItems)
                {
                    stoppedEarly = true;
                    break;
                }
                report.CheckedFolders++;
                var known = documents.Where(d => d.ContractId == contract.Id && !string.IsNullOrEmpty(d.ItemId)).Select(d => d.ItemId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var file in await _store.ListFolderAsync(contract.StoreDriveId!, contract.StoreFolderId!, cancellationToken))
                    {
                        if (!known.Contains(file.ItemId))
                        {
                            items.Add(new ReconciliationItemDto
                            {
                                Kind = "Orphan",
                                ContractId = contract.Id,
                                ContractNo = contract.ContractNo,
                                Path = contract.ContractNo + "/" + file.Name,
                                Detail = "A file in the contract's folder that the app does not know (" + file.Size + " bytes)."
                            });
                        }
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is StorageFileMissingException)
                {
                    items.Add(new ReconciliationItemDto
                    {
                        Kind = "MissingFile",
                        ContractId = contract.Id,
                        ContractNo = contract.ContractNo,
                        Path = contract.ContractNo,
                        Detail = "The contract's folder is not in storage any more."
                    });
                }
            }

            report.Status = "Completed";
            report.Error = stoppedEarly
                ? "Stopped after " + (int)watch.Elapsed.TotalSeconds + " s or " + MaxItems + " findings: " + report.CheckedDocuments + " of " + documents.Count + " documents were checked. Run it again to continue from the oldest."
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The store went away in the middle of the run (or anything else): keep what was found.
            report.Status = "Failed";
            var message = ex is StorageUnavailableException unavailable ? StorageUnavailableException.UserMessage + " " + unavailable.TechnicalDetail : ex.Message;
            report.Error = message.Length > 1000 ? message.Substring(0, 1000) : message;
        }
        report.FinishedAt = _clock.UtcNow;
        report.ItemsJson = JsonDefaults.Write(items);
        _audit.Record("storage.reconciled", "Storage reconciliation " + report.Status.ToLowerInvariant() + ": " + report.CheckedDocuments + " documents and "
            + report.CheckedFolders + " folders checked, " + items.Count + " findings");
        if (items.Count > 0 && report.Status == "Completed")
        {
            await StorageAlerts.NotifyAdminsAsync(_db, null,
                "The storage reconciliation found " + items.Count + " thing(s) to look at: "
                    + string.Join(", ", items.GroupBy(i => i.Kind).Select(g => g.Count() + " " + g.Key)) + ". See Settings > Storage.",
                cancellationToken);
        }
        // Keep the last 30 reports.
        var old = await _db.StorageReconciliationReports.OrderByDescending(r => r.StartedAt).Skip(30).ToListAsync(cancellationToken);
        _db.StorageReconciliationReports.RemoveRange(old);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(report);
    }
}
