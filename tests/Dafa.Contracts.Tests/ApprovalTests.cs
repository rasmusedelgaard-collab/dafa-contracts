using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Tests;

public class ApprovalRuleMatcherTests
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly Guid Type = Guid.NewGuid();
    private static readonly Guid Department = Guid.NewGuid();

    private static ApprovalRule Rule(string name, decimal? min = null, decimal? max = null, Guid? entityId = null,
        Guid? typeId = null, Guid? departmentId = null, int priority = 0, bool active = true)
    {
        return new ApprovalRule
        {
            Id = Guid.NewGuid(), Name = name, MinValue = min, MaxValue = max, EntityId = entityId, TypeId = typeId,
            DepartmentId = departmentId, Priority = priority, IsActive = active
        };
    }

    [Fact]
    public void ValueBands_LowerBoundInclusive_UpperBoundExclusive()
    {
        var rules = new[]
        {
            Rule("under 1m", null, 1_000_000m),
            Rule("1-5m", 1_000_000m, 5_000_000m),
            Rule("5-20m", 5_000_000m, 20_000_000m),
            Rule("over 20m", 20_000_000m, null)
        };

        Assert.Equal("under 1m", ApprovalRuleMatcher.Match(rules, Entity, Type, Department, 999_999m)!.Name);
        Assert.Equal("1-5m", ApprovalRuleMatcher.Match(rules, Entity, Type, Department, 1_000_000m)!.Name);
        Assert.Equal("5-20m", ApprovalRuleMatcher.Match(rules, Entity, Type, Department, 19_999_999m)!.Name);
        Assert.Equal("over 20m", ApprovalRuleMatcher.Match(rules, Entity, Type, Department, 20_000_000m)!.Name);
    }

    [Fact]
    public void MostSpecificRuleWins()
    {
        var rules = new[]
        {
            Rule("any"),
            Rule("type", typeId: Type),
            Rule("type and entity", typeId: Type, entityId: Entity),
            Rule("other entity", entityId: Guid.NewGuid(), typeId: Type, departmentId: Department)
        };

        Assert.Equal("type and entity", ApprovalRuleMatcher.Match(rules, Entity, Type, Department, 100m)!.Name);
        Assert.Equal("any", ApprovalRuleMatcher.Match(rules, Entity, Guid.NewGuid(), Department, 100m)!.Name);
    }

    [Fact]
    public void PriorityBreaksTies_BetweenEquallySpecificRules()
    {
        var rules = new[]
        {
            Rule("band", null, 1_000_000m, priority: 0),
            Rule("nda", typeId: Type, priority: 10)
        };
        Assert.Equal("nda", ApprovalRuleMatcher.Match(rules, Entity, Type, Department, 100m)!.Name);
    }

    [Fact]
    public void InactiveRules_AreIgnored_AndNoMatchGivesNull()
    {
        var rules = new[] { Rule("inactive", active: false), Rule("high", 5_000_000m, null) };
        Assert.Null(ApprovalRuleMatcher.Match(rules, Entity, Type, Department, 100m));
    }

    [Fact]
    public void Value_IsTheLargerOfTotalAndAnnual_ConvertedToDkk()
    {
        Assert.Equal(2_000_000m, ApprovalRuleMatcher.ValueDkk(500_000m, 2_000_000m, "DKK"));
        // M3: a small total value cannot hide a larger annual value.
        Assert.Equal(500_000m, ApprovalRuleMatcher.ValueDkk(500_000m, 300_000m, "DKK"));
        Assert.Equal(50_000_000m, ApprovalRuleMatcher.ValueDkk(50_000_000m, 1m, "DKK"));
        Assert.Equal(300_000m, ApprovalRuleMatcher.ValueDkk(null, 300_000m, "DKK"));
        Assert.Equal(7_460_000m, ApprovalRuleMatcher.ValueDkk(1_000_000m, null, "EUR"));
        Assert.Equal(0m, ApprovalRuleMatcher.ValueDkk(null, null, "DKK"));
    }
}

public class ApprovalEngineTests
{
    /// <summary>Stage 1: department head and Legal in parallel. Stage 2: Group CFO.</summary>
    private static Contract DraftWithTwoStageRule(TestWorld world)
    {
        world.AddRule("two stages", null, null, null, 0,
            new[] { ApproverKind.DepartmentHead, ApproverKind.Legal },
            new[] { ApproverKind.GroupCFO });
        return world.AddContract(c => c.Status = ContractStatus.Draft);
    }

