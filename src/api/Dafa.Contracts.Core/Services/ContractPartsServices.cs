using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

public class ObligationService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IContractAccessService _access;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public ObligationService(IAppDbContext db, ICurrentUser currentUser, IContractAccessService access, IClock clock, AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _access = access;
        _clock = clock;
        _audit = audit;
    }

    public async Task<List<ObligationDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await _access.GetReadableAsync(contractId, false, cancellationToken);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var obligations = await _db.Obligations.AsNoTracking().Where(o => o.ContractId == contractId).ToListAsync(cancellationToken);
        return obligations
            .OrderBy(o => o.Status)
            .ThenBy(o => o.DueDate ?? DateOnly.MaxValue)
            .Select(o => ContractQueryService.ToObligationDto(o, reference))
            .ToList();
    }

    /// <summary>Owner, delegate, responsible and administrators manage obligations; so does the obligation's own responsible.</summary>
    private async Task<Contract> RequireManageAsync(Guid contractId, Obligation? obligation, CancellationToken cancellationToken)
    {
        var contract = await _access.GetReadableAsync(contractId, false, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var allowed = profile.IsAdmin
            || ContractAccessRules.IsOnContract(profile, contract)
            || (obligation is not null && obligation.ResponsibleId == profile.UserId);
        if (!allowed)
        {
            throw new ForbiddenException("Only the people on the contract or an administrator can change its obligations.");
        }
        return contract;
    }

    private async Task<Guid> ValidateAsync(ObligationInput input, Contract contract, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(input.Description))
            ContractValidator.Add(errors, "description", "Description is required.");
        if (input.FrequencyMonths is not null && (input.FrequencyMonths.Value < 1 || input.FrequencyMonths.Value > 120))
            ContractValidator.Add(errors, "frequencyMonths", "Frequency must be between 1 and 120 months.");
        if (input.FrequencyMonths is not null && input.DueDate is null)
            ContractValidator.Add(errors, "dueDate", "A recurring obligation needs a first due date.");

        if (input.Status is not null && !Enum.IsDefined(typeof(ObligationStatus), input.Status.Value))
            ContractValidator.Add(errors, "status", "Unknown status.");

        var responsibleId = input.ResponsibleId ?? contract.ResponsibleId ?? contract.OwnerId;
        var responsible = await AccessProfileLoader.LoadAsync(_db, responsibleId, cancellationToken);
        if (responsible is null)
            ContractValidator.Add(errors, "responsibleId", "Unknown or inactive user.");
        else if (!ContractAccessRules.CanRead(responsible, contract))
            ContractValidator.Add(errors, "responsibleId",
                "This user cannot see the contract, so they could not complete the obligation. Choose someone with access, or make them responsible on the contract first.");

        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        return responsibleId;
    }

    public async Task<ObligationDto> CreateAsync(Guid contractId, ObligationInput input, CancellationToken cancellationToken = default)
    {
        var contract = await RequireManageAsync(contractId, null, cancellationToken);
        var responsibleId = await ValidateAsync(input, contract, cancellationToken);
        var obligation = new Obligation
        {
            Id = Guid.NewGuid(),
            ContractId = contract.Id,
            Description = input.Description!.Trim(),
            DueDate = input.DueDate,
            FrequencyMonths = input.FrequencyMonths,
            ResponsibleId = responsibleId,
            Status = ObligationStatus.Open
        };
        _db.Obligations.Add(obligation);
        _audit.Record("obligation.created", "Added obligation: " + obligation.Description, contract.Id);
        await _db.SaveChangesAsync(cancellationToken);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        return ContractQueryService.ToObligationDto(obligation, reference);
    }

    private async Task<Obligation> FindAsync(Guid obligationId, CancellationToken cancellationToken)
    {
        var obligation = await _db.Obligations.FirstOrDefaultAsync(o => o.Id == obligationId, cancellationToken);
        if (obligation is null)
        {
            throw new NotFoundException("Obligation not found.");
        }
        return obligation;
    }

    public async Task<ObligationDto> UpdateAsync(Guid obligationId, ObligationInput input, CancellationToken cancellationToken = default)
    {
        var obligation = await FindAsync(obligationId, cancellationToken);
        var contract = await RequireManageAsync(obligation.ContractId, obligation, cancellationToken);
        var responsibleId = await ValidateAsync(input, contract, cancellationToken);
        obligation.Description = input.Description!.Trim();
        obligation.DueDate = input.DueDate;
        obligation.FrequencyMonths = input.FrequencyMonths;
        obligation.ResponsibleId = responsibleId;
        if (input.Status is not null)
        {
            obligation.Status = input.Status.Value;
        }
        _audit.Record("obligation.updated", "Updated obligation: " + obligation.Description, contract.Id);
        await _db.SaveChangesAsync(cancellationToken);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        return ContractQueryService.ToObligationDto(obligation, reference);
    }

    public async Task DeleteAsync(Guid obligationId, CancellationToken cancellationToken = default)
    {
        var obligation = await FindAsync(obligationId, cancellationToken);
        var contract = await RequireManageAsync(obligation.ContractId, obligation, cancellationToken);
        var reminders = await _db.Reminders.Where(r => r.ObligationId == obligationId).ToListAsync(cancellationToken);
        _db.Reminders.RemoveRange(reminders);
        _db.Obligations.Remove(obligation);
        _audit.Record("obligation.deleted", "Deleted obligation: " + obligation.Description, contract.Id);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Marks done. A recurring obligation stays open and moves its due date one period forward.</summary>
    public async Task<ObligationDto> CompleteAsync(Guid obligationId, CancellationToken cancellationToken = default)
    {
        var obligation = await FindAsync(obligationId, cancellationToken);
        var contract = await RequireManageAsync(obligation.ContractId, obligation, cancellationToken);
        ObligationRules.Complete(obligation, _clock.UtcNow);
        _audit.Record("obligation.completed", "Completed obligation: " + obligation.Description, contract.Id);
        await _db.SaveChangesAsync(cancellationToken);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        return ContractQueryService.ToObligationDto(obligation, reference);
    }
}

