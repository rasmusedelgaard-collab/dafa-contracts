using System.Text.RegularExpressions;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>Release 4: users as picker options (name, initials, department, home entity).</summary>
public static class PersonDirectory
{
    public static async Task<List<PersonOptionDto>> ToOptionsAsync(IAppDbContext db, IReadOnlyCollection<AppUser> users, CancellationToken cancellationToken = default)
    {
        var result = new List<PersonOptionDto>();
        if (users.Count == 0)
        {
            return result;
        }
        var departments = (await db.Departments.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(d => d.Id);
        var entities = (await db.LegalEntities.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(e => e.Id, e => e.Name);
        var userIds = users.Select(u => u.Id).ToList();
        var memberships = await db.UserDepartments.AsNoTracking().Where(m => userIds.Contains(m.UserId)).ToListAsync(cancellationToken);
        foreach (var user in users)
        {
            var names = memberships
                .Where(m => m.UserId == user.Id && departments.ContainsKey(m.DepartmentId))
                .Select(m => departments[m.DepartmentId])
                .OrderBy(d => d.SortOrder).ThenBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => d.Name)
                .ToList();
            result.Add(new PersonOptionDto
            {
                Id = user.Id,
                Name = user.Name,
                Initials = user.Initials,
                Email = user.Email,
                Title = user.JobTitle,
                DepartmentName = names.Count == 0 ? null : names[0],
                DepartmentNames = names,
                EntityName = user.HomeEntityId is not null && entities.TryGetValue(user.HomeEntityId.Value, out var entityName) ? entityName : null,
                IsActive = user.IsActive
            });
        }
        return result;
    }
}

/// <summary>
/// Release 4 pickers (CONTRACT_V4 section 3): the person type-ahead and "create counterparty" for
/// the users the general setting counterpartyCreation allows.
/// </summary>
public class PickerService
{
    private static readonly Regex CountryPattern = new("^[A-Za-z]{2}$", RegexOptions.CultureInvariant);

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public PickerService(IAppDbContext db, ICurrentUser currentUser, IClock clock, AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>
    /// GET people?q=&amp;limit=&amp;role=&amp;includeInactive=. Name, e-mail or initials contain q; active users
    /// only unless includeInactive; role restricts to users with that role (Admin counts for every role).
    /// </summary>
    public async Task<List<PersonOptionDto>> SearchPeopleAsync(string? q, int? limit, string? role, bool includeInactive, CancellationToken cancellationToken = default)
    {
        // Known, active callers only.
        await _currentUser.GetProfileAsync(cancellationToken);
        var term = (q ?? "").Trim();
        var take = Math.Clamp(limit ?? 20, 1, 50);
        AppRole? wanted = null;
        var roleText = (role ?? "").Trim();
        if (roleText.Length > 0)
        {
            if (!Enum.TryParse<AppRole>(roleText, false, out var parsed) || parsed == AppRole.None || !Enum.IsDefined(typeof(AppRole), parsed) || int.TryParse(roleText, out _))
            {
                throw new InputValidationException("role", "Use ContractOwner, Approver or Admin.");
            }
            // Every user is a reader: role=Reader restricts nothing.
            wanted = parsed == AppRole.Reader ? null : parsed;
        }
        var users = await _db.Users.AsNoTracking().ToListAsync(cancellationToken);
        var matches = users
            .Where(u => includeInactive || u.IsActive)
            .Where(u => wanted is null || (u.Roles & wanted.Value) == wanted.Value || (u.Roles & AppRole.Admin) == AppRole.Admin)
            .Where(u => term.Length == 0
                || u.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || u.Email.Contains(term, StringComparison.OrdinalIgnoreCase)
                || u.Initials.Contains(term, StringComparison.OrdinalIgnoreCase))
            // Names that start with the term first, then alphabetical.
            .OrderBy(u => term.Length > 0 && u.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
        var options = await PersonDirectory.ToOptionsAsync(_db, matches, cancellationToken);
        return options.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<bool> MayCreateCounterpartyAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var settings = await AppSettingsStore.LoadAsync(_db, cancellationToken);
        return AppSettingsStore.MayCreateCounterparty(settings, profile);
    }

    /// <summary>
    /// POST counterparties: creates a master record from the contract form. 403 not-permitted unless
    /// the setting allows the caller; 409 duplicate (with existingId) when an active record has the
    /// same name (case-insensitive, trimmed) or the same VAT number.
    /// </summary>
    public async Task<CounterpartyDto> CreateCounterpartyAsync(CounterpartyInput input, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var settings = await AppSettingsStore.LoadAsync(_db, cancellationToken);
        if (!AppSettingsStore.MayCreateCounterparty(settings, profile))
        {
            throw new NotPermittedException(settings.CounterpartyCreation == "ContractOwners"
                ? "Only contract owners and administrators can create counterparties. Ask one of them to add it."
                : "Only administrators can create counterparties. Ask an administrator to add it.");
        }
        var errors = new Dictionary<string, string[]>();
        var name = (input?.Name ?? "").Trim();
        if (name.Length < 1 || name.Length > 300 || name.Any(char.IsControl))
            ContractValidator.Add(errors, "name", "Name is required (at most 300 characters).");
        var country = string.IsNullOrWhiteSpace(input?.Country) ? null : input!.Country!.Trim().ToUpperInvariant();
        if (country is not null && !CountryPattern.IsMatch(country))
            ContractValidator.Add(errors, "country", "Use a two-letter country code.");
        var email = string.IsNullOrWhiteSpace(input?.Email) ? null : input!.Email!.Trim();
        if (email is not null && !SignatureService.IsValidEmail(email))
            ContractValidator.Add(errors, "email", "Enter a valid e-mail address.");
        var kind = CounterpartyKind.Supplier;
        if (!string.IsNullOrWhiteSpace(input?.Kind)
            && (!Enum.TryParse(input!.Kind!.Trim(), false, out kind) || !Enum.IsDefined(typeof(CounterpartyKind), kind) || int.TryParse(input.Kind.Trim(), out _)))
            ContractValidator.Add(errors, "kind", "Use Supplier, Customer, Partner, Authority or Other.");
        var vat = string.IsNullOrWhiteSpace(input?.VatNo) ? null : input!.VatNo!.Trim();
        if (vat is not null && vat.Length > 50) ContractValidator.Add(errors, "vatNo", "The VAT number can be at most 50 characters.");
        var city = string.IsNullOrWhiteSpace(input?.City) ? null : input!.City!.Trim();
        if (city is not null && city.Length > 100) ContractValidator.Add(errors, "city", "The city can be at most 100 characters.");
        var phone = string.IsNullOrWhiteSpace(input?.Phone) ? null : input!.Phone!.Trim();
        if (phone is not null && phone.Length > 50) ContractValidator.Add(errors, "phone", "The phone number can be at most 50 characters.");
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }

        var active = await _db.Suppliers.AsNoTracking().Where(s => s.IsActive).ToListAsync(cancellationToken);
        var existing = active.FirstOrDefault(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            ?? (vat is null ? null : active.FirstOrDefault(s => string.Equals((s.VatNo ?? "").Trim(), vat, StringComparison.OrdinalIgnoreCase)));
        if (existing is not null)
        {
            throw new ConflictException(
                "The counterparty already exists: " + existing.Name + ". Choose it from the list.",
                ProblemTypes.Duplicate,
                new Dictionary<string, object?> { ["existingId"] = existing.Id });
        }

        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Name = name,
            Country = country,
            VatNo = vat,
            Kind = kind,
            City = city,
            Email = email,
            Phone = phone,
            IsActive = true,
            Source = "Manual",
            UpdatedAt = _clock.UtcNow
        };
        _db.Suppliers.Add(supplier);
        _audit.Record("counterparty.created", "Created counterparty " + supplier.Name + " from the contract form");
        await _db.SaveChangesAsync(cancellationToken);
        return SearchService.ToCounterpartyDto(supplier, 0);
    }
}

/// <summary>
/// Release 4: GET contracts/distinct, the values of one register column for a header filter, with
/// counts. Runs the shared contract filter (so the access filter) with the column's own filter
/// taken out, then counts over the visible, matching contracts only.
/// </summary>
public class DistinctValuesService
{
    public static readonly string[] Columns =
    {
        "counterparty", "type", "entity", "department", "owner", "delegate", "responsible", "status", "currency", "tags"
    };

