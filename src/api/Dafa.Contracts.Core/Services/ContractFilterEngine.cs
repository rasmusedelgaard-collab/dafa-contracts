using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>The result of running a ContractFilter: matching visible contracts, sorted.</summary>
public sealed class FilteredContracts
{
    public FilteredContracts(List<Contract> items, ReferenceData reference, AccessProfile profile)
    {
        Items = items;
        Reference = reference;
        Profile = profile;
    }

    public List<Contract> Items { get; }
    public ReferenceData Reference { get; }
    public AccessProfile Profile { get; }
}

/// <summary>
/// Applies the shared contract filter (CONTRACT_V3 section 2) to the contracts the caller can see.
/// The access filter and the indexed criteria (ids, entity, department, type, status, counterparty,
/// owner, person, tags, watching, custom fields, flags) run in SQL; criteria on calculated values
/// (notice deadline, DKK value, missing data, text search over names) run in memory on the
/// remaining rows, which is fast at this scale (a few thousand contracts) and identical on Sqlite
/// and SQL Server (Sqlite cannot compare or sum decimals in SQL).
/// </summary>
public class ContractFilterEngine
{
    public static readonly string[] SortKeys =
    {
        "title", "entity", "owner", "notice", "status", "value",
        "contractNo", "counterparty", "type", "department", "start", "end", "totalValue", "created"
    };

    /// <summary>Release 1-2 aliases that keep working.</summary>
    private static readonly string[] LegacySortKeys =
    {
        "contractno", "counterpartyname", "typename", "entityname", "departmentname", "ownername",
        "annualvalue", "startdate", "enddate", "noticedeadline", "daystonotice", "totalvalue", "created", "createdat"
    };

    public static readonly string[] ValueBandKeys = { "lt1m", "1to5m", "5to20m", "gte20m" };
    public static readonly string[] DateFields = { "notice", "end", "start", "created", "signed" };

    private readonly IAppDbContext _db;
    private readonly IContractAccessService _access;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public ContractFilterEngine(IAppDbContext db, IContractAccessService access, ICurrentUser currentUser, IClock clock)
    {
        _db = db;
        _access = access;
        _currentUser = currentUser;
        _clock = clock;
    }

    /// <summary>400 for a sort key that is not known.</summary>
    public static void ValidateSort(string? sort)
    {
        var key = NormalizeSortKey(sort, out _);
        if (key.Length == 0)
        {
            return;
        }
        if (!SortKeys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            && !LegacySortKeys.Contains(key.ToLowerInvariant()))
        {
            throw new InputValidationException("sort", "Unknown sort field '" + key + "'.");
        }
    }

    public static string NormalizeSortKey(string? sort, out bool descending)
    {
        var key = (sort ?? "").Trim();
        descending = false;
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
        return key.Trim();
    }

    /// <param name="defaultIncludeEnded">true for the register and its export, false for analytics.</param>
    public async Task<FilteredContracts> RunAsync(ContractFilter filter, bool defaultIncludeEnded, CancellationToken cancellationToken = default)
    {
        ValidateSort(filter.Sort);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var query = await _access.FilterAsync(_db.Contracts.AsNoTracking(), cancellationToken);
        query = await ApplySqlAsync(query, filter, profile, defaultIncludeEnded, cancellationToken);
        var items = await query.ToListAsync(cancellationToken);
        var filtered = ApplyMemory(items, filter, profile, reference, _clock.Today);
        Sort(filtered, filter.Sort, reference);
        return new FilteredContracts(filtered, reference, profile);
    }