    private static List<ApprovalStep> Steps(TestWorld world, Guid contractId)
    {
        using var db = world.NewDb();
        return db.ApprovalSteps.AsNoTracking().Where(s => s.ContractId == contractId).ToList();
    }

    private static ApprovalStep StepOf(TestWorld world, Guid contractId, Guid approverId)
    {
        return Steps(world, contractId).Single(s => s.ApproverId == approverId && s.Status != ApprovalStepStatus.Delegated);
    }

    private static Contract Reload(TestWorld world, Guid contractId)
    {
        using var db = world.NewDb();
        return db.Contracts.AsNoTracking().Single(c => c.Id == contractId);
    }

    [Fact]
    public async Task Submit_OpensFirstStage_WithParallelApprovers()
    {
        using var world = new TestWorld();
        var contract = DraftWithTwoStageRule(world);

        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);

        Assert.Equal(ContractStatus.InApproval, Reload(world, contract.Id).Status);
        var steps = Steps(world, contract.Id);
        Assert.Equal(3, steps.Count);
        Assert.Equal(ApprovalStepStatus.Pending, steps.Single(s => s.ApproverId == world.PurchasingHead).Status);
        Assert.Equal(ApprovalStepStatus.Pending, steps.Single(s => s.ApproverId == world.Legal).Status);
        Assert.Equal(ApprovalStepStatus.Waiting, steps.Single(s => s.ApproverId == world.Cfo).Status);

