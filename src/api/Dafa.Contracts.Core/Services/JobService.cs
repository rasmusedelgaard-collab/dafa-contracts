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

    public async Task<JobRunResultDto> RunAllAsync(CancellationToken cancellationToken = default)
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
                var contractId = contract.Id;
                var old = await _db.Reminders.Where(r => r.ContractId == contractId && r.ObligationId == null).ToListAsync(cancellationToken);
                _db.Reminders.RemoveRange(old);
                result.ContractsRenewed++;
                _audit.Record("renewal.automatic",
                    "Renewed automatically; new end date " + contract.EndDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), contract.Id);
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

    /// <summary>
    /// Notice reminders (default 120/60/30 days before the notice deadline) to the chosen recipient
    /// and an escalation to the owner (default 14 days) while no renewal decision is recorded.
    /// One Reminder row per contract and kind makes this idempotent.
    /// </summary>
    public async Task SendNoticeRemindersAsync(JobRunResultDto result, CancellationToken cancellationToken = default)
    {
        var today = _clock.Today;
        var now = _clock.UtcNow;
        var culture = CultureInfo.InvariantCulture;
        var settings = await ReminderSettingsReader.GetAsync(_db, cancellationToken);
        var noticeDays = settings.GetNoticeDays();

        var contracts = await _db.Contracts.AsNoTracking()
            .Where(c => c.Status == ContractStatus.Active)
            .ToListAsync(cancellationToken);
        var existing = await _db.Reminders.AsNoTracking().Where(r => r.ObligationId == null).ToListAsync(cancellationToken);
        var sent = new HashSet<string>(existing.Select(r => r.ContractId.ToString("N") + "|" + r.Kind));

        foreach (var contract in contracts)
        {
            if (!ContractCalculations.IsRenewalOpen(contract))
            {
                continue;
            }
            var deadline = ContractCalculations.NoticeDeadline(contract);
            if (deadline is null)
            {
                continue;
            }
            var key = contract.Id.ToString("N") + "|";
            var deadlineText = deadline.Value.ToString("d MMM yyyy", culture);

            var offset = ContractCalculations.DueNoticeOffset(deadline.Value, today, noticeDays);
            if (offset is not null)
            {
                var kind = ContractCalculations.NoticeKind(offset.Value);
                if (sent.Add(key + kind))
                {
                    var recipientId = ContractPeople.ReminderRecipientId(contract);
                    var daysLeft = deadline.Value.DayNumber - today.DayNumber;
                    await _notifier.SendAsync(new Notification(
                        recipientId,
                        kind,
                        "Renewal decision needed: " + contract.Title,
                        "Contract " + contract.ContractNo + " (" + contract.Title + ") with " + contract.CounterpartyName
                            + " has its notice deadline on " + deadlineText + " (" + daysLeft + " days). "
                            + (contract.AutoRenew ? "It renews automatically unless notice is given. " : "")
                            + "Record a renewal decision in DAFA Contracts.",
                        contract.Id), cancellationToken);
                    _db.Reminders.Add(new Reminder
                    {
                        Id = Guid.NewGuid(),
                        ContractId = contract.Id,
                        Kind = kind,
                        DueDate = deadline.Value.AddDays(-offset.Value),
                        SentAt = now,
                        RecipientId = recipientId
                    });
                    result.NoticeReminders++;
                }
            }

            var escalationDate = deadline.Value.AddDays(-settings.EscalationDays);
            var stillRunning = contract.EndDate is not null && today <= contract.EndDate.Value;
            if (today >= escalationDate && stillRunning && sent.Add(key + ContractCalculations.EscalationKind))
            {
                await _notifier.SendAsync(new Notification(
                    contract.OwnerId,
                    ContractCalculations.EscalationKind,
                    "Escalation: no renewal decision for " + contract.Title,
                    "Contract " + contract.ContractNo + " (" + contract.Title + ") has its notice deadline on " + deadlineText
                        + " and no renewal decision has been recorded. As owner, please decide now.",
                    contract.Id), cancellationToken);
                _db.Reminders.Add(new Reminder
                {
                    Id = Guid.NewGuid(),
                    ContractId = contract.Id,
                    Kind = ContractCalculations.EscalationKind,
                    DueDate = escalationDate,
                    SentAt = now,
                    RecipientId = contract.OwnerId
                });
                result.Escalations++;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Reminds the responsible (default 14 days) before an open obligation is due. Once per due date.</summary>
    public async Task SendObligationRemindersAsync(JobRunResultDto result, CancellationToken cancellationToken = default)
    {
        var today = _clock.Today;
        var now = _clock.UtcNow;
        var culture = CultureInfo.InvariantCulture;
        var settings = await ReminderSettingsReader.GetAsync(_db, cancellationToken);

        var obligations = await _db.Obligations.AsNoTracking()
            .Where(o => o.Status == ObligationStatus.Open && o.DueDate != null)
            .ToListAsync(cancellationToken);
        if (obligations.Count == 0)
        {
            return;
        }
        var existing = await _db.Reminders.AsNoTracking().Where(r => r.ObligationId != null).ToListAsync(cancellationToken);
        var sent = new HashSet<string>(existing.Select(r => r.ObligationId!.Value.ToString("N") + "|" + r.DueDate.DayNumber));
        var contracts = (await _db.Contracts.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(c => c.Id, c => c);

        foreach (var obligation in obligations)
        {
            var due = obligation.DueDate!.Value;
            if (today < due.AddDays(-settings.ObligationDays))
            {
                continue;
            }
            if (!contracts.TryGetValue(obligation.ContractId, out var contract)
                || contract.Status == ContractStatus.Expired || contract.Status == ContractStatus.Terminated)
            {
                continue;
            }
            if (!sent.Add(obligation.Id.ToString("N") + "|" + due.DayNumber))
            {
                continue;
            }
            await _notifier.SendAsync(new Notification(
                obligation.ResponsibleId,
                ContractCalculations.ObligationKind,
                "Obligation due: " + obligation.Description,
                "Obligation \"" + obligation.Description + "\" on contract " + contract.ContractNo + " (" + contract.Title
                    + ") is due on " + due.ToString("d MMM yyyy", culture) + ".",
                contract.Id), cancellationToken);
            _db.Reminders.Add(new Reminder
            {
                Id = Guid.NewGuid(),
                ContractId = contract.Id,
                ObligationId = obligation.Id,
                Kind = ContractCalculations.ObligationKind,
                DueDate = due,
                SentAt = now,
                RecipientId = obligation.ResponsibleId
            });
            result.ObligationReminders++;
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

    private async Task PurgePendingUploadsAsync(CancellationToken cancellationToken)
    {
        var cutoff = _clock.UtcNow.AddDays(-7);
        var old = await _db.PendingUploads.Where(u => u.CreatedAt < cutoff).ToListAsync(cancellationToken);
        if (old.Count > 0)
        {
            _db.PendingUploads.RemoveRange(old);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
