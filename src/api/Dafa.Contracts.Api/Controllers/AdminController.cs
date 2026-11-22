using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Dtos;
using Dafa.Contracts.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dafa.Contracts.Api.Controllers;

/// <summary>Settings. Role Admin only; the role comes from the database on every request.</summary>
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ApiControllerBase
{
    private readonly AdminService _admin;
    private readonly JobService _jobs;

    public AdminController(AdminService admin, JobService jobs)
    {
        _admin = admin;
        _jobs = jobs;
    }

    // ---------- approval rules ----------

    [HttpGet("approval-rules")]
    public async Task<ActionResult<List<ApprovalRuleDto>>> GetApprovalRules(CancellationToken cancellationToken)
    {
        return await _admin.GetApprovalRulesAsync(cancellationToken);
    }

    [HttpPost("approval-rules")]
    public async Task<ActionResult<ApprovalRuleDto>> CreateApprovalRule([FromBody] ApprovalRuleDto input, CancellationToken cancellationToken)
    {
        return await _admin.CreateApprovalRuleAsync(input, cancellationToken);
    }

    [HttpPut("approval-rules/{id:guid}")]
    public async Task<ActionResult<ApprovalRuleDto>> UpdateApprovalRule(Guid id, [FromBody] ApprovalRuleDto input, CancellationToken cancellationToken)
    {
        return await _admin.UpdateApprovalRuleAsync(id, input, cancellationToken);
    }

    [HttpDelete("approval-rules/{id:guid}")]
    public async Task<IActionResult> DeleteApprovalRule(Guid id, CancellationToken cancellationToken)
    {
        await _admin.DeleteApprovalRuleAsync(id, cancellationToken);
        return NoContent();
    }

    // ---------- retention rules ----------

    [HttpGet("retention-rules")]
    public async Task<ActionResult<List<RetentionRuleDto>>> GetRetentionRules(CancellationToken cancellationToken)
    {
        return await _admin.GetRetentionRulesAsync(cancellationToken);
    }

    [HttpPost("retention-rules")]
    public async Task<ActionResult<RetentionRuleDto>> CreateRetentionRule([FromBody] RetentionRuleDto input, CancellationToken cancellationToken)
    {
        return await _admin.CreateRetentionRuleAsync(input, cancellationToken);
    }

    [HttpPut("retention-rules/{id:guid}")]
    public async Task<ActionResult<RetentionRuleDto>> UpdateRetentionRule(Guid id, [FromBody] RetentionRuleDto input, CancellationToken cancellationToken)
    {
        return await _admin.UpdateRetentionRuleAsync(id, input, cancellationToken);
    }

    [HttpDelete("retention-rules/{id:guid}")]
    public async Task<IActionResult> DeleteRetentionRule(Guid id, CancellationToken cancellationToken)
    {
        await _admin.DeleteRetentionRuleAsync(id, cancellationToken);
        return NoContent();
    }

    // ---------- departments ----------

    [HttpGet("departments")]
    public async Task<ActionResult<List<NamedItemDto>>> GetDepartments(CancellationToken cancellationToken)
    {
        return await _admin.GetDepartmentsAsync(cancellationToken);
    }

    [HttpPost("departments")]
    public async Task<ActionResult<NamedItemDto>> CreateDepartment([FromBody] NamedItemDto input, CancellationToken cancellationToken)
    {
        return await _admin.CreateDepartmentAsync(input, cancellationToken);
    }

    [HttpPut("departments/{id:guid}")]
    public async Task<ActionResult<NamedItemDto>> UpdateDepartment(Guid id, [FromBody] NamedItemDto input, CancellationToken cancellationToken)
    {
        return await _admin.UpdateDepartmentAsync(id, input, cancellationToken);
    }

    [HttpDelete("departments/{id:guid}")]
    public async Task<IActionResult> DeleteDepartment(Guid id, CancellationToken cancellationToken)
    {
        await _admin.DeleteDepartmentAsync(id, cancellationToken);
        return NoContent();
    }

    // ---------- contract types ----------

    [HttpGet("contract-types")]
    public async Task<ActionResult<List<NamedItemDto>>> GetContractTypes(CancellationToken cancellationToken)
    {
        return await _admin.GetContractTypesAsync(cancellationToken);
    }

    [HttpPost("contract-types")]
    public async Task<ActionResult<NamedItemDto>> CreateContractType([FromBody] NamedItemDto input, CancellationToken cancellationToken)
    {
        return await _admin.CreateContractTypeAsync(input, cancellationToken);
    }

    [HttpPut("contract-types/{id:guid}")]
    public async Task<ActionResult<NamedItemDto>> UpdateContractType(Guid id, [FromBody] NamedItemDto input, CancellationToken cancellationToken)
    {
        return await _admin.UpdateContractTypeAsync(id, input, cancellationToken);
    }

    [HttpDelete("contract-types/{id:guid}")]
    public async Task<IActionResult> DeleteContractType(Guid id, CancellationToken cancellationToken)
    {
        await _admin.DeleteContractTypeAsync(id, cancellationToken);
        return NoContent();
    }

    // ---------- templates ----------

    [HttpGet("templates")]
    public async Task<ActionResult<List<TemplateDto>>> GetTemplates(CancellationToken cancellationToken)
    {
        return await _admin.GetTemplatesAsync(cancellationToken);
    }

    /// <summary>JSON { name, description, typeId }: creates the template with a generated starter document.</summary>
    [HttpPost("templates")]
    [Consumes("application/json")]
    public async Task<ActionResult<TemplateDto>> CreateTemplate([FromBody] TemplateInput input, CancellationToken cancellationToken)
    {
        return await _admin.CreateTemplateAsync(input, null, null, cancellationToken);
    }

    /// <summary>Multipart: file (.docx), name, description, typeId. Creates the template from an uploaded Word file.</summary>
    [HttpPost("templates")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(30L * 1024 * 1024)]
    public async Task<ActionResult<TemplateDto>> UploadTemplate(
        IFormFile? file, [FromForm] string? name, [FromForm] string? description, [FromForm] Guid? typeId,
        [FromForm] Guid? categoryId, [FromForm] List<Guid>? typeIds, [FromForm] string? language, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new InputValidationException("file", "Choose a Word document (.docx).");
        }
        if (file.Length > IntakeService.MaxUploadBytes)
        {
            throw new InputValidationException("file", "The file is larger than 25 MB.");
        }
        var bytes = await ReadAllAsync(file, cancellationToken);
        // Release 4: category, contract types (repeated form field typeIds) and language.
        var input = new TemplateInput
        {
            Name = name,
            Description = description,
            TypeId = typeId,
            CategoryId = categoryId,
            TypeIds = typeIds is null || typeIds.Count == 0 ? null : typeIds,
            Language = language
        };
        return await _admin.CreateTemplateAsync(input, file.FileName, bytes, cancellationToken);
    }

    [HttpGet("templates/{id:guid}/content")]
    public async Task<IActionResult> GetTemplateContent(Guid id, CancellationToken cancellationToken)
    {
        var content = await _admin.GetTemplateContentAsync(id, cancellationToken);
        return File(content.Stream, content.ContentType, content.FileName);
    }

    [HttpPut("templates/{id:guid}")]
    public async Task<ActionResult<TemplateDto>> UpdateTemplate(Guid id, [FromBody] TemplateInput input, CancellationToken cancellationToken)
    {
        return await _admin.UpdateTemplateAsync(id, input, cancellationToken);
    }

    [HttpDelete("templates/{id:guid}")]
    public async Task<IActionResult> DeleteTemplate(Guid id, CancellationToken cancellationToken)
    {
        await _admin.DeleteTemplateAsync(id, cancellationToken);
        return NoContent();
    }

    // ---------- reminder settings ----------

    [HttpGet("reminder-settings")]
    public async Task<ActionResult<ReminderSettingsDto>> GetReminderSettings(CancellationToken cancellationToken)
    {
        return await _admin.GetReminderSettingsAsync(cancellationToken);
    }

    [HttpPut("reminder-settings")]
    public async Task<ActionResult<ReminderSettingsDto>> UpdateReminderSettings([FromBody] ReminderSettingsDto input, CancellationToken cancellationToken)
    {
        return await _admin.UpdateReminderSettingsAsync(input, cancellationToken);
    }

    // ---------- audit, outbox, jobs ----------

    [HttpGet("audit")]
    public async Task<ActionResult<PagedDto<AuditEventDto>>> GetAudit([FromQuery] Guid? contractId, [FromQuery] int? page, CancellationToken cancellationToken)
    {
        return await _admin.GetAuditAsync(contractId, page, cancellationToken);
    }

    [HttpGet("outbox")]
    public async Task<ActionResult<List<OutboxMessageDto>>> GetOutbox(CancellationToken cancellationToken)
    {
        return await _admin.GetOutboxAsync(cancellationToken);
    }

    [HttpPost("jobs/run")]
    public async Task<ActionResult<JobRunResultDto>> RunJobs(CancellationToken cancellationToken)
    {
        return await _jobs.RunAllAsync(cancellationToken);
    }

    // ---------- holiday cover ----------

    [HttpGet("holiday-cover")]
    public async Task<ActionResult<List<HolidayCoverDto>>> GetHolidayCover(CancellationToken cancellationToken)
    {
        return await _admin.GetHolidayCoverAsync(cancellationToken);
    }

    /// <summary>Replaces the whole list.</summary>
    [HttpPut("holiday-cover")]
    public async Task<ActionResult<List<HolidayCoverDto>>> ReplaceHolidayCover([FromBody] List<HolidayCoverDto> input, CancellationToken cancellationToken)
    {
        return await _admin.ReplaceHolidayCoverAsync(input, cancellationToken);
    }
}
