using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Api.Auth;
using Dafa.Contracts.Api.Hosting;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dafa.Contracts.Tests;

// HTTP-level tests for the findings of docs/REVIEW_BACKEND.md: access by id, scoped reads,
// CSRF header, deactivated users, the access code and its throttle, Entra mode.

/// <summary>Dev mode without an access code (the Development default), demo seed, its own database.</summary>
[Collection(ApiCollection.Name)]
public class ApiSecurityTests : IClassFixture<ApiFactory>
{
    private static readonly Guid Epdm = SeedIds.For("contract:epdm");   // DAFA A/S, Purchasing, owned by Jonas, active
    private static readonly Guid Tpe = SeedIds.For("contract:tpe");     // in approval, submitted by Jonas

    private readonly ApiFactory _factory;

    public ApiSecurityTests(ApiFactory factory)
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

    private static async Task<List<Guid>> ListIdsAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=200");
        return list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    // ---------- CSRF header ----------

    [Fact]
    public async Task StateChangingRequests_WithoutTheRequestedWithHeader_Get400()
    {
        var anonymous = _factory.CreateClient();
        var login = await anonymous.PostAsJsonAsync("/api/dev/login", new { userId = SeedIds.User("jonas") });
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
        Assert.Equal("application/problem+json", login.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsync("/api/auth/logout", null)).StatusCode);
        // Reading is not affected.
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/dev/config")).StatusCode);

