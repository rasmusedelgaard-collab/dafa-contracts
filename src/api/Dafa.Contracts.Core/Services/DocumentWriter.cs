using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Stores a file in the document store and adds the Document row to the current unit of work.
/// No access checks here; callers (DocumentService, approvals, intake, seeding) do those.
/// </summary>
public class DocumentWriter
{
    private readonly IAppDbContext _db;
    private readonly IDocumentStore _store;
    private readonly IClock _clock;

    public DocumentWriter(IAppDbContext db, IDocumentStore store, IClock clock)
    {
        _db = db;
        _store = store;
        _clock = clock;
    }

    public static string SafeFileName(string? fileName)
    {
        var name = (fileName ?? "").Trim();
        var cut = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (cut >= 0)
        {
            name = name.Substring(cut + 1);
        }
        var chars = name.Select(ch => char.IsControl(ch) || "<>:\"|?*".IndexOf(ch) >= 0 ? '_' : ch).ToArray();
        name = new string(chars).Trim().TrimStart('.');
        if (name.Length > 120)
        {
            var extension = Path.GetExtension(name);
            name = name.Substring(0, 120 - extension.Length) + extension;
        }
        return name.Length == 0 ? "document" : name;
    }

    public static string ContentTypeFor(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".txt" => "text/plain; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
    }

    public async Task<DocumentLocation> GetLocationAsync(Contract contract, CancellationToken cancellationToken = default)
    {
        var entityName = await _db.LegalEntities.AsNoTracking()
            .Where(e => e.Id == contract.EntityId).Select(e => e.Name).FirstOrDefaultAsync(cancellationToken);
        var departmentName = await _db.Departments.AsNoTracking()
            .Where(d => d.Id == contract.DepartmentId).Select(d => d.Name).FirstOrDefaultAsync(cancellationToken);
        return new DocumentLocation(contract.Id, contract.ContractNo, entityName ?? "", departmentName ?? "");
    }

    public async Task<ContractDocument> AddAsync(
        Contract contract, string fileName, DocumentKind kind, Stream content, Guid? uploadedById,
        CancellationToken cancellationToken = default)
    {
        var name = SafeFileName(fileName);
        var contractId = contract.Id;
        var versions = await _db.Documents.AsNoTracking()
            .Where(d => d.ContractId == contractId && d.Name == name)
            .Select(d => d.Version)
            .ToListAsync(cancellationToken);
        var version = versions.Count == 0 ? 1 : versions.Max() + 1;

        var location = await GetLocationAsync(contract, cancellationToken);
        var stored = await _store.SaveAsync(location, name, version, content, cancellationToken);

        var document = new ContractDocument
        {
            Id = Guid.NewGuid(),
            ContractId = contract.Id,
            Name = name,
            Kind = kind,
            StoreKey = stored.StoreKey,
            Version = version,
            Size = stored.Size,
            UploadedById = uploadedById,
            UploadedAt = _clock.UtcNow
        };
        _db.Documents.Add(document);
        return document;
    }

    public Task<ContractDocument> AddAsync(
        Contract contract, string fileName, DocumentKind kind, byte[] content, Guid? uploadedById,
        CancellationToken cancellationToken = default)
    {
        return AddAsync(contract, fileName, kind, new MemoryStream(content, false), uploadedById, cancellationToken);
    }
}