        using var db = world.NewDb();
        Assert.Equal(2, db.OutboxMessages.Count(m => m.Kind == "ApprovalRequested"));
    }

    [Fact]
    public async Task Stage_CompletesOnlyWhenAllParallelApproversApprove_ThenFinalApprovalCreatesLog()
    {
        using var world = new TestWorld();
        var contract = DraftWithTwoStageRule(world);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);

        await world.As(world.PurchasingHead).Approvals.DecideAsync(
            StepOf(world, contract.Id, world.PurchasingHead).Id, ApprovalDecision.Approve, null);

        // One of two has approved: still stage 1.
        Assert.Equal(ApprovalStepStatus.Waiting, StepOf(world, contract.Id, world.Cfo).Status);
        Assert.Equal(ContractStatus.InApproval, Reload(world, contract.Id).Status);

        await world.As(world.Legal).Approvals.DecideAsync(
            StepOf(world, contract.Id, world.Legal).Id, ApprovalDecision.Approve, "Fine");

        // Stage 2 is open now.
        Assert.Equal(ApprovalStepStatus.Pending, StepOf(world, contract.Id, world.Cfo).Status);
        using (var db = world.NewDb())
        {
            Assert.Equal(2, db.ApprovalInstances.Single(i => i.ContractId == contract.Id).CurrentStage);
        }

        await world.As(world.Cfo).Approvals.DecideAsync(
            StepOf(world, contract.Id, world.Cfo).Id, ApprovalDecision.Approve, null);

        Assert.Equal(ContractStatus.Approved, Reload(world, contract.Id).Status);
        using (var db = world.NewDb())
        {
            Assert.Equal(ApprovalStatus.Approved, db.ApprovalInstances.Single(i => i.ContractId == contract.Id).Status);
            var log = db.Documents.Single(d => d.ContractId == contract.Id);
            Assert.Equal(DocumentKind.ApprovalLog, log.Kind);
            Assert.True(world.Store.Files.ContainsKey(log.StoreKey));
            Assert.Equal(1, db.OutboxMessages.Count(m => m.Kind == "ApprovalCompleted" && m.RecipientId == world.Owner));
        }

        // The owner marks it signed and it becomes active.
        await world.As(world.Owner).Commands.SignAsync(contract.Id,
            new Dafa.Contracts.Core.Dtos.SignInput { SignedDate = world.Clock.Today, Method = "Manual" });
        Assert.Equal(ContractStatus.Active, Reload(world, contract.Id).Status);
    }

    [Fact]
    public async Task Reject_ReturnsContractToDraft_WithTheComment()
    {
        using var world = new TestWorld();
        var contract = DraftWithTwoStageRule(world);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var step = StepOf(world, contract.Id, world.Legal);

        await world.As(world.Legal).Approvals.DecideAsync(step.Id, ApprovalDecision.Reject, "Liability cap is too low");

        Assert.Equal(ContractStatus.Draft, Reload(world, contract.Id).Status);
        using var db = world.NewDb();
        Assert.Equal(ApprovalStatus.Rejected, db.ApprovalInstances.Single(i => i.ContractId == contract.Id).Status);
        var rejected = db.ApprovalSteps.Single(s => s.Id == step.Id);
        Assert.Equal(ApprovalStepStatus.Rejected, rejected.Status);
        Assert.Equal("Liability cap is too low", rejected.Comment);
        // Nobody else has an open step any more.
        Assert.False(db.ApprovalSteps.Any(s => s.ContractId == contract.Id && s.Status == ApprovalStepStatus.Pending));
        Assert.Equal(1, db.OutboxMessages.Count(m => m.Kind == "ApprovalRejected" && m.RecipientId == world.Owner));
    }

    [Fact]
    public async Task Reject_RequiresAComment()
    {
        using var world = new TestWorld();
        var contract = DraftWithTwoStageRule(world);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var step = StepOf(world, contract.Id, world.Legal);

        await Assert.ThrowsAsync<InputValidationException>(
            () => world.As(world.Legal).Approvals.DecideAsync(step.Id, ApprovalDecision.Reject, "  "));
        Assert.Equal(ContractStatus.InApproval, Reload(world, contract.Id).Status);
    }

    [Fact]
    public async Task OnlyTheAssignedApprover_CanDecide()
    {
        using var world = new TestWorld();
        var contract = DraftWithTwoStageRule(world);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var legalStep = StepOf(world, contract.Id, world.Legal);
        var cfoStep = StepOf(world, contract.Id, world.Cfo);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => world.As(world.Outsider).Approvals.DecideAsync(legalStep.Id, ApprovalDecision.Approve, null));
        // A step in a later stage is not open yet.
        await Assert.ThrowsAsync<ConflictException>(
            () => world.As(world.Cfo).Approvals.DecideAsync(cfoStep.Id, ApprovalDecision.Approve, null));
    }

    [Fact]
    public async Task Delegate_MovesTheStepToAnotherUser()
    {
        using var world = new TestWorld();
        var contract = DraftWithTwoStageRule(world);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var original = StepOf(world, contract.Id, world.PurchasingHead);

        await world.As(world.PurchasingHead).Approvals.DelegateAsync(original.Id, world.Deputy);

        var steps = Steps(world, contract.Id);
        Assert.Equal(ApprovalStepStatus.Delegated, steps.Single(s => s.Id == original.Id).Status);
        var delegated = steps.Single(s => s.ApproverId == world.Deputy);
        Assert.Equal(ApprovalStepStatus.Pending, delegated.Status);
        Assert.Equal(1, delegated.StageOrder);
        Assert.Equal(world.PurchasingHead, delegated.OriginalApproverId);

        // The original approver can no longer decide.
        await Assert.ThrowsAsync<ConflictException>(
            () => world.As(world.PurchasingHead).Approvals.DecideAsync(original.Id, ApprovalDecision.Approve, null));

        // The delegate and Legal approve: the delegated step does not block the stage.
        await world.As(world.Deputy).Approvals.DecideAsync(delegated.Id, ApprovalDecision.Approve, null);
        await world.As(world.Legal).Approvals.DecideAsync(StepOf(world, contract.Id, world.Legal).Id, ApprovalDecision.Approve, null);
        Assert.Equal(ApprovalStepStatus.Pending, StepOf(world, contract.Id, world.Cfo).Status);
    }

    [Fact]
    public async Task HolidayCover_ReroutesToTheDeputy_WhenTheStageOpens()
    {
        using var world = new TestWorld();
        using (var db = world.NewDb())
        {
            db.HolidayCovers.Add(new HolidayCover
            {
                Id = Guid.NewGuid(),
                UserId = world.Legal,
                DeputyId = world.Deputy,
                FromDate = world.Clock.Today.AddDays(-1),
                ToDate = world.Clock.Today.AddDays(5)
            });
            db.SaveChanges();
        }
        var contract = DraftWithTwoStageRule(world);

        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);

        var steps = Steps(world, contract.Id);
        Assert.DoesNotContain(steps, s => s.ApproverId == world.Legal);
        var covered = steps.Single(s => s.ApproverId == world.Deputy);
        Assert.Equal(world.Legal, covered.OriginalApproverId);
        Assert.Equal(ApprovalStepStatus.Pending, covered.Status);
        Assert.Contains("cover for Legal User", covered.RoleLabel);
    }

    [Fact]
    public async Task HolidayCover_OutsideItsDates_DoesNothing_AndJobReroutesOpenStepsLater()
    {
        using var world = new TestWorld();
        using (var db = world.NewDb())
        {
            db.HolidayCovers.Add(new HolidayCover
            {
                Id = Guid.NewGuid(),
                UserId = world.Legal,
                DeputyId = world.Deputy,
                FromDate = world.Clock.Today.AddDays(10),
                ToDate = world.Clock.Today.AddDays(20)
            });
            db.SaveChanges();
        }
        var contract = DraftWithTwoStageRule(world);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        Assert.Equal(ApprovalStepStatus.Pending, StepOf(world, contract.Id, world.Legal).Status);

        // The holiday starts while the step is still open; the hourly job moves it.
        world.Clock.AdvanceDays(12);
        var result = await world.As(null).Jobs.RunAllAsync();
        Assert.Equal(1, result.StepsRerouted);
        Assert.Empty(result.Errors);
        Assert.Equal(world.Legal, StepOf(world, contract.Id, world.Deputy).OriginalApproverId);

        // Running again does not move it a second time.
        var again = await world.As(null).Jobs.RunAllAsync();
        Assert.Equal(0, again.StepsRerouted);
    }

    [Fact]
    public async Task CurrentApprover_CanReadTheContract_OnlyWhileTheStepIsOpen()
    {
        using var world = new TestWorld();
        var contract = DraftWithTwoStageRule(world);

        // Legal has no department, entity or role on the contract.
        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.Legal).Access.GetReadableAsync(contract.Id));

        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var readable = await world.As(world.Legal).Access.GetReadableAsync(contract.Id);
        Assert.Equal(contract.Id, readable.Id);
        var mine = await world.As(world.Legal).Queries.MyApprovalsAsync();
        Assert.Single(mine);
        Assert.Equal(contract.Id, mine[0].Contract.Id);

        await world.As(world.Legal).Approvals.DecideAsync(StepOf(world, contract.Id, world.Legal).Id, ApprovalDecision.Approve, null);
        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.Legal).Access.GetReadableAsync(contract.Id));
    }

    [Fact]
    public async Task UnassignedRole_FallsBackToAnAdministrator()
    {
        using var world = new TestWorld();
        world.AddRule("it head", null, null, null, 0, new[] { ApproverKind.EntityManager });
        // Entity B has no entity manager.
        var contract = world.AddContract(c =>
        {
            c.Status = ContractStatus.Draft;
            c.EntityId = world.EntityB;
        });

        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);

        var step = Assert.Single(Steps(world, contract.Id));
        Assert.Equal(world.Admin, step.ApproverId);
    }

    [Fact]
    public async Task Submit_WithoutMatchingRule_IsAConflict()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c => c.Status = ContractStatus.Draft);
        await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Commands.SubmitAsync(contract.Id));
        Assert.Equal(ContractStatus.Draft, Reload(world, contract.Id).Status);
    }

    [Fact]
    public async Task Preview_ListsStagesWithResolvedApprovers()
    {
        using var world = new TestWorld();
        world.AddRule("two stages", null, null, null, 0,
            new[] { ApproverKind.DepartmentHead, ApproverKind.Legal },
            new[] { ApproverKind.GroupCFO });

        var preview = await world.As(world.Owner).Approvals.PreviewAsync(world.EntityA, world.TypeSupplier, world.Purchasing, 100_000m, "DKK");

        Assert.Equal(2, preview.Stages.Count);
        Assert.Equal(new[] { "Department head \u00B7 Purchasing Head", "Legal \u00B7 Legal User" }, preview.Stages[0].Approvers);
        Assert.Equal(new[] { "Group CFO \u00B7 Cfo User" }, preview.Stages[1].Approvers);
    }
}