public static class ObligationRules
{
    public static void Complete(Obligation obligation, DateTime nowUtc)
    {
        obligation.LastCompletedAt = nowUtc;
        if (obligation.FrequencyMonths is > 0 && obligation.DueDate is not null)
        {
            obligation.DueDate = obligation.DueDate.Value.AddMonths(obligation.FrequencyMonths.Value);
            obligation.Status = ObligationStatus.Open;
        }
        else
        {
            obligation.Status = ObligationStatus.Done;
        }
    }
}

public class LinkService
{
    private readonly IAppDbContext _db;
    private readonly IContractAccessService _access;
    private readonly AuditService _audit;

    public LinkService(IAppDbContext db, IContractAccessService access, AuditService audit)
    {
        _db = db;
        _access = access;
        _audit = audit;
    }

    public async Task<LinkDto> CreateAsync(Guid contractId, LinkInput input, CancellationToken cancellationToken = default)
    {
        var from = await _access.GetEditableAsync(contractId, cancellationToken);
        if (input.ToContractId is null)
        {
            throw new InputValidationException("toContractId", "Choose the contract to link to.");
        }
        if (input.Kind is null)
        {
            throw new InputValidationException("kind", "Choose the kind of link.");
        }
        var toId = input.ToContractId.Value;
        if (toId == from.Id)
        {
            throw new InputValidationException("toContractId", "A contract cannot be linked to itself.");
        }
        var to = await _access.GetReadableAsync(toId, false, cancellationToken);
        var kind = input.Kind.Value;
        if (!Enum.IsDefined(typeof(LinkKind), kind))
        {
            throw new InputValidationException("kind", "Choose the kind of link.");
        }
        var fromId = from.Id;
        var exists = await _db.ContractLinks.AnyAsync(l => l.FromId == fromId && l.ToId == toId && l.Kind == kind, cancellationToken);
        if (exists)
        {
            throw new ConflictException("This link already exists.", ProblemTypes.Duplicate);
        }
        var link = new ContractLink { Id = Guid.NewGuid(), FromId = from.Id, ToId = to.Id, Kind = kind };
        _db.ContractLinks.Add(link);
        // The other contract's number is left out: readers of this contract may not be allowed to see it.
        _audit.Record("link.created", "Linked to another contract (" + kind + ")", from.Id);
        await _db.SaveChangesAsync(cancellationToken);
        return new LinkDto { Id = link.Id, ContractId = to.Id, Title = to.Title, Kind = kind.ToString(), Direction = "out" };
    }

    public async Task DeleteAsync(Guid linkId, CancellationToken cancellationToken = default)
    {
        var link = await _db.ContractLinks.FirstOrDefaultAsync(l => l.Id == linkId, cancellationToken);
        if (link is null)
        {
            throw new NotFoundException("Link not found.");
        }
        var from = await _access.GetEditableAsync(link.FromId, cancellationToken);
        _db.ContractLinks.Remove(link);
        _audit.Record("link.deleted", "Removed a link (" + link.Kind + ")", from.Id);
        await _db.SaveChangesAsync(cancellationToken);
    }
}

