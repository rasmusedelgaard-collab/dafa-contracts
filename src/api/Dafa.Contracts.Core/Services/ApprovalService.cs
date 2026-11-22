using System.Globalization;
using System.Text;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>Picks the approval rule for a contract: most specific match wins, then priority.</summary>
public static class ApprovalRuleMatcher
{
    /// <summary>Number of criteria the rule sets. A value band (min and/or max) counts as one.</summary>
    public static int Specificity(ApprovalRule rule) =>
        (rule.EntityId.HasValue ? 1 : 0)
        + (rule.TypeId.HasValue ? 1 : 0)
        + (rule.DepartmentId.HasValue ? 1 : 0)
        + (rule.MinValue.HasValue || rule.MaxValue.HasValue ? 1 : 0);

    public static bool Matches(ApprovalRule rule, Guid? entityId, Guid? typeId, Guid? departmentId, decimal valueDkk)
    {
        if (!rule.IsActive) return false;
        if (rule.EntityId.HasValue && rule.EntityId != entityId) return false;
        if (rule.TypeId.HasValue && rule.TypeId != typeId) return false;
        if (rule.DepartmentId.HasValue && rule.DepartmentId != departmentId) return false;
        if (rule.MinValue.HasValue && valueDkk < rule.MinValue.Value) return false;
        if (rule.MaxValue.HasValue && valueDkk >= rule.MaxValue.Value) return false;
        return true;
    }

    public static ApprovalRule? Match(IEnumerable<ApprovalRule> rules, Guid? entityId, Guid? typeId, Guid? departmentId, decimal valueDkk)
    {
        return rules
            .Where(r => Matches(r, entityId, typeId, departmentId, valueDkk))
            .OrderByDescending(Specificity)
            .ThenByDescending(r => r.Priority)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>The value an approval is based on: total value when set, otherwise annual value, in DKK.</summary>
    public static decimal ValueDkk(decimal? annualValue, decimal? totalValue, string? currency)
    {
        var value = totalValue ?? annualValue ?? 0m;
        return CurrencyRates.ToDkk(value, currency);
    }

    public static List<ApprovalStageDefinition> Stages(ApprovalRule rule)
    {
        return JsonDefaults.ReadList<ApprovalStageDefinition>(rule.StagesJson)
            .Where(s => s.Approvers != null && s.Approvers.Count > 0)
            .OrderBy(s => s.Order)
            .ToList();
    }
}

/// <summary>In-memory snapshot of users and their functions, used to resolve approvers.</summary>
public sealed class ApproverDirectory
{
    private readonly List<AppUser> _users;
    private readonly List<UserDepartment> _userDepartments;
    private readonly List<UserEntityManagement> _entityManagers;
    private readonly List<HolidayCover> _covers;

    public ApproverDirectory(
        IEnumerable<AppUser> users,
        IEnumerable<UserDepartment> userDepartments,
        IEnumerable<UserEntityManagement> entityManagers,
        IEnumerable<HolidayCover> covers)
    {
        _users = users.ToList();
        _userDepartments = userDepartments.ToList();
        _entityManagers = entityManagers.ToList();
        _covers = covers.ToList();
    }

    public static async Task<ApproverDirectory> LoadAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        return new ApproverDirectory(
            await db.Users.AsNoTracking().ToListAsync(cancellationToken),
            await db.UserDepartments.AsNoTracking().ToListAsync(cancellationToken),
            await db.UserEntityManagements.AsNoTracking().ToListAsync(cancellationToken),
            await db.HolidayCovers.AsNoTracking().ToListAsync(cancellationToken));
    }

    public AppUser? Find(Guid? userId)
    {
        if (userId is null) return null;
        return _users.FirstOrDefault(u => u.Id == userId.Value);
    }

    public string NameOf(Guid? userId) => Find(userId)?.Name ?? "Unknown user";

