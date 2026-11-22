using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Api.Controllers;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Tests;

/// <summary>A host with the e-signature webhook secret configured (the default test host has none).</summary>
public sealed class WebhookApiFactory : ApiFactory
{
    public const string Secret = "test-webhook-secret-0123456789abcdef";

    public WebhookApiFactory() : base(new Dictionary<string, string?> { ["Signing__Webhook__Secret"] = Secret })
    {
    }
}

/// <summary>Release 4 over HTTP on the demo seed.</summary>
[Collection(ApiCollection.Name)]
public class Release4ApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public Release4ApiTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static MultipartFormDataContent FileForm(string fileName, byte[] bytes, string? kind = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", fileName);
        if (kind is not null)
        {
            form.Add(new StringContent(kind), "kind");
        }
        return form;
    }

    // ---------- item 7: settings are there right after sign-in ----------

    [Fact]
    public async Task EveryAdminPage_AnswersOnTheFirstRequestAfterSignIn_AndIsNeverCached()
    {
        var paths = new[]
        {
            "approval-rules", "retention-rules", "departments", "contract-types", "templates", "reminder-settings", "audit", "outbox",
            "holiday-cover", "counterparties", "counterparties/duplicates", "imports/history", "sync-connections", "sync-sources",
            "custom-fields", "field-rules", "entities", "currencies", "tags", "users", "notification-settings", "jobs", "app-settings",
            "template-categories", "access-mapping", "esign-settings", "storage/health", "storage/reconciliation"
        };
        foreach (var path in paths)
        {
            // A fresh sign-in for every page: nothing may depend on an earlier request having warmed something up.
            var admin = await _factory.LoginAsync("mette");
            var response = await admin.GetAsync("/api/admin/" + path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, path + " answered " + (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore == true, path + " may be cached");
        }

        // The same pages are closed to everyone else, and to a browser without a session.
        var reader = await _factory.LoginAsync("thomas");
        var anonymous = _factory.CreateApiClient();
        foreach (var path in paths)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/" + path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/" + path)).StatusCode);
        }
    }

    [Fact]
    public async Task Me_ReflectsARoleChange_OnTheVeryNextRequest()
    {
        var admin = await _factory.LoginAsync("mette");
        var user = await _factory.LoginAsync("nina");
        var ninaId = SeedIds.User("nina");
        var before = await user.GetFromJsonAsync<JsonElement>("/api/me");
        var rolesBefore = before.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToList();
        Assert.DoesNotContain("Admin", rolesBefore);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/app-settings")).StatusCode);
        Assert.False(before.GetProperty("canSendForSignature").GetBoolean());

        var entity = SeedIds.Entity("DAFA A/S");
        var update = await admin.PutAsync("/api/admin/users/" + ninaId, Json(new
        {
            roles = rolesBefore.Concat(new[] { "Admin" }).ToList(),
            canSendForSignature = true,
            signatureEntityIds = new[] { entity },
            entityId = entity
        }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // Same session, no new sign-in.
        var after = await user.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Contains("Admin", after.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.True(after.GetProperty("canSendForSignature").GetBoolean());
        Assert.Equal(entity, after.GetProperty("signatureEntityIds")[0].GetGuid());
        Assert.Equal(entity, after.GetProperty("entityId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/admin/app-settings")).StatusCode);

        // And back: the right is gone at once.
        var revert = await admin.PutAsync("/api/admin/users/" + ninaId, Json(new { roles = rolesBefore, canSendForSignature = false, signatureEntityIds = Array.Empty<Guid>() }));
        Assert.Equal(HttpStatusCode.OK, revert.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/app-settings")).StatusCode);
        var reverted = await user.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.False(reverted.GetProperty("canSendForSignature").GetBoolean());
        Assert.Equal(entity, reverted.GetProperty("entityId").GetGuid());       // omitted = unchanged
    }

    // ---------- item 1: filters ----------

    [Fact]
    public async Task EmptyFilterParameters_AreNoFilter_OnEveryListAndReport()
    {
        var admin = await _factory.LoginAsync("mette");
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=1");
        var total = all.GetProperty("total").GetInt32();
        Assert.True(total > 0);

        const string empty = "q=&entityId=&departmentId=&typeId=&status=&ownerId=&counterpartyId=&currency=&endFrom=&endTo=&annualValueMin=&annualValueMax=&tag=";
        var filtered = await admin.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=1&" + empty);
        Assert.Equal(total, filtered.GetProperty("total").GetInt32());

        foreach (var path in new[]
        {
            "/api/contracts/export?" + empty,
            "/api/overview/analytics?" + empty,
            "/api/overview?entityId=",
            "/api/home?entityId=",
            "/api/renewals?entityId=",
            "/api/approvals/mine?entityId=",
            "/api/search?q=&entityId=",
            "/api/search/full?q=&entityId=&group=",
            "/api/contracts/distinct?column=currency&" + empty,
            "/api/people?q=&role=",
            "/api/counterparties?q=",
            "/api/templates?categoryId=&typeId=&language=&q="
        })
        {
            var response = await admin.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, path + " answered " + (int)response.StatusCode);
        }
    }

    [Fact]
    public async Task EntityScope_NarrowsEveryPage_AndDistinctValuesFollowAccess()
    {
        var admin = await _factory.LoginAsync("mette");
        var newcomer = await _factory.LoginAsync("nina");
        var sverige = SeedIds.Entity("DAFA Sverige AB");

        var everything = await admin.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=200");
        var scoped = await admin.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=200&entityId=" + sverige);
        Assert.True(scoped.GetProperty("total").GetInt32() < everything.GetProperty("total").GetInt32());
        Assert.All(scoped.GetProperty("items").EnumerateArray(), i => Assert.Equal(sverige, i.GetProperty("entityId").GetGuid()));

        var analyticsAll = await admin.GetFromJsonAsync<JsonElement>("/api/overview/analytics?includeEnded=true");
        var analyticsScoped = await admin.GetFromJsonAsync<JsonElement>("/api/overview/analytics?includeEnded=true&entityId=" + sverige);
        Assert.True(analyticsScoped.GetProperty("kpis").GetProperty("count").GetInt32() < analyticsAll.GetProperty("kpis").GetProperty("count").GetInt32());

        // Distinct values come from the contracts the caller may see: an administrator sees currencies, a newcomer sees none.
        var forAdmin = await admin.GetFromJsonAsync<JsonElement>("/api/contracts/distinct?column=currency");
        Assert.True(forAdmin.GetProperty("values").GetArrayLength() > 0);
        var forNewcomer = await newcomer.GetFromJsonAsync<JsonElement>("/api/contracts/distinct?column=currency");
        Assert.Equal(0, forNewcomer.GetProperty("values").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/contracts/distinct?column=nonsense")).StatusCode);
    }

    // ---------- item 4: branding ----------

    [Fact]
    public async Task Logo_IsUploadedByAdminsOnly_ServedAnonymouslyWithCaching_AndRejectedWhenUnsafe()
    {
        var admin = await _factory.LoginAsync("mette");
        var reader = await _factory.LoginAsync("thomas");
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/admin/branding/logo", FileForm("logo.png", R4.Png()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateApiClient().PostAsync("/api/admin/branding/logo", FileForm("logo.png", R4.Png()))).StatusCode);

        // Wrong content behind an allowed name, a script in an SVG, and a file that is too large.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/branding/logo", FileForm("logo.png", Encoding.ASCII.GetBytes("<html>not an image</html>")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/branding/logo", FileForm("logo.svg", R4.Svg("<script>alert(1)</script>")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/branding/logo", FileForm("logo.exe", R4.Png()))).StatusCode);
        var tooLarge = new byte[1024 * 1024 + 16];
        R4.Png().CopyTo(tooLarge, 0);
        var large = await admin.PostAsync("/api/admin/branding/logo", FileForm("logo.png", tooLarge));
        Assert.True(large.StatusCode == HttpStatusCode.BadRequest || large.StatusCode == HttpStatusCode.RequestEntityTooLarge);

        var uploaded = await admin.PostAsync("/api/admin/branding/logo?variant=full", FileForm("logo.svg", R4.Svg()));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var branding = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        var logoUrl = branding.GetProperty("logoUrl").GetString()!;
        Assert.StartsWith("branding/logo?variant=full&v=", logoUrl);
        Assert.Equal(JsonValueKind.Null, branding.GetProperty("darkLogoUrl").ValueKind);

        // Anonymous (the sign-in screen shows it): the versioned address is cacheable for good.
        var publicInfo = await anonymous.GetFromJsonAsync<JsonElement>("/api/branding");
        Assert.Equal(logoUrl, publicInfo.GetProperty("logoUrl").GetString());
        var image = await anonymous.GetAsync("/api/" + logoUrl);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/svg+xml", image.Content.Headers.ContentType!.MediaType);
        Assert.True(image.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromDays(365), image.Headers.CacheControl.MaxAge);
        Assert.Contains("immutable", image.Headers.CacheControl.ToString());
        Assert.Equal(BrandingController.LogoContentSecurityPolicy, string.Join("", image.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", string.Join("", image.Headers.GetValues("X-Content-Type-Options")));
        var etag = image.Headers.ETag!;

        // Without the version the browser must revalidate, and gets 304 when nothing changed.
        var unversioned = new HttpRequestMessage(HttpMethod.Get, "/api/branding/logo");
        unversioned.Headers.IfNoneMatch.Add(etag);
        var notModified = await anonymous.SendAsync(unversioned);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        var plain = await anonymous.GetAsync("/api/branding/logo");
        Assert.True(plain.Headers.CacheControl!.NoCache);

        // app-config and the sign-in configuration carry the same addresses.
        var config = await reader.GetFromJsonAsync<JsonElement>("/api/app-config");
        Assert.Equal(logoUrl, config.GetProperty("branding").GetProperty("logoUrl").GetString());
        Assert.False(string.IsNullOrEmpty(config.GetProperty("numberFormat").GetString()));
        Assert.False(string.IsNullOrEmpty(config.GetProperty("dateFormat").GetString()));
        var devConfig = await anonymous.GetFromJsonAsync<JsonElement>("/api/dev/config");
        Assert.Equal(logoUrl, devConfig.GetProperty("branding").GetProperty("logoUrl").GetString());

        // The dark logo is handled exactly like the logo.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/branding/logo?variant=dark")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/branding/logo?variant=dark", FileForm("dark.svg", R4.Svg("<script>alert(1)</script>")))).StatusCode);
        var dark = await admin.PostAsync("/api/admin/branding/logo?variant=dark", FileForm("dark.png", R4.Png()));
        Assert.Equal(HttpStatusCode.OK, dark.StatusCode);
        var darkUrl = (await dark.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("darkLogoUrl").GetString()!;
        Assert.StartsWith("branding/logo?variant=dark&v=", darkUrl);
        var darkImage = await anonymous.GetAsync("/api/" + darkUrl);
        Assert.Equal("image/png", darkImage.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", darkImage.Headers.CacheControl!.ToString());
        Assert.Equal(darkUrl, (await reader.GetFromJsonAsync<JsonElement>("/api/app-config")).GetProperty("branding").GetProperty("darkLogoUrl").GetString());

        // Deleting gives the default mark back.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync("/api/admin/branding/logo?variant=dark")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/admin/branding/logo?variant=dark")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/admin/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/branding/logo")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await anonymous.GetFromJsonAsync<JsonElement>("/api/branding")).GetProperty("logoUrl").ValueKind);
    }

    // ---------- theme ----------

    [Fact]
    public async Task Theme_IsAPersonalPreference_DefaultLight_Validated()
    {
        var user = await _factory.LoginAsync("thomas");
        var other = await _factory.LoginAsync("mette");

        var initial = await user.GetFromJsonAsync<JsonElement>("/api/me/preferences");
        Assert.Equal("light", initial.GetProperty("theme").GetString());

        foreach (var theme in new[] { "dark", "system", "light", "dark" })
        {
            var saved = await user.PutAsync("/api/me/preferences", Json(new { theme }));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(theme, (await user.GetFromJsonAsync<JsonElement>("/api/me/preferences")).GetProperty("theme").GetString());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PutAsync("/api/me/preferences", Json(new { theme = "neon" }))).StatusCode);
        Assert.Equal("dark", (await user.GetFromJsonAsync<JsonElement>("/api/me/preferences")).GetProperty("theme").GetString());

        // Somebody else's preference is untouched.
        Assert.Equal("light", (await other.GetFromJsonAsync<JsonElement>("/api/me/preferences")).GetProperty("theme").GetString());
        Assert.Equal(HttpStatusCode.OK, (await user.PutAsync("/api/me/preferences", Json(new { theme = "light" }))).StatusCode);
    }

    // ---------- item 6: e-signature ----------

    private async Task<Guid> ApprovedContractAsync(string ownerKey, string entityName)
    {
        var ownerId = SeedIds.User(ownerKey);
        var entityId = SeedIds.Entity(entityName);
        var id = Guid.Empty;
        await _factory.WithDbAsync(async db =>
        {
            var open = await db.SignatureRequests.AsNoTracking().Select(r => r.ContractId).ToListAsync();
            var candidates = await db.Contracts.Where(c => c.OwnerId == ownerId && c.EntityId == entityId).ToListAsync();
            var contract = candidates.OrderBy(c => c.ContractNo, StringComparer.Ordinal).First(c => !open.Contains(c.Id));
            contract.Status = ContractStatus.Approved;
            contract.SignedDate = null;
            contract.SigningMethod = null;
            await db.SaveChangesAsync();
            id = contract.Id;
        });
        return id;
    }

    [Fact]
    public async Task SendingForSignature_NeedsThePermission_AndTheSimulatedFlowEndsInAnActiveContract()
    {
        var admin = await _factory.LoginAsync("mette");      // administrator WITHOUT the signing permission
        var cfo = await _factory.LoginAsync("lars");         // may send for every legal entity
        var buyer = await _factory.LoginAsync("jonas");      // may send for DAFA A/S and DAFA Building Solutions A/S only

        var config = await cfo.GetFromJsonAsync<JsonElement>("/api/app-config");
        Assert.Equal("Simulated", config.GetProperty("eSign").GetProperty("provider").GetString());
        Assert.True(config.GetProperty("eSign").GetProperty("enabled").GetBoolean());

        var contractId = await ApprovedContractAsync("lars", "DAFA A/S");
        // From approval on, only an administrator adds a Main document.
        var upload = await admin.PostAsync("/api/contracts/" + contractId + "/documents", FileForm("Agreement to sign.pdf", ReviewTestHelpers.PdfBytes("to sign"), "Main"));
        Assert.True(upload.IsSuccessStatusCode, "upload answered " + (int)upload.StatusCode);
        var documentId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var body = new
        {
            documentIds = new[] { documentId },
            order = "Parallel",
            message = "Please sign.",
            signers = new object[]
            {
                new { kind = "External", name = "Anna Lindqvist", email = "anna@supplier.example", order = 1 },
                new { kind = "External", name = "Bo Ek", email = "bo@supplier.example", order = 2 }
            }
        };

        // Being an administrator is not enough.
        var refused = await admin.PostAsync("/api/contracts/" + contractId + "/signature-requests", Json(body));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("urn:dafa:problem:not-permitted", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/contracts/" + contractId + "/signature-permissions")).GetProperty("canSend").GetBoolean());

        // The permission is limited to legal entities: the buyer may not send a Polish contract.
        var polishId = await ApprovedContractAsync("jonas", "DAFA Polska sp. z o.o.");
        var polishUpload = await admin.PostAsync("/api/contracts/" + polishId + "/documents", FileForm("Polish agreement.pdf", ReviewTestHelpers.PdfBytes("pl"), "Main"));
        Assert.True(polishUpload.IsSuccessStatusCode);
        var polishDocument = (await polishUpload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var outsideLimit = await buyer.PostAsync("/api/contracts/" + polishId + "/signature-requests", Json(new
        {
            documentIds = new[] { polishDocument },
            signers = new object[] { new { kind = "External", name = "Jan Nowak", email = "jan@supplier.example", order = 1 } }
        }));
        Assert.Equal(HttpStatusCode.Forbidden, outsideLimit.StatusCode);
        Assert.Equal("urn:dafa:problem:not-permitted", (await outsideLimit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());

        // The CFO sends; both signers sign in the simulation; the contract becomes Active with the signed copy as Main document.
        Assert.True((await cfo.GetFromJsonAsync<JsonElement>("/api/contracts/" + contractId + "/signature-permissions")).GetProperty("canSend").GetBoolean());
        var sent = await cfo.PostAsync("/api/contracts/" + contractId + "/signature-requests", Json(body));
        Assert.True(sent.IsSuccessStatusCode, "send answered " + (int)sent.StatusCode + ": " + await sent.Content.ReadAsStringAsync());
        var request = await sent.Content.ReadFromJsonAsync<JsonElement>();
        var requestId = request.GetProperty("id").GetGuid();
        Assert.Equal("Sent", request.GetProperty("status").GetString());
        Assert.Equal("Simulated", request.GetProperty("provider").GetString());
        var signerIds = request.GetProperty("signers").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, signerIds.Count);

        // One open request per contract; manual "record signature" is refused meanwhile.
        Assert.Equal(HttpStatusCode.Conflict, (await cfo.PostAsync("/api/contracts/" + contractId + "/signature-requests", Json(body))).StatusCode);
        // Somebody without access to the request cannot simulate.
        var newcomer = await _factory.LoginAsync("nina");
        var foreign = await newcomer.PostAsync("/api/signature-requests/" + requestId + "/simulate", Json(new { signerId = signerIds[0], action = "Sign" }));
        Assert.True(foreign.StatusCode == HttpStatusCode.NotFound || foreign.StatusCode == HttpStatusCode.Forbidden);

        var first = await cfo.PostAsync("/api/signature-requests/" + requestId + "/simulate", Json(new { signerId = signerIds[0], action = "Sign" }));
        Assert.True(first.IsSuccessStatusCode, "simulate answered " + (int)first.StatusCode + ": " + await first.Content.ReadAsStringAsync());
        Assert.Equal("Sent", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var second = await cfo.PostAsync("/api/signature-requests/" + requestId + "/simulate", Json(new { signerId = signerIds[1], action = "Sign" }));
        var completed = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", completed.GetProperty("status").GetString());
        var signedDocumentId = completed.GetProperty("signedDocumentId").GetGuid();

        var detail = await cfo.GetFromJsonAsync<JsonElement>("/api/contracts/" + contractId);
        Assert.Equal("Active", detail.GetProperty("status").GetString());
        Assert.Equal("Completed", detail.GetProperty("signature").GetProperty("status").GetString());
        Assert.Contains(detail.GetProperty("documents").EnumerateArray(), d => d.GetProperty("id").GetGuid() == signedDocumentId && d.GetProperty("kind").GetString() == "Main");
        var signedFile = await cfo.GetAsync("/api/contracts/documents/" + signedDocumentId + "/content");
        Assert.Equal(HttpStatusCode.OK, signedFile.StatusCode);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(await signedFile.Content.ReadAsByteArrayAsync(), 0, 5));

        var list = await cfo.GetFromJsonAsync<JsonElement>("/api/contracts/" + contractId + "/signature-requests");
        Assert.Equal(1, list.GetArrayLength());
        await _factory.WithDbAsync(async db =>
        {
            var actions = await db.AuditEvents.AsNoTracking().Where(e => e.ContractId == contractId).Select(e => e.Action).ToListAsync();
            Assert.Contains("signature.sent", actions);
            Assert.Contains("signature.completed", actions);
        });
    }

    [Fact]
    public async Task Webhook_DoesNotExist_WhenNoSecretIsConfigured()
    {
        // No X-Requested-With on purpose: the route is exempt from that rule, and closed without a secret.
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsync(SignatureWebhookController.RoutePrefix + "/Simulated", Json(new { reference = "x", @event = "Signed" }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- storage (section 9a) ----------

    [Fact]
    public async Task StoragePages_AreForAdministrators_AndTheReportIsNullUntilTheFirstRun()
    {
        var admin = await _factory.LoginAsync("mette");
        var reader = await _factory.LoginAsync("thomas");

        var health = await admin.GetFromJsonAsync<JsonElement>("/api/admin/storage/health");
        Assert.Equal("Local", health.GetProperty("mode").GetString());
        Assert.Equal("Healthy", health.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Array, health.GetProperty("details").ValueKind);
        Assert.Equal(0, health.GetProperty("recentFailures").GetInt32());

        var run = await admin.PostAsync("/api/admin/storage/reconciliation/run", null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var report = await run.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", report.GetProperty("status").GetString());
        Assert.True(report.GetProperty("checkedDocuments").GetInt32() > 0);
        Assert.Equal(JsonValueKind.Array, report.GetProperty("items").ValueKind);
        var latest = await admin.GetFromJsonAsync<JsonElement>("/api/admin/storage/reconciliation");
        Assert.Equal(report.GetProperty("id").GetGuid(), latest.GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/storage/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/admin/storage/reconciliation/run", null)).StatusCode);

        // The folder link of a contract is for administrators only (null for everyone in Local mode without a stored link).
        var contractId = Guid.Empty;
        await _factory.WithDbAsync(async db =>
        {
            var ownerId = SeedIds.User("jonas");
            var contract = (await db.Contracts.Where(c => c.OwnerId == ownerId).ToListAsync()).OrderBy(c => c.ContractNo, StringComparer.Ordinal).Last();
            contract.StoreFolderUrl = "https://dafa.sharepoint.com/sites/contracts/Purchasing/" + contract.ContractNo;
            await db.SaveChangesAsync();
            contractId = contract.Id;
        });
        var owner = await _factory.LoginAsync("jonas");
        Assert.StartsWith("https://dafa.sharepoint.com/", (await admin.GetFromJsonAsync<JsonElement>("/api/contracts/" + contractId)).GetProperty("storeUrl").GetString());
        Assert.Equal(JsonValueKind.Null, (await owner.GetFromJsonAsync<JsonElement>("/api/contracts/" + contractId)).GetProperty("storeUrl").ValueKind);
    }
}

/// <summary>The e-signature webhook with a configured secret.</summary>
[Collection(ApiCollection.Name)]
public class Release4WebhookApiTests : IClassFixture<WebhookApiFactory>
{
    private readonly WebhookApiFactory _factory;

    public Release4WebhookApiTests(WebhookApiFactory factory)
    {
        _factory = factory;
    }

    private static HttpRequestMessage Post(string provider, string body)
    {
        return new HttpRequestMessage(HttpMethod.Post, SignatureWebhookController.RoutePrefix + "/" + provider)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task Webhook_NeedsTheSharedSecret_AndIsTheOnlyRouteWithoutTheRequestHeader()
    {
        // A plain client: no session cookie, no X-Requested-With (a provider sends neither).
        var provider = _factory.CreateClient();
        const string body = "{\"reference\":\"unknown-envelope\",\"event\":\"Signed\"}";

        // No proof, a wrong secret, a wrong signature: 401, and nothing is said about the envelope.
        Assert.Equal(HttpStatusCode.Unauthorized, (await provider.SendAsync(Post("Simulated", body))).StatusCode);
        var wrong = Post("Simulated", body);
        wrong.Headers.Add(SignatureWebhookController.SecretHeader, "not-the-secret-not-the-secret-123");
        Assert.Equal(HttpStatusCode.Unauthorized, (await provider.SendAsync(wrong)).StatusCode);
        var wrongSignature = Post("Simulated", body);
        wrongSignature.Headers.Add(SignatureWebhookController.HexSignatureHeader, Convert.ToHexString(new byte[32]));
        Assert.Equal(HttpStatusCode.Unauthorized, (await provider.SendAsync(wrongSignature)).StatusCode);

        // The secret itself.
        var right = Post("Simulated", body);
        right.Headers.Add(SignatureWebhookController.SecretHeader, WebhookApiFactory.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await provider.SendAsync(right)).StatusCode);

        // An HMAC-SHA256 of the body, in hex or (DocuSign Connect) in Base64.
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookApiFactory.Secret), Encoding.UTF8.GetBytes(body));
        var hex = Post("Simulated", body);
        hex.Headers.Add(SignatureWebhookController.HexSignatureHeader, Convert.ToHexString(mac).ToLowerInvariant());
        Assert.Equal(HttpStatusCode.NoContent, (await provider.SendAsync(hex)).StatusCode);
        var base64 = Post("DocuSign", "{}");
        base64.Headers.Add(SignatureWebhookController.DocuSignSignatureHeader,
            Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookApiFactory.Secret), Encoding.UTF8.GetBytes("{}"))));
        Assert.Equal(HttpStatusCode.NoContent, (await provider.SendAsync(base64)).StatusCode);
        // A signature over another body does not open the door.
        var replayed = Post("Simulated", body + " ");
        replayed.Headers.Add(SignatureWebhookController.HexSignatureHeader, Convert.ToHexString(mac));
        Assert.Equal(HttpStatusCode.Unauthorized, (await provider.SendAsync(replayed)).StatusCode);

        // The exemption is exactly this route: every other state-changing request still needs the header.
        Assert.Equal(HttpStatusCode.BadRequest, (await provider.PostAsync("/api/dev/login", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await provider.PostAsync(SignatureWebhookController.RoutePrefix + "/Simulated/extra", new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await provider.PostAsync("/api/esign/webhooks", new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await provider.PutAsync(SignatureWebhookController.RoutePrefix + "/Simulated", new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public void WebhookProof_IsComparedExactly()
    {
        const string secret = WebhookApiFactory.Secret;
        Assert.True(SignatureWebhookController.IsAuthentic(secret, "{}", secret, null, null));
        Assert.False(SignatureWebhookController.IsAuthentic(secret, "{}", secret + "x", null, null));
        Assert.False(SignatureWebhookController.IsAuthentic(secret, "{}", secret.Substring(1), null, null));
        Assert.False(SignatureWebhookController.IsAuthentic(secret, "{}", null, null, null));
        Assert.False(SignatureWebhookController.IsAuthentic(secret, "{}", "", "zz-not-hex", null));
        Assert.False(SignatureWebhookController.IsAuthentic(secret, "{}", "", "", "not base64 !"));
    }
}