public sealed record DocumentContent(Stream Stream, string FileName, string ContentType);

public class DocumentService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IContractAccessService _access;
    private readonly IDocumentStore _store;
    private readonly DocumentWriter _writer;
    private readonly AuditService _audit;

    public DocumentService(
        IAppDbContext db, ICurrentUser currentUser, IContractAccessService access, IDocumentStore store,
        DocumentWriter writer, AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _access = access;
        _store = store;
        _writer = writer;
        _audit = audit;
    }

    public async Task<DocumentDto> UploadAsync(Guid contractId, string fileName, string? kind, Stream content, CancellationToken cancellationToken = default)
    {
        var contract = await _access.GetReadableAsync(contractId, true, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (!profile.IsAdmin && !ContractAccessRules.IsOnContract(profile, contract))
        {
            throw new ForbiddenException("Only the people on the contract or an administrator can add documents.");
        }
        var documentKind = DocumentKind.Other;
        if (!string.IsNullOrWhiteSpace(kind) && !Enum.TryParse(kind.Trim(), true, out documentKind))
        {
            throw new InputValidationException("kind", "Unknown document kind.");
        }
        if (!Enum.IsDefined(typeof(DocumentKind), documentKind))
        {
            throw new InputValidationException("kind", "Unknown document kind.");
        }
        if (documentKind == DocumentKind.ApprovalLog)
        {
            throw new InputValidationException("kind", "Approval logs are created by the system.");
        }

        // M6: size, allowlisted type and matching content.
        var name = DocumentWriter.SafeFileName(fileName);
        var bytes = await ReadLimitedAsync(content, FileTypeRules.MaxDocumentBytes, cancellationToken);
        FileTypeRules.Validate(name, bytes);

        // The main document is what was approved. From submission on, only an administrator may
        // add a Main document or a new version of an existing one.
        var replacesMain = false;
        if (ApprovedTerms.AppliesTo(contract.Status))
        {
            var contractKey = contract.Id;
            var sameNameIsMain = await _db.Documents.AsNoTracking()
                .AnyAsync(d => d.ContractId == contractKey && d.Name == name && d.Kind == DocumentKind.Main, cancellationToken);
            replacesMain = documentKind == DocumentKind.Main || sameNameIsMain;
            if (replacesMain && !profile.IsAdmin)
            {
                throw new ConflictException(
                    "The main document is locked once the contract is submitted for approval. Upload the file as an amendment or appendix under another name, or ask an administrator.",
                    ProblemTypes.LockedFields);
            }
        }

        var document = await _writer.AddAsync(contract, name, documentKind, bytes, profile.UserId, cancellationToken);
        _audit.Record("document.uploaded", "Uploaded " + document.Name + " (v" + document.Version + ", " + document.Kind + ")", contract.Id);
        if (replacesMain)
        {
            _audit.Record("contract.approved-terms-changed",
                "Administrator added main document " + document.Name + " (v" + document.Version + ") after submission (status " + contract.Status + ").",
                contract.Id);
        }
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique index on (contract, name, version): the same name was uploaded at the same moment.
            _db.DiscardChanges();
            throw new ConflictException("A file with the same name was uploaded at the same time. Try again.", ProblemTypes.Stale);
        }
        return new DocumentDto
        {
            Id = document.Id,
            Name = document.Name,
            Kind = document.Kind.ToString(),
            Version = document.Version,
            Size = document.Size,
            UploadedAt = document.UploadedAt,
            UploadedBy = profile.Name
        };
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }
            if (buffer.Length + read > maxBytes)
            {
                throw new InputValidationException("file", "The file is larger than 25 MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public async Task<DocumentContent> OpenAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            throw new NotFoundException("Document not found.");
        }
        // Access to a file follows access to its contract.
        var contract = await _access.GetReadableAsync(document.ContractId, false, cancellationToken);
        var stream = await _store.OpenReadAsync(document.StoreKey, cancellationToken);
        _audit.Record("document.opened", "Opened " + document.Name, contract.Id);
        await _db.SaveChangesAsync(cancellationToken);
        return new DocumentContent(stream, document.Name, DocumentWriter.ContentTypeFor(document.Name));
    }
}

public class IntakeService
{
    public const long MaxUploadBytes = 25 * 1024 * 1024;

    private static readonly string[] AllowedExtensions = { ".pdf", ".docx" };

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IContractAnalyzer _analyzer;
    private readonly ITemplateEngine _templates;
    private readonly AuditService _audit;

