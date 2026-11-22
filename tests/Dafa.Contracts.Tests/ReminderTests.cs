using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Tests;

public class ReminderTests
{
    private static List<Reminder> Reminders(TestWorld world, Guid contractId)
    {
        using var db = world.NewDb();
        return db.Reminders.AsNoTracking().Where(r => r.ContractId == contractId).ToList();
    }

    private static int OutboxCount(TestWorld world, string kind)
    {
        using var db = world.NewDb();
        return db.OutboxMessages.Count(m => m.Kind == kind);
    }

    [Fact]
    public async Task NoticeReminder_IsSentOnce_HoweverOftenTheJobRuns()
    {
        using var world = new TestWorld();
        // Ends in 150 days with 90 days' notice: the deadline is 60 days away.
        var contract = world.AddContract();

        var first = await world.As(null).Jobs.RunAllAsync();
        var second = await world.As(null).Jobs.RunAllAsync();

        Assert.Empty(first.Errors);
        Assert.Equal(1, first.NoticeReminders);
        Assert.Equal(0, second.NoticeReminders);
        var reminder = Assert.Single(Reminders(world, contract.Id));
        Assert.Equal("Notice60", reminder.Kind);
        Assert.Equal(world.Owner, reminder.RecipientId);
        Assert.NotNull(reminder.SentAt);
        Assert.Equal(1, OutboxCount(world, "Notice60"));
    }

    [Fact]
    public async Task Reminders_FollowTheSchedule_AndEscalateToTheOwner()
    {
        using var world = new TestWorld();
        // Deadline 130 days away; reminders go to the responsible, the escalation to the owner.
        var contract = world.AddContract(c =>
        {
            c.EndDate = world.Clock.Today.AddDays(220);
            c.ResponsibleId = world.PurchasingMember;
            c.ReminderRecipient = ReminderRecipient.Responsible;
        });

        await world.As(null).Jobs.RunAllAsync();
        Assert.Empty(Reminders(world, contract.Id));

        world.Clock.AdvanceDays(10);   // 120 days before the deadline
        await world.As(null).Jobs.RunAllAsync();
        Assert.Equal(new[] { "Notice120" }, Reminders(world, contract.Id).Select(r => r.Kind).ToArray());

        world.Clock.AdvanceDays(60);   // 60 days before
        await world.As(null).Jobs.RunAllAsync();
        world.Clock.AdvanceDays(30);   // 30 days before
        await world.As(null).Jobs.RunAllAsync();
        var kinds = Reminders(world, contract.Id).Select(r => r.Kind).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { "Notice120", "Notice30", "Notice60" }, kinds);
        Assert.All(Reminders(world, contract.Id), r => Assert.Equal(world.PurchasingMember, r.RecipientId));

        world.Clock.AdvanceDays(15);   // 15 days before: no escalation yet
        await world.As(null).Jobs.RunAllAsync();
        Assert.DoesNotContain(Reminders(world, contract.Id), r => r.Kind == "Escalation");

        world.Clock.AdvanceDays(1);    // 14 days before: escalate to the owner
        var result = await world.As(null).Jobs.RunAllAsync();
        Assert.Equal(1, result.Escalations);
        var escalation = Reminders(world, contract.Id).Single(r => r.Kind == "Escalation");
        Assert.Equal(world.Owner, escalation.RecipientId);

