using System.Net.Http.Headers;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>TEST MODE (Documents:Provider=Local). Files on disk under App_Data/files.</summary>
public sealed class LocalDocumentStore : IDocumentStore
{
    private readonly string _root;

    public LocalDocumentStore(string rootPath)
    {
        _root = Path.GetFullPath(rootPath);
    }

    private string Resolve(string storeKey)
    {
        var relative = storeKey.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid document key.");
        }
        return full;
    }

    public async Task<StoredDocument> SaveAsync(DocumentLocation location, string fileName, int version, Stream content, CancellationToken cancellationToken = default)
    {
        // A random part in the key and CreateNew: two uploads can never write to the same file.
        var key = location.ContractId.ToString("N") + "/v" + version + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + fileName;
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long size;
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(file, cancellationToken);
            size = file.Length;
        }
        return new StoredDocument(key, size);
    }

    public Task<Stream> OpenReadAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        var path = Resolve(storeKey);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The stored file is missing.", storeKey);
        }
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        var path = Resolve(storeKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetFolderUrlAsync(DocumentLocation location, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }
}

/// <summary>
/// PRODUCTION MODE (Documents:Provider=SharePoint). NOT exercised in the test environment.
/// Site per legal entity (SharePoint:Sites), document library per department, folder per contract,
/// through Microsoft Graph REST. Known limits of this first version: app-only token instead of
/// on-behalf-of-user, simple upload (files up to about 4 MB; larger files need an upload session).
/// </summary>
public sealed class SharePointDocumentStore : IDocumentStore
{
    private const string GraphScope = "https://graph.microsoft.com/.default";
    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccessTokenProvider _tokens;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SharePointDocumentStore> _logger;

    public SharePointDocumentStore(
        IHttpClientFactory httpClientFactory, IAccessTokenProvider tokens, IConfiguration configuration,
        ILogger<SharePointDocumentStore> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _configuration = configuration;
        _logger = logger;
    }

    private async Task<HttpClient> CreateClientAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("graph");
        var token = await _tokens.GetTokenAsync(GraphScope, cancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private string GetSiteId(string entityName)
    {
        foreach (var site in _configuration.GetSection("SharePoint:Sites").GetChildren())
        {
            if (string.Equals(site["Entity"], entityName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(site["SiteId"]))
            {
                return site["SiteId"]!;
            }
        }
        var fallback = _configuration["SharePoint:DefaultSiteId"];
        if (string.IsNullOrWhiteSpace(fallback))
        {
            throw new InvalidOperationException("No SharePoint site is configured for " + entityName + " (SharePoint:Sites or SharePoint:DefaultSiteId).");
        }
        return fallback;
    }

    /// <summary>Finds the document library (drive) named after the department; falls back to the site's default library.</summary>
    private async Task<string> GetDriveIdAsync(HttpClient client, string siteId, string departmentName, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(GraphBase + "/sites/" + siteId + "/drives?$select=id,name", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        string? first = null;
        if (json.RootElement.TryGetProperty("value", out var drives) && drives.ValueKind == JsonValueKind.Array)
        {
            foreach (var drive in drives.EnumerateArray())
            {
                var id = drive.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                var name = drive.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (id is null)
                {
                    continue;
                }
                first ??= id;
                if (string.Equals(name, departmentName, StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }
        }
        if (first is null)
        {
            throw new InvalidOperationException("The SharePoint site has no document libraries.");
        }
        _logger.LogWarning("No document library named {Department} on site {Site}; using the default library", departmentName, siteId);
        return first;
    }

    private static string FolderName(DocumentLocation location) => Uri.EscapeDataString(location.ContractNo);

    public async Task<StoredDocument> SaveAsync(DocumentLocation location, string fileName, int version, Stream content, CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync(cancellationToken);
        var driveId = await GetDriveIdAsync(client, GetSiteId(location.EntityName), location.DepartmentName, cancellationToken);
        var storedName = version > 1
            ? Path.GetFileNameWithoutExtension(fileName) + " v" + version + Path.GetExtension(fileName)
            : fileName;
        var url = GraphBase + "/drives/" + driveId + "/root:/" + FolderName(location) + "/" + Uri.EscapeDataString(storedName) + ":/content";

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        using var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PutAsync(url, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var itemId = json.RootElement.GetProperty("id").GetString();
        return new StoredDocument("sp:" + driveId + ":" + itemId, bytes.LongLength);
    }

    private static (string DriveId, string ItemId) ParseKey(string storeKey)
    {
        var parts = storeKey.Split(':');
        if (parts.Length != 3 || parts[0] != "sp")
        {
            throw new InvalidOperationException("Not a SharePoint document key.");
        }
        return (parts[1], parts[2]);
    }

    public async Task<Stream> OpenReadAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        var (driveId, itemId) = ParseKey(storeKey);
        var client = await CreateClientAsync(cancellationToken);
        using var response = await client.GetAsync(GraphBase + "/drives/" + driveId + "/items/" + itemId + "/content", cancellationToken);
        response.EnsureSuccessStatusCode();
        var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        return buffer;
    }

    public async Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        var (driveId, itemId) = ParseKey(storeKey);
        var client = await CreateClientAsync(cancellationToken);
        using var response = await client.DeleteAsync(GraphBase + "/drives/" + driveId + "/items/" + itemId, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string?> GetFolderUrlAsync(DocumentLocation location, CancellationToken cancellationToken = default)
    {
        var client = await CreateClientAsync(cancellationToken);
        var driveId = await GetDriveIdAsync(client, GetSiteId(location.EntityName), location.DepartmentName, cancellationToken);
        using var response = await client.GetAsync(GraphBase + "/drives/" + driveId + "/root:/" + FolderName(location) + "?$select=webUrl", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.TryGetProperty("webUrl", out var webUrl) ? webUrl.GetString() : null;
    }
}
