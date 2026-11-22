using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Due-diligence data room export (CONTRACT_V3 section 6): a zip with index.csv, manifest.json and
/// the documents of the visible contracts that match a filter. Only Admin, group management and
/// entity management may export; the contract set always goes through the access filter, so the
/// zip never holds a document the caller could not open one by one. Built synchronously (the API
/// answers 202 with status Ready or Failed), kept 7 days, audited once.
/// </summary>
public class DataRoomService
{
    public const int MaxDocuments = 5000;
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IContractAccessService _access;
    private readonly IClock _clock;
    private readonly IDocumentStore _store;
    private readonly IExportStorage _storage;
    private readonly AuditService _audit;

    public DataRoomService(
        IAppDbContext db, ICurrentUser currentUser, IContractAccessService access, IClock clock,
        IDocumentStore store, IExportStorage storage, AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _access = access;
        _clock = clock;
        _store = store;
        _storage = storage;
        _audit = audit;
    }

    public static DataRoomExportDto ToDto(DataRoomExport row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Status = row.Status,
        ContractCount = row.ContractCount,
        DocumentCount = row.DocumentCount,
        SizeBytes = row.SizeBytes,
        CreatedAt = row.CreatedAt,
        CreatedBy = row.CreatedByName,
        FileName = row.FileName,
        Error = row.Error,
        ExpiresAt = row.ExpiresAt
    };

