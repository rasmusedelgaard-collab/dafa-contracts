using System.IO.Compression;
using System.Text;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Dafa.Contracts.Core.Services;
using Dafa.Contracts.Infrastructure.Providers;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Tests;

// Tests for the findings of docs/REVIEW_BACKEND.md (backend phase 2). Service level, on the
// in-memory Sqlite TestWorld. The HTTP-level tests are in ApiSecurityTests.cs.

internal static class ReviewTestHelpers
{
    public static Contract Reload(TestWorld world, Guid contractId)
    {
        using var db = world.NewDb();
        return db.Contracts.AsNoTracking().Single(c => c.Id == contractId);
    }

    public static List<Reminder> Reminders(TestWorld world, Guid contractId)
    {
        using var db = world.NewDb();
        return db.Reminders.AsNoTracking().Where(r => r.ContractId == contractId).ToList();
    }

    public static List<AuditEvent> Audit(TestWorld world, Guid contractId, string action)
    {
        using var db = world.NewDb();
        return db.AuditEvents.AsNoTracking().Where(e => e.ContractId == contractId && e.Action == action).ToList();
    }

    public static List<ApprovalStep> Steps(TestWorld world, Guid contractId)
    {
        using var db = world.NewDb();
        return db.ApprovalSteps.AsNoTracking().Where(s => s.ContractId == contractId).ToList();
    }

    /// <summary>The stored contract as a PUT body that changes nothing.</summary>
    public static ContractInput InputFor(TestWorld world, Guid contractId)
    {
        return ContractValidator.ToInput(Reload(world, contractId));
    }

    public static byte[] PdfBytes(string text = "demo")
    {
        return Encoding.ASCII.GetBytes("%PDF-1.4\n% " + text + "\n%%EOF\n");
    }
}

/// <summary>H1: approved terms are locked for owners; administrators may change them, with an audit trail.</summary>
public class ApprovedTermsLockTests
{
    [Fact]
    public async Task Owner_CannotChangeApprovedTerms_OnAnApprovedOrActiveContract()
    {
        using var world = new TestWorld();
        foreach (var status in new[] { ContractStatus.Approved, ContractStatus.Active, ContractStatus.NoticeGiven })
        {
            var contract = world.AddContract(c => c.Status = status);
            var input = ReviewTestHelpers.InputFor(world, contract.Id);
            input.TotalValue = 50_000_000m;
            input.CounterpartyName = "Somebody else";

            var error = await Assert.ThrowsAsync<ConflictException>(
                () => world.As(world.Owner).Commands.UpdateAsync(contract.Id, input));

            Assert.StartsWith(ApprovedTerms.LockedMessage, error.Message);
            Assert.Equal(ProblemTypes.LockedFields, error.ProblemType);
            Assert.Equal(409, error.StatusCode);
            var stored = ReviewTestHelpers.Reload(world, contract.Id);
            Assert.Null(stored.TotalValue);
            Assert.Equal(contract.CounterpartyName, stored.CounterpartyName);
            Assert.Equal(status, stored.Status);
        }
    }

