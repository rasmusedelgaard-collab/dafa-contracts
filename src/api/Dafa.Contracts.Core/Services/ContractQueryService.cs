using System.Globalization;
using System.Text;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

public class ContractListQuery
{
    public string? View { get; set; }
    public string? Q { get; set; }
    public Guid? EntityId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? TypeId { get; set; }
    public string? Status { get; set; }
    public string? Sort { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}

public static class ContractPeople
{
    /// <summary>The user who receives notice reminders; falls back to the owner when the chosen role is empty.</summary>
    public static Guid ReminderRecipientId(Contract contract)
    {
        return contract.ReminderRecipient switch
        {
            ReminderRecipient.Delegate => contract.DelegateId ?? contract.OwnerId,
            ReminderRecipient.Responsible => contract.ResponsibleId ?? contract.OwnerId,
            _ => contract.OwnerId
        };
    }
}

/// <summary>
/// All read endpoints. Every contract read starts from IContractAccessService; filtering,
/// calculated fields, sorting and paging then happen in memory (the register is ~800 rows),
/// which keeps the SQL identical and trivial on Sqlite and SQL Server.
/// </summary>
public class ContractQueryService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IContractAccessService _access;
    private readonly IClock _clock;
    private readonly IDocumentStore _store;
    private readonly DocumentWriter _documents;
    private readonly AuditService _audit;

    public ContractQueryService(
        IAppDbContext db, ICurrentUser currentUser, IContractAccessService access, IClock clock,
        IDocumentStore store, DocumentWriter documents, AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _access = access;
        _clock = clock;
        _store = store;
        _documents = documents;
        _audit = audit;
    }

    // ---------- shared ----------

    private async Task<List<Contract>> LoadVisibleAsync(CancellationToken cancellationToken)
    {
        var query = await _access.FilterAsync(_db.Contracts.AsNoTracking(), cancellationToken);
        return await query.ToListAsync(cancellationToken);
    }

    private static bool IsLive(Contract contract) =>
        contract.Status == ContractStatus.Active || contract.Status == ContractStatus.NoticeGiven;

    public static ContractListItemDto ToListItem(Contract contract, ReferenceData reference, DateOnly today)
    {
        var item = new ContractListItemDto();
        FillListItem(item, contract, reference, today);
        return item;
    }

    private static void FillListItem(ContractListItemDto item, Contract contract, ReferenceData reference, DateOnly today)
    {
        var deadline = ContractCalculations.ListNoticeDeadline(contract);
        item.Id = contract.Id;
        item.ContractNo = contract.ContractNo;
        item.Title = contract.Title;
        item.Counterparty = contract.CounterpartyName;
        item.TypeName = reference.Type(contract.TypeId);
        item.EntityName = reference.Entity(contract.EntityId);
        item.DepartmentName = reference.Department(contract.DepartmentId);
        item.OwnerName = reference.UserName(contract.OwnerId);
        item.OwnerInitials = reference.UserInitials(contract.OwnerId);
        item.NoticeDeadline = deadline;
        item.DaysToNotice = ContractCalculations.DaysToNotice(deadline, today);
        item.AnnualValue = contract.AnnualValue;
        item.Currency = contract.Currency;
        item.Status = contract.Status.ToString();
        item.MissingData = ContractCalculations.MissingData(contract);
    }

    private static bool ContainsText(string? value, string term) =>
        !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    // ---------- list / export ----------

    private async Task<List<Contract>> QueryAsync(ContractListQuery query, ReferenceData reference, CancellationToken cancellationToken)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var today = _clock.Today;
        IEnumerable<Contract> items = await LoadVisibleAsync(cancellationToken);

        switch ((query.View ?? "all").Trim().ToLowerInvariant())
        {
            case "due90":
                // Same rule as the home tile and the overview count: no decision recorded yet.
                items = items.Where(c => IsDueWithin(c, today, 90));
                break;
            case "approval":
                items = items.Where(c => c.Status == ContractStatus.InApproval);
                break;
            case "mine":
                items = items.Where(c => ContractAccessRules.IsOnContract(profile, c));
                break;
            case "missing":
                items = items.Where(c => ContractCalculations.MissingData(c).Count > 0);
                break;
        }

