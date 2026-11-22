using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Api.Hosting;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Services;
using Dafa.Contracts.Infrastructure.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dafa.Contracts.Tests;

// SharePoint hardening ("the app is the only door"). The Graph client and the SharePoint store are
// exercised against a scripted HTTP handler; nothing here talks to Microsoft.

public sealed class FixedTokenProvider : IAccessTokenProvider
{
    public Task<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default) => Task.FromResult("test-token");
}

public class GraphResilienceTests
{
    private static HttpResponseMessage Status(HttpStatusCode status, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfterSeconds is not null)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds.Value));
        }
        return response;
    }

    private sealed class Rig
    {
        public Rig(Func<int, HttpResponseMessage> respond, GraphResilienceOptions? options = null)
        {
            Options = options ?? new GraphResilienceOptions();
            Inner = new ScriptedHandler(_ =>
            {
                Calls++;
                return respond(Calls);
            });
            Handler = new GraphResilienceHandler(Health, Options, (time, _) => { Waits.Add(time); return Task.CompletedTask; }, () => Now, () => 0d)
            {
                InnerHandler = Inner
            };
            Client = new HttpClient(Handler);
        }

        public int Calls { get; private set; }
        public DateTime Now { get; set; } = new(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);
        public List<TimeSpan> Waits { get; } = new();
        public StorageHealthState Health { get; } = new();
        public GraphResilienceOptions Options { get; }
        public ScriptedHandler Inner { get; }
        public GraphResilienceHandler Handler { get; }
        public HttpClient Client { get; }
    }

    [Fact]
    public async Task Throttled_429_IsRetried_AfterTheRetryAfterTime()
    {
        var rig = new Rig(call => call == 1 ? Status(HttpStatusCode.TooManyRequests, 7) : Status(HttpStatusCode.OK));

        var response = await rig.Client.GetAsync("https://graph.microsoft.com/v1.0/sites/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, rig.Calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, rig.Waits);          // exactly what SharePoint asked for
        Assert.NotNull(rig.Health.LastSuccessAt);
        Assert.Equal("Closed", rig.Health.BreakerState);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task TransientErrors_AreRetriedWithBackoff_ABoundedNumberOfTimes_ThenBecomeStorageUnavailable(HttpStatusCode status)
    {
        var rig = new Rig(_ => Status(status));

        var error = await Assert.ThrowsAsync<StorageUnavailableException>(() => rig.Client.GetAsync("https://graph.microsoft.com/v1.0/sites/x"));

        Assert.Equal(4, rig.Calls);                                          // MaxAttempts
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, rig.Waits);   // exponential, jitter 0 here
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(StorageProblemTypes.Unavailable, error.ProblemType);
        Assert.Equal("SharePoint is busy. Try again in a moment.", error.Message);
        Assert.Contains(((int)status).ToString(), error.TechnicalDetail);
        Assert.NotNull(rig.Health.LastFailureAt);
        Assert.Equal(1, rig.Health.RecentFailures(rig.Now));
    }

    [Fact]
    public async Task ALongRetryAfter_IsNotWaitedFor_TheCallerIsToldHowLong()
    {
        var rig = new Rig(_ => Status(HttpStatusCode.TooManyRequests, 120));

        var error = await Assert.ThrowsAsync<StorageUnavailableException>(() => rig.Client.GetAsync("https://graph.microsoft.com/v1.0/sites/x"));

        Assert.Equal(1, rig.Calls);
        Assert.Empty(rig.Waits);
        Assert.Equal(120, error.RetryAfterSeconds);
    }

    [Fact]
    public async Task DefiniteAnswers_AreNotRetried_AndJitterStaysWithinBounds()
    {
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Conflict, HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError })
        {
            var rig = new Rig(_ => Status(status));
            Assert.Equal(status, (await rig.Client.GetAsync("https://graph.microsoft.com/v1.0/sites/x")).StatusCode);
            Assert.Equal(1, rig.Calls);
        }
        var jittery = new GraphResilienceHandler(new StorageHealthState(), new GraphResilienceOptions(), null, null, () => 1d);
        Assert.Equal(TimeSpan.FromSeconds(1.25), jittery.BackoffFor(1));
        Assert.Equal(TimeSpan.FromSeconds(5), jittery.BackoffFor(3));
        Assert.Equal(TimeSpan.FromSeconds(20), jittery.BackoffFor(10));       // capped at MaxDelay
    }

    [Fact]
    public async Task ConnectionFailures_AndTimeouts_AreRetried_AndRequestContentIsSentAgain()
    {
        var rig = new Rig(call => call < 3 ? throw new HttpRequestException("connection reset") : Status(HttpStatusCode.Created));
        using var request = GraphCalls.Request(HttpMethod.Put, "https://graph.microsoft.com/v1.0/drives/d/items/f:/a.pdf:/content", "token", new ByteArrayContent(Encoding.ASCII.GetBytes("file-bytes")), true);

        var response = await rig.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(3, rig.Calls);
        Assert.All(rig.Inner.Requests, r => Assert.Equal("file-bytes", r.Body));
        Assert.All(rig.Inner.Requests, r => Assert.Equal("Bearer token", r.Authorization));

        var slow = new GraphResilienceOptions { MaxAttempts = 2, MetadataTimeout = TimeSpan.FromMilliseconds(50) };
        var hanging = new GraphResilienceHandler(new StorageHealthState(), slow, (_, _) => Task.CompletedTask, null, () => 0d) { InnerHandler = new HangingHandler() };
        var timedOut = await Assert.ThrowsAsync<StorageUnavailableException>(() => new HttpClient(hanging).GetAsync("https://graph.microsoft.com/v1.0/sites/x"));
        Assert.Contains("No answer", timedOut.TechnicalDetail);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterRepeatedFailures_RefusesWithoutCalling_AndClosesAgain()
    {
        var healthy = false;
        var rig = new Rig(_ => healthy ? Status(HttpStatusCode.OK) : Status(HttpStatusCode.ServiceUnavailable),
            new GraphResilienceOptions { MaxAttempts = 1, BreakerFailures = 3, BreakerOpenFor = TimeSpan.FromSeconds(30) });

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<StorageUnavailableException>(() => rig.Client.GetAsync("https://graph.microsoft.com/v1.0/sites/x"));
        }
        Assert.Equal("Open", rig.Health.BreakerState);
        Assert.Equal(3, rig.Calls);

        // Open: refused at once, the store is not called, and the answer says when to come back.
        var refused = await Assert.ThrowsAsync<StorageUnavailableException>(() => rig.Client.GetAsync("https://graph.microsoft.com/v1.0/sites/x"));
        Assert.Equal(3, rig.Calls);
        Assert.Equal(30, refused.RetryAfterSeconds);

        // After the pause one call is let through; it fails, so the breaker opens again at once.
        rig.Now = rig.Now.AddSeconds(31);
        await Assert.ThrowsAsync<StorageUnavailableException>(() => rig.Client.GetAsync("https://graph.microsoft.com/v1.0/sites/x"));
        Assert.Equal(4, rig.Calls);
        Assert.Equal("Open", rig.Health.BreakerState);

        // The store is back: the next call after the pause closes the breaker.
        healthy = true;
        rig.Now = rig.Now.AddSeconds(31);
        Assert.Equal(HttpStatusCode.OK, (await rig.Client.GetAsync("https://graph.microsoft.com/v1.0/sites/x")).StatusCode);
        Assert.Equal("Closed", rig.Health.BreakerState);
        Assert.Equal(0, rig.Health.ConsecutiveFailures);
    }

    [Fact]
    public void RetryAfter_IsReadAsSecondsOrAsADate()
    {
        var now = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.FromSeconds(12), GraphResilienceHandler.ReadRetryAfter(Status(HttpStatusCode.TooManyRequests, 12), now));
        var dated = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        dated.Headers.RetryAfter = new RetryConditionHeaderValue(new DateTimeOffset(now.AddSeconds(45)));
        Assert.Equal(TimeSpan.FromSeconds(45), GraphResilienceHandler.ReadRetryAfter(dated, now));
        Assert.Null(GraphResilienceHandler.ReadRetryAfter(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), now));
    }
}