    [Theory]
    [InlineData("title")]
    [InlineData("endDate")]
    [InlineData("noticePeriodDays")]
    [InlineData("autoRenew")]
    [InlineData("annualValue")]
    [InlineData("currency")]
    [InlineData("governingLaw")]
    [InlineData("changeOfControl")]
    [InlineData("entityId")]
    [InlineData("typeId")]
    public async Task EveryApprovalRelevantField_IsLockedForTheOwner(string field)
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c => c.GoverningLaw = "Danish law");
        var input = ReviewTestHelpers.InputFor(world, contract.Id);
        input.GoverningLaw = "Danish law";
        switch (field)
        {
            case "title": input.Title = "Another title"; break;
            case "endDate": input.EndDate = contract.EndDate!.Value.AddDays(30); break;
            case "noticePeriodDays": input.NoticePeriodDays = 10; break;
            case "autoRenew": input.AutoRenew = true; input.RenewalMonths = 12; break;
            case "annualValue": input.AnnualValue = 1m; break;
            case "currency": input.Currency = "EUR"; break;
            case "governingLaw": input.GoverningLaw = "Swedish law"; break;
            case "changeOfControl": input.ChangeOfControl = true; break;
            case "entityId": input.EntityId = world.EntityB; break;
            case "typeId": input.TypeId = world.TypeNda; break;
        }

        await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Commands.UpdateAsync(contract.Id, input));
        Assert.Contains(field, ApprovedTerms.LockedFields);
    }

    [Fact]
    public async Task Owner_CanStillEditRolesPaymentTermsAndSummary_AfterApproval_WithOldAndNewValuesAudited()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        var input = ReviewTestHelpers.InputFor(world, contract.Id);
        input.PaymentTerms = "Net 45 days";
        input.Summary = "Updated summary";
        input.DelegateId = world.PurchasingMember;
        input.ResponsibleId = world.PurchasingHead;
        input.ReminderRecipient = ReminderRecipient.Responsible;

        await world.As(world.Owner).Commands.UpdateAsync(contract.Id, input);

        var stored = ReviewTestHelpers.Reload(world, contract.Id);
        Assert.Equal("Net 45 days", stored.PaymentTerms);
        Assert.Equal(world.PurchasingMember, stored.DelegateId);
        Assert.Equal(ReminderRecipient.Responsible, stored.ReminderRecipient);

        var audit = Assert.Single(ReviewTestHelpers.Audit(world, contract.Id, "contract.updated"));
        Assert.Contains("Payment terms: \"(empty)\" -> \"Net 45 days\"", audit.Text);
        Assert.Contains("Delegate: \"(empty)\" -> \"Purchasing Member\"", audit.Text);
        Assert.Empty(ReviewTestHelpers.Audit(world, contract.Id, "contract.approved-terms-changed"));
    }

    [Fact]
    public async Task Admin_CanChangeApprovedTerms_AndEveryFieldIsAuditedWithOldAndNewValue()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        var input = ReviewTestHelpers.InputFor(world, contract.Id);
        input.AnnualValue = 750_000m;
        // Release 4: the counterparty changes by choosing another master record, not by typing a name.
        input.SupplierId = world.AddSupplier("New Counterparty A/S");

        await world.As(world.Admin).Commands.UpdateAsync(contract.Id, input);

        var stored = ReviewTestHelpers.Reload(world, contract.Id);
        Assert.Equal(750_000m, stored.AnnualValue);
        Assert.Equal("New Counterparty A/S", stored.CounterpartyName);
        Assert.Equal(ContractStatus.Active, stored.Status);

        var audit = Assert.Single(ReviewTestHelpers.Audit(world, contract.Id, "contract.approved-terms-changed"));
        Assert.Contains("Annual value: \"500000\" -> \"750000\"", audit.Text);
        Assert.Contains("Counterparty: \"" + contract.CounterpartyName + "\" -> \"New Counterparty A/S\"", audit.Text);
        Assert.Equal("Admin User", audit.UserName);
    }

    [Fact]
    public async Task InDraft_EverythingStaysEditable_AndChangesAreAudited()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c => c.Status = ContractStatus.Draft);
        var input = ReviewTestHelpers.InputFor(world, contract.Id);
        input.Title = "Renamed draft";
        input.TotalValue = 2_000_000m;

        await world.As(world.Owner).Commands.UpdateAsync(contract.Id, input);

        var stored = ReviewTestHelpers.Reload(world, contract.Id);
        Assert.Equal("Renamed draft", stored.Title);
        Assert.Equal(2_000_000m, stored.TotalValue);
        var audit = Assert.Single(ReviewTestHelpers.Audit(world, contract.Id, "contract.updated"));
        Assert.Contains("Title: \"" + contract.Title + "\" -> \"Renamed draft\"", audit.Text);
        Assert.Contains("Total value: \"(empty)\" -> \"2000000\"", audit.Text);
    }

    [Fact]
    public async Task EmptyLockedFields_CanBeFilledInOnce_ByTheOwner_AndAreLockedAfterwards()
    {
        using var world = new TestWorld();
        // An imported, active contract with missing data.
        var contract = world.AddContract(c =>
        {
            c.NoticePeriodDays = null;
            c.AnnualValue = null;
            c.TotalValue = null;
            c.ChangeOfControl = null;
            c.GoverningLaw = null;
        });
        var before = await world.As(world.Owner).Queries.GetDetailAsync(contract.Id, false);
        Assert.DoesNotContain("noticePeriodDays", before.LockedFields);
        Assert.DoesNotContain("annualValue", before.LockedFields);
        Assert.DoesNotContain("totalValue", before.LockedFields);
        Assert.DoesNotContain("changeOfControl", before.LockedFields);
        Assert.DoesNotContain("governingLaw", before.LockedFields);
        Assert.Contains("title", before.LockedFields);
        Assert.Contains("endDate", before.LockedFields);

        var input = ReviewTestHelpers.InputFor(world, contract.Id);
        input.NoticePeriodDays = 60;
        input.AnnualValue = 120_000m;
        input.TotalValue = 360_000m;
        input.ChangeOfControl = true;
        input.GoverningLaw = "Danish law";
        await world.As(world.Owner).Commands.UpdateAsync(contract.Id, input);

        var stored = ReviewTestHelpers.Reload(world, contract.Id);
        Assert.Equal(60, stored.NoticePeriodDays);
        Assert.Equal(120_000m, stored.AnnualValue);
        Assert.Equal(360_000m, stored.TotalValue);
        Assert.True(stored.ChangeOfControl);
        Assert.Empty(ContractCalculations.MissingData(stored));
        var audit = Assert.Single(ReviewTestHelpers.Audit(world, contract.Id, "contract.missing-data-completed"));
        Assert.Contains("Notice period (days): \"(empty)\" -> \"60\"", audit.Text);
        Assert.Contains("Change of control: \"(empty)\" -> \"Yes\"", audit.Text);
        Assert.Empty(ReviewTestHelpers.Audit(world, contract.Id, "contract.approved-terms-changed"));

        // Once filled in, the fields are locked like any other approved term.
        var after = await world.As(world.Owner).Queries.GetDetailAsync(contract.Id, false);
        Assert.Contains("noticePeriodDays", after.LockedFields);
        Assert.Contains("annualValue", after.LockedFields);
        Assert.Contains("changeOfControl", after.LockedFields);
        foreach (var change in new Action<ContractInput>[]
        {
            i => i.NoticePeriodDays = 30,
            i => i.AnnualValue = 1m,
            i => i.ChangeOfControl = false,
            i => i.GoverningLaw = "Swedish law"
        })
        {
            var second = ReviewTestHelpers.InputFor(world, contract.Id);
            second.GoverningLaw = "Danish law";
            change(second);
            var error = await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Commands.UpdateAsync(contract.Id, second));
            Assert.Equal(ProblemTypes.LockedFields, error.ProblemType);
        }
        Assert.Equal(60, ReviewTestHelpers.Reload(world, contract.Id).NoticePeriodDays);
    }

    [Fact]
    public async Task AnEmptyValueField_CannotBeFilledIn_WhenTheOtherValueIsAlreadySet()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();                       // annual value 500,000, no total value
        var detail = await world.As(world.Owner).Queries.GetDetailAsync(contract.Id, false);
        Assert.Contains("totalValue", detail.LockedFields);

        // Otherwise an approved 500,000 contract could become a 50m one: approval looks at the larger value.
        var input = ReviewTestHelpers.InputFor(world, contract.Id);
        input.TotalValue = 50_000_000m;
        await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Commands.UpdateAsync(contract.Id, input));

        await world.As(world.Admin).Commands.UpdateAsync(contract.Id, input);
        Assert.Equal(50_000_000m, ReviewTestHelpers.Reload(world, contract.Id).TotalValue);
    }

    [Fact]
    public async Task Detail_ListsLockedFields_ForOwnersOfApprovedContractsOnly()
    {
        using var world = new TestWorld();
        var active = world.AddContract();
        var draft = world.AddContract(c => c.Status = ContractStatus.Draft);

        var ownerView = await world.As(world.Owner).Queries.GetDetailAsync(active.Id, false);
        var adminView = await world.As(world.Admin).Queries.GetDetailAsync(active.Id, false);
        var draftView = await world.As(world.Owner).Queries.GetDetailAsync(draft.Id, false);

        Assert.True(ownerView.CanEdit);
        Assert.Contains("totalValue", ownerView.LockedFields);
        Assert.Contains("counterpartyName", ownerView.LockedFields);
        Assert.Contains("endDate", ownerView.LockedFields);
        Assert.DoesNotContain("paymentTerms", ownerView.LockedFields);
        Assert.DoesNotContain("ownerId", ownerView.LockedFields);
        Assert.Empty(adminView.LockedFields);
        Assert.Empty(draftView.LockedFields);
    }
}

/// <summary>H2: a changed notice deadline starts a new reminder cycle.</summary>
public class NoticeDeadlineChangeTests
{
    [Fact]
    public async Task EditingTheEndDate_RemovesOldReminders_ClearsTheStaleDecision_AndRemindsForTheNewDeadline()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();                       // deadline in 60 days
        await world.As(null).Jobs.RunAllAsync();
        Assert.Single(ReviewTestHelpers.Reminders(world, contract.Id));
        await world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id,
            new RenewalDecisionInput { Decision = RenewalDecision.Renegotiate, Comment = "Talking to the supplier" });

        // The renegotiated contract runs a year longer; an administrator corrects the end date.
        var input = ReviewTestHelpers.InputFor(world, contract.Id);
        input.EndDate = contract.EndDate!.Value.AddDays(365);
        await world.As(world.Admin).Commands.UpdateAsync(contract.Id, input);

        var stored = ReviewTestHelpers.Reload(world, contract.Id);
        Assert.Null(stored.RenewalDecision);
        Assert.Null(stored.RenewalDecidedAt);
        Assert.Empty(ReviewTestHelpers.Reminders(world, contract.Id));
        Assert.Single(ReviewTestHelpers.Audit(world, contract.Id, "reminders.reset"));

        // New deadline is 425 days away. 305 days later it is 120 days away: the cycle runs again.
        world.Clock.AdvanceDays(305);
        var result = await world.As(null).Jobs.RunAllAsync();
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.NoticeReminders);
        var reminder = Assert.Single(ReviewTestHelpers.Reminders(world, contract.Id));
        Assert.Equal("Notice120", reminder.Kind);
        Assert.Equal(ContractCalculations.NoticeTermKey(stored.EndDate!.Value.AddDays(-90)), reminder.TermKey);

        // And the escalation is sent for the new deadline too.
        world.Clock.AdvanceDays(106);
        var later = await world.As(null).Jobs.RunAllAsync();
        Assert.Equal(1, later.Escalations);
    }

    [Fact]
    public async Task EditingTheNoticePeriod_AlsoRestartsTheCycle_ButOtherEditsDoNot()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        await world.As(null).Jobs.RunAllAsync();
        Assert.Single(ReviewTestHelpers.Reminders(world, contract.Id));

        var unrelated = ReviewTestHelpers.InputFor(world, contract.Id);
        unrelated.Summary = "No change to the term";
        await world.As(world.Owner).Commands.UpdateAsync(contract.Id, unrelated);
        Assert.Single(ReviewTestHelpers.Reminders(world, contract.Id));

        var input = ReviewTestHelpers.InputFor(world, contract.Id);
        input.NoticePeriodDays = 30;
        await world.As(world.Admin).Commands.UpdateAsync(contract.Id, input);
        Assert.Empty(ReviewTestHelpers.Reminders(world, contract.Id));
    }

    [Fact]
    public async Task AutomaticRollForward_RestartsTheCycle()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c =>
        {
            c.AutoRenew = true;
            c.RenewalMonths = 12;
        });
        await world.As(null).Jobs.RunAllAsync();
        Assert.Single(ReviewTestHelpers.Reminders(world, contract.Id));

        world.Clock.AdvanceDays(151);                             // the end date has passed
        var result = await world.As(null).Jobs.RunAllAsync();

        Assert.Equal(1, result.ContractsRenewed);
        var stored = ReviewTestHelpers.Reload(world, contract.Id);
        Assert.Equal(contract.EndDate!.Value.AddMonths(12), stored.EndDate);
        // No row of the old term is left to block the new one.
        Assert.DoesNotContain(ReviewTestHelpers.Reminders(world, contract.Id),
            r => r.TermKey == ContractCalculations.NoticeTermKey(contract.EndDate.Value.AddDays(-90)));
    }
}

