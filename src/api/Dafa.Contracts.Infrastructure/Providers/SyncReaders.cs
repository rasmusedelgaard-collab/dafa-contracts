using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>Connection references from server configuration: Sync:Connections:&lt;name&gt;:Kind/Description/ConnectionString.</summary>
public static class SyncConnections
{
    public static List<(string Name, string Kind, string Description)> FromConfiguration(IConfiguration configuration)
    {
        var result = new List<(string Name, string Kind, string Description)>();
        foreach (var section in configuration.GetSection("Sync:Connections").GetChildren())
        {
            var kind = section["Kind"] ?? "";
            if (kind != "Sql" && kind != "PowerBi")
            {
                continue;
            }
            result.Add((section.Key, kind, section["Description"] ?? (kind == "Sql" ? "SQL connection " + section.Key : "Power BI (app registration)")));
        }
        return result;
    }

    /// <summary>The connection string of a SQL reference: Sync:Connections:&lt;name&gt;:ConnectionString, else ConnectionStrings:&lt;name&gt;.</summary>
    public static string? ConnectionString(IConfiguration configuration, string name)
    {
        var value = configuration["Sync:Connections:" + name + ":ConnectionString"];
        return string.IsNullOrWhiteSpace(value) ? configuration.GetConnectionString(name) : value;
    }

    public static string CellText(object? value)
    {
        return value switch
        {
            null => "",
            DBNull => "",
            DateTime date => date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
    }
}

/// <summary>
/// TEST MODE (Sync:Provider=Demo, the default). Returns generated demo rows for any source, so "test
/// connection" and "run now" work on the test site without a data warehouse or Power BI. Offers two
/// demo connections (DemoErp: Sql, DemoPowerBi: PowerBi) in addition to the configured ones.
/// </summary>
public sealed class DemoSyncSourceReader : ISyncSourceReader
{
    private readonly IConfiguration _configuration;

    public DemoSyncSourceReader(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public IReadOnlyList<(string Name, string Kind, string Description)> GetConnections()
    {
        var list = new List<(string Name, string Kind, string Description)>
        {
            ("DemoErp", "Sql", "Demo ERP database (test mode: returns generated rows)"),
            ("DemoPowerBi", "PowerBi", "Demo Power BI model (test mode: returns generated rows)")
        };
        list.AddRange(SyncConnections.FromConfiguration(_configuration).Where(c => list.All(l => l.Name != c.Name)));
        return list;
    }

    public Task<TabularData> ReadAsync(SyncQuery query, int maxRows, CancellationToken cancellationToken = default)
    {
        var data = new TabularData();
        switch (query.Target)
        {
            case "users":
                data.Columns = new List<string> { "Email", "Name", "Title", "Roles", "Departments" };
                data.Rows.Add(new[] { "demo.buyer@dafa-demo.example", "Bo Demo", "Buyer", "Reader;ContractOwner", "Purchasing" });
                data.Rows.Add(new[] { "demo.controller@dafa-demo.example", "Clara Demo", "Controller", "Reader", "Finance" });
                break;
            case "contracts":
                data.Columns = new List<string> { "ContractNo", "Title", "Counterparty", "Type", "Entity", "Department", "Owner", "StartDate", "EndDate", "NoticeDays", "AnnualValue", "Currency" };
                data.Rows.Add(new[] { "DEMO-0001", "Demo sync: packaging film", "S-10090", "Supplier – direct materials", "DAFA A/S", "Purchasing", "jonas@dafa-demo.example", "2025-01-01", "2027-12-31", "90", "250000", "DKK" });
                data.Rows.Add(new[] { "DEMO-0002", "Demo sync: forklift service", "S-20040", "Supplier – indirect and services", "DAFA Sverige AB", "Maintenance and facilities", "jonas@dafa-demo.example", "2025-06-01", "", "60", "120000", "SEK" });
                break;
            default:
                data.Columns = new List<string> { "VendorNo", "Name", "Country", "VatNo", "City" };
                foreach (var record in SeedSupplierSource.Records)
                {
                    data.Rows.Add(new[] { record.ExternalId, record.Name, record.Country ?? "", record.VatNo ?? "", "" });
                }
                data.Rows.Add(new[] { "S-90010", "Kolding Gummi A/S", "DK", "DK39901010", "Kolding" });
                data.Rows.Add(new[] { "S-90020", "Fyns Pakningsfabrik ApS", "DK", "DK39902020", "Odense" });
                break;
        }
        if (data.Rows.Count > maxRows)
        {
            data.Rows = data.Rows.Take(maxRows).ToList();
        }
        return Task.FromResult(data);
    }
}

/// <summary>
/// PRODUCTION MODE (Sync:Provider=Live). NOT exercised in the test environment.
/// SQL: runs the SELECT on the named server-side connection inside a transaction that is rolled
/// back (read-only), 60 s timeout. Power BI: executeQueries on the workspace's dataset with a token
/// from Suppliers:PowerBi:TenantId/ClientId/ClientSecret, the managed identity
/// (Suppliers:PowerBi:UseManagedIdentity=true) or the AzureAd app registration.
/// </summary>
public sealed class LiveSyncSourceReader : ISyncSourceReader
{
    private const string PowerBiScope = "https://analysis.windows.net/powerbi/api/.default";
    private const string PowerBiResource = "https://analysis.windows.net/powerbi/api";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccessTokenProvider _tokens;