public class SharePointStoreTests
{
    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static SharePointDocumentStore Store(ScriptedHandler handler) =>
        new(new StaticHttpClientFactory(handler), new FixedTokenProvider(), NullLogger<SharePointDocumentStore>.Instance);

    private static DocumentLocation Location(string siteId, string? library = "Purchasing") =>
        new(Guid.NewGuid(), "C-2026-0012", "DAFA A/S", "Purchasing", "https://dafa.sharepoint.com/sites/contracts-dafa", siteId, library);

    /// <summary>A small fake of the Graph drive API: one site with libraries, folders by name, files by name.</summary>
    private sealed class FakeDrive
    {
        public List<string> Libraries { get; } = new() { "Purchasing", "IT" };
        public HashSet<string> Folders { get; } = new();
        public Dictionary<string, byte[]> Files { get; } = new();
        public int Sessions { get; private set; }
        public List<string> Ranges { get; } = new();

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (request.Method == HttpMethod.Get && url.Contains("/drives?"))
            {
                return Json(HttpStatusCode.OK, "{\"value\":[" + string.Join(",", Libraries.Select(l => "{\"id\":\"drive-" + l + "\",\"name\":\"" + l + "\"}")) + "]}");
            }
            if (request.Method == HttpMethod.Post && url.EndsWith("/root/children", StringComparison.Ordinal))
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("\"@microsoft.graph.conflictBehavior\":\"fail\"", body);
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
                return Folders.Add(name)
                    ? Json(HttpStatusCode.Created, "{\"id\":\"folder-" + name + "\",\"webUrl\":\"https://dafa.sharepoint.com/sites/contracts-dafa/Purchasing/" + name + "\"}")
                    : Json(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"nameAlreadyExists\"}}");
            }
            if (request.Method == HttpMethod.Get && url.Contains("/root:/"))
            {
                var name = url.Split("/root:/")[1].Split('?')[0];
                return Json(HttpStatusCode.OK, "{\"id\":\"folder-" + name + "\",\"webUrl\":\"https://dafa.sharepoint.com/sites/contracts-dafa/Purchasing/" + name + "\"}");
            }
            if (request.Method == HttpMethod.Put && url.Contains(":/content"))
            {
                Assert.Contains("@microsoft.graph.conflictBehavior=fail", url);
                var name = url.Split(":/")[2].Split(':')[0];
                if (Files.ContainsKey(name))
                {
                    return Json(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"nameAlreadyExists\"}}");
                }
                Files[name] = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                return Json(HttpStatusCode.Created, "{\"id\":\"item-" + Files.Count + "\",\"size\":" + Files[name].Length + ",\"eTag\":\"etag-1\",\"cTag\":\"ctag-1\"}");
            }
            if (request.Method == HttpMethod.Post && url.EndsWith("/createUploadSession", StringComparison.Ordinal))
            {
                Sessions++;
                var name = url.Split(":/")[2].Split(':')[0];
                return Json(HttpStatusCode.OK, "{\"uploadUrl\":\"https://upload.example/session/" + Uri.EscapeDataString(name) + "\"}");
            }
            if (request.Method == HttpMethod.Put && url.StartsWith("https://upload.example/session/", StringComparison.Ordinal))
            {
                Assert.Null(request.Headers.Authorization);                // the upload address is pre-authorised
                var range = request.Content!.Headers.ContentRange!;
                Ranges.Add(range.ToString());
                if (range.To + 1 < range.Length)
                {
                    return Json(HttpStatusCode.Accepted, "{\"nextExpectedRanges\":[\"" + (range.To + 1) + "-\"]}");
                }
                return Json(HttpStatusCode.Created, "{\"id\":\"item-big\",\"size\":" + range.Length + ",\"eTag\":\"etag-big\",\"cTag\":\"ctag-big\"}");
            }
            return Json(HttpStatusCode.NotFound, "{}");
        }
    }

    [Fact]
    public async Task NoMappedSite_IsAConfigurationError_NotADefaultSite()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var unmapped = new DocumentLocation(Guid.NewGuid(), "C-2026-0001", "DAFA Polska sp. z o.o.", "Purchasing");

        var error = await Assert.ThrowsAsync<StorageNotConfiguredException>(
            () => Store(handler).SaveAsync(unmapped, "a.pdf", 1, new MemoryStream(new byte[] { 1 })));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal(StorageProblemTypes.NotConfigured, error.ProblemType);
        Assert.Contains("DAFA Polska", error.Message);
        Assert.Empty(handler.Requests);                 // nothing was uploaded anywhere
    }

    [Fact]
    public async Task NoMappedLibrary_IsAConfigurationError_NotTheFirstLibrary()
    {
        var drive = new FakeDrive();
        var handler = new ScriptedHandler(drive.Respond);

        var error = await Assert.ThrowsAsync<StorageNotConfiguredException>(
            () => Store(handler).SaveAsync(Location("site-nolib", "Procurement"), "a.pdf", 1, new MemoryStream(new byte[] { 1 })));

        Assert.Contains("\"Procurement\"", error.Message);
        Assert.Empty(drive.Files);
        Assert.Empty(drive.Folders);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Upload_CreatesTheFolderOnce_NeverOverwrites_AndReturnsWhereTheFileIs()
    {
        var drive = new FakeDrive();
        var handler = new ScriptedHandler(drive.Respond);
        var store = Store(handler);
        var location = Location("site-upload");

        var first = await store.SaveAsync(location, "Agreement.pdf", 1, new MemoryStream(Encoding.ASCII.GetBytes("one")));

        Assert.Equal("sp:drive-Purchasing:item-1", first.StoreKey);
        Assert.Equal("site-upload", first.SiteId);
        Assert.Equal("drive-Purchasing", first.DriveId);
        Assert.Equal("item-1", first.ItemId);
        Assert.Equal("Purchasing/C-2026-0012/Agreement.pdf", first.RelativePath);
        Assert.Equal("etag-1", first.ETag);
        Assert.Equal("ctag-1", first.CTag);
        Assert.Equal(3, first.Size);
        Assert.Equal("folder-C-2026-0012", first.Folder!.ItemId);
        Assert.StartsWith("https://dafa.sharepoint.com/", first.Folder.WebUrl);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer test-token", r.Authorization));

        // Same name again (a simultaneous upload that got the same version): the existing file stays, the new one gets another name.
        var second = await store.SaveAsync(location, "Agreement.pdf", 1, new MemoryStream(Encoding.ASCII.GetBytes("two")));
        Assert.Equal("one", Encoding.ASCII.GetString(drive.Files["Agreement.pdf"]));
        Assert.Equal(2, drive.Files.Count);
        Assert.NotEqual(first.StoreKey, second.StoreKey);
        Assert.Matches(@"^Purchasing/C-2026-0012/Agreement \([0-9a-f]{6}\)\.pdf$", second.RelativePath);
        // The folder existed: "create" answered 409 and the existing folder was used.
        Assert.Equal(first.Folder, second.Folder);
        Assert.Single(drive.Folders);

        // A new version of the name is a new file with the version in its name.
        var third = await store.SaveAsync(location, "Agreement.pdf", 2, new MemoryStream(Encoding.ASCII.GetBytes("three")));
        Assert.Equal("Purchasing/C-2026-0012/Agreement v2.pdf", third.RelativePath);
        Assert.Equal("Agreement v3 (ab12cd).docx", SharePointDocumentStore.StoredName("Agreement.docx", 3, "ab12cd"));
    }

    [Fact]
    public async Task FilesAboveTenMegabytes_UseAnUploadSession_InParts()
    {
        var drive = new FakeDrive();
        var store = Store(new ScriptedHandler(drive.Respond));
        var big = new byte[SharePointDocumentStore.SimpleUploadLimit + 1];

        var stored = await store.SaveAsync(Location("site-big"), "Scan.pdf", 1, new MemoryStream(big));

        Assert.Equal(1, drive.Sessions);
        Assert.Empty(drive.Files);                                           // no single PUT
        Assert.Equal(3, drive.Ranges.Count);                                 // 5 MB parts
        Assert.Equal("bytes 0-5242879/10485761", drive.Ranges[0]);
        Assert.Equal("bytes 10485760-10485760/10485761", drive.Ranges[2]);
        Assert.Equal("item-big", stored.ItemId);
        Assert.Equal(big.LongLength, stored.Size);
    }

    [Fact]
    public async Task Download_IsStreamed_AMissingFileIsFileMissing_AndAFailingStoreIsUnavailable()
    {
        var handler = new ScriptedHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/items/gone")) return Json(HttpStatusCode.NotFound, "{}");
            if (url.Contains("/items/broken")) return Json(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
            if (url.Contains("/items/denied")) return Json(HttpStatusCode.Forbidden, "{}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("file content")) };
        });
        var store = Store(handler);

        await using (var stream = await store.OpenReadAsync("sp:drive-1:item-1"))
        {
            Assert.IsType<ResponseStream>(stream);                           // the response body itself, not a buffered copy
            using var reader = new StreamReader(stream);
            Assert.Equal("file content", await reader.ReadToEndAsync());
        }
        var missing = await Assert.ThrowsAsync<StorageFileMissingException>(() => store.OpenReadAsync("sp:drive-1:gone"));
        Assert.Equal(404, missing.StatusCode);
        Assert.Equal(StorageProblemTypes.FileMissing, missing.ProblemType);
        await Assert.ThrowsAsync<StorageUnavailableException>(() => store.OpenReadAsync("sp:drive-1:broken"));
        await Assert.ThrowsAsync<StorageNotConfiguredException>(() => store.OpenReadAsync("sp:drive-1:denied"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenReadAsync("../../etc/passwd"));
    }
}

public class StorageBehaviourTests
{
    private static List<ApprovalStep> Steps(TestWorld world, Guid contractId)
    {
        using var db = world.NewDb();
        return db.ApprovalSteps.AsNoTracking().Where(s => s.ContractId == contractId).ToList();
    }

    [Fact]
    public async Task Upload_StoresAHash_AndASecondUploadOfTheSameNameIsANewVersion_NeverAnOverwrite()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        var owner = world.As(world.Owner);
        var firstBytes = Encoding.UTF8.GetBytes("first version");
        var secondBytes = Encoding.UTF8.GetBytes("second version, longer");

        var first = await owner.Documents.UploadAsync(contract.Id, "terms.txt", "Appendix", new MemoryStream(firstBytes));
        var second = await world.As(world.Owner).Documents.UploadAsync(contract.Id, "terms.txt", "Appendix", new MemoryStream(secondBytes));

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        using var db = world.NewDb();
        var rows = db.Documents.Where(d => d.ContractId == contract.Id).OrderBy(d => d.Version).ToList();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(firstBytes)).ToLowerInvariant(), rows[0].ContentHash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(secondBytes)).ToLowerInvariant(), rows[1].ContentHash);
        Assert.NotEqual(rows[0].StoreKey, rows[1].StoreKey);
        Assert.Equal(firstBytes, world.Store.Files[rows[0].StoreKey]);       // the first file is untouched
        Assert.Equal(secondBytes, world.Store.Files[rows[1].StoreKey]);
    }

    [Fact]
    public async Task ContractDetail_AndLists_NeverCallTheStore_SoTheyWorkWhenItIsDown()
    {
        using var world = new TestWorld();
        var contract = world.AddContract(c => c.StoreFolderUrl = "https://dafa.sharepoint.com/sites/contracts/Purchasing/C-2026-0001");
        await world.As(world.Owner).Documents.UploadAsync(contract.Id, "terms.txt", "Appendix", new MemoryStream(Encoding.UTF8.GetBytes("x")));
        var callsBefore = world.Store.Calls;
        world.Store.Failure = new StorageUnavailableException("simulated outage");

        var detail = await world.As(world.Owner).Queries.GetDetailAsync(contract.Id, true);
        var list = await world.As(world.Owner).Queries.ListAsync(new Dafa.Contracts.Core.Dtos.ContractFilter());
        var home = await world.As(world.Owner).Queries.HomeAsync();
        var asAdmin = await world.As(world.Admin).Queries.GetDetailAsync(contract.Id, false);

        Assert.Single(detail.Documents);
        Assert.Equal(1, list.Total);
        Assert.NotNull(home);
        Assert.Equal(callsBefore, world.Store.Calls);                        // not one call to the store
        // The folder link comes from the contract row, and only administrators get it.
        Assert.Null(detail.StoreUrl);
        Assert.Equal(contract.StoreFolderUrl, asAdmin.StoreUrl);

        // The file routes say "busy" (503), not a generic error.
        var download = await Assert.ThrowsAsync<StorageUnavailableException>(() => world.As(world.Owner).Documents.OpenAsync(detail.Documents[0].Id));
        Assert.Equal(503, download.StatusCode);
        await Assert.ThrowsAsync<StorageUnavailableException>(
            () => world.As(world.Owner).Documents.UploadAsync(contract.Id, "more.txt", "Appendix", new MemoryStream(Encoding.UTF8.GetBytes("y"))));
        using var db = world.NewDb();
        Assert.Equal(1, db.Documents.Count(d => d.ContractId == contract.Id));     // no row without a file
    }

    [Fact]
    public async Task FinalApproval_Succeeds_WhenTheStoreIsDown_AndTheLogIsWrittenLater()
    {
        using var world = new TestWorld();
        world.AddRule("one stage", null, null, null, 0, new[] { ApproverKind.GroupCFO });
        var contract = world.AddContract(c => c.Status = ContractStatus.Draft);
        await world.As(world.Owner).Commands.SubmitAsync(contract.Id);
        var step = Steps(world, contract.Id).Single(s => s.ApproverId == world.Cfo);
        world.Store.Failure = new StorageUnavailableException("simulated outage");

        await world.As(world.Cfo).Approvals.DecideAsync(step.Id, ApprovalDecision.Approve, "Fine");

        Assert.Equal(ContractStatus.Approved, ReviewTestHelpers.Reload(world, contract.Id).Status);
        using (var db = world.NewDb())
        {
            var instance = db.ApprovalInstances.Single(i => i.ContractId == contract.Id);
            Assert.Equal(ApprovalStatus.Approved, instance.Status);
            Assert.True(instance.LogPending);
            Assert.Empty(db.Documents.Where(d => d.ContractId == contract.Id));
            Assert.Contains("could not be stored", db.AuditEvents.Single(e => e.ContractId == contract.Id && e.Action == "approval.completed").Text);
        }
        var health = await world.As(world.Admin).Storage.GetHealthAsync();
        Assert.Contains(health.Details, d => d.Contains("approval log"));

        // Still down: nothing happens, nothing is lost.
        Assert.Equal(0, await world.As(null).Approvals.WritePendingLogsAsync());
        // Back again: the job writes the log.
        world.Store.Failure = null;
        Assert.Equal(1, await world.As(null).Approvals.WritePendingLogsAsync());
        Assert.Equal(0, await world.As(null).Approvals.WritePendingLogsAsync());
        using (var db = world.NewDb())
        {
            Assert.False(db.ApprovalInstances.Single(i => i.ContractId == contract.Id).LogPending);
            var log = db.Documents.Single(d => d.ContractId == contract.Id);
            Assert.Equal(DocumentKind.ApprovalLog, log.Kind);
            Assert.NotNull(log.ContentHash);
            Assert.Equal(1, db.AuditEvents.Count(e => e.Action == "approval.log-saved"));
        }
    }

    [Fact]
    public async Task NewContract_IsSavedFirst_TheUploadedFileIsStoredAfterwards_AndAStoreProblemDoesNotLoseTheDraft()
    {
        using var world = new TestWorld();
        Guid uploadId = Guid.NewGuid();
        using (var db = world.NewDb())
        {
            db.PendingUploads.Add(new PendingUpload { Id = uploadId, FileName = "Draft.pdf", Size = 18, Content = Encoding.ASCII.GetBytes("%PDF-1.4 test file"), CreatedById = world.Owner, CreatedAt = world.Clock.UtcNow });
            db.SaveChanges();
        }
        var input = R4.Input(world, "With a file");
        input.UploadId = uploadId;
        world.Store.Failure = new StorageNotConfiguredException("No SharePoint site is linked to Entity A.");

        var id = await world.As(world.Owner).Commands.CreateAsync(input);

        using (var db = world.NewDb())
        {
            Assert.Equal(ContractStatus.Draft, db.Contracts.Single(c => c.Id == id).Status);
            Assert.Empty(db.Documents.Where(d => d.ContractId == id));
            Assert.Equal(1, db.PendingUploads.Count(u => u.Id == uploadId));              // kept, so nothing is lost
            Assert.Contains("could not be stored", db.AuditEvents.Single(e => e.ContractId == id && e.Action == "document.upload-failed").Text);
        }

        // With the store working, the file lands in the folder of the FINAL contract number.
        world.Store.Failure = null;
        var second = R4.Input(world, "With a file, again");
        second.UploadId = uploadId;
        var secondId = await world.As(world.Owner).Commands.CreateAsync(second);
        using (var db = world.NewDb())
        {
            var contract = db.Contracts.Single(c => c.Id == secondId);
            Assert.Equal(DocumentKind.Main, db.Documents.Single(d => d.ContractId == secondId).Kind);
            Assert.Equal(contract.ContractNo, world.Store.SavedLocations.Last().ContractNo);
            Assert.Equal(0, db.PendingUploads.Count(u => u.Id == uploadId));
        }
    }

    [Fact]
    public async Task SearchBackfill_RetriesAFailedDownload_WithBackoff_InsteadOfGivingUp()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        await world.As(world.Owner).Documents.UploadAsync(contract.Id, "old.txt", "Other", new MemoryStream(Encoding.UTF8.GetBytes("Retried platypus clause")));
        world.Store.Failure = new StorageUnavailableException("simulated outage");

        Assert.Equal(0, await world.As(null).Indexer.BackfillAsync());
        using (var db = world.NewDb())
        {
            var row = db.DocumentTexts.Single();
            Assert.Equal("Retry", row.Status);                 // not "Failed"
            Assert.Equal(1, row.Attempts);
            Assert.Equal(world.Clock.UtcNow.AddMinutes(15), row.NextAttemptAt);
        }
        // Not due yet: the document is left alone (no hammering of a store that is down).
        var calls = world.Store.Calls;
        Assert.Equal(0, await world.As(null).Indexer.BackfillAsync());
        Assert.Equal(calls, world.Store.Calls);

        // Due, still down: the pause doubles.
        world.Clock.UtcNow = world.Clock.UtcNow.AddMinutes(16);
        Assert.Equal(0, await world.As(null).Indexer.BackfillAsync());
        using (var db = world.NewDb())
        {
            Assert.Equal(2, db.DocumentTexts.Single().Attempts);
            Assert.Equal(world.Clock.UtcNow.AddMinutes(30), db.DocumentTexts.Single().NextAttemptAt);
        }

        // The store is back: the document is indexed and becomes searchable.
        world.Store.Failure = null;
        world.Clock.UtcNow = world.Clock.UtcNow.AddMinutes(31);
        Assert.Equal(1, await world.As(null).Indexer.BackfillAsync());
        using (var db = world.NewDb())
        {
            Assert.Equal("Indexed", db.DocumentTexts.Single().Status);
            Assert.Null(db.DocumentTexts.Single().NextAttemptAt);
        }
        Assert.Equal(1, (await world.As(world.Admin).Search.FullSearchAsync(new Dafa.Contracts.Core.Dtos.FullSearchQuery { Q = "platypus" })).Documents.Total);
        Assert.Equal(TimeSpan.FromHours(24), DocumentTextIndexer.Backoff(20));

        // A file that is really gone is Failed at once; and a document gives up after MaxAttempts.
        var gone = await world.As(world.Owner).Documents.UploadAsync(contract.Id, "gone.txt", "Other", new MemoryStream(Encoding.UTF8.GetBytes("x")));
        using (var db = world.NewDb())
        {
            world.Store.Files.Remove(db.Documents.Single(d => d.Id == gone.Id).StoreKey);
        }
        Assert.Equal(0, await world.As(null).Indexer.BackfillAsync());
        using (var db = world.NewDb())
        {
            var failed = db.DocumentTexts.Single(t => t.DocumentId == gone.Id);
            Assert.Equal("Failed", failed.Status);
            Assert.Contains("missing", failed.Note);
        }
    }

    [Fact]
    public async Task Reconciliation_ReportsMissingFilesAndChangedContent_AndRepairsNothing()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        var owner = world.As(world.Owner);
        var healthy = await owner.Documents.UploadAsync(contract.Id, "healthy.txt", "Other", new MemoryStream(Encoding.UTF8.GetBytes("healthy")));
        var missing = await world.As(world.Owner).Documents.UploadAsync(contract.Id, "missing.txt", "Other", new MemoryStream(Encoding.UTF8.GetBytes("missing")));
        var changed = await world.As(world.Owner).Documents.UploadAsync(contract.Id, "changed.txt", "Other", new MemoryStream(Encoding.UTF8.GetBytes("changed")));
        var resized = await world.As(world.Owner).Documents.UploadAsync(contract.Id, "resized.txt", "Other", new MemoryStream(Encoding.UTF8.GetBytes("resized")));
        using (var db = world.NewDb())
        {
            var keys = db.Documents.ToDictionary(d => d.Id, d => d.StoreKey);
            world.Store.Files.Remove(keys[missing.Id]);
            world.Store.Files[keys[changed.Id]] = Encoding.UTF8.GetBytes("CHANGED");           // same size, other content
            world.Store.Files[keys[resized.Id]] = Encoding.UTF8.GetBytes("resized and longer");
        }
        var admin = world.As(world.Admin);
        Assert.Null(await admin.Storage.GetLatestAsync());
        var filesBefore = world.Store.Files.Count;

        var report = await admin.Storage.RunAsync();

        Assert.Equal("Completed", report.Status);
        Assert.Null(report.Error);
        Assert.NotNull(report.FinishedAt);
        Assert.Equal(4, report.CheckedDocuments);
        Assert.Equal(3, report.Items.Count);
        var missingItem = report.Items.Single(i => i.Kind == "MissingFile");
        Assert.Equal(missing.Id, missingItem.DocumentId);
        Assert.Equal(contract.Id, missingItem.ContractId);
        Assert.Equal(contract.ContractNo, missingItem.ContractNo);
        Assert.Contains("missing.txt", missingItem.Path);
        Assert.Equal(new[] { changed.Id, resized.Id }.OrderBy(g => g), report.Items.Where(i => i.Kind == "Mismatch").Select(i => i.DocumentId!.Value).OrderBy(g => g));
        Assert.DoesNotContain(report.Items, i => i.DocumentId == healthy.Id);

        // Report only: nothing was repaired, removed or re-uploaded.
        Assert.Equal(filesBefore, world.Store.Files.Count);
        using (var db = world.NewDb())
        {
            Assert.Equal(4, db.Documents.Count());
            Assert.Equal(1, db.AuditEvents.Count(e => e.Action == "storage.reconciled"));
            Assert.True(db.OutboxMessages.Any(m => m.Kind == StorageAlerts.Event && m.RecipientId == world.Admin));
        }
        var latest = await world.As(world.Admin).Storage.GetLatestAsync();
        Assert.Equal(report.Id, latest!.Id);
        Assert.Equal(3, latest.Items.Count);

        // A store that is down during the run gives a Failed report, not an exception.
        world.Store.Failure = new StorageUnavailableException("simulated outage");
        var failed = await world.As(world.Admin).Storage.RunAsync();
        Assert.Equal("Failed", failed.Status);
        Assert.Contains("SharePoint is busy", failed.Error);
        Assert.Equal("Down", (await world.As(world.Admin).Storage.GetHealthAsync()).State);
    }

    [Fact]
    public async Task OpeningADocumentWhoseFileIsGone_IsFileMissing_AndAdministratorsAreToldOnce()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        var document = await world.As(world.Owner).Documents.UploadAsync(contract.Id, "lost.txt", "Other", new MemoryStream(Encoding.UTF8.GetBytes("x")));
        world.Store.Files.Clear();

        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<StorageFileMissingException>(() => world.As(world.Owner).Documents.OpenAsync(document.Id));
            Assert.Equal(404, error.StatusCode);
            Assert.Equal(StorageProblemTypes.FileMissing, error.ProblemType);
        }
        using var db = world.NewDb();
        Assert.Equal(1, db.AuditEvents.Count(e => e.Action == "storage.file-missing" && e.ContractId == contract.Id));
        Assert.Equal(1, db.OutboxMessages.Count(m => m.Kind == StorageAlerts.Event && m.RecipientId == world.Admin));
        Assert.Equal(0, db.AuditEvents.Count(e => e.Action == "document.opened"));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<(int Status, string? RetryAfter, JsonElement Body)> RunMiddlewareAsync(Exception thrown)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        var middleware = new ApiExceptionMiddleware(_ => throw thrown, NullLogger<ApiExceptionMiddleware>.Instance, new TestEnvironment());
        await middleware.InvokeAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var retryAfter = context.Response.Headers["Retry-After"].ToString();
        return (context.Response.StatusCode, retryAfter.Length == 0 ? null : retryAfter, document.RootElement.Clone());
    }

    [Fact]
    public async Task StorageProblems_BecomeTheDocumentedProblemResponses_NeverAGeneric500()
    {
        var busy = await RunMiddlewareAsync(new StorageUnavailableException("HTTP 503 from graph.microsoft.com", 17));
        Assert.Equal(503, busy.Status);
        Assert.Equal("17", busy.RetryAfter);
        Assert.Equal("urn:dafa:problem:storage-unavailable", busy.Body.GetProperty("type").GetString());
        Assert.Equal("SharePoint is busy. Try again in a moment.", busy.Body.GetProperty("detail").GetString());
        Assert.DoesNotContain("graph.microsoft.com", busy.Body.GetRawText());          // the technical reason stays in the log

        var noHint = await RunMiddlewareAsync(new StorageUnavailableException());
        Assert.Equal(503, noHint.Status);
        Assert.Null(noHint.RetryAfter);

        foreach (var missing in new Exception[] { new StorageFileMissingException("sp:d:i"), new FileNotFoundException("gone", "key") })
        {
            var gone = await RunMiddlewareAsync(missing);
            Assert.Equal(404, gone.Status);
            Assert.Equal("urn:dafa:problem:file-missing", gone.Body.GetProperty("type").GetString());
            Assert.Equal("This file can no longer be found in storage. An administrator has been notified.", gone.Body.GetProperty("detail").GetString());
        }

        var unmapped = await RunMiddlewareAsync(new StorageNotConfiguredException("No SharePoint site is linked to DAFA Polska sp. z o.o."));
        Assert.Equal(409, unmapped.Status);
        Assert.Equal("urn:dafa:problem:storage-not-configured", unmapped.Body.GetProperty("type").GetString());
        Assert.Contains("DAFA Polska", unmapped.Body.GetProperty("detail").GetString());

        var duplicate = await RunMiddlewareAsync(new ConflictException("Exists", ProblemTypes.Duplicate, new Dictionary<string, object?> { ["existingId"] = "abc" }));
        Assert.Equal(409, duplicate.Status);
        Assert.Equal("abc", duplicate.Body.GetProperty("existingId").GetString());
    }
}
