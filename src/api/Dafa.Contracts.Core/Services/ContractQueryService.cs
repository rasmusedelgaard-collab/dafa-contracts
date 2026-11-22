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

    /// <summary>The release 3 filter with the same meaning.</summary>
    public ContractFilter ToFilter()
    {
        var filter = new ContractFilter { View = View, Q = Q, Sort = Sort, Page = Page, PageSize = PageSize };
        if (EntityId is not null) filter.EntityIds.Add(EntityId.Value);
        if (DepartmentId is not null) filter.DepartmentIds.Add(DepartmentId.Value);
        if (TypeId is not null) filter.TypeIds.Add(TypeId.Value);
        if (!string.IsNullOrWhiteSpace(Status)) filter.Statuses.Add(Status);
        return filter;
    }
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

    private Task<List<Contract>> LoadVisibleAsync(CancellationToken cancellationToken) => LoadVisibleAsync(null, cancellationToken);

    /// <summary>
    /// Release 4: the visible contracts, optionally narrowed to the entity scope (repeated entityId).
    /// The scope is applied AFTER the access filter; an empty scope means every entity the user can
    /// see, and an id the user cannot see simply matches nothing.
    /// </summary>
    private async Task<List<Contract>> LoadVisibleAsync(IReadOnlyCollection<Guid>? entityIds, CancellationToken cancellationToken)
    {
        var query = await _access.FilterAsync(_db.Contracts.AsNoTracking(), cancellationToken);
        if (entityIds is not null && entityIds.Count > 0)
        {
            var ids = entityIds.Distinct().ToList();
            query = query.Where(c => ids.Contains(c.EntityId));
        }
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

    public static void FillListItem(ContractListItemDto item, Contract contract, ReferenceData reference, DateOnly today)
    {
        var deadline = ContractCalculations.ListNoticeDeadline(contract);
        // Release 3 fields
        item.EntityId = contract.EntityId;
        item.DepartmentId = contract.DepartmentId;
        item.TypeId = contract.TypeId;
        item.OwnerId = contract.OwnerId;
        item.DelegateName = contract.DelegateId is null ? null : reference.UserName(contract.DelegateId);
        item.ResponsibleName = contract.ResponsibleId is null ? null : reference.UserName(contract.ResponsibleId);
        item.CounterpartyId = contract.SupplierId;
        item.StartDate = contract.StartDate;
        item.EndDate = contract.EndDate;
        item.TotalValue = contract.TotalValue;
        item.AnnualValueDkk = reference.AnnualDkk(contract);
        item.TotalValueDkk = reference.TotalDkk(contract);
        item.AutoRenew = contract.AutoRenew;
        item.ChangeOfControl = contract.ChangeOfControl;
        item.Exclusivity = contract.Exclusivity;
        item.CreatedAt = DateOnly.FromDateTime(contract.CreatedAt);
        item.SignedDate = contract.SignedDate;
        item.HasSignedDocument = reference.HasSignedDocument(contract);
        item.Tags = reference.TagsFor(contract.Id);
        item.Watching = reference.IsWatched(contract.Id);
        item.CustomFields = reference.CustomValuesFor(contract.Id);
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
        item.MissingData = reference.Missing(contract);
    }

    private static bool ContainsText(string? value, string term) =>
        !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    // ---------- list / export ----------

    private ContractFilterEngine Engine() => new(_db, _access, _currentUser, _clock);

    public Task<PagedDto<ContractListItemDto>> ListAsync(ContractListQuery query, CancellationToken cancellationToken = default) =>
        ListAsync(query.ToFilter(), cancellationToken);

    /// <summary>GET contracts: the shared filter (CONTRACT_V3 section 2), sorted and paged; ended contracts included by default.</summary>
    public async Task<PagedDto<ContractListItemDto>> ListAsync(ContractFilter filter, CancellationToken cancellationToken = default)
    {
        var result = await Engine().RunAsync(filter, true, cancellationToken);
        var all = result.Items;
        var page = Math.Max(1, filter.Page ?? 1);
        var pageSize = Math.Clamp(filter.PageSize ?? 25, 1, 200);
        var today = _clock.Today;
        var pageItems = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        await result.Reference.LoadExtrasAsync(_db, pageItems.Select(c => c.Id).ToList(), result.Profile.UserId, cancellationToken);
        return new PagedDto<ContractListItemDto>
        {
            Items = pageItems.Select(c => ToListItem(c, result.Reference, today)).ToList(),
            Total = all.Count,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>List items for given contracts (already access-checked by the caller), with tags, custom fields and watches.</summary>
    public async Task<List<ContractListItemDto>> ToListItemsAsync(IReadOnlyList<Contract> contracts, ReferenceData reference, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        await reference.LoadExtrasAsync(_db, contracts.Select(c => c.Id).ToList(), profile.UserId, cancellationToken);
        var today = _clock.Today;
        return contracts.Select(c => ToListItem(c, reference, today)).ToList();
    }

    public Task<byte[]> ExportCsvAsync(ContractListQuery query, CancellationToken cancellationToken = default) =>
        ExportCsvAsync(query.ToFilter(), cancellationToken);

    /// <summary>Register column keys usable in the export (CONTRACT_V3 REGISTER_COLUMNS) and their headers.</summary>
    public static readonly (string Key, string Header)[] ExportColumns =
    {
        ("contract", "Title"), ("contractNo", "Contract no"), ("counterparty", "Counterparty"), ("type", "Type"),
        ("entity", "Entity"), ("department", "Department"), ("owner", "Owner"), ("delegate", "Delegate"),
        ("responsible", "Responsible"), ("notice", "Notice deadline"), ("start", "Start date"), ("end", "End date"),
        ("annualValue", "Annual value"), ("totalValue", "Total value"), ("status", "Status"), ("tags", "Tags"),
        ("autoRenew", "Auto-renew"), ("changeOfControl", "Change of control")
    };

    public async Task<byte[]> ExportCsvAsync(ContractFilter filter, CancellationToken cancellationToken = default)
    {
        var result = await Engine().RunAsync(filter, true, cancellationToken);
        var reference = result.Reference;
        var all = result.Items;
        var today = _clock.Today;
        var csv = new StringBuilder();
        if (filter.Columns.Count > 0)
        {
            var columns = ValidateColumns(filter.Columns, reference);
            await reference.LoadExtrasAsync(_db, all.Select(c => c.Id).ToList(), result.Profile.UserId, cancellationToken);
            csv.AppendLine(string.Join(",", columns.Select(c => CsvCell(ColumnHeader(c, reference)))));
            foreach (var contract in all)
            {
                csv.AppendLine(string.Join(",", columns.Select(c => CsvCell(ColumnValue(c, contract, reference)))));
            }
            _audit.Record("contracts.exported", "Exported " + all.Count + " contracts to CSV");
            await _db.SaveChangesAsync(cancellationToken);
            return WithBom(csv.ToString());
        }
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
                string.Join("; ", reference.Missing(contract))
            };
            csv.AppendLine(string.Join(",", cells.Select(CsvCell)));
        }
        _audit.Record("contracts.exported", "Exported " + all.Count + " contracts to CSV");
        await _db.SaveChangesAsync(cancellationToken);
        return WithBom(csv.ToString());
    }

    public static byte[] WithBom(string text)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(text);
        var bytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
        return bytes;
    }

    /// <summary>400 for an unknown column key; cf.&lt;key&gt; must be an active custom field.</summary>
    public static List<string> ValidateColumns(IEnumerable<string> columns, ReferenceData reference)
    {
        var result = new List<string>();
        foreach (var column in columns)
        {
            var known = ExportColumns.Any(c => c.Key == column)
                || (column.StartsWith("cf.", StringComparison.Ordinal) && reference.CustomFields.Any(d => d.Key == column.Substring(3)));
            if (!known)
            {
                throw new InputValidationException("columns", "Unknown column '" + column + "'.");
            }
            if (!result.Contains(column))
            {
                result.Add(column);
            }
        }
        return result;
    }

    public static string ColumnHeader(string column, ReferenceData reference)
    {
        if (column.StartsWith("cf.", StringComparison.Ordinal))
        {
            var definition = reference.CustomFields.FirstOrDefault(d => d.Key == column.Substring(3));
            return definition?.Label ?? column;
        }
        return ExportColumns.Where(c => c.Key == column).Select(c => c.Header).FirstOrDefault() ?? column;
    }

    public static string ColumnValue(string column, Contract contract, ReferenceData reference)
    {
        var culture = CultureInfo.InvariantCulture;
        switch (column)
        {
            case "contract": return contract.Title;
            case "contractNo": return contract.ContractNo;
            case "counterparty": return contract.CounterpartyName;
            case "type": return reference.Type(contract.TypeId);
            case "entity": return reference.Entity(contract.EntityId);
            case "department": return reference.Department(contract.DepartmentId);
            case "owner": return reference.UserName(contract.OwnerId);
            case "delegate": return reference.UserName(contract.DelegateId);
            case "responsible": return reference.UserName(contract.ResponsibleId);
            case "notice": return FormatDate(ContractCalculations.ListNoticeDeadline(contract));
            case "start": return FormatDate(contract.StartDate);
            case "end": return FormatDate(contract.EndDate);
            case "annualValue": return contract.AnnualValue?.ToString("0.##", culture) ?? "";
            case "totalValue": return contract.TotalValue?.ToString("0.##", culture) ?? "";
            case "status": return contract.Status.ToString();
            case "tags": return string.Join("; ", reference.TagsFor(contract.Id).Select(t => t.Name));
            case "autoRenew": return contract.AutoRenew ? "Yes" : "No";
            case "changeOfControl": return contract.ChangeOfControl is null ? "" : (contract.ChangeOfControl.Value ? "Yes" : "No");
        }
        if (column.StartsWith("cf.", StringComparison.Ordinal))
        {
            var key = column.Substring(3);
            var values = reference.CustomValuesFor(contract.Id);
            if (values.TryGetValue(key, out var value) && value is not null)
            {
                return CustomFieldService.DisplayRaw(value, reference);
            }
        }
        return "";
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
        await reference.LoadExtrasAsync(_db, new[] { contract.Id }, profile.UserId, cancellationToken);
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

        // Reminders (release 3: the contract type's own schedule when it has one)
        var settings = await ReminderSettingsReader.GetForTypeAsync(_db, contract.TypeId, cancellationToken);
        var noticeDays = settings.GetNoticeDays();
        var reminders = await _db.Reminders.AsNoTracking().Where(r => r.ContractId == id).ToListAsync(cancellationToken);
        detail.Reminders = ContractCalculations.ReminderSchedule(contract, noticeDays, settings.EscalationDays, reminders)
            .Select(r => new ReminderDto { Kind = r.Kind, DueDate = r.DueDate, SentAt = r.SentAt })
            .ToList();
        var sentKinds = new HashSet<string>(reminders.Where(r => r.ObligationId is null).Select(r => r.Kind));
        detail.NextReminderDate = ContractCalculations.NextReminderDate(contract, today, noticeDays, sentKinds);

        // Activity
        var events = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.ContractId == id && e.Action != AuditService.ContractViewed && e.Action != "contract.watch")
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
        // Release 3
        detail.WatcherCount = await _db.ContractWatchers.AsNoTracking().CountAsync(w => w.ContractId == id, cancellationToken);
        detail.CommentCount = await _db.ContractComments.AsNoTracking().CountAsync(c => c.ContractId == id, cancellationToken);
        detail.CanTag = ContractAccessRules.CanEdit(profile, contract);
        detail.CanDecideRenewal = ContractAccessRules.CanDecideRenewal(profile, contract);
        detail.LockedFields = ApprovedTerms.LockedFieldsFor(profile, contract);
        // Release 4: e-signature. The open request (else the last finished one), and whether the
        // caller may send this contract for signature right now.
        detail.Signature = await SignatureMapper.LatestAsync(_db, id, cancellationToken);
        var eSign = await ESignSettingsStore.LoadAsync(_db, cancellationToken);
        detail.CanSendForSignature = SignatureRules.SendBlocker(profile, contract, eSign, reference.Entity(contract.EntityId)) is null
            && (detail.Signature is null || (detail.Signature.Status != "Sent" && detail.Signature.Status != "Draft"));

        // SharePoint hardening: no call to the store when a contract is read. The folder link was
        // stored on the contract when its folder was created, and only administrators get it
        // (everyone else opens files through the app; users have no direct access to the libraries).
        detail.StoreUrl = profile.IsAdmin && !string.IsNullOrWhiteSpace(contract.StoreFolderUrl) ? contract.StoreFolderUrl : null;

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

    public Task<List<MyApprovalDto>> MyApprovalsAsync(CancellationToken cancellationToken = default) => MyApprovalsAsync(null, cancellationToken);

    /// <summary>Release 4: entityIds = the entity scope (the contract's entity); empty = all.</summary>
    public async Task<List<MyApprovalDto>> MyApprovalsAsync(IReadOnlyCollection<Guid>? entityIds, CancellationToken cancellationToken = default)
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
            if (entityIds is not null && entityIds.Count > 0 && !entityIds.Contains(contract.EntityId))
            {
                continue;
            }
            await reference.LoadExtrasAsync(_db, new[] { contract.Id }, profile.UserId, cancellationToken);
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

    public Task<List<RenewalDto>> RenewalsAsync(int? months, CancellationToken cancellationToken = default) => RenewalsAsync(months, null, cancellationToken);

    public async Task<List<RenewalDto>> RenewalsAsync(int? months, IReadOnlyCollection<Guid>? entityIds, CancellationToken cancellationToken = default)
    {
        var horizonMonths = Math.Clamp(months ?? 12, 1, 60);
        var today = _clock.Today;
        var horizon = today.AddMonths(horizonMonths);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var contracts = await LoadVisibleAsync(entityIds, cancellationToken);
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

    public Task<OverviewDto> OverviewAsync(CancellationToken cancellationToken = default) => OverviewAsync(null, cancellationToken);

    public async Task<OverviewDto> OverviewAsync(IReadOnlyCollection<Guid>? entityIds, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        // Release 3: every signed-in user; the figures cover only the contracts the user can see.
        var today = _clock.Today;
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var contracts = await LoadVisibleAsync(entityIds, cancellationToken);
        var live = contracts.Where(IsLive).ToList();

        var overview = new OverviewDto
        {
            ActiveContracts = live.Count,
            AnnualValueDkk = live.Sum(c => reference.AnnualDkk(c)),
            DueIn90 = live.Count(c => IsDueWithin(c, today, 90)),
            InApproval = contracts.Count(c => c.Status == ContractStatus.InApproval),
            MissingData = contracts.Count(c =>
                c.Status != ContractStatus.Expired && c.Status != ContractStatus.Terminated
                && reference.Missing(c).Count > 0)
        };

        overview.ValueByEntity = live
            .GroupBy(c => c.EntityId)
            .Select(g => new NameValueDto { Name = reference.Entity(g.Key), ValueDkk = g.Sum(c => reference.AnnualDkk(c)) })
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

    public Task<HomeDto> HomeAsync(CancellationToken cancellationToken = default) => HomeAsync(null, cancellationToken);

    /// <summary>
    /// Release 4: entityIds = the entity scope. It narrows tasks, KPIs, the month chart and the
    /// recent list; approvalsWaiting (the navigation badge) stays the unscoped number of open steps.
    /// </summary>
    public async Task<HomeDto> HomeAsync(IReadOnlyCollection<Guid>? entityIds, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var userId = profile.UserId;
        var today = _clock.Today;
        var culture = CultureInfo.InvariantCulture;
        var contracts = await LoadVisibleAsync(entityIds, cancellationToken);
        var byId = contracts.ToDictionary(c => c.Id, c => c);
        var mine = contracts.Where(c => ContractAccessRules.IsOnContract(profile, c)).ToList();
        var home = new HomeDto();
        var homeReference = await ReferenceData.LoadAsync(_db, cancellationToken);

        home.MyContracts = mine.Count(c => c.Status != ContractStatus.Expired && c.Status != ContractStatus.Terminated);
        home.DecisionsThisWeek = mine.Count(c => IsDueWithin(c, today, 7));
        home.RenewalsThisWeek = mine.Count(c =>
            IsLive(c) && c.EndDate is not null && c.EndDate.Value >= today && c.EndDate.Value <= today.AddDays(7));

        var due90 = contracts.Where(c => IsDueWithin(c, today, 90)).ToList();
        home.RenewingIn90 = new RenewingDto
        {
            Count = due90.Count,
            ValueDkk = due90.Sum(c => homeReference.AnnualDkk(c))
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
            var missing = homeReference.Missing(contract);
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
        // Release 3: inactive rows are included so names still resolve; pickers hide them.
        var entities = await _db.LegalEntities.AsNoTracking().ToListAsync(cancellationToken);
        var departments = await _db.Departments.AsNoTracking().ToListAsync(cancellationToken);
        var types = await _db.ContractTypes.AsNoTracking().ToListAsync(cancellationToken);
        var users = await _db.Users.AsNoTracking().ToListAsync(cancellationToken);
        var userDepartments = await _db.UserDepartments.AsNoTracking().ToListAsync(cancellationToken);
        var departmentsByUser = userDepartments.GroupBy(d => d.UserId).ToDictionary(g => g.Key, g => g.Select(d => d.DepartmentId).ToList());

        return new LookupsDto
        {
            Entities = entities.OrderBy(e => e.SortOrder).ThenBy(e => e.Name, StringComparer.Ordinal)
                .Select(e => new LookupEntityDto { Id = e.Id, Name = e.Name, Country = e.Country, Currency = e.Currency, IsActive = e.IsActive }).ToList(),
            Departments = departments.OrderBy(d => d.SortOrder).ThenBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => new NamedRefV3Dto { Id = d.Id, Name = d.Name, IsActive = d.IsActive }).ToList(),
            ContractTypes = types.OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => new NamedRefV3Dto { Id = t.Id, Name = t.Name, IsActive = t.IsActive }).ToList(),
            Users = users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Select(u => new LookupUserDto
            {
                Id = u.Id,
                Name = u.Name,
                Initials = u.Initials,
                DepartmentIds = departmentsByUser.TryGetValue(u.Id, out var ids) ? ids : new List<Guid>(),
                Roles = u.Roles.ToNames(),
                IsActive = u.IsActive,
                Email = u.Email,
                Title = u.JobTitle,
                EntityId = u.HomeEntityId
            }).ToList(),
            Currencies = await CurrencyTable.ActiveCodesAsync(_db, cancellationToken)
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
        return ToMe(profile, authMode);
    }

    /// <summary>
    /// GET me and the answer of dev/login. Always built from the database rows of this request
    /// (never from the cookie), so a role or permission change by an administrator shows at once.
    /// </summary>
    public static MeDto ToMe(AccessProfile profile, string authMode) => new()
    {
        Id = profile.UserId,
        Name = profile.Name,
        Email = profile.Email,
        Initials = profile.Initials,
        Roles = profile.Roles.ToNames(),
        DepartmentIds = profile.DepartmentIds.ToList(),
        ManagedEntityIds = profile.ManagedEntityIds.ToList(),
        IsGroupManagement = profile.IsGroupManagement,
        AuthMode = authMode,
        EntityId = profile.HomeEntityId,
        CanSendForSignature = profile.CanSendForSignature,
        SignatureEntityIds = profile.SignatureEntityIds.ToList()
    };
}

public static class ReminderSettingsReader
{
    /// <summary>The stored settings, or the defaults (120/60/30, escalate at 14, obligations 14) when none exist.</summary>
    public static async Task<ReminderSettings> GetAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        var settings = await db.ReminderSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return settings ?? new ReminderSettings();
    }

    public static ReminderSettings FromSchedule(ReminderSchedule schedule) => new()
    {
        Id = schedule.Id,
        NoticeDays = schedule.NoticeDays,
        EscalationDays = schedule.EscalationDays,
        ObligationDays = schedule.ObligationDays
    };

    /// <summary>Release 3: the settings for one contract type (its own schedule, else the global settings).</summary>
    public static async Task<ReminderSettings> GetForTypeAsync(IAppDbContext db, Guid typeId, CancellationToken cancellationToken = default)
    {
        var schedule = await db.ReminderSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.ContractTypeId == typeId, cancellationToken);
        return schedule is null ? await GetAsync(db, cancellationToken) : FromSchedule(schedule);
    }

    /// <summary>Loads the global settings and every per-type schedule once; returns a lookup by contract type.</summary>
    public static async Task<Func<Guid, ReminderSettings>> GetResolverAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        var global = await GetAsync(db, cancellationToken);
        var schedules = await db.ReminderSchedules.AsNoTracking().ToListAsync(cancellationToken);
        var byType = new Dictionary<Guid, ReminderSettings>();
        foreach (var schedule in schedules)
        {
            byType[schedule.ContractTypeId] = FromSchedule(schedule);
        }
        return typeId => byType.TryGetValue(typeId, out var settings) ? settings : global;
    }
}