    private static readonly Dictionary<ContractStatus, string> StatusLabels = new()
    {
        [ContractStatus.Draft] = "Draft",
        [ContractStatus.InApproval] = "In approval",
        [ContractStatus.Approved] = "Approved",
        [ContractStatus.Active] = "Active",
        [ContractStatus.NoticeGiven] = "Notice given",
        [ContractStatus.Expired] = "Expired",
        [ContractStatus.Terminated] = "Terminated"
    };

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IContractAccessService _access;
    private readonly IClock _clock;

    public DistinctValuesService(IAppDbContext db, ICurrentUser currentUser, IContractAccessService access, IClock clock)
    {
        _db = db;
        _currentUser = currentUser;
        _access = access;
        _clock = clock;
    }

    /// <summary>Takes the column's own filter out, so the list does not collapse to the selected values.</summary>
    private static void ClearOwnFilter(ContractFilter filter, string column)
    {
        switch (column)
        {
            case "counterparty": filter.CounterpartyIds.Clear(); break;
            case "type": filter.TypeIds.Clear(); break;
            case "entity": filter.EntityIds.Clear(); break;
            case "department": filter.DepartmentIds.Clear(); break;
            case "owner": filter.OwnerIds.Clear(); break;
            case "delegate": filter.DelegateIds.Clear(); break;
            case "responsible": filter.ResponsibleIds.Clear(); break;
            case "status": filter.Statuses.Clear(); break;
            case "currency": filter.Currencies.Clear(); break;
            case "tags": filter.TagIds.Clear(); break;
            default:
                if (column.StartsWith("cf.", StringComparison.Ordinal))
                {
                    var key = column.Substring(3);
                    filter.Custom.Remove(key);
                    filter.CustomAny.Remove(key);
                }
                break;
        }
    }