/// <summary>M1: concurrency tokens, idempotent decide and submit, one pending approval per contract.</summary>
public class ApprovalConcurrencyTests
{
    private static Contract DraftWithParallelStage(TestWorld world)
    {
        world.AddRule("two stages", null, null, null, 0,
            new[] { ApproverKind.DepartmentHead, ApproverKind.Legal },
            new[] { ApproverKind.GroupCFO });
        return world.AddContract(c => c.Status = ContractStatus.Draft);
    }

    private static ApprovalStep StepOf(TestWorld world, Guid contractId, Guid approverId) =>
        ReviewTestHelpers.Steps(world, contractId).Single(s => s.ApproverId == approverId && s.Status != ApprovalStepStatus.Delegated);

    [Fact]
    public void StaleCopy_OfAVersionedRow_CannotBeSaved()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        using var first = world.NewDb();
        using var second = world.NewDb();
        var copyA = first.Contracts.Single(c => c.Id == contract.Id);
        var copyB = second.Contracts.Single(c => c.Id == contract.Id);
        Assert.NotEqual(Guid.Empty, copyA.Version);

        copyA.Title = "Saved first";
        first.SaveChanges();
        copyB.Title = "Based on a stale copy";

        Assert.Throws<DbUpdateConcurrencyException>(() => second.SaveChanges());
        Assert.Equal("Saved first", ReviewTestHelpers.Reload(world, contract.Id).Title);
    }

    [Fact]
    public async Task DecidingTheSameStepTwice_IsAConflict_AndCreatesOneApprovalLog()
    {
        using var world = new TestWorld();
        world.AddRule("legal only", null, null, null, 0, new[] { ApproverKind.Legal });
        var contract = world.AddContract(c => c.Status = ContractStatus.Draft);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var step = StepOf(world, contract.Id, world.Legal);

        await world.As(world.Legal).Approvals.DecideAsync(step.Id, ApprovalDecision.Approve, null);
        await Assert.ThrowsAsync<ConflictException>(
            () => world.As(world.Legal).Approvals.DecideAsync(step.Id, ApprovalDecision.Approve, null));
        await Assert.ThrowsAsync<ConflictException>(
            () => world.As(world.Legal).Approvals.DecideAsync(step.Id, ApprovalDecision.Reject, "Changed my mind"));

        using var db = world.NewDb();
        Assert.Equal(ContractStatus.Approved, db.Contracts.Single(c => c.Id == contract.Id).Status);
        Assert.Equal(1, db.Documents.Count(d => d.ContractId == contract.Id && d.Kind == DocumentKind.ApprovalLog));
        Assert.Equal(1, db.OutboxMessages.Count(m => m.Kind == "ApprovalCompleted"));
    }

    [Fact]
    public async Task DecidingOnAClosedApproval_IsAConflict()
    {
        using var world = new TestWorld();
        var contract = DraftWithParallelStage(world);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var headStep = StepOf(world, contract.Id, world.PurchasingHead);
        await world.As(world.Legal).Approvals.DecideAsync(
            StepOf(world, contract.Id, world.Legal).Id, ApprovalDecision.Reject, "Not like this");

        await Assert.ThrowsAsync<ConflictException>(
            () => world.As(world.PurchasingHead).Approvals.DecideAsync(headStep.Id, ApprovalDecision.Approve, null));
        Assert.Equal(ContractStatus.Draft, ReviewTestHelpers.Reload(world, contract.Id).Status);
    }

    [Fact]
    public async Task SubmittingTwice_IsAConflict_AndThereIsOnlyOneApproval()
    {
        using var world = new TestWorld();
        var contract = DraftWithParallelStage(world);

        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Commands.SubmitAsync(contract.Id));

        using var db = world.NewDb();
        Assert.Equal(1, db.ApprovalInstances.Count(i => i.ContractId == contract.Id));
    }

    [Fact]
    public async Task TwoSubmitsBasedOnTheSameDraft_OnlyOneWins()
    {
        using var world = new TestWorld();
        var contract = DraftWithParallelStage(world);
        // The second request reads the draft, then the first request submits it, then the second saves.
        var clock = new InterleavingClock(world.Clock);
        var second = world.As(world.Owner, clock);
        clock.Arm(() => Task.Run(() => world.As(world.Owner).Commands.SubmitAsync(contract.Id)).GetAwaiter().GetResult());

        await Assert.ThrowsAsync<ConflictException>(() => second.Commands.SubmitAsync(contract.Id));

        using var db = world.NewDb();
        Assert.Equal(1, db.ApprovalInstances.Count(i => i.ContractId == contract.Id));
        Assert.Equal(ContractStatus.InApproval, db.Contracts.Single(c => c.Id == contract.Id).Status);
        Assert.Equal(2, db.ApprovalSteps.Count(s => s.ContractId == contract.Id && s.Status == ApprovalStepStatus.Pending));
    }

    [Fact]
    public void TheDatabase_RefusesASecondPendingApprovalForOneContract()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c => c.Status = ContractStatus.Draft);
        using var db = world.NewDb();
        db.ApprovalInstances.Add(new ApprovalInstance { Id = Guid.NewGuid(), ContractId = contract.Id, Status = ApprovalStatus.Pending, StartedAt = world.Clock.UtcNow });
        db.SaveChanges();
        // A closed one next to it is fine.
        db.ApprovalInstances.Add(new ApprovalInstance { Id = Guid.NewGuid(), ContractId = contract.Id, Status = ApprovalStatus.Rejected, StartedAt = world.Clock.UtcNow });
        db.SaveChanges();

        db.ApprovalInstances.Add(new ApprovalInstance { Id = Guid.NewGuid(), ContractId = contract.Id, Status = ApprovalStatus.Pending, StartedAt = world.Clock.UtcNow });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task TwoParallelApprovers_ApprovingAtTheSameMoment_StillAdvanceTheStage()
    {
        using var world = new TestWorld();
        var contract = DraftWithParallelStage(world);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var headStep = StepOf(world, contract.Id, world.PurchasingHead);
        var legalStep = StepOf(world, contract.Id, world.Legal);

        // The department head's request has loaded the steps (Legal still pending) when Legal's
        // approval is saved. Without the concurrency token both would see the other as pending
        // and the stage would never complete.
        var clock = new InterleavingClock(world.Clock);
        var head = world.As(world.PurchasingHead, clock);
        clock.Arm(() => Task.Run(() => world.As(world.Legal).Approvals.DecideAsync(legalStep.Id, ApprovalDecision.Approve, null))
            .GetAwaiter().GetResult());

        await head.Approvals.DecideAsync(headStep.Id, ApprovalDecision.Approve, null);

        var steps = ReviewTestHelpers.Steps(world, contract.Id);
        Assert.Equal(ApprovalStepStatus.Approved, steps.Single(s => s.Id == headStep.Id).Status);
        Assert.Equal(ApprovalStepStatus.Approved, steps.Single(s => s.Id == legalStep.Id).Status);
        Assert.Equal(ApprovalStepStatus.Pending, steps.Single(s => s.ApproverId == world.Cfo).Status);
        using var db = world.NewDb();
        Assert.Equal(2, db.ApprovalInstances.Single(i => i.ContractId == contract.Id).CurrentStage);
        // The retried decision is recorded once.
        Assert.Equal(2, db.AuditEvents.Count(e => e.ContractId == contract.Id && e.Action == "approval.approved"));
        Assert.Equal(1, db.OutboxMessages.Count(m => m.Kind == "ApprovalRequested" && m.RecipientId == world.Cfo));
    }
}

