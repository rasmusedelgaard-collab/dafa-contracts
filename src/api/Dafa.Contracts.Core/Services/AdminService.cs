using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>Settings maintained by administrators. The API host restricts these to role Admin.</summary>
public class AdminService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly ITemplateEngine _templates;

    public AdminService(IAppDbContext db, IClock clock, AuditService audit, ITemplateEngine templates)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
        _templates = templates;
    }

    // ---------- approval rules ----------

    private static ApprovalRuleDto ToDto(ApprovalRule rule)
    {
        return new ApprovalRuleDto
        {
            Id = rule.Id,
            Name = rule.Name,
            EntityId = rule.EntityId,
            TypeId = rule.TypeId,
            DepartmentId = rule.DepartmentId,
            MinValue = rule.MinValue,
            MaxValue = rule.MaxValue,
            Priority = rule.Priority,
            IsActive = rule.IsActive,
            Stages = JsonDefaults.ReadList<ApprovalStageDefinition>(rule.StagesJson)
                .OrderBy(s => s.Order)
                .Select(s => new RuleStageDto
                {
                    Order = s.Order,
                    Approvers = (s.Approvers ?? new List<ApproverDefinition>())
                        .Select(a => a.Kind == ApproverKind.User
                            ? new RuleApproverDto { UserId = a.UserId, Role = null }
                            : new RuleApproverDto { UserId = null, Role = a.Kind })
                        .ToList()
                })
                .ToList()
        };
    }

    public async Task<List<ApprovalRuleDto>> GetApprovalRulesAsync(CancellationToken cancellationToken = default)
    {
        var rules = await _db.ApprovalRules.AsNoTracking().ToListAsync(cancellationToken);
        return rules
            .OrderByDescending(ApprovalRuleMatcher.Specificity)
            .ThenByDescending(r => r.Priority)
            .ThenBy(r => r.MinValue ?? 0m)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => ToDto(r))
            .ToList();
    }

    private static void Apply(ApprovalRuleDto input, ApprovalRule rule)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(input.Name))
            ContractValidator.Add(errors, "name", "Name is required.");
        if (input.MinValue is not null && input.MaxValue is not null && input.MinValue.Value >= input.MaxValue.Value)
            ContractValidator.Add(errors, "maxValue", "The upper value must be greater than the lower value.");

        var stages = new List<ApprovalStageDefinition>();
        var order = 0;
        foreach (var stageInput in (input.Stages ?? new List<RuleStageDto>()).Where(s => s is not null).OrderBy(s => s.Order))
        {
            order++;
            var stage = new ApprovalStageDefinition { Order = order };
            foreach (var approver in stageInput.Approvers ?? new List<RuleApproverDto>())
            {
                if (approver is null)
                {
                    continue;
                }
                if (approver.Role is not null && approver.Role.Value != ApproverKind.User)
                {
                    stage.Approvers.Add(new ApproverDefinition { Kind = approver.Role.Value });
                }
                else if (approver.UserId is not null)
                {
                    stage.Approvers.Add(new ApproverDefinition { Kind = ApproverKind.User, UserId = approver.UserId });
                }
                else
                {
                    ContractValidator.Add(errors, "stages", "Stage " + order + ": choose a user or a role for every approver.");
                }
            }
            if (stage.Approvers.Count == 0)
            {
                ContractValidator.Add(errors, "stages", "Stage " + order + " needs at least one approver.");
            }
            stages.Add(stage);
        }
        if (stages.Count == 0)
        {
            ContractValidator.Add(errors, "stages", "Add at least one stage.");
        }
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }

        rule.Name = input.Name!.Trim();
        rule.EntityId = input.EntityId;
        rule.TypeId = input.TypeId;
        rule.DepartmentId = input.DepartmentId;
        rule.MinValue = input.MinValue;
        rule.MaxValue = input.MaxValue;
        rule.Priority = input.Priority;
        rule.IsActive = input.IsActive ?? true;
        rule.StagesJson = JsonDefaults.Write(stages);
    }

    public async Task<ApprovalRuleDto> CreateApprovalRuleAsync(ApprovalRuleDto input, CancellationToken cancellationToken = default)
    {
        var rule = new ApprovalRule { Id = Guid.NewGuid() };
        Apply(input, rule);
        _db.ApprovalRules.Add(rule);
        _audit.Record("admin.approvalRule.created", "Created approval rule " + rule.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(rule);
    }

    public async Task<ApprovalRuleDto> UpdateApprovalRuleAsync(Guid id, ApprovalRuleDto input, CancellationToken cancellationToken = default)
    {
        var rule = await _db.ApprovalRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            throw new NotFoundException("Approval rule not found.");
        }
        Apply(input, rule);
        _audit.Record("admin.approvalRule.updated", "Updated approval rule " + rule.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(rule);
    }

    public async Task DeleteApprovalRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rule = await _db.ApprovalRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            throw new NotFoundException("Approval rule not found.");
        }
        _db.ApprovalRules.Remove(rule);
        _audit.Record("admin.approvalRule.deleted", "Deleted approval rule " + rule.Name);
        await _db.SaveChangesAsync(cancellationToken);
    }

    // ---------- retention rules ----------

    public async Task<List<RetentionRuleDto>> GetRetentionRulesAsync(CancellationToken cancellationToken = default)
    {
        var rules = await _db.RetentionRules.AsNoTracking().ToListAsync(cancellationToken);
        var types = (await _db.ContractTypes.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(t => t.Id, t => t);
        return rules
            .Select(r => ToRetentionDto(r, types.TryGetValue(r.TypeId, out var type) ? type : null))
            .OrderBy(r => r.TypeName, StringComparer.Ordinal)
            .ToList();
    }

    private static RetentionRuleDto ToRetentionDto(RetentionRule rule, ContractType? type)
    {
        return new RetentionRuleDto
        {
            Id = rule.Id,
            TypeId = rule.TypeId,
            TypeName = type?.Name ?? "",
            Years = rule.Years,
            Action = rule.Action,
            ReviewerDepartmentId = rule.ReviewerDepartmentId
        };
    }

    private async Task<ContractType> ApplyAsync(RetentionRuleDto input, RetentionRule rule, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        ContractType? type = null;
        if (input.TypeId is null)
        {
            ContractValidator.Add(errors, "typeId", "Contract type is required.");
        }
        else
        {
            var typeId = input.TypeId.Value;
            type = await _db.ContractTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == typeId, cancellationToken);
            if (type is null)
            {
                ContractValidator.Add(errors, "typeId", "Unknown contract type.");
            }
            var ruleId = rule.Id;
            if (await _db.RetentionRules.AnyAsync(r => r.TypeId == typeId && r.Id != ruleId, cancellationToken))
            {
                ContractValidator.Add(errors, "typeId", "This contract type already has a retention rule.");
            }
        }
        if (input.Years is not null && (input.Years.Value < 0 || input.Years.Value > 100))
            ContractValidator.Add(errors, "years", "Years must be between 0 and 100.");
        if (input.Action != RetentionAction.Keep && input.Years is null)
            ContractValidator.Add(errors, "years", "Enter the number of years to keep the contract after it ends.");
        if (errors.Count > 0 || type is null)
        {
            throw new InputValidationException(errors);
        }

        rule.TypeId = type.Id;
        rule.Years = input.Action == RetentionAction.Keep ? null : input.Years;
        rule.Action = input.Action;
        rule.ReviewerDepartmentId = input.ReviewerDepartmentId;
        return type;
    }

    public async Task<RetentionRuleDto> CreateRetentionRuleAsync(RetentionRuleDto input, CancellationToken cancellationToken = default)
    {
        var rule = new RetentionRule { Id = Guid.NewGuid() };
        var type = await ApplyAsync(input, rule, cancellationToken);
        _db.RetentionRules.Add(rule);
        _audit.Record("admin.retentionRule.created", "Created retention rule for " + type.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return ToRetentionDto(rule, type);
    }

    public async Task<RetentionRuleDto> UpdateRetentionRuleAsync(Guid id, RetentionRuleDto input, CancellationToken cancellationToken = default)
    {
        var rule = await _db.RetentionRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            throw new NotFoundException("Retention rule not found.");
        }
        var type = await ApplyAsync(input, rule, cancellationToken);
        _audit.Record("admin.retentionRule.updated", "Updated retention rule for " + type.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return ToRetentionDto(rule, type);
    }

    public async Task DeleteRetentionRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rule = await _db.RetentionRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            throw new NotFoundException("Retention rule not found.");
        }
        _db.RetentionRules.Remove(rule);
        _audit.Record("admin.retentionRule.deleted", "Deleted a retention rule");
        await _db.SaveChangesAsync(cancellationToken);
    }

    // ---------- departments ----------

    private static string RequireName(NamedItemDto input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new InputValidationException("name", "Name is required.");
        }
        return input.Name.Trim();
    }

    public async Task<List<NamedItemDto>> GetDepartmentsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.Departments.AsNoTracking().ToListAsync(cancellationToken);
        return rows.OrderBy(d => d.SortOrder).ThenBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => new NamedItemDto { Id = d.Id, Name = d.Name, IsActive = d.IsActive, SortOrder = d.SortOrder })
            .ToList();
    }

    public async Task<NamedItemDto> CreateDepartmentAsync(NamedItemDto input, CancellationToken cancellationToken = default)
    {
        var name = RequireName(input);
        if (await _db.Departments.AnyAsync(d => d.Name == name, cancellationToken))
        {
            throw new InputValidationException("name", "A department with this name already exists.");
        }
        var sortOrders = await _db.Departments.AsNoTracking().Select(d => d.SortOrder).ToListAsync(cancellationToken);
        var row = new Department
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsActive = input.IsActive ?? true,
            SortOrder = input.SortOrder ?? (sortOrders.Count == 0 ? 1 : sortOrders.Max() + 1)
        };
        _db.Departments.Add(row);
        _audit.Record("admin.department.created", "Created department " + name);
        await _db.SaveChangesAsync(cancellationToken);
        return new NamedItemDto { Id = row.Id, Name = row.Name, IsActive = row.IsActive, SortOrder = row.SortOrder };
    }

    public async Task<NamedItemDto> UpdateDepartmentAsync(Guid id, NamedItemDto input, CancellationToken cancellationToken = default)
    {
        var name = RequireName(input);
        var row = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (row is null)
        {
            throw new NotFoundException("Department not found.");
        }
        if (await _db.Departments.AnyAsync(d => d.Name == name && d.Id != id, cancellationToken))
        {
            throw new InputValidationException("name", "A department with this name already exists.");
        }
        row.Name = name;
        row.IsActive = input.IsActive ?? row.IsActive;
        row.SortOrder = input.SortOrder ?? row.SortOrder;
        _audit.Record("admin.department.updated", "Updated department " + name);
        await _db.SaveChangesAsync(cancellationToken);
        return new NamedItemDto { Id = row.Id, Name = row.Name, IsActive = row.IsActive, SortOrder = row.SortOrder };
    }

    public async Task DeleteDepartmentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (row is null)
        {
            throw new NotFoundException("Department not found.");
        }
        if (await _db.Contracts.AnyAsync(c => c.DepartmentId == id, cancellationToken))
        {
            throw new ConflictException("The department is used by contracts. Deactivate it instead of deleting it.", ProblemTypesV3.InUse);
        }
        var memberships = await _db.UserDepartments.Where(d => d.DepartmentId == id).ToListAsync(cancellationToken);
        _db.UserDepartments.RemoveRange(memberships);
        _db.Departments.Remove(row);
        _audit.Record("admin.department.deleted", "Deleted department " + row.Name);
        await _db.SaveChangesAsync(cancellationToken);
    }

    // ---------- contract types ----------

    public async Task<List<NamedItemDto>> GetContractTypesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.ContractTypes.AsNoTracking().ToListAsync(cancellationToken);
        return rows.OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new NamedItemDto { Id = t.Id, Name = t.Name, IsActive = t.IsActive, SortOrder = t.SortOrder })
            .ToList();
    }

    public async Task<NamedItemDto> CreateContractTypeAsync(NamedItemDto input, CancellationToken cancellationToken = default)
    {
        var name = RequireName(input);
        if (await _db.ContractTypes.AnyAsync(t => t.Name == name, cancellationToken))
        {
            throw new InputValidationException("name", "A contract type with this name already exists.");
        }
        var sortOrders = await _db.ContractTypes.AsNoTracking().Select(t => t.SortOrder).ToListAsync(cancellationToken);
        var row = new ContractType
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsActive = input.IsActive ?? true,
            SortOrder = input.SortOrder ?? (sortOrders.Count == 0 ? 1 : sortOrders.Max() + 1)
        };
        _db.ContractTypes.Add(row);
        _audit.Record("admin.contractType.created", "Created contract type " + name);
        await _db.SaveChangesAsync(cancellationToken);
        return new NamedItemDto { Id = row.Id, Name = row.Name, IsActive = row.IsActive, SortOrder = row.SortOrder };
    }

    public async Task<NamedItemDto> UpdateContractTypeAsync(Guid id, NamedItemDto input, CancellationToken cancellationToken = default)
    {
        var name = RequireName(input);
        var row = await _db.ContractTypes.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (row is null)
        {
            throw new NotFoundException("Contract type not found.");
        }
        if (await _db.ContractTypes.AnyAsync(t => t.Name == name && t.Id != id, cancellationToken))
        {
            throw new InputValidationException("name", "A contract type with this name already exists.");
        }
        row.Name = name;
        row.IsActive = input.IsActive ?? row.IsActive;
        row.SortOrder = input.SortOrder ?? row.SortOrder;
        _audit.Record("admin.contractType.updated", "Updated contract type " + name);
        await _db.SaveChangesAsync(cancellationToken);
        return new NamedItemDto { Id = row.Id, Name = row.Name, IsActive = row.IsActive, SortOrder = row.SortOrder };
    }

    public async Task DeleteContractTypeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _db.ContractTypes.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (row is null)
        {
            throw new NotFoundException("Contract type not found.");
        }
        if (await _db.Contracts.AnyAsync(c => c.TypeId == id, cancellationToken))
        {
            throw new ConflictException("The contract type is used by contracts. Deactivate it instead of deleting it.", ProblemTypesV3.InUse);
        }
        var retention = await _db.RetentionRules.Where(r => r.TypeId == id).ToListAsync(cancellationToken);
        _db.RetentionRules.RemoveRange(retention);
        _db.ContractTypes.Remove(row);
        _audit.Record("admin.contractType.deleted", "Deleted contract type " + row.Name);
        await _db.SaveChangesAsync(cancellationToken);
    }

    // ---------- templates ----------

    public async Task<List<TemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.Templates.AsNoTracking().ToListAsync(cancellationToken);
        return rows.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(IntakeService.ToDto).ToList();
    }

    private async Task CheckTemplateTypeAsync(Guid? typeId, CancellationToken cancellationToken)
    {
        if (typeId is null)
        {
            return;
        }
        var id = typeId.Value;
        if (!await _db.ContractTypes.AnyAsync(t => t.Id == id, cancellationToken))
        {
            throw new InputValidationException("typeId", "Unknown contract type.");
        }
    }

    /// <summary>
    /// Creates a template. With a file (multipart) the uploaded .docx is used; without one (JSON)
    /// a starter document with the standard placeholders is generated.
    /// </summary>
    public async Task<TemplateDto> CreateTemplateAsync(TemplateInput input, string? fileName, byte[]? content, CancellationToken cancellationToken = default)
    {
        await CheckTemplateTypeAsync(input.TypeId, cancellationToken);
        string safeName;
        if (content is null)
        {
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                throw new InputValidationException("name", "Name is required.");
            }
            safeName = DocumentWriter.SafeFileName(input.Name.Trim() + ".docx");
            content = _templates.CreateStarter(input.Name.Trim());
        }
        else
        {
            safeName = DocumentWriter.SafeFileName(fileName);
            if (!safeName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                throw new InputValidationException("file", "A template must be a Word document (.docx).");
            }
            if (content.Length == 0)
            {
                throw new InputValidationException("file", "The file is empty.");
            }
        }

        IReadOnlyList<string> placeholders;
        try
        {
            placeholders = _templates.GetPlaceholders(content);
        }
        catch (Exception)
        {
            throw new InputValidationException("file", "The file could not be read as a Word document.");
        }
        var row = new ContractTemplate
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(input.Name) ? Path.GetFileNameWithoutExtension(safeName) : input.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            TypeId = input.TypeId,
            FileName = safeName,
            Content = content,
            Placeholders = string.Join(",", placeholders),
            IsActive = input.IsActive ?? true,
            UpdatedAt = _clock.UtcNow
        };
        _db.Templates.Add(row);
        _audit.Record("admin.template.created", "Added template " + row.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return IntakeService.ToDto(row);
    }

    public async Task<TemplateDto> UpdateTemplateAsync(Guid id, TemplateInput input, CancellationToken cancellationToken = default)
    {
        var row = await _db.Templates.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (row is null)
        {
            throw new NotFoundException("Template not found.");
        }
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new InputValidationException("name", "Name is required.");
        }
        await CheckTemplateTypeAsync(input.TypeId, cancellationToken);
        row.Name = input.Name.Trim();
        row.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        row.TypeId = input.TypeId;
        row.IsActive = input.IsActive ?? row.IsActive;
        row.UpdatedAt = _clock.UtcNow;
        _audit.Record("admin.template.updated", "Updated template " + row.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return IntakeService.ToDto(row);
    }

    public async Task<DocumentContent> GetTemplateContentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (row is null)
        {
            throw new NotFoundException("Template not found.");
        }
        return new DocumentContent(new MemoryStream(row.Content, false), row.FileName, DocumentWriter.ContentTypeFor(row.FileName));
    }

    public async Task DeleteTemplateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _db.Templates.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (row is null)
        {
            throw new NotFoundException("Template not found.");
        }
        _db.Templates.Remove(row);
        _audit.Record("admin.template.deleted", "Deleted template " + row.Name);
        await _db.SaveChangesAsync(cancellationToken);
    }

    // ---------- reminder settings ----------

    public async Task<ReminderSettingsDto> GetReminderSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ReminderSettingsReader.GetAsync(_db, cancellationToken);
        return new ReminderSettingsDto
        {
            NoticeDays = settings.GetNoticeDays(),
            EscalationDays = settings.EscalationDays,
            ObligationDays = settings.ObligationDays
        };
    }

    public async Task<ReminderSettingsDto> UpdateReminderSettingsAsync(ReminderSettingsDto input, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        var days = (input.NoticeDays ?? new List<int>()).Distinct().OrderByDescending(d => d).ToList();
        if (days.Count == 0 || days.Count > 6 || days.Any(d => d < 1 || d > 730))
            ContractValidator.Add(errors, "noticeDays", "Enter one to six reminder days between 1 and 730.");
        if (input.EscalationDays < 0 || input.EscalationDays > 365)
            ContractValidator.Add(errors, "escalationDays", "Escalation days must be between 0 and 365.");
        if (input.ObligationDays < 0 || input.ObligationDays > 365)
            ContractValidator.Add(errors, "obligationDays", "Obligation reminder days must be between 0 and 365.");
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }

        var settings = await _db.ReminderSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new ReminderSettings { Id = Guid.NewGuid() };
            _db.ReminderSettings.Add(settings);
        }
        settings.NoticeDays = string.Join(",", days);
        settings.EscalationDays = input.EscalationDays;
        settings.ObligationDays = input.ObligationDays;
        _audit.Record("admin.reminderSettings.updated", "Updated reminder settings: " + settings.NoticeDays + " days, escalation " + settings.EscalationDays);
        await _db.SaveChangesAsync(cancellationToken);
        return await GetReminderSettingsAsync(cancellationToken);
    }

    // ---------- audit and outbox ----------

    public async Task<PagedDto<AuditEventDto>> GetAuditAsync(Guid? contractId, int? page, CancellationToken cancellationToken = default)
    {
        const int pageSize = 50;
        var pageNumber = Math.Max(1, page ?? 1);
        IQueryable<AuditEvent> query = _db.AuditEvents.AsNoTracking();
        if (contractId is not null)
        {
            var id = contractId.Value;
            query = query.Where(e => e.ContractId == id);
        }
        var total = await query.CountAsync(cancellationToken);
        var events = await query
            .OrderByDescending(e => e.At)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var contracts = (await _db.Contracts.AsNoTracking().Select(c => new { c.Id, c.ContractNo, c.Title }).ToListAsync(cancellationToken))
            .ToDictionary(c => c.Id, c => c);

        return new PagedDto<AuditEventDto>
        {
            Items = events.Select(e => new AuditEventDto
            {
                Id = e.Id,
                At = e.At,
                Who = e.UserName,
                Action = e.Action,
                ContractId = e.ContractId,
                ContractNo = e.ContractId is not null && contracts.TryGetValue(e.ContractId.Value, out var numbered) ? numbered.ContractNo : null,
                ContractTitle = e.ContractId is not null && contracts.TryGetValue(e.ContractId.Value, out var titled) ? titled.Title : null,
                Text = e.Text
            }).ToList(),
            Total = total,
            Page = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<List<OutboxMessageDto>> GetOutboxAsync(CancellationToken cancellationToken = default)
    {
        var messages = await _db.OutboxMessages.AsNoTracking()
            .OrderByDescending(m => m.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);
        var users = (await _db.Users.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(u => u.Id, u => u.Name);
        return messages.Select(m => ToOutboxDto(m, m.RecipientId is not null && users.TryGetValue(m.RecipientId.Value, out var name) ? name : "")).ToList();
    }

    private static OutboxMessageDto ToOutboxDto(OutboxMessage m, string recipientName)
    {
        string to;
        if (recipientName.Length > 0 && m.RecipientEmail.Length > 0) to = recipientName + " <" + m.RecipientEmail + ">";
        else to = recipientName.Length > 0 ? recipientName : m.RecipientEmail;
        return new OutboxMessageDto
        {
            Id = m.Id,
            CreatedAt = m.CreatedAt,
            To = to,
            Channel = string.IsNullOrEmpty(m.Channel) ? "Mail" : m.Channel,
            Recipient = recipientName,
            RecipientEmail = m.RecipientEmail,
            Kind = m.Kind,
            Subject = m.Subject,
            Body = m.Body,
            ContractId = m.ContractId,
            DeliveredAt = m.DeliveredAt
        };
    }

    // ---------- holiday cover ----------

    public async Task<List<HolidayCoverDto>> GetHolidayCoverAsync(CancellationToken cancellationToken = default)
    {
        var covers = await _db.HolidayCovers.AsNoTracking().ToListAsync(cancellationToken);
        var users = (await _db.Users.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(u => u.Id, u => u.Name);
        return covers
            .OrderBy(c => c.FromDate)
            .Select(c => new HolidayCoverDto
            {
                Id = c.Id,
                UserId = c.UserId,
                UserName = users.TryGetValue(c.UserId, out var userName) ? userName : "",
                DeputyId = c.DeputyId,
                DeputyName = users.TryGetValue(c.DeputyId, out var deputyName) ? deputyName : "",
                From = c.FromDate,
                To = c.ToDate
            })
            .ToList();
    }

    /// <summary>Replaces the whole list of holiday covers.</summary>
    public async Task<List<HolidayCoverDto>> ReplaceHolidayCoverAsync(List<HolidayCoverDto> input, CancellationToken cancellationToken = default)
    {
        var userIds = new HashSet<Guid>(await _db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => u.Id).ToListAsync(cancellationToken));
        var errors = new Dictionary<string, string[]>();
        var rows = new List<HolidayCover>();
        var index = 0;
        foreach (var item in input)
        {
            index++;
            var label = "Row " + index + ": ";
            if (item.UserId is null || !userIds.Contains(item.UserId.Value))
                ContractValidator.Add(errors, "userId", label + "choose the user who is away.");
            if (item.DeputyId is null || !userIds.Contains(item.DeputyId.Value))
                ContractValidator.Add(errors, "deputyId", label + "choose the deputy.");
            if (item.UserId is not null && item.UserId == item.DeputyId)
                ContractValidator.Add(errors, "deputyId", label + "the deputy must be someone else.");
            if (item.From is null || item.To is null)
                ContractValidator.Add(errors, "from", label + "enter both dates.");
            else if (item.To.Value < item.From.Value)
                ContractValidator.Add(errors, "to", label + "the end date cannot be before the start date.");

            if (item.UserId is not null && item.DeputyId is not null && item.From is not null && item.To is not null)
            {
                rows.Add(new HolidayCover
                {
                    Id = Guid.NewGuid(),
                    UserId = item.UserId.Value,
                    DeputyId = item.DeputyId.Value,
                    FromDate = item.From.Value,
                    ToDate = item.To.Value
                });
            }
        }
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }

        var existing = await _db.HolidayCovers.ToListAsync(cancellationToken);
        _db.HolidayCovers.RemoveRange(existing);
        _db.HolidayCovers.AddRange(rows);
        _audit.Record("admin.holidayCover.updated", "Updated holiday cover (" + rows.Count + " entries)");
        await _db.SaveChangesAsync(cancellationToken);
        return await GetHolidayCoverAsync(cancellationToken);
    }
}
