using System.Diagnostics;
using System.Globalization;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Runs the background jobs of CONTRACT_V3 section 9.7 on their own schedules (checked by the
/// hosted service every few minutes) or on demand, and records the last run of each.
/// </summary>
public class JobRunner
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly JobService _jobs;
    private readonly ApprovalService _approvals;
    private readonly IDirectoryService _directory;
    private readonly SyncService _sync;
    private readonly DocumentTextIndexer _indexer;
    private readonly DigestService _digest;
    private readonly IExportStorage _exports;
    private readonly ILogger<JobRunner> _logger;

    public JobRunner(
        IAppDbContext db, IClock clock, JobService jobs, ApprovalService approvals, IDirectoryService directory,
        SyncService sync, DocumentTextIndexer indexer, DigestService digest, IExportStorage exports, ILogger<JobRunner> logger)
    {
        _db = db;
        _clock = clock;
        _jobs = jobs;
        _approvals = approvals;
        _directory = directory;
        _sync = sync;
        _indexer = indexer;
        _digest = digest;
        _exports = exports;
        _logger = logger;
    }

    /// <summary>Runs one job's work. Returns (status, message).</summary>
    private async Task<(string Status, string Message)> ExecuteAsync(string key, CancellationToken cancellationToken)
    {
        var result = new JobRunResultDto { RanAt = _clock.UtcNow };
        switch (key)
        {
            case JobKeys.Reminders:
                await _jobs.SendNoticeRemindersAsync(result, cancellationToken);
                result.StepsRerouted = await _approvals.ApplyHolidayCoverAsync(cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                return Outcome(result, result.NoticeReminders + " notice reminders, " + result.Escalations + " escalations, "
                    + result.AnnualReviewReminders + " annual reviews, " + result.StepsRerouted + " steps rerouted");
            case JobKeys.Obligations:
                await _jobs.SendObligationRemindersAsync(result, cancellationToken);
                return Outcome(result, result.ObligationReminders + " obligation reminders");
            case JobKeys.ContractEnd:
                await _jobs.RollContractTermsAsync(result, cancellationToken);
                return Outcome(result, result.ContractsRenewed + " renewed, " + result.ContractsEnded + " ended");
            case JobKeys.Digest:
            {
                var sent = await _digest.RunAsync(cancellationToken);
                return ("Succeeded", sent + " digests sent");
            }
            case JobKeys.CounterpartySync:
            {
                var suppliers = await _jobs.SyncSuppliersAsync(cancellationToken);
                var sources = await _sync.RunDueAsync(cancellationToken);
                return ("Succeeded", suppliers + " supplier master changes, " + sources + " sync sources run");
            }
            case JobKeys.DirectorySync:
            {
                var changes = await _directory.SyncAsync(cancellationToken);
                return ("Succeeded", changes + " directory changes");
            }
            case JobKeys.UploadCleanup:
            {
                await _jobs.PurgePendingUploadsAsync(cancellationToken);
                var sessions = await ImportService.PurgeSessionsAsync(_db, _clock, cancellationToken);
                var indexed = await _indexer.BackfillAsync(DocumentTextIndexer.DefaultBatch, cancellationToken);
                return ("Succeeded", sessions + " import sessions removed, " + indexed + " documents indexed for search");
            }
            case JobKeys.DataRoomCleanup:
            {
                var removed = await DataRoomService.CleanupAsync(_db, _exports, _clock, cancellationToken);
                return ("Succeeded", removed + " expired exports removed");
            }
        }
        throw new NotFoundException("Unknown job.");
    }

    private static (string Status, string Message) Outcome(JobRunResultDto result, string message)
    {
        if (result.Errors.Count == 0)
        {
            return ("Succeeded", message);
        }
        return ("PartiallySucceeded", message + "; " + string.Join(" ", result.Errors.Take(5)));
    }

    private async Task<JobSchedule> RunAndRecordAsync(JobSchedule row, CancellationToken cancellationToken)
    {
        var key = row.JobKey;
        var started = _clock.UtcNow;
        var watch = Stopwatch.StartNew();
        string status;
        string message;
        try
        {
            (status, message) = await ExecuteAsync(key, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {Job} failed", key);
            _db.DiscardChanges();
            status = "Failed";
            message = ex.Message;
        }
        watch.Stop();
        // The job may have discarded tracked rows; read the schedule row again.
        var stored = await _db.JobSchedules.FirstOrDefaultAsync(j => j.JobKey == key, cancellationToken) ?? row;
        stored.LastRunAt = started;
        stored.LastStatus = status;
        stored.LastMessage = message.Length > 1000 ? message.Substring(0, 1000) : message;
        stored.LastDurationMs = (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds);
        stored.NextRunAt = ScheduleCalculator.NextRun(stored.Frequency, stored.Time, stored.DayOfWeek, _clock.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return stored;
    }

    /// <summary>POST admin/jobs/{key}/run: runs one job now (synchronously) and returns its info.</summary>
    public async Task<JobInfoDto> RunNowAsync(string key, CancellationToken cancellationToken = default)
    {
        var rows = await SettingsService.EnsureJobSchedulesAsync(_db, _clock, cancellationToken);
        var row = rows.FirstOrDefault(r => r.JobKey == key) ?? throw new NotFoundException("Unknown job.");
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken))
        {
            throw new ConflictException("Another job is running. Try again in a minute.");
        }
        try
        {
            return SettingsService.ToDto(await RunAndRecordAsync(row, cancellationToken));
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Hosted service tick: runs every job whose next run is due. Returns how many ran.</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken = default)
    {
        if (!await Gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return 0;
        }
        try
        {
            var rows = await SettingsService.EnsureJobSchedulesAsync(_db, _clock, cancellationToken);
            var now = _clock.UtcNow;
            var count = 0;
            foreach (var key in JobKeys.All.Select(j => j.Key))
            {
                var row = rows.FirstOrDefault(r => r.JobKey == key);
                if (row is null || row.Frequency == ScheduleFrequency.Off)
                {
                    continue;
                }
                if (row.NextRunAt is null)
                {
                    row.NextRunAt = ScheduleCalculator.NextRun(row.Frequency, row.Time, row.DayOfWeek, now);
                    await _db.SaveChangesAsync(cancellationToken);
                    continue;
                }
                if (row.NextRunAt > now)
                {
                    continue;
                }
                await RunAndRecordAsync(row, cancellationToken);
                count++;
                // Rows may have been detached by a failed job; reload for the next one.
                rows = await SettingsService.EnsureJobSchedulesAsync(_db, _clock, cancellationToken);
            }
            return count;
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>
/// Digest e-mails (job Digest): for users whose preference (or the admin default) is Daily or
/// Weekly, a summary of their upcoming notice deadlines, approvals waiting, changes on watched
/// contracts and unread notifications. Only visible contracts are mentioned; nothing is sent when
/// there is nothing to report.
/// </summary>
public class DigestService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly INotifier _notifier;

    public DigestService(IAppDbContext db, IClock clock, INotifier notifier)
    {
        _db = db;
        _clock = clock;
        _notifier = notifier;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var today = _clock.Today;
        var culture = CultureInfo.InvariantCulture;
        var digestDefault = await AppSettingsStore.LoadDigestDefaultAsync(_db, cancellationToken);
        var users = await _db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(cancellationToken);
        var preferences = (await _db.UserPreferences.ToListAsync(cancellationToken)).ToDictionary(p => p.UserId);
        var sent = 0;
        foreach (var user in users)
        {
            preferences.TryGetValue(user.Id, out var preference);
            var frequency = preference?.Digest ?? digestDefault;
            if (frequency != "Daily" && frequency != "Weekly")
            {
                continue;
            }
            var period = frequency == "Daily" ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
            var last = preference?.LastDigestAt;
            if (last is not null && now - last.Value < period - TimeSpan.FromHours(1))
            {
                continue;
            }
            var since = last ?? now - period;
            var profile = await AccessProfileLoader.LoadAsync(_db, user.Id, cancellationToken);
            if (profile is null)
            {
                continue;
            }
            var approvals = await ContractAccessService.LoadApprovalContractIdsAsync(_db, user.Id, cancellationToken);
            var visible = ContractAccessService.ApplyFilter(_db.Contracts.AsNoTracking(), profile, approvals);
            var userId = user.Id;

            var mine = await visible.Where(c => c.Status == ContractStatus.Active && c.RenewalDecision == null
                && (c.OwnerId == userId || c.DelegateId == userId || c.ResponsibleId == userId)).ToListAsync(cancellationToken);
            var deadlines = mine
                .Select(c => (Contract: c, Deadline: ContractCalculations.NoticeDeadline(c)))
                .Where(x => x.Deadline is not null && x.Deadline.Value >= today && x.Deadline.Value <= today.AddDays(30))
                .OrderBy(x => x.Deadline)
                .ToList();
            var waiting = await _db.ApprovalSteps.AsNoTracking().CountAsync(s => s.ApproverId == userId && s.Status == ApprovalStepStatus.Pending, cancellationToken);
            var watchedIds = _db.ContractWatchers.AsNoTracking().Where(w => w.UserId == userId).Select(w => w.ContractId);
            var visibleIds = visible.Select(c => c.Id);
            var changes = await _db.AuditEvents.AsNoTracking()
                .Where(e => e.ContractId != null && watchedIds.Contains(e.ContractId.Value) && visibleIds.Contains(e.ContractId.Value)
                    && e.At > since && e.UserId != userId && e.Action != AuditService.ContractViewed && e.Action != "contract.watch" && e.Action != "document.opened")
                .Select(e => e.ContractId!.Value)
                .Distinct()
                .CountAsync(cancellationToken);
            var unread = await _db.Notifications.AsNoTracking().CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken);
            if (deadlines.Count == 0 && waiting == 0 && changes == 0 && unread == 0)
            {
                continue;
            }
            var body = new System.Text.StringBuilder();
            body.AppendLine("Your " + (frequency == "Daily" ? "daily" : "weekly") + " summary from DAFA Contracts.");
            if (deadlines.Count > 0)
            {
                body.AppendLine();
                body.AppendLine("Notice deadlines in the next 30 days:");
                foreach (var item in deadlines.Take(20))
                {
                    body.AppendLine("- " + item.Contract.ContractNo + " " + item.Contract.Title + ": " + item.Deadline!.Value.ToString("d MMM yyyy", culture));
                }
            }
            if (waiting > 0) body.AppendLine().AppendLine(waiting + " approval" + (waiting == 1 ? " is" : "s are") + " waiting for you.");
            if (changes > 0) body.AppendLine().AppendLine(changes + " watched contract" + (changes == 1 ? " was" : "s were") + " changed.");
            if (unread > 0) body.AppendLine().AppendLine(unread + " unread notification" + (unread == 1 ? "" : "s") + ".");
            await _notifier.SendAsync(new Notification(userId, "Digest", "Your DAFA Contracts summary", body.ToString(), null), cancellationToken);
            if (preference is null)
            {
                preference = new UserPreference { UserId = userId, UpdatedAt = now };
                _db.UserPreferences.Add(preference);
                preferences[userId] = preference;
            }
            preference.LastDigestAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            sent++;
        }
        return sent;
    }
}
