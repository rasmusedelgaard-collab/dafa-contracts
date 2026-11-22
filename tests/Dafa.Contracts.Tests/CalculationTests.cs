using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Dafa.Contracts.Core.Services;

namespace Dafa.Contracts.Tests;

public class CalculationTests
{
    private static readonly DateOnly Today = new(2026, 3, 2);

    [Fact]
    public void NoticeDeadline_IsEndDateMinusNoticePeriod()
    {
        Assert.Equal(new DateOnly(2026, 10, 2), ContractCalculations.NoticeDeadline(new DateOnly(2026, 12, 31), 90));
        Assert.Equal(new DateOnly(2026, 12, 31), ContractCalculations.NoticeDeadline(new DateOnly(2026, 12, 31), 0));
        Assert.Equal(new DateOnly(2026, 12, 31), ContractCalculations.NoticeDeadline(new DateOnly(2026, 12, 31), null));
    }

    [Fact]
    public void NoticeDeadline_IsNull_ForOpenEndedContracts()
    {
        Assert.Null(ContractCalculations.NoticeDeadline(null, 90));
        Assert.Null(ContractCalculations.DaysToNotice((DateOnly?)null, Today));
    }

    [Fact]
    public void DaysToNotice_CountsFromToday_AndIsNegativeWhenPassed()
    {
        Assert.Equal(30, ContractCalculations.DaysToNotice(Today.AddDays(30), Today));
        Assert.Equal(0, ContractCalculations.DaysToNotice(Today, Today));
        Assert.Equal(-5, ContractCalculations.DaysToNotice(Today.AddDays(-5), Today));
    }

    [Fact]
    public void DueNoticeOffset_PicksTheClosestOffsetThatIsDue()
    {
        var offsets = new[] { 120, 60, 30 };
        Assert.Null(ContractCalculations.DueNoticeOffset(Today.AddDays(121), Today, offsets));
        Assert.Equal(120, ContractCalculations.DueNoticeOffset(Today.AddDays(120), Today, offsets));
        Assert.Equal(120, ContractCalculations.DueNoticeOffset(Today.AddDays(61), Today, offsets));
        Assert.Equal(60, ContractCalculations.DueNoticeOffset(Today.AddDays(45), Today, offsets));
        Assert.Equal(30, ContractCalculations.DueNoticeOffset(Today, Today, offsets));
        Assert.Null(ContractCalculations.DueNoticeOffset(Today.AddDays(-1), Today, offsets));
    }

    [Fact]
    public void NextReminderDate_IsTheNextUnsentOffset()
    {
        var contract = new Contract { Status = ContractStatus.Active, EndDate = Today.AddDays(190), NoticePeriodDays = 90 };
        var offsets = new[] { 120, 60, 30 };

        // Deadline in 100 days: the 120-day reminder is due now.
        Assert.Equal(Today, ContractCalculations.NextReminderDate(contract, Today, offsets, new HashSet<string>()));
        // Once it is sent, the next one is 60 days before the deadline.
        var sent = new HashSet<string> { ContractCalculations.NoticeKind(120) };
        Assert.Equal(Today.AddDays(40), ContractCalculations.NextReminderDate(contract, Today, offsets, sent));

        contract.RenewalDecision = RenewalDecision.Renegotiate;
        Assert.Null(ContractCalculations.NextReminderDate(contract, Today, offsets, sent));
    }

    [Fact]
    public void Renew_OnAutoRenewContract_RollsEndDateForward_AndClearsTheDecision()
    {
        var contract = new Contract
        {
            Status = ContractStatus.Active,
            EndDate = new DateOnly(2026, 6, 30),
            AutoRenew = true,
            RenewalMonths = 12,
            RenewalDecision = RenewalDecision.Renegotiate
        };

        var rolled = RenewalRules.Apply(contract, RenewalDecision.Renew, Guid.NewGuid(), DateTime.UtcNow, "ok");

        Assert.True(rolled);
        Assert.Equal(new DateOnly(2027, 6, 30), contract.EndDate);
        Assert.Null(contract.RenewalDecision);
        Assert.Null(contract.RenewalDecidedAt);
        Assert.Equal(ContractStatus.Active, contract.Status);
    }

    [Fact]
    public void Renew_OnContractWithoutAutoRenew_IsRecordedAsADecision()
    {
        var userId = Guid.NewGuid();
        var contract = new Contract { Status = ContractStatus.Active, EndDate = new DateOnly(2026, 6, 30), AutoRenew = false };

        var rolled = RenewalRules.Apply(contract, RenewalDecision.Renew, userId, DateTime.UtcNow, " keep ");

        Assert.False(rolled);
        Assert.Equal(new DateOnly(2026, 6, 30), contract.EndDate);
        Assert.Equal(RenewalDecision.Renew, contract.RenewalDecision);
        Assert.Equal(userId, contract.RenewalDecidedById);
        Assert.Equal("keep", contract.RenewalComment);
    }