    public IntakeService(
        IAppDbContext db, ICurrentUser currentUser, IClock clock, IContractAnalyzer analyzer,
        ITemplateEngine templates, AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _analyzer = analyzer;
        _templates = templates;
        _audit = audit;
    }

    private async Task<AccessProfile> RequireCreatorAsync(CancellationToken cancellationToken)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (!ContractAccessRules.CanCreate(profile))
        {
            throw new ForbiddenException("You need the contract owner role to create contracts.");
        }
        return profile;
    }

    public async Task<ExtractResultDto> ExtractAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        var profile = await RequireCreatorAsync(cancellationToken);
        var name = DocumentWriter.SafeFileName(fileName);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            throw new InputValidationException("file", "Upload a PDF or a Word document (.docx).");
        }
        if (content.Length > 0 && content.LongLength <= MaxUploadBytes && !FileTypeRules.ContentMatches(name, content))
        {
            throw new InputValidationException("file", "The content of the file does not match its file type.");
        }
        var creatorId = profile.UserId;
        var pending = await _db.PendingUploads.CountAsync(u => u.CreatedById == creatorId, cancellationToken);
        if (pending >= ContractCommandService.MaxPendingUploadsPerUser)
        {
            throw new InputValidationException("file",
                "You have too many uploaded files that are not attached to a contract yet. Finish or discard a draft first; unused uploads are removed after 7 days.");
        }
        if (content.Length == 0)
        {
            throw new InputValidationException("file", "The file is empty.");
        }
        if (content.LongLength > MaxUploadBytes)
        {
            throw new InputValidationException("file", "The file is larger than 25 MB.");
        }

        var analysis = await _analyzer.AnalyzeAsync(name, content, cancellationToken);
        var upload = new PendingUpload
        {
            Id = Guid.NewGuid(),
            FileName = name,
            Size = content.LongLength,
            Content = content,
            Summary = string.IsNullOrWhiteSpace(analysis.Summary) ? null : analysis.Summary,
            DeviationsJson = JsonDefaults.Write(analysis.Deviations),
            CreatedById = profile.UserId,
            CreatedAt = _clock.UtcNow
        };
        _db.PendingUploads.Add(upload);
        _audit.Record("intake.extracted", "Analyzed uploaded file " + name);
        await _db.SaveChangesAsync(cancellationToken);

        return new ExtractResultDto
        {
            UploadId = upload.Id,
            FileName = name,
            Size = upload.Size,
            Pages = analysis.Pages,
            Suggestions = analysis.Suggestions,
            Summary = analysis.Summary,
            Deviations = analysis.Deviations,
            Note = analysis.Note
        };
    }

    public static TemplateDto ToDto(ContractTemplate template)
    {
        return new TemplateDto
        {
            Id = template.Id,
            Name = template.Name,
            Description = template.Description ?? "",
            TypeId = template.TypeId,
            FileName = template.FileName,
            Placeholders = (template.Placeholders ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            IsActive = template.IsActive
        };
    }

    public async Task<List<TemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await _currentUser.GetProfileAsync(cancellationToken);
        var templates = await _db.Templates.AsNoTracking().Where(t => t.IsActive).ToListAsync(cancellationToken);
        return templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(ToDto).ToList();
    }

    public async Task<FromTemplateResultDto> FromTemplateAsync(FromTemplateInput input, CancellationToken cancellationToken = default)
    {
        var profile = await RequireCreatorAsync(cancellationToken);
        if (input.TemplateId is null)
        {
            throw new InputValidationException("templateId", "Choose a template.");
        }
        var templateId = input.TemplateId.Value;
        var template = await _db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken);
        if (template is null || !template.IsActive)
        {
            throw new NotFoundException("Template not found.");
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (input.Fields is not null)
        {
            foreach (var pair in input.Fields)
            {
                fields[pair.Key] = pair.Value ?? "";
            }
        }
        var filled = _templates.Fill(template.Content, fields);

        var title = fields.TryGetValue("title", out var value) && !string.IsNullOrWhiteSpace(value) ? value : template.Name;
        var fileName = DocumentWriter.SafeFileName(title + " (draft).docx");
        var upload = new PendingUpload
        {
            Id = Guid.NewGuid(),
            FileName = fileName,
            Size = filled.LongLength,
            Content = filled,
            CreatedById = profile.UserId,
            CreatedAt = _clock.UtcNow
        };
        _db.PendingUploads.Add(upload);
        _audit.Record("intake.template", "Created a draft from template " + template.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return new FromTemplateResultDto { UploadId = upload.Id, FileName = fileName };
    }
}