        if (query.EntityId is not null) items = items.Where(c => c.EntityId == query.EntityId.Value);
        if (query.DepartmentId is not null) items = items.Where(c => c.DepartmentId == query.DepartmentId.Value);
        if (query.TypeId is not null) items = items.Where(c => c.TypeId == query.TypeId.Value);

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var statuses = new HashSet<ContractStatus>();
            foreach (var part in query.Status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<ContractStatus>(part, true, out var status))
                {
                    statuses.Add(status);
                }
            }
            if (statuses.Count > 0)
            {
                items = items.Where(c => statuses.Contains(c.Status));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var term = query.Q.Trim();
            items = items.Where(c =>
                ContainsText(c.Title, term)
                || ContainsText(c.ContractNo, term)
                || ContainsText(c.CounterpartyName, term)
                || ContainsText(reference.UserName(c.OwnerId), term));
        }

        var list = items.ToList();
        SortContracts(list, query.Sort, reference);
        return list;
    }

    private static void SortContracts(List<Contract> list, string? sort, ReferenceData reference)
    {
        var key = (sort ?? "").Trim();
        var descending = false;
        if (key.StartsWith("-", StringComparison.Ordinal))
        {
            descending = true;
            key = key.Substring(1);
        }
        foreach (var suffix in new[] { ":desc", " desc", ".desc" })
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                descending = true;
                key = key.Substring(0, key.Length - suffix.Length);
            }
        }
        foreach (var suffix in new[] { ":asc", " asc", ".asc" })
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                key = key.Substring(0, key.Length - suffix.Length);
            }
        }
        key = key.Trim().ToLowerInvariant();

        list.Sort((a, b) =>
        {
            var result = CompareBy(a, b, key, descending, reference);
            return result != 0 ? result : string.CompareOrdinal(a.ContractNo, b.ContractNo);
        });
    }

    private static int CompareBy(Contract a, Contract b, string key, bool descending, ReferenceData reference)
    {
        switch (key)
        {
            case "title":
                return CompareText(a.Title, b.Title, descending);
            case "contractno":
                return CompareText(a.ContractNo, b.ContractNo, descending);
            case "counterparty":
            case "counterpartyname":
                return CompareText(a.CounterpartyName, b.CounterpartyName, descending);
            case "typename":
            case "type":
                return CompareText(reference.Type(a.TypeId), reference.Type(b.TypeId), descending);
            case "entityname":
            case "entity":
                return CompareText(reference.Entity(a.EntityId), reference.Entity(b.EntityId), descending);
            case "departmentname":
            case "department":
                return CompareText(reference.Department(a.DepartmentId), reference.Department(b.DepartmentId), descending);
            case "ownername":
            case "owner":
                return CompareText(reference.UserName(a.OwnerId), reference.UserName(b.OwnerId), descending);
            case "status":
                return CompareText(a.Status.ToString(), b.Status.ToString(), descending);
            case "annualvalue":
            case "value":
                return CompareNullable(a.AnnualValue, b.AnnualValue, descending);
            case "startdate":
                return CompareNullable<DateOnly>(a.StartDate, b.StartDate, descending);
            case "enddate":
                return CompareNullable(a.EndDate, b.EndDate, descending);
            default:
                // notice / noticeDeadline / daysToNotice / no sort: soonest deadline first, contracts without one last.
                return CompareNullable(ContractCalculations.ListNoticeDeadline(a), ContractCalculations.ListNoticeDeadline(b), descending);
        }
    }

    private static int CompareText(string? a, string? b, bool descending)
    {
        var result = string.Compare(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        return descending ? -result : result;
    }

    /// <summary>Nulls always sort last, whatever the direction.</summary>
    private static int CompareNullable<T>(T? a, T? b, bool descending) where T : struct, IComparable<T>
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;
        var result = a.Value.CompareTo(b.Value);
        return descending ? -result : result;
    }

    public async Task<PagedDto<ContractListItemDto>> ListAsync(ContractListQuery query, CancellationToken cancellationToken = default)
    {
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var all = await QueryAsync(query, reference, cancellationToken);
        var page = Math.Max(1, query.Page ?? 1);
        var pageSize = Math.Clamp(query.PageSize ?? 25, 1, 200);
        var today = _clock.Today;
        return new PagedDto<ContractListItemDto>
        {
            Items = all.Skip((page - 1) * pageSize).Take(pageSize).Select(c => ToListItem(c, reference, today)).ToList(),
            Total = all.Count,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<byte[]> ExportCsvAsync(ContractListQuery query, CancellationToken cancellationToken = default)
    {
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var all = await QueryAsync(query, reference, cancellationToken);
        var today = _clock.Today;
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", new[]
        {
            "Contract no", "Title", "Counterparty", "Type", "Entity", "Department", "Owner", "Status",
            "Start date", "End date", "Notice period (days)", "Notice deadline", "Days to notice",
            "Auto-renew", "Annual value", "Total value", "Currency", "Missing data"
        }));
        foreach (var contract in all)
        {
            var deadline = ContractCalculations.ListNoticeDeadline(contract);
            var cells = new[]
            {
                contract.ContractNo,
                contract.Title,
                contract.CounterpartyName,
                reference.Type(contract.TypeId),
                reference.Entity(contract.EntityId),
                reference.Department(contract.DepartmentId),
                reference.UserName(contract.OwnerId),
                contract.Status.ToString(),
                FormatDate(contract.StartDate),
                FormatDate(contract.EndDate),
                contract.NoticePeriodDays?.ToString(CultureInfo.InvariantCulture) ?? "",
                FormatDate(deadline),
                ContractCalculations.DaysToNotice(deadline, today)?.ToString(CultureInfo.InvariantCulture) ?? "",
                contract.AutoRenew ? "Yes" : "No",
                contract.AnnualValue?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
                contract.TotalValue?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
                contract.Currency,
                string.Join("; ", ContractCalculations.MissingData(contract))
            };
            csv.AppendLine(string.Join(",", cells.Select(CsvCell)));
        }
        _audit.Record("contracts.exported", "Exported " + all.Count + " contracts to CSV");
        await _db.SaveChangesAsync(cancellationToken);

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(csv.ToString());
        var bytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
        return bytes;
    }

    private static string FormatDate(DateOnly? date) =>
        date is null ? "" : date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string CsvCell(string? value)
    {
        var text = value ?? "";
        // Stop spreadsheet programs from treating user text as a formula.
        // Excel also evaluates cells that start with a tab or a carriage return.
        if (text.Length > 0 && "=+-@\t\r".IndexOf(text[0]) >= 0)
        {
            text = "'" + text;
        }
        if (text.IndexOfAny(new[] { ',', '"', '\n', '\r', '\t' }) >= 0)
        {
            text = "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        return text;
    }

    // ---------- detail ----------

    public async Task<ContractDetailDto> GetDetailAsync(Guid contractId, bool recordView, CancellationToken cancellationToken = default)
    {
        var contract = await _access.GetReadableAsync(contractId, false, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var detail = await BuildDetailAsync(contract, profile, reference, cancellationToken);
        if (recordView)
        {
            _audit.Record(AuditService.ContractViewed, "Opened contract " + contract.ContractNo, contract.Id);
            await _db.SaveChangesAsync(cancellationToken);
        }
        return detail;
    }

    private async Task<ContractDetailDto> BuildDetailAsync(
        Contract contract, AccessProfile profile, ReferenceData reference, CancellationToken cancellationToken)
    {
        var today = _clock.Today;
        var id = contract.Id;
        var detail = new ContractDetailDto();
        FillListItem(detail, contract, reference, today);

        detail.TypeId = contract.TypeId;
        detail.SupplierId = contract.SupplierId;
        detail.CounterpartyName = contract.CounterpartyName;
        detail.EntityId = contract.EntityId;
        detail.DepartmentId = contract.DepartmentId;
        detail.OwnerId = contract.OwnerId;
        detail.DelegateId = contract.DelegateId;
        detail.ResponsibleId = contract.ResponsibleId;
        detail.ReminderRecipient = contract.ReminderRecipient.ToString();
        detail.StartDate = contract.StartDate;
        detail.EndDate = contract.EndDate;
        detail.OpenEnded = contract.EndDate is null;
        detail.NoticePeriodDays = contract.NoticePeriodDays;
        detail.AutoRenew = contract.AutoRenew;
        detail.RenewalMonths = contract.RenewalMonths;
        detail.TotalValue = contract.TotalValue;
        detail.PaymentTerms = contract.PaymentTerms;
        detail.PriceAdjustment = contract.PriceAdjustment;
        detail.PriceAdjustmentDate = contract.PriceAdjustmentDate;
        detail.GoverningLaw = contract.GoverningLaw;
        detail.LiabilityCap = contract.LiabilityCap;
        detail.Exclusivity = contract.Exclusivity;
        detail.ChangeOfControl = contract.ChangeOfControl;
        detail.Summary = contract.Summary;
        detail.SignedDate = contract.SignedDate;
        detail.SigningMethod = contract.SigningMethod;
        detail.RenewalDecision = contract.RenewalDecision?.ToString();
        detail.RenewalDecidedById = contract.RenewalDecidedById;
        detail.RenewalDecidedBy = contract.RenewalDecidedById is null ? null : reference.UserName(contract.RenewalDecidedById);
        detail.RenewalDecidedAt = contract.RenewalDecidedAt;
        detail.RenewalComment = contract.RenewalComment;

        detail.Lifecycle = ContractCalculations.Lifecycle(contract, today)
            .Select(s => new LifecycleDto { Name = s.Name, Date = s.Date, State = s.State })
            .ToList();

        // People
        var recipientId = ContractPeople.ReminderRecipientId(contract);
        AddPerson(detail, "Owner", contract.OwnerId, recipientId, reference);
        AddPerson(detail, "Delegate", contract.DelegateId, recipientId, reference);
        AddPerson(detail, "Responsible", contract.ResponsibleId, recipientId, reference);

        // Documents
        var documents = await _db.Documents.AsNoTracking().Where(d => d.ContractId == id).ToListAsync(cancellationToken);
        detail.Documents = documents
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => new DocumentDto
            {
                Id = d.Id,
                Name = d.Name,
                Kind = d.Kind.ToString(),
                Version = d.Version,
                Size = d.Size,
                UploadedAt = d.UploadedAt,
                UploadedBy = d.UploadedById is null ? "System" : reference.UserName(d.UploadedById)
            })
            .ToList();

        // Obligations
        var obligations = await _db.Obligations.AsNoTracking().Where(o => o.ContractId == id).ToListAsync(cancellationToken);
        detail.Obligations = obligations
            .OrderBy(o => o.Status)
            .ThenBy(o => o.DueDate ?? DateOnly.MaxValue)
            .Select(o => ToObligationDto(o, reference))
            .ToList();

        // Links, only to contracts the user may see
        var links = await _db.ContractLinks.AsNoTracking().Where(l => l.FromId == id || l.ToId == id).ToListAsync(cancellationToken);
        if (links.Count > 0)
        {
            var otherIds = links.Select(l => l.FromId == id ? l.ToId : l.FromId).Distinct().ToList();
            var visible = await _access.FilterAsync(_db.Contracts.AsNoTracking().Where(c => otherIds.Contains(c.Id)), cancellationToken);
            var others = (await visible.ToListAsync(cancellationToken)).ToDictionary(c => c.Id, c => c.Title);
            foreach (var link in links)
            {
                var outgoing = link.FromId == id;
                var otherId = outgoing ? link.ToId : link.FromId;
                if (others.TryGetValue(otherId, out var title))
                {
                    detail.Links.Add(new LinkDto
                    {
                        Id = link.Id,
                        ContractId = otherId,
                        Title = title,
                        Kind = link.Kind.ToString(),
                        Direction = outgoing ? "out" : "in"
                    });
                }
            }
        }

        // Approval: the most recent instance
        var instances = await _db.ApprovalInstances.AsNoTracking().Where(i => i.ContractId == id).ToListAsync(cancellationToken);
        var instance = instances.OrderByDescending(i => i.StartedAt).FirstOrDefault();
        if (instance is not null)
        {
            var instanceId = instance.Id;
            var steps = await _db.ApprovalSteps.AsNoTracking().Where(s => s.InstanceId == instanceId).ToListAsync(cancellationToken);
            detail.Approval = ApprovalMapper.ToDto(instance, steps, reference, contract.Title);
            detail.SubmittedById = instance.StartedById;
        }

        // Reminders
        var settings = await ReminderSettingsReader.GetAsync(_db, cancellationToken);
        var noticeDays = settings.GetNoticeDays();
        var reminders = await _db.Reminders.AsNoTracking().Where(r => r.ContractId == id).ToListAsync(cancellationToken);
        detail.Reminders = ContractCalculations.ReminderSchedule(contract, noticeDays, settings.EscalationDays, reminders)
            .Select(r => new ReminderDto { Kind = r.Kind, DueDate = r.DueDate, SentAt = r.SentAt })
            .ToList();
        var sentKinds = new HashSet<string>(reminders.Where(r => r.ObligationId is null).Select(r => r.Kind));
        detail.NextReminderDate = ContractCalculations.NextReminderDate(contract, today, noticeDays, sentKinds);

        // Activity
        var events = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.ContractId == id && e.Action != AuditService.ContractViewed)
            .OrderByDescending(e => e.At)
            .Take(40)
            .ToListAsync(cancellationToken);
        detail.Activity = events.Select(e => new ActivityDto { At = e.At, Who = e.UserName, Text = e.Text }).ToList();

        // Who can see this contract
        detail.Access.Add(reference.Department(contract.DepartmentId) + " department");
        detail.Access.Add(reference.Entity(contract.EntityId) + " management");
        detail.Access.Add("Group management");
        detail.Access.Add("Owner, delegate and responsible");
        detail.Access.Add("Administrators");
        if (instance is not null && instance.Status == ApprovalStatus.Pending)
        {
            detail.Access.Add("Current approvers");
        }

        detail.Deviations = JsonDefaults.ReadList<Deviation>(contract.DeviationsJson);
        detail.CanEdit = ContractAccessRules.CanEdit(profile, contract) && contract.Status != ContractStatus.InApproval;
        detail.CanDecideRenewal = ContractAccessRules.CanDecideRenewal(profile, contract);
        detail.LockedFields = ApprovedTerms.LockedFieldsFor(profile, contract);

        try
        {
            var location = await _documents.GetLocationAsync(contract, cancellationToken);
            detail.StoreUrl = await _store.GetFolderUrlAsync(location, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A store outage must not block reading the contract record.
            detail.StoreUrl = null;
        }

        return detail;
    }

    private static void AddPerson(ContractDetailDto detail, string role, Guid? userId, Guid recipientId, ReferenceData reference)
    {
        if (userId is null)
        {
            return;
        }
        detail.People.Add(new PersonDto
        {
            Role = role,
            UserId = userId.Value,
            Name = reference.UserName(userId),
            Initials = reference.UserInitials(userId),
            GetsReminders = userId.Value == recipientId
        });
    }

    public static ObligationDto ToObligationDto(Obligation obligation, ReferenceData reference)
    {
        return new ObligationDto
        {
            Id = obligation.Id,
            Description = obligation.Description,
            DueDate = obligation.DueDate,
            FrequencyMonths = obligation.FrequencyMonths,
            ResponsibleId = obligation.ResponsibleId,
            ResponsibleName = reference.UserName(obligation.ResponsibleId),
            Status = obligation.Status.ToString()
        };
    }

    // ---------- approvals waiting for me ----------

    public async Task<List<MyApprovalDto>> MyApprovalsAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var userId = profile.UserId;
        var mySteps = await _db.ApprovalSteps.AsNoTracking()
            .Where(s => s.ApproverId == userId && s.Status == ApprovalStepStatus.Pending)
            .ToListAsync(cancellationToken);
        var result = new List<MyApprovalDto>();
        if (mySteps.Count == 0)
        {
            return result;
        }

        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var now = _clock.UtcNow;
        foreach (var step in mySteps.OrderBy(s => s.ActivatedAt ?? now))
        {
            var instanceId = step.InstanceId;
            var instance = await _db.ApprovalInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
            if (instance is null || instance.Status != ApprovalStatus.Pending)
            {
                continue;
            }
            var contractId = step.ContractId;
            var contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken);
            if (contract is null)
            {
                continue;
            }
            var detail = await BuildDetailAsync(contract, profile, reference, cancellationToken);
            result.Add(new MyApprovalDto
            {
                Instance = detail.Approval ?? new ApprovalInstanceDto { Id = instance.Id, ContractId = contract.Id, ContractTitle = contract.Title, Status = instance.Status.ToString(), CurrentStage = instance.CurrentStage },
                StepId = step.Id,
                WaitingDays = step.ActivatedAt is null ? 0 : Math.Max(0, (int)(now - step.ActivatedAt.Value).TotalDays),
                Contract = detail
            });
        }
        return result;
    }

    // ---------- renewals ----------

    public async Task<List<RenewalDto>> RenewalsAsync(int? months, CancellationToken cancellationToken = default)
    {
        var horizonMonths = Math.Clamp(months ?? 12, 1, 60);
        var today = _clock.Today;
        var horizon = today.AddMonths(horizonMonths);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var contracts = await LoadVisibleAsync(cancellationToken);
        var result = new List<RenewalDto>();
        foreach (var contract in contracts.Where(c => c.Status == ContractStatus.Active))
        {
            var deadline = ContractCalculations.NoticeDeadline(contract);
            if (deadline is null || deadline.Value > horizon)
            {
                continue;
            }
            if (deadline.Value < today)
            {
                // Past deadlines stay on the timeline only while a decision is still missing.
                var stillRunning = contract.EndDate is not null && contract.EndDate.Value >= today;
                if (!stillRunning || contract.RenewalDecision is not null)
                {
                    continue;
                }
            }
            result.Add(new RenewalDto
            {
                Id = contract.Id,
                Title = contract.Title,
                EntityId = contract.EntityId,
                EntityName = reference.Entity(contract.EntityId),
                DepartmentName = reference.Department(contract.DepartmentId),
                NoticeDeadline = deadline.Value,
                DaysToNotice = deadline.Value.DayNumber - today.DayNumber,
                AutoRenew = contract.AutoRenew,
                AnnualValue = contract.AnnualValue,
                Currency = contract.Currency
            });
        }
        return result.OrderBy(r => r.NoticeDeadline).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- overview (management) ----------

    public async Task<OverviewDto> OverviewAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (!ContractAccessRules.CanSeeOverview(profile))
        {
            throw new ForbiddenException("The overview is for entity management, group management and administrators.");
        }
        var today = _clock.Today;
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var contracts = await LoadVisibleAsync(cancellationToken);
        var live = contracts.Where(IsLive).ToList();

        var overview = new OverviewDto
        {
            ActiveContracts = live.Count,
            AnnualValueDkk = live.Sum(c => CurrencyRates.ToDkk(c.AnnualValue, c.Currency)),
            DueIn90 = live.Count(c => IsDueWithin(c, today, 90)),
            InApproval = contracts.Count(c => c.Status == ContractStatus.InApproval),
            MissingData = contracts.Count(c =>
                c.Status != ContractStatus.Expired && c.Status != ContractStatus.Terminated
                && ContractCalculations.MissingData(c).Count > 0)
        };

        overview.ValueByEntity = live
            .GroupBy(c => c.EntityId)
            .Select(g => new NameValueDto { Name = reference.Entity(g.Key), ValueDkk = g.Sum(c => CurrencyRates.ToDkk(c.AnnualValue, c.Currency)) })
            .OrderByDescending(v => v.ValueDkk)
            .ToList();
        overview.CountByType = live
            .GroupBy(c => c.TypeId)
            .Select(g => new NameCountDto { Name = reference.Type(g.Key), Count = g.Count() })
            .OrderByDescending(v => v.Count)
            .ThenBy(v => v.Name, StringComparer.Ordinal)
            .ToList();
        overview.ChangeOfControl = live
            .Where(c => c.ChangeOfControl == true)
            .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ChangeOfControlDto { Id = c.Id, Title = c.Title, EntityName = reference.Entity(c.EntityId) })
            .ToList();
        overview.ExpiringNoRenewal = live
            .Where(c => !c.AutoRenew && c.RenewalDecision is null && c.EndDate is not null
                && c.EndDate.Value >= today && c.EndDate.Value <= today.AddDays(180))
            .OrderBy(c => c.EndDate)
            .Select(c => ToListItem(c, reference, today))
            .ToList();
        return overview;
    }

    private static bool IsDueWithin(Contract contract, DateOnly today, int days)
    {
        if (contract.Status != ContractStatus.Active || contract.RenewalDecision is not null)
        {
            return false;
        }
        var daysToNotice = ContractCalculations.DaysToNotice(contract, today);
        return daysToNotice is not null && daysToNotice.Value >= 0 && daysToNotice.Value <= days;
    }

    // ---------- home ----------

    public async Task<HomeDto> HomeAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var userId = profile.UserId;
        var today = _clock.Today;
        var culture = CultureInfo.InvariantCulture;
        var contracts = await LoadVisibleAsync(cancellationToken);
        var byId = contracts.ToDictionary(c => c.Id, c => c);
        var mine = contracts.Where(c => ContractAccessRules.IsOnContract(profile, c)).ToList();
        var home = new HomeDto();

        home.MyContracts = mine.Count(c => c.Status != ContractStatus.Expired && c.Status != ContractStatus.Terminated);
        home.DecisionsThisWeek = mine.Count(c => IsDueWithin(c, today, 7));
        home.RenewalsThisWeek = mine.Count(c =>
            IsLive(c) && c.EndDate is not null && c.EndDate.Value >= today && c.EndDate.Value <= today.AddDays(7));

        var due90 = contracts.Where(c => IsDueWithin(c, today, 90)).ToList();
        home.RenewingIn90 = new RenewingDto
        {
            Count = due90.Count,
            ValueDkk = due90.Sum(c => CurrencyRates.ToDkk(c.AnnualValue, c.Currency))
        };

        var tasks = new List<(int Rank, HomeTaskDto Dto)>();

        // Approvals waiting for me
        var mySteps = await _db.ApprovalSteps.AsNoTracking()
            .Where(s => s.ApproverId == userId && s.Status == ApprovalStepStatus.Pending)
            .ToListAsync(cancellationToken);
        home.ApprovalsWaiting = mySteps.Count;
        var now = _clock.UtcNow;
        foreach (var step in mySteps)
        {
            if (!byId.TryGetValue(step.ContractId, out var contract))
            {
                continue;
            }
            var waiting = step.ActivatedAt is null ? 0 : Math.Max(0, (int)(now - step.ActivatedAt.Value).TotalDays);
            tasks.Add((waiting >= 3 ? 5 : 20, new HomeTaskDto
            {
                Kind = "Approval",
                Title = "Approve: " + contract.Title,
                Meta = contract.CounterpartyName + " · " + step.RoleLabel,
                Pill = waiting == 0 ? "New" : "Waiting " + waiting + (waiting == 1 ? " day" : " days"),
                Urgency = waiting >= 3 ? "high" : "medium",
                ContractId = contract.Id,
                ApprovalStepId = step.Id
            }));
        }

        // Renewal decisions on my contracts
        foreach (var contract in mine.Where(c => c.Status == ContractStatus.Active && c.RenewalDecision is null))
        {
            var deadline = ContractCalculations.NoticeDeadline(contract);
            if (deadline is null || contract.EndDate is null || contract.EndDate.Value < today)
            {
                continue;
            }
            var days = deadline.Value.DayNumber - today.DayNumber;
            if (days > 120)
            {
                continue;
            }
            string pill;
            string urgency;
            if (days < 0) { pill = "Overdue"; urgency = "high"; }
            else if (days <= 14) { pill = days == 0 ? "Today" : days + (days == 1 ? " day" : " days"); urgency = "high"; }
            else if (days <= 60) { pill = days + " days"; urgency = "medium"; }
            else { pill = days + " days"; urgency = "low"; }
            tasks.Add((days, new HomeTaskDto
            {
                Kind = "Renewal",
                Title = "Decide renewal: " + contract.Title,
                Meta = contract.CounterpartyName + " · notice deadline " + deadline.Value.ToString("d MMM yyyy", culture),
                Pill = pill,
                Urgency = urgency,
                ContractId = contract.Id
            }));
        }

        // My obligations
        var myObligations = await _db.Obligations.AsNoTracking()
            .Where(o => o.ResponsibleId == userId && o.Status == ObligationStatus.Open)
            .ToListAsync(cancellationToken);
        foreach (var obligation in myObligations)
        {
            if (obligation.DueDate is null || !byId.TryGetValue(obligation.ContractId, out var contract))
            {
                continue;
            }
            var days = obligation.DueDate.Value.DayNumber - today.DayNumber;
            if (days > 30)
            {
                continue;
            }
            tasks.Add((days, new HomeTaskDto
            {
                Kind = "Obligation",
                Title = obligation.Description,
                Meta = contract.Title + " · due " + obligation.DueDate.Value.ToString("d MMM yyyy", culture),
                Pill = days < 0 ? "Overdue" : days == 0 ? "Today" : days + (days == 1 ? " day" : " days"),
                Urgency = days <= 7 ? "high" : "medium",
                ContractId = contract.Id,
                ObligationId = obligation.Id
            }));
        }

        // Missing data on my contracts
        foreach (var contract in mine.Where(c => c.Status != ContractStatus.Expired && c.Status != ContractStatus.Terminated))
        {
            var missing = ContractCalculations.MissingData(contract);
            if (missing.Count == 0)
            {
                continue;
            }
            tasks.Add((1000, new HomeTaskDto
            {
                Kind = "MissingData",
                Title = "Complete data: " + contract.Title,
                Meta = "Missing " + string.Join(", ", missing),
                Pill = missing.Count + (missing.Count == 1 ? " field" : " fields"),
                Urgency = "info",
                ContractId = contract.Id
            }));
        }

        home.Tasks = tasks.OrderBy(t => t.Rank).ThenBy(t => t.Dto.Title, StringComparer.OrdinalIgnoreCase).Select(t => t.Dto).Take(50).ToList();

        // Notice deadlines per month, twelve months from the current one
        var firstMonth = new DateOnly(today.Year, today.Month, 1);
        for (var i = 0; i < 12; i++)
        {
            var month = firstMonth.AddMonths(i);
            var count = contracts.Count(c =>
            {
                if (c.Status != ContractStatus.Active) return false;
                var deadline = ContractCalculations.NoticeDeadline(c);
                return deadline is not null && deadline.Value.Year == month.Year && deadline.Value.Month == month.Month;
            });
            home.NoticeByMonth.Add(new MonthCountDto { Month = month.ToString("yyyy-MM", culture), Count = count });
        }

        // Recently opened by me, then recently updated
        var views = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.UserId == userId && e.Action == AuditService.ContractViewed)
            .OrderByDescending(e => e.At)
            .Take(40)
            .ToListAsync(cancellationToken);
        foreach (var view in views)
        {
            if (view.ContractId is null || home.Recent.Count >= 5)
            {
                continue;
            }
            if (byId.TryGetValue(view.ContractId.Value, out var contract) && !home.Recent.Any(r => r.Id == contract.Id))
            {
                home.Recent.Add(new RecentDto { Id = contract.Id, Title = contract.Title });
            }
        }
        foreach (var contract in mine.OrderByDescending(c => c.UpdatedAt))
        {
            if (home.Recent.Count >= 5)
            {
                break;
            }
            if (!home.Recent.Any(r => r.Id == contract.Id))
            {
                home.Recent.Add(new RecentDto { Id = contract.Id, Title = contract.Title });
            }
        }

        return home;
    }

    // ---------- search, suppliers, lookups, me ----------

    public async Task<List<SearchResultDto>> SearchAsync(string? q, CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResultDto>();
        var term = (q ?? "").Trim();
        if (term.Length < 2)
        {
            return results;
        }
        var contracts = await LoadVisibleAsync(cancellationToken);
        foreach (var contract in contracts
            .Where(c => ContainsText(c.Title, term) || ContainsText(c.ContractNo, term) || ContainsText(c.CounterpartyName, term))
            .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .Take(6))
        {
            results.Add(new SearchResultDto
            {
                Kind = "Contract",
                Id = contract.Id,
                Title = contract.Title,
                Subtitle = contract.ContractNo + " · " + contract.CounterpartyName
            });
        }
        var suppliers = await SuppliersAsync(term, cancellationToken);
        foreach (var supplier in suppliers.Take(Math.Max(2, 8 - results.Count)))
        {
            if (results.Count >= 8)
            {
                break;
            }
            results.Add(new SearchResultDto
            {
                Kind = "Supplier",
                Id = supplier.Id,
                Title = supplier.Name,
                Subtitle = supplier.Country ?? ""
            });
        }
        return results;
    }

    public async Task<List<SupplierDto>> SuppliersAsync(string? q, CancellationToken cancellationToken = default)
    {
        // Known, active users only (M10).
        await _currentUser.GetProfileAsync(cancellationToken);
        var term = (q ?? "").Trim();
        var suppliers = await _db.Suppliers.AsNoTracking().Where(s => s.IsActive).ToListAsync(cancellationToken);
        return suppliers
            .Where(s => term.Length == 0 || ContainsText(s.Name, term) || ContainsText(s.VatNo, term) || ContainsText(s.ExternalId, term))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(term.Length == 0 ? 200 : 25)
            .Select(s => new SupplierDto { Id = s.Id, Name = s.Name, Country = s.Country, VatNo = s.VatNo })
            .ToList();
    }

    public async Task<LookupsDto> LookupsAsync(CancellationToken cancellationToken = default)
    {
        await _currentUser.GetProfileAsync(cancellationToken);
        var entities = await _db.LegalEntities.AsNoTracking().ToListAsync(cancellationToken);
        var departments = await _db.Departments.AsNoTracking().Where(d => d.IsActive).ToListAsync(cancellationToken);
        var types = await _db.ContractTypes.AsNoTracking().Where(t => t.IsActive).ToListAsync(cancellationToken);
        var users = await _db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(cancellationToken);
        var userDepartments = await _db.UserDepartments.AsNoTracking().ToListAsync(cancellationToken);
        var departmentsByUser = userDepartments.GroupBy(d => d.UserId).ToDictionary(g => g.Key, g => g.Select(d => d.DepartmentId).ToList());

        return new LookupsDto
        {
            Entities = entities.OrderBy(e => e.SortOrder).ThenBy(e => e.Name, StringComparer.Ordinal).Select(e => new IdNameDto(e.Id, e.Name)).ToList(),
            Departments = departments.OrderBy(d => d.SortOrder).ThenBy(d => d.Name, StringComparer.Ordinal).Select(d => new IdNameDto(d.Id, d.Name)).ToList(),
            ContractTypes = types.OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.Ordinal).Select(t => new IdNameDto(t.Id, t.Name)).ToList(),
            Users = users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Select(u => new LookupUserDto
            {
                Id = u.Id,
                Name = u.Name,
                Initials = u.Initials,
                DepartmentIds = departmentsByUser.TryGetValue(u.Id, out var ids) ? ids : new List<Guid>(),
                Roles = u.Roles.ToNames()
            }).ToList(),
            Currencies = CurrencyRates.Currencies.ToList()
        };
    }

    /// <summary>Read-only reminder settings for every signed-in user.</summary>
    public async Task<ReminderSettingsDto> ReminderSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _currentUser.GetProfileAsync(cancellationToken);
        var settings = await ReminderSettingsReader.GetAsync(_db, cancellationToken);
        return new ReminderSettingsDto
        {
            NoticeDays = settings.GetNoticeDays(),
            EscalationDays = settings.EscalationDays,
            ObligationDays = settings.ObligationDays
        };
    }

    public async Task<MeDto> MeAsync(string authMode, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        return new MeDto
        {
            Id = profile.UserId,
            Name = profile.Name,
            Email = profile.Email,
            Initials = profile.Initials,
            Roles = profile.Roles.ToNames(),
            DepartmentIds = profile.DepartmentIds.ToList(),
            ManagedEntityIds = profile.ManagedEntityIds.ToList(),
            IsGroupManagement = profile.IsGroupManagement,
            AuthMode = authMode
        };
    }
}

public static class ReminderSettingsReader
{
    /// <summary>The stored settings, or the defaults (120/60/30, escalate at 14, obligations 14) when none exist.</summary>
    public static async Task<ReminderSettings> GetAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        var settings = await db.ReminderSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return settings ?? new ReminderSettings();
    }
}