    [Fact]
    public void GiveNotice_SetsStatusNoticeGiven()
    {
        var contract = new Contract { Status = ContractStatus.Active, EndDate = new DateOnly(2026, 6, 30), AutoRenew = true, RenewalMonths = 12 };
        RenewalRules.Apply(contract, RenewalDecision.GiveNotice, Guid.NewGuid(), DateTime.UtcNow, null);
        Assert.Equal(ContractStatus.NoticeGiven, contract.Status);
        Assert.Equal(new DateOnly(2026, 6, 30), contract.EndDate);
    }

    [Fact]
    public void MissingData_ListsEmptyRequiredFields()
    {
        var complete = new Contract
        {
            Title = "T", CounterpartyName = "C", NoticePeriodDays = 30, Currency = "DKK", AnnualValue = 1m, ChangeOfControl = false
        };
        Assert.Empty(ContractCalculations.MissingData(complete));

        var incomplete = new Contract { Title = "T", CounterpartyName = "", Currency = "DKK" };
        var missing = ContractCalculations.MissingData(incomplete);
        Assert.Contains("Counterparty", missing);
        Assert.Contains("Notice period", missing);
        Assert.Contains("Contract value", missing);
        Assert.Contains("Change of control", missing);
        Assert.DoesNotContain("Signed date", missing);

        var activeUnsigned = new Contract
        {
            Title = "T", CounterpartyName = "C", NoticePeriodDays = 30, Currency = "DKK", AnnualValue = 1m, ChangeOfControl = false,
            Status = ContractStatus.Active
        };
        Assert.Equal(new[] { "Signed date" }, ContractCalculations.MissingData(activeUnsigned).ToArray());
    }

    [Fact]
    public void ListNoticeDeadline_IsNull_OnceNoticeIsGivenOrTheContractHasEnded()
    {
        var contract = new Contract { Status = ContractStatus.Active, EndDate = new DateOnly(2026, 12, 31), NoticePeriodDays = 90 };
        Assert.Equal(new DateOnly(2026, 10, 2), ContractCalculations.ListNoticeDeadline(contract));

        foreach (var status in new[] { ContractStatus.NoticeGiven, ContractStatus.Expired, ContractStatus.Terminated })
        {
            contract.Status = status;
            Assert.Null(ContractCalculations.ListNoticeDeadline(contract));
        }
    }

    [Fact]
    public void Lifecycle_FlagsNoticeDeadlineAsAlert_WhenCloseAndUndecided()
    {
        var contract = new Contract
        {
            Status = ContractStatus.Active,
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            StartDate = new DateOnly(2025, 2, 1),
            EndDate = Today.AddDays(100),
            NoticePeriodDays = 90
        };
        var steps = ContractCalculations.Lifecycle(contract, Today);
        var notice = Assert.Single(steps, s => s.Name == "Notice deadline");
        Assert.Equal("alert", notice.State);
        Assert.Equal(Today.AddDays(10), notice.Date);

        contract.RenewalDecision = RenewalDecision.Renew;
        notice = Assert.Single(ContractCalculations.Lifecycle(contract, Today), s => s.Name == "Notice deadline");
        Assert.NotEqual("alert", notice.State);
    }

    [Fact]
    public void Obligation_Complete_RollsRecurringObligationForward()
    {
        var recurring = new Obligation { DueDate = new DateOnly(2026, 3, 31), FrequencyMonths = 3, Status = ObligationStatus.Open };
        ObligationRules.Complete(recurring, DateTime.UtcNow);
        Assert.Equal(ObligationStatus.Open, recurring.Status);
        Assert.Equal(new DateOnly(2026, 6, 30), recurring.DueDate);

        var single = new Obligation { DueDate = new DateOnly(2026, 3, 31), Status = ObligationStatus.Open };
        ObligationRules.Complete(single, DateTime.UtcNow);
        Assert.Equal(ObligationStatus.Done, single.Status);
    }
}

public class ValidationTests
{
    private static ContractInput ValidInput() => new()
    {
        Title = "Supply agreement",
        TypeId = Guid.NewGuid(),
        CounterpartyName = "Supplier A/S",
        EntityId = Guid.NewGuid(),
        DepartmentId = Guid.NewGuid(),
        OwnerId = Guid.NewGuid(),
        ReminderRecipient = ReminderRecipient.Owner,
        StartDate = new DateOnly(2026, 1, 1),
        EndDate = new DateOnly(2026, 12, 31),
        NoticePeriodDays = 90,
        AutoRenew = false,
        Currency = "DKK",
        AnnualValue = 100_000m,
        ChangeOfControl = false
    };

