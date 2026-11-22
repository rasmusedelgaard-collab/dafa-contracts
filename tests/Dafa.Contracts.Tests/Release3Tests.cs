using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Dafa.Contracts.Core.Services;
using Dafa.Contracts.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Tests;

/// <summary>Release 3 at service level, on the small TestWorld organisation (in-memory Sqlite).</summary>
public class Release3Tests
{
    private static ContractInput NewInput(TestWorld world, string title) => new()
    {
        Title = title,
        TypeId = world.TypeSupplier,
        CounterpartyName = "Release 3 Supplier A/S",
        EntityId = world.EntityA,
        DepartmentId = world.Purchasing,
        OwnerId = world.Owner,
        ReminderRecipient = ReminderRecipient.Owner,
        StartDate = new DateOnly(2026, 1, 1),
        EndDate = new DateOnly(2027, 12, 31),
        NoticePeriodDays = 90,
        AutoRenew = false,
        Currency = "DKK",
        AnnualValue = 100_000m,
        ChangeOfControl = false
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static byte[] Csv(string text) => Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));

    // ---------- analytics: access scoping ----------

    [Fact]
    public async Task Analytics_CoverOnlyVisibleContracts_ForEveryUser()
    {
        using var world = new TestWorld();
        world.AddContract(c => { c.Title = "Purchasing A"; c.AnnualValue = 1_000_000m; });
        world.AddContract(c => { c.Title = "IT B"; c.DepartmentId = world.It; c.EntityId = world.EntityB; c.OwnerId = world.ItHead; c.AnnualValue = 2_000_000m; c.ChangeOfControl = true; });
        world.AddContract(c => { c.Title = "IT A"; c.DepartmentId = world.It; c.OwnerId = world.ItHead; c.AnnualValue = 500_000m; });

        var admin = await world.As(world.Admin).Analytics.GetAsync(new ContractFilter(), null);
        var member = await world.As(world.PurchasingMember).Analytics.GetAsync(new ContractFilter(), null);
        var manager = await world.As(world.EntityAManager).Analytics.GetAsync(new ContractFilter(), null);
        var outsider = await world.As(world.Outsider).Analytics.GetAsync(new ContractFilter(), null);

        Assert.Equal(3, admin.Kpis.Count);
        Assert.Equal(3_500_000m, admin.Kpis.AnnualValueDkk);
        Assert.Equal("All contracts", admin.Scope.Label);

        Assert.Equal(1, member.Kpis.Count);
        Assert.Equal(1_000_000m, member.Kpis.AnnualValueDkk);
        Assert.Single(member.ByDepartment);
        Assert.Empty(member.DueDiligence.ChangeOfControl);
        Assert.Equal(100m, member.ByEntity.Single().SharePct);

        Assert.Equal(2, manager.Kpis.Count);
        Assert.All(manager.ByEntity, b => Assert.Equal(world.EntityA.ToString(), b.Key));

        Assert.Equal(0, outsider.Kpis.Count);
        Assert.Equal(0, outsider.Scope.VisibleContracts);
        Assert.Empty(outsider.ByEntity);
        Assert.Equal(24, outsider.NoticeByMonth.Count);
        Assert.Equal(8, outsider.ExpiringByQuarter.Count);
        Assert.Equal(7, outsider.StatusFunnel.Count);

        Assert.Single(admin.DueDiligence.ChangeOfControl);
        Assert.Equal("IT B", admin.DueDiligence.ChangeOfControl[0].Title);
    }

    [Fact]
    public async Task Analytics_Filters_ExcludeEndedByDefault_AndApplyValueBands()
    {
        using var world = new TestWorld();
        world.AddContract(c => { c.Title = "Small"; c.AnnualValue = 200_000m; });
        world.AddContract(c => { c.Title = "Large"; c.AnnualValue = 6_000_000m; });
        world.AddContract(c => { c.Title = "Ended"; c.Status = ContractStatus.Expired; c.AnnualValue = 9_000_000m; });
        var admin = world.As(world.Admin);

        var all = await admin.Analytics.GetAsync(new ContractFilter(), null);
        Assert.Equal(2, all.Kpis.Count);

        var withEnded = await admin.Analytics.GetAsync(new ContractFilter { IncludeEnded = true }, null);
        Assert.Equal(3, withEnded.Kpis.Count);

        var band = await admin.Analytics.GetAsync(new ContractFilter { ValueBands = { "5to20m" } }, 1_000_000m);
        Assert.Equal(1, band.Kpis.Count);
        Assert.Single(band.DueDiligence.AboveThreshold);

        // The register keeps ended contracts by default.
        var list = await admin.Queries.ListAsync(new ContractFilter { ValueBands = { "5to20m" } });
        Assert.Equal(2, list.Total);
    }

    // ---------- search ----------

    [Fact]
    public async Task Search_FindsDocumentText_OnlyInVisibleContracts()
    {
        using var world = new TestWorld();
        var purchasing = world.AddContract(c => c.Title = "Purchasing frame");
        var it = world.AddContract(c => { c.Title = "IT frame"; c.DepartmentId = world.It; c.EntityId = world.EntityB; c.OwnerId = world.ItHead; });

        await world.As(world.Owner).IndexedDocuments.UploadAsync(purchasing.Id, "terms.txt", "Appendix",
            new MemoryStream(Encoding.UTF8.GetBytes("Liability is capped at the zanzibarite threshold for each claim.")));
        await world.As(world.ItHead).IndexedDocuments.UploadAsync(it.Id, "it-terms.txt", "Appendix",
            new MemoryStream(Encoding.UTF8.GetBytes("Service credits apply above the zanzibarite threshold.")));

        var admin = await world.As(world.Admin).Search.FullSearchAsync(new FullSearchQuery { Q = "Zanzibarite threshold" });
        Assert.Equal(2, admin.Documents.Total);
        var hit = admin.Documents.Items.First();
        Assert.Contains("text", hit.MatchedIn);
        Assert.NotNull(hit.Snippet);
        Assert.Contains(hit.Snippet!.Highlights, h => hit.Snippet.Text.Substring(h[0], h[1] - h[0]).Equals("zanzibarite", StringComparison.OrdinalIgnoreCase));

        var member = await world.As(world.PurchasingMember).Search.FullSearchAsync(new FullSearchQuery { Q = "zanzibarite" });
        Assert.Equal(1, member.Documents.Total);
        Assert.Equal(purchasing.Id, member.Documents.Items.Single().ContractId);

        var outsider = await world.As(world.Outsider).Search.FullSearchAsync(new FullSearchQuery { Q = "zanzibarite" });
        Assert.Equal(0, outsider.Documents.Total);
        Assert.Equal(0, outsider.Contracts.Total);

        // Every term must match: a term that is in neither document finds nothing.
        var none = await world.As(world.Admin).Search.FullSearchAsync(new FullSearchQuery { Q = "zanzibarite nonexistentword" });
        Assert.Equal(0, none.Documents.Total);
    }

    [Fact]
    public async Task Search_ContractsAndPalette_AreScoped()
    {
        using var world = new TestWorld();
        world.AddContract(c => c.Title = "Quokka supply");
        world.AddContract(c => { c.Title = "Quokka hosting"; c.DepartmentId = world.It; c.EntityId = world.EntityB; c.OwnerId = world.ItHead; });

        var member = await world.As(world.PurchasingMember).Search.FullSearchAsync(new FullSearchQuery { Q = "quokka" });
        Assert.Equal(1, member.Contracts.Total);
        Assert.Contains("title", member.Contracts.Items.Single().MatchedIn);

        var palette = await world.As(world.PurchasingMember).Search.PaletteAsync("quokka");
        Assert.Single(palette, r => r.Kind == "Contract");

        var people = await world.As(world.Outsider).Search.FullSearchAsync(new FullSearchQuery { Q = "Purchasing Head" });
        var head = Assert.Single(people.People.Items);
        Assert.Equal(0, head.ContractCount);
    }

    [Fact]
    public async Task BackfillIndexesStoredDocuments()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        var owner = world.As(world.Owner);
        // Uploaded without the indexer (like seeded and older documents).
        await owner.Documents.UploadAsync(contract.Id, "old.txt", "Other", new MemoryStream(Encoding.UTF8.GetBytes("Backfilled wombat clause")));
        Assert.Equal(0, (await world.As(world.Admin).Search.FullSearchAsync(new FullSearchQuery { Q = "wombat" })).Documents.Total);

        var indexed = await world.As(null).Indexer.BackfillAsync();
        Assert.Equal(1, indexed);
        Assert.Equal(0, await world.As(null).Indexer.BackfillAsync());
        Assert.Equal(1, (await world.As(world.Admin).Search.FullSearchAsync(new FullSearchQuery { Q = "wombat" })).Documents.Total);
    }

    // ---------- person and counterparty pages ----------

    [Fact]
    public async Task PersonAndCounterpartyPages_ShowOnlyVisibleContracts()
    {
        using var world = new TestWorld();
        Guid supplierId;
        using (var db = world.NewDb())
        {
            var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Shared Supplier A/S", IsActive = true };
            db.Suppliers.Add(supplier);
            db.SaveChanges();
            supplierId = supplier.Id;
        }
        world.AddContract(c => { c.SupplierId = supplierId; c.CounterpartyName = "Shared Supplier A/S"; c.OwnerId = world.ItHead; });
        world.AddContract(c => { c.SupplierId = supplierId; c.CounterpartyName = "Shared Supplier A/S"; c.OwnerId = world.ItHead; c.DepartmentId = world.It; c.EntityId = world.EntityB; });

        var asMember = await world.As(world.PurchasingMember).Search.PersonAsync(world.ItHead);
        Assert.Single(asMember.Contracts);
        Assert.Equal(1, asMember.Counts.Owner);
        Assert.Contains("Owner", asMember.Contracts[0].Roles);

        var asAdmin = await world.As(world.Admin).Search.PersonAsync(world.ItHead);
        Assert.Equal(2, asAdmin.Counts.Total);

        var asOutsider = await world.As(world.Outsider).Search.PersonAsync(world.ItHead);
        Assert.Empty(asOutsider.Contracts);
        Assert.Equal(0m, asOutsider.AnnualValueDkk);

        var counterparty = await world.As(world.PurchasingMember).Search.CounterpartyAsync(supplierId);
        Assert.Equal(1, counterparty.Summary.Contracts);
        Assert.Equal("Shared Supplier A/S", counterparty.Counterparty.Name);
        var counterpartyOutsider = await world.As(world.Outsider).Search.CounterpartyAsync(supplierId);
        Assert.Empty(counterpartyOutsider.Contracts);

        await Assert.ThrowsAsync<NotFoundException>(() => world.As(world.Admin).Search.PersonAsync(Guid.NewGuid()));
    }

    // ---------- imports ----------

    private static async Task<ImportSessionDto> UploadAsync(Actor actor, ImportKind kind, string csv) =>
        await actor.Imports.UploadAsync(kind, "import.csv", Csv(csv));

    [Fact]
    public async Task CounterpartyImport_DetectsColumns_PreviewWritesNothing_RunCreates()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);
        var session = await UploadAsync(admin, ImportKind.Counterparties,
            "Vendor No;Name;Country;VAT\nS-1;Alpha A/S;DK;DK1\nS-2;Beta AB;SE;SE2\n;;;\nS-1;Alpha again;DK;DK1\n");
        Assert.Equal(new[] { "Vendor No", "Name", "Country", "VAT" }, session.Columns);
        Assert.Equal(3, session.RowCount);                       // the empty row is ignored
        Assert.Equal("Name", session.SuggestedMapping["name"]);
        Assert.Equal("Country", session.SuggestedMapping["country"]);

        var input = new ImportRunInput
        {
            Mapping = new Dictionary<string, string?> { ["externalId"] = "Vendor No", ["name"] = "Name", ["country"] = "Country", ["vatNo"] = "VAT" },
            Options = new ImportOptionsDto { Mode = "CreateOnly" }
        };
        var preview = await admin.Imports.PreviewAsync(session.Id, input);
        Assert.Equal(2, preview.Create);
        Assert.Equal(1, preview.Error);
        Assert.Contains(preview.Rows.Single(r => r.Action == "Error").Errors, e => e.Message.StartsWith("Duplicate of row", StringComparison.Ordinal));
        using (var db = world.NewDb())
        {
            Assert.Equal(0, db.Suppliers.Count());              // a preview writes nothing
        }

        var result = await admin.Imports.RunAsync(session.Id, input);
        Assert.Equal(2, result.Created);
        Assert.Equal(1, result.Failed);
        using (var db = world.NewDb())
        {
            Assert.Equal(2, db.Suppliers.Count());
            Assert.Single(db.ImportRuns);
            Assert.Contains(db.AuditEvents, e => e.Action == "import.run");
        }

        // A session runs once.
        var again = await Assert.ThrowsAsync<ConflictException>(() => admin.Imports.RunAsync(session.Id, input));
        Assert.Equal(ProblemTypesV3.ImportState, again.ProblemType);
    }

    [Fact]
    public async Task CounterpartyImport_UpdateAndSkipModes()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);
        var mapping = new Dictionary<string, string?> { ["externalId"] = "No", ["name"] = "Name", ["city"] = "City" };
        var first = await UploadAsync(admin, ImportKind.Counterparties, "No,Name,City\nS-1,Alpha A/S,Aarhus\n");
        await admin.Imports.RunAsync(first.Id, new ImportRunInput { Mapping = mapping, Options = new ImportOptionsDto { Mode = "CreateOnly" } });

        var skip = await UploadAsync(admin, ImportKind.Counterparties, "No,Name,City\nS-1,Alpha Renamed,Odense\nS-3,Gamma,\n");
        var skipped = await admin.Imports.RunAsync(skip.Id, new ImportRunInput { Mapping = mapping, Options = new ImportOptionsDto { Mode = "SkipDuplicates" } });
        Assert.Equal(1, skipped.Skipped);
        Assert.Equal(1, skipped.Created);

        var update = await UploadAsync(admin, ImportKind.Counterparties, "No,Name,City\nS-1,Alpha Renamed,\n");
        var preview = await admin.Imports.PreviewAsync(update.Id, new ImportRunInput { Mapping = mapping, Options = new ImportOptionsDto { Mode = "CreateAndUpdate" } });
        Assert.Equal("Update", preview.Rows.Single().Action);
        Assert.NotNull(preview.Rows.Single().Match);
        var updated = await admin.Imports.RunAsync(update.Id, new ImportRunInput { Mapping = mapping, Options = new ImportOptionsDto { Mode = "CreateAndUpdate" } });
        Assert.Equal(1, updated.Updated);
        using var db = world.NewDb();
        var alpha = db.Suppliers.Single(s => s.ExternalId == "S-1");
        Assert.Equal("Alpha Renamed", alpha.Name);
        Assert.Equal("Aarhus", alpha.City);                     // an empty cell does not overwrite
    }

    [Fact]
    public async Task ContractImport_ValidatesRequiredFields_AndRegistersDraftOrActive()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);
        var csv = "Title;Counterparty;Type;Entity;Department;Owner;Start;End;Notice\n"
            + "Imported frame;Imported Supplier;Supplier;Entity A;Purchasing;owner.user@example.test;2024-01-01;2027-12-31;90\n"
            + "Old lease;Imported Supplier;Supplier;Entity A;Purchasing;Owner User;01.01.2020;31.12.2021;\n"
            + "No owner;Imported Supplier;Supplier;Entity A;Purchasing;purchasing.member@example.test;2024-01-01;;\n"
            + ";Imported Supplier;Supplier;Entity A;Purchasing;Owner User;2024-01-01;;\n";
        var session = await UploadAsync(admin, ImportKind.Contracts, csv);
        var input = new ImportRunInput
        {
            Mapping = new Dictionary<string, string?>
            {
                ["title"] = "Title", ["counterparty"] = "Counterparty", ["type"] = "Type", ["entity"] = "Entity",
                ["department"] = "Department", ["owner"] = "Owner", ["startDate"] = "Start", ["endDate"] = "End", ["noticePeriodDays"] = "Notice"
            },
            Options = new ImportOptionsDto { Mode = "CreateOnly", ContractStatus = "Active", CreateMissingCounterparties = true }
        };
        var preview = await admin.Imports.PreviewAsync(session.Id, input);
        Assert.Equal("Create", preview.Rows[0].Action);
        Assert.Contains(preview.Rows[0].Warnings, w => w.StartsWith("New counterparty", StringComparison.Ordinal));
        Assert.Equal("Create", preview.Rows[1].Action);
        Assert.Contains(preview.Rows[1].Warnings, w => w.Contains("Expired", StringComparison.Ordinal));
        Assert.Contains(preview.Rows[1].Warnings, w => w.Contains("Notice period", StringComparison.Ordinal));
        Assert.Equal("Error", preview.Rows[2].Action);         // owner without the contract owner role
        Assert.Contains(preview.Rows[2].Errors, e => e.Field == "owner");
        Assert.Equal("Error", preview.Rows[3].Action);         // no title
        Assert.Contains(preview.Rows[3].Errors, e => e.Field == "title");

        var result = await admin.Imports.RunAsync(session.Id, input);
        Assert.Equal(2, result.Created);
        Assert.Equal(2, result.Failed);
        using var db = world.NewDb();
        var frame = db.Contracts.Single(c => c.Title == "Imported frame");
        Assert.Equal(ContractStatus.Active, frame.Status);
        Assert.Equal(new DateOnly(2024, 1, 1), frame.SignedDate);   // the start date when no signed date is given
        Assert.NotNull(frame.SupplierId);
        Assert.StartsWith("C-2026-", frame.ContractNo, StringComparison.Ordinal);
        Assert.Equal(ContractStatus.Expired, db.Contracts.Single(c => c.Title == "Old lease").Status);
        Assert.Null(db.Contracts.Single(c => c.Title == "Old lease").NoticePeriodDays);
        Assert.Equal(1, db.Suppliers.Count(s => s.Name == "Imported Supplier"));
        Assert.Contains(db.AuditEvents, e => e.Action == "import.contract-created" && e.ContractId == frame.Id);
        Assert.Empty(db.ApprovalInstances);                     // imports skip approval
    }

    [Fact]
    public async Task ContractImport_MissingRequiredMapping_MakesEveryRowAnError()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);
        var session = await UploadAsync(admin, ImportKind.Contracts, "Title\nOnly a title\n");
        var preview = await admin.Imports.PreviewAsync(session.Id, new ImportRunInput
        {
            Mapping = new Dictionary<string, string?> { ["title"] = "Title" },
            Options = new ImportOptionsDto { Mode = "CreateOnly" }
        });
        Assert.NotEmpty(preview.MappingErrors);
        Assert.All(preview.Rows, r => Assert.Equal("Error", r.Action));

        var badColumn = await Assert.ThrowsAsync<InputValidationException>(() => admin.Imports.PreviewAsync(session.Id, new ImportRunInput
        {
            Mapping = new Dictionary<string, string?> { ["title"] = "No such column" },
            Options = new ImportOptionsDto { Mode = "CreateOnly" }
        }));
        Assert.True(badColumn.Errors.ContainsKey("mapping.title"));
    }

    // ---------- counterparty merge ----------

    [Fact]
    public async Task MergeDuplicates_RepointsContracts_AndDeactivatesSources()
    {
        using var world = new TestWorld();
        var target = new Supplier { Id = Guid.NewGuid(), Name = "Nordic Polymer A/S", VatNo = "DK100", IsActive = true };
        var source = new Supplier { Id = Guid.NewGuid(), Name = "Nordic Polymer ApS", IsActive = true };
        using (var db = world.NewDb())
        {
            db.Suppliers.AddRange(target, source);
            db.SaveChanges();
        }
        var moved = world.AddContract(c => { c.SupplierId = source.Id; c.CounterpartyName = source.Name; });
        var admin = world.As(world.Admin);

        var duplicates = await admin.Counterparties.DuplicatesAsync();
        Assert.Contains(duplicates, g => g.Reason == "Name" && g.Items.Count == 2);

        var result = await admin.Counterparties.MergeAsync(new MergeInput { TargetId = target.Id, SourceIds = new List<Guid> { source.Id } });
        Assert.Equal(1, result.MovedContracts);
        Assert.Equal(1, result.Deactivated);

        using var check = world.NewDb();
        var contract = check.Contracts.Single(c => c.Id == moved.Id);
        Assert.Equal(target.Id, contract.SupplierId);
        Assert.Equal("Nordic Polymer A/S", contract.CounterpartyName);
        var merged = check.Suppliers.Single(s => s.Id == source.Id);
        Assert.False(merged.IsActive);
        Assert.Equal(target.Id, merged.MergedIntoId);

        await Assert.ThrowsAsync<InputValidationException>(() => admin.Counterparties.MergeAsync(new MergeInput { TargetId = target.Id, SourceIds = new List<Guid> { target.Id } }));
    }

    // ---------- custom fields ----------

    [Fact]
    public async Task CustomFields_AreValidated_StoredAndFilterable()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);
        await admin.CustomFields.CreateAsync(new CustomFieldInput { Key = "riskLevel", Label = "Risk level", Type = "Choice", Options = new List<string> { "Low", "High" }, Required = true, ShowInFilters = true });
        await admin.CustomFields.CreateAsync(new CustomFieldInput { Key = "spendCap", Label = "Spend cap", Type = "Number" });
        await admin.CustomFields.CreateAsync(new CustomFieldInput { Key = "ndaOnly", Label = "NDA only", Type = "Text", ContractTypeIds = new List<Guid> { world.TypeNda } });
        var bad = await Assert.ThrowsAsync<InputValidationException>(() => admin.CustomFields.CreateAsync(new CustomFieldInput { Key = "Bad-Key", Label = "x", Type = "Text" }));
        Assert.True(bad.Errors.ContainsKey("key"));

        var owner = world.As(world.Owner);
        // Required field missing on create.
        var missing = await Assert.ThrowsAsync<InputValidationException>(() => owner.Commands.CreateAsync(NewInput(world, "No risk")));
        Assert.True(missing.Errors.ContainsKey("customFields.riskLevel"));

        // Wrong value type, unknown option, field not valid for the type.
        var input = NewInput(world, "Wrong types");
        input.CustomFields = new Dictionary<string, JsonElement> { ["riskLevel"] = Json("\"Medium\""), ["spendCap"] = Json("\"many\""), ["ndaOnly"] = Json("\"x\"") };
        var wrong = await Assert.ThrowsAsync<InputValidationException>(() => owner.Commands.CreateAsync(input));
        Assert.True(wrong.Errors.ContainsKey("customFields.riskLevel"));
        Assert.True(wrong.Errors.ContainsKey("customFields.spendCap"));
        Assert.True(wrong.Errors.ContainsKey("customFields.ndaOnly"));

        var high = NewInput(world, "High risk");
        high.CustomFields = new Dictionary<string, JsonElement> { ["riskLevel"] = Json("\"High\""), ["spendCap"] = Json("250000") };
        var highId = await owner.Commands.CreateAsync(high);
        var low = NewInput(world, "Low risk");
        low.CustomFields = new Dictionary<string, JsonElement> { ["riskLevel"] = Json("\"Low\"") };
        await owner.Commands.CreateAsync(low);

        var detail = await owner.Queries.GetDetailAsync(highId, false);
        Assert.Equal("High", detail.CustomFields["riskLevel"]);
        Assert.Equal(250000d, detail.CustomFields["spendCap"]);

        var filtered = await owner.Queries.ListAsync(new ContractFilter { Custom = { ["riskLevel"] = "High" } });
        Assert.Equal(highId, Assert.Single(filtered.Items).Id);
        var range = await owner.Queries.ListAsync(new ContractFilter { Custom = { ["spendCap.min"] = "100000" } });
        Assert.Single(range.Items);
        var unknownKey = await owner.Queries.ListAsync(new ContractFilter { Custom = { ["noSuchField"] = "x" } });
        Assert.Empty(unknownKey.Items);

        // Update with customFields: the full set (spendCap is cleared because it is not given).
        var update = ContractValidator.ToInput(ReviewTestHelpers.Reload(world, highId));
        update.CustomFields = new Dictionary<string, JsonElement> { ["riskLevel"] = Json("\"Low\"") };
        await owner.Commands.UpdateAsync(highId, update);
        var after = await owner.Queries.GetDetailAsync(highId, false);
        Assert.Equal("Low", after.CustomFields["riskLevel"]);
        Assert.False(after.CustomFields.ContainsKey("spendCap"));

        // A field with values cannot be deleted.
        var spendCap = (await admin.CustomFields.GetAllAsync()).Single(d => d.Key == "riskLevel");
        var inUse = await Assert.ThrowsAsync<ConflictException>(() => admin.CustomFields.DeleteAsync(spendCap.Id));
        Assert.Equal(ProblemTypesV3.InUse, inUse.ProblemType);
    }

    [Fact]
    public async Task FieldRules_MakeStandardFieldsRequired_OnCreate()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);
        await admin.CustomFields.ReplaceFieldRulesAsync(new FieldRulesDto
        {
            Types = { new TypeFieldRulesDto { TypeId = world.TypeSupplier, Required = { "paymentTerms", "endDate" }, Defaults = new FieldRuleDefaultsDto { Currency = "EUR" } } }
        });
        var rules = await admin.CustomFields.GetFieldRulesAsync();
        Assert.Equal("EUR", rules.Types.Single().Defaults.Currency);

        var input = NewInput(world, "No payment terms");
        input.EndDate = null;
        input.OpenEnded = true;
        var errors = await Assert.ThrowsAsync<InputValidationException>(() => world.As(world.Owner).Commands.CreateAsync(input));
        Assert.True(errors.Errors.ContainsKey("paymentTerms"));
        Assert.True(errors.Errors.ContainsKey("endDate"));

        var ok = NewInput(world, "With payment terms");
        ok.PaymentTerms = "30 days net";
        Assert.NotEqual(Guid.Empty, await world.As(world.Owner).Commands.CreateAsync(ok));
    }

    // ---------- effective access, reassignment ----------

    [Fact]
    public async Task EffectiveAccess_ExplainsEveryRule()
    {
        using var world = new TestWorld();
        world.AddContract(c => c.Title = "Purchasing");
        world.AddContract(c => { c.Title = "Delegated IT"; c.DepartmentId = world.It; c.EntityId = world.EntityB; c.OwnerId = world.ItHead; c.DelegateId = world.PurchasingMember; });
        world.AddContract(c => { c.Title = "Hidden"; c.DepartmentId = world.It; c.EntityId = world.EntityB; c.OwnerId = world.ItHead; });

        var access = await world.As(world.Admin).AdminDirectory.EffectiveAccessAsync(world.PurchasingMember, null, 1, 50);
        Assert.Equal(2, access.Total);
        Assert.Equal(1, access.Rules.Single(r => r.Rule == "Department").Count);
        Assert.Equal(1, access.Rules.Single(r => r.Rule == "OnContract").Count);
        var delegated = access.Contracts.Single(c => c.Item.Title == "Delegated IT");
        Assert.Contains(delegated.Reasons, r => r.Rule == "OnContract" && r.Label == "Delegate");
        var purchasing = access.Contracts.Single(c => c.Item.Title == "Purchasing");
        Assert.Contains(purchasing.Reasons, r => r.Label == "Member of Purchasing");

        // The same answer as the access service itself.
        var listed = await world.As(world.PurchasingMember).Queries.ListAsync(new ContractFilter { PageSize = 200 });
        Assert.Equal(access.Total, listed.Total);

        var outsider = await world.As(world.Admin).AdminDirectory.EffectiveAccessAsync(world.Outsider, null, 1, 50);
        Assert.Equal(0, outsider.Total);
    }

    [Fact]
    public async Task BulkReassignment_PreviewsAndRespectsSeparationOfDuties()
    {
        using var world = new TestWorld();
        var plain = world.AddContract(c => c.Title = "Plain");
        var inApproval = world.AddContract(c => { c.Title = "In approval"; c.Status = ContractStatus.InApproval; });
        var guarded = world.AddContract(c => c.Title = "Guarded");
        var ended = world.AddContract(c => { c.Title = "Ended"; c.Status = ContractStatus.Expired; });
        using (var db = world.NewDb())
        {
            // The target already holds an open approval step on "Guarded".
            var instance = new ApprovalInstance { Id = Guid.NewGuid(), ContractId = guarded.Id, RuleName = "Test", Status = ApprovalStatus.Pending, CurrentStage = 1, StartedAt = world.Clock.UtcNow };
            db.ApprovalInstances.Add(instance);
            db.ApprovalSteps.Add(new ApprovalStep { Id = Guid.NewGuid(), InstanceId = instance.Id, ContractId = guarded.Id, StageOrder = 1, ApproverId = world.PurchasingHead, RoleLabel = "Test", Status = ApprovalStepStatus.Waiting });
            db.Obligations.Add(new Obligation { Id = Guid.NewGuid(), ContractId = plain.Id, Description = "Report", ResponsibleId = world.Owner, Status = ObligationStatus.Open });
            db.SaveChanges();
        }
        var admin = world.As(world.Admin);
        var request = new ReassignInput { FromUserId = world.Owner, ToUserId = world.PurchasingHead, Roles = new List<string> { "Owner" }, IncludeObligations = true };
        var preview = await admin.AdminDirectory.PreviewReassignAsync(request);
        Assert.Equal(1, preview.Contracts);
        Assert.Equal(1, preview.Obligations);
        Assert.Equal("In approval", preview.Rows.Single(r => r.ContractId == inApproval.Id).SkipReason);
        Assert.Contains("approver", preview.Rows.Single(r => r.ContractId == guarded.Id).SkipReason!, StringComparison.Ordinal);
        Assert.Equal("Ended", preview.Rows.Single(r => r.ContractId == ended.Id).SkipReason);

        // A target without the contract owner role cannot become owner.
        var noRole = await admin.AdminDirectory.PreviewReassignAsync(new ReassignInput { FromUserId = world.Owner, ToUserId = world.PurchasingMember, Roles = new List<string> { "Owner" } });
        Assert.Equal(0, noRole.Contracts);

        var result = await admin.AdminDirectory.ReassignAsync(request);
        Assert.Equal(preview.Contracts, result.Updated);
        Assert.Equal(1, result.ObligationsMoved);
        Assert.Equal(world.PurchasingHead, ReviewTestHelpers.Reload(world, plain.Id).OwnerId);
        Assert.Equal(world.Owner, ReviewTestHelpers.Reload(world, guarded.Id).OwnerId);
        Assert.Equal(world.Owner, ReviewTestHelpers.Reload(world, inApproval.Id).OwnerId);
        Assert.Single(ReviewTestHelpers.Audit(world, plain.Id, "contract.reassigned"));
    }

    // ---------- data room ----------

    [Fact]
    public async Task DataRoomExport_HoldsOnlyPermittedDocuments()
    {
        using var world = new TestWorld();
        var visible = world.AddContract(c => c.Title = "Entity A contract");
        var hidden = world.AddContract(c => { c.Title = "Entity B contract"; c.EntityId = world.EntityB; c.DepartmentId = world.It; c.OwnerId = world.ItHead; });
        // Main documents of active contracts can only be added by an administrator.
        await world.As(world.Admin).Documents.UploadAsync(visible.Id, "visible.txt", "Main", new MemoryStream(Encoding.UTF8.GetBytes("visible")));
        await world.As(world.Admin).Documents.UploadAsync(hidden.Id, "secret.txt", "Main", new MemoryStream(Encoding.UTF8.GetBytes("secret")));

        // A plain reader may not export at all.
        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.PurchasingMember).DataRoom.CreateAsync(new DataRoomInput { Name = "Nope" }));

        var manager = world.As(world.EntityAManager);
        var export = await manager.DataRoom.CreateAsync(new DataRoomInput { Name = "Due diligence", Filter = Json("{}"), Documents = "All", Versions = "Latest" });
        Assert.Equal("Ready", export.Status);
        Assert.Equal(1, export.ContractCount);
        Assert.Equal(1, export.DocumentCount);

        var content = await manager.DataRoom.OpenAsync(export.Id);
        using var zip = new ZipArchive(content.Stream, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("index.csv", names);
        Assert.Contains("manifest.json", names);
        Assert.Contains(names, n => n.EndsWith("/visible.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("secret", StringComparison.Ordinal));

        using var db = world.NewDb();
        Assert.Single(db.AuditEvents.Where(e => e.Action == "dataroom.exported"));
        // Another user cannot see someone else's export.
        await Assert.ThrowsAsync<NotFoundException>(() => world.As(world.Cfo).DataRoom.GetAsync(export.Id));
    }

    // ---------- collaboration ----------

    [Fact]
    public async Task Comments_MentionsAndWatchers_NotifyOnlyPeopleWhoCanSee()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        var member = world.As(world.PurchasingMember);
        await member.Collaboration.WatchAsync(contract.Id, true);

        var owner = world.As(world.Owner);
        var refused = await Assert.ThrowsAsync<InputValidationException>(() =>
            owner.Collaboration.AddCommentAsync(contract.Id, new CommentInput { Body = "@Outsider look", MentionIds = new List<Guid> { world.Outsider } }));
        Assert.True(refused.Errors.ContainsKey("mentionIds"));

        var comment = await owner.Collaboration.AddCommentAsync(contract.Id, new CommentInput { Body = "@Purchasing Head please check", MentionIds = new List<Guid> { world.PurchasingHead } });
        Assert.Single(comment.Mentions);

        var head = await world.As(world.PurchasingHead).Collaboration.GetNotificationsAsync(false, 1, 25);
        Assert.Contains(head.Items, n => n.Event == "Mention" && n.Link == "/contracts/" + contract.Id + "#comments");
        var watcher = await member.Collaboration.GetNotificationsAsync(false, 1, 25);
        Assert.Contains(watcher.Items, n => n.Event == "WatchedContractChanged");
        Assert.True((await member.Collaboration.UnreadCountAsync()).Count >= 1);
        await member.Collaboration.MarkAllReadAsync();
        Assert.Equal(0, (await member.Collaboration.UnreadCountAsync()).Count);

        // An outsider can neither read nor comment.
        await Assert.ThrowsAsync<NotFoundException>(() => world.As(world.Outsider).Collaboration.GetCommentsAsync(contract.Id));
    }

    [Fact]
    public async Task Versions_RebuildKeyTermsFromTheAuditTrail()
    {
        using var world = new TestWorld();
        var owner = world.As(world.Owner);
        var id = await owner.Commands.CreateAsync(NewInput(world, "Version one"));
        var update = ContractValidator.ToInput(ReviewTestHelpers.Reload(world, id));
        update.Title = "Version two";
        update.AnnualValue = 250_000m;
        await world.As(world.Owner).Commands.UpdateAsync(id, update);
        update.Title = "Version three";
        await world.As(world.Owner).Commands.UpdateAsync(id, update);

        var versions = await owner.Versions.GetVersionsAsync(id);
        Assert.Equal("current", versions.First().Id);
        Assert.Equal("created", versions.Last().Id);
        Assert.Equal(3, versions.Count);

        var comparison = await owner.Versions.CompareAsync(id, "created", "current");
        var title = comparison.Fields.Single(f => f.Field == "title");
        Assert.Equal("Version one", title.From);
        Assert.Equal("Version three", title.To);
        Assert.True(title.Changed);
        var value = comparison.Fields.Single(f => f.Field == "annualValue");
        Assert.Equal("100000", value.From);
        Assert.Equal("250000", value.To);
        Assert.False(comparison.Fields.Single(f => f.Field == "currency").Changed);

        await Assert.ThrowsAsync<NotFoundException>(() => owner.Versions.CompareAsync(id, "nope", "current"));
        await Assert.ThrowsAsync<NotFoundException>(() => world.As(world.Outsider).Versions.GetVersionsAsync(id));
    }

    [Fact]
    public async Task Tags_AndSavedViews()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        var admin = world.As(world.Admin);
        var tag = await admin.Collaboration.CreateTagAsync(new TagInput { Name = "Strategic", Color = "blue" });
        await Assert.ThrowsAsync<ConflictException>(() => admin.Collaboration.CreateTagAsync(new TagInput { Name = "strategic", Color = "red" }));

        var detail = await world.As(world.Owner).Collaboration.SetContractTagsAsync(contract.Id, new ContractTagsInput { TagIds = new List<Guid> { tag.Id } });
        Assert.Equal("Strategic", Assert.Single(detail.Tags).Name);
        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.PurchasingMember).Collaboration.SetContractTagsAsync(contract.Id, new ContractTagsInput { TagIds = new List<Guid>() }));
        var tagged = await world.As(world.PurchasingMember).Queries.ListAsync(new ContractFilter { TagIds = { tag.Id } });
        Assert.Single(tagged.Items);

        var reader = world.As(world.PurchasingMember);
        await reader.Collaboration.CreateViewAsync(new SavedViewInput { Name = "Mine", Scope = "Personal", Columns = new List<string> { "contract", "notice" }, Query = Json("{\"view\":\"mine\"}"), IsDefault = true });
        await Assert.ThrowsAsync<ConflictException>(() => reader.Collaboration.CreateViewAsync(new SavedViewInput { Name = "mine", Scope = "Personal" }));
        await Assert.ThrowsAsync<ForbiddenException>(() => reader.Collaboration.CreateViewAsync(new SavedViewInput { Name = "Shared", Scope = "Shared" }));
        await Assert.ThrowsAsync<InputValidationException>(() => reader.Collaboration.CreateViewAsync(new SavedViewInput { Name = "Bad", Columns = new List<string> { "nope" } }));
        var shared = await world.As(world.PurchasingHead).Collaboration.CreateViewAsync(new SavedViewInput { Name = "Team", Scope = "Shared" });
        var views = await reader.Collaboration.GetViewsAsync();
        Assert.Equal(2, views.Count);
        Assert.False(views.Single(v => v.Id == shared.Id).CanEdit);
        Assert.Equal("mine", views.Single(v => v.Name == "Mine").Query.GetProperty("view").GetString());
    }

    // ---------- schema upgrader ----------

    [Fact]
    public async Task SchemaUpgrader_UpgradesAnOldDatabase_AndIsIdempotent()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            // A release 2 style table with data, missing the release 3 columns.
            command.CommandText =
                "CREATE TABLE \"LegalEntities\" (\"Id\" TEXT NOT NULL CONSTRAINT \"PK_LegalEntities\" PRIMARY KEY, \"Name\" TEXT NOT NULL, \"SortOrder\" INTEGER NOT NULL);"
                + "INSERT INTO \"LegalEntities\" (\"Id\", \"Name\", \"SortOrder\") VALUES ('3F2504E0-4F89-11D3-9A0C-0305E82C3301', 'Old Entity A/S', 1);";
            command.ExecuteNonQuery();
        }
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        using (var db = new AppDbContext(options))
        {
            var first = await new SchemaUpgrader(db).UpgradeAsync();
            Assert.Equal(new List<int> { 1, 2 }, first.AppliedVersions);
            Assert.NotEmpty(first.Statements);
            Assert.Equal(2, first.CurrentVersion);
        }
        using (var db = new AppDbContext(options))
        {
            var second = await new SchemaUpgrader(db).UpgradeAsync();
            Assert.Empty(second.AppliedVersions);
            Assert.Empty(second.Statements);
            Assert.Equal(new List<int> { 1, 2 }, await new SchemaUpgrader(db).GetRecordedVersionsAsync());
        }
        using (var db = new AppDbContext(options))
        {
            // The old row survived and got the defaults; every model table works through EF.
            var entity = db.LegalEntities.Single();
            Assert.Equal("Old Entity A/S", entity.Name);
            Assert.True(entity.IsActive);
            Assert.Null(entity.Country);
            db.Tags.Add(new Tag { Id = Guid.NewGuid(), Name = "After upgrade", Color = "green" });
            db.AppSettings.Add(new AppSetting { Key = "probe", Value = "1", UpdatedAt = DateTime.UtcNow });
            db.Notifications.Add(new InAppNotification { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Event = "Mention", Title = "t", Body = "b", CreatedAt = DateTime.UtcNow });
            db.CustomFieldValues.Add(new CustomFieldValue { Id = Guid.NewGuid(), ContractId = Guid.NewGuid(), FieldId = Guid.NewGuid(), NumberValue = 1.5 });
            db.SaveChanges();
            Assert.Equal(1, db.Tags.Count());
            Assert.Equal(0, db.Contracts.Count());
            Assert.Equal(0, db.DocumentTexts.Count());
            Assert.Equal(0, db.DataRoomExports.Count());
        }
    }

    [Fact]
    public async Task SchemaUpgrader_OnAFreshEnsureCreatedDatabase_OnlyRecordsVersions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        var result = await new SchemaUpgrader(db).UpgradeAsync();
        Assert.Equal(new List<int> { 1, 2 }, result.AppliedVersions);
        // Only the SchemaVersion table itself was created.
        Assert.Single(result.Statements);
        Assert.Empty(result.Warnings);
        var again = await new SchemaUpgrader(db).UpgradeAsync();
        Assert.Empty(again.Statements);
    }

    // ---------- settings ----------

    [Fact]
    public async Task NotificationTemplates_AreValidated_AndRendered()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);
        var settings = await admin.Settings.GetNotificationSettingsAsync();
        Assert.Equal(NotificationCatalog.All.Count, settings.Events.Count);

        var mention = settings.Events.Single(e => e.Event == "Mention");
        mention.Subject = "{{unknown.thing}}";
        var bad = await Assert.ThrowsAsync<InputValidationException>(() => admin.Settings.UpdateNotificationSettingsAsync(settings));
        Assert.True(bad.Errors.ContainsKey("events.Mention.subject"));

        mention.Subject = "Hi {{recipient.name}}: {{contract.title}}";
        mention.Channels = new List<string> { "InApp" };
        await admin.Settings.UpdateNotificationSettingsAsync(settings);

        var preview = admin.Settings.Preview(new TemplatePreviewInput { Event = "Mention", Subject = "{{contract.no}} {{nope}}", Body = "x" });
        Assert.StartsWith("C-", preview.Subject, StringComparison.Ordinal);
        Assert.Equal(new List<string> { "nope" }, preview.Unknown);

        var contract = world.AddContract(c => c.Title = "Templated");
        await world.As(world.Owner).Collaboration.AddCommentAsync(contract.Id, new CommentInput { Body = "hello", MentionIds = new List<Guid> { world.PurchasingMember } });
        var notifications = await world.As(world.PurchasingMember).Collaboration.GetNotificationsAsync(false, 1, 10);
        Assert.Equal("Hi Purchasing Member: Templated", notifications.Items.Single(n => n.Event == "Mention").Title);
        using var db = world.NewDb();
        Assert.DoesNotContain(db.OutboxMessages, m => m.Kind == "Mention");   // e-mail turned off for the event
    }

    [Fact]
    public void ScheduleCalculator_DailyAndWeekly_InCopenhagenTime()
    {
        var zone = TimeZoneInfo.Utc;
        var from = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);   // a Monday
        Assert.Equal(new DateTime(2026, 11, 24, 6, 0, 0, DateTimeKind.Utc), ScheduleCalculator.NextRun(ScheduleFrequency.Daily, "06:00", null, from, zone));
        Assert.Equal(new DateTime(2026, 11, 23, 18, 30, 0, DateTimeKind.Utc), ScheduleCalculator.NextRun(ScheduleFrequency.Daily, "18:30", null, from, zone));
        // dayOfWeek 1 = Monday: next Monday at 06:00 (today's has passed).
        Assert.Equal(new DateTime(2026, 11, 30, 6, 0, 0, DateTimeKind.Utc), ScheduleCalculator.NextRun(ScheduleFrequency.Weekly, "06:00", 1, from, zone));
        Assert.Equal(new DateTime(2026, 11, 29, 6, 0, 0, DateTimeKind.Utc), ScheduleCalculator.NextRun(ScheduleFrequency.Weekly, "06:00", 7, from, zone));
        Assert.Null(ScheduleCalculator.NextRun(ScheduleFrequency.Off, "06:00", null, from, zone));
    }
}