/// <summary>M2: nobody approves their own contract; delegation rules.</summary>
public class SeparationOfDutiesTests
{
    [Fact]
    public async Task DepartmentHead_WhoOwnsTheContract_IsReplacedByTheEntityManager()
    {
        using var world = new TestWorld();
        world.AddRule("head only", null, null, null, 0, new[] { ApproverKind.DepartmentHead });
        var contract = world.AddContract(c =>
        {
            c.Status = ContractStatus.Draft;
            c.OwnerId = world.PurchasingHead;
        });

        await world.As(world.PurchasingHead).Commands.SubmitAsync(contract.Id);

        var step = Assert.Single(ReviewTestHelpers.Steps(world, contract.Id));
        Assert.Equal(world.EntityAManager, step.ApproverId);
        Assert.StartsWith("Department head", step.RoleLabel);
        Assert.Contains("instead of Purchasing Head", step.RoleLabel);
        Assert.Equal(ApprovalStepStatus.Pending, step.Status);
    }

    [Fact]
    public async Task WithoutAnEntityManager_TheSubstituteIsTheGroupCfo_ThenTheGroupCeo()
    {
        using var world = new TestWorld();
        world.AddRule("head only", null, null, null, 0, new[] { ApproverKind.DepartmentHead });
        var contract = world.AddContract(c =>
        {
            c.Status = ContractStatus.Draft;
            c.OwnerId = world.PurchasingHead;
            c.EntityId = world.EntityB;                           // nobody manages Entity B
        });
        await world.As(world.PurchasingHead).Commands.SubmitAsync(contract.Id);
        Assert.Equal(world.Cfo, Assert.Single(ReviewTestHelpers.Steps(world, contract.Id)).ApproverId);

        // Pure resolver: when the CFO is excluded as well, the CEO is next.
        using var db = world.NewDb();
        var directory = await ApproverDirectory.LoadAsync(db);
        var substitute = directory.Substitute(world.EntityB, new HashSet<Guid> { world.PurchasingHead, world.Cfo });
        Assert.Equal(world.Ceo, substitute!.Id);
    }

    [Fact]
    public async Task TheSubmitter_IsExcludedToo_EvenWhenNotTheOwner()
    {
        using var world = new TestWorld();
        // Entity B has no manager, so the slot would fall back to an administrator: the only
        // administrator is the one submitting.
        world.AddRule("entity manager", null, null, null, 0, new[] { ApproverKind.EntityManager });
        var contract = world.AddContract(c =>
        {
            c.Status = ContractStatus.Draft;
            c.EntityId = world.EntityB;
        });

        await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Admin).Commands.SubmitAsync(contract.Id));
        Assert.Equal(ContractStatus.Draft, ReviewTestHelpers.Reload(world, contract.Id).Status);

        // Submitted by the owner, the administrator is an acceptable fallback.
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        Assert.Equal(world.Admin, Assert.Single(ReviewTestHelpers.Steps(world, contract.Id)).ApproverId);
    }

    [Fact]
    public async Task ASpecificUserRule_NamingTheOwner_IsSubstitutedAsWell()
    {
        using var world = new TestWorld();
        var rule = new ApprovalRule
        {
            Id = Guid.NewGuid(),
            Name = "named approver",
            IsActive = true,
            StagesJson = JsonDefaults.Write(new List<ApprovalStageDefinition>
            {
                new() { Order = 1, Approvers = new List<ApproverDefinition> { new() { Kind = ApproverKind.User, UserId = world.ItHead } } }
            })
        };
        using (var db = world.NewDb())
        {
            db.ApprovalRules.Add(rule);
            db.SaveChanges();
        }
        var contract = world.AddContract(c =>
        {
            c.Status = ContractStatus.Draft;
            c.OwnerId = world.ItHead;
        });

        await world.As(world.ItHead).Commands.SubmitAsync(contract.Id);

        Assert.Equal(world.EntityAManager, Assert.Single(ReviewTestHelpers.Steps(world, contract.Id)).ApproverId);
    }

    [Fact]
    public async Task Delegation_MayNotTargetTheOwnerOrANonApprover_AndAnAdminCannotTakeTheStep()
    {
        using var world = new TestWorld();
        world.AddRule("legal only", null, null, null, 0, new[] { ApproverKind.Legal });
        var contract = world.AddContract(c => c.Status = ContractStatus.Draft);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var step = Assert.Single(ReviewTestHelpers.Steps(world, contract.Id));

        // The owner (who is also the submitter here).
        await Assert.ThrowsAsync<InputValidationException>(() => world.As(world.Legal).Approvals.DelegateAsync(step.Id, world.Owner));
        // No approver role.
        await Assert.ThrowsAsync<InputValidationException>(() => world.As(world.Legal).Approvals.DelegateAsync(step.Id, world.Outsider));
        // Oneself.
        await Assert.ThrowsAsync<InputValidationException>(() => world.As(world.Legal).Approvals.DelegateAsync(step.Id, world.Legal));
        // An administrator may reassign, but not to themselves.
        await Assert.ThrowsAsync<InputValidationException>(() => world.As(world.Admin).Approvals.DelegateAsync(step.Id, world.Admin));
        // Somebody without a stake in the step cannot delegate it at all.
        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.Outsider).Approvals.DelegateAsync(step.Id, world.Deputy));

        Assert.Equal(ApprovalStepStatus.Pending, Assert.Single(ReviewTestHelpers.Steps(world, contract.Id)).Status);
    }

    [Fact]
    public async Task AdminReassignment_IsRecordedDistinctly()
    {
        using var world = new TestWorld();
        world.AddRule("legal only", null, null, null, 0, new[] { ApproverKind.Legal });
        var contract = world.AddContract(c => c.Status = ContractStatus.Draft);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var step = Assert.Single(ReviewTestHelpers.Steps(world, contract.Id));

        await world.As(world.Admin).Approvals.DelegateAsync(step.Id, world.Deputy);

        var moved = ReviewTestHelpers.Steps(world, contract.Id).Single(s => s.ApproverId == world.Deputy);
        Assert.Contains("reassigned from Legal User by administrator Admin User", moved.RoleLabel);
        Assert.Single(ReviewTestHelpers.Audit(world, contract.Id, "approval.reassigned"));
        Assert.Empty(ReviewTestHelpers.Audit(world, contract.Id, "approval.delegated"));
    }

    [Fact]
    public async Task HolidayCover_NeverRoutesTheStepToTheOwner()
    {
        using var world = new TestWorld();
        using (var db = world.NewDb())
        {
            db.HolidayCovers.Add(new HolidayCover
            {
                Id = Guid.NewGuid(),
                UserId = world.Legal,
                DeputyId = world.Owner,
                FromDate = world.Clock.Today.AddDays(-1),
                ToDate = world.Clock.Today.AddDays(5)
            });
            db.SaveChanges();
        }
        world.AddRule("legal only", null, null, null, 0, new[] { ApproverKind.Legal });
        var contract = world.AddContract(c => c.Status = ContractStatus.Draft);

        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var result = await world.As(null).Jobs.RunAllAsync();

        Assert.Equal(0, result.StepsRerouted);
        Assert.Equal(world.Legal, Assert.Single(ReviewTestHelpers.Steps(world, contract.Id)).ApproverId);
    }
}

