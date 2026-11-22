using System.Net.Http.Headers;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>TEST MODE (Directory:Provider=Seeded). Users and memberships are the seeded rows; nothing to sync.</summary>
public sealed class SeededDirectoryService : IDirectoryService
{
    public Task<int> SyncAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}

/// <summary>
/// PRODUCTION MODE (Directory:Provider=Graph). NOT exercised in the test environment.
/// First version: imports enabled member accounts from Microsoft Graph as users with the Reader
/// role (first page of up to 999 users). Mapping Entra groups to departments, entity management
/// and roles is still to be done; until then those are maintained in the database.
/// </summary>
public sealed class GraphDirectoryService : IDirectoryService
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    private readonly IAppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccessTokenProvider _tokens;

    public GraphDirectoryService(IAppDbContext db, IHttpClientFactory httpClientFactory, IAccessTokenProvider tokens)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
    }

    public async Task<int> SyncAsync(CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient("graph");
        var token = await _tokens.GetTokenAsync(GraphScope, cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://graph.microsoft.com/v1.0/users?$select=id,displayName,mail,userPrincipalName,jobTitle,accountEnabled,userType&$top=999");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        var users = await _db.Users.ToListAsync(cancellationToken);
        var changed = 0;
        if (!json.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }
        foreach (var item in value.EnumerateArray())
        {
            var externalId = Text(item, "id");
            var name = Text(item, "displayName");
            var email = Text(item, "mail") ?? Text(item, "userPrincipalName");
            var enabled = !item.TryGetProperty("accountEnabled", out var enabledElement) || enabledElement.ValueKind != JsonValueKind.False;
            var isGuest = string.Equals(Text(item, "userType"), "Guest", StringComparison.OrdinalIgnoreCase);
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
                    JobTitle = Text(item, "jobTitle"),
                    Roles = AppRole.Reader,
                    IsActive = true
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

    private static string? Text(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
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

/// <summary>TEST MODE and v1 (Signature:Provider=None). The owner marks the contract as signed manually.</summary>
public sealed class NoSignatureProvider : ISignatureProvider
{
    public string Name => "None";
    public bool IsEnabled => false;

    public Task<string> SendForSignatureAsync(Guid contractId, string documentName, Stream document, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("E-signing is not enabled. Mark the contract as signed manually.");
    }
}

/// <summary>STUB (Signature:Provider=DocuSign). Placeholder for the later DocuSign integration.</summary>
public sealed class DocuSignSignatureProvider : ISignatureProvider
{
    public string Name => "DocuSign";
    public bool IsEnabled => false;

    public Task<string> SendForSignatureAsync(Guid contractId, string documentName, Stream document, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("The DocuSign integration is not built yet.");
    }
}
