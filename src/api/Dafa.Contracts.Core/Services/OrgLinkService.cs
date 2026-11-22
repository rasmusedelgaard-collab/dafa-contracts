using System.Text.RegularExpressions;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

/// <summary>
/// Release 4: validation and mapping of the SharePoint / Entra fields on legal entities and
/// departments (CONTRACT_V4 section 7). Shared by the admin services that save them.
/// </summary>
public static class OrgLinkRules
{
    public const string TestModeDetail = "Test mode: saved, used in SharePoint mode";

    private static readonly Regex SiteUrlPattern = new(
        @"^https://[a-z0-9][a-z0-9-]*\.sharepoint\.com/sites/[A-Za-z0-9._~-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The URL without a trailing slash, or null when empty. 400 on sharePointSiteUrl when malformed.</summary>
    public static string? NormalizeSiteUrl(string? value, Dictionary<string, string[]> errors)
    {
        var text = (value ?? "").Trim().TrimEnd('/');
        if (text.Length == 0)
        {
            return null;
        }
        if (text.Length > 400 || !SiteUrlPattern.IsMatch(text))
        {
            ContractValidator.Add(errors, "sharePointSiteUrl", "Use the address of the site, like https://<tenant>.sharepoint.com/sites/<name>.");
            return null;
        }
        return text;
    }

    public static bool IsSiteUrl(string? value) => !string.IsNullOrWhiteSpace(value) && SiteUrlPattern.IsMatch(value.Trim().TrimEnd('/'));

    /// <summary>The Entra object id in canonical form, or null when empty. 400 on the field when it is not a GUID.</summary>
    public static string? NormalizeGroupId(string? value, string field, Dictionary<string, string[]> errors)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }
        if (!Guid.TryParse(text, out var id) || id == Guid.Empty)
        {
            ContractValidator.Add(errors, field, "Use the object id of the Entra group (a GUID).");
            return null;
        }
        return id.ToString();
    }

    /// <summary>A document library name, or null when empty. 400 on documentLibrary for characters SharePoint does not allow.</summary>
    public static string? NormalizeLibrary(string? value, Dictionary<string, string[]> errors)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }
        if (text.Length > 200 || text.IndexOfAny(new[] { '"', '*', ':', '<', '>', '?', '/', '\\', '|', '#', '%' }) >= 0 || text.Any(char.IsControl))
        {
            ContractValidator.Add(errors, "documentLibrary", "Use the name of the document library (at most 200 characters, without \" * : < > ? / \\ | # %).");
            return null;
        }
        return text;
    }

    public static string? NormalizeName(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }
        return text.Length > 200 ? text.Substring(0, 200) : text;
    }

    public static LinkStatusDto ToDto(string? status, DateTime? checkedAt, string? message) => new()
    {
        State = LinkStates.Normalize(status),
        CheckedAt = checkedAt,
        Message = message
    };

    public static LinkStatusDto ToDto(LegalEntity entity) => ToDto(entity.LinkStatus, entity.LinkCheckedAt, entity.LinkMessage);

    public static LinkStatusDto ToDto(Department department) => ToDto(department.LinkStatus, department.LinkCheckedAt, department.LinkMessage);

    /// <summary>
    /// Applies the mapping fields to an entity. Changing the site or the group resets the link to
    /// NotLinked (and forgets the resolved site id / group name), so a stale "Linked" never survives.
    /// Returns true when something changed.
    /// </summary>
    public static bool Apply(LegalEntity entity, string? siteUrl, string? groupId, string? groupName)
    {
        var siteChanged = !string.Equals(entity.SharePointSiteUrl ?? "", siteUrl ?? "", StringComparison.OrdinalIgnoreCase);
        var groupChanged = !string.Equals(entity.ManagementGroupId ?? "", groupId ?? "", StringComparison.OrdinalIgnoreCase);
        if (siteChanged)
        {
            entity.SharePointSiteUrl = siteUrl;
            entity.SharePointSiteId = null;
        }
        if (groupChanged)
        {
            entity.ManagementGroupId = groupId;
            entity.ManagementGroupName = groupId is null ? null : groupName;
        }
        else if (groupId is not null && groupName is not null)
        {
            entity.ManagementGroupName = groupName;
        }
        if (siteChanged || groupChanged)
        {
            entity.LinkStatus = LinkStates.NotLinked;
            entity.LinkCheckedAt = null;
            entity.LinkMessage = null;
        }
        return siteChanged || groupChanged;
    }

    public static bool Apply(Department department, string? library, string? groupId, string? groupName)
    {
        var libraryChanged = !string.Equals(department.LibraryName ?? "", library ?? "", StringComparison.Ordinal);
        var groupChanged = !string.Equals(department.GroupId ?? "", groupId ?? "", StringComparison.OrdinalIgnoreCase);
        if (libraryChanged)
        {
            department.LibraryName = library;
        }
        if (groupChanged)
        {
            department.GroupId = groupId;
            department.GroupName = groupId is null ? null : groupName;
        }
        else if (groupId is not null && groupName is not null)
        {
            department.GroupName = groupName;
        }
        if (libraryChanged || groupChanged)
        {
            department.LinkStatus = LinkStates.NotLinked;
            department.LinkCheckedAt = null;
            department.LinkMessage = null;
        }
        return libraryChanged || groupChanged;
    }

    /// <summary>The library a department's documents go to on every entity site: its own name unless one is set.</summary>
    public static string LibraryOf(Department department) =>
        string.IsNullOrWhiteSpace(department.LibraryName) ? department.Name : department.LibraryName!;
}

