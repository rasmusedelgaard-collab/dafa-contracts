using System.Text.RegularExpressions;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Admin: legal entities, currencies, users and memberships, effective access, bulk reassignment
/// (CONTRACT_V3 sections 9.1, 9.4, 9.5). The host restricts these endpoints to role Admin.
/// </summary>
public class AdminDirectoryService
{
    private static readonly Regex CountryPattern = new("^[A-Z]{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex CurrencyPattern = new("^[A-Z]{3}$", RegexOptions.CultureInvariant);
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant);

    /// <summary>Fields of an Entra-linked user that come from the directory.</summary>
    public static readonly string[] SyncedFields = { "name", "email", "title", "isActive" };

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public AdminDirectoryService(IAppDbContext db, ICurrentUser currentUser, IClock clock, AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _audit = audit;
    }

    public static string Initials(string name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0].Substring(0, 1) + parts[parts.Length - 1].Substring(0, 1)).ToUpperInvariant();
    }

    // ---------- legal entities ----------

    public async Task<List<LegalEntityDto>> GetEntitiesAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _db.LegalEntities.AsNoTracking().ToListAsync(cancellationToken);
        var counts = (await _db.Contracts.AsNoTracking().Select(c => c.EntityId).ToListAsync(cancellationToken))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        return entities.OrderBy(e => e.SortOrder).ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => ToDto(e, counts.TryGetValue(e.Id, out var n) ? n : 0))
            .ToList();
    }

    private static LegalEntityDto ToDto(LegalEntity entity, int count) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Country = entity.Country ?? "",
        Currency = entity.Currency ?? "",
        IsActive = entity.IsActive,
        ContractCount = count,
        SharePointSiteUrl = entity.SharePointSiteUrl,
        SharePointSiteId = entity.SharePointSiteId,
        ManagementGroupId = entity.ManagementGroupId,
        ManagementGroupName = entity.ManagementGroupName,
        Link = OrgLinkRules.ToDto(entity)
    };

    private async Task ApplyEntityAsync(LegalEntity entity, LegalEntityInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = (input?.Name ?? "").Trim();
        if (name.Length < 1 || name.Length > 200) ContractValidator.Add(errors, "name", "Name is required (at most 200 characters).");
        var country = (input?.Country ?? "").Trim();
        if (!CountryPattern.IsMatch(country)) ContractValidator.Add(errors, "country", "Use a two-letter country code in capitals (ISO 3166).");
        var currency = (input?.Currency ?? "").Trim();
        var active = await CurrencyTable.ActiveCodesAsync(_db, cancellationToken);
        if (!active.Contains(currency)) ContractValidator.Add(errors, "currency", "Choose an active currency.");
        var entityId = entity.Id;
        if (name.Length > 0 && await _db.LegalEntities.AnyAsync(e => e.Id != entityId && e.Name == name, cancellationToken))
            ContractValidator.Add(errors, "name", "Another legal entity has this name.");
        // Release 4: SharePoint site and Entra management group (set as given; null clears).
        var siteUrl = OrgLinkRules.NormalizeSiteUrl(input?.SharePointSiteUrl, errors);
        var groupId = OrgLinkRules.NormalizeGroupId(input?.ManagementGroupId, "managementGroupId", errors);
        if (siteUrl is not null)
        {
            var others = await _db.LegalEntities.AsNoTracking().Where(e => e.Id != entityId && e.SharePointSiteUrl != null).Select(e => e.SharePointSiteUrl).ToListAsync(cancellationToken);
            if (others.Any(other => string.Equals(other, siteUrl, StringComparison.OrdinalIgnoreCase)))
                ContractValidator.Add(errors, "sharePointSiteUrl", "Another legal entity is linked to this site. Every legal entity has its own site.");
        }
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        entity.Name = name;
        entity.Country = country;
        entity.Currency = currency;
        entity.IsActive = input!.IsActive ?? true;
        OrgLinkRules.Apply(entity, siteUrl, groupId, OrgLinkRules.NormalizeName(input.ManagementGroupName));
    }

    public async Task<LegalEntityDto> CreateEntityAsync(LegalEntityInput input, CancellationToken cancellationToken = default)
    {
        var orders = await _db.LegalEntities.Select(e => e.SortOrder).ToListAsync(cancellationToken);
        var entity = new LegalEntity { Id = Guid.NewGuid(), SortOrder = orders.Count == 0 ? 1 : orders.Max() + 1 };
        await ApplyEntityAsync(entity, input, cancellationToken);
        _db.LegalEntities.Add(entity);
        _audit.Record("settings.entities", "Created legal entity " + entity.Name);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(entity, 0);
    }

    public async Task<LegalEntityDto> UpdateEntityAsync(Guid id, LegalEntityInput input, CancellationToken cancellationToken = default)
    {
        var entity = await _db.LegalEntities.FirstOrDefaultAsync(e => e.Id == id, cancellationToken) ?? throw new NotFoundException("Legal entity not found.");
        await ApplyEntityAsync(entity, input, cancellationToken);
        _audit.Record("settings.entities", "Updated legal entity " + entity.Name + (entity.IsActive ? "" : " (inactive)"));
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(entity, await _db.Contracts.CountAsync(c => c.EntityId == id, cancellationToken));
    }

    // ---------- currencies ----------

    public async Task<List<CurrencyDto>> GetCurrenciesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.Currencies.AsNoTracking().ToListAsync(cancellationToken);
        return rows.OrderBy(c => c.SortOrder).ThenBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new CurrencyDto { Code = c.Code, Name = c.Name, RateToDkk = c.RateToDkk, IsActive = c.IsActive })
            .ToList();
    }

    /// <summary>PUT admin/currencies: replaces the list. The rates feed every DKK figure, approvals included.</summary>
    public async Task<List<CurrencyDto>> ReplaceCurrenciesAsync(List<CurrencyDto> input, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        var items = (input ?? new List<CurrencyDto>()).Where(c => c is not null).ToList();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var code = (item.Code ?? "").Trim();
            if (!CurrencyPattern.IsMatch(code)) ContractValidator.Add(errors, "code", "Codes are three capital letters (" + code + ").");
            else if (!codes.Add(code)) ContractValidator.Add(errors, "code", "Code " + code + " is listed twice.");
            if (item.RateToDkk is null || item.RateToDkk <= 0m) ContractValidator.Add(errors, "rateToDkk", "Rates must be greater than 0 (" + code + ").");
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Trim().Length > 100) ContractValidator.Add(errors, "name", "Name is required (" + code + ").");
        }
        var dkk = items.FirstOrDefault(i => (i.Code ?? "").Trim() == "DKK");
        if (dkk is null || dkk.IsActive == false || dkk.RateToDkk != 1m)
            ContractValidator.Add(errors, "code", "DKK must be in the list, active, with rate 1.");
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        var existing = await _db.Currencies.ToListAsync(cancellationToken);
        foreach (var removed in existing.Where(e => !codes.Contains(e.Code)).ToList())
        {
            var code = removed.Code;
            if (await _db.Contracts.AnyAsync(c => c.Currency == code, cancellationToken) || await _db.LegalEntities.AnyAsync(e => e.Currency == code, cancellationToken))
            {
                throw new ConflictException("Currency " + code + " is used by contracts or legal entities. Deactivate it instead.", ProblemTypesV3.InUse);
            }
            _db.Currencies.Remove(removed);
        }
        var order = 0;
        foreach (var item in items)
        {
            var code = item.Code!.Trim();
            var row = existing.FirstOrDefault(e => e.Code == code);
            if (row is null)
            {
                row = new CurrencyDefinition { Code = code };
                _db.Currencies.Add(row);
            }
            row.Name = item.Name!.Trim();
            row.RateToDkk = item.RateToDkk!.Value;
            row.IsActive = item.IsActive ?? true;
            row.SortOrder = ++order;
        }
        _audit.Record("settings.currencies", "Updated currencies and rates: " + string.Join(", ", items.Select(i => i.Code + " " + i.RateToDkk)));
        await _db.SaveChangesAsync(cancellationToken);
        return await GetCurrenciesAsync(cancellationToken);
    }

    // ---------- users ----------

    private static List<string> ParseRoles(List<string>? roles, Dictionary<string, string[]> errors, out AppRole value)
    {
        value = AppRole.None;
        var names = new List<string>();
        foreach (var role in roles ?? new List<string>())
        {
            if (Enum.TryParse<AppRole>(role, false, out var parsed) && parsed != AppRole.None && Enum.IsDefined(typeof(AppRole), parsed) && !int.TryParse(role, out _))
            {
                value |= parsed;
                names.Add(role);
            }
            else
            {
                ContractValidator.Add(errors, "roles", "Unknown role: " + role + ".");
            }
        }
        if (value == AppRole.None)
        {
            value = AppRole.Reader;
        }
        return names;
    }

    private async Task<List<AdminUserDto>> ToDtosAsync(List<AppUser> users, CancellationToken cancellationToken)
    {
        var departments = await _db.UserDepartments.AsNoTracking().ToListAsync(cancellationToken);
        var managed = await _db.UserEntityManagements.AsNoTracking().ToListAsync(cancellationToken);
        var contracts = await _db.Contracts.AsNoTracking().Select(c => new { c.OwnerId, c.DelegateId, c.ResponsibleId }).ToListAsync(cancellationToken);
        return users.Select(u => new AdminUserDto
        {
            Id = u.Id,
            Name = u.Name,
            Email = u.Email,
            Initials = u.Initials,
            Title = u.JobTitle,
            Roles = u.Roles.ToNames(),
            DepartmentIds = departments.Where(d => d.UserId == u.Id).Select(d => d.DepartmentId).ToList(),
            ManagedEntityIds = managed.Where(m => m.UserId == u.Id).Select(m => m.EntityId).ToList(),
            IsGroupManagement = u.IsGroupManagement,
            IsActive = u.IsActive,
            Source = !string.IsNullOrEmpty(u.ExternalId) ? "Entra" : (string.IsNullOrWhiteSpace(u.Source) ? "Seed" : u.Source!),
            SyncedFields = !string.IsNullOrEmpty(u.ExternalId) ? SyncedFields.ToList() : new List<string>(),
            ExternalId = u.ExternalId,
            LastSignInAt = u.LastSignInAt,
            ContractCount = contracts.Count(c => c.OwnerId == u.Id || c.DelegateId == u.Id || c.ResponsibleId == u.Id),
            EntityId = u.HomeEntityId,
            CanSendForSignature = u.CanSendForSignature,
            SignatureEntityIds = JsonDefaults.ReadList<Guid>(u.SignatureEntityIdsJson)
        }).ToList();
    }

    public async Task<List<AdminUserDto>> GetUsersAsync(string? q, bool? active, CancellationToken cancellationToken = default)
    {
        var term = (q ?? "").Trim();
        var users = (await _db.Users.AsNoTracking().ToListAsync(cancellationToken))
            .Where(u => active is null || u.IsActive == active.Value)
            .Where(u => term.Length == 0 || u.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || u.Email.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return await ToDtosAsync(users, cancellationToken);
    }

    public async Task<AdminUserDto> GetUserAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken) ?? throw new NotFoundException("User not found.");
        return (await ToDtosAsync(new List<AppUser> { user }, cancellationToken))[0];
    }

    private async Task ValidateMembershipsAsync(AdminUserInput input, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        foreach (var departmentId in (input.DepartmentIds ?? new List<Guid>()).Distinct())
        {
            if (!await _db.Departments.AnyAsync(d => d.Id == departmentId, cancellationToken))
                ContractValidator.Add(errors, "departmentIds", "Unknown department.");
        }
        foreach (var entityId in (input.ManagedEntityIds ?? new List<Guid>()).Distinct())
        {
            if (!await _db.LegalEntities.AnyAsync(e => e.Id == entityId, cancellationToken))
                ContractValidator.Add(errors, "managedEntityIds", "Unknown legal entity.");
        }
    }

    /// <summary>
    /// Release 4: validates the home entity and the signing permission of a user input and returns
    /// what to store. homeEntity: (false, _) = unchanged; (true, id or null) = set.
    /// </summary>
    private async Task<(bool SetHome, Guid? HomeEntityId, List<Guid>? SignatureEntityIds)> ValidateRelease4Async(
        AdminUserInput input, Guid? currentHomeEntityId, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        var setHome = false;
        Guid? home = null;
        var element = input.EntityId;
        if (element.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            setHome = true;
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                setHome = true;
            }
            else if (!Guid.TryParse(text, out var parsed))
            {
                ContractValidator.Add(errors, "entityId", "Not a valid id.");
            }
            else
            {
                // The entity the user already has stays valid even when it was deactivated since.
                var known = parsed == currentHomeEntityId
                    ? await _db.LegalEntities.AnyAsync(e => e.Id == parsed, cancellationToken)
                    : await _db.LegalEntities.AnyAsync(e => e.Id == parsed && e.IsActive, cancellationToken);
                if (!known)
                    ContractValidator.Add(errors, "entityId", "Choose an active legal entity.");
                setHome = true;
                home = parsed;
            }
        }
        else if (element.ValueKind != System.Text.Json.JsonValueKind.Undefined)
        {
            ContractValidator.Add(errors, "entityId", "Not a valid id.");
        }

        List<Guid>? signatureEntityIds = null;
        if (input.SignatureEntityIds is not null)
        {
            signatureEntityIds = input.SignatureEntityIds.Distinct().ToList();
            foreach (var entityId in signatureEntityIds)
            {
                if (!await _db.LegalEntities.AnyAsync(e => e.Id == entityId, cancellationToken))
                    ContractValidator.Add(errors, "signatureEntityIds", "Unknown legal entity.");
            }
        }
        return (setHome, home, signatureEntityIds);
    }

    private static void ApplyRelease4(AppUser user, AdminUserInput input, (bool SetHome, Guid? HomeEntityId, List<Guid>? SignatureEntityIds) values)
    {
        if (values.SetHome)
        {
            user.HomeEntityId = values.HomeEntityId;
        }
        if (input.CanSendForSignature is not null)
        {
            user.CanSendForSignature = input.CanSendForSignature.Value;
        }
        if (values.SignatureEntityIds is not null)
        {
            user.SignatureEntityIdsJson = values.SignatureEntityIds.Count == 0 ? null : JsonDefaults.Write(values.SignatureEntityIds);
        }
    }

    private static string SigningText(AppUser user)
    {
        if (!user.CanSendForSignature)
        {
            return "";
        }
        var limit = JsonDefaults.ReadList<Guid>(user.SignatureEntityIdsJson).Count;
        return ", may send for signature (" + (limit == 0 ? "all legal entities" : limit + (limit == 1 ? " legal entity" : " legal entities")) + ")";
    }

    private async Task SetMembershipsAsync(Guid userId, AdminUserInput input, CancellationToken cancellationToken)
    {
        if (input.DepartmentIds is not null)
        {
            var wanted = input.DepartmentIds.Distinct().ToList();
            var current = await _db.UserDepartments.Where(d => d.UserId == userId).ToListAsync(cancellationToken);
            _db.UserDepartments.RemoveRange(current.Where(c => !wanted.Contains(c.DepartmentId)));
            foreach (var departmentId in wanted.Where(w => current.All(c => c.DepartmentId != w)))
            {
                _db.UserDepartments.Add(new UserDepartment { UserId = userId, DepartmentId = departmentId, IsHead = false });
            }
        }
        if (input.ManagedEntityIds is not null)
        {
            var wanted = input.ManagedEntityIds.Distinct().ToList();
            var current = await _db.UserEntityManagements.Where(m => m.UserId == userId).ToListAsync(cancellationToken);
            _db.UserEntityManagements.RemoveRange(current.Where(c => !wanted.Contains(c.EntityId)));
            foreach (var entityId in wanted.Where(w => current.All(c => c.EntityId != w)))
            {
                _db.UserEntityManagements.Add(new UserEntityManagement { UserId = userId, EntityId = entityId });
            }
        }
    }

    public async Task<AdminUserDto> CreateUserAsync(AdminUserInput input, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        input ??= new AdminUserInput();
        var name = (input.Name ?? "").Trim();
        var email = (input.Email ?? "").Trim();
        if (name.Length < 1 || name.Length > 200) ContractValidator.Add(errors, "name", "Name is required.");
        if (!EmailPattern.IsMatch(email) || email.Length > 256) ContractValidator.Add(errors, "email", "Enter a valid e-mail address.");
        ParseRoles(input.Roles, errors, out var roles);
        await ValidateMembershipsAsync(input, errors, cancellationToken);
        var release4 = await ValidateRelease4Async(input, null, errors, cancellationToken);
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        var lower = email.ToLower();
        if (await _db.Users.AnyAsync(u => u.Email.ToLower() == lower, cancellationToken))
        {
            throw new ConflictException("A user with this e-mail address already exists.", ProblemTypes.Duplicate);
        }
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Name = name,
            Email = email,
            Initials = Initials(name),
            JobTitle = string.IsNullOrWhiteSpace(input.Title) ? null : input.Title.Trim(),
            Roles = roles,
            IsGroupManagement = input.IsGroupManagement ?? false,
            IsActive = input.IsActive ?? true,
            Source = "Manual"
        };
        ApplyRelease4(user, input, release4);
        _db.Users.Add(user);
        await SetMembershipsAsync(user.Id, input, cancellationToken);
        _audit.Record("settings.users", "Created user " + user.Name + " (" + string.Join(", ", roles.ToNames()) + ")" + SigningText(user));
        await _db.SaveChangesAsync(cancellationToken);
        return await GetUserAsync(user.Id, cancellationToken);
    }

    public async Task<AdminUserDto> UpdateUserAsync(Guid id, AdminUserInput input, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken) ?? throw new NotFoundException("User not found.");
        input ??= new AdminUserInput();
        var errors = new Dictionary<string, string[]>();
        var name = input.Name is null ? user.Name : input.Name.Trim();
        var email = input.Email is null ? user.Email : input.Email.Trim();
        if (name.Length < 1 || name.Length > 200) ContractValidator.Add(errors, "name", "Name is required.");
        if (!EmailPattern.IsMatch(email) || email.Length > 256) ContractValidator.Add(errors, "email", "Enter a valid e-mail address.");
        var roles = user.Roles;
        if (input.Roles is not null)
        {
            ParseRoles(input.Roles, errors, out roles);
        }
        await ValidateMembershipsAsync(input, errors, cancellationToken);
        var release4 = await ValidateRelease4Async(input, user.HomeEntityId, errors, cancellationToken);
        if (errors.Count > 0)
        {
            throw new InputValidationException(errors);
        }
        var title = input.Title is null ? user.JobTitle : (string.IsNullOrWhiteSpace(input.Title) ? null : input.Title.Trim());
        var isActive = input.IsActive ?? user.IsActive;
        if (!string.IsNullOrEmpty(user.ExternalId))
        {
            var changed = new List<string>();
            if (name != user.Name) changed.Add("name");
            if (!string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase)) changed.Add("email");
            if (title != user.JobTitle) changed.Add("title");
            if (isActive != user.IsActive) changed.Add("isActive");
            if (changed.Count > 0)
            {
                throw new ConflictException("These fields come from Entra ID and cannot be changed here: " + string.Join(", ", changed) + ".", ProblemTypesV3.SyncedField);
            }
        }
        if (user.Id == profile.UserId)
        {
            if ((roles & AppRole.Admin) != AppRole.Admin)
                throw new ConflictException("You cannot remove your own administrator role.");
            if (!isActive)
                throw new ConflictException("You cannot deactivate yourself.");
        }
        var lower = email.ToLower();
        if (await _db.Users.AnyAsync(u => u.Id != id && u.Email.ToLower() == lower, cancellationToken))
        {
            throw new ConflictException("Another user has this e-mail address.", ProblemTypes.Duplicate);
        }
        var before = user.Roles.ToNames();
        user.Name = name;
        user.Email = email;
        user.Initials = Initials(name);
        user.JobTitle = title;
        user.Roles = roles;
        user.IsGroupManagement = input.IsGroupManagement ?? user.IsGroupManagement;
        user.IsActive = isActive;
        var signingBefore = SigningText(user);
        ApplyRelease4(user, input, release4);
        await SetMembershipsAsync(user.Id, input, cancellationToken);
        _audit.Record("settings.users", "Updated user " + user.Name + ": roles " + string.Join(", ", before) + " -> " + string.Join(", ", roles.ToNames())
            + (user.IsActive ? "" : " (inactive)") + (user.IsGroupManagement ? ", group management" : "")
            + (signingBefore != SigningText(user)
                ? "; signing permission changed" + (user.CanSendForSignature ? SigningText(user) : ": may no longer send for signature")
                : ""));
        await _db.SaveChangesAsync(cancellationToken);
        return await GetUserAsync(user.Id, cancellationToken);
    }

    // ---------- effective access ----------

    private static readonly (string Rule, string Label)[] RuleLabels =
    {
        ("Admin", "Administrator"), ("GroupManagement", "Group management"), ("Department", "Department membership"),
        ("EntityManagement", "Entity management"), ("OnContract", "Owner, delegate or responsible"), ("CurrentApprover", "Current approver")
    };

    public async Task<EffectiveAccessDto> EffectiveAccessAsync(Guid userId, string? q, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        var userDto = await GetUserAsync(userId, cancellationToken);
        var result = new EffectiveAccessDto
        {
            User = userDto,
            Page = Math.Max(1, page ?? 1),
            PageSize = Math.Clamp(pageSize ?? 25, 1, 200)
        };
        // The same rules as ContractAccessService, evaluated for that user (inactive users see nothing).
        var profile = await AccessProfileLoader.LoadAsync(_db, userId, cancellationToken);
        if (profile is null)
        {
            result.Rules = RuleLabels.Select(r => new AccessRuleCountDto { Rule = r.Rule, Label = r.Label, Count = 0 }).ToList();
            return result;
        }
        var approvals = await ContractAccessService.LoadApprovalContractIdsAsync(_db, userId, cancellationToken);
        var contracts = await ContractAccessService.ApplyFilter(_db.Contracts.AsNoTracking(), profile, approvals).ToListAsync(cancellationToken);
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        if (!string.IsNullOrWhiteSpace(q))
        {
            contracts = ContractFilterEngine.ApplyMemory(contracts, new ContractFilter { Q = q }, profile, reference, _clock.Today);
        }
        var rows = new List<(Contract Contract, List<AccessReasonDto> Reasons)>();
        foreach (var contract in contracts.OrderBy(c => c.ContractNo, StringComparer.Ordinal))
        {
            var reasons = new List<AccessReasonDto>();
            foreach (var rule in ContractAccessRules.GrantingRules(profile, contract, approvals.Contains(contract.Id)))
            {
                switch (rule)
                {
                    case "Admin": reasons.Add(new AccessReasonDto { Rule = "Admin", Label = "Administrator" }); break;
                    case "GroupManagement": reasons.Add(new AccessReasonDto { Rule = "GroupManagement", Label = "Group management" }); break;
                    case "Department": reasons.Add(new AccessReasonDto { Rule = "Department", Label = "Member of " + reference.Department(contract.DepartmentId) }); break;
                    case "EntityManagement": reasons.Add(new AccessReasonDto { Rule = "EntityManagement", Label = "Manages " + reference.Entity(contract.EntityId) }); break;
                    case "CurrentApprover": reasons.Add(new AccessReasonDto { Rule = "CurrentApprover", Label = "Current approver" }); break;
                    default: reasons.Add(new AccessReasonDto { Rule = "OnContract", Label = rule }); break;
                }
            }
            rows.Add((contract, reasons));
        }
        result.Total = rows.Count;
        result.Rules = RuleLabels.Select(r => new AccessRuleCountDto
        {
            Rule = r.Rule,
            Label = r.Label,
            Count = rows.Count(x => x.Reasons.Any(reason => reason.Rule == r.Rule))
        }).ToList();
        var pageRows = rows.Skip((result.Page - 1) * result.PageSize).Take(result.PageSize).ToList();
        await reference.LoadExtrasAsync(_db, pageRows.Select(x => x.Contract.Id).ToList(), null, cancellationToken);
        var today = _clock.Today;
        result.Contracts = pageRows.Select(x => new EffectiveAccessContractDto
        {
            Item = ContractQueryService.ToListItem(x.Contract, reference, today),
            Reasons = x.Reasons
        }).ToList();
        return result;
    }

    // ---------- bulk reassignment ----------

    private sealed class ReassignPlan
    {
        public List<ReassignPreviewRowDto> Rows { get; } = new();
        public List<(Contract Contract, List<string> Roles)> Changes { get; } = new();
        public List<Obligation> Obligations { get; } = new();
    }

    private async Task<ReassignPlan> PlanAsync(ReassignInput input, bool tracked, CancellationToken cancellationToken)
    {
        if (input?.FromUserId is null) throw new InputValidationException("fromUserId", "Choose the person to move roles from.");
        if (input.ToUserId is null) throw new InputValidationException("toUserId", "Choose the person to move roles to.");
        var fromId = input.FromUserId.Value;
        var toId = input.ToUserId.Value;
        if (fromId == toId) throw new InputValidationException("toUserId", "Choose another person.");
        var from = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == fromId, cancellationToken)
            ?? throw new InputValidationException("fromUserId", "Unknown user.");
        var to = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == toId, cancellationToken)
            ?? throw new InputValidationException("toUserId", "Unknown user.");
        if (!to.IsActive) throw new InputValidationException("toUserId", to.Name + " is inactive.");
        var roles = (input.Roles ?? new List<string>()).Distinct().ToList();
        if (roles.Count == 0) roles = new List<string> { "Owner", "Delegate", "Responsible" };
        if (roles.Any(r => r != "Owner" && r != "Delegate" && r != "Responsible")) throw new InputValidationException("roles", "Use Owner, Delegate or Responsible.");

        var query = tracked ? _db.Contracts.AsQueryable() : _db.Contracts.AsNoTracking();
        query = query.Where(c => c.OwnerId == fromId || c.DelegateId == fromId || c.ResponsibleId == fromId);
        if (input.ContractIds is not null)
        {
            var ids = input.ContractIds.Distinct().ToList();
            query = query.Where(c => ids.Contains(c.Id));
        }
        var contracts = (await query.ToListAsync(cancellationToken)).OrderBy(c => c.ContractNo, StringComparer.Ordinal).ToList();
        var contractIds = contracts.Select(c => c.Id).ToList();
        var openSteps = await _db.ApprovalSteps.AsNoTracking()
            .Where(s => contractIds.Contains(s.ContractId) && s.ApproverId == toId
                && (s.Status == ApprovalStepStatus.Pending || s.Status == ApprovalStepStatus.Waiting))
            .Select(s => s.ContractId)
            .ToListAsync(cancellationToken);
        var canOwn = (to.Roles & (AppRole.ContractOwner | AppRole.Admin)) != AppRole.None;
        var plan = new ReassignPlan();
        foreach (var contract in contracts)
        {
            var held = new List<string>();
            if (roles.Contains("Owner") && contract.OwnerId == fromId) held.Add("Owner");
            if (roles.Contains("Delegate") && contract.DelegateId == fromId) held.Add("Delegate");
            if (roles.Contains("Responsible") && contract.ResponsibleId == fromId) held.Add("Responsible");
            if (held.Count == 0)
            {
                continue;
            }
            string? skip = null;
            var moving = new List<string>(held);
            if (contract.Status == ContractStatus.InApproval) skip = "In approval";
            else if ((contract.Status == ContractStatus.Expired || contract.Status == ContractStatus.Terminated) && input.IncludeEnded != true) skip = "Ended";
            else
            {
                if (moving.Contains("Owner") && !canOwn)
                {
                    moving.Remove("Owner");
                    skip = to.Name + " does not have the contract owner role";
                }
                if (moving.Contains("Owner") && openSteps.Contains(contract.Id))
                {
                    // Separation of duties: the owner is never an approver of the same contract.
                    moving.Remove("Owner");
                    skip = to.Name + " is an approver on an open approval of this contract";
                }
                if (moving.Contains("Delegate") && contract.OwnerId == toId && !moving.Contains("Owner"))
                {
                    moving.Remove("Delegate");
                    skip ??= to.Name + " is already the owner";
                }
                if (moving.Contains("Delegate") && contract.DelegateId == toId) { moving.Remove("Delegate"); skip ??= to.Name + " is already the delegate"; }
                if (moving.Contains("Responsible") && contract.ResponsibleId == toId) { moving.Remove("Responsible"); skip ??= to.Name + " is already the responsible"; }
                if (moving.Count > 0) skip = null;
            }
            if (skip is not null)
            {
                moving.Clear();
            }
            plan.Rows.Add(new ReassignPreviewRowDto
            {
                ContractId = contract.Id,
                ContractNo = contract.ContractNo,
                Title = contract.Title,
                Status = contract.Status.ToString(),
                Roles = moving.Count > 0 ? moving : held,
                SkipReason = skip
            });
            if (moving.Count > 0)
            {
                plan.Changes.Add((contract, moving));
            }
        }
        if (input.IncludeObligations == true && plan.Changes.Count > 0)
        {
            var changedIds = plan.Changes.Select(c => c.Contract.Id).ToList();
            var obligationQuery = tracked ? _db.Obligations.AsQueryable() : _db.Obligations.AsNoTracking();
            plan.Obligations.AddRange(await obligationQuery
                .Where(o => changedIds.Contains(o.ContractId) && o.ResponsibleId == fromId && o.Status == ObligationStatus.Open)
                .ToListAsync(cancellationToken));
        }
        return plan;
    }

    public async Task<ReassignPreviewDto> PreviewReassignAsync(ReassignInput input, CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(input, false, cancellationToken);
        return new ReassignPreviewDto { Rows = plan.Rows, Contracts = plan.Changes.Count, Obligations = plan.Obligations.Count };
    }

    public async Task<ReassignResultDto> ReassignAsync(ReassignInput input, CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(input, true, cancellationToken);
        var toId = input.ToUserId!.Value;
        var fromId = input.FromUserId!.Value;
        var reference = await ReferenceData.LoadAsync(_db, cancellationToken);
        var now = _clock.UtcNow;
        foreach (var (contract, roles) in plan.Changes)
        {
            if (roles.Contains("Owner")) contract.OwnerId = toId;
            if (roles.Contains("Delegate")) contract.DelegateId = toId;
            if (roles.Contains("Responsible")) contract.ResponsibleId = toId;
            contract.UpdatedAt = now;
            _audit.Record("contract.reassigned",
                string.Join(", ", roles) + ": " + reference.UserName(fromId) + " -> " + reference.UserName(toId) + " (administrator reassignment)", contract.Id);
        }
        foreach (var obligation in plan.Obligations)
        {
            obligation.ResponsibleId = toId;
            _audit.Record("obligation.updated", "Obligation \"" + obligation.Description + "\" moved to " + reference.UserName(toId) + " (reassignment)", obligation.ContractId);
        }
        await _db.SaveChangesAsync(cancellationToken);
        return new ReassignResultDto
        {
            Updated = plan.Changes.Count,
            ObligationsMoved = plan.Obligations.Count,
            Skipped = plan.Rows.Where(r => r.SkipReason is not null).Select(r => new ReassignSkipDto { ContractId = r.ContractId, Reason = r.SkipReason! }).ToList()
        };
    }
}
