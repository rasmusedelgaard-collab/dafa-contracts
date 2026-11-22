using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
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
/// PRODUCTION MODE (Documents:Provider=SharePoint). Written and desk-checked, NEVER run against a tenant.
///
/// Access model ("the app is the only door"): the app reaches the contract sites app-only
/// (application permission Sites.Selected, granted per site); users have no direct access to the
/// libraries and every file goes through the API, where the app's own access model decides.
///
/// Where a file goes is NOT a naming convention and NOT configuration: the DocumentLocation carries
/// the mapping the administrator made (site of the legal entity, library of the department).
/// When the site is not linked or the library does not exist the upload is refused with
/// StorageNotConfiguredException; there is no default site and no "first library".
///
/// Rules of this store:
/// - one folder per contract number, created explicitly and idempotently ("create, or use the existing one");
/// - never overwrite: uploads use conflictBehavior=fail; a taken name gets a short random suffix;
/// - files are addressed by drive id + item id afterwards (renames and moves inside a library do not break links);
/// - up to 10 MB one PUT, above that an upload session in 5 MB parts;
/// - downloads are streamed, not buffered;
/// - every call goes through the shared Graph client (retry with Retry-After, timeouts, circuit breaker).
/// </summary>
public sealed class SharePointDocumentStore : IDocumentStore
{
    /// <summary>Above this size an upload session is used (Microsoft recommends it above 10 MB).</summary>
    public const int SimpleUploadLimit = 10 * 1024 * 1024;
    /// <summary>Part size of an upload session: a multiple of 320 KiB, as Graph requires.</summary>
    public const int ChunkSize = 16 * 320 * 1024;

    // Library (drive) ids per site and library name; they do not change, and one lookup per upload is avoidable.
    private static readonly ConcurrentDictionary<string, (string DriveId, DateTime CachedAt)> DriveCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccessTokenProvider _tokens;
    private readonly ILogger<SharePointDocumentStore> _logger;

    public SharePointDocumentStore(IHttpClientFactory httpClientFactory, IAccessTokenProvider tokens, ILogger<SharePointDocumentStore> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _logger = logger;
    }

    private HttpClient Client() => _httpClientFactory.CreateClient(GraphCalls.ClientName);

    private Task<string> TokenAsync(CancellationToken cancellationToken) => _tokens.GetTokenAsync(GraphCalls.Scope, cancellationToken);

    private static string Entity(DocumentLocation location) =>
        string.IsNullOrWhiteSpace(location.EntityName) ? "the legal entity" : location.EntityName;

    /// <summary>The mapped site of the location. No fallback: an unmapped legal entity cannot store files.</summary>
    private async Task<string> ResolveSiteIdAsync(DocumentLocation location, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(location.SiteId))
        {
            return location.SiteId!;
        }
        var parsed = GraphCalls.ParseSiteUrl(location.SiteUrl);
        if (parsed is null)
        {
            throw new StorageNotConfiguredException(
                "No SharePoint site is linked to " + Entity(location) + ", so its documents cannot be stored. An administrator links the site under Settings > Legal entities.");
        }
        // Linked but not tested yet: resolve the mapped address (this is the mapping, not a fallback).
        using var request = GraphCalls.Request(HttpMethod.Get, GraphCalls.SiteByUrl(parsed.Value.Host, parsed.Value.Name), await TokenAsync(cancellationToken));
        using var response = await Client().SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new StorageNotConfiguredException(
                "The SharePoint site linked to " + Entity(location) + " was not found. An administrator checks the address under Settings > Legal entities.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw await GraphCalls.FailureAsync(response, "find the site of " + Entity(location), cancellationToken);
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return GraphCalls.Text(json.RootElement, "id")
            ?? throw new StorageNotConfiguredException("The SharePoint site linked to " + Entity(location) + " could not be identified.");
    }