    private async Task<AccessProfile> AllowedAsync(CancellationToken cancellationToken)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (!PersonalService.CanExportDataRoom(profile))
        {
            throw new ForbiddenException("Data room exports are for administrators, group management and entity management.");
        }
        return profile;
    }

    public static string SafeName(string text, int max = 100)
    {
        var builder = new StringBuilder();
        foreach (var ch in text ?? "")
        {
            builder.Append(char.IsControl(ch) || "/\\:*?\"<>|".IndexOf(ch) >= 0 ? '_' : ch);
        }
        var name = builder.ToString().Trim().TrimEnd('.');
        if (name.Length > max)
        {
            name = name.Substring(0, max).Trim();
        }
        return name.Length == 0 ? "_" : name;
    }

    public async Task<DataRoomExportDto> CreateAsync(DataRoomInput input, CancellationToken cancellationToken = default)
    {
        var profile = await AllowedAsync(cancellationToken);
        var errors = new Dictionary<string, string[]>();
        var name = (input?.Name ?? "").Trim();
        if (name.Length < 1 || name.Length > 80) ContractValidator.Add(errors, "name", "Name is required (at most 80 characters).");
        var documents = input?.Documents ?? "All";
        if (documents != "All" && documents != "MainOnly" && documents != "None") ContractValidator.Add(errors, "documents", "Use All, MainOnly or None.");
        var versions = input?.Versions ?? "Latest";
        if (versions != "Latest" && versions != "All") ContractValidator.Add(errors, "versions", "Use Latest or All.");
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        var filter = ContractFilterParser.FromJson(input!.Filter);
        filter.Page = null;
        filter.PageSize = null;
        filter.Columns = new List<string>();

        var now = _clock.UtcNow;
        var row = new DataRoomExport
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = "Running",
            FilterJson = input.Filter is null ? "{}" : input.Filter.Value.GetRawText(),
            Documents = documents,
            Versions = versions,
            FileName = SafeName(name, 80) + " " + now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".zip",
            CreatedById = profile.UserId,
            CreatedByName = profile.Name,
            CreatedAt = now
        };
        _db.DataRoomExports.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await BuildAsync(row, filter, profile, cancellationToken);
            row.Status = "Ready";
            row.ExpiresAt = _clock.UtcNow.Add(Lifetime);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DataRoomLimitException || ex is IOException || ex is InvalidDataException)
        {
            row.Status = "Failed";
            row.Error = ex.Message.Length > 1000 ? ex.Message.Substring(0, 1000) : ex.Message;
            if (row.StorageKey is not null)
            {
                try { await _storage.DeleteAsync(row.StorageKey, cancellationToken); } catch (IOException) { }
                row.StorageKey = null;
            }
        }
        _audit.Record("dataroom.exported",
            "Data room export \"" + row.Name + "\": " + row.Status + ", " + row.ContractCount + " contracts, " + row.DocumentCount + " documents"
                + (row.Error is null ? "" : " (" + row.Error + ")"));
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(row);
    }

    private sealed class DataRoomLimitException : Exception
    {
        public DataRoomLimitException(string message) : base(message) { }
    }

    private async Task BuildAsync(DataRoomExport row, ContractFilter filter, AccessProfile profile, CancellationToken cancellationToken)
    {
        var engine = new ContractFilterEngine(_db, _access, _currentUser, _clock);
        var run = await engine.RunAsync(filter, true, cancellationToken);
        var contracts = run.Items;
        var reference = run.Reference;
        await reference.LoadExtrasAsync(_db, contracts.Select(c => c.Id).ToList(), profile.UserId, cancellationToken);
        row.ContractCount = contracts.Count;

        // Documents of exactly these (visible) contracts.
        var documentRows = new List<ContractDocument>();
        if (row.Documents != "None")
        {
            foreach (var chunk in contracts.Select(c => c.Id).Chunk(500))
            {
                var ids = chunk.ToList();
                var query = _db.Documents.AsNoTracking().Where(d => ids.Contains(d.ContractId));
                if (row.Documents == "MainOnly")
                {
                    query = query.Where(d => d.Kind == DocumentKind.Main);
                }
                documentRows.AddRange(await query.ToListAsync(cancellationToken));
            }
            if (row.Versions == "Latest")
            {
                documentRows = documentRows
                    .GroupBy(d => (d.ContractId, d.Name.ToLowerInvariant()))
                    .Select(g => g.OrderByDescending(d => d.Version).First())
                    .ToList();
            }
        }
        if (documentRows.Count > MaxDocuments)
        {
            throw new DataRoomLimitException("The export would hold " + documentRows.Count + " documents; the limit is " + MaxDocuments + ". Narrow the filter.");
        }
        if (documentRows.Sum(d => d.Size) > MaxBytes)
        {
            throw new DataRoomLimitException("The export would be larger than 2 GB. Narrow the filter or export the main documents only.");
        }

        var byContract = contracts.ToDictionary(c => c.Id);
        var manifestDocuments = new List<Dictionary<string, object?>>();
        var documentNames = new Dictionary<Guid, List<string>>();
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        row.StorageKey = "dataroom/" + row.Id.ToString("N") + ".zip";
        long total = 0;
        await using (var output = await _storage.CreateAsync(row.StorageKey, cancellationToken))
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
            foreach (var document in documentRows.OrderBy(d => byContract[d.ContractId].ContractNo).ThenBy(d => d.Name).ThenBy(d => d.Version))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var contract = byContract[document.ContractId];
                var folder = "documents/" + SafeName(contract.ContractNo + " – " + contract.Title);
                var fileName = SafeName(row.Versions == "All" ? "v" + document.Version + " – " + document.Name : document.Name, 150);
                var path = folder + "/" + fileName;
                var suffix = 2;
                while (!usedPaths.Add(path))
                {
                    path = folder + "/" + SafeName(Path.GetFileNameWithoutExtension(fileName) + " (" + suffix++ + ")" + Path.GetExtension(fileName), 150);
                }
                byte[] bytes;
                await using (var source = await _store.OpenReadAsync(document.StoreKey, cancellationToken))
                using (var buffer = new MemoryStream())
                {
                    await source.CopyToAsync(buffer, cancellationToken);
                    bytes = buffer.ToArray();
                }
                total += bytes.LongLength;
                if (total > MaxBytes)
                {
                    throw new DataRoomLimitException("The export grew larger than 2 GB. Narrow the filter.");
                }
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using (var entryStream = entry.Open())
                {
                    entryStream.Write(bytes, 0, bytes.Length);
                }
                manifestDocuments.Add(new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["contractNo"] = contract.ContractNo,
                    ["kind"] = document.Kind.ToString(),
                    ["version"] = document.Version,
                    ["size"] = bytes.LongLength,
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
                });
                if (!documentNames.TryGetValue(contract.Id, out var names))
                {
                    names = new List<string>();
                    documentNames[contract.Id] = names;
                }
                names.Add(document.Name);
            }

            // index.csv: every register column and every custom field, plus the document names.
            var columns = ContractQueryService.ExportColumns.Select(c => c.Key)
                .Concat(reference.CustomFields.OrderBy(d => d.SortOrder).Select(d => "cf." + d.Key))
                .ToList();
            var csv = new StringBuilder();
            csv.AppendLine(string.Join(",", columns.Select(c => ContractQueryService.CsvCell(ContractQueryService.ColumnHeader(c, reference))).Append("documents")));
            foreach (var contract in contracts)
            {
                var cells = columns.Select(c => ContractQueryService.CsvCell(ContractQueryService.ColumnValue(c, contract, reference))).ToList();
                cells.Add(ContractQueryService.CsvCell(string.Join("; ", documentNames.TryGetValue(contract.Id, out var names) ? names : new List<string>())));
                csv.AppendLine(string.Join(",", cells));
            }
            var index = zip.CreateEntry("index.csv", CompressionLevel.Optimal);
            using (var indexStream = index.Open())
            {
                var bytes = ContractQueryService.WithBom(csv.ToString());
                indexStream.Write(bytes, 0, bytes.Length);
            }

            JsonElement filterElement;
            using (var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.FilterJson) ? "{}" : row.FilterJson))
            {
                filterElement = parsed.RootElement.Clone();
            }
            var manifest = new Dictionary<string, object?>
            {
                ["name"] = row.Name,
                ["createdAt"] = row.CreatedAt,
                ["createdBy"] = row.CreatedByName,
                ["filter"] = filterElement,
                ["contracts"] = contracts.Count,
                ["documents"] = manifestDocuments
            };
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var manifestStream = manifestEntry.Open())
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
                manifestStream.Write(bytes, 0, bytes.Length);
            }
        }
        row.DocumentCount = manifestDocuments.Count;
        await using (var stored = await _storage.OpenReadAsync(row.StorageKey, cancellationToken))
        {
            row.SizeBytes = stored?.Length;
        }
    }

    public async Task<List<DataRoomExportDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var profile = await AllowedAsync(cancellationToken);
        var userId = profile.UserId;
        var query = _db.DataRoomExports.AsNoTracking();
        if (!profile.IsAdmin)
        {
            query = query.Where(e => e.CreatedById == userId);
        }
        var rows = await query.ToListAsync(cancellationToken);
        return rows.OrderByDescending(r => r.CreatedAt).Select(ToDto).ToList();
    }

    private async Task<DataRoomExport> OwnAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await AllowedAsync(cancellationToken);
        var row = await _db.DataRoomExports.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (row is null || (!profile.IsAdmin && row.CreatedById != profile.UserId))
        {
            throw new NotFoundException("Export not found.");
        }
        return row;
    }

    public async Task<DataRoomExportDto> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ToDto(await OwnAsync(id, cancellationToken));

    public async Task<DocumentContent> OpenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await OwnAsync(id, cancellationToken);
        if (row.Status != "Ready" || row.StorageKey is null || (row.ExpiresAt is not null && row.ExpiresAt.Value <= _clock.UtcNow))
        {
            throw new ConflictException("The export is not ready (or has expired).");
        }
        var stream = await _storage.OpenReadAsync(row.StorageKey, cancellationToken)
            ?? throw new ConflictException("The export file is no longer available.");
        _audit.Record("dataroom.downloaded", "Downloaded data room export \"" + row.Name + "\"");
        await _db.SaveChangesAsync(cancellationToken);
        return new DocumentContent(stream, row.FileName, "application/zip");
    }

    /// <summary>DataRoomCleanup job: deletes expired exports' files. Returns how many were removed.</summary>
    public static async Task<int> CleanupAsync(IAppDbContext db, IExportStorage storage, IClock clock, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var expired = await db.DataRoomExports.Where(e => e.StorageKey != null && e.ExpiresAt != null && e.ExpiresAt < now).ToListAsync(cancellationToken);
        foreach (var row in expired)
        {
            try
            {
                await storage.DeleteAsync(row.StorageKey!, cancellationToken);
            }
            catch (IOException)
            {
                // Try again next run.
                continue;
            }
            row.StorageKey = null;
            row.Status = "Failed";
            row.Error = "Expired";
        }
        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return expired.Count;
    }
}
