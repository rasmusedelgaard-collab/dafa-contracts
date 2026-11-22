using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Dafa.Contracts.Infrastructure.Providers;

/// <summary>TEST MODE (Suppliers:Provider=Seed). A fixed list of fictional supplier master records.</summary>
public sealed class SeedSupplierSource : ISupplierSource
{
    public static readonly IReadOnlyList<SupplierRecord> Records = new List<SupplierRecord>
    {
        new("S-10010", "Nordic Polymer Compounds AB", "SE", "SE556100101001"),
        new("S-10020", "Rhein Schaumstoff GmbH", "DE", "DE811200202"),
        new("S-10030", "Jutland Adhesive Tapes A/S", "DK", "DK30100303"),
        new("S-10040", "Alpen Silikon Chemie AG", "DE", "DE811400404"),
        new("S-10050", "Baltic Rubber Profiles sp. z o.o.", "PL", "PL5250005055"),
        new("S-10060", "Tianjin Haihe Sealing Materials Co., Ltd.", "CN", null),
        new("S-10070", "Lombardia Guarnizioni S.r.l.", "IT", "IT01230070707"),
        new("S-10080", "Midwest Gasket Supply Inc.", "US", null),
        new("S-10090", "Vestkyst Emballage A/S", "DK", "DK30100909"),
        new("S-20010", "Østjysk Vikarservice A/S", "DK", "DK31201010"),
        new("S-20020", "Brabrand Ejendomsservice ApS", "DK", "DK31202020"),
        new("S-20030", "Aarhus Erhvervsrengøring A/S", "DK", "DK31203030"),
        new("S-20040", "Skandia Truck Leasing AB", "SE", "SE556200404001"),
        new("S-20050", "Kattegat Kantineservice ApS", "DK", "DK31205050"),
        new("S-30010", "Nordfragt Transport A/S", "DK", "DK32301010"),
        new("S-30020", "Hansa Spedition GmbH", "DE", "DE812302020"),
        new("S-30030", "Øresund Lagerhotel A/S", "DK", "DK32303030"),
        new("S-40010", "Fjordbyte ERP A/S", "DK", "DK33401010"),
        new("S-40020", "Cloudhaven Hosting ApS", "DK", "DK33402020"),
        new("S-40030", "Securon Cyber Defence AB", "SE", "SE556400303001"),
        new("S-40040", "Planwise Software B.V.", "NL", "NL840040404B01"),
        new("S-50010", "Jyske Erhvervsejendomme P/S", "DK", "DK34501010"),
        new("S-50020", "Tianjin Binhai Industrial Park Co., Ltd.", "CN", null),
        new("S-60010", "Nordlys Forsikring A/S", "DK", "DK35601010"),
        new("S-60020", "Limfjord Erhvervsbank A/S", "DK", "DK35602020"),
        new("S-70010", "Institut for Materialeprøvning", "DK", "DK36701010"),
        new("S-70020", "PolyLab Engineering GmbH", "DE", "DE813702020"),
        new("S-80010", "Sønderby & Krag Advokater P/S", "DK", "DK37801010")
    };

    public Task<IReadOnlyList<SupplierRecord>> GetSuppliersAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Records);
    }
}

/// <summary>
/// PRODUCTION MODE (Suppliers:Provider=PowerBi). NOT exercised in the test environment.
/// Runs a DAX query through the Power BI executeQueries REST API. Settings: PowerBi:DatasetId,
/// PowerBi:Query, and the result column names in PowerBi:Columns:Id, :Name, :Country, :VatNo.
/// To be replaced by a SQL query on the data warehouse later.
/// </summary>
public sealed class PowerBiSupplierSource : ISupplierSource
{
    private const string PowerBiScope = "https://analysis.windows.net/powerbi/api/.default";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccessTokenProvider _tokens;
    private readonly IConfiguration _configuration;

    public PowerBiSupplierSource(IHttpClientFactory httpClientFactory, IAccessTokenProvider tokens, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<SupplierRecord>> GetSuppliersAsync(CancellationToken cancellationToken = default)
    {
        var datasetId = _configuration["PowerBi:DatasetId"];
        var query = _configuration["PowerBi:Query"];
        if (string.IsNullOrWhiteSpace(datasetId) || string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("PowerBi:DatasetId and PowerBi:Query must be configured.");
        }
        var idColumn = _configuration["PowerBi:Columns:Id"] ?? "Supplier[No]";
        var nameColumn = _configuration["PowerBi:Columns:Name"] ?? "Supplier[Name]";
        var countryColumn = _configuration["PowerBi:Columns:Country"] ?? "Supplier[Country]";
        var vatColumn = _configuration["PowerBi:Columns:VatNo"] ?? "Supplier[VatNo]";

        var payload = new Dictionary<string, object>
        {
            ["queries"] = new object[] { new Dictionary<string, string> { ["query"] = query } },
            ["serializerSettings"] = new Dictionary<string, bool> { ["includeNulls"] = true }
        };

        var client = _httpClientFactory.CreateClient("powerbi");
        var token = await _tokens.GetTokenAsync(PowerBiScope, cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.powerbi.com/v1.0/myorg/datasets/" + Uri.EscapeDataString(datasetId) + "/executeQueries");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        var records = new List<SupplierRecord>();
        var rows = json.RootElement.GetProperty("results")[0].GetProperty("tables")[0].GetProperty("rows");
        foreach (var row in rows.EnumerateArray())
        {
            var id = ReadText(row, idColumn);
            var name = ReadText(row, nameColumn);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            records.Add(new SupplierRecord(id, name, ReadText(row, countryColumn), ReadText(row, vatColumn)));
        }
        return records;
    }

    private static string? ReadText(JsonElement row, string column)
    {
        if (!row.TryGetProperty(column, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
}