    /// <summary>The library named in the mapping (the department's library name, by default the department name). Exact match only.</summary>
    private async Task<(string DriveId, string Library)> ResolveDriveAsync(string siteId, DocumentLocation location, CancellationToken cancellationToken)
    {
        var library = string.IsNullOrWhiteSpace(location.LibraryName) ? location.DepartmentName : location.LibraryName!;
        if (string.IsNullOrWhiteSpace(library))
        {
            throw new StorageNotConfiguredException("The contract's department has no document library. An administrator sets it under Settings > Departments.");
        }
        var cacheKey = siteId + "|" + library;
        if (DriveCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.CachedAt < TimeSpan.FromHours(1))
        {
            return (cached.DriveId, library);
        }
        var url = GraphCalls.BaseUrl + "/sites/" + siteId + "/drives?$select=id,name&$top=200";
        using var request = GraphCalls.Request(HttpMethod.Get, url, await TokenAsync(cancellationToken));
        using var response = await Client().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await GraphCalls.FailureAsync(response, "list the document libraries of " + Entity(location), cancellationToken);
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (json.RootElement.TryGetProperty("value", out var drives) && drives.ValueKind == JsonValueKind.Array)
        {
            foreach (var drive in drives.EnumerateArray())
            {
                var id = GraphCalls.Text(drive, "id");
                var name = GraphCalls.Text(drive, "name");
                if (id is not null && string.Equals(name, library, StringComparison.OrdinalIgnoreCase))
                {
                    DriveCache[cacheKey] = (id, DateTime.UtcNow);
                    return (id, library);
                }
            }
        }
        throw new StorageNotConfiguredException(
            "The document library \"" + library + "\" does not exist on the SharePoint site of " + Entity(location)
            + ". An administrator creates it or corrects the library name under Settings > Departments (see Settings > Access mapping).");
    }

    /// <summary>Creates the contract's folder, or uses the one that exists. Safe to call any number of times.</summary>
    private async Task<StoreFolder> EnsureFolderAsync(string driveId, string contractNo, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(cancellationToken);
        var body = new Dictionary<string, object>
        {
            ["name"] = contractNo,
            ["folder"] = new Dictionary<string, object>(),
            ["@microsoft.graph.conflictBehavior"] = "fail"
        };
        using (var create = GraphCalls.Request(HttpMethod.Post, GraphCalls.BaseUrl + "/drives/" + driveId + "/root/children", token, GraphCalls.Json(body)))
        using (var created = await Client().SendAsync(create, cancellationToken))
        {
            if (created.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
                var id = GraphCalls.Text(json.RootElement, "id") ?? throw new InvalidOperationException("Microsoft Graph returned a folder without an id.");
                return new StoreFolder(driveId, id, GraphCalls.Text(json.RootElement, "webUrl"));
            }
            if (created.StatusCode != HttpStatusCode.Conflict)
            {
                throw await GraphCalls.FailureAsync(created, "create the folder " + contractNo, cancellationToken);
            }
        }
        // 409: the folder is already there. Use it.
        var url = GraphCalls.BaseUrl + "/drives/" + driveId + "/root:/" + Uri.EscapeDataString(contractNo) + "?$select=id,webUrl";
        using var get = GraphCalls.Request(HttpMethod.Get, url, token);
        using var existing = await Client().SendAsync(get, cancellationToken);
        if (!existing.IsSuccessStatusCode)
        {
            throw await GraphCalls.FailureAsync(existing, "open the folder " + contractNo, cancellationToken);
        }
        using var existingJson = JsonDocument.Parse(await existing.Content.ReadAsStringAsync(cancellationToken));
        var existingId = GraphCalls.Text(existingJson.RootElement, "id") ?? throw new InvalidOperationException("Microsoft Graph returned a folder without an id.");
        return new StoreFolder(driveId, existingId, GraphCalls.Text(existingJson.RootElement, "webUrl"));
    }

