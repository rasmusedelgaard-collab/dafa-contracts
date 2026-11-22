using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Dtos;
using Dafa.Contracts.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Api.Controllers;

[Route("api/contracts")]
public class ContractsController : ApiControllerBase
{
    private const long MaxDocumentBytes = FileTypeRules.MaxDocumentBytes;

    private readonly ContractQueryService _queries;
    private readonly ContractCommandService _commands;
    private readonly DocumentService _documents;
    private readonly ObligationService _obligations;
    private readonly LinkService _links;

    public ContractsController(
        ContractQueryService queries, ContractCommandService commands, DocumentService documents,
        ObligationService obligations, LinkService links)
    {
        _queries = queries;
        _commands = commands;
        _documents = documents;
        _obligations = obligations;
        _links = links;
    }

    /// <summary>Release 3: the shared ContractFilter (repeatable parameters, cf.&lt;key&gt;), sort and paging.</summary>
    [HttpGet("")]
    public async Task<ActionResult<PagedDto<ContractListItemDto>>> List(CancellationToken cancellationToken)
    {
        return await _queries.ListAsync(QueryFilter.From(Request), cancellationToken);
    }

    /// <summary>Same filter; optional repeatable columns (register column keys or cf.&lt;key&gt;).</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var bytes = await _queries.ExportCsvAsync(QueryFilter.From(Request), cancellationToken);
        return File(bytes, "text/csv; charset=utf-8", "dafa-contracts.csv");
    }

    /// <summary>peek=true reads the contract without recording it as recently opened.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContractDetailDto>> Get(Guid id, [FromQuery] bool? peek, CancellationToken cancellationToken)
    {
        return await _queries.GetDetailAsync(id, peek != true, cancellationToken);
    }

    [HttpPost("")]
    public async Task<ActionResult<ContractDetailDto>> Create([FromBody] ContractInput input, CancellationToken cancellationToken)
    {
        var id = await _commands.CreateAsync(input, cancellationToken);
        var detail = await _queries.GetDetailAsync(id, false, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id }, detail);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ContractDetailDto>> Update(Guid id, [FromBody] ContractInput input, CancellationToken cancellationToken)
    {
        await _commands.UpdateAsync(id, input, cancellationToken);
        return await _queries.GetDetailAsync(id, false, cancellationToken);
    }

    [HttpPost("{id:guid}/submit")]
    public async Task<ActionResult<ContractDetailDto>> Submit(Guid id, CancellationToken cancellationToken)
    {
        await _commands.SubmitAsync(id, cancellationToken);
        return await _queries.GetDetailAsync(id, false, cancellationToken);
    }

    [HttpPost("{id:guid}/sign")]
    public async Task<ActionResult<ContractDetailDto>> Sign(Guid id, [FromBody] SignInput input, CancellationToken cancellationToken)
    {
        await _commands.SignAsync(id, input, cancellationToken);
        return await _queries.GetDetailAsync(id, false, cancellationToken);
    }

    [HttpPost("{id:guid}/renewal-decision")]
    public async Task<ActionResult<ContractDetailDto>> RenewalDecision(Guid id, [FromBody] RenewalDecisionInput input, CancellationToken cancellationToken)
    {
        await _commands.DecideRenewalAsync(id, input, cancellationToken);
        return await _queries.GetDetailAsync(id, false, cancellationToken);
    }

    [HttpPost("{id:guid}/documents")]
    [RequestSizeLimit(30L * 1024 * 1024)]
    public async Task<ActionResult<ContractDetailDto>> UploadDocument(Guid id, IFormFile? file, [FromForm] string? kind, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new InputValidationException("file", "Choose a file to upload.");
        }
        if (file.Length > MaxDocumentBytes)
        {
            throw new InputValidationException("file", "The file is larger than 25 MB.");
        }
        await using (var stream = file.OpenReadStream())
        {
            await _documents.UploadAsync(id, file.FileName, kind, stream, cancellationToken);
        }
        return await _queries.GetDetailAsync(id, false, cancellationToken);
    }

    [HttpGet("{id:guid}/obligations")]
    public async Task<ActionResult<List<ObligationDto>>> Obligations(Guid id, CancellationToken cancellationToken)
    {
        return await _obligations.ListAsync(id, cancellationToken);
    }

    [HttpPost("{id:guid}/obligations")]
    public async Task<ActionResult<ObligationDto>> CreateObligation(Guid id, [FromBody] ObligationInput input, CancellationToken cancellationToken)
    {
        return await _obligations.CreateAsync(id, input, cancellationToken);
    }

    [HttpPost("{id:guid}/links")]
    public async Task<ActionResult<ContractDetailDto>> CreateLink(Guid id, [FromBody] LinkInput input, CancellationToken cancellationToken)
    {
        await _links.CreateAsync(id, input, cancellationToken);
        return await _queries.GetDetailAsync(id, false, cancellationToken);
    }
}

[Route("api")]
public class ContractPartsController : ApiControllerBase
{
    private readonly DocumentService _documents;
    private readonly ObligationService _obligations;
    private readonly LinkService _links;

    public ContractPartsController(DocumentService documents, ObligationService obligations, LinkService links)
    {
        _documents = documents;
        _obligations = obligations;
        _links = links;
    }

    [HttpGet("documents/{id:guid}/content")]
    public async Task<IActionResult> DocumentContent(Guid id, CancellationToken cancellationToken)
    {
        var content = await _documents.OpenAsync(id, cancellationToken);
        return File(content.Stream, content.ContentType, content.FileName);
    }

