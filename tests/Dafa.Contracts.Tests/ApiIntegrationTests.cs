using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dafa.Contracts.Api.Auth;
using Dafa.Contracts.Infrastructure.Seed;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Dafa.Contracts.Tests;

/// <summary>
/// Boots the real API host on a shared in-memory Sqlite database with the demo seed.
/// Settings are passed as environment variables because Program.cs reads configuration while
/// it is building the host, before WebApplicationFactory's own configuration hooks apply.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _tempFolder;

    public ApiFactory()
    {
        var connectionString = "Data Source=dafa-tests-" + Guid.NewGuid().ToString("N") + ";Mode=Memory;Cache=Shared";
        _tempFolder = Path.Combine(Path.GetTempPath(), "dafa-contracts-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFolder);

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("Database__Provider", "Sqlite");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", connectionString);
        Environment.SetEnvironmentVariable("Seed__DemoData", "true");
        Environment.SetEnvironmentVariable("Jobs__Enabled", "false");
        Environment.SetEnvironmentVariable("Auth__Mode", "Dev");
        Environment.SetEnvironmentVariable("Auth__Dev__AccessCode", "");
        Environment.SetEnvironmentVariable("Documents__Provider", "Local");
        Environment.SetEnvironmentVariable("Documents__LocalPath", Path.Combine(_tempFolder, "files"));
        Environment.SetEnvironmentVariable("DataProtection__KeysPath", Path.Combine(_tempFolder, "keys"));

        // The named in-memory database lives as long as one connection to it is open.
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
    }

    public async Task<HttpClient> LoginAsync(string demoUserKey)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/dev/login", new { userId = SeedIds.User(demoUserKey) });
        response.EnsureSuccessStatusCode();
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAlive.Dispose();
            try
            {
                Directory.Delete(_tempFolder, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

public class ApiIntegrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ApiIntegrationTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Healthz_IsAnonymous()
    {
        var response = await _factory.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_RequiresSignIn()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/contracts")).StatusCode);
    }

    [Fact]
    public async Task DevConfig_And_DevUsers_AreOpen_WhenNoAccessCodeIsConfigured()
    {
        var client = _factory.CreateClient();

        var config = await client.GetFromJsonAsync<JsonElement>("/api/dev/config");
        Assert.Equal("Dev", config.GetProperty("authMode").GetString());
        Assert.False(config.GetProperty("accessCodeRequired").GetBoolean());

        var users = await client.GetFromJsonAsync<JsonElement>("/api/dev/users");
        Assert.True(users.GetArrayLength() >= 8);
    }

    [Fact]
    public async Task Login_AsDemoUser_ReturnsMe_WithCamelCaseShape()
    {
        var client = await _factory.LoginAsync("jonas");

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");

        Assert.Equal("Jonas Berg", me.GetProperty("name").GetString());
        Assert.Equal("Dev", me.GetProperty("authMode").GetString());
        Assert.False(me.GetProperty("isGroupManagement").GetBoolean());
        var roles = me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("ContractOwner", roles);
        Assert.Equal(1, me.GetProperty("departmentIds").GetArrayLength());
    }

    [Fact]
    public async Task ContractList_IsFilteredByAccess()
    {
        var admin = await _factory.LoginAsync("mette");
        var planner = await _factory.LoginAsync("thomas");   // member of Production only
        var newcomer = await _factory.LoginAsync("nina");    // no access path at all

        var all = await admin.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=200");
        var production = await planner.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=200");
        var nothing = await newcomer.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=200");

        var total = all.GetProperty("total").GetInt32();
        Assert.True(total >= 40, "Expected the demo seed to contain about 40 contracts, got " + total);
        Assert.Equal(0, nothing.GetProperty("total").GetInt32());
        Assert.Equal(0, nothing.GetProperty("items").GetArrayLength());

        var productionTotal = production.GetProperty("total").GetInt32();
        Assert.InRange(productionTotal, 1, total - 1);
        foreach (var item in production.GetProperty("items").EnumerateArray())
        {
            Assert.Equal("Production", item.GetProperty("departmentName").GetString());
            // Shape check: enums as strings, dates as yyyy-MM-dd or null.
            Assert.Equal(JsonValueKind.String, item.GetProperty("status").ValueKind);
            var deadline = item.GetProperty("noticeDeadline");
            if (deadline.ValueKind != JsonValueKind.Null)
            {
                Assert.Matches("^\\d{4}-\\d{2}-\\d{2}$", deadline.GetString());
            }
        }
    }

    [Fact]
    public async Task ContractDetail_Is403WithoutAccess_404WhenMissing_And200WithAccess()
    {
        var contractId = SeedIds.For("contract:epdm");       // DAFA A/S, Purchasing, owned by Jonas
        var newcomer = await _factory.LoginAsync("nina");
        var owner = await _factory.LoginAsync("jonas");

        var forbidden = await newcomer.GetAsync("/api/contracts/" + contractId);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("application/problem+json", forbidden.Content.Headers.ContentType?.MediaType);

        var missing = await owner.GetAsync("/api/contracts/" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var detail = await owner.GetFromJsonAsync<JsonElement>("/api/contracts/" + contractId);
        Assert.Equal("Supply agreement, EPDM compounds", detail.GetProperty("title").GetString());
        Assert.True(detail.GetProperty("canEdit").GetBoolean());
        Assert.True(detail.GetProperty("documents").GetArrayLength() >= 1);
        Assert.Equal("Owner", detail.GetProperty("reminderRecipient").GetString());

        // The same rule protects the files.
        var documentId = detail.GetProperty("documents")[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await newcomer.GetAsync("/api/documents/" + documentId + "/content")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/documents/" + documentId + "/content")).StatusCode);
    }

    [Fact]
    public async Task Admin_IsForAdministratorsOnly()
    {
        var owner = await _factory.LoginAsync("jonas");
        var admin = await _factory.LoginAsync("mette");

        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/admin/approval-rules")).StatusCode);

        var rules = await admin.GetFromJsonAsync<JsonElement>("/api/admin/approval-rules");
        Assert.True(rules.GetArrayLength() >= 4);
    }

    [Fact]
    public async Task Create_WithMissingFields_Returns400ProblemWithFieldErrors()
    {
        var owner = await _factory.LoginAsync("jonas");

        var response = await owner.PostAsJsonAsync("/api/contracts", new { title = "Only a title" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("entityId", out _));
    }
}

public class AuthSettingsTests
{
    [Fact]
    public void WithoutAccessCode_NothingIsRequired()
    {
        var settings = new AuthSettings(AuthModes.Dev, "");
        Assert.False(settings.AccessCodeRequired);
        Assert.True(settings.AccessCodeMatches(null));
    }

    [Fact]
    public void WithAccessCode_OnlyTheExactCodeMatches()
    {
        var settings = new AuthSettings(AuthModes.Dev, "open-sesame");
        Assert.True(settings.AccessCodeRequired);
        Assert.True(settings.AccessCodeMatches("open-sesame"));
        Assert.False(settings.AccessCodeMatches("open-sesam"));
        Assert.False(settings.AccessCodeMatches("OPEN-SESAME"));
        Assert.False(settings.AccessCodeMatches(""));
        Assert.False(settings.AccessCodeMatches(null));
    }

    [Fact]
    public void EntraMode_NeverAsksForTheDevAccessCode()
    {
        var settings = new AuthSettings(AuthModes.Parse("entra"), "open-sesame");
        Assert.Equal(AuthModes.Entra, settings.Mode);
        Assert.False(settings.AccessCodeRequired);
    }
}