    private static List<ContractStatus> ParseStatuses(IEnumerable<string> values, out bool anyUnknown)
    {
        anyUnknown = false;
        var result = new List<ContractStatus>();
        foreach (var value in values)
        {
            foreach (var part in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<ContractStatus>(part, true, out var status) && Enum.IsDefined(typeof(ContractStatus), status)
                    && !int.TryParse(part, out _))
                {
                    if (!result.Contains(status)) result.Add(status);
                }
                else
                {
                    anyUnknown = true;
                }
            }
        }
        return result;
    }

    private async Task<IQueryable<Contract>> ApplySqlAsync(
        IQueryable<Contract> query, ContractFilter filter, AccessProfile profile, bool defaultIncludeEnded, CancellationToken cancellationToken)
    {
        if (filter.Ids.Count > 0 && filter.Ids.Count <= 500)
        {
            var ids = filter.Ids.Distinct().ToList();
            query = query.Where(c => ids.Contains(c.Id));
        }
        if (filter.EntityIds.Count > 0)
        {
            var ids = filter.EntityIds.Distinct().ToList();
            query = query.Where(c => ids.Contains(c.EntityId));
        }
        if (filter.DepartmentIds.Count > 0)
        {
            var ids = filter.DepartmentIds.Distinct().ToList();
            query = query.Where(c => ids.Contains(c.DepartmentId));
        }
        if (filter.TypeIds.Count > 0)
        {
            var ids = filter.TypeIds.Distinct().ToList();
            query = query.Where(c => ids.Contains(c.TypeId));
        }
        if (filter.CounterpartyIds.Count > 0)
        {
            var ids = filter.CounterpartyIds.Distinct().ToList();
            query = query.Where(c => c.SupplierId != null && ids.Contains(c.SupplierId.Value));
        }
        if (filter.OwnerIds.Count > 0)
        {
            var ids = filter.OwnerIds.Distinct().ToList();
            query = query.Where(c => ids.Contains(c.OwnerId));
        }
        if (filter.PersonId is not null)
        {
            var person = filter.PersonId.Value;
            query = query.Where(c => c.OwnerId == person || c.DelegateId == person || c.ResponsibleId == person);
        }

        var hasStatus = filter.Statuses.Count > 0;
        if (hasStatus)
        {
            var statuses = ParseStatuses(filter.Statuses, out var anyUnknown);
            if (statuses.Count == 0)
            {
                // Only unknown values: they match nothing.
                query = query.Where(c => false);
            }
            else
            {
                query = query.Where(c => statuses.Contains(c.Status));
            }
        }
        else if (!(filter.IncludeEnded ?? defaultIncludeEnded))
        {
            query = query.Where(c => c.Status != ContractStatus.Expired && c.Status != ContractStatus.Terminated);
        }

        if (filter.TagIds.Count > 0)
        {
            var tagIds = filter.TagIds.Distinct().ToList();
            var links = _db.ContractTags.AsNoTracking();
            query = query.Where(c => links.Any(t => t.ContractId == c.Id && tagIds.Contains(t.TagId)));
        }
        if (filter.Watching is not null)
        {
            var userId = profile.UserId;
            var watchers = _db.ContractWatchers.AsNoTracking();
            query = filter.Watching.Value
                ? query.Where(c => watchers.Any(w => w.ContractId == c.Id && w.UserId == userId))
                : query.Where(c => !watchers.Any(w => w.ContractId == c.Id && w.UserId == userId));
        }
        if (filter.ChangeOfControl is not null)
        {
            var wanted = filter.ChangeOfControl.Value;
            query = query.Where(c => c.ChangeOfControl == wanted);
        }
        if (filter.AutoRenew is not null)
        {
            var wanted = filter.AutoRenew.Value;
            query = query.Where(c => c.AutoRenew == wanted);
        }
        if (filter.Decided is not null)
        {
            query = filter.Decided.Value
                ? query.Where(c => c.RenewalDecision != null)
                : query.Where(c => c.RenewalDecision == null);
        }
        if (filter.Custom.Count > 0)
        {
            query = await ApplyCustomAsync(query, filter.Custom, cancellationToken);
        }
        return query;
    }

    /// <summary>cf.&lt;key&gt; filters with the CONTRACT_V3 semantics; an unknown key or a bad value matches nothing.</summary>
    private async Task<IQueryable<Contract>> ApplyCustomAsync(IQueryable<Contract> query, Dictionary<string, string> filters, CancellationToken cancellationToken)
    {
        var definitions = await _db.CustomFieldDefinitions.AsNoTracking().Where(d => d.IsActive).ToListAsync(cancellationToken);
        var values = _db.CustomFieldValues.AsNoTracking();
        foreach (var pair in filters)
        {
            if (pair.Value is null)
            {
                continue;
            }
            var key = pair.Key;
            var bound = "";
            if (key.EndsWith(".min", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".max", StringComparison.OrdinalIgnoreCase))
            {
                bound = key.Substring(key.Length - 3).ToLowerInvariant();
                key = key.Substring(0, key.Length - 4);
            }
            var definition = definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
            if (definition is null)
            {
                return query.Where(c => false);
            }
            var fieldId = definition.Id;
            var text = pair.Value.Trim();
            switch (definition.FieldType)
            {
                case CustomFieldType.Number:
                case CustomFieldType.Money:
                {
                    var error = CustomFieldService.ParseText(definition, text.Split(' ')[0], null, out var parsed);
                    if (error is not null || parsed.NumberValue is null)
                    {
                        return query.Where(c => false);
                    }
                    var number = parsed.NumberValue.Value;
                    if (bound == "min") query = query.Where(c => values.Any(v => v.ContractId == c.Id && v.FieldId == fieldId && v.NumberValue >= number));
                    else if (bound == "max") query = query.Where(c => values.Any(v => v.ContractId == c.Id && v.FieldId == fieldId && v.NumberValue <= number));
                    else query = query.Where(c => values.Any(v => v.ContractId == c.Id && v.FieldId == fieldId && v.NumberValue == number));
                    break;
                }
                case CustomFieldType.Date:
                {
                    if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                    {
                        return query.Where(c => false);
                    }
                    if (bound == "min") query = query.Where(c => values.Any(v => v.ContractId == c.Id && v.FieldId == fieldId && v.DateValue >= date));
                    else if (bound == "max") query = query.Where(c => values.Any(v => v.ContractId == c.Id && v.FieldId == fieldId && v.DateValue <= date));
                    else query = query.Where(c => values.Any(v => v.ContractId == c.Id && v.FieldId == fieldId && v.DateValue == date));
                    break;
                }
                case CustomFieldType.YesNo:
                {
                    bool wanted;
                    if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) wanted = true;
                    else if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) wanted = false;
                    else return query.Where(c => false);
                    query = query.Where(c => values.Any(v => v.ContractId == c.Id && v.FieldId == fieldId && v.BoolValue == wanted));
                    break;
                }
                case CustomFieldType.Person:
                {
                    if (!Guid.TryParse(text, out var userId))
                    {
                        return query.Where(c => false);
                    }
                    query = query.Where(c => values.Any(v => v.ContractId == c.Id && v.FieldId == fieldId && v.UserValue == userId));
                    break;
                }
                case CustomFieldType.Choice:
                    query = query.Where(c => values.Any(v => v.ContractId == c.Id && v.FieldId == fieldId && v.TextValue == text));
                    break;
                case CustomFieldType.MultiChoice:
                {
                    var token = "|" + text + "|";
                    query = query.Where(c => values.Any(v => v.ContractId == c.Id && v.FieldId == fieldId && v.TextValue != null && v.TextValue.Contains(token)));
                    break;
                }
                default:
                {
                    var needle = text.ToLower();
                    query = query.Where(c => values.Any(v => v.ContractId == c.Id && v.FieldId == fieldId && v.TextValue != null && v.TextValue.ToLower().Contains(needle)));
                    break;
                }
            }
        }
        return query;
    }

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    public static bool HasExclusivity(Contract contract) =>
        !string.IsNullOrWhiteSpace(contract.Exclusivity) && !string.Equals(contract.Exclusivity.Trim(), "None", StringComparison.OrdinalIgnoreCase);

    public static bool IsDueWithin(Contract contract, DateOnly today, int days)
    {
        if (contract.Status != ContractStatus.Active || contract.RenewalDecision is not null)
        {
            return false;
        }
        var daysToNotice = ContractCalculations.DaysToNotice(contract, today);
        return daysToNotice is not null && daysToNotice.Value >= 0 && daysToNotice.Value <= days;
    }

    private static DateOnly? DateOf(Contract contract, string field) => field switch
    {
        "notice" => ContractCalculations.NoticeDeadline(contract),
        "start" => contract.StartDate,
        "created" => DateOnly.FromDateTime(contract.CreatedAt),
        "signed" => contract.SignedDate,
        _ => contract.EndDate
    };

    public static bool InValueBand(decimal valueDkk, string band) => band switch
    {
        "lt1m" => valueDkk < 1_000_000m,
        "1to5m" => valueDkk >= 1_000_000m && valueDkk < 5_000_000m,
        "5to20m" => valueDkk >= 5_000_000m && valueDkk < 20_000_000m,
        "gte20m" => valueDkk >= 20_000_000m,
        _ => false
    };

    public static List<Contract> ApplyMemory(List<Contract> items, ContractFilter filter, AccessProfile profile, ReferenceData reference, DateOnly today)
    {
        IEnumerable<Contract> result = items;
        if (filter.Ids.Count > 500)
        {
            var ids = filter.Ids.ToHashSet();
            result = result.Where(c => ids.Contains(c.Id));
        }
        switch ((filter.View ?? "all").Trim().ToLowerInvariant())
        {
            case "due90":
                result = result.Where(c => IsDueWithin(c, today, 90));
                break;
            case "approval":
                result = result.Where(c => c.Status == ContractStatus.InApproval);
                break;
            case "mine":
                result = result.Where(c => ContractAccessRules.IsOnContract(profile, c));
                break;
            case "missing":
                result = result.Where(c => reference.Missing(c).Count > 0);
                break;
        }
        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var term = filter.Q.Trim();
            result = result.Where(c =>
                Contains(c.Title, term)
                || Contains(c.ContractNo, term)
                || Contains(c.CounterpartyName, term)
                || Contains(reference.Type(c.TypeId), term)
                || Contains(reference.Entity(c.EntityId), term)
                || Contains(reference.Department(c.DepartmentId), term)
                || Contains(reference.UserName(c.OwnerId), term));
        }
        if (filter.ValueBands.Count > 0)
        {
            var bands = filter.ValueBands.Where(b => ValueBandKeys.Contains(b)).ToList();
            result = result.Where(c => bands.Any(b => InValueBand(reference.ContractValueDkk(c), b)));
        }
        if (filter.MinValueDkk is not null)
        {
            var min = filter.MinValueDkk.Value;
            result = result.Where(c => reference.ContractValueDkk(c) >= min);
        }
        if (filter.From is not null || filter.To is not null)
        {
            var field = string.IsNullOrWhiteSpace(filter.DateField) ? "end" : filter.DateField!.Trim();
            if (!DateFields.Contains(field))
            {
                throw new InputValidationException("dateField", "Use notice, end, start, created or signed.");
            }
            var from = filter.From;
            var to = filter.To;
            result = result.Where(c =>
            {
                var date = DateOf(c, field);
                return date is not null && (from is null || date.Value >= from.Value) && (to is null || date.Value <= to.Value);
            });
        }
        if (filter.NoticeWithinDays is not null)
        {
            var days = filter.NoticeWithinDays.Value;
            result = result.Where(c => IsDueWithin(c, today, days));
        }
        if (filter.Exclusivity is not null)
        {
            var wanted = filter.Exclusivity.Value;
            result = result.Where(c => HasExclusivity(c) == wanted);
        }
        if (filter.MissingData is not null)
        {
            var wanted = filter.MissingData.Value;
            result = result.Where(c => (reference.Missing(c).Count > 0) == wanted);
        }
        if (!string.IsNullOrWhiteSpace(filter.MissingField))
        {
            var key = filter.MissingField.Trim();
            result = result.Where(c => reference.MissingEntries(c).Any(e => e.Key == key));
        }
        if (filter.MissingSignedDocument is not null)
        {
            var wanted = filter.MissingSignedDocument.Value;
            result = result.Where(c => (DataQuality.IsLive(c) && !reference.HasSignedDocument(c)) == wanted);
        }
        return result.ToList();
    }

    public static void Sort(List<Contract> list, string? sort, ReferenceData reference)
    {
        var key = NormalizeSortKey(sort, out var descending).ToLowerInvariant();
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
                return CompareNullable(a.AnnualValue is null ? (decimal?)null : reference.AnnualDkk(a), b.AnnualValue is null ? (decimal?)null : reference.AnnualDkk(b), descending);
            case "totalvalue":
                return CompareNullable(a.TotalValue is null ? (decimal?)null : reference.TotalDkk(a), b.TotalValue is null ? (decimal?)null : reference.TotalDkk(b), descending);
            case "startdate":
            case "start":
                return CompareNullable<DateOnly>(a.StartDate, b.StartDate, descending);
            case "enddate":
            case "end":
                return CompareNullable(a.EndDate, b.EndDate, descending);
            case "created":
            case "createdat":
                return CompareNullable<DateTime>(a.CreatedAt, b.CreatedAt, descending);
            default:
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
}