    private static void Count(Dictionary<string, (string Label, int Count)> counts, string value, string label)
    {
        counts[value] = counts.TryGetValue(value, out var existing) ? (existing.Label, existing.Count + 1) : (label, 1);
    }

    public async Task<DistinctValuesDto> GetAsync(string? column, ContractFilter filter, string? q, int? limit, CancellationToken cancellationToken = default)
    {
        var key = (column ?? "").Trim();
        CustomFieldDefinition? definition = null;
        if (key.StartsWith("cf.", StringComparison.Ordinal) && key.Length > 3)
        {
            var fieldKey = key.Substring(3);
            definition = await _db.CustomFieldDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.IsActive && d.Key == fieldKey, cancellationToken);
            var listable = definition is not null && (definition.FieldType == CustomFieldType.Choice || definition.FieldType == CustomFieldType.MultiChoice
                || definition.FieldType == CustomFieldType.Person || definition.FieldType == CustomFieldType.Text);
            if (!listable)
            {
                throw new InputValidationException("column", "Unknown column '" + key + "'. Custom fields of type Choice, MultiChoice, Person and Text can be listed.");
            }
        }
        else if (!Columns.Contains(key))
        {
            throw new InputValidationException("column", "Unknown column '" + key + "'.");
        }

        // Sorting and paging of the register do not apply here.
        filter.Sort = null;
        filter.Page = null;
        filter.PageSize = null;
        filter.Columns = new List<string>();
        ClearOwnFilter(filter, key);
        var engine = new ContractFilterEngine(_db, _access, _currentUser, _clock);
        var result = await engine.RunAsync(filter, true, cancellationToken);
        var contracts = result.Items;
        var reference = result.Reference;
        var counts = new Dictionary<string, (string Label, int Count)>(StringComparer.Ordinal);

