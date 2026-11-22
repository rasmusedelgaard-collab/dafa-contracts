using System.Globalization;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Background work: contract end/auto-renewal, notice reminders, escalations, obligation
/// reminders, holiday cover rerouting, supplier and directory sync. Run hourly by the API host
/// and on demand from admin. Every part is idempotent, so running it twice sends nothing twice.
/// </summary>
public class JobService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly INotifier _notifier;
    private readonly ISupplierSource _suppliers;
    private readonly IDirectoryService _directory;
    private readonly ApprovalService _approvals;
    private readonly AuditService _audit;
    private readonly ILogger<JobService> _logger;

    public JobService(
        IAppDbContext db, IClock clock, INotifier notifier, ISupplierSource suppliers, IDirectoryService directory,
        ApprovalService approvals, AuditService audit, ILogger<JobService> logger)
    {
        _db = db;
        _clock = clock;
        _notifier = notifier;
        _suppliers = suppliers;
        _directory = directory;
        _approvals = approvals;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// One run at a time in this process (M8): the hourly timer and the admin "run now" action
    /// wait for each other. Across several app instances the unique index on reminders
    /// (contract, kind, term) is what prevents a second row.
    /// </summary>
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    private static readonly TimeSpan RunGateTimeout = TimeSpan.FromMinutes(5);

    public async Task<JobRunResultDto> RunAllAsync(CancellationToken cancellationToken = default)
    {
        if (!await RunGate.WaitAsync(RunGateTimeout, cancellationToken))
        {
            var skipped = new JobRunResultDto { RanAt = _clock.UtcNow };
            skipped.Errors.Add("Another job run is still in progress; this run was skipped.");
            return skipped;
        }
        try
        {
            return await RunAllCoreAsync(cancellationToken);
        }
        finally
        {
            RunGate.Release();
        }
    }

    private async Task<JobRunResultDto> RunAllCoreAsync(CancellationToken cancellationToken)
    {
        var result = new JobRunResultDto { RanAt = _clock.UtcNow };

        await RunPartAsync("contract terms", result, async () =>
        {
            await RollContractTermsAsync(result, cancellationToken);
        });
        await RunPartAsync("notice reminders", result, async () =>
        {
            await SendNoticeRemindersAsync(result, cancellationToken);
        });
        await RunPartAsync("obligation reminders", result, async () =>
        {
            await SendObligationRemindersAsync(result, cancellationToken);
        });
        await RunPartAsync("holiday cover", result, async () =>
        {
            result.StepsRerouted = await _approvals.ApplyHolidayCoverAsync(cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        });
        await RunPartAsync("supplier sync", result, async () =>
        {
            result.SuppliersSynced = await SyncSuppliersAsync(cancellationToken);
        });
        await RunPartAsync("directory sync", result, async () =>
        {
            result.DirectoryChanges = await _directory.SyncAsync(cancellationToken);
        });
        await RunPartAsync("upload cleanup", result, async () =>
        {
            await PurgePendingUploadsAsync(cancellationToken);
        });

        return result;
    }

    private async Task RunPartAsync(string name, JobRunResultDto result, Func<Task> part)
    {
        try
        {
            await part();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job part '{Part}' failed", name);
            result.Errors.Add(name + ": " + ex.Message);
            // Drop whatever this part left half-done, so the next parts save only their own changes.
            _db.DiscardChanges();
        }
    }

    /// <summary>Ends contracts whose end date has passed, or rolls auto-renewing ones into the next term.</summary>
    public async Task RollContractTermsAsync(JobRunResultDto result, CancellationToken cancellationToken = default)
    {
        var today = _clock.Today;
        var now = _clock.UtcNow;
        var contracts = await _db.Contracts
            .Where(c => c.Status == ContractStatus.Active || c.Status == ContractStatus.NoticeGiven)
            .ToListAsync(cancellationToken);

        foreach (var contract in contracts)
        {
            if (contract.EndDate is null || contract.EndDate.Value >= today)
            {
                continue;
            }

            if (contract.Status == ContractStatus.NoticeGiven || contract.RenewalDecision == RenewalDecision.GiveNotice)
            {
                contract.Status = ContractStatus.Terminated;
                contract.UpdatedAt = now;
                result.ContractsEnded++;
                _audit.Record("contract.terminated", "Contract ended after notice", contract.Id);
            }
            else if (contract.AutoRenew && contract.RenewalMonths is > 0 && contract.RenewalDecision != RenewalDecision.LetExpire)
            {
                contract.LastRenewedDeadline = ContractCalculations.NoticeDeadline(contract);
                var endBefore = contract.EndDate.Value;
                var guard = 0;
                while (contract.EndDate.Value < today && guard < 120)
                {
                    contract.EndDate = ContractCalculations.RollForward(contract.EndDate.Value, contract.RenewalMonths.Value);
                    guard++;
                }
                contract.RenewalDecision = null;
                contract.RenewalDecidedById = null;
                contract.RenewalDecidedAt = null;
                contract.RenewalComment = null;
                contract.UpdatedAt = now;
                // New term, new notice deadline: the reminder cycle starts over (H2).
                await NoticeCycle.RemoveAsync(_db, contract.Id, cancellationToken);
                result.ContractsRenewed++;
                _audit.Record("renewal.automatic",
                    "Renewed automatically; new end date " + contract.EndDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), contract.Id,
                    ContractChanges.ToJson(new[]
                    {
                        new FieldChange("endDate", "End date", endBefore.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            contract.EndDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    }));
            }
            else
            {
                contract.Status = ContractStatus.Expired;
                contract.UpdatedAt = now;
                result.ContractsEnded++;
                _audit.Record("contract.expired", "Contract expired", contract.Id);
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string ReminderKey(Guid contractId, string kind, string termKey) =>
        contractId.ToString("N") + "|" + kind + "|" + termKey;

    /// <summary>
    /// Sends one reminder at most once per (contract, kind, term). Returns true when a notification
    /// was delivered or queued now. When delivery fails (Graph mail), the row is kept with SentAt
    /// empty and the job tries again on later runs, up to MaxReminderAttempts.
    /// </summary>
    private async Task<bool> SendOnceAsync(
        Dictionary<string, Reminder> existing, Guid contractId, Guid? obligationId, string kind, string termKey,
        DateOnly dueDate, Notification notification, JobRunResultDto result, CancellationToken cancellationToken)
    {
        var key = ReminderKey(contractId, kind, termKey);
        if (existing.TryGetValue(key, out var row))
        {
            if (row.SentAt is not null || row.Attempts >= ContractCalculations.MaxReminderAttempts)
            {
                return false;
            }
        }
        else
        {
            row = new Reminder
            {
                Id = Guid.NewGuid(),
                ContractId = contractId,
                ObligationId = obligationId,
                Kind = kind,
                TermKey = termKey,
                DueDate = dueDate,
                RecipientId = notification.RecipientId
            };
            _db.Reminders.Add(row);
            existing[key] = row;
        }

        row.Attempts++;
        row.RecipientId = notification.RecipientId;
        var delivered = await _notifier.SendAsync(notification, cancellationToken);
        if (delivered)
        {
            row.SentAt = _clock.UtcNow;
            return true;
        }
        if (row.Attempts >= ContractCalculations.MaxReminderAttempts)
        {
            result.Errors.Add("Reminder " + kind + " could not be delivered after " + row.Attempts
                + " attempts and was given up (" + notification.Subject + ").");
        }
        else
        {
            _logger.LogWarning("Reminder {Kind} for contract {ContractId} was not delivered (attempt {Attempt}); it will be retried",
                kind, contractId, row.Attempts);
        }
        return false;
    }

    /// <summary>
    /// Notice reminders (default 120/60/30 days before the notice deadline) to the chosen recipient,
    /// an escalation to the owner (default 14 days) while no renewal decision is recorded, and a
    /// yearly review reminder for open-ended contracts (60 days before each anniversary of the
    /// start date). One Reminder row per contract, kind and term makes this idempotent.
    /// </summary>
    public async Task SendNoticeRemindersAsync(JobRunResultDto result, CancellationToken cancellationToken = default)
    {
        var today = _clock.Today;
        var culture = CultureInfo.InvariantCulture;
        // Release 3: a contract type can have its own reminder schedule.
        var settingsFor = await ReminderSettingsReader.GetResolverAsync(_db, cancellationToken);

        var contracts = await _db.Contracts.AsNoTracking()
            .Where(c => c.Status == ContractStatus.Active)
            .ToListAsync(cancellationToken);
        var rows = await _db.Reminders.Where(r => r.ObligationId == null).ToListAsync(cancellationToken);
        var existing = new Dictionary<string, Reminder>();
        foreach (var row in rows)
        {
            existing[ReminderKey(row.ContractId, row.Kind, row.TermKey)] = row;
        }

        foreach (var contract in contracts)
        {
            var settings = settingsFor(contract.TypeId);
            var noticeDays = settings.GetNoticeDays();
            // Open-ended contracts have no notice deadline; they get a yearly review reminder instead (M5).
            var anniversary = ContractCalculations.DueAnnualReview(contract, today);
            if (anniversary is not null)
            {
                var reviewSent = await SendOnceAsync(
                    existing, contract.Id, null, ContractCalculations.AnnualReviewKind,
                    ContractCalculations.AnnualReviewTermKey(anniversary.Value),
                    anniversary.Value.AddDays(-ContractCalculations.AnnualReviewLeadDays),
                    new Notification(
                        ContractPeople.ReminderRecipientId(contract),
                        ContractCalculations.AnnualReviewKind,
                        "Annual review: " + contract.Title,
                        "Contract " + contract.ContractNo + " (" + contract.Title + ") with " + contract.CounterpartyName
                            + " is open-ended and has its anniversary on " + anniversary.Value.ToString("d MMM yyyy", culture)
                            + ". Review whether it should continue. To end it, record 'Give notice' in DAFA Contracts ("
                            + Math.Max(0, contract.NoticePeriodDays ?? 0) + " days' notice).",
                        contract.Id),
                    result, cancellationToken);
                if (reviewSent)
                {
                    result.AnnualReviewReminders++;
                }
            }

            if (!ContractCalculations.IsRenewalOpen(contract))
            {
                continue;
            }
            var deadline = ContractCalculations.NoticeDeadline(contract);
            if (deadline is null)
            {
                continue;
            }
            var termKey = ContractCalculations.NoticeTermKey(deadline.Value);
            var deadlineText = deadline.Value.ToString("d MMM yyyy", culture);

            var offset = ContractCalculations.DueNoticeOffset(deadline.Value, today, noticeDays);
            if (offset is not null)
            {
                var kind = ContractCalculations.NoticeKind(offset.Value);
                var daysLeft = deadline.Value.DayNumber - today.DayNumber;
                var noticeSent = await SendOnceAsync(
                    existing, contract.Id, null, kind, termKey, deadline.Value.AddDays(-offset.Value),
                    new Notification(
                        ContractPeople.ReminderRecipientId(contract),
                        kind,
                        "Renewal decision needed: " + contract.Title,
                        "Contract " + contract.ContractNo + " (" + contract.Title + ") with " + contract.CounterpartyName
                            + " has its notice deadline on " + deadlineText + " (" + daysLeft + " days). "
                            + (contract.AutoRenew ? "It renews automatically unless notice is given. " : "")
                            + "Record a renewal decision in DAFA Contracts.",
                        contract.Id),
                    result, cancellationToken);
                if (noticeSent)
                {
                    result.NoticeReminders++;
                }
            }

            var escalationDate = deadline.Value.AddDays(-settings.EscalationDays);
            var stillRunning = contract.EndDate is not null && today <= contract.EndDate.Value;
            if (today >= escalationDate && stillRunning)
            {
                var escalated = await SendOnceAsync(
                    existing, contract.Id, null, ContractCalculations.EscalationKind, termKey, escalationDate,
                    new Notification(
                        contract.OwnerId,
                        ContractCalculations.EscalationKind,
                        "Escalation: no renewal decision for " + contract.Title,
                        "Contract " + contract.ContractNo + " (" + contract.Title + ") has its notice deadline on " + deadlineText
                            + " and no renewal decision has been recorded. As owner, please decide now.",
                        contract.Id),
                    result, cancellationToken);
                if (escalated)
                {
                    result.Escalations++;
                }
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Reminds the responsible (default 14 days) before an open obligation is due. Once per due date.</summary>
    public async Task SendObligationRemindersAsync(JobRunResultDto result, CancellationToken cancellationToken = default)
    {
        var today = _clock.Today;
        var culture = CultureInfo.InvariantCulture;
        var settingsFor = await ReminderSettingsReader.GetResolverAsync(_db, cancellationToken);

        var obligations = await _db.Obligations.AsNoTracking()
            .Where(o => o.Status == ObligationStatus.Open && o.DueDate != null)
            .ToListAsync(cancellationToken);
        if (obligations.Count == 0)
        {
            return;
        }
        var rows = await _db.Reminders.Where(r => r.ObligationId != null).ToListAsync(cancellationToken);
        var existing = new Dictionary<string, Reminder>();
        foreach (var row in rows)
        {
            // Rows written before term keys existed are matched by obligation and due date.
            var termKey = string.IsNullOrEmpty(row.TermKey)
                ? ContractCalculations.ObligationTermKey(row.ObligationId!.Value, row.DueDate)
                : row.TermKey;
            existing[ReminderKey(row.ContractId, row.Kind, termKey)] = row;
        }
        var contracts = (await _db.Contracts.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(c => c.Id, c => c);

        foreach (var obligation in obligations)
        {
            var due = obligation.DueDate!.Value;
            if (!contracts.TryGetValue(obligation.ContractId, out var contract)
                || contract.Status == ContractStatus.Expired || contract.Status == ContractStatus.Terminated)
            {
                continue;
            }
            if (today < due.AddDays(-settingsFor(contract.TypeId).ObligationDays))
            {
                continue;
            }
            var sent = await SendOnceAsync(
                existing, contract.Id, obligation.Id, ContractCalculations.ObligationKind,
                ContractCalculations.ObligationTermKey(obligation.Id, due), due,
                new Notification(
                    obligation.ResponsibleId,
                    ContractCalculations.ObligationKind,
                    "Obligation due: " + obligation.Description,
                    "Obligation \"" + obligation.Description + "\" on contract " + contract.ContractNo + " (" + contract.Title
                        + ") is due on " + due.ToString("d MMM yyyy", culture) + ".",
                    contract.Id),
                result, cancellationToken);
            if (sent)
            {
                result.ObligationReminders++;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Upserts suppliers from the supplier source by external id.</summary>
    public async Task<int> SyncSuppliersAsync(CancellationToken cancellationToken = default)
    {
        var records = await _suppliers.GetSuppliersAsync(cancellationToken);
        if (records.Count == 0)
        {
            return 0;
        }
        var existing = await _db.Suppliers.ToListAsync(cancellationToken);
        var byExternalId = new Dictionary<string, Supplier>(StringComparer.OrdinalIgnoreCase);
        foreach (var supplier in existing)
        {
            if (!string.IsNullOrEmpty(supplier.ExternalId))
            {
                byExternalId[supplier.ExternalId] = supplier;
            }
        }

        var changed = 0;
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.ExternalId) || string.IsNullOrWhiteSpace(record.Name))
            {
                continue;
            }
            if (byExternalId.TryGetValue(record.ExternalId, out var supplier))
            {
                if (supplier.Name != record.Name || supplier.Country != record.Country || supplier.VatNo != record.VatNo || !supplier.IsActive)
                {
                    supplier.Name = record.Name;
                    supplier.Country = record.Country;
                    supplier.VatNo = record.VatNo;
                    supplier.IsActive = true;
                    changed++;
                }
            }
            else
            {
                var added = new Supplier
                {
                    Id = Guid.NewGuid(),
                    ExternalId = record.ExternalId,
                    Name = record.Name,
                    Country = record.Country,
                    VatNo = record.VatNo,
                    IsActive = true
                };
                _db.Suppliers.Add(added);
                byExternalId[record.ExternalId] = added;
                changed++;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    public async Task PurgePendingUploadsAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.UtcNow.AddDays(-7);
        // Only the ids are read: the rows hold the file content (up to 25 MB each).
        var oldIds = await _db.PendingUploads.AsNoTracking()
            .Where(u => u.CreatedAt < cutoff)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        if (oldIds.Count > 0)
        {
            foreach (var id in oldIds)
            {
                _db.PendingUploads.Remove(new PendingUpload { Id = id });
            }
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