    [HttpPut("obligations/{id:guid}")]
    public async Task<ActionResult<ObligationDto>> UpdateObligation(Guid id, [FromBody] ObligationInput input, CancellationToken cancellationToken)
    {
        return await _obligations.UpdateAsync(id, input, cancellationToken);
    }

    [HttpDelete("obligations/{id:guid}")]
    public async Task<IActionResult> DeleteObligation(Guid id, CancellationToken cancellationToken)
    {
        await _obligations.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("obligations/{id:guid}/complete")]
    public async Task<ActionResult<ObligationDto>> CompleteObligation(Guid id, CancellationToken cancellationToken)
    {
        return await _obligations.CompleteAsync(id, cancellationToken);
    }

    [HttpDelete("links/{id:guid}")]
    public async Task<IActionResult> DeleteLink(Guid id, CancellationToken cancellationToken)
    {
        await _links.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}

[Route("api")]
public class IntakeController : ApiControllerBase
{
    private readonly IntakeService _intake;
    private readonly Dafa.Contracts.Core.Abstractions.IAppDbContext _db;
    private readonly TemplateCatalogService _templates;

    public IntakeController(IntakeService intake, Dafa.Contracts.Core.Abstractions.IAppDbContext db, TemplateCatalogService templates)
    {
        _intake = intake;
        _db = db;
        _templates = templates;
    }

    [HttpPost("intake/extract")]
    [RequestSizeLimit(30L * 1024 * 1024)]
    public async Task<ActionResult<ExtractResultDto>> Extract(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new InputValidationException("file", "Choose a PDF or Word document to upload.");
        }
        if (file.Length > IntakeService.MaxUploadBytes)
        {
            throw new InputValidationException("file", "The file is larger than 25 MB.");
        }
        var bytes = await ReadAllAsync(file, cancellationToken);
        var result = await _intake.ExtractAsync(file.FileName, bytes, cancellationToken);
        var settings = await AppSettingsStore.LoadAsync(_db, cancellationToken);
        if (!settings.Features.AiSuggestions)
        {
            // Release 3 feature toggle: the file is kept, but nothing is suggested.
            var upload = await _db.PendingUploads.FirstOrDefaultAsync(u => u.Id == result.UploadId, cancellationToken);
            if (upload is not null)
            {
                upload.Summary = null;
                upload.DeviationsJson = null;
                await _db.SaveChangesAsync(cancellationToken);
            }
            result.Suggestions.Clear();
            result.Deviations.Clear();
            result.Summary = "";
            result.Note = "AI suggestions are turned off";
        }
        return result;
    }

    /// <summary>
    /// Release 4: active templates in active categories, by category order then name. Optional
    /// filters (empty = no filter): categoryId, typeId, language, q.
    /// </summary>
    [HttpGet("templates")]
    public async Task<ActionResult<List<TemplateDto>>> Templates(
        [FromQuery] Guid? categoryId, [FromQuery] Guid? typeId, [FromQuery] string? language, [FromQuery] string? q, CancellationToken cancellationToken)
    {
        var settings = await AppSettingsStore.LoadAsync(_db, cancellationToken);
        var templates = await _templates.ListAsync(categoryId, typeId, language, q, cancellationToken);
        return settings.Features.Templates ? templates : new List<TemplateDto>();
    }

    [HttpPost("intake/from-template")]
    public async Task<ActionResult<FromTemplateResultDto>> FromTemplate([FromBody] FromTemplateInput input, CancellationToken cancellationToken)
    {
        var settings = await AppSettingsStore.LoadAsync(_db, cancellationToken);
        if (!settings.Features.Templates)
        {
            throw new FeatureOffException("Templates are turned off in General settings.");
        }
        return await _intake.FromTemplateAsync(input, cancellationToken);
    }
}

[Route("api/approvals")]
public class ApprovalsController : ApiControllerBase
{
    private readonly ContractQueryService _queries;
    private readonly ApprovalService _approvals;

    public ApprovalsController(ContractQueryService queries, ApprovalService approvals)
    {
        _queries = queries;
        _approvals = approvals;
    }

    /// <summary>Release 4: ?entityId= (repeatable) is the entity scope (the contract's entity).</summary>
    [HttpGet("mine")]
    public async Task<ActionResult<List<MyApprovalDto>>> Mine(CancellationToken cancellationToken)
    {
        return await _queries.MyApprovalsAsync(EntityScope.From(Request), cancellationToken);
    }

    [HttpPost("steps/{stepId:guid}/decision")]
    public async Task<IActionResult> Decide(Guid stepId, [FromBody] StepDecisionInput input, CancellationToken cancellationToken)
    {
        if (input.Decision is null)
        {
            throw new InputValidationException("decision", "Choose Approve or Reject.");
        }
        await _approvals.DecideAsync(stepId, input.Decision.Value, input.Comment, cancellationToken);
        return NoContent();
    }

    [HttpPost("steps/{stepId:guid}/delegate")]
    public async Task<IActionResult> DelegateStep(Guid stepId, [FromBody] DelegateInput input, CancellationToken cancellationToken)
    {
        if (input.UserId is null)
        {
            throw new InputValidationException("userId", "Choose the user to delegate to.");
        }
        await _approvals.DelegateAsync(stepId, input.UserId.Value, cancellationToken);
        return NoContent();
    }

    [HttpGet("preview")]
    public async Task<ActionResult<ApprovalPreviewDto>> Preview(
        [FromQuery] Guid? entityId, [FromQuery] Guid? typeId, [FromQuery] Guid? departmentId,
        [FromQuery] decimal? value, [FromQuery] string? currency, CancellationToken cancellationToken)
    {
        return await _approvals.PreviewAsync(entityId, typeId, departmentId, value, currency, cancellationToken);
    }
}
