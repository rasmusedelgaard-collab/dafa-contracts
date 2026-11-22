using System.Globalization;

namespace Dafa.Contracts.Core.Domain;

public sealed record LifecycleStep(string Name, DateOnly? Date, string State);

public sealed record ReminderScheduleItem(string Kind, DateOnly DueDate, DateTime? SentAt);

/// <summary>Pure date and data-quality calculations on a contract.</summary>
public static class ContractCalculations
{
    public const string EscalationKind = "Escalation";
    public const string ObligationKind = "Obligation";
    public const string AnnualReviewKind = "AnnualReview";

    /// <summary>Open-ended contracts get a review reminder this many days before each anniversary of the start date.</summary>
    public const int AnnualReviewLeadDays = 60;

    /// <summary>A notification that could not be delivered is retried by the job up to this many times.</summary>
    public const int MaxReminderAttempts = 5;

    /// <summary>Term key of notice and escalation reminders: the notice deadline they are about.</summary>
    public static string NoticeTermKey(DateOnly noticeDeadline) =>
        noticeDeadline.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string AnnualReviewTermKey(DateOnly anniversary) =>
        anniversary.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string ObligationTermKey(Guid obligationId, DateOnly dueDate) =>
        obligationId.ToString("N") + "|" + dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>True for the reminder kinds that belong to one notice deadline (Notice{days} and Escalation).</summary>
    public static bool IsNoticeCycleKind(string? kind) =>
        kind == EscalationKind || (kind is not null && kind.StartsWith("Notice", StringComparison.Ordinal));

    /// <summary>The first anniversary of the start date that is today or later (never the start date itself).</summary>
    public static DateOnly NextAnniversary(DateOnly startDate, DateOnly today)
    {
        var years = Math.Max(1, today.Year - startDate.Year);
        var anniversary = startDate.AddYears(years);
        var guard = 0;
        while (anniversary < today && guard < 200)
        {
            years++;
            anniversary = startDate.AddYears(years);
            guard++;
        }
        return anniversary;
    }

    /// <summary>
    /// The anniversary an open-ended, active contract should be reviewed for today, or null when
    /// none is due: from <see cref="AnnualReviewLeadDays"/> days before the anniversary until the day itself.
    /// </summary>
    public static DateOnly? DueAnnualReview(Contract contract, DateOnly today)
    {
        if (contract.Status != ContractStatus.Active || contract.EndDate is not null)
        {
            return null;
        }
        var anniversary = NextAnniversary(contract.StartDate, today);
        return today >= anniversary.AddDays(-AnnualReviewLeadDays) ? anniversary : null;
    }

    /// <summary>
    /// True when a Renew decision may be recorded today: from the first reminder
    /// (the largest notice offset before the deadline) onwards, also after the deadline.
    /// </summary>
    public static bool IsInRenewalWindow(DateOnly noticeDeadline, DateOnly today, IEnumerable<int> noticeDays)
    {
        var largest = 0;
        foreach (var days in noticeDays)
        {
            if (days > largest)
            {
                largest = days;
            }
        }
        return today >= noticeDeadline.AddDays(-largest);
    }

    public static string NoticeKind(int daysBefore) => "Notice" + daysBefore;

    /// <summary>noticeDeadline = endDate - noticePeriodDays. Null for open-ended contracts.</summary>
    public static DateOnly? NoticeDeadline(DateOnly? endDate, int? noticePeriodDays)
    {
        if (endDate is null)
        {
            return null;
        }
        var days = noticePeriodDays ?? 0;
        if (days < 0)
        {
            days = 0;
        }
        return endDate.Value.AddDays(-days);
    }

    public static DateOnly? NoticeDeadline(Contract contract) =>
        NoticeDeadline(contract.EndDate, contract.NoticePeriodDays);

    /// <summary>Days from today to the notice deadline. Negative when the deadline has passed.</summary>
    public static int? DaysToNotice(DateOnly? noticeDeadline, DateOnly today)
    {
        if (noticeDeadline is null)
        {
            return null;
        }
        return noticeDeadline.Value.DayNumber - today.DayNumber;
    }

    public static int? DaysToNotice(Contract contract, DateOnly today) =>
        DaysToNotice(NoticeDeadline(contract), today);

    /// <summary>True while reminders about the notice deadline are relevant.</summary>
    public static bool IsRenewalOpen(Contract contract) =>
        contract.Status == ContractStatus.Active && contract.RenewalDecision is null && contract.EndDate is not null;