/// <summary>M3: the approval route cannot be steered by the value basis or the type label.</summary>
public class ApprovalRouteTests
{
    [Fact]
    public async Task ALargeAnnualValue_WithATinyTotal_IsRoutedByTheAnnualValue()
    {
        using var world = new TestWorld();
        world.AddRule("under 1m", null, 1_000_000m, null, 0, new[] { ApproverKind.DepartmentHead });
        world.AddRule("over 20m", 20_000_000m, null, null, 0, new[] { ApproverKind.GroupCFO }, new[] { ApproverKind.GroupCEO });
        var contract = world.AddContract(c =>
        {
            c.Status = ContractStatus.Draft;
            c.AnnualValue = 50_000_000m;
            c.TotalValue = 1m;
        });

        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);

        using var db = world.NewDb();
        Assert.Equal("over 20m", db.ApprovalInstances.Single(i => i.ContractId == contract.Id).RuleName);
    }

    [Fact]
    public async Task NoMatchingRule_IsA409_WithItsOwnProblemType_NotAStaleUpdate()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c => c.Status = ContractStatus.Draft);

        var error = await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Commands.SubmitAsync(contract.Id));

        Assert.Equal(ProblemTypes.NoApprovalRule, error.ProblemType);
        Assert.NotEqual(ProblemTypes.Stale, error.ProblemType);

        // A second submit of an already submitted contract is an invalid state.
        world.AddRule("any", null, null, null, 0, new[] { ApproverKind.Legal });
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var again = await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Commands.SubmitAsync(contract.Id));
        Assert.Equal(ProblemTypes.InvalidState, again.ProblemType);
    }

    [Fact]
    public void ATypeRuleWithAMaximumValue_DoesNotCatchLargeContractsOfThatType()
    {
        var nda = Guid.NewGuid();
        var rules = new[]
        {
            new ApprovalRule { Id = Guid.NewGuid(), Name = "under 1m", MaxValue = 1_000_000m, IsActive = true },
            new ApprovalRule { Id = Guid.NewGuid(), Name = "5-20m", MinValue = 5_000_000m, MaxValue = 20_000_000m, IsActive = true },
            // As seeded: type NDA with a cap, priority 10.
            new ApprovalRule { Id = Guid.NewGuid(), Name = "NDA", TypeId = nda, MaxValue = 1_000_000m, Priority = 10, IsActive = true }
        };

        Assert.Equal("NDA", ApprovalRuleMatcher.Match(rules, null, nda, null, 0m)!.Name);
        Assert.Equal("NDA", ApprovalRuleMatcher.Match(rules, null, nda, null, 999_999m)!.Name);
        Assert.Equal("5-20m", ApprovalRuleMatcher.Match(rules, null, nda, null, 8_000_000m)!.Name);
        Assert.Equal("under 1m", ApprovalRuleMatcher.Match(rules, null, Guid.NewGuid(), null, 100m)!.Name);
    }
}

