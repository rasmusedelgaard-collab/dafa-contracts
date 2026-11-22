using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Core.Common;
using Microsoft.Extensions.Configuration;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>Access tokens for Microsoft APIs used by the production-mode providers.</summary>
public interface IAccessTokenProvider
{
    Task<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default);
}

/// <summary>
/// PRODUCTION MODE. App-only tokens from Entra ID for the app registration in AzureAd:*. Not used
/// in the test environment, never run against a tenant.
///
/// How the app proves who it is, is chosen with Graph:Auth (SharePoint hardening):
///   ClientSecret (default)  AzureAd:ClientSecret. Secrets expire: set an expiry alert.
///   Certificate             a certificate whose public key is uploaded to the app registration:
///                           AzureAd:ClientCertificateThumbprint (certificate store of the web
///                           app, CurrentUser/My) or AzureAd:ClientCertificatePath (+ ...Password).
///   ManagedIdentity         the web app's managed identity (no secret at all);
///                           AzureAd:ManagedIdentityClientId for a user-assigned identity.
/// Missing configuration is StorageNotConfiguredException (409 storage-not-configured); a token
/// endpoint that does not answer is StorageUnavailableException (503).
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

    /// <summary>ClientSecret, Certificate or ManagedIdentity (Graph:Auth; anything else is a configuration error).</summary>
    public static string AuthMode(IConfiguration configuration)
    {
        var value = (configuration["Graph:Auth"] ?? "").Trim();
        if (value.Length == 0 || string.Equals(value, "ClientSecret", StringComparison.OrdinalIgnoreCase)) return "ClientSecret";
        if (string.Equals(value, "Certificate", StringComparison.OrdinalIgnoreCase)) return "Certificate";
        if (string.Equals(value, "ManagedIdentity", StringComparison.OrdinalIgnoreCase)) return "ManagedIdentity";
        throw new StorageNotConfiguredException("Graph:Auth must be ClientSecret, Certificate or ManagedIdentity.");
    }

    public async Task<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(scope, out var cached) && cached.ExpiresUtc > DateTime.UtcNow.AddMinutes(2))
        {
            return cached.Value;
        }
        var mode = AuthMode(_configuration);
        JsonDocument json;
        try
        {
            json = mode == "ManagedIdentity"
                ? await ManagedIdentityAsync(scope, cancellationToken)
                : await ClientCredentialsAsync(scope, mode == "Certificate", cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new StorageUnavailableException("The token endpoint could not be reached: " + ex.Message, null, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StorageUnavailableException("The token endpoint did not answer in time.", null, ex);
        }
        using (json)
        {
            var token = json.RootElement.TryGetProperty("access_token", out var tokenElement) ? tokenElement.GetString() : null;
            if (string.IsNullOrEmpty(token))
            {
                throw new StorageNotConfiguredException("The sign-in service returned no access token for the app. Check the app registration and its credentials.");
            }
            var expiresIn = 3000;
            if (json.RootElement.TryGetProperty("expires_in", out var expires))
            {
                // Entra returns a number, the managed identity endpoint a string.
                if (expires.ValueKind == JsonValueKind.Number && expires.TryGetInt32(out var seconds)) expiresIn = seconds;
                else if (expires.ValueKind == JsonValueKind.String && int.TryParse(expires.GetString(), out var parsed)) expiresIn = parsed;
            }
            _cache[scope] = new CachedToken(token, DateTime.UtcNow.AddSeconds(expiresIn));
            return token;
        }
    }

    private async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if ((int)response.StatusCode >= 500)
        {
            throw new StorageUnavailableException("The token endpoint answered HTTP " + (int)response.StatusCode + ".");
        }
        if (!response.IsSuccessStatusCode)
        {
            // 400/401: wrong or expired secret, unknown certificate, wrong tenant. Never echo the body (it can name the client).
            throw new StorageNotConfiguredException(
                "The app could not sign in to Microsoft Graph (HTTP " + (int)response.StatusCode + "). The client secret or certificate may have expired; check the app registration.");
        }
        return JsonDocument.Parse(body);
    }

    private async Task<JsonDocument> ClientCredentialsAsync(string scope, bool useCertificate, CancellationToken cancellationToken)
    {
        var tenantId = _configuration["AzureAd:TenantId"];
        var clientId = _configuration["AzureAd:ClientId"];
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
        {
            throw new StorageNotConfiguredException("AzureAd:TenantId and AzureAd:ClientId must be configured before Microsoft Graph can be used.");
        }
        var endpoint = "https://login.microsoftonline.com/" + Uri.EscapeDataString(tenantId) + "/oauth2/v2.0/token";
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = scope,
            ["grant_type"] = "client_credentials"
        };
        if (useCertificate)
        {
            using var certificate = LoadCertificate();
            form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            form["client_assertion"] = BuildClientAssertion(certificate, clientId, endpoint, DateTime.UtcNow);
        }
        else
        {
            var clientSecret = _configuration["AzureAd:ClientSecret"];
            if (string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new StorageNotConfiguredException("AzureAd:ClientSecret must be configured (or set Graph:Auth to Certificate or ManagedIdentity).");
            }
            form["client_secret"] = clientSecret;
        }
        var client = _httpClientFactory.CreateClient("token");
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(endpoint, content, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    private X509Certificate2 LoadCertificate()
    {
        var thumbprint = (_configuration["AzureAd:ClientCertificateThumbprint"] ?? "").Replace(" ", "").Trim();
        var path = _configuration["AzureAd:ClientCertificatePath"];
        if (thumbprint.Length > 0)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
            if (found.Count > 0 && found[0].HasPrivateKey)
            {
                return found[0];
            }
            throw new StorageNotConfiguredException(
                "The certificate with thumbprint AzureAd:ClientCertificateThumbprint was not found with its private key in the web app's certificate store.");
        }
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(path, _configuration["AzureAd:ClientCertificatePassword"]);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is IOException)
            {
                throw new StorageNotConfiguredException("The certificate file in AzureAd:ClientCertificatePath could not be read.");
            }
        }
        throw new StorageNotConfiguredException("Graph:Auth is Certificate, but neither AzureAd:ClientCertificateThumbprint nor AzureAd:ClientCertificatePath is set.");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The signed JWT Entra accepts instead of a client secret (RS256; header x5t = SHA-1 thumbprint
    /// of the certificate; audience = the token endpoint; issuer and subject = the client id; valid 10 minutes).
    /// </summary>
    public static string BuildClientAssertion(X509Certificate2 certificate, string clientId, string tokenEndpoint, DateTime utcNow)
    {
        using var key = certificate.GetRSAPrivateKey()
            ?? throw new StorageNotConfiguredException("The client certificate has no RSA private key.");
        var header = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["x5t"] = Base64Url(certificate.GetCertHash())
        });
        var issued = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["aud"] = tokenEndpoint,
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["jti"] = Guid.NewGuid().ToString(),
            ["nbf"] = issued - 60,
            ["iat"] = issued,
            ["exp"] = issued + 600
        });
        var unsigned = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(payload));
        var signature = key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Base64Url(signature);
    }

    /// <summary>App Service / Container Apps managed identity endpoint (IDENTITY_ENDPOINT + IDENTITY_HEADER).</summary>
    private async Task<JsonDocument> ManagedIdentityAsync(string scope, CancellationToken cancellationToken)
    {
        var endpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
        var header = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(header))
        {
            throw new StorageNotConfiguredException("Graph:Auth is ManagedIdentity, but this host has no managed identity (IDENTITY_ENDPOINT is not set).");
        }
        // The endpoint takes a resource, not a scope: "https://graph.microsoft.com/.default" -> "https://graph.microsoft.com".
        var resource = scope.EndsWith("/.default", StringComparison.Ordinal) ? scope.Substring(0, scope.Length - "/.default".Length) : scope;
        var url = endpoint + "?resource=" + Uri.EscapeDataString(resource) + "&api-version=2019-08-01";
        var clientId = _configuration["AzureAd:ManagedIdentityClientId"];
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            url += "&client_id=" + Uri.EscapeDataString(clientId);
        }
        var client = _httpClientFactory.CreateClient("token");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-IDENTITY-HEADER", header);
        using var response = await client.SendAsync(request, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }
}