        // Idempotent from here on.
        var again = await world.As(null).Jobs.RunAllAsync();
        Assert.Equal(0, again.Escalations);
        Assert.Equal(0, again.NoticeReminders);
        Assert.Equal(4, Reminders(world, contract.Id).Count);
    }

    [Fact]
    public async Task RecordedDecision_StopsFurtherReminders()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c => c.EndDate = world.Clock.Today.AddDays(210)); // deadline in 120 days
        await world.As(null).Jobs.RunAllAsync();
        Assert.Single(Reminders(world, contract.Id));

        await world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id,
            new RenewalDecisionInput { Decision = RenewalDecision.Renegotiate, Comment = "Talking to the supplier" });

        world.Clock.AdvanceDays(110);  // 10 days before the deadline: would be Notice30 and an escalation
        var result = await world.As(null).Jobs.RunAllAsync();
        Assert.Equal(0, result.NoticeReminders);
        Assert.Equal(0, result.Escalations);
        Assert.Single(Reminders(world, contract.Id));
    }

    [Fact]
    public async Task GiveNotice_And_LetExpire_RequireAComment()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();

        foreach (var decision in new[] { RenewalDecision.GiveNotice, RenewalDecision.LetExpire })
        {
            await Assert.ThrowsAsync<Dafa.Contracts.Core.Common.InputValidationException>(
                () => world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id, new RenewalDecisionInput { Decision = decision, Comment = " " }));
        }

        await world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id,
            new RenewalDecisionInput { Decision = RenewalDecision.GiveNotice, Comment = "Moving to another supplier" });

        using var db = world.NewDb();
        Assert.Equal(ContractStatus.NoticeGiven, db.Contracts.Single(c => c.Id == contract.Id).Status);
        // Listed without a deadline from now on.
        var list = await world.As(world.Owner).Queries.ListAsync(new Dafa.Contracts.Core.Services.ContractListQuery());
        Assert.Null(list.Items.Single().NoticeDeadline);
        Assert.Null(list.Items.Single().DaysToNotice);
    }

    [Fact]
    public async Task Renew_OnAutoRenewContract_RollsForward_AndRestartsTheReminderCycle()
    {
        using var world = new TestWorld();
        var endDate = world.Clock.Today.AddDays(150);
        var contract = world.AddContract(c =>
        {
            c.AutoRenew = true;
            c.RenewalMonths = 12;
        });
        await world.As(null).Jobs.RunAllAsync();
        Assert.Single(Reminders(world, contract.Id));

        await world.As(world.Owner).Commands.DecideRenewalAsync(contract.Id,
            new RenewalDecisionInput { Decision = RenewalDecision.Renew });

        using var db = world.NewDb();
        var saved = db.Contracts.AsNoTracking().Single(c => c.Id == contract.Id);
        Assert.Equal(endDate.AddMonths(12), saved.EndDate);
        Assert.Null(saved.RenewalDecision);
        Assert.Equal(ContractStatus.Active, saved.Status);
        Assert.Empty(Reminders(world, contract.Id));
    }

    [Fact]
    public async Task Job_EndsExpiredContracts_AndAutoRenewsOthers()
    {
        using var world = new TestWorld();
        var expiring = world.AddContract(c => c.EndDate = world.Clock.Today.AddDays(-1));
        var renewing = world.AddContract(c =>
        {
            c.EndDate = world.Clock.Today.AddDays(-1);
            c.AutoRenew = true;
            c.RenewalMonths = 12;
        });
        var noticeGiven = world.AddContract(c =>
        {
            c.EndDate = world.Clock.Today.AddDays(-1);
            c.AutoRenew = true;
            c.RenewalMonths = 12;
            c.Status = ContractStatus.NoticeGiven;
            c.RenewalDecision = RenewalDecision.GiveNotice;
        });

        var result = await world.As(null).Jobs.RunAllAsync();

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.ContractsEnded);
        Assert.Equal(1, result.ContractsRenewed);
        using var db = world.NewDb();
        Assert.Equal(ContractStatus.Expired, db.Contracts.Single(c => c.Id == expiring.Id).Status);
        Assert.Equal(ContractStatus.Terminated, db.Contracts.Single(c => c.Id == noticeGiven.Id).Status);
        var renewed = db.Contracts.Single(c => c.Id == renewing.Id);
        Assert.Equal(ContractStatus.Active, renewed.Status);
        Assert.Equal(world.Clock.Today.AddDays(-1).AddMonths(12), renewed.EndDate);
    }

    [Fact]
    public async Task ObligationReminder_IsSent14DaysBefore_OncePerDueDate()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c => c.EndDate = null); // open-ended: no notice reminders
        var obligationId = Guid.NewGuid();
        using (var db = world.NewDb())
        {
            db.Obligations.Add(new Obligation
            {
                Id = obligationId,
                ContractId = contract.Id,
                Description = "Send the forecast",
                DueDate = world.Clock.Today.AddDays(20),
                ResponsibleId = world.PurchasingMember,
                Status = ObligationStatus.Open
            });
            db.SaveChanges();
        }

        var early = await world.As(null).Jobs.RunAllAsync();
        Assert.Equal(0, early.ObligationReminders);

        world.Clock.AdvanceDays(6);    // 14 days before
        var due = await world.As(null).Jobs.RunAllAsync();
        var again = await world.As(null).Jobs.RunAllAsync();
        Assert.Equal(1, due.ObligationReminders);
        Assert.Equal(0, again.ObligationReminders);

        // The open-ended contract also gets its yearly review reminder; look at the obligation's only.
        var reminder = Assert.Single(Reminders(world, contract.Id), r => r.ObligationId != null);
        Assert.Equal(obligationId, reminder.ObligationId);
        Assert.Equal(world.PurchasingMember, reminder.RecipientId);
        Assert.Equal(1, OutboxCount(world, "Obligation"));
    }
}
