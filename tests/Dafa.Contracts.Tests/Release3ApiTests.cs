using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Infrastructure.Data;
using Dafa.Contracts.Infrastructure.Seed;

namespace Dafa.Contracts.Tests;

/// <summary>Release 3 over HTTP on the demo seed: scoping of analytics, search, pages and the data room; imports; schema version.</summary>
[Collection(ApiCollection.Name)]
public class Release3ApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public Release3ApiTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<HashSet<Guid>> VisibleIdsAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=200");
        return list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
    }

    [Fact]
    public async Task StartupUpgrader_RecordsTheSchemaVersions()
    {
        await _factory.WithDbAsync(async db =>
        {
            var versions = await new SchemaUpgrader(db).GetRecordedVersionsAsync();
            Assert.Equal(new List<int> { 1, 2, 3, 4 }, versions);
        });
    }

    [Fact]
    public async Task OverviewAnalytics_IsOpenToEveryUser_AndScoped()
    {
        var planner = await _factory.LoginAsync("thomas");   // Production only
        var newcomer = await _factory.LoginAsync("nina");    // no access at all
        var admin = await _factory.LoginAsync("mette");

        var production = SeedIds.Department("Production").ToString();
        var mine = await planner.GetFromJsonAsync<JsonElement>("/api/overview/analytics");
        Assert.All(mine.GetProperty("byDepartment").EnumerateArray(), b => Assert.Equal(production, b.GetProperty("key").GetString()));
        Assert.Equal(24, mine.GetProperty("noticeByMonth").GetArrayLength());
        Assert.Equal((await VisibleIdsAsync(planner)).Count, mine.GetProperty("scope").GetProperty("visibleContracts").GetInt32());

        var none = await newcomer.GetFromJsonAsync<JsonElement>("/api/overview/analytics");
        Assert.Equal(0, none.GetProperty("kpis").GetProperty("count").GetInt32());
        Assert.Equal("Your contracts", none.GetProperty("scope").GetProperty("label").GetString());

        var all = await admin.GetFromJsonAsync<JsonElement>("/api/overview/analytics?includeEnded=true");
        Assert.True(all.GetProperty("kpis").GetProperty("count").GetInt32() > mine.GetProperty("kpis").GetProperty("count").GetInt32());
        Assert.Equal(7, all.GetProperty("statusFunnel").GetArrayLength());

        // The shared filter on the register: repeated parameters, unknown sort is 400.
        var dafa = SeedIds.Entity("DAFA A/S");
        var sverige = SeedIds.Entity("DAFA Sverige AB");
        var both = await admin.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=200&entityId=" + dafa + "&entityId=" + sverige);
        Assert.All(both.GetProperty("items").EnumerateArray(), i =>
            Assert.Contains(i.GetProperty("entityId").GetGuid(), new[] { dafa, sverige }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/contracts?sort=nonsense")).StatusCode);
    }

    [Fact]
    public async Task DocumentTextSearch_AfterBackfill_IsScoped()
    {
        var admin = await _factory.LoginAsync("mette");
        var planner = await _factory.LoginAsync("thomas");
        var newcomer = await _factory.LoginAsync("nina");

        // Seeded documents have no text row yet; the UploadCleanup job backfills them.
        for (var i = 0; i < 3; i++)
        {
            var run = await admin.PostAsync("/api/admin/jobs/UploadCleanup/run", null);
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        }

        var all = await admin.GetFromJsonAsync<JsonElement>("/api/search/full?q=stands+in+for+the+signed&group=documents&pageSize=50");
        Assert.True(all.GetProperty("documents").GetProperty("total").GetInt32() > 0);
        var first = all.GetProperty("documents").GetProperty("items")[0];
        Assert.Contains("text", first.GetProperty("matchedIn").EnumerateArray().Select(m => m.GetString()));

        var visible = await VisibleIdsAsync(planner);
        var scoped = await planner.GetFromJsonAsync<JsonElement>("/api/search/full?q=stands+in+for+the+signed&group=documents&pageSize=50");
        Assert.True(scoped.GetProperty("documents").GetProperty("total").GetInt32() < all.GetProperty("documents").GetProperty("total").GetInt32());
        Assert.All(scoped.GetProperty("documents").GetProperty("items").EnumerateArray(), d => Assert.Contains(d.GetProperty("contractId").GetGuid(), visible));

        var nothing = await newcomer.GetFromJsonAsync<JsonElement>("/api/search/full?q=stands+in+for+the+signed");
        Assert.Equal(0, nothing.GetProperty("documents").GetProperty("total").GetInt32());
        Assert.Equal(0, nothing.GetProperty("contracts").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task PersonAndCounterpartyPages_AreScoped()
    {
        var newcomer = await _factory.LoginAsync("nina");
        var admin = await _factory.LoginAsync("mette");
        var jonas = SeedIds.User("jonas");

        var asAdmin = await admin.GetFromJsonAsync<JsonElement>("/api/people/" + jonas);
        Assert.True(asAdmin.GetProperty("counts").GetProperty("total").GetInt32() > 0);
        var asNewcomer = await newcomer.GetFromJsonAsync<JsonElement>("/api/people/" + jonas);
        Assert.Equal(0, asNewcomer.GetProperty("contracts").GetArrayLength());
        Assert.Equal("Jonas Berg", asNewcomer.GetProperty("person").GetProperty("name").GetString());

        var supplier = SeedIds.Supplier("S-10010");
        var page = await newcomer.GetFromJsonAsync<JsonElement>("/api/counterparties/" + supplier);
        Assert.Equal(0, page.GetProperty("contracts").GetArrayLength());
        Assert.Equal(0, page.GetProperty("documents").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await newcomer.GetAsync("/api/people/" + Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task DataRoom_IsForManagement_AndHoldsOnlyVisibleContracts()
    {
        var planner = await _factory.LoginAsync("thomas");
        var director = await _factory.LoginAsync("karin");   // manages DAFA Sverige AB only

        var refused = await planner.PostAsync("/api/data-room/exports", Json(new { name = "Nope", filter = new { }, documents = "All", versions = "Latest" }));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var created = await director.PostAsync("/api/data-room/exports", Json(new { name = "Sverige DD", filter = new { includeEnded = true }, documents = "All", versions = "Latest" }));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var export = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ready", export.GetProperty("status").GetString());
        var id = export.GetProperty("id").GetGuid();

        var zipBytes = await director.GetByteArrayAsync("/api/data-room/exports/" + id + "/content");
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var visible = await director.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=200");
        var numbers = visible.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("contractNo").GetString()).ToHashSet();
        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream);
        Assert.All(manifest.RootElement.GetProperty("documents").EnumerateArray(), d => Assert.Contains(d.GetProperty("contractNo").GetString(), numbers));
        Assert.Equal(numbers.Count, manifest.RootElement.GetProperty("contracts").GetInt32());

        // Another user does not see the export.
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.GetAsync("/api/data-room/exports/" + id)).StatusCode);
    }

    [Fact]
    public async Task CounterpartyImport_OverHttp_RunsOnce_AndIsAdminOnly()
    {
        var admin = await _factory.LoginAsync("mette");
        var owner = await _factory.LoginAsync("jonas");
        var csv = Encoding.UTF8.GetBytes("Vendor;Name;Country\nS-77001;Http Import A/S;DK\nS-77002;Http Import AB;SE\n");

        var form = new MultipartFormDataContent { { new ByteArrayContent(csv), "file", "vendors.csv" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/api/admin/imports/counterparties/upload", form)).StatusCode);

        var upload = await admin.PostAsync("/api/admin/imports/counterparties/upload", new MultipartFormDataContent { { new ByteArrayContent(csv), "file", "vendors.csv" } });
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var session = await upload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, session.GetProperty("rowCount").GetInt32());
        var sessionId = session.GetProperty("id").GetGuid();
        var body = new
        {
            mapping = new Dictionary<string, string?> { ["externalId"] = "Vendor", ["name"] = "Name", ["country"] = "Country" },
            options = new { mode = "CreateAndUpdate" }
        };

        var preview = await (await admin.PostAsync("/api/admin/imports/sessions/" + sessionId + "/preview", Json(body))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, preview.GetProperty("create").GetInt32());

        var run = await admin.PostAsync("/api/admin/imports/sessions/" + sessionId + "/run", Json(body));
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var result = await run.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("created").GetInt32());
        Assert.Equal("counterparties", result.GetProperty("kind").GetString());

        var again = await admin.PostAsync("/api/admin/imports/sessions/" + sessionId + "/run", Json(body));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("urn:dafa:problem:import-state", problem.GetProperty("type").GetString());

        var found = await owner.GetFromJsonAsync<JsonElement>("/api/counterparties?q=Http+Import");
        Assert.Equal(2, found.GetArrayLength());
    }

    [Fact]
    public async Task SyncSource_DemoReader_TestAndRunWork()
    {
        var admin = await _factory.LoginAsync("mette");
        var connections = await admin.GetFromJsonAsync<JsonElement>("/api/admin/sync-connections");
        Assert.Contains(connections.EnumerateArray(), c => c.GetProperty("name").GetString() == "DemoErp");

        var input = new
        {
            name = "Demo vendors",
            target = "counterparties",
            kind = "Sql",
            powerBi = (object?)null,
            sql = new { connectionName = "DemoErp", query = "SELECT * FROM Vendors" },
            mapping = new Dictionary<string, string?> { ["externalId"] = "VendorNo", ["name"] = "Name", ["country"] = "Country", ["vatNo"] = "VatNo" },
            options = new { mode = "CreateAndUpdate" },
            schedule = new { frequency = "Daily", time = "06:00", dayOfWeek = (int?)null },
            isActive = true
        };
        var test = await (await admin.PostAsync("/api/admin/sync-sources/test", Json(input))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(test.GetProperty("ok").GetBoolean());
        Assert.Contains(test.GetProperty("columns").EnumerateArray(), c => c.GetString() == "VendorNo");

        var bad = await admin.PostAsync("/api/admin/sync-sources", Json(new { name = "Bad", target = "counterparties", kind = "Sql", sql = new { connectionName = "DemoErp", query = "DELETE FROM Vendors" }, schedule = new { frequency = "Off", time = "06:00" } }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await (await admin.PostAsync("/api/admin/sync-sources", Json(input))).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        Assert.NotEqual(JsonValueKind.Null, created.GetProperty("nextRunAt").ValueKind);
        var run = await (await admin.PostAsync("/api/admin/sync-sources/" + id + "/run", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        Assert.True(run.GetProperty("created").GetInt32() >= 2);   // the two demo vendors the seed does not have
        var runs = await admin.GetFromJsonAsync<JsonElement>("/api/admin/sync-sources/" + id + "/runs");
        Assert.Equal(1, runs.GetArrayLength());
    }

    [Fact]
    public async Task AppConfig_Preferences_AndJobs()
    {
        var newcomer = await _factory.LoginAsync("nina");
        var config = await newcomer.GetFromJsonAsync<JsonElement>("/api/app-config");
        Assert.Equal("DAFA Contracts", config.GetProperty("appName").GetString());
        Assert.False(config.GetProperty("canExportDataRoom").GetBoolean());
        Assert.True(config.GetProperty("rates").GetProperty("DKK").GetDecimal() == 1m);

        var preferences = await newcomer.GetFromJsonAsync<JsonElement>("/api/me/preferences");
        // 13 release 3 events + SignatureRequested, SignatureCompleted, SignatureDeclined (release 4).
        Assert.Equal(16, preferences.GetProperty("events").GetArrayLength());
        var put = await newcomer.PutAsync("/api/me/preferences", Json(new { digest = "Off", events = Array.Empty<object>(), defaultEntityId = (Guid?)null, landingPage = "contracts", autoWatch = true }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await newcomer.GetFromJsonAsync<JsonElement>("/api/app-config");
        Assert.Equal("contracts", after.GetProperty("landingPage").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await newcomer.GetAsync("/api/admin/jobs")).StatusCode);
        var admin = await _factory.LoginAsync("mette");
        var jobs = await admin.GetFromJsonAsync<JsonElement>("/api/admin/jobs");
        // 8 release 3 jobs + SignatureSync and StorageReconciliation (release 4).
        Assert.Equal(10, jobs.GetArrayLength());
        var update = await admin.PutAsync("/api/admin/jobs/Digest", Json(new { schedule = new { frequency = "Weekly", time = "07:30", dayOfWeek = 1 } }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var badSchedule = await admin.PutAsync("/api/admin/jobs/Digest", Json(new { schedule = new { frequency = "Weekly", time = "07:30", dayOfWeek = 9 } }));
        Assert.Equal(HttpStatusCode.BadRequest, badSchedule.StatusCode);
    }
}