/// <summary>M4 and M5: renewal decisions and open-ended contracts.</summary>
public class RenewalRuleTests
{
    [Fact]
    public async Task Renew_OnAnAutoRenewContract_WorksOncePerTerm()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c =>
        {
            c.AutoRenew = true;
            c.RenewalMonths = 12;
        });
        var renew = new RenewalDecisionInput { Decision = RenewalDecision.Renew };

        await world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id, renew);
        // A double click, or a second try: the new term's reminder window has not started.
        await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id, renew));

        var stored = ReviewTestHelpers.Reload(world, contract.Id);
        Assert.Equal(contract.EndDate!.Value.AddMonths(12), stored.EndDate);
        Assert.Equal(contract.EndDate.Value.AddDays(-90), stored.LastRenewedDeadline);
    }

    [Fact]
    public async Task Renew_IsRefused_BeforeTheReminderWindow_AndForATermThatWasAlreadyRenewed()
    {
        using var world = new TestWorld();
        var early = world.AddContract(c =>
        {
            c.AutoRenew = true;
            c.RenewalMonths = 12;
            c.EndDate = world.Clock.Today.AddDays(400);           // deadline in 310 days, window opens at 120
        });
        var renew = new RenewalDecisionInput { Decision = RenewalDecision.Renew };
        await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Commands.DecideRenewalAsync(early.Id, renew));
        Assert.Equal(early.EndDate, ReviewTestHelpers.Reload(world, early.Id).EndDate);

        var renewed = world.AddContract(c =>
        {
            c.AutoRenew = true;
            c.RenewalMonths = 12;
            c.LastRenewedDeadline = c.EndDate!.Value.AddDays(-90); // the current term is marked as renewed
        });
        var error = await Assert.ThrowsAsync<ConflictException>(() => world.As(world.Owner).Commands.DecideRenewalAsync(renewed.Id, renew));
        Assert.Contains("already been renewed", error.Message);

        // After the deadline has passed (still running) a renewal can still be recorded.
        var late = world.AddContract(c =>
        {
            c.AutoRenew = true;
            c.RenewalMonths = 12;
            c.EndDate = world.Clock.Today.AddDays(20);            // deadline was 70 days ago
        });
        await world.As(world.Owner).Commands.DecideRenewalAsync(late.Id, renew);
        Assert.Equal(late.EndDate!.Value.AddMonths(12), ReviewTestHelpers.Reload(world, late.Id).EndDate);
    }

    [Fact]
    public async Task Renew_OnAFixedTermContract_NeedsANewEndDate_AndStartsANewTerm()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();                       // not auto-renew, deadline in 60 days
        await world.As(null).Jobs.RunAllAsync();
        Assert.Single(ReviewTestHelpers.Reminders(world, contract.Id));

        var missing = await Assert.ThrowsAsync<InputValidationException>(
            () => world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id, new RenewalDecisionInput { Decision = RenewalDecision.Renew }));
        Assert.True(missing.Errors.ContainsKey("newEndDate"));
        await Assert.ThrowsAsync<InputValidationException>(
            () => world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id,
                new RenewalDecisionInput { Decision = RenewalDecision.Renew, NewEndDate = contract.EndDate }));

        var newEnd = contract.EndDate!.Value.AddYears(2);
        await world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id,
            new RenewalDecisionInput { Decision = RenewalDecision.Renew, NewEndDate = newEnd, Comment = "Extended two years" });

        var stored = ReviewTestHelpers.Reload(world, contract.Id);
        Assert.Equal(newEnd, stored.EndDate);
        Assert.Equal(ContractStatus.Active, stored.Status);
        Assert.Null(stored.RenewalDecision);                       // reminders run again for the new term
        Assert.Empty(ReviewTestHelpers.Reminders(world, contract.Id));
        Assert.Single(ReviewTestHelpers.Audit(world, contract.Id, "renewal.renewed"));
    }

    [Fact]
    public async Task LetExpire_IsRejected_OnAnAutoRenewContract()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c =>
        {
            c.AutoRenew = true;
            c.RenewalMonths = 12;
        });

        var error = await Assert.ThrowsAsync<InputValidationException>(
            () => world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id,
                new RenewalDecisionInput { Decision = RenewalDecision.LetExpire, Comment = "No longer needed" }));

        Assert.Equal(400, error.StatusCode);
        Assert.True(error.Errors.ContainsKey("decision"));
        Assert.Null(ReviewTestHelpers.Reload(world, contract.Id).RenewalDecision);
    }

    [Fact]
    public async Task OpenEndedContract_AcceptsGiveNoticeAtAnyTime_AndThenEndsAfterTheNoticePeriod()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c =>
        {
            c.EndDate = null;
            c.NoticePeriodDays = 180;
        });

        var detail = await world.As(world.Owner).Queries.GetDetailAsync(contract.Id, false);
        Assert.True(detail.CanDecideRenewal);
        Assert.True(detail.OpenEnded);

        // Nothing to renew or let expire.
        await Assert.ThrowsAsync<ConflictException>(
            () => world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id, new RenewalDecisionInput { Decision = RenewalDecision.Renew }));
        await Assert.ThrowsAsync<InputValidationException>(
            () => world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id, new RenewalDecisionInput { Decision = RenewalDecision.GiveNotice }));

        await world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id,
            new RenewalDecisionInput { Decision = RenewalDecision.GiveNotice, Comment = "Service no longer needed" });

        var stored = ReviewTestHelpers.Reload(world, contract.Id);
        Assert.Equal(ContractStatus.NoticeGiven, stored.Status);
        Assert.Equal(world.Clock.Today.AddDays(180), stored.EndDate);
        Assert.Equal(RenewalDecision.GiveNotice, stored.RenewalDecision);

        // The job ends it when the notice period is over.
        world.Clock.AdvanceDays(181);
        var result = await world.As(null).Jobs.RunAllAsync();
        Assert.Empty(result.Errors);
        Assert.Equal(ContractStatus.Terminated, ReviewTestHelpers.Reload(world, contract.Id).Status);
    }

    [Fact]
    public async Task OpenEndedContract_GetsAReviewReminder_60DaysBeforeEachAnniversary_Once()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c =>
        {
            c.EndDate = null;
            c.StartDate = world.Clock.Today.AddYears(-1).AddDays(90);   // first anniversary in 90 days
        });
        var anniversary = contract.StartDate.AddYears(1);

        var early = await world.As(null).Jobs.RunAllAsync();
        Assert.Equal(0, early.AnnualReviewReminders);
        Assert.Empty(ReviewTestHelpers.Reminders(world, contract.Id));

        world.Clock.AdvanceDays(30);                              // 60 days before the anniversary
        var due = await world.As(null).Jobs.RunAllAsync();
        var again = await world.As(null).Jobs.RunAllAsync();
        Assert.Empty(due.Errors);
        Assert.Equal(1, due.AnnualReviewReminders);
        Assert.Equal(0, again.AnnualReviewReminders);
        var reminder = Assert.Single(ReviewTestHelpers.Reminders(world, contract.Id));
        Assert.Equal(ContractCalculations.AnnualReviewKind, reminder.Kind);
        Assert.Equal(anniversary.AddDays(-60), reminder.DueDate);
        Assert.Equal(world.Owner, reminder.RecipientId);

        world.Clock.AdvanceDays(370);                             // inside the 60-day window of the next anniversary
        var nextYear = await world.As(null).Jobs.RunAllAsync();
        Assert.Equal(1, nextYear.AnnualReviewReminders);
        Assert.Equal(2, ReviewTestHelpers.Reminders(world, contract.Id).Count);
    }

    [Fact]
    public void NextAnniversary_IsTheFirstOneFromTodayOn()
    {
        var start = new DateOnly(2024, 5, 10);
        Assert.Equal(new DateOnly(2026, 5, 10), ContractCalculations.NextAnniversary(start, new DateOnly(2026, 3, 2)));
        Assert.Equal(new DateOnly(2026, 5, 10), ContractCalculations.NextAnniversary(start, new DateOnly(2026, 5, 10)));
        Assert.Equal(new DateOnly(2027, 5, 10), ContractCalculations.NextAnniversary(start, new DateOnly(2026, 5, 11)));
        // A contract that has not started yet: its first anniversary, never the start date itself.
        Assert.Equal(new DateOnly(2027, 1, 1), ContractCalculations.NextAnniversary(new DateOnly(2026, 1, 1), new DateOnly(2025, 6, 1)));
    }
}