        // A signed-in session is no exception: the header is required on every unsafe method.
        var owner = await _factory.LoginAsync("jonas");
        owner.DefaultRequestHeaders.Remove(ApiFactory.RequestedWithHeader);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/contracts/" + Epdm + "/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsync("/api/contracts/" + Epdm, Json(new { title = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync("/api/links/" + Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.PostAsync("/api/contracts/" + Epdm + "/documents", FileForm("a.pdf", ReviewTestHelpers.PdfBytes()))).StatusCode);

        // A wrong value does not count either.
        owner.DefaultRequestHeaders.Add(ApiFactory.RequestedWithHeader, "XMLHttpRequest");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/api/auth/logout", null)).StatusCode);
    }

    [Fact]
    public void OnlyGetAndHead_AreExemptFromTheHeader()
    {
        Assert.True(RequestedWithMiddleware.IsExempt("GET"));
        Assert.True(RequestedWithMiddleware.IsExempt("HEAD"));
        foreach (var method in new[] { "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "TRACE" })
        {
            Assert.False(RequestedWithMiddleware.IsExempt(method));
        }
    }

    // ---------- access by id ----------

    [Fact]
    public async Task ByIdEndpoints_Are403_ForAUserWithoutAccessToTheContract()
    {
        var admin = await _factory.LoginAsync("mette");
        var detail = await admin.GetFromJsonAsync<JsonElement>("/api/contracts/" + Epdm + "?peek=true");
        var documentId = detail.GetProperty("documents")[0].GetProperty("id").GetString();
        var obligationId = detail.GetProperty("obligations")[0].GetProperty("id").GetString();
        var linkId = detail.GetProperty("links")[0].GetProperty("id").GetString();
        var approval = await admin.GetFromJsonAsync<JsonElement>("/api/contracts/" + Tpe + "?peek=true");
        var stepId = approval.GetProperty("approval").GetProperty("stages")[0].GetProperty("steps").EnumerateArray()
            .First(s => s.GetProperty("status").GetString() == "Pending").GetProperty("id").GetString();

        var outsider = await _factory.LoginAsync("nina");   // no department, no role on any contract
        var contract = "/api/contracts/" + Epdm;
        var attempts = new (string Name, Func<Task<HttpResponseMessage>> Call)[]
        {
            ("contract detail", () => outsider.GetAsync(contract)),
            ("contract update", () => outsider.PutAsync(contract, Json(new { title = "Mine now" }))),
            ("submit", () => outsider.PostAsync(contract + "/submit", null)),
            ("sign", () => outsider.PostAsync(contract + "/sign", Json(new { signedDate = "2026-01-01", method = "Manual" }))),
            ("renewal decision", () => outsider.PostAsync(contract + "/renewal-decision", Json(new { decision = "Renegotiate", comment = "x" }))),
            ("document content", () => outsider.GetAsync("/api/documents/" + documentId + "/content")),
            ("document upload", () => outsider.PostAsync(contract + "/documents", FileForm("a.pdf", ReviewTestHelpers.PdfBytes()))),
            ("obligation list", () => outsider.GetAsync(contract + "/obligations")),
            ("obligation create", () => outsider.PostAsync(contract + "/obligations", Json(new { description = "x" }))),
            ("obligation update", () => outsider.PutAsync("/api/obligations/" + obligationId, Json(new { description = "x" }))),
            ("obligation complete", () => outsider.PostAsync("/api/obligations/" + obligationId + "/complete", null)),
            ("obligation delete", () => outsider.DeleteAsync("/api/obligations/" + obligationId)),
            ("link create", () => outsider.PostAsync(contract + "/links", Json(new { toContractId = Tpe, kind = "Related" }))),
            ("link delete", () => outsider.DeleteAsync("/api/links/" + linkId)),
            ("approval decision", () => outsider.PostAsync("/api/approvals/steps/" + stepId + "/decision", Json(new { decision = "Approve" }))),
            ("approval delegate", () => outsider.PostAsync("/api/approvals/steps/" + stepId + "/delegate", Json(new { userId = SeedIds.User("karin") })))
        };
        foreach (var attempt in attempts)
        {
            var response = await attempt.Call();
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                attempt.Name + " returned " + (int)response.StatusCode + " instead of 403");
        }

        // Nothing was changed by any of it.
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/contracts/" + Epdm + "?peek=true");
        Assert.Equal(detail.GetProperty("title").GetString(), after.GetProperty("title").GetString());
        Assert.Equal(detail.GetProperty("obligations").GetArrayLength(), after.GetProperty("obligations").GetArrayLength());
        Assert.Equal(detail.GetProperty("links").GetArrayLength(), after.GetProperty("links").GetArrayLength());
        Assert.Equal(detail.GetProperty("documents").GetArrayLength(), after.GetProperty("documents").GetArrayLength());
    }

    [Fact]
    public async Task ADepartmentMember_CanRead_ButNotChange_AContractTheyAreNotOn()
    {
        var planner = await _factory.LoginAsync("thomas");  // Production member, Reader only
        var ids = await ListIdsAsync(planner);
        var visible = "/api/contracts/" + ids[0];

        Assert.Equal(HttpStatusCode.OK, (await planner.GetAsync(visible + "?peek=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PutAsync(visible, Json(new { title = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsync(visible + "/sign", Json(new { signedDate = "2026-01-01" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await planner.PostAsync(visible + "/renewal-decision", Json(new { decision = "Renegotiate", comment = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await planner.PostAsync(visible + "/documents", FileForm("a.pdf", ReviewTestHelpers.PdfBytes()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await planner.PostAsync(visible + "/obligations", Json(new { description = "x" }))).StatusCode);
    }

    // ---------- scoped reads ----------

    [Fact]
    public async Task SearchExportHomeRenewalsAndOverview_ShowNothing_ToAUserWithoutAnyAccess()
    {
        var outsider = await _factory.LoginAsync("nina");

        var search = await outsider.GetFromJsonAsync<JsonElement>("/api/search?q=agreement");
        Assert.DoesNotContain(search.EnumerateArray(), r => r.GetProperty("kind").GetString() == "Contract");

        var csv = await outsider.GetStringAsync("/api/contracts/export");
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(lines);                                 // the header row only

        var home = await outsider.GetFromJsonAsync<JsonElement>("/api/home");
        Assert.Equal(0, home.GetProperty("myContracts").GetInt32());
        Assert.Equal(0, home.GetProperty("approvalsWaiting").GetInt32());
        Assert.Equal(0, home.GetProperty("renewingIn90").GetProperty("count").GetInt32());
        Assert.Equal(0, home.GetProperty("tasks").GetArrayLength());
        Assert.Equal(0, home.GetProperty("recent").GetArrayLength());
        Assert.All(home.GetProperty("noticeByMonth").EnumerateArray(), m => Assert.Equal(0, m.GetProperty("count").GetInt32()));

        var renewals = await outsider.GetFromJsonAsync<JsonElement>("/api/renewals?months=60");
        Assert.Equal(0, renewals.GetArrayLength());
        var mine = await outsider.GetFromJsonAsync<JsonElement>("/api/approvals/mine");
        Assert.Equal(0, mine.GetArrayLength());

        // Release 3: the overview is open to every user, computed over the contracts they can see.
        var overview = await outsider.GetFromJsonAsync<JsonElement>("/api/overview");
        Assert.Equal(0, overview.GetProperty("activeContracts").GetInt32());
        Assert.Equal(0, overview.GetProperty("valueByEntity").GetArrayLength());
    }

    [Fact]
    public async Task SearchExportAndRenewals_StayInsideWhatTheUserCanSee()
    {
        var planner = await _factory.LoginAsync("thomas");  // Production only
        var ids = await ListIdsAsync(planner);
        Assert.NotEmpty(ids);

        foreach (var term in new[] { "agreement", "supply", "lease", "C-20" })
        {
            var search = await planner.GetFromJsonAsync<JsonElement>("/api/search?q=" + term);
            foreach (var hit in search.EnumerateArray().Where(r => r.GetProperty("kind").GetString() == "Contract"))
            {
                Assert.Contains(hit.GetProperty("id").GetGuid(), ids);
            }
        }

        var csv = await planner.GetStringAsync("/api/contracts/export");
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(ids.Count, lines.Length - 1);
        Assert.All(lines.Skip(1), line => Assert.Contains("Production", line));

        var renewals = await planner.GetFromJsonAsync<JsonElement>("/api/renewals?months=60");
        Assert.All(renewals.EnumerateArray(), r => Assert.Contains(r.GetProperty("id").GetGuid(), ids));

        // Release 3: a plain department member gets the overview of the contracts they can see.
        var overview = await planner.GetFromJsonAsync<JsonElement>("/api/overview");
        Assert.True(overview.GetProperty("activeContracts").GetInt32() <= ids.Count);
    }

    [Fact]
    public async Task Overview_IsForManagement_AndCoversOnlyTheirEntities()
    {
        var director = await _factory.LoginAsync("karin");  // manages DAFA Sverige AB only
        var admin = await _factory.LoginAsync("mette");

        var own = await director.GetFromJsonAsync<JsonElement>("/api/overview");
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/overview");

        Assert.True(own.GetProperty("activeContracts").GetInt32() >= 1);
        Assert.True(own.GetProperty("activeContracts").GetInt32() < all.GetProperty("activeContracts").GetInt32());
        Assert.All(own.GetProperty("valueByEntity").EnumerateArray(), v => Assert.Equal("DAFA Sverige AB", v.GetProperty("name").GetString()));
        Assert.All(own.GetProperty("changeOfControl").EnumerateArray(), c => Assert.Equal("DAFA Sverige AB", c.GetProperty("entityName").GetString()));

        var list = await director.GetFromJsonAsync<JsonElement>("/api/contracts?pageSize=200");
        Assert.All(list.GetProperty("items").EnumerateArray(), i => Assert.Equal("DAFA Sverige AB", i.GetProperty("entityName").GetString()));
        var renewals = await director.GetFromJsonAsync<JsonElement>("/api/renewals?months=60");
        Assert.All(renewals.EnumerateArray(), r => Assert.Equal("DAFA Sverige AB", r.GetProperty("entityName").GetString()));
    }

    // ---------- intake ----------

    [Fact]
    public async Task AnUploadId_CannotBeUsedByAnotherUser()
    {
        var jonas = await _factory.LoginAsync("jonas");
        var extract = await jonas.PostAsync("/api/intake/extract", FileForm("Supply contract.pdf", ReviewTestHelpers.PdfBytes()));
        Assert.Equal(HttpStatusCode.OK, extract.StatusCode);
        var uploadId = (await extract.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetGuid();

        var peter = await _factory.LoginAsync("peter");
        var body = new
        {
            title = "Borrowed upload",
            typeId = SeedIds.ContractType(SeedNames.TypeIt),
            // Release 4: a counterparty from the master data (free text is no longer accepted).
            supplierId = SeedIds.Supplier("S-10010"),
            entityId = SeedIds.Entity("DAFA A/S"),
            departmentId = SeedIds.Department("IT"),
            ownerId = SeedIds.User("peter"),
            reminderRecipient = "Owner",
            startDate = "2026-01-01",
            endDate = "2027-12-31",
            noticePeriodDays = 90,
            autoRenew = false,
            annualValue = 1000,
            currency = "DKK",
            changeOfControl = false,
            uploadId
        };

        var response = await peter.PostAsync("/api/contracts", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("uploadId", out _));
        await _factory.WithDbAsync(async db =>
        {
            Assert.True(await db.PendingUploads.AnyAsync(u => u.Id == uploadId));
            Assert.False(await db.Contracts.AnyAsync(c => c.Title == "Borrowed upload"));
        });
    }

    [Fact]
    public async Task Intake_RejectsADisguisedFile()
    {
        var jonas = await _factory.LoginAsync("jonas");
        var response = await jonas.PostAsync("/api/intake/extract", FileForm("contract.pdf", new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- deactivated users (M10) ----------

    [Fact]
    public async Task ADeactivatedUser_WithALiveCookie_Gets401_OnEveryEndpoint()
    {
        var userId = Guid.NewGuid();
        await _factory.WithDbAsync(async db =>
        {
            db.Users.Add(new AppUser { Id = userId, Name = "Leaving Soon", Email = "leaving@dafa-demo.example", Initials = "LS", Roles = AppRole.Reader, IsActive = true });
            await db.SaveChangesAsync();
        });
        var client = await _factory.LoginAsync(userId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/suppliers")).StatusCode);

        await _factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.IsActive = false;
            await db.SaveChangesAsync();
        });

        foreach (var path in new[]
        {
            "/api/me", "/api/lookups", "/api/suppliers", "/api/home", "/api/contracts", "/api/templates", "/api/reminder-settings",
            "/api/approvals/preview?value=1000", "/api/approvals/mine", "/api/search?q=supply", "/api/renewals", "/api/admin/outbox"
        })
        {
            var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, path + " returned " + (int)response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/contracts", Json(new { title = "x" }))).StatusCode);
    }

    // ---------- API contract ----------

    [Fact]
    public async Task EnumsAsNumbers_AreRejected()
    {
        var jonas = await _factory.LoginAsync("jonas");
        var response = await jonas.PostAsync("/api/contracts/" + Epdm + "/renewal-decision", Json(new { decision = 2, comment = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApprovedTerms_AreLockedOverHttp_WithTheLockedFieldsProblemType()
    {
        var jonas = await _factory.LoginAsync("jonas");
        var detail = await jonas.GetFromJsonAsync<JsonElement>("/api/contracts/" + Epdm + "?peek=true");
        var locked = detail.GetProperty("lockedFields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("annualValue", locked);
        Assert.Contains("counterpartyName", locked);
        Assert.DoesNotContain("paymentTerms", locked);
        Assert.True(detail.GetProperty("canEdit").GetBoolean());

        var body = new Dictionary<string, object?>();
        foreach (var field in new[]
        {
            "title", "typeId", "supplierId", "counterpartyName", "entityId", "departmentId", "ownerId", "delegateId", "responsibleId",
            "reminderRecipient", "startDate", "endDate", "openEnded", "noticePeriodDays", "autoRenew", "renewalMonths", "annualValue",
            "totalValue", "currency", "paymentTerms", "priceAdjustment", "priceAdjustmentDate", "governingLaw", "liabilityCap",
            "exclusivity", "changeOfControl", "summary"
        })
        {
            body[field] = detail.GetProperty(field);
        }

        // Owners keep the unlocked fields.
        body["paymentTerms"] = "Net 60 days";
        var allowed = await jonas.PutAsync("/api/contracts/" + Epdm, Json(body));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        body["annualValue"] = 99_000_000;
        var refused = await jonas.PutAsync("/api/contracts/" + Epdm, Json(body));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProblemTypes.LockedFields, problem.GetProperty("type").GetString());
        Assert.StartsWith("Approved terms are locked.", problem.GetProperty("detail").GetString());

        // An invalid state has its own type, so the UI does not call it a stale update.
        var submit = await jonas.PostAsync("/api/contracts/" + Epdm + "/submit", null);
        Assert.Equal(HttpStatusCode.Conflict, submit.StatusCode);
        Assert.Equal(ProblemTypes.InvalidState, (await submit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Documents_OverHttp_TypeAllowlist_MainLock_AndSafeDownloadHeaders()
    {
        var jonas = await _factory.LoginAsync("jonas");
        var documents = "/api/contracts/" + SeedIds.For("contract:tapes") + "/documents";   // active, owned by Jonas

        Assert.Equal(HttpStatusCode.BadRequest, (await jonas.PostAsync(documents, FileForm("tool.exe", new byte[] { 0x4D, 0x5A, 0x90 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await jonas.PostAsync(documents, FileForm("page.html", Encoding.UTF8.GetBytes("<script>alert(1)</script>")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await jonas.PostAsync(documents, FileForm("fake.pdf", new byte[] { 0x4D, 0x5A, 0x90 }))).StatusCode);

        var main = await jonas.PostAsync(documents, FileForm("New main.pdf", ReviewTestHelpers.PdfBytes(), "Main"));
        Assert.Equal(HttpStatusCode.Conflict, main.StatusCode);
        Assert.Equal(ProblemTypes.LockedFields, (await main.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());

        var appendix = await jonas.PostAsync(documents, FileForm("Appendix A.pdf", ReviewTestHelpers.PdfBytes(), "Appendix"));
        Assert.Equal(HttpStatusCode.OK, appendix.StatusCode);
        var detail = await appendix.Content.ReadFromJsonAsync<JsonElement>();
        var uploaded = detail.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "Appendix A.pdf");

        var download = await jonas.GetAsync("/api/documents/" + uploaded.GetProperty("id").GetString() + "/content");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("nosniff", download.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(ReviewTestHelpers.PdfBytes(), await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Lookups_CarryRoles_AndTheDetailSaysWhoSubmitted()
    {
        var jonas = await _factory.LoginAsync("jonas");

        var lookups = await jonas.GetFromJsonAsync<JsonElement>("/api/lookups");
        var users = lookups.GetProperty("users").EnumerateArray().ToList();
        Assert.All(users, u => Assert.Equal(JsonValueKind.Array, u.GetProperty("roles").ValueKind));
        var legal = users.Single(u => u.GetProperty("id").GetGuid() == SeedIds.User("sofie"));
        Assert.Contains("Approver", legal.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        var newcomer = users.Single(u => u.GetProperty("id").GetGuid() == SeedIds.User("nina"));
        Assert.DoesNotContain("Approver", newcomer.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        var inApproval = await jonas.GetFromJsonAsync<JsonElement>("/api/contracts/" + Tpe + "?peek=true");
        Assert.Equal(SeedIds.User("jonas"), inApproval.GetProperty("submittedById").GetGuid());
        var neverSubmitted = await jonas.GetFromJsonAsync<JsonElement>("/api/contracts/" + Epdm + "?peek=true");
        Assert.Equal(JsonValueKind.Null, neverSubmitted.GetProperty("submittedById").ValueKind);
    }

    [Fact]
    public async Task SeededApprovals_NeverHaveTheOwnerOrTheSubmitterAsApprover()
    {
        await _factory.WithDbAsync(async db =>
        {
            var instances = await db.ApprovalInstances.AsNoTracking().ToListAsync();
            var steps = await db.ApprovalSteps.AsNoTracking().ToListAsync();
            var contracts = await db.Contracts.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c);
            Assert.True(instances.Count >= 6, "Expected the demo seed to contain six approvals, got " + instances.Count);
            foreach (var instance in instances)
            {
                var contract = contracts[instance.ContractId];
                foreach (var step in steps.Where(s => s.InstanceId == instance.Id))
                {
                    Assert.True(step.ApproverId != contract.OwnerId, contract.Title + ": the owner is an approver (" + step.RoleLabel + ")");
                    Assert.True(step.ApproverId != instance.StartedById, contract.Title + ": the submitter is an approver (" + step.RoleLabel + ")");
                }
            }
            // The TPE supply agreement is owned by the head of Purchasing: his slot went to a substitute.
            Assert.Contains(steps, s => s.ContractId == Tpe && s.RoleLabel.Contains("instead of Jonas Berg"));
        });
    }
}

/// <summary>A test site as deployed: Dev mode behind a shared access code.</summary>
public sealed class AccessCodeApiFactory : ApiFactory
{
    public const string Code = "correct-horse-battery";

    public AccessCodeApiFactory() : base(new Dictionary<string, string?> { ["Auth__Dev__AccessCode"] = Code })
    {
    }
}

[Collection(ApiCollection.Name)]
public class AccessCodeApiTests : IClassFixture<AccessCodeApiFactory>
{
    private readonly AccessCodeApiFactory _factory;

    public AccessCodeApiTests(AccessCodeApiFactory factory)
    {
        _factory = factory;
    }

    private static HttpRequestMessage UsersRequest(string? code)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/dev/users");
        if (code is not null)
        {
            request.Headers.Add("X-Dev-Access-Code", code);
        }
        return request;
    }

    [Fact]
    public async Task DevUsers_AndLogin_NeedTheAccessCode()
    {
        var client = _factory.CreateApiClient();

        var config = await client.GetFromJsonAsync<JsonElement>("/api/dev/config");
        Assert.True(config.GetProperty("accessCodeRequired").GetBoolean());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(UsersRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(UsersRequest("not-the-code"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(UsersRequest(AccessCodeApiFactory.Code))).StatusCode);

        var userId = SeedIds.User("jonas");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/dev/login", new { userId })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/dev/login", new { userId, accessCode = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);

        var ok = await client.PostAsJsonAsync("/api/dev/login", new { userId, accessCode = AccessCodeApiFactory.Code });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);

        // With a session for a known user the switcher works without sending the code again.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(UsersRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/dev/login", new { userId = SeedIds.User("peter") })).StatusCode);
    }

    [Fact]
    public async Task ACookieOfADeactivatedUser_DoesNotReplaceTheAccessCode()
    {
        var userId = Guid.NewGuid();
        await _factory.WithDbAsync(async db =>
        {
            db.Users.Add(new AppUser { Id = userId, Name = "Former Tester", Email = "former@dafa-demo.example", Initials = "FT", Roles = AppRole.Reader, IsActive = true });
            await db.SaveChangesAsync();
        });
        var client = await _factory.LoginAsync(userId, AccessCodeApiFactory.Code);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(UsersRequest(null))).StatusCode);

        await _factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.IsActive = false;
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(UsersRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/dev/login", new { userId = SeedIds.User("mette") })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }
}

/// <summary>Its own host, because the throttle is per app instance and would block the other tests.</summary>
[Collection(ApiCollection.Name)]
public class AccessCodeThrottleApiTests : IClassFixture<AccessCodeApiFactory>
{
    private readonly AccessCodeApiFactory _factory;

    public AccessCodeThrottleApiTests(AccessCodeApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TenWrongCodes_BlockFurtherAttempts_EvenWithTheRightCode()
    {
        var client = _factory.CreateApiClient();
        var userId = SeedIds.User("jonas");

        for (var i = 0; i < AccessCodeThrottle.MaxFailures; i++)
        {
            var wrong = await client.PostAsJsonAsync("/api/dev/login", new { userId, accessCode = "guess-number-" + i });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var blocked = await client.PostAsJsonAsync("/api/dev/login", new { userId, accessCode = "one-more-guess" });
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
        var right = await client.PostAsJsonAsync("/api/dev/login", new { userId, accessCode = AccessCodeApiFactory.Code });
        Assert.Equal((HttpStatusCode)429, right.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }
}

public class AccessCodeThrottleTests
{
    [Fact]
    public void BlocksAfterTenFailuresPerClient_AndOpensAgainAfterTheWindow()
    {
        var now = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);
        var throttle = new AccessCodeThrottle(() => now);

        for (var i = 0; i < AccessCodeThrottle.MaxFailures - 1; i++)
        {
            throttle.RecordFailure("10.0.0.1");
        }
        Assert.False(throttle.IsBlocked("10.0.0.1"));
        throttle.RecordFailure("10.0.0.1");
        Assert.True(throttle.IsBlocked("10.0.0.1"));
        Assert.False(throttle.IsBlocked("10.0.0.2"));       // counted per client address

        now = now.AddMinutes(9);
        Assert.True(throttle.IsBlocked("10.0.0.1"));
        now = now.AddMinutes(2);
        Assert.False(throttle.IsBlocked("10.0.0.1"));
        throttle.RecordFailure("10.0.0.1");                  // a new window starts at one
        Assert.False(throttle.IsBlocked("10.0.0.1"));
    }
}

/// <summary>
/// Production sign-in mode with a placeholder app registration. No request here reaches Microsoft:
/// only anonymous endpoints are called.
/// </summary>
public sealed class EntraApiFactory : ApiFactory
{
    public EntraApiFactory() : base(new Dictionary<string, string?>
    {
        ["Auth__Mode"] = "Entra",
        ["Seed__DemoData"] = "true",                          // must be ignored in Entra mode
        ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
        ["AzureAd__TenantId"] = "11111111-1111-1111-1111-111111111111",
        ["AzureAd__ClientId"] = "22222222-2222-2222-2222-222222222222",
        ["AzureAd__ClientSecret"] = "not-a-real-secret"
    })
    {
    }
}

[Collection(ApiCollection.Name)]
public class EntraModeApiTests : IClassFixture<EntraApiFactory>
{
    private readonly EntraApiFactory _factory;

    public EntraModeApiTests(EntraApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DevEndpoints_Are404_InEntraMode()
    {
        var client = _factory.CreateApiClient();

        var config = await client.GetFromJsonAsync<JsonElement>("/api/dev/config");
        Assert.Equal("Entra", config.GetProperty("authMode").GetString());
        Assert.False(config.GetProperty("accessCodeRequired").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/dev/users")).StatusCode);
        var login = await client.PostAsJsonAsync("/api/dev/login", new { userId = SeedIds.User("mette") });
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task DemoData_IsNeverSeeded_InEntraMode()
    {
        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.Users.CountAsync());
            Assert.Equal(0, await db.Contracts.CountAsync());
            // Reference data is still there.
            Assert.Equal(9, await db.LegalEntities.CountAsync());
            Assert.True(await db.ApprovalRules.AnyAsync());
        });
    }
}

/// <summary>M9: mapping an Entra sign-in to an AppUser.</summary>
public class EntraProvisioningTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
    {
        var data = new Dictionary<string, string?>();
        foreach (var pair in values)
        {
            data[pair.Key] = pair.Value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static ClaimsPrincipal EntraPrincipal(string? objectId, string email, string name)
    {
        var claims = new List<Claim> { new("preferred_username", email), new("name", name) };
        if (objectId is not null)
        {
            claims.Add(new Claim("oid", objectId));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestEntra"));
    }

    private static async Task<ClaimsPrincipal> SignInAsync(TestWorld world, IConfiguration configuration, string? objectId, string email, string name)
    {
        using var db = world.NewDb();
        var transformation = new AppUserClaimsTransformation(db, new AuthSettings(AuthModes.Entra, null), configuration);
        return await transformation.TransformAsync(EntraPrincipal(objectId, email, name));
    }

    private static Guid? AppUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(AppClaims.AppUserId)?.Value;
        return raw is null ? null : Guid.Parse(raw);
    }

    [Fact]
    public async Task FirstSignIn_CreatesOneReader_AndLaterSignInsReuseIt()
    {
        using var world = new TestWorld();
        var config = Config();

        var first = await SignInAsync(world, config, "oid-1", "new.person@dafa.example", "New Person");
        var second = await SignInAsync(world, config, "oid-1", "new.person@dafa.example", "New Person");
        var third = await SignInAsync(world, config, "oid-1", "renamed@dafa.example", "New Person");

        Assert.NotNull(AppUserId(first));
        Assert.Equal(AppUserId(first), AppUserId(second));
        Assert.Equal(AppUserId(first), AppUserId(third));
        Assert.True(first.IsInRole("Reader"));
        Assert.False(first.IsInRole("Admin"));
        using var db = world.NewDb();
        var row = Assert.Single(db.Users.AsNoTracking().Where(u => u.ExternalId == "oid-1").ToList());
        Assert.Equal(AppRole.Reader, row.Roles);
    }

    [Fact]
    public async Task AnEmailThatBelongsToAnotherDirectoryObject_IsNotLinked()
    {
        using var world = new TestWorld();
        Guid formerId;
        using (var db = world.NewDb())
        {
            // The previous holder of the address was an administrator.
            var former = db.Users.Single(u => u.Id == world.Admin);
            former.ExternalId = "oid-former-holder";
            formerId = former.Id;
            db.SaveChanges();
        }

        var principal = await SignInAsync(world, Config(), "oid-new-holder", "admin.user@example.test", "New Holder");

        Assert.NotNull(AppUserId(principal));
        Assert.NotEqual(formerId, AppUserId(principal));
        Assert.False(principal.IsInRole("Admin"));
        using var check = world.NewDb();
        Assert.Equal("oid-former-holder", check.Users.Single(u => u.Id == formerId).ExternalId);
        Assert.Equal(2, check.Users.Count(u => u.Email == "admin.user@example.test"));
    }

    [Fact]
    public async Task ARowWithoutAnObjectId_IsLinkedByEmail_AndKeepsItsRoles()
    {
        using var world = new TestWorld();

        var principal = await SignInAsync(world, Config(), "oid-legal", "legal.user@example.test", "Legal User");

        Assert.Equal(world.Legal, AppUserId(principal));
        Assert.True(principal.IsInRole("Approver"));
        using var db = world.NewDb();
        Assert.Equal("oid-legal", db.Users.Single(u => u.Id == world.Legal).ExternalId);
    }

    [Fact]
    public async Task BootstrapAdmins_BecomeAdministrators_EvenWithoutAutoProvisioning()
    {
        using var world = new TestWorld();
        var config = Config(
            ("Auth:Entra:AutoProvision", "false"),
            ("Auth:BootstrapAdmins:0", "First.Admin@dafa.example"),
            ("Auth:BootstrapAdmins:1", "second.admin@dafa.example"));

        var admin = await SignInAsync(world, config, "oid-admin", "first.admin@dafa.example", "First Admin");
        var someoneElse = await SignInAsync(world, config, "oid-other", "other@dafa.example", "Other Person");

        Assert.NotNull(AppUserId(admin));
        Assert.True(admin.IsInRole("Admin"));
        Assert.Null(AppUserId(someoneElse));                 // not provisioned: no app user, so 403 everywhere
        using var db = world.NewDb();
        Assert.False(db.Users.Any(u => u.ExternalId == "oid-other"));
    }

    [Fact]
    public void BootstrapAdmins_CanAlsoBeOneSeparatedString()
    {
        var admins = AppUserClaimsTransformation.ReadBootstrapAdmins(Config(("Auth:BootstrapAdmins", "a@dafa.example; b@dafa.example,c@dafa.example")));
        Assert.Equal(3, admins.Count);
        Assert.Contains("B@DAFA.example", admins);
        Assert.Empty(AppUserClaimsTransformation.ReadBootstrapAdmins(Config()));
    }

    [Fact]
    public async Task WithoutAnObjectId_OrForADeactivatedUser_ThereIsNoAppUser()
    {
        using var world = new TestWorld();

        var noObjectId = await SignInAsync(world, Config(), null, "legal.user@example.test", "Legal User");
        Assert.Null(AppUserId(noObjectId));

        using (var db = world.NewDb())
        {
            var user = db.Users.Single(u => u.Id == world.Outsider);
            user.ExternalId = "oid-outsider";
            user.IsActive = false;
            db.SaveChanges();
        }
        var deactivated = await SignInAsync(world, Config(), "oid-outsider", "outsider.user@example.test", "Outsider User");
        Assert.Null(AppUserId(deactivated));
    }

    [Fact]
    public void TheDatabase_AllowsOneUserPerObjectId_AndManyWithoutOne()
    {
        using var world = new TestWorld();
        using var db = world.NewDb();
        Assert.True(db.Users.Count(u => u.ExternalId == null) > 1);

        db.Users.Add(new AppUser { Id = Guid.NewGuid(), Name = "A", Email = "a@dafa.example", Initials = "A", ExternalId = "same-oid" });
        db.SaveChanges();
        db.Users.Add(new AppUser { Id = Guid.NewGuid(), Name = "B", Email = "b@dafa.example", Initials = "B", ExternalId = "same-oid" });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }
}