    /// <summary>"Agreement.pdf", version 2 -> "Agreement v2.pdf"; a suffix makes a taken name unique.</summary>
    public static string StoredName(string fileName, int version, string? suffix)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var name = version > 1 ? stem + " v" + version : stem;
        if (!string.IsNullOrEmpty(suffix))
        {
            name += " (" + suffix + ")";
        }
        return name + extension;
    }

    private static string ItemPath(string driveId, string folderId, string name) =>
        GraphCalls.BaseUrl + "/drives/" + driveId + "/items/" + folderId + ":/" + Uri.EscapeDataString(name) + ":";

    /// <summary>One PUT that refuses to replace an existing file. Null = the name is taken (409).</summary>
    private async Task<JsonDocument?> SimpleUploadAsync(string driveId, string folderId, string name, byte[] bytes, CancellationToken cancellationToken)
    {
        var url = ItemPath(driveId, folderId, name) + "/content?@microsoft.graph.conflictBehavior=fail";
        var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = GraphCalls.Request(HttpMethod.Put, url, await TokenAsync(cancellationToken), body, true);
        using var response = await Client().SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw await GraphCalls.FailureAsync(response, "upload " + name, cancellationToken);
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>An upload session in parts of ChunkSize, also refusing to replace. Null = the name is taken.</summary>
    private async Task<JsonDocument?> SessionUploadAsync(string driveId, string folderId, string name, byte[] bytes, CancellationToken cancellationToken)
    {
        var sessionBody = new Dictionary<string, object>
        {
            ["item"] = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "fail" }
        };
        string uploadUrl;
        using (var create = GraphCalls.Request(HttpMethod.Post, ItemPath(driveId, folderId, name) + "/createUploadSession", await TokenAsync(cancellationToken), GraphCalls.Json(sessionBody)))
        using (var created = await Client().SendAsync(create, cancellationToken))
        {
            if (created.StatusCode == HttpStatusCode.Conflict)
            {
                return null;
            }
            if (!created.IsSuccessStatusCode)
            {
                throw await GraphCalls.FailureAsync(created, "start the upload of " + name, cancellationToken);
            }
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
            uploadUrl = GraphCalls.Text(json.RootElement, "uploadUrl") ?? throw new InvalidOperationException("Microsoft Graph returned no upload address.");
        }
        for (var offset = 0; offset < bytes.Length; offset += ChunkSize)
        {
            var length = Math.Min(ChunkSize, bytes.Length - offset);
            var part = new ByteArrayContent(bytes, offset, length);
            part.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, bytes.Length);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            // The upload address is pre-authorised: no bearer token is sent to it.
            using var request = GraphCalls.Request(HttpMethod.Put, uploadUrl, null, part, true);
            using var response = await Client().SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw await GraphCalls.FailureAsync(response, "upload " + name, cancellationToken);
            }
            if (offset + length >= bytes.Length)
            {
                // The answer to the last part is the stored item.
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            }
        }
        throw new InvalidOperationException("The upload session ended without a stored file.");
    }

    public async Task<StoredDocument> SaveAsync(DocumentLocation location, string fileName, int version, Stream content, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        if (content is MemoryStream memory && memory.Position == 0)
        {
            bytes = memory.ToArray();
        }
        else
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }
        var siteId = await ResolveSiteIdAsync(location, cancellationToken);
        var (driveId, library) = await ResolveDriveAsync(siteId, location, cancellationToken);
        var folder = await EnsureFolderAsync(driveId, location.ContractNo, cancellationToken);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            // First the plain versioned name; when it is taken (a simultaneous upload), a random suffix.
            var name = StoredName(fileName, version, attempt == 0 ? null : Guid.NewGuid().ToString("N").Substring(0, 6));
            var item = bytes.Length > SimpleUploadLimit
                ? await SessionUploadAsync(driveId, folder.ItemId, name, bytes, cancellationToken)
                : await SimpleUploadAsync(driveId, folder.ItemId, name, bytes, cancellationToken);
            if (item is null)
            {
                _logger.LogInformation("The name {Name} is taken in folder {Folder}; trying another name", name, location.ContractNo);
                continue;
            }
            using (item)
            {
                var itemId = GraphCalls.Text(item.RootElement, "id") ?? throw new InvalidOperationException("Microsoft Graph returned a file without an id.");
                var size = item.RootElement.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var reported) ? reported : bytes.LongLength;
                return new StoredDocument("sp:" + driveId + ":" + itemId, size)
                {
                    SiteId = siteId,
                    DriveId = driveId,
                    ItemId = itemId,
                    RelativePath = library + "/" + location.ContractNo + "/" + name,
                    ETag = GraphCalls.Text(item.RootElement, "eTag"),
                    CTag = GraphCalls.Text(item.RootElement, "cTag"),
                    Folder = folder
                };
            }
        }
        throw new StorageUnavailableException("No free file name could be found for " + fileName + " in folder " + location.ContractNo + ".");
    }

    private static (string DriveId, string ItemId) ParseKey(string storeKey)
    {
        var parts = (storeKey ?? "").Split(':');
        if (parts.Length != 3 || parts[0] != "sp" || parts[1].Length == 0 || parts[2].Length == 0)
        {
            throw new InvalidOperationException("Not a SharePoint document key.");
        }
        return (parts[1], parts[2]);
    }

    /// <summary>Streams the file: the body is not read into memory here.</summary>
    public async Task<Stream> OpenReadAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        var (driveId, itemId) = ParseKey(storeKey);
        using var request = GraphCalls.Request(
            HttpMethod.Get, GraphCalls.BaseUrl + "/drives/" + driveId + "/items/" + itemId + "/content", await TokenAsync(cancellationToken), null, true);
        var response = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new StorageFileMissingException(storeKey);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw await GraphCalls.FailureAsync(response, "download a file", cancellationToken);
            }
            var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new ResponseStream(response, body);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        var (driveId, itemId) = ParseKey(storeKey);
        using var request = GraphCalls.Request(HttpMethod.Delete, GraphCalls.BaseUrl + "/drives/" + driveId + "/items/" + itemId, await TokenAsync(cancellationToken));
        using var response = await Client().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            throw await GraphCalls.FailureAsync(response, "delete a file", cancellationToken);
        }
    }

    /// <summary>The folder's address, without creating it. Not used when a contract is read (the link is stored on the contract).</summary>
    public async Task<string?> GetFolderUrlAsync(DocumentLocation location, CancellationToken cancellationToken = default)
    {
        var siteId = await ResolveSiteIdAsync(location, cancellationToken);
        var (driveId, _) = await ResolveDriveAsync(siteId, location, cancellationToken);
        var url = GraphCalls.BaseUrl + "/drives/" + driveId + "/root:/" + Uri.EscapeDataString(location.ContractNo) + "?$select=webUrl";
        using var request = GraphCalls.Request(HttpMethod.Get, url, await TokenAsync(cancellationToken));
        using var response = await Client().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return GraphCalls.Text(json.RootElement, "webUrl");
    }

    private static StoreItem ToItem(JsonElement element)
    {
        string? parent = null;
        if (element.TryGetProperty("parentReference", out var parentReference))
        {
            parent = GraphCalls.Text(parentReference, "id");
        }
        var size = element.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value) ? value : 0;
        return new StoreItem(
            GraphCalls.Text(element, "id") ?? "", GraphCalls.Text(element, "name") ?? "", size,
            GraphCalls.Text(element, "eTag"), GraphCalls.Text(element, "cTag"), parent, GraphCalls.Text(element, "webUrl"));
    }

    public async Task<StoreItem?> GetItemAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        var (driveId, itemId) = ParseKey(storeKey);
        var url = GraphCalls.BaseUrl + "/drives/" + driveId + "/items/" + itemId + "?$select=id,name,size,eTag,cTag,parentReference,webUrl";
        using var request = GraphCalls.Request(HttpMethod.Get, url, await TokenAsync(cancellationToken));
        using var response = await Client().SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new StorageFileMissingException(storeKey);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw await GraphCalls.FailureAsync(response, "read a file's properties", cancellationToken);
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ToItem(json.RootElement);
    }

    public async Task<IReadOnlyList<StoreItem>> ListFolderAsync(string driveId, string folderItemId, CancellationToken cancellationToken = default)
    {
        var items = new List<StoreItem>();
        string? url = GraphCalls.BaseUrl + "/drives/" + driveId + "/items/" + folderItemId + "/children?$select=id,name,size,eTag,cTag,file,parentReference,webUrl&$top=200";
        var pages = 0;
        while (url is not null && pages < 50)
        {
            pages++;
            using var request = GraphCalls.Request(HttpMethod.Get, url, await TokenAsync(cancellationToken));
            using var response = await Client().SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new StorageFileMissingException("sp:" + driveId + ":" + folderItemId);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw await GraphCalls.FailureAsync(response, "list a contract folder", cancellationToken);
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in value.EnumerateArray())
                {
                    // Files only; sub-folders are not the app's.
                    if (element.TryGetProperty("file", out _))
                    {
                        items.Add(ToItem(element));
                    }
                }
            }
            url = GraphCalls.Text(json.RootElement, "@odata.nextLink");
        }
        return items;
    }

    /// <summary>A cheap call with the metadata timeout: can the app reach Microsoft Graph and sign in?</summary>
    public async Task<StoreProbe> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var request = GraphCalls.Request(HttpMethod.Get, GraphCalls.BaseUrl + "/sites?$select=id&$top=1&search=*", await TokenAsync(cancellationToken));
        using var response = await Client().SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new StoreProbe(true, "Microsoft Graph answered.");
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new StoreProbe(false, "Microsoft Graph refused the app's credentials (HTTP 401).");
        }
        // 403 with Sites.Selected is normal for a tenant-wide question: Graph is reachable and the app is signed in.
        return new StoreProbe((int)response.StatusCode < 500, "Microsoft Graph answered HTTP " + (int)response.StatusCode + ".");
    }
}