/// <summary>M6: allowlisted file types, 25 MB, the main document is locked after submission, versions never overwrite.</summary>
public class DocumentRuleTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
    private static readonly byte[] Exe = { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 };
    private static readonly byte[] ZipHeader = { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0 };
    private static readonly byte[] OleHeader = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0 };

    [Theory]
    [InlineData("contract.pdf", true)]
    [InlineData("contract.PDF", true)]
    [InlineData("contract.docx", true)]
    [InlineData("old.doc", true)]
    [InlineData("prices.xlsx", true)]
    [InlineData("prices.xls", true)]
    [InlineData("deck.pptx", true)]
    [InlineData("mail.msg", true)]
    [InlineData("mail.eml", true)]
    [InlineData("notes.txt", true)]
    [InlineData("scan.png", true)]
    [InlineData("scan.jpg", true)]
    [InlineData("scan.jpeg", true)]
    [InlineData("setup.exe", false)]
    [InlineData("page.html", false)]
    [InlineData("app.hta", false)]
    [InlineData("macro.docm", false)]
    [InlineData("macro.xlsm", false)]
    [InlineData("script.js", false)]
    [InlineData("archive.zip", false)]
    [InlineData("noextension", false)]
    [InlineData("contract.pdf.exe", false)]
    public void OnlyAllowlistedExtensions_AreAccepted(string fileName, bool allowed)
    {
        Assert.Equal(allowed, FileTypeRules.IsAllowedExtension(fileName));
    }

    [Fact]
    public void Content_MustMatchTheExtension()
    {
        Assert.True(FileTypeRules.ContentMatches("a.pdf", ReviewTestHelpers.PdfBytes()));
        Assert.True(FileTypeRules.ContentMatches("a.docx", ZipHeader));
        Assert.True(FileTypeRules.ContentMatches("a.xlsx", ZipHeader));
        Assert.True(FileTypeRules.ContentMatches("a.doc", OleHeader));
        Assert.True(FileTypeRules.ContentMatches("a.msg", OleHeader));
        Assert.True(FileTypeRules.ContentMatches("a.png", Png));
        Assert.True(FileTypeRules.ContentMatches("a.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.True(FileTypeRules.ContentMatches("a.txt", Encoding.UTF8.GetBytes("plain text")));

        // An executable renamed to something harmless.
        Assert.False(FileTypeRules.ContentMatches("a.pdf", Exe));
        Assert.False(FileTypeRules.ContentMatches("a.docx", Exe));
        Assert.False(FileTypeRules.ContentMatches("a.png", Exe));
        Assert.False(FileTypeRules.ContentMatches("a.txt", Exe));
        Assert.False(FileTypeRules.ContentMatches("a.jpg", Png));
        Assert.False(FileTypeRules.ContentMatches("a.docx", ReviewTestHelpers.PdfBytes()));
    }

    [Fact]
    public void Validate_RejectsEmptyOversizedAndDisallowedFiles()
    {
        Assert.Throws<InputValidationException>(() => FileTypeRules.Validate("a.pdf", Array.Empty<byte>()));
        Assert.Throws<InputValidationException>(() => FileTypeRules.Validate("a.exe", Exe));
        Assert.Throws<InputValidationException>(() => FileTypeRules.Validate("a.pdf", Exe));

        var tooLarge = new byte[FileTypeRules.MaxDocumentBytes + 1];
        ReviewTestHelpers.PdfBytes().CopyTo(tooLarge, 0);
        Assert.Throws<InputValidationException>(() => FileTypeRules.Validate("a.pdf", tooLarge));

        FileTypeRules.Validate("a.pdf", ReviewTestHelpers.PdfBytes());
    }

    private static Task<DocumentDto> Upload(Actor actor, Guid contractId, string name, string kind, byte[] bytes) =>
        actor.Documents.UploadAsync(contractId, name, kind, new MemoryStream(bytes, false));

    [Fact]
    public async Task Upload_RejectsDisallowedTypes_AndDisguisedContent()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();

        await Assert.ThrowsAsync<InputValidationException>(() => Upload(world.As(world.Owner), contract.Id, "tool.exe", "Other", Exe));
        await Assert.ThrowsAsync<InputValidationException>(() => Upload(world.As(world.Owner), contract.Id, "page.html", "Other", Encoding.UTF8.GetBytes("<script>")));
        await Assert.ThrowsAsync<InputValidationException>(() => Upload(world.As(world.Owner), contract.Id, "appendix.pdf", "Appendix", Exe));
        await Assert.ThrowsAsync<InputValidationException>(() => Upload(world.As(world.Owner), contract.Id, "log.txt", "ApprovalLog", Encoding.UTF8.GetBytes("x")));

        using var db = world.NewDb();
        Assert.Equal(0, db.Documents.Count(d => d.ContractId == contract.Id));
        Assert.Empty(world.Store.Files);
    }

    [Fact]
    public async Task MainDocument_IsLockedAfterSubmission_ForEveryoneButAdministrators()
    {
        using var world = new TestWorld();
        var draft = world.AddContract(c => c.Status = ContractStatus.Draft);
        var active = world.AddContract();

        // In draft the owner manages the main document freely.
        await Upload(world.As(world.Owner), draft.Id, "Agreement.pdf", "Main", ReviewTestHelpers.PdfBytes("draft"));

        // After approval: no new main document, and no new version of an existing main document
        // under another kind either.
        await Upload(world.As(world.Admin), active.Id, "Agreement.pdf", "Main", ReviewTestHelpers.PdfBytes("signed"));
        await Assert.ThrowsAsync<ConflictException>(
            () => Upload(world.As(world.Owner), active.Id, "Replacement.pdf", "Main", ReviewTestHelpers.PdfBytes("other")));
        await Assert.ThrowsAsync<ConflictException>(
            () => Upload(world.As(world.Owner), active.Id, "Agreement.pdf", "Other", ReviewTestHelpers.PdfBytes("other")));

        // Appendices and amendments are still fine for the owner.
        var appendix = await Upload(world.As(world.Owner), active.Id, "Appendix 1.pdf", "Appendix", ReviewTestHelpers.PdfBytes("appendix"));
        Assert.Equal("Appendix", appendix.Kind);

        // The administrator's replacement is a new version and is audited as a change to approved terms.
        var replaced = await Upload(world.As(world.Admin), active.Id, "Agreement.pdf", "Main", ReviewTestHelpers.PdfBytes("corrected"));
        Assert.Equal(2, replaced.Version);
        Assert.Equal(2, ReviewTestHelpers.Audit(world, active.Id, "contract.approved-terms-changed").Count);
    }

    [Fact]
    public async Task SameNameUpload_CreatesANewVersion_AndKeepsTheOldFile()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();

        var first = await Upload(world.As(world.Owner), contract.Id, "Price list.pdf", "Appendix", ReviewTestHelpers.PdfBytes("2026"));
        var second = await Upload(world.As(world.Owner), contract.Id, "Price list.pdf", "Appendix", ReviewTestHelpers.PdfBytes("2027 prices"));

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        using var db = world.NewDb();
        var rows = db.Documents.AsNoTracking().Where(d => d.ContractId == contract.Id).OrderBy(d => d.Version).ToList();
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].StoreKey, rows[1].StoreKey);
        Assert.Equal(ReviewTestHelpers.PdfBytes("2026"), world.Store.Files[rows[0].StoreKey]);
        Assert.Equal(ReviewTestHelpers.PdfBytes("2027 prices"), world.Store.Files[rows[1].StoreKey]);
    }

    [Fact]
    public void TheDatabase_RefusesTwoDocumentsWithTheSameNameAndVersion()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        using var db = world.NewDb();
        db.Documents.Add(new ContractDocument { Id = Guid.NewGuid(), ContractId = contract.Id, Name = "a.pdf", Version = 1, StoreKey = "k1", UploadedAt = world.Clock.UtcNow });
        db.SaveChanges();
        db.Documents.Add(new ContractDocument { Id = Guid.NewGuid(), ContractId = contract.Id, Name = "a.pdf", Version = 1, StoreKey = "k2", UploadedAt = world.Clock.UtcNow });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task LocalStore_NeverOverwritesAnExistingFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dafa-store-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalDocumentStore(folder);
            var location = new DocumentLocation(Guid.NewGuid(), "C-2026-0001", "Entity", "Department");

            var first = await store.SaveAsync(location, "a.pdf", 1, new MemoryStream(new byte[] { 1 }));
            var second = await store.SaveAsync(location, "a.pdf", 1, new MemoryStream(new byte[] { 2, 2 }));

            Assert.NotEqual(first.StoreKey, second.StoreKey);
            await using (var stream = await store.OpenReadAsync(first.StoreKey))
            {
                Assert.Equal(1L, stream.Length);
            }
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }
}

/// <summary>M7: bounded text extraction that degrades to "no suggestions" with a note.</summary>
public class ExtractionLimitTests
{
    private static byte[] DocxLikeZip(long documentXmlBytes)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("word/document.xml", CompressionLevel.Fastest);
            using var stream = entry.Open();
            var chunk = new byte[64 * 1024];
            long written = 0;
            while (written < documentXmlBytes)
            {
                var count = (int)Math.Min(chunk.Length, documentXmlBytes - written);
                stream.Write(chunk, 0, count);
                written += count;
            }
        }
        return buffer.ToArray();
    }

    [Fact]
    public void ADocxThatInflatesBeyondTheLimit_IsNotParsed()
    {
        // 60 MB of zeros compresses to a few kilobytes: a small upload that would explode in memory.
        var bomb = DocxLikeZip(TextExtractor.MaxDocxPartBytes + 10L * 1024 * 1024);
        Assert.True(bomb.Length < 1024 * 1024);

        Assert.False(TextExtractor.DocxIsWithinLimits(bomb, CancellationToken.None));
        var extracted = TextExtractor.Extract("bomb.docx", bomb);

        Assert.Equal("", extracted.Text);
        Assert.Equal(TextExtractor.TooLargeNote, extracted.Note);

        var analysis = TextExtractor.ToAnalysis(extracted, "bomb.docx");
        Assert.Empty(analysis.Suggestions);
        Assert.Equal(TextExtractor.TooLargeNote, analysis.Note);
    }

    [Fact]
    public void ASmallArchive_IsWithinLimits()
    {
        Assert.True(TextExtractor.DocxIsWithinLimits(DocxLikeZip(10_000), CancellationToken.None));
    }

    [Fact]
    public void WhenTheTimeBudgetIsUsedUp_ThereAreNoSuggestions_AndNoError()
    {
        using var expired = new CancellationTokenSource();
        expired.Cancel();

        var extracted = TextExtractor.Extract("slow.docx", DocxLikeZip(10_000), expired.Token);

        Assert.Equal("", extracted.Text);
        Assert.Equal(TextExtractor.TooSlowNote, extracted.Note);
    }

    [Fact]
    public void ExtractedText_IsCapped()
    {
        var huge = Encoding.UTF8.GetBytes(new string('a', TextExtractor.MaxCharacters + 5000));

        var extracted = TextExtractor.Extract("big.txt", huge);

        Assert.Equal(TextExtractor.MaxCharacters, extracted.Text.Length);
        Assert.Equal(TextExtractor.TruncatedNote, extracted.Note);
    }

    [Fact]
    public async Task ADamagedFile_GivesAnEmptyResult_NotAnException()
    {
        var notReallyAPdf = Encoding.ASCII.GetBytes("%PDF-1.4 this is not a pdf");
        var notReallyADocx = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5 };

        var pdf = await TextExtractor.ExtractAsync("broken.pdf", notReallyAPdf);
        var docx = await TextExtractor.ExtractAsync("broken.docx", notReallyADocx);

        Assert.Equal("", pdf.Text);
        Assert.Equal("", docx.Text);
        var analysis = await new HeuristicContractAnalyzer().AnalyzeAsync("broken.pdf", notReallyAPdf);
        Assert.Empty(analysis.Suggestions);
    }
}