        switch (key)
        {
            case "counterparty":
            {
                // Contracts without a master record are not listed (the filter for those is counterpartyName).
                var names = (await _db.Suppliers.AsNoTracking().Select(s => new { s.Id, s.Name }).ToListAsync(cancellationToken)).ToDictionary(s => s.Id, s => s.Name);
                foreach (var contract in contracts.Where(c => c.SupplierId is not null))
                {
                    var id = contract.SupplierId!.Value;
                    Count(counts, id.ToString(), names.TryGetValue(id, out var name) ? name : contract.CounterpartyName);
                }
                break;
            }
            case "type":
                foreach (var contract in contracts) Count(counts, contract.TypeId.ToString(), reference.Type(contract.TypeId));
                break;
            case "entity":
                foreach (var contract in contracts) Count(counts, contract.EntityId.ToString(), reference.Entity(contract.EntityId));
                break;
            case "department":
                foreach (var contract in contracts) Count(counts, contract.DepartmentId.ToString(), reference.Department(contract.DepartmentId));
                break;
            case "owner":
                foreach (var contract in contracts) Count(counts, contract.OwnerId.ToString(), reference.UserName(contract.OwnerId));
                break;
            case "delegate":
                foreach (var contract in contracts.Where(c => c.DelegateId is not null)) Count(counts, contract.DelegateId!.Value.ToString(), reference.UserName(contract.DelegateId));
                break;
            case "responsible":
                foreach (var contract in contracts.Where(c => c.ResponsibleId is not null)) Count(counts, contract.ResponsibleId!.Value.ToString(), reference.UserName(contract.ResponsibleId));
                break;
            case "status":
                foreach (var contract in contracts) Count(counts, contract.Status.ToString(), StatusLabels.TryGetValue(contract.Status, out var label) ? label : contract.Status.ToString());
                break;
            case "currency":
                foreach (var contract in contracts) Count(counts, contract.Currency, contract.Currency);
                break;
            case "tags":
            {
                var visibleIds = contracts.Select(c => c.Id).ToHashSet();
                var tags = (await _db.Tags.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(t => t.Id, t => t.Name);
                var links = await _db.ContractTags.AsNoTracking().ToListAsync(cancellationToken);
                foreach (var link in links.Where(l => visibleIds.Contains(l.ContractId) && tags.ContainsKey(l.TagId)))
                {
                    Count(counts, link.TagId.ToString(), tags[link.TagId]);
                }
                break;
            }
            default:
            {
                var visibleIds = contracts.Select(c => c.Id).ToHashSet();
                var fieldId = definition!.Id;
                var values = await _db.CustomFieldValues.AsNoTracking().Where(v => v.FieldId == fieldId).ToListAsync(cancellationToken);
                foreach (var value in values.Where(v => visibleIds.Contains(v.ContractId)))
                {
                    switch (definition.FieldType)
                    {
                        case CustomFieldType.Person:
                            if (value.UserValue is not null) Count(counts, value.UserValue.Value.ToString(), reference.UserName(value.UserValue));
                            break;
                        case CustomFieldType.MultiChoice:
                            foreach (var option in (value.TextValue ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)) Count(counts, option, option);
                            break;
                        default:
                            if (!string.IsNullOrWhiteSpace(value.TextValue)) Count(counts, value.TextValue!, value.TextValue!);
                            break;
                    }
                }
                break;
            }
        }

        var term = (q ?? "").Trim();
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var all = counts
            .Select(pair => new DistinctValueDto { Value = pair.Key, Label = string.IsNullOrWhiteSpace(pair.Value.Label) ? pair.Key : pair.Value.Label, Count = pair.Value.Count })
            .Where(v => term.Length == 0 || v.Label.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Count)
            .ThenBy(v => v.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DistinctValuesDto
        {
            Column = key,
            Values = all.Take(take).ToList(),
            Truncated = all.Count > take
        };
    }
}