    private AppUser? FirstActive(IEnumerable<Guid> userIds)
    {
        var ids = new HashSet<Guid>(userIds);
        return _users.Where(u => u.IsActive && ids.Contains(u.Id)).OrderBy(u => u.Name, StringComparer.Ordinal).FirstOrDefault();
    }

    public AppUser? Resolve(ApproverDefinition approver, Guid? entityId, Guid? departmentId)
    {
        switch (approver.Kind)
        {
            case ApproverKind.User:
                var user = Find(approver.UserId);
                return user is not null && user.IsActive ? user : null;
            case ApproverKind.DepartmentHead:
                if (departmentId is null) return null;
                return FirstActive(_userDepartments.Where(d => d.IsHead && d.DepartmentId == departmentId.Value).Select(d => d.UserId));
            case ApproverKind.EntityManager:
                if (entityId is null) return null;
                return FirstActive(_entityManagers.Where(m => m.EntityId == entityId.Value).Select(m => m.UserId));
            case ApproverKind.GroupCFO:
                return FirstActive(_users.Where(u => u.GroupFunction == GroupFunction.GroupCFO).Select(u => u.Id));
            case ApproverKind.GroupCEO:
                return FirstActive(_users.Where(u => u.GroupFunction == GroupFunction.GroupCEO).Select(u => u.Id));
            case ApproverKind.Legal:
                return FirstActive(_users.Where(u => u.GroupFunction == GroupFunction.Legal).Select(u => u.Id));
            default:
                return null;
        }
    }

    public AppUser? FallbackAdmin() =>
        _users.Where(u => u.IsActive && (u.Roles & AppRole.Admin) == AppRole.Admin)
            .OrderBy(u => u.Name, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>The deputy covering for the user today, if a holiday cover is active.</summary>
    public AppUser? ActiveDeputy(Guid userId, DateOnly today)
    {
        foreach (var cover in _covers.Where(c => c.UserId == userId && c.FromDate <= today && today <= c.ToDate).OrderBy(c => c.FromDate))
        {
            var deputy = Find(cover.DeputyId);
            if (deputy is not null && deputy.IsActive && deputy.Id != userId)
            {
                return deputy;
            }
        }
        return null;
    }

    public static string Label(ApproverKind kind) => kind switch
    {
        ApproverKind.DepartmentHead => "Department head",
        ApproverKind.EntityManager => "Entity manager",
        ApproverKind.GroupCFO => "Group CFO",
        ApproverKind.GroupCEO => "Group CEO",
        ApproverKind.Legal => "Legal",
        _ => "Approver"
    };
}

public static class ApprovalMapper
{
    public static ApprovalInstanceDto ToDto(ApprovalInstance instance, IEnumerable<ApprovalStep> steps, ReferenceData reference, string contractTitle)
    {
        var dto = new ApprovalInstanceDto
        {
            Id = instance.Id,
            ContractId = instance.ContractId,
            ContractTitle = contractTitle,
            Status = instance.Status.ToString(),
            CurrentStage = instance.CurrentStage
        };
        foreach (var group in steps.Where(s => s.InstanceId == instance.Id).GroupBy(s => s.StageOrder).OrderBy(g => g.Key))
        {
            var stage = new ApprovalStageDto { Order = group.Key };
            foreach (var step in group.OrderBy(s => s.ActivatedAt ?? DateTime.MaxValue).ThenBy(s => s.RoleLabel, StringComparer.Ordinal))
            {
                stage.Steps.Add(new ApprovalStepDto
                {
                    Id = step.Id,
                    ApproverId = step.ApproverId,
                    ApproverName = reference.UserName(step.ApproverId),
                    Initials = reference.UserInitials(step.ApproverId),
                    RoleLabel = step.RoleLabel,
                    Status = step.Status.ToString(),
                    DecidedAt = step.DecidedAt,
                    Comment = step.Comment
                });
            }
            dto.Stages.Add(stage);
        }
        return dto;
    }
}

/// <summary>
/// The approval engine: rule matching, stages with parallel approvers, role resolvers,
/// delegation, holiday cover, rejection back to Draft and final approval with a log document.
/// </summary>
public class ApprovalService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly INotifier _notifier;
    private readonly AuditService _audit;
    private readonly DocumentWriter _documents;