/// <summary>M8: reminder idempotency in the database, and delivery failures are retried, not lost.</summary>
public class ReminderDeliveryTests
{
    [Fact]
    public void TheDatabase_RefusesASecondReminder_ForTheSameContractKindAndTerm()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        using var db = world.NewDb();
        db.Reminders.Add(new Reminder { Id = Guid.NewGuid(), ContractId = contract.Id, Kind = "Notice60", TermKey = "2026-05-01", DueDate = world.Clock.Today, RecipientId = world.Owner });
        // Another kind and another term are fine.
        db.Reminders.Add(new Reminder { Id = Guid.NewGuid(), ContractId = contract.Id, Kind = "Notice30", TermKey = "2026-05-01", DueDate = world.Clock.Today, RecipientId = world.Owner });
        db.Reminders.Add(new Reminder { Id = Guid.NewGuid(), ContractId = contract.Id, Kind = "Notice60", TermKey = "2027-05-01", DueDate = world.Clock.Today, RecipientId = world.Owner });
        db.SaveChanges();

        db.Reminders.Add(new Reminder { Id = Guid.NewGuid(), ContractId = contract.Id, Kind = "Notice60", TermKey = "2026-05-01", DueDate = world.Clock.Today, RecipientId = world.Owner });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task AFailedDelivery_IsNotMarkedAsSent_AndIsRetriedOnTheNextRun()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();                       // Notice60 is due
        var notifier = new SwitchableNotifier { Fail = true };

        var failed = await world.As(null, null, notifier).Jobs.RunAllAsync();

        Assert.Equal(0, failed.NoticeReminders);
        var pending = Assert.Single(ReviewTestHelpers.Reminders(world, contract.Id));
        Assert.Null(pending.SentAt);
        Assert.Equal(1, pending.Attempts);

        notifier.Fail = false;
        var retried = await world.As(null, null, notifier).Jobs.RunAllAsync();
        var again = await world.As(null, null, notifier).Jobs.RunAllAsync();

        Assert.Equal(1, retried.NoticeReminders);
        Assert.Equal(0, again.NoticeReminders);
        var sent = Assert.Single(ReviewTestHelpers.Reminders(world, contract.Id));
        Assert.NotNull(sent.SentAt);
        Assert.Equal(2, sent.Attempts);
        Assert.Equal(1, notifier.Delivered);
    }

    [Fact]
    public async Task Retries_StopAtTheCap_AndTheFailureIsReported()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        var notifier = new SwitchableNotifier { Fail = true };
        var errors = new List<string>();

        for (var run = 0; run < ContractCalculations.MaxReminderAttempts + 3; run++)
        {
            var result = await world.As(null, null, notifier).Jobs.RunAllAsync();
            errors.AddRange(result.Errors);
        }

        var row = Assert.Single(ReviewTestHelpers.Reminders(world, contract.Id));
        Assert.Null(row.SentAt);
        Assert.Equal(ContractCalculations.MaxReminderAttempts, row.Attempts);
        Assert.Equal(ContractCalculations.MaxReminderAttempts, notifier.Attempts);
        Assert.Single(errors);
        Assert.Contains("could not be delivered", errors[0]);
    }

    [Fact]
    public async Task TwoRunsStartedTogether_AreSerialized_AndSendNothingTwice()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        var notifier = new SwitchableNotifier();
        var first = await world.As(null, null, notifier).Jobs.RunAllAsync();
        Assert.Empty(first.Errors);
        Assert.Equal(1, first.NoticeReminders);

        // Two runs started together are serialized and the second sends nothing new.
        var runs = await Task.WhenAll(
            world.As(null, null, notifier).Jobs.RunAllAsync(),
            world.As(null, null, notifier).Jobs.RunAllAsync());
        Assert.All(runs, r => Assert.Empty(r.Errors));
        Assert.Equal(0, runs.Sum(r => r.NoticeReminders));
        Assert.Single(ReviewTestHelpers.Reminders(world, contract.Id));
    }
}

/// <summary>Low findings that were cheap to fix.</summary>
public class LowFindingTests
{
    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+45 12 34", "'+45 12 34")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("plain", "plain")]
    public void CsvCell_NeutralisesFormulas(string value, string expected)
    {
        Assert.Equal(expected, ContractQueryService.CsvCell(value));
    }

    [Fact]
    public void CsvCell_AlsoNeutralisesCellsStartingWithTabOrCarriageReturn()
    {
        Assert.StartsWith("\"'\t", ContractQueryService.CsvCell("\t=1+1"));
        Assert.StartsWith("\"'\r", ContractQueryService.CsvCell("\r=1+1"));
        Assert.Equal("\"a,b\"", ContractQueryService.CsvCell("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ContractQueryService.CsvCell("say \"hi\""));
    }

    [Fact]
    public void CounterpartyName_HasAMaximumLength()
    {
        var input = new ContractInput { CounterpartyName = new string('x', 301) };
        var errors = ContractValidator.Validate(input);
        Assert.True(errors.ContainsKey("counterpartyName"));
    }

    [Fact]
    public async Task Due90View_LeavesOutContractsWithARecordedDecision_LikeTheHomeTile()
    {
        using var world = new TestWorld();
        var open = world.AddContract();                           // deadline in 60 days
        var decided = world.AddContract(c => c.RenewalDecision = RenewalDecision.Renegotiate);

        var list = await world.As(world.Owner).Queries.ListAsync(new ContractListQuery { View = "due90" });
        var home = await world.As(world.Owner).Queries.HomeAsync();

        Assert.Equal(new[] { open.Id }, list.Items.Select(i => i.Id).ToArray());
        Assert.Equal(1, home.RenewingIn90.Count);
        Assert.NotEqual(decided.Id, list.Items[0].Id);
    }

    [Fact]
    public async Task LinkAuditText_DoesNotRevealTheOtherContractsNumber()
    {
        using var world = new TestWorld();
        var from = world.AddContract();
        var to = world.AddContract();
        var actor = world.As(world.Owner);
        var links = new LinkService(actor.Db, actor.Access, new AuditService(actor.Db, new FakeCurrentUser(actor.Db, world.Owner, "Owner User"), world.Clock));

        await links.CreateAsync(from.Id, new LinkInput { ToContractId = to.Id, Kind = LinkKind.Amendment });

        var audit = Assert.Single(ReviewTestHelpers.Audit(world, from.Id, "link.created"));
        Assert.DoesNotContain(to.ContractNo, audit.Text);
        Assert.Contains("Amendment", audit.Text);
    }
}
