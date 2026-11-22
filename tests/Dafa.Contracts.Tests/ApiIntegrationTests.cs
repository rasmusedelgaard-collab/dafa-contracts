using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dafa.Contracts.Api.Auth;
using Dafa.Contracts.Infrastructure.Seed;
using Dafa.Contracts.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dafa.Contracts.Tests;

/// <summary>
/// All tests that boot the API host run one after another: the host reads its settings from
/// process environment variables, which are shared by the whole test process.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection
{
    public const string Name = "Api host";
}

/// <summary>
/// Boots the real API host on a shared in-memory Sqlite database with the demo seed.
/// Settings are passed as environment variables because Program.cs reads configuration while
/// it is building the host, before WebApplicationFactory's own configuration hooks apply.
/// They are set again right before the host is created, so several factories with different
/// settings can exist in one test run (see <see cref="ApiCollection"/>).
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string RequestedWithHeader = "X-Requested-With";
    public const string RequestedWithValue = "dafa-contracts";

    private readonly SqliteConnection _keepAlive;
    private readonly string _tempFolder;
    private readonly Dictionary<string, string?> _settings;

    public ApiFactory() : this(null)
    {
    }

    protected ApiFactory(Dictionary<string, string?>? overrides)
    {
        var connectionString = "Data Source=dafa-tests-" + Guid.NewGuid().ToString("N") + ";Mode=Memory;Cache=Shared";
        _tempFolder = Path.Combine(Path.GetTempPath(), "dafa-contracts-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFolder);

        _settings = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["Database__Provider"] = "Sqlite",
            ["ConnectionStrings__Default"] = connectionString,
            ["Seed__DemoData"] = "true",
            ["Jobs__Enabled"] = "false",
            ["Auth__Mode"] = "Dev",
            ["Auth__Dev__AccessCode"] = null,
            ["Auth__BootstrapAdmins"] = null,
            ["Signing__Webhook__Secret"] = null,
            ["Signing__AllowSimulated"] = null,
            ["AzureAd__Instance"] = null,
            ["AzureAd__TenantId"] = null,
            ["AzureAd__ClientId"] = null,
            ["AzureAd__ClientSecret"] = null,
            ["Documents__Provider"] = "Local",
            ["Documents__LocalPath"] = Path.Combine(_tempFolder, "files"),
            ["DataProtection__KeysPath"] = Path.Combine(_tempFolder, "keys")
        };
        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                _settings[pair.Key] = pair.Value;
            }
        }
        ApplySettings();

        // The named in-memory database lives as long as one connection to it is open.
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
    }

    private void ApplySettings()
    {
        foreach (var pair in _settings)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Program.Main starts inside base.CreateHost; make sure it sees this factory's settings.
        ApplySettings();
        return base.CreateHost(builder);
    }

    /// <summary>A client that sends the header every state-changing API request needs.</summary>
    public HttpClient CreateApiClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(RequestedWithHeader, RequestedWithValue);
        return client;
    }

    public async Task<HttpClient> LoginAsync(string demoUserKey, string? accessCode = null)
    {
        return await LoginAsync(SeedIds.User(demoUserKey), accessCode);
    }

    public async Task<HttpClient> LoginAsync(Guid userId, string? accessCode = null)
    {
        var client = CreateApiClient();
        var response = await client.PostAsJsonAsync("/api/dev/login", new { userId, accessCode });
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Direct database access for arranging and checking state.</summary>
    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(db);
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

[Collection(ApiCollection.Name)]
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

    [Theory]
    [InlineData("EntraID")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Production")]
    [InlineData("Dev ,")]
    public void UnknownOrMissingAuthMode_StopsTheApp(string? value)
    {
        Assert.Throws<InvalidOperationException>(() => AuthModes.Parse(value));
    }

    [Fact]
    public void KnownAuthModes_AreAcceptedInAnyCasing()
    {
        Assert.Equal(AuthModes.Dev, AuthModes.Parse(" dev "));
        Assert.Equal(AuthModes.Entra, AuthModes.Parse("ENTRA"));
    }

    [Fact]
    public void DevMode_OutsideDevelopment_RefusesToStart_WithoutALongAccessCode()
    {
        Assert.Throws<InvalidOperationException>(() => new AuthSettings(AuthModes.Dev, null).EnsureSafeFor(false));
        Assert.Throws<InvalidOperationException>(() => new AuthSettings(AuthModes.Dev, "   ").EnsureSafeFor(false));
        Assert.Throws<InvalidOperationException>(() => new AuthSettings(AuthModes.Dev, "elevenchars").EnsureSafeFor(false));

        new AuthSettings(AuthModes.Dev, "twelve-chars").EnsureSafeFor(false);
        // Local development needs no code, and Entra mode never uses one.
        new AuthSettings(AuthModes.Dev, null).EnsureSafeFor(true);
        new AuthSettings(AuthModes.Entra, null).EnsureSafeFor(false);
    }

    [Fact]
    public void AccessCodeStamp_ChangesWithTheCode_AndDoesNotContainIt()
    {
        var first = new AuthSettings(AuthModes.Dev, "correct-horse-battery").AccessCodeStamp();
        var second = new AuthSettings(AuthModes.Dev, "another-long-code").AccessCodeStamp();
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("correct", first);
        Assert.Equal("", new AuthSettings(AuthModes.Dev, null).AccessCodeStamp());
    }

    [Fact]
    public void EntraMode_NeverAsksForTheDevAccessCode()
    {
        var settings = new AuthSettings(AuthModes.Parse("entra"), "open-sesame");
        Assert.Equal(AuthModes.Entra, settings.Mode);
        Assert.False(settings.AccessCodeRequired);
    }
}