    /// <summary>
    /// The notice reminder that should be sent today, if any: the offset closest to the deadline
    /// that is already due. Larger offsets that were missed are skipped, so a contract registered
    /// late gets one reminder instead of three.
    /// </summary>
    public static int? DueNoticeOffset(DateOnly noticeDeadline, DateOnly today, IEnumerable<int> noticeDays)
    {
        if (today > noticeDeadline)
        {
            return null;
        }
        int? best = null;
        foreach (var days in noticeDays)
        {
            if (noticeDeadline.AddDays(-days) <= today && (best is null || days < best.Value))
            {
                best = days;
            }
        }
        return best;
    }

    /// <summary>The next date a notice reminder will go out, or null when none is planned.</summary>
    public static DateOnly? NextReminderDate(Contract contract, DateOnly today, IEnumerable<int> noticeDays, ISet<string> sentKinds)
    {
        if (!IsRenewalOpen(contract))
        {
            return null;
        }
        var deadline = NoticeDeadline(contract);
        if (deadline is null || today > deadline.Value)
        {
            return null;
        }
        var offsets = noticeDays.ToList();
        var dueNow = DueNoticeOffset(deadline.Value, today, offsets);
        if (dueNow is not null && !sentKinds.Contains(NoticeKind(dueNow.Value)))
        {
            return today;
        }
        DateOnly? next = null;
        foreach (var days in offsets)
        {
            var date = deadline.Value.AddDays(-days);
            if (date > today && (next is null || date < next.Value))
            {
                next = date;
            }
        }
        return next;
    }

    /// <summary>Planned and sent reminders for the contract record.</summary>
    public static List<ReminderScheduleItem> ReminderSchedule(
        Contract contract, IEnumerable<int> noticeDays, int escalationDays, IEnumerable<Reminder> existing)
    {
        var rows = existing.Where(r => r.ObligationId is null).ToList();
        var result = new List<ReminderScheduleItem>();
        var deadline = NoticeDeadline(contract);
        var open = IsRenewalOpen(contract);

        if (deadline is not null)
        {
            foreach (var days in noticeDays.OrderByDescending(d => d))
            {
                var kind = NoticeKind(days);
                var row = rows.FirstOrDefault(r => r.Kind == kind);
                if (row is not null || open)
                {
                    result.Add(new ReminderScheduleItem(kind, row?.DueDate ?? deadline.Value.AddDays(-days), row?.SentAt));
                }
            }
            var escalation = rows.FirstOrDefault(r => r.Kind == EscalationKind);
            if (escalation is not null || open)
            {
                result.Add(new ReminderScheduleItem(
                    EscalationKind, escalation?.DueDate ?? deadline.Value.AddDays(-escalationDays), escalation?.SentAt));
            }
        }

        foreach (var row in rows)
        {
            if (!result.Any(r => r.Kind == row.Kind))
            {
                result.Add(new ReminderScheduleItem(row.Kind, row.DueDate, row.SentAt));
            }
        }
        return result;
    }

    public const string MissingTitle = "Title";
    public const string MissingCounterparty = "Counterparty";
    public const string MissingNoticePeriod = "Notice period";
    public const string MissingCurrency = "Currency";
    public const string MissingValue = "Contract value";
    public const string MissingChangeOfControl = "Change of control";
    public const string MissingRenewalPeriod = "Renewal period";
    public const string MissingSignedDate = "Signed date";

