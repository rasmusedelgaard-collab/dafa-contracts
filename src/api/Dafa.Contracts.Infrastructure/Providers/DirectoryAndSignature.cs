using System.Net.Http.Headers;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>TEST MODE (Directory:Provider=Seeded). Users and memberships are the seeded rows; nothing to sync.</summary>
public sealed class SeededDirectoryService : IDirectoryService
{
    public Task<int> SyncAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}

/// <summary>
/// PRODUCTION MODE (Directory:Provider=Graph). Written and desk-checked, NEVER run against a tenant.
///
/// 1. Users: enabled member accounts are imported as users with the Reader role (all pages).
///    Needs the application permission User.Read.All (the call reads accountEnabled and userType).
/// 2. Memberships (release 4): read from the mapping the administrator made, not from a naming
///    convention:
///      Department.GroupId             -> members of that Entra group are members of the department
///      LegalEntity.ManagementGroupId  -> members of that Entra group are entity management for the entity
///    For a MAPPED department or entity the group is the source of truth for Entra-linked users
///    (users with a directory object id): members are added, and linked users who left the group
///    lose the membership. Departments and entities without a group, and users that are not linked
///    to Entra, are never touched; their memberships stay as maintained under Settings > Users.
///    A group that cannot be read is skipped (nothing is removed on a failed call).
///    Needs the application permission GroupMember.Read.All.
/// Roles (ContractOwner, Approver, Admin), group management and the department-head flag are
/// always maintained in the app.
/// </summary>
public sealed class GraphDirectoryService : IDirectoryService
{
    private readonly IAppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccessTokenProvider _tokens;
    private readonly ILogger<GraphDirectoryService> _logger;