    public LiveSyncSourceReader(IConfiguration configuration, IHttpClientFactory httpClientFactory, IAccessTokenProvider tokens)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
    }

    public IReadOnlyList<(string Name, string Kind, string Description)> GetConnections() =>
        SyncConnections.FromConfiguration(_configuration);

    public async Task<TabularData> ReadAsync(SyncQuery query, int maxRows, CancellationToken cancellationToken = default)
    {
        return query.Kind == "PowerBi"
            ? await ReadPowerBiAsync(query, maxRows, cancellationToken)
            : await ReadSqlAsync(query, maxRows, cancellationToken);
    }

    private async Task<TabularData> ReadSqlAsync(SyncQuery query, int maxRows, CancellationToken cancellationToken)
    {
        var name = query.ConnectionName ?? "";
        if (!GetConnections().Any(c => c.Name == name && c.Kind == "Sql"))
        {
            throw new InvalidOperationException("The connection '" + name + "' is not configured on the server.");
        }
        if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The application's own database cannot be used as a sync source.");
        }
        var connectionString = SyncConnections.ConnectionString(_configuration, name);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("The connection '" + name + "' has no connection string on the server.");
        }
        var sql = query.SqlQuery ?? "";
        var trimmed = sql.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only SELECT queries are allowed.");
        }
        var data = new TabularData();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // Read-only: whatever the statement does is rolled back.
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = 60;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                data.Columns.Add(reader.GetName(i));
            }
            while (data.Rows.Count < maxRows && await reader.ReadAsync(cancellationToken))
            {
                var cells = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    cells[i] = SyncConnections.CellText(reader.IsDBNull(i) ? null : reader.GetValue(i));
                }
                data.Rows.Add(cells);
            }
        }
        await transaction.RollbackAsync(cancellationToken);
        return data;
    }

    private async Task<string> PowerBiTokenAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("powerbi-token");
        if (_configuration.GetValue("Suppliers:PowerBi:UseManagedIdentity", false))
        {
            var endpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
            var header = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(header))
            {
                throw new InvalidOperationException("No managed identity is available (IDENTITY_ENDPOINT is not set).");
            }
            var url = endpoint + "?resource=" + Uri.EscapeDataString(PowerBiResource) + "&api-version=2019-08-01";
            var clientId = _configuration["Suppliers:PowerBi:ManagedIdentityClientId"];
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                url += "&client_id=" + Uri.EscapeDataString(clientId);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-IDENTITY-HEADER", header);
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return json.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("No access token.");
        }
        var tenant = _configuration["Suppliers:PowerBi:TenantId"];
        var appId = _configuration["Suppliers:PowerBi:ClientId"];
        var secret = _configuration["Suppliers:PowerBi:ClientSecret"];
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret))
        {
            // The app registration from AzureAd:*.
            return await _tokens.GetTokenAsync(PowerBiScope, cancellationToken);
        }
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = appId,
            ["client_secret"] = secret,
            ["scope"] = PowerBiScope,
            ["grant_type"] = "client_credentials"
        });
        using var tokenResponse = await client.PostAsync("https://login.microsoftonline.com/" + Uri.EscapeDataString(tenant) + "/oauth2/v2.0/token", form, cancellationToken);
        tokenResponse.EnsureSuccessStatusCode();
        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
        return tokenJson.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("No access token.");
    }

    private async Task<TabularData> ReadPowerBiAsync(SyncQuery query, int maxRows, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.WorkspaceId) || string.IsNullOrWhiteSpace(query.DatasetId))
        {
            throw new InvalidOperationException("Workspace id and dataset id are required.");
        }
        var dax = !string.IsNullOrWhiteSpace(query.DaxQuery)
            ? query.DaxQuery!
            : "EVALUATE '" + (query.Table ?? "").Replace("'", "''") + "'";
        var payload = new Dictionary<string, object>
        {
            ["queries"] = new object[] { new Dictionary<string, string> { ["query"] = dax } },
            ["serializerSettings"] = new Dictionary<string, bool> { ["includeNulls"] = true }
        };
        var token = await PowerBiTokenAsync(cancellationToken);
        var client = _httpClientFactory.CreateClient("powerbi");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.powerbi.com/v1.0/myorg/groups/" + Uri.EscapeDataString(query.WorkspaceId!) + "/datasets/" + Uri.EscapeDataString(query.DatasetId!) + "/executeQueries");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException("Power BI answered " + (int)response.StatusCode + ": " + (body.Length > 300 ? body.Substring(0, 300) : body));
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var rows = json.RootElement.GetProperty("results")[0].GetProperty("tables")[0].GetProperty("rows");
        var data = new TabularData();
        foreach (var row in rows.EnumerateArray())
        {
            if (data.Rows.Count >= maxRows)
            {
                break;
            }
            foreach (var property in row.EnumerateObject())
            {
                if (!data.Columns.Contains(property.Name))
                {
                    data.Columns.Add(property.Name);
                }
            }
            var cells = new string[data.Columns.Count];
            foreach (var property in row.EnumerateObject())
            {
                var index = data.Columns.IndexOf(property.Name);
                cells[index] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => ""
                };
            }
            data.Rows.Add(cells);
        }
        // Rows read before a column appeared get empty cells for it.
        for (var i = 0; i < data.Rows.Count; i++)
        {
            if (data.Rows[i].Length < data.Columns.Count)
            {
                var widened = new string[data.Columns.Count];
                Array.Copy(data.Rows[i], widened, data.Rows[i].Length);
                for (var c = data.Rows[i].Length; c < widened.Length; c++) widened[c] = "";
                data.Rows[i] = widened;
            }
            for (var c = 0; c < data.Rows[i].Length; c++)
            {
                data.Rows[i][c] ??= "";
            }
        }
        // Power BI names columns 'Table[Column]': use the part in brackets.
        data.Columns = data.Columns.Select(c =>
        {
            var start = c.IndexOf('[');
            return start >= 0 && c.EndsWith("]", StringComparison.Ordinal) ? c.Substring(start + 1, c.Length - start - 2) : c;
        }).ToList();
        if (data.Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Columns.Count)
        {
            throw new InvalidOperationException("The query returns two columns with the same name.");
        }
        return data;
    }
}

/// <summary>Search text via the bounded analyzer extractor (PDF, DOCX, TXT; other types have no text).</summary>
public sealed class DocumentTextExtractor : IDocumentTextExtractor
{
    public async Task<DocumentTextResult> ExtractAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        var extracted = await TextExtractor.ExtractAsync(fileName, content, cancellationToken);
        return new DocumentTextResult(extracted.Text, extracted.Note);
    }
}

/// <summary>Data room zips in a local folder (DataRoom:LocalPath, default App_Data/dataroom).</summary>
public sealed class LocalExportStorage : IExportStorage
{
    private readonly string _root;

    public LocalExportStorage(string root)
    {
        _root = Path.GetFullPath(root);
    }

    private string PathFor(string key)
    {
        var relative = key.Replace('\\', '/').TrimStart('/');
        if (relative.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid storage key.");
        }
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid storage key.");
        }
        return full;
    }

    public Task<Stream> CreateAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Stream stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        return Task.FromResult(stream);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        Stream? stream = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }
}