    public ApprovalService(
        IAppDbContext db, ICurrentUser currentUser, IClock clock, INotifier notifier,
        AuditService audit, DocumentWriter documents)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _notifier = notifier;
        _audit = audit;
        _documents = documents;
    }

    public async Task<ApprovalPreviewDto> PreviewAsync(
        Guid? entityId, Guid? typeId, Guid? departmentId, decimal? value, string? currency,
        CancellationToken cancellationToken = default)
    {
        var rules = await _db.ApprovalRules.AsNoTracking().ToListAsync(cancellationToken);
        var valueDkk = CurrencyRates.ToDkk(value ?? 0m, string.IsNullOrWhiteSpace(currency) ? "DKK" : currency);
        var rule = ApprovalRuleMatcher.Match(rules, entityId, typeId, departmentId, valueDkk);
        var preview = new ApprovalPreviewDto();
        if (rule is null)
        {
            return preview;
        }

        preview.RuleId = rule.Id;
        preview.RuleName = rule.Name;
        var directory = await ApproverDirectory.LoadAsync(_db, cancellationToken);
        var order = 0;
        foreach (var stage in ApprovalRuleMatcher.Stages(rule))
        {
            order++;
            var names = new List<string>();
            foreach (var approver in stage.Approvers)
            {
                var label = ApproverDirectory.Label(approver.Kind);
                var user = directory.Resolve(approver, entityId, departmentId);
                // "Role · Name", the format the UI shows as it is.
                var text = label + " \u00B7 " + (user is null ? "not assigned" : user.Name);
                if (!names.Contains(text))
                {
                    names.Add(text);
                }
            }
            preview.Stages.Add(new ApprovalPreviewStageDto { Order = order, Approvers = names });
        }
        return preview;
    }

    /// <summary>
    /// Creates the approval instance and its steps for a contract and opens the first stage.
    /// Adds to the unit of work; the caller sets the contract status and saves.
    /// </summary>
    public async Task<ApprovalInstance> StartAsync(Contract contract, Guid? startedById, CancellationToken cancellationToken = default)
    {
        var rules = await _db.ApprovalRules.AsNoTracking().ToListAsync(cancellationToken);
        var valueDkk = ApprovalRuleMatcher.ValueDkk(contract.AnnualValue, contract.TotalValue, contract.Currency);
        var rule = ApprovalRuleMatcher.Match(rules, contract.EntityId, contract.TypeId, contract.DepartmentId, valueDkk);
        if (rule is null)
        {
            throw new ConflictException("No approval rule matches this contract. Ask an administrator to add a rule.");
        }
        var stages = ApprovalRuleMatcher.Stages(rule);
        if (stages.Count == 0)
        {
            throw new ConflictException("The approval rule '" + rule.Name + "' has no stages. Ask an administrator to complete it.");
        }

        var directory = await ApproverDirectory.LoadAsync(_db, cancellationToken);
        var now = _clock.UtcNow;
        var instance = new ApprovalInstance
        {
            Id = Guid.NewGuid(),
            ContractId = contract.Id,
            RuleId = rule.Id,
            RuleName = rule.Name,
            Status = ApprovalStatus.Pending,
            CurrentStage = 1,
            StartedById = startedById,
            StartedAt = now
        };

        var steps = new List<ApprovalStep>();
        var order = 0;
        foreach (var stage in stages)
        {
            order++;
            var seen = new HashSet<Guid>();
            foreach (var approver in stage.Approvers)
            {
                var label = ApproverDirectory.Label(approver.Kind);
                var user = directory.Resolve(approver, contract.EntityId, contract.DepartmentId);
                if (user is null)
                {
                    user = directory.FallbackAdmin();
                    label += " (not assigned, sent to administrator)";
                }
                if (user is null)
                {
                    throw new ConflictException("No approver could be found for stage " + order + " (" + ApproverDirectory.Label(approver.Kind) + ").");
                }
                if (!seen.Add(user.Id))
                {
                    continue;
                }
                steps.Add(new ApprovalStep
                {
                    Id = Guid.NewGuid(),
                    InstanceId = instance.Id,
                    ContractId = contract.Id,
                    StageOrder = order,
                    ApproverId = user.Id,
                    RoleLabel = label,
                    Status = ApprovalStepStatus.Waiting
                });
            }
        }

        _db.ApprovalInstances.Add(instance);
        _db.ApprovalSteps.AddRange(steps);
        await ActivateStageAsync(contract, steps, 1, directory, cancellationToken);
        return instance;
    }

    private async Task ActivateStageAsync(
        Contract contract, List<ApprovalStep> steps, int stageOrder, ApproverDirectory directory,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        foreach (var step in steps.Where(s => s.StageOrder == stageOrder && s.Status == ApprovalStepStatus.Waiting))
        {
            var deputy = directory.ActiveDeputy(step.ApproverId, _clock.Today);
            if (deputy is not null)
            {
                Reroute(step, deputy, directory);
            }
            step.Status = ApprovalStepStatus.Pending;
            step.ActivatedAt = now;
            await _notifier.SendAsync(new Notification(
                step.ApproverId,
                "ApprovalRequested",
                "Approval needed: " + contract.Title,
                "Contract " + contract.ContractNo + " (" + contract.Title + ") with " + contract.CounterpartyName
                    + " is waiting for your approval as " + step.RoleLabel + ".",
                contract.Id), cancellationToken);
        }
    }

    private static void Reroute(ApprovalStep step, AppUser deputy, ApproverDirectory directory)
    {
        var originalName = directory.NameOf(step.ApproverId);
        step.OriginalApproverId = step.ApproverId;
        step.ApproverId = deputy.Id;
        step.RoleLabel = step.RoleLabel + " (cover for " + originalName + ")";
    }

    public async Task DecideAsync(Guid stepId, ApprovalDecision decision, string? comment, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var step = await _db.ApprovalSteps.FirstOrDefaultAsync(s => s.Id == stepId, cancellationToken);
        if (step is null)
        {
            throw new NotFoundException("Approval step not found.");
        }
        if (step.ApproverId != profile.UserId)
        {
            throw new ForbiddenException("This approval step is assigned to someone else.");
        }
        if (step.Status != ApprovalStepStatus.Pending)
        {
            throw new ConflictException("This approval step is not open for a decision.");
        }

        var instanceId = step.InstanceId;
        var instance = await _db.ApprovalInstances.FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null || instance.Status != ApprovalStatus.Pending)
        {
            throw new ConflictException("This approval is already closed.");
        }
        var contractId = instance.ContractId;
        var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken);
        if (contract is null)
        {
            throw new NotFoundException("Contract not found.");
        }
        var steps = await _db.ApprovalSteps.Where(s => s.InstanceId == instanceId).ToListAsync(cancellationToken);
        var now = _clock.UtcNow;
        var trimmed = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

        if (decision == ApprovalDecision.Reject)
        {
            if (trimmed is null)
            {
                throw new InputValidationException("comment", "A comment is required when rejecting.");
            }
            step.Status = ApprovalStepStatus.Rejected;
            step.DecidedAt = now;
            step.Comment = trimmed;
            foreach (var other in steps.Where(s => s.Id != step.Id && s.Status == ApprovalStepStatus.Pending))
            {
                // The instance is closed; nobody else needs to act.
                other.Status = ApprovalStepStatus.Waiting;
            }
            instance.Status = ApprovalStatus.Rejected;
            instance.CompletedAt = now;
            contract.Status = ContractStatus.Draft;
            contract.UpdatedAt = now;
            _audit.Record("approval.rejected", "Rejected by " + profile.Name + ": " + trimmed, contract.Id);
            await _notifier.SendAsync(new Notification(
                contract.OwnerId,
                "ApprovalRejected",
                "Rejected: " + contract.Title,
                profile.Name + " rejected contract " + contract.ContractNo + ". Comment: " + trimmed
                    + " The contract is back in draft.",
                contract.Id), cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        step.Status = ApprovalStepStatus.Approved;
        step.DecidedAt = now;
        step.Comment = trimmed;
        _audit.Record("approval.approved", "Approved by " + profile.Name + " (" + step.RoleLabel + ")", contract.Id);

        var stageDone = steps
            .Where(s => s.StageOrder == step.StageOrder && s.Status != ApprovalStepStatus.Delegated)
            .All(s => s.Status == ApprovalStepStatus.Approved);

        if (stageDone)
        {
            var laterStages = steps.Where(s => s.StageOrder > step.StageOrder).Select(s => s.StageOrder).Distinct().OrderBy(o => o).ToList();
            if (laterStages.Count > 0)
            {
                var directory = await ApproverDirectory.LoadAsync(_db, cancellationToken);
                instance.CurrentStage = laterStages[0];
                await ActivateStageAsync(contract, steps, laterStages[0], directory, cancellationToken);
            }
            else
            {
                await CompleteAsync(contract, instance, steps, now, cancellationToken);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task CompleteAsync(
        Contract contract, ApprovalInstance instance, List<ApprovalStep> steps, DateTime now,
        CancellationToken cancellationToken)
    {
        instance.Status = ApprovalStatus.Approved;
        instance.CompletedAt = now;
        contract.Status = ContractStatus.Approved;
        contract.UpdatedAt = now;

        var directory = await ApproverDirectory.LoadAsync(_db, cancellationToken);
        var log = BuildApprovalLog(contract, instance, steps, directory);
        await _documents.AddAsync(
            contract,
            "Approval log " + contract.ContractNo + ".txt",
            DocumentKind.ApprovalLog,
            Encoding.UTF8.GetBytes(log),
            null,
            cancellationToken);

        _audit.Record("approval.completed", "Fully approved. Approval log saved to documents.", contract.Id);
        await _notifier.SendAsync(new Notification(
            contract.OwnerId,
            "ApprovalCompleted",
            "Approved: " + contract.Title,
            "Contract " + contract.ContractNo + " is fully approved. Mark it as signed once the signatures are in place.",
            contract.Id), cancellationToken);
    }

    public static string BuildApprovalLog(Contract contract, ApprovalInstance instance, IEnumerable<ApprovalStep> steps, ApproverDirectory directory)
    {
        var culture = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine("DAFA Contracts - approval log");
        text.AppendLine("=============================");
        text.AppendLine("Contract:      " + contract.ContractNo + " - " + contract.Title);
        text.AppendLine("Counterparty:  " + contract.CounterpartyName);
        text.AppendLine("Rule:          " + instance.RuleName);
        text.AppendLine("Started (UTC): " + instance.StartedAt.ToString("yyyy-MM-dd HH:mm", culture));
        if (instance.CompletedAt is not null)
        {
            text.AppendLine("Completed:     " + instance.CompletedAt.Value.ToString("yyyy-MM-dd HH:mm", culture));
        }
        text.AppendLine("Result:        " + instance.Status);
        text.AppendLine();
        foreach (var stage in steps.GroupBy(s => s.StageOrder).OrderBy(g => g.Key))
        {
            text.AppendLine("Stage " + stage.Key);
            foreach (var step in stage.OrderBy(s => s.DecidedAt ?? DateTime.MaxValue))
            {
                var decided = step.DecidedAt is null ? "-" : step.DecidedAt.Value.ToString("yyyy-MM-dd HH:mm", culture);
                text.AppendLine("  " + directory.NameOf(step.ApproverId) + " (" + step.RoleLabel + "): " + step.Status + " " + decided);
                if (!string.IsNullOrWhiteSpace(step.Comment))
                {
                    text.AppendLine("    Comment: " + step.Comment);
                }
            }
        }
        return text.ToString();
    }

    public async Task DelegateAsync(Guid stepId, Guid toUserId, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var step = await _db.ApprovalSteps.FirstOrDefaultAsync(s => s.Id == stepId, cancellationToken);
        if (step is null)
        {
            throw new NotFoundException("Approval step not found.");
        }
        if (step.ApproverId != profile.UserId && !profile.IsAdmin)
        {
            throw new ForbiddenException("This approval step is assigned to someone else.");
        }
        if (step.Status != ApprovalStepStatus.Pending)
        {
            throw new ConflictException("Only an open approval step can be delegated.");
        }
        if (toUserId == step.ApproverId)
        {
            throw new InputValidationException("userId", "Choose a different user to delegate to.");
        }
        var target = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == toUserId, cancellationToken);
        if (target is null || !target.IsActive)
        {
            throw new InputValidationException("userId", "The selected user does not exist or is inactive.");
        }
        var contractId = step.ContractId;
        var contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken);
        if (contract is null)
        {
            throw new NotFoundException("Contract not found.");
        }

        var now = _clock.UtcNow;
        var previousApproverId = step.ApproverId;
        var previousApprover = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == previousApproverId, cancellationToken);
        var previousName = previousApprover?.Name ?? profile.Name;

        var replacement = new ApprovalStep
        {
            Id = Guid.NewGuid(),
            InstanceId = step.InstanceId,
            ContractId = step.ContractId,
            StageOrder = step.StageOrder,
            ApproverId = target.Id,
            OriginalApproverId = step.ApproverId,
            RoleLabel = step.RoleLabel + " (delegated by " + previousName + ")",
            Status = ApprovalStepStatus.Pending,
            ActivatedAt = now
        };
        _db.ApprovalSteps.Add(replacement);

        step.Status = ApprovalStepStatus.Delegated;
        step.DecidedAt = now;
        step.Comment = "Delegated to " + target.Name;

        _audit.Record("approval.delegated", previousName + " delegated the approval to " + target.Name, contract.Id);
        await _notifier.SendAsync(new Notification(
            target.Id,
            "ApprovalRequested",
            "Approval delegated to you: " + contract.Title,
            previousName + " delegated the approval of contract " + contract.ContractNo + " (" + contract.Title + ") to you.",
            contract.Id), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Moves open steps to the deputy when a holiday cover has become active after the step was
    /// opened. A step is rerouted at most once. Adds to the unit of work; the caller saves.
    /// </summary>
    public async Task<int> ApplyHolidayCoverAsync(CancellationToken cancellationToken = default)
    {
        var open = await _db.ApprovalSteps
            .Where(s => s.Status == ApprovalStepStatus.Pending && s.OriginalApproverId == null)
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
        {
            return 0;
        }
        var directory = await ApproverDirectory.LoadAsync(_db, cancellationToken);
        var count = 0;
        foreach (var step in open)
        {
            var deputy = directory.ActiveDeputy(step.ApproverId, _clock.Today);
            if (deputy is null)
            {
                continue;
            }
            var stepContractId = step.ContractId;
            var contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == stepContractId, cancellationToken);
            Reroute(step, deputy, directory);
            count++;
            if (contract is not null)
            {
                await _notifier.SendAsync(new Notification(
                    deputy.Id,
                    "ApprovalRequested",
                    "Approval needed (holiday cover): " + contract.Title,
                    "You are covering an approval of contract " + contract.ContractNo + " (" + contract.Title + ").",
                    contract.Id), cancellationToken);
            }
        }
        return count;
    }
}