    [Fact]
    public void ValidInput_HasNoErrors()
    {
        Assert.Empty(ContractValidator.Validate(ValidInput()));
    }

    [Fact]
    public void EmptyInput_ReportsEveryRequiredField()
    {
        var errors = ContractValidator.Validate(new ContractInput());
        foreach (var field in new[]
        {
            "title", "typeId", "counterpartyName", "entityId", "departmentId", "ownerId", "reminderRecipient",
            "startDate", "noticePeriodDays", "autoRenew", "currency", "annualValue", "changeOfControl"
        })
        {
            Assert.True(errors.ContainsKey(field), "Expected an error for " + field);
        }
    }

    [Fact]
    public void EndDate_IsRequired_UnlessOpenEnded()
    {
        var input = ValidInput();
        input.EndDate = null;
        input.OpenEnded = false;
        Assert.True(ContractValidator.Validate(input).ContainsKey("endDate"));

        input.OpenEnded = true;
        Assert.Empty(ContractValidator.Validate(input));
    }

    [Fact]
    public void EndDate_CannotBeBeforeStartDate()
    {
        var input = ValidInput();
        input.EndDate = new DateOnly(2025, 12, 31);
        Assert.True(ContractValidator.Validate(input).ContainsKey("endDate"));
    }

    [Fact]
    public void EitherAnnualOrTotalValue_IsEnough()
    {
        var input = ValidInput();
        input.AnnualValue = null;
        Assert.True(ContractValidator.Validate(input).ContainsKey("annualValue"));

        input.TotalValue = 250_000m;
        Assert.Empty(ContractValidator.Validate(input));
    }

    [Fact]
    public void SupplierId_CountsAsCounterparty()
    {
        var input = ValidInput();
        input.CounterpartyName = null;
        Assert.True(ContractValidator.Validate(input).ContainsKey("counterpartyName"));

        input.SupplierId = Guid.NewGuid();
        Assert.Empty(ContractValidator.Validate(input));
    }

    [Fact]
    public void ReminderRecipient_MustPointAtAnAssignedPerson()
    {
        var input = ValidInput();
        input.ReminderRecipient = ReminderRecipient.Delegate;
        Assert.True(ContractValidator.Validate(input).ContainsKey("reminderRecipient"));

        input.DelegateId = Guid.NewGuid();
        Assert.Empty(ContractValidator.Validate(input));
    }

    [Fact]
    public void UnknownCurrency_IsRejected()
    {
        var input = ValidInput();
        input.Currency = "XXX";
        Assert.True(ContractValidator.Validate(input).ContainsKey("currency"));
    }

    [Fact]
    public async Task Create_WithMissingFields_ThrowsValidationError_AndSavesNothing()
    {
        using var world = new TestWorld();
        var actor = world.As(world.Owner);
        var error = await Assert.ThrowsAsync<Dafa.Contracts.Core.Common.InputValidationException>(
            () => actor.Commands.CreateAsync(new ContractInput { Title = "Only a title" }));
        Assert.True(error.Errors.ContainsKey("entityId"));
        Assert.Empty(world.NewDb().Contracts);
    }

    [Fact]
    public async Task Create_WithValidInput_AssignsContractNumber_AndStartsAsDraft()
    {
        using var world = new TestWorld();
        var input = ValidInput();
        input.TypeId = world.TypeSupplier;
        input.EntityId = world.EntityA;
        input.DepartmentId = world.Purchasing;
        input.OwnerId = world.Owner;
        // Release 4: the counterparty is a master record chosen from the list.
        input.SupplierId = world.DefaultSupplier;

        var id = await world.As(world.Owner).Commands.CreateAsync(input);

        var saved = world.NewDb().Contracts.Single(c => c.Id == id);
        Assert.Equal("C-2026-0001", saved.ContractNo);
        Assert.Equal(ContractStatus.Draft, saved.Status);

        var second = await world.As(world.Owner).Commands.CreateAsync(input);
        Assert.Equal("C-2026-0002", world.NewDb().Contracts.Single(c => c.Id == second).ContractNo);
    }

    [Fact]
    public async Task Create_RequiresContractOwnerRole()
    {
        using var world = new TestWorld();
        var input = ValidInput();
        await Assert.ThrowsAsync<Dafa.Contracts.Core.Common.ForbiddenException>(
            () => world.As(world.Outsider).Commands.CreateAsync(input));
    }
}
