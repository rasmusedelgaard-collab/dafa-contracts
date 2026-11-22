using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>Access tokens for Microsoft APIs used by the production-mode providers.</summary>
public interface IAccessTokenProvider
{
    Task<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default);
}

/// <summary>
/// PRODUCTION MODE. App-only (client credentials) tokens from Entra ID using AzureAd:TenantId,
/// AzureAd:ClientId and AzureAd:ClientSecret. Not used in the test environment.
/// </summary>
public sealed class ClientCredentialsTokenProvider : IAccessTokenProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, CachedToken> _cache = new();

    public ClientCredentialsTokenProvider(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    private sealed record CachedToken(string Value, DateTime ExpiresUtc);

    public async Task<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(scope, out var cached) && cached.ExpiresUtc > DateTime.UtcNow.AddMinutes(2))
        {
            return cached.Value;
        }

        var tenantId = _configuration["AzureAd:TenantId"];
        var clientId = _configuration["AzureAd:ClientId"];
        var clientSecret = _configuration["AzureAd:ClientSecret"];
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("AzureAd:TenantId, AzureAd:ClientId and AzureAd:ClientSecret must be configured.");
        }

        var client = _httpClientFactory.CreateClient("token");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = scope,
            ["grant_type"] = "client_credentials"
        });
        using var response = await client.PostAsync(
            "https://login.microsoftonline.com/" + Uri.EscapeDataString(tenantId) + "/oauth2/v2.0/token", form, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var token = json.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("The token endpoint returned no access token.");
        }
        var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 3000;
        _cache[scope] = new CachedToken(token, DateTime.UtcNow.AddSeconds(expiresIn));
        return token;
    }
}