    /// <summary>
    /// Display labels of required data that is empty on a stored contract. The labels are shown
    /// as they are in the UI ("Notice period", "Contract value", "Signed date", ...).
    /// </summary>
    public static List<string> MissingData(Contract contract)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(contract.Title)) missing.Add(MissingTitle);
        if (string.IsNullOrWhiteSpace(contract.CounterpartyName) && contract.SupplierId is null) missing.Add(MissingCounterparty);
        if (contract.NoticePeriodDays is null) missing.Add(MissingNoticePeriod);
        if (string.IsNullOrWhiteSpace(contract.Currency)) missing.Add(MissingCurrency);
        if (contract.AnnualValue is null && contract.TotalValue is null) missing.Add(MissingValue);
        if (contract.ChangeOfControl is null) missing.Add(MissingChangeOfControl);
        if (contract.AutoRenew && (contract.RenewalMonths is null || contract.RenewalMonths <= 0)) missing.Add(MissingRenewalPeriod);
        var isLive = contract.Status == ContractStatus.Active || contract.Status == ContractStatus.NoticeGiven;
        if (isLive && contract.SignedDate is null) missing.Add(MissingSignedDate);
        return missing;
    }

    /// <summary>
    /// The notice deadline as shown in lists: null once notice is given or the contract has ended,
    /// so finished contracts do not show up as overdue.
    /// </summary>
    public static DateOnly? ListNoticeDeadline(Contract contract)
    {
        if (contract.Status == ContractStatus.NoticeGiven
            || contract.Status == ContractStatus.Expired
            || contract.Status == ContractStatus.Terminated)
        {
            return null;
        }
        return NoticeDeadline(contract);
    }

    public static DateOnly RollForward(DateOnly endDate, int renewalMonths) => endDate.AddMonths(renewalMonths);

    /// <summary>Timeline shown on the contract record. States: done, current, upcoming, alert.</summary>
    public static List<LifecycleStep> Lifecycle(Contract contract, DateOnly today)
    {
        var steps = new List<LifecycleStep>();
        var status = contract.Status;
        var created = DateOnly.FromDateTime(contract.CreatedAt);

        steps.Add(new LifecycleStep("Draft", created, status == ContractStatus.Draft ? "current" : "done"));

        var approvalState = status switch
        {
            ContractStatus.Draft => "upcoming",
            ContractStatus.InApproval => "current",
            _ => "done"
        };
        steps.Add(new LifecycleStep("Approval", null, approvalState));

        var signedState = status switch
        {
            ContractStatus.Draft or ContractStatus.InApproval => "upcoming",
            ContractStatus.Approved => "current",
            _ => "done"
        };
        steps.Add(new LifecycleStep("Signed", contract.SignedDate, signedState));

        var isLive = status == ContractStatus.Active || status == ContractStatus.NoticeGiven;
        var isOver = status == ContractStatus.Expired || status == ContractStatus.Terminated;
        var deadline = NoticeDeadline(contract);

        string startState;
        if (isOver) startState = "done";
        else if (!isLive) startState = "upcoming";
        else if (contract.StartDate > today) startState = "upcoming";
        else if (deadline is null || today <= deadline.Value) startState = contract.EndDate is null ? "current" : "done";
        else startState = "done";
        steps.Add(new LifecycleStep("Start", contract.StartDate, startState));

        if (contract.EndDate is not null && deadline is not null)
        {
            string noticeState;
            if (isOver || status == ContractStatus.NoticeGiven || today > deadline.Value) noticeState = "done";
            else if (!isLive) noticeState = "upcoming";
            else if (contract.RenewalDecision is null && deadline.Value.DayNumber - today.DayNumber <= 30) noticeState = "alert";
            else noticeState = contract.StartDate <= today ? "current" : "upcoming";

            if (isLive && contract.RenewalDecision is null && today > deadline.Value && today <= contract.EndDate.Value)
            {
                // Deadline passed without a recorded decision.
                noticeState = "alert";
            }
            steps.Add(new LifecycleStep("Notice deadline", deadline, noticeState));

            string endState;
            if (isOver) endState = "done";
            else if (isLive && today > deadline.Value) endState = "current";
            else endState = "upcoming";
            var endName = contract.AutoRenew && status != ContractStatus.NoticeGiven && !isOver ? "Renews" : "Ends";
            steps.Add(new LifecycleStep(endName, contract.EndDate, endState));
        }
        else
        {
            steps.Add(new LifecycleStep("Open-ended", null, isOver ? "done" : "upcoming"));
        }

        return steps;
    }
}

/// <summary>Applies a renewal decision to a contract (brief: section 4, Reminders).</summary>
public static class RenewalRules
{
    /// <summary>
    /// Returns true when the contract was rolled forward into a new term: Renew with an explicit
    /// new end date, or Renew on an auto-renew contract (endDate moves by renewalMonths). The
    /// decision is cleared so the next cycle starts clean, and the renewed term is remembered in
    /// LastRenewedDeadline. Whether Renew is allowed at all is checked by the caller.
    /// </summary>
    public static bool Apply(
        Contract contract, RenewalDecision decision, Guid decidedById, DateTime nowUtc, string? comment,
        DateOnly? newEndDate = null)
    {
        DateOnly? rolledEndDate = null;
        if (decision == RenewalDecision.Renew && contract.EndDate is not null)
        {
            if (newEndDate is not null)
            {
                rolledEndDate = newEndDate;
            }
            else if (contract.AutoRenew && contract.RenewalMonths is > 0)
            {
                rolledEndDate = ContractCalculations.RollForward(contract.EndDate.Value, contract.RenewalMonths.Value);
            }
        }

        if (rolledEndDate is not null)
        {
            contract.LastRenewedDeadline = ContractCalculations.NoticeDeadline(contract);
            contract.EndDate = rolledEndDate;
            contract.RenewalDecision = null;
            contract.RenewalDecidedById = null;
            contract.RenewalDecidedAt = null;
            contract.RenewalComment = null;
            contract.Status = ContractStatus.Active;
            contract.UpdatedAt = nowUtc;
            return true;
        }

        contract.RenewalDecision = decision;
        contract.RenewalDecidedById = decidedById;
        contract.RenewalDecidedAt = nowUtc;
        contract.RenewalComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        contract.Status = decision == RenewalDecision.GiveNotice ? ContractStatus.NoticeGiven : ContractStatus.Active;
        contract.UpdatedAt = nowUtc;
        return false;
    }
}