/// <summary>
/// Release 4: "test link" for legal entities and departments and the access-mapping matrix.
/// How things are linked (also in docs/RUNBOOK.md):
///   legal entity  &lt;-&gt; one SharePoint site (SharePointSiteUrl, resolved to SharePointSiteId)
///                 &lt;-&gt; one Entra group whose members are the entity's management (ManagementGroupId)
///   department    &lt;-&gt; one document library with the same name on every entity site (LibraryName)
///                 &lt;-&gt; one Entra group whose members are the department's members (GroupId)
/// In test mode (local files, seeded directory) nothing outside the app is called.
/// </summary>
public class OrgLinkService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly AuditService _audit;
    private readonly IOrgLinkProbe _probe;
    private readonly RuntimeModes _modes;

    public OrgLinkService(IAppDbContext db, IClock clock, AuditService audit, IOrgLinkProbe probe, RuntimeModes modes)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
        _probe = probe;
        _modes = modes;
    }

    private bool IsTestMode => !_modes.IsSharePoint && !_modes.IsGraphDirectory;

    private static string Short(string? text)
    {
        var value = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length > 300 ? value.Substring(0, 300) : value;
    }

    private static string Join(IEnumerable<string> parts)
    {
        var text = string.Join(" ", parts);
        return text.Length > 1000 ? text.Substring(0, 1000) : text;
    }

    /// <summary>POST admin/entities/{id}/test-link.</summary>
    public async Task<LinkTestResultDto> TestEntityAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        var entity = await _db.LegalEntities.FirstOrDefaultAsync(e => e.Id == entityId, cancellationToken)
            ?? throw new NotFoundException("Legal entity not found.");
        var details = new List<string>();
        var problems = new List<string>();
        var state = LinkStates.Linked;
        var hasSite = !string.IsNullOrWhiteSpace(entity.SharePointSiteUrl);
        var hasGroup = !string.IsNullOrWhiteSpace(entity.ManagementGroupId);
        Exception? crash = null;

        if (!hasSite && !hasGroup)
        {
            state = LinkStates.NotLinked;
            details.Add("No SharePoint site and no management group have been entered.");
        }
        else
        {
            if (hasSite && !OrgLinkRules.IsSiteUrl(entity.SharePointSiteUrl))
            {
                problems.Add("The SharePoint site address is not of the form https://<tenant>.sharepoint.com/sites/<name>.");
            }
            if (hasGroup && !Guid.TryParse(entity.ManagementGroupId, out _))
            {
                problems.Add("The management group id is not a GUID.");
            }
            if (IsTestMode)
            {
                details.Add(OrgLinkRules.TestModeDetail);
            }
            try
            {
                if (_modes.IsSharePoint && problems.Count == 0)
                {
                    if (!hasSite)
                    {
                        problems.Add("No SharePoint site: documents of this legal entity cannot be stored.");
                    }
                    else
                    {
                        var site = await _probe.ResolveSiteAsync(entity.SharePointSiteUrl!, cancellationToken);
                        if (site is null)
                        {
                            entity.SharePointSiteId = null;
                            problems.Add("The SharePoint site was not found, or the app registration has no access to it.");
                        }
                        else
                        {
                            entity.SharePointSiteId = site.SiteId;
                            details.Add("Site found: " + (site.DisplayName ?? site.WebUrl ?? site.SiteId) + ".");
                            var libraries = await _probe.GetLibraryNamesAsync(site.SiteId, cancellationToken);
                            var id = entity.Id;
                            var usedDepartments = (await _db.Contracts.AsNoTracking().Where(c => c.EntityId == id).Select(c => c.DepartmentId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
                            var departments = await _db.Departments.AsNoTracking().Where(d => d.IsActive).ToListAsync(cancellationToken);
                            foreach (var department in departments.OrderBy(d => d.SortOrder).ThenBy(d => d.Name, StringComparer.Ordinal))
                            {
                                var library = OrgLinkRules.LibraryOf(department);
                                if (libraries.Contains(library, StringComparer.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                if (usedDepartments.Contains(department.Id))
                                {
                                    problems.Add("The document library \"" + library + "\" (" + department.Name + ") is missing; this legal entity has contracts in that department.");
                                }
                                else
                                {
                                    details.Add("The document library \"" + library + "\" (" + department.Name + ") does not exist yet; create it before contracts are registered there.");
                                }
                            }
                        }
                    }
                }
                else if (!_modes.IsSharePoint && !IsTestMode && hasSite)
                {
                    details.Add("Documents are stored locally: the site is saved and used in SharePoint mode.");
                }
                if (_modes.IsGraphDirectory && problems.Count == 0)
                {
                    if (!hasGroup)
                    {
                        details.Add("No management group: entity management for this legal entity is maintained under Users.");
                    }
                    else
                    {
                        var groupName = await _probe.GetGroupNameAsync(entity.ManagementGroupId!, cancellationToken);
                        if (groupName is null)
                        {
                            problems.Add("The Entra group was not found.");
                        }
                        else
                        {
                            entity.ManagementGroupName = OrgLinkRules.NormalizeName(groupName);
                            details.Add("Management group found: " + groupName + ".");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not AppException)
            {
                crash = ex;
                problems.Add("The check could not be completed: " + Short(ex.Message));
            }
            if (problems.Count > 0)
            {
                state = LinkStates.Error;
            }
        }

        details.InsertRange(0, problems);
        entity.LinkStatus = state;
        entity.LinkCheckedAt = _clock.UtcNow;
        entity.LinkMessage = state == LinkStates.Error ? Join(problems) : (details.Count == 0 ? null : Join(details.Take(1)));
        _audit.Record("entity.link-tested", "Tested the SharePoint and Entra link of " + entity.Name + ": " + state
            + (state == LinkStates.Error ? " (" + Join(problems) + ")" : ""));
        await _db.SaveChangesAsync(cancellationToken);
        if (crash is not null)
        {
            throw new ExternalCallException("The link test could not reach Microsoft Graph: " + Short(crash.Message));
        }
        return new LinkTestResultDto
        {
            Link = OrgLinkRules.ToDto(entity),
            SiteId = entity.SharePointSiteId,
            GroupName = entity.ManagementGroupName,
            Details = details
        };
    }

    /// <summary>POST admin/departments/{id}/test-link.</summary>
    public async Task<LinkTestResultDto> TestDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default)
    {
        var department = await _db.Departments.FirstOrDefaultAsync(d => d.Id == departmentId, cancellationToken)
            ?? throw new NotFoundException("Department not found.");
        var details = new List<string>();
        var problems = new List<string>();
        var library = OrgLinkRules.LibraryOf(department);
        var hasGroup = !string.IsNullOrWhiteSpace(department.GroupId);
        Exception? crash = null;

        if (hasGroup && !Guid.TryParse(department.GroupId, out _))
        {
            problems.Add("The group id is not a GUID.");
        }
        if (IsTestMode)
        {
            details.Add(OrgLinkRules.TestModeDetail);
            details.Add("Document library: \"" + library + "\"" + (string.IsNullOrWhiteSpace(department.LibraryName) ? " (the department name)." : "."));
        }
        try
        {
            if (_modes.IsSharePoint && problems.Count == 0)
            {
                var entities = await _db.LegalEntities.Where(e => e.IsActive).ToListAsync(cancellationToken);
                var checkedSites = 0;
                foreach (var entity in entities.OrderBy(e => e.SortOrder).ThenBy(e => e.Name, StringComparer.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(entity.SharePointSiteUrl))
                    {
                        details.Add(entity.Name + ": no SharePoint site.");
                        continue;
                    }
                    var siteId = entity.SharePointSiteId;
                    if (string.IsNullOrWhiteSpace(siteId))
                    {
                        var site = await _probe.ResolveSiteAsync(entity.SharePointSiteUrl!, cancellationToken);
                        if (site is null)
                        {
                            details.Add(entity.Name + ": the SharePoint site was not found.");
                            continue;
                        }
                        siteId = site.SiteId;
                        entity.SharePointSiteId = siteId;
                    }
                    checkedSites++;
                    var libraries = await _probe.GetLibraryNamesAsync(siteId!, cancellationToken);
                    if (libraries.Contains(library, StringComparer.OrdinalIgnoreCase))
                    {
                        details.Add(entity.Name + ": library \"" + library + "\" found.");
                    }
                    else
                    {
                        problems.Add(entity.Name + ": the document library \"" + library + "\" is missing.");
                    }
                }
                if (checkedSites == 0)
                {
                    problems.Add("No legal entity has a SharePoint site yet, so the library could not be checked.");
                }
            }
            if (_modes.IsGraphDirectory && problems.Count == 0)
            {
                if (!hasGroup)
                {
                    details.Add("No Entra group: the members of this department are maintained under Users.");
                }
                else
                {
                    var groupName = await _probe.GetGroupNameAsync(department.GroupId!, cancellationToken);
                    if (groupName is null)
                    {
                        problems.Add("The Entra group was not found.");
                    }
                    else
                    {
                        department.GroupName = OrgLinkRules.NormalizeName(groupName);
                        details.Add("Group found: " + groupName + ".");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not AppException)
        {
            crash = ex;
            problems.Add("The check could not be completed: " + Short(ex.Message));
        }

        var state = problems.Count > 0 ? LinkStates.Error : LinkStates.Linked;
        details.InsertRange(0, problems);
        department.LinkStatus = state;
        department.LinkCheckedAt = _clock.UtcNow;
        department.LinkMessage = state == LinkStates.Error ? Join(problems) : (details.Count == 0 ? null : Join(details.Take(1)));
        _audit.Record("department.link-tested", "Tested the library and group link of " + department.Name + ": " + state
            + (state == LinkStates.Error ? " (" + Join(problems) + ")" : ""));
        await _db.SaveChangesAsync(cancellationToken);
        if (crash is not null)
        {
            throw new ExternalCallException("The link test could not reach Microsoft Graph: " + Short(crash.Message));
        }
        return new LinkTestResultDto
        {
            Link = OrgLinkRules.ToDto(department),
            SiteId = null,
            GroupName = department.GroupName,
            Details = details
        };
    }

    /// <summary>
    /// GET admin/access-mapping: per legal entity x department, where documents go (site, library)
    /// and which Entra groups give access, with warnings for everything that is not mapped.
    /// </summary>
    public async Task<AccessMappingDto> GetAccessMappingAsync(CancellationToken cancellationToken = default)
    {
        var entities = (await _db.LegalEntities.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        var departments = (await _db.Departments.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
        var pairs = (await _db.Contracts.AsNoTracking().Select(c => new { c.EntityId, c.DepartmentId }).ToListAsync(cancellationToken))
            .GroupBy(c => (c.EntityId, c.DepartmentId))
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new AccessMappingDto
        {
            Mode = _modes.IsSharePoint ? "SharePoint" : "Local",
            Entities = entities.Where(e => e.IsActive || pairs.Keys.Any(k => k.EntityId == e.Id)).Select(e => new IdNameDto(e.Id, e.Name)).ToList(),
            Departments = departments.Where(d => d.IsActive || pairs.Keys.Any(k => k.DepartmentId == d.Id)).Select(d => new IdNameDto(d.Id, d.Name)).ToList()
        };

        foreach (var entity in entities.Where(e => result.Entities.Any(x => x.Id == e.Id)))
        {
            var contractCount = pairs.Where(p => p.Key.EntityId == entity.Id).Sum(p => p.Value);
            if (string.IsNullOrWhiteSpace(entity.SharePointSiteUrl))
            {
                result.Warnings.Add(entity.Name + ": no SharePoint site" + (contractCount > 0 ? " (" + contractCount + " contracts)" : ""));
            }
            else if (LinkStates.Normalize(entity.LinkStatus) == LinkStates.Error)
            {
                result.Warnings.Add(entity.Name + ": the last link test failed" + (string.IsNullOrWhiteSpace(entity.LinkMessage) ? "" : " (" + entity.LinkMessage + ")"));
            }
            else if (_modes.IsSharePoint && string.IsNullOrWhiteSpace(entity.SharePointSiteId))
            {
                result.Warnings.Add(entity.Name + ": the SharePoint site has not been tested yet");
            }
            if (string.IsNullOrWhiteSpace(entity.ManagementGroupId))
            {
                result.Warnings.Add(entity.Name + ": no management group");
            }
        }
        foreach (var department in departments.Where(d => result.Departments.Any(x => x.Id == d.Id)))
        {
            if (string.IsNullOrWhiteSpace(department.LibraryName))
            {
                result.Warnings.Add(department.Name + ": no document library (the department name is used)");
            }
            if (LinkStates.Normalize(department.LinkStatus) == LinkStates.Error)
            {
                result.Warnings.Add(department.Name + ": the last link test failed" + (string.IsNullOrWhiteSpace(department.LinkMessage) ? "" : " (" + department.LinkMessage + ")"));
            }
            if (string.IsNullOrWhiteSpace(department.GroupId))
            {
                result.Warnings.Add(department.Name + ": no Entra group");
            }
        }

        foreach (var entity in entities)
        {
            foreach (var department in departments)
            {
                pairs.TryGetValue((entity.Id, department.Id), out var count);
                // A pair is listed when it has contracts, or when both sides have been mapped explicitly.
                var configured = entity.IsActive && department.IsActive
                    && !string.IsNullOrWhiteSpace(entity.SharePointSiteUrl)
                    && (!string.IsNullOrWhiteSpace(department.LibraryName) || !string.IsNullOrWhiteSpace(department.GroupId));
                if (count == 0 && !configured)
                {
                    continue;
                }
                var cell = new AccessMappingCellDto
                {
                    EntityId = entity.Id,
                    DepartmentId = department.Id,
                    SiteUrl = string.IsNullOrWhiteSpace(entity.SharePointSiteUrl) ? null : entity.SharePointSiteUrl,
                    Library = string.IsNullOrWhiteSpace(entity.SharePointSiteUrl) ? null : OrgLinkRules.LibraryOf(department),
                    EntityGroupName = string.IsNullOrWhiteSpace(entity.ManagementGroupId) ? null : (entity.ManagementGroupName ?? entity.ManagementGroupId),
                    DepartmentGroupName = string.IsNullOrWhiteSpace(department.GroupId) ? null : (department.GroupName ?? department.GroupId),
                    ContractCount = count
                };
                if (cell.SiteUrl is null)
                {
                    cell.Warnings.Add("No SharePoint site for " + entity.Name);
                    if (count > 0)
                    {
                        result.Warnings.Add(count + (count == 1 ? " contract in " : " contracts in ") + entity.Name + " / " + department.Name + (count == 1 ? " has no site" : " have no site"));
                    }
                }
                if (cell.EntityGroupName is null)
                {
                    cell.Warnings.Add("No management group for " + entity.Name);
                }
                if (cell.DepartmentGroupName is null)
                {
                    cell.Warnings.Add("No Entra group for " + department.Name);
                }
                if (!entity.IsActive)
                {
                    cell.Warnings.Add(entity.Name + " is inactive");
                }
                if (!department.IsActive)
                {
                    cell.Warnings.Add(department.Name + " is inactive");
                }
                result.Cells.Add(cell);
            }
        }
        return result;
    }
}
