using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
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
        // SharePoint does not accept these: a leading ~ (temporary files), _vti_ anywhere, and the
        // reserved device names.
        name = name.TrimStart('~', '$', ' ');
        name = name.Replace("_vti_", "_vti-", StringComparison.OrdinalIgnoreCase);
        var stem = Path.GetFileNameWithoutExtension(name).TrimEnd('.', ' ').ToUpperInvariant();
        if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == ".LOCK" || stem == "DESKTOP"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3])))
        {
            name = "file " + name;
        }
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
            ".xls" => "application/vnd.ms-excel",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".msg" => "application/vnd.ms-outlook",
            ".eml" => "message/rfc822",
            ".txt" => "text/plain; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// Release 4: the location carries the SharePoint mapping of the contract's legal entity (site)
    /// and department (library) as the administrator linked them.
    /// </summary>
    public async Task<DocumentLocation> GetLocationAsync(Contract contract, CancellationToken cancellationToken = default)
    {
        var entity = await _db.LegalEntities.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == contract.EntityId, cancellationToken);
        var department = await _db.Departments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == contract.DepartmentId, cancellationToken);
        return new DocumentLocation(
            contract.Id, contract.ContractNo, entity?.Name ?? "", department?.Name ?? "",
            string.IsNullOrWhiteSpace(entity?.SharePointSiteUrl) ? null : entity!.SharePointSiteUrl,
            string.IsNullOrWhiteSpace(entity?.SharePointSiteId) ? null : entity!.SharePointSiteId,
            string.IsNullOrWhiteSpace(department?.LibraryName) ? null : department!.LibraryName);
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
        // Also count documents added in this unit of work that are not saved yet.
        versions.AddRange(_db.Documents.Local.Where(d => d.ContractId == contractId && d.Name == name).Select(d => d.Version));
        // A same-name upload is always a new version: the unique index on (contract, name, version)
        // refuses a second row, and the store never overwrites (Local: a random part in the key and
        // CreateNew; SharePoint: conflictBehavior=fail and a new name when the name is taken).
        var version = versions.Count == 0 ? 1 : versions.Max() + 1;

        // The bytes are hashed before they leave, so the row always knows what was uploaded.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var location = await GetLocationAsync(contract, cancellationToken);
        var stored = await _store.SaveAsync(location, name, version, new MemoryStream(bytes, false), cancellationToken);

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
            UploadedAt = _clock.UtcNow,
            SiteId = stored.SiteId,
            DriveId = stored.DriveId,
            ItemId = stored.ItemId,
            RelativePath = stored.RelativePath,
            ETag = stored.ETag,
            CTag = stored.CTag,
            ContentHash = hash
        };
        _db.Documents.Add(document);
        // The contract's folder is stored once (when the contract row is tracked), so reading a
        // contract never has to ask the store for it.
        if (stored.Folder is not null && string.IsNullOrEmpty(contract.StoreFolderId))
        {
            contract.StoreDriveId = stored.Folder.DriveId;
            contract.StoreFolderId = stored.Folder.ItemId;
            contract.StoreFolderUrl = stored.Folder.WebUrl;
        }
        return document;
    }

    public Task<ContractDocument> AddAsync(
        Contract contract, string fileName, DocumentKind kind, byte[] content, Guid? uploadedById,
        CancellationToken cancellationToken = default)
    {
        return AddAsync(contract, fileName, kind, new MemoryStream(content, false), uploadedById, cancellationToken);
    }
}

/// <summary>
/// M6: which files may be stored. Checked by extension and, where the format has a simple
/// signature, by the first bytes of the content, so an executable renamed to .pdf is refused.
/// </summary>
public static class FileTypeRules
{
    public const long MaxDocumentBytes = 25L * 1024 * 1024;

    public static readonly string[] AllowedExtensions =
    {
        ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".msg", ".eml", ".txt", ".png", ".jpg", ".jpeg"
    };

    public const string AllowedList = "PDF, Word, Excel, PowerPoint, Outlook message (.msg, .eml), text, PNG or JPEG";

    private static readonly byte[] Pdf = { 0x25, 0x50, 0x44, 0x46, 0x2D };                          // %PDF-
    private static readonly byte[] Zip = { 0x50, 0x4B, 0x03, 0x04 };                                // PK..
    private static readonly byte[] Ole = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };        // legacy Office, .msg
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF };
    private static readonly byte[] Exe = { 0x4D, 0x5A };                                            // MZ
    private static readonly byte[] Elf = { 0x7F, 0x45, 0x4C, 0x46 };

    public static bool IsAllowedExtension(string fileName) =>
        Array.IndexOf(AllowedExtensions, Path.GetExtension(fileName ?? "").ToLowerInvariant()) >= 0;

    private static bool StartsWith(byte[] content, byte[] signature)
    {
        if (content.Length < signature.Length)
        {
            return false;
        }
        for (var i = 0; i < signature.Length; i++)
        {
            if (content[i] != signature[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>True when the first bytes fit the extension. Text formats only have to not be a known binary format.</summary>
    public static bool ContentMatches(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        switch (extension)
        {
            case ".pdf":
                // Some PDFs have a few junk bytes before the header; look in the first 1024 bytes.
                return IndexOf(content, Pdf, 1024) >= 0;
            case ".docx":
            case ".xlsx":
            case ".pptx":
                return StartsWith(content, Zip);
            case ".doc":
            case ".xls":
            case ".msg":
                return StartsWith(content, Ole);
            case ".png":
                return StartsWith(content, Png);
            case ".jpg":
            case ".jpeg":
                return StartsWith(content, Jpeg);
            case ".txt":
            case ".eml":
                return !StartsWith(content, Exe) && !StartsWith(content, Elf) && !StartsWith(content, Zip)
                    && !StartsWith(content, Ole) && !StartsWith(content, Pdf);
            default:
                return false;
        }
    }

    private static int IndexOf(byte[] content, byte[] signature, int within)
    {
        var last = Math.Min(content.Length - signature.Length, within);
        for (var start = 0; start <= last; start++)
        {
            var match = true;
            for (var i = 0; i < signature.Length; i++)
            {
                if (content[start + i] != signature[i])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return start;
            }
        }
        return -1;
    }

    /// <summary>Throws a field error on "file" when the file may not be stored.</summary>
    public static void Validate(string fileName, byte[] content)
    {
        if (content.Length == 0)
        {
            throw new InputValidationException("file", "The file is empty.");
        }
        if (content.LongLength > MaxDocumentBytes)
        {
            throw new InputValidationException("file", "The file is larger than 25 MB.");
        }
        if (!IsAllowedExtension(fileName))
        {
            throw new InputValidationException("file", "This file type is not allowed. Upload a " + AllowedList + " file.");
        }
        if (!ContentMatches(fileName, content))
        {
            throw new InputValidationException("file", "The content of the file does not match its file type.");
        }
    }
}