    public GraphDirectoryService(IAppDbContext db, IHttpClientFactory httpClientFactory, IAccessTokenProvider tokens, ILogger<GraphDirectoryService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>Reads every page of a Graph collection (follows @odata.nextLink, at most 100 pages).</summary>
    private async Task<List<JsonElement>> ReadAllAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(GraphCalls.ClientName);
        var items = new List<JsonElement>();
        string? next = url;
        var pages = 0;
        while (next is not null && pages < 100)
        {
            pages++;
            using var request = GraphCalls.Request(HttpMethod.Get, next, await _tokens.GetTokenAsync(GraphCalls.Scope, cancellationToken));
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    items.Add(item.Clone());
                }
            }
            next = GraphCalls.Text(json.RootElement, "@odata.nextLink");
        }
        return items;
    }

    public async Task<int> SyncAsync(CancellationToken cancellationToken = default)
    {
        var changed = await SyncUsersAsync(cancellationToken);
        changed += await SyncMembershipsAsync(cancellationToken);
        return changed;
    }

    private async Task<int> SyncUsersAsync(CancellationToken cancellationToken)
    {
        var entries = await ReadAllAsync(
            GraphCalls.BaseUrl + "/users?$select=id,displayName,mail,userPrincipalName,jobTitle,accountEnabled,userType&$top=999", cancellationToken);
        var users = await _db.Users.ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var item in entries)
        {
            var externalId = GraphCalls.Text(item, "id");
            var name = GraphCalls.Text(item, "displayName");
            var email = GraphCalls.Text(item, "mail") ?? GraphCalls.Text(item, "userPrincipalName");
            var enabled = !item.TryGetProperty("accountEnabled", out var enabledElement) || enabledElement.ValueKind != JsonValueKind.False;
            var isGuest = string.Equals(GraphCalls.Text(item, "userType"), "Guest", StringComparison.OrdinalIgnoreCase);
            if (externalId is null || name is null || email is null || isGuest)
            {
                continue;
            }

            // Link by e-mail only to a row that has no directory id yet: a reused address must not
            // inherit the roles and memberships of its previous holder.
            var user = users.FirstOrDefault(u => u.ExternalId == externalId)
                ?? users.FirstOrDefault(u => string.IsNullOrEmpty(u.ExternalId)
                    && string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                if (!enabled)
                {
                    continue;
                }
                user = new AppUser
                {
                    Id = Guid.NewGuid(),
                    ExternalId = externalId,
                    Name = name,
                    Email = email,
                    Initials = UserNames.Initials(name),
                    JobTitle = GraphCalls.Text(item, "jobTitle"),
                    Roles = AppRole.Reader,
                    IsActive = true,
                    Source = "Entra"
                };
                _db.Users.Add(user);
                users.Add(user);
                changed++;
            }
            else if (user.ExternalId != externalId || user.Name != name || user.Email != email || user.IsActive != enabled)
            {
                user.ExternalId = externalId;
                user.Name = name;
                user.Email = email;
                user.IsActive = enabled;
                changed++;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>The directory object ids of the users in a group (nested groups included), or null when the group cannot be read.</summary>
    private async Task<HashSet<string>?> GroupMembersAsync(string groupId, string what, CancellationToken cancellationToken)
    {
        try
        {
            var members = await ReadAllAsync(
                GraphCalls.BaseUrl + "/groups/" + Uri.EscapeDataString(groupId) + "/transitiveMembers/microsoft.graph.user?$select=id&$top=999", cancellationToken);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in members)
            {
                var id = GraphCalls.Text(member, "id");
                if (id is not null)
                {
                    ids.Add(id);
                }
            }
            return ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never remove memberships because a call failed.
            _logger.LogWarning(ex, "The Entra group {Group} of {What} could not be read; its memberships are left as they are", groupId, what);
            return null;
        }
    }

    private async Task<int> SyncMembershipsAsync(CancellationToken cancellationToken)
    {
        var linked = (await _db.Users.AsNoTracking().Where(u => u.ExternalId != null && u.ExternalId != "").ToListAsync(cancellationToken))
            .ToDictionary(u => u.Id, u => u.ExternalId!);
        var changed = 0;

        var departments = await _db.Departments.AsNoTracking().Where(d => d.GroupId != null && d.GroupId != "").ToListAsync(cancellationToken);
        foreach (var department in departments)
        {
            var members = await GroupMembersAsync(department.GroupId!, department.Name, cancellationToken);
            if (members is null)
            {
                continue;
            }
            var departmentId = department.Id;
            var current = await _db.UserDepartments.Where(m => m.DepartmentId == departmentId).ToListAsync(cancellationToken);
            foreach (var pair in linked)
            {
                var isMember = members.Contains(pair.Value);
                var row = current.FirstOrDefault(m => m.UserId == pair.Key);
                if (isMember && row is null)
                {
                    _db.UserDepartments.Add(new UserDepartment { UserId = pair.Key, DepartmentId = departmentId, IsHead = false });
                    changed++;
                }
                else if (!isMember && row is not null)
                {
                    _db.UserDepartments.Remove(row);
                    changed++;
                }
            }
        }

        var entities = await _db.LegalEntities.AsNoTracking().Where(e => e.ManagementGroupId != null && e.ManagementGroupId != "").ToListAsync(cancellationToken);
        foreach (var entity in entities)
        {
            var members = await GroupMembersAsync(entity.ManagementGroupId!, entity.Name, cancellationToken);
            if (members is null)
            {
                continue;
            }
            var entityId = entity.Id;
            var current = await _db.UserEntityManagements.Where(m => m.EntityId == entityId).ToListAsync(cancellationToken);
            foreach (var pair in linked)
            {
                var isMember = members.Contains(pair.Value);
                var row = current.FirstOrDefault(m => m.UserId == pair.Key);
                if (isMember && row is null)
                {
                    _db.UserEntityManagements.Add(new UserEntityManagement { UserId = pair.Key, EntityId = entityId });
                    changed++;
                }
                else if (!isMember && row is not null)
                {
                    _db.UserEntityManagements.Remove(row);
                    changed++;
                }
            }
        }
        if (changed > 0)
        {
            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                At = DateTime.UtcNow,
                UserName = "System",
                Action = "directory.memberships-synced",
                Text = "Directory sync changed " + changed + " department or entity-management memberships from the mapped Entra groups"
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
        return changed;
    }
}

/// <summary>TEST MODE (local files and seeded directory): nothing outside the app is called; the link test only validates formats.</summary>
public sealed class NoOrgLinkProbe : IOrgLinkProbe
{
    public Task<SiteInfo?> ResolveSiteAsync(string siteUrl, CancellationToken cancellationToken = default) => Task.FromResult<SiteInfo?>(null);

    public Task<IReadOnlyList<string>> GetLibraryNamesAsync(string siteId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<string?> GetGroupNameAsync(string groupId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

/// <summary>
/// PRODUCTION MODE (Documents:Provider=SharePoint and/or Directory:Provider=Graph). NEVER run
/// against a tenant. The lookups behind "test link": the site by its address, the libraries of a
/// site, the name of a group.
/// </summary>
public sealed class GraphOrgLinkProbe : IOrgLinkProbe
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccessTokenProvider _tokens;

    public GraphOrgLinkProbe(IHttpClientFactory httpClientFactory, IAccessTokenProvider tokens)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
    }

    private async Task<JsonDocument?> GetAsync(string url, string what, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(GraphCalls.ClientName);
        using var request = GraphCalls.Request(HttpMethod.Get, url, await _tokens.GetTokenAsync(GraphCalls.Scope, cancellationToken));
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw await GraphCalls.FailureAsync(response, what, cancellationToken);
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<SiteInfo?> ResolveSiteAsync(string siteUrl, CancellationToken cancellationToken = default)
    {
        var parsed = GraphCalls.ParseSiteUrl(siteUrl);
        if (parsed is null)
        {
            return null;
        }
        using var json = await GetAsync(GraphCalls.SiteByUrl(parsed.Value.Host, parsed.Value.Name), "find the SharePoint site", cancellationToken);
        if (json is null)
        {
            return null;
        }
        var id = GraphCalls.Text(json.RootElement, "id");
        return id is null ? null : new SiteInfo(id, GraphCalls.Text(json.RootElement, "webUrl"), GraphCalls.Text(json.RootElement, "displayName"));
    }

    public async Task<IReadOnlyList<string>> GetLibraryNamesAsync(string siteId, CancellationToken cancellationToken = default)
    {
        var names = new List<string>();
        using var json = await GetAsync(GraphCalls.BaseUrl + "/sites/" + siteId + "/drives?$select=id,name&$top=200", "list the document libraries", cancellationToken);
        if (json is not null && json.RootElement.TryGetProperty("value", out var drives) && drives.ValueKind == JsonValueKind.Array)
        {
            foreach (var drive in drives.EnumerateArray())
            {
                var name = GraphCalls.Text(drive, "name");
                if (name is not null)
                {
                    names.Add(name);
                }
            }
        }
        return names;
    }

    public async Task<string?> GetGroupNameAsync(string groupId, CancellationToken cancellationToken = default)
    {
        using var json = await GetAsync(GraphCalls.BaseUrl + "/groups/" + Uri.EscapeDataString(groupId) + "?$select=id,displayName", "find the Entra group", cancellationToken);
        return json is null ? null : (GraphCalls.Text(json.RootElement, "displayName") ?? groupId);
    }
}

public static class UserNames
{
    public static string Initials(string name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "?";
        }
        if (parts.Length == 1)
        {
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        }
        return (parts[0].Substring(0, 1) + parts[parts.Length - 1].Substring(0, 1)).ToUpperInvariant();
    }
}

