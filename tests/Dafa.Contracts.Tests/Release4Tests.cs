using System.Text;
using System.Text.Json;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Dtos;
using Dafa.Contracts.Core.Services;
using Dafa.Contracts.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Tests;

/// <summary>Release 4 at service level, on the small TestWorld organisation (in-memory Sqlite).</summary>
internal static class R4
{
    public static ContractFilter Query(params (string Key, string Value)[] pairs) =>
        ContractFilterParser.FromQuery(pairs.GroupBy(p => p.Key).Select(g => new KeyValuePair<string, string[]>(g.Key, g.Select(p => p.Value).ToArray())));

    public static byte[] Png() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3, 4 };

    public static byte[] Svg(string inner = "<rect width=\"10\" height=\"10\" fill=\"#00456c\"/>") =>
        Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\">" + inner + "</svg>");

    public static ContractInput Input(TestWorld world, string title) => new()
    {
        Title = title,
        TypeId = world.TypeSupplier,
        SupplierId = world.DefaultSupplier,
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

    /// <summary>Simulated e-signature on, the Owner may send for every entity, and an Approved contract with one document.</summary>
    public static async Task<(Contract Contract, Guid DocumentId)> SigningSetupAsync(TestWorld world, Action<ESignSettingsDto>? settings = null)
    {
        var eSign = new ESignSettingsDto { Provider = SignatureProviderNames.Simulated };
        settings?.Invoke(eSign);
        await world.As(world.Admin).Signatures.UpdateSettingsAsync(eSign);
        world.UpdateUser(world.Owner, u => u.CanSendForSignature = true);
        var contract = world.AddContract(c => { c.Status = ContractStatus.Approved; c.SignedDate = null; c.SigningMethod = null; });
        var document = await world.As(world.Admin).Documents.UploadAsync(contract.Id, "Agreement.pdf", "Main", new MemoryStream(ReviewTestHelpers.PdfBytes("agreement")));
        return (contract, document.Id);
    }

    public static SignatureRequestInput TwoSigners(TestWorld world, Guid documentId, string order = "Parallel") => new()
    {
        DocumentIds = new List<Guid> { documentId },
        Order = order,
        Message = "Please sign.",
        Signers = new List<SignerInput>
        {
            new() { Kind = "Internal", UserId = world.Cfo, Order = 1 },
            new() { Kind = "External", Name = "Anna Lindqvist", Email = "anna@supplier.example", Order = 2 }
        }
    };
}

public class Release4FilterTests
{
    [Fact]
    public void EmptyFilterValues_MeanNoFilter_AndNeverThrow()
    {
        var filter = R4.Query(
            ("q", ""), ("entityId", ""), ("departmentId", ""), ("typeId", ""), ("status", ""), ("counterpartyId", ""), ("ownerId", ""),
            ("delegateId", ""), ("responsibleId", ""), ("personId", ""), ("valueBand", ""), ("minValueDkk", ""), ("dateField", ""),
            ("from", ""), ("to", ""), ("noticeWithinDays", ""), ("decided", ""), ("tagId", ""), ("changeOfControl", ""), ("autoRenew", ""),
            ("missingData", ""), ("missingField", ""), ("watching", ""), ("includeEnded", ""), ("sort", ""), ("page", ""), ("pageSize", ""),
            ("title", ""), ("contractNo", ""), ("counterpartyName", ""), ("currency", ""), ("annualValueMin", ""), ("annualValueMax", ""),
            ("totalValueMin", ""), ("totalValueMax", ""), ("noticeFrom", ""), ("noticeTo", ""), ("endFrom", ""), ("endTo", ""),
            ("startFrom", ""), ("signedTo", ""), ("createdFrom", ""), ("cf.risk", ""), ("cf.amount.min", ""), ("columns", ""));

        Assert.Null(filter.Q);
        Assert.Empty(filter.EntityIds);
        Assert.Empty(filter.Statuses);
        Assert.Empty(filter.Currencies);
        Assert.Empty(filter.DateRanges);
        Assert.Empty(filter.Custom);        // cf.risk= used to filter on "" and match nothing
        Assert.Empty(filter.CustomAny);
        Assert.Null(filter.AnnualValueMin);
        Assert.Null(filter.IncludeEnded);
    }

    [Fact]
    public async Task EmptyValues_ReturnTheSameContractsAsNoFilter()
    {
        using var world = new TestWorld();
        world.AddContract();
        world.AddContract(c => c.EntityId = world.EntityB);
        var admin = world.As(world.Admin);

        var all = await admin.Queries.ListAsync(new ContractFilter());
        var empty = await admin.Queries.ListAsync(R4.Query(("entityId", ""), ("status", ""), ("cf.anything", ""), ("from", ""), ("currency", "")));

        Assert.Equal(2, all.Total);
        Assert.Equal(all.Total, empty.Total);
    }

    [Fact]
    public void MalformedValues_AreStill400_OnTheParameter()
    {
        Assert.True(Assert.Throws<InputValidationException>(() => R4.Query(("entityId", "not-a-guid"))).Errors.ContainsKey("entityId"));
        Assert.True(Assert.Throws<InputValidationException>(() => R4.Query(("annualValueMin", "abc"))).Errors.ContainsKey("annualValueMin"));
        Assert.True(Assert.Throws<InputValidationException>(() => R4.Query(("endFrom", "31-12-2026"))).Errors.ContainsKey("endFrom"));
    }

    [Fact]
    public async Task HeaderFilters_OnText_Currency_Values_AndDates()
    {
        using var world = new TestWorld();
        var cheap = world.AddContract(c => { c.Title = "Alpha rubber"; c.AnnualValue = 100_000m; c.EndDate = new DateOnly(2026, 6, 30); });
        var dear = world.AddContract(c => { c.Title = "Beta foam"; c.AnnualValue = 100_000m; c.Currency = "EUR"; c.EndDate = new DateOnly(2027, 6, 30); c.DelegateId = world.PurchasingHead; });
        var noValue = world.AddContract(c => { c.Title = "Gamma tape"; c.AnnualValue = null; c.TotalValue = 5_000m; c.EndDate = null; });
        var admin = world.As(world.Admin);

        async Task<List<Guid>> IdsAsync(params (string, string)[] pairs) =>
            (await admin.Queries.ListAsync(R4.Query(pairs))).Items.Select(i => i.Id).ToList();

        Assert.Equal(new[] { cheap.Id }, await IdsAsync(("title", "ALPHA")));
        Assert.Equal(new[] { dear.Id }, await IdsAsync(("currency", "EUR")));
        Assert.Equal(new[] { dear.Id }, await IdsAsync(("delegateId", world.PurchasingHead.ToString())));
        // DKK values: 100,000 EUR is far above 200,000 DKK; a contract without an annual value never matches a range on it.
        Assert.Equal(new[] { dear.Id }, await IdsAsync(("annualValueMin", "200000")));
        Assert.Equal(new[] { cheap.Id }, await IdsAsync(("annualValueMax", "200000")));
        Assert.Equal(new[] { noValue.Id }, await IdsAsync(("totalValueMax", "10000")));
        // Date ranges per column, inclusive; no end date never matches.
        Assert.Equal(new[] { cheap.Id }, await IdsAsync(("endFrom", "2026-06-30"), ("endTo", "2026-06-30")));
        Assert.Equal(new[] { dear.Id }, await IdsAsync(("endFrom", "2027-01-01")));
        // Min above max is not an error: it matches nothing.
        Assert.Empty(await IdsAsync(("annualValueMin", "10"), ("annualValueMax", "5")));
    }

    [Fact]
    public async Task EntityScope_NarrowsHome_Renewals_Overview_AndApprovals_AfterTheAccessFilter()
    {
        using var world = new TestWorld();
        var inA = world.AddContract();
        var inB = world.AddContract(c => c.EntityId = world.EntityB);
        var admin = world.As(world.Admin);
        var scopeA = new List<Guid> { world.EntityA };

        Assert.Equal(2, (await admin.Queries.RenewalsAsync(12)).Count);
        Assert.Equal(inA.Id, Assert.Single(await admin.Queries.RenewalsAsync(12, scopeA)).Id);
        Assert.Equal(2, (await admin.Queries.OverviewAsync()).ActiveContracts);
        Assert.Equal(1, (await admin.Queries.OverviewAsync(scopeA)).ActiveContracts);
        Assert.Equal(2, (await admin.Queries.HomeAsync(null)).NoticeByMonth.Sum(m => m.Count));
        Assert.Equal(1, (await admin.Queries.HomeAsync(scopeA)).NoticeByMonth.Sum(m => m.Count));
        // An entity nobody has (or the user cannot see) matches nothing; it never widens access.
        Assert.Empty(await admin.Queries.RenewalsAsync(12, new List<Guid> { Guid.NewGuid() }));
        Assert.Empty(await world.As(world.Outsider).Queries.RenewalsAsync(12, new List<Guid> { world.EntityB }));
        Assert.NotEqual(inA.Id, inB.Id);
    }

    [Fact]
    public async Task DistinctValues_AreScopedByAccess_Counted_Searchable_AndCapped()
    {
        using var world = new TestWorld();
        var alpha = world.AddSupplier("Alpha Rubber A/S");
        var beta = world.AddSupplier("Beta Foam GmbH");
        world.AddContract(c => { c.SupplierId = alpha; c.CounterpartyName = "Alpha Rubber A/S"; });
        world.AddContract(c => { c.SupplierId = alpha; c.CounterpartyName = "Alpha Rubber A/S"; });
        world.AddContract(c => { c.SupplierId = beta; c.CounterpartyName = "Beta Foam GmbH"; });
        // Not visible to the purchasing member: IT department, another owner.
        world.AddContract(c => { c.SupplierId = beta; c.CounterpartyName = "Beta Foam GmbH"; c.DepartmentId = world.It; c.OwnerId = world.ItHead; c.EntityId = world.EntityB; });

        var member = world.As(world.PurchasingMember);
        var counterparties = await member.Distinct.GetAsync("counterparty", new ContractFilter(), null, null);
        Assert.Equal("counterparty", counterparties.Column);
        Assert.Equal(new[] { alpha.ToString(), beta.ToString() }, counterparties.Values.Select(v => v.Value).ToArray());
        Assert.Equal(new[] { 2, 1 }, counterparties.Values.Select(v => v.Count).ToArray());   // the IT contract is not counted
        Assert.False(counterparties.Truncated);

        var admin = await world.As(world.Admin).Distinct.GetAsync("counterparty", new ContractFilter(), null, null);
        Assert.Equal(2, admin.Values.Single(v => v.Value == beta.ToString()).Count);

        Assert.Empty((await world.As(world.Outsider).Distinct.GetAsync("counterparty", new ContractFilter(), null, null)).Values);

        // Searchable and capped.
        var searched = await member.Distinct.GetAsync("counterparty", new ContractFilter(), "foam", null);
        Assert.Equal("Beta Foam GmbH", Assert.Single(searched.Values).Label);
        var capped = await member.Distinct.GetAsync("counterparty", new ContractFilter(), null, 1);
        Assert.Single(capped.Values);
        Assert.True(capped.Truncated);

        // The column's own filter is left out (the list does not collapse); other filters apply.
        var own = await member.Distinct.GetAsync("counterparty", R4.Query(("counterpartyId", alpha.ToString())), null, null);
        Assert.Equal(2, own.Values.Count);
        var entities = await world.As(world.Admin).Distinct.GetAsync("entity", R4.Query(("counterpartyId", alpha.ToString())), null, null);
        Assert.Equal(world.EntityA.ToString(), Assert.Single(entities.Values).Value);

        var statuses = await member.Distinct.GetAsync("status", new ContractFilter(), null, null);
        Assert.Equal("Active", Assert.Single(statuses.Values).Value);

        var unknown = await Assert.ThrowsAsync<InputValidationException>(() => member.Distinct.GetAsync("salary", new ContractFilter(), null, null));
        Assert.True(unknown.Errors.ContainsKey("column"));
    }
}

public class Release4PickerTests
{
    [Fact]
    public async Task PeopleSearch_MatchesNameEmailAndInitials_ActiveOnly_WithDepartmentAndEntity()
    {
        using var world = new TestWorld();
        world.UpdateUser(world.PurchasingHead, u => u.HomeEntityId = world.EntityA);
        world.UpdateUser(world.Deputy, u => u.IsActive = false);
        var reader = world.As(world.Outsider);

        var head = Assert.Single(await reader.Pickers.SearchPeopleAsync("purchasing head", null, null, false));
        Assert.Equal("PH", head.Initials);
        Assert.Equal("Purchasing", head.DepartmentName);
        Assert.Equal("Entity A", head.EntityName);
        Assert.True(head.IsActive);

        Assert.Single(await reader.Pickers.SearchPeopleAsync("it.head@example", null, null, false));
        Assert.Empty(await reader.Pickers.SearchPeopleAsync("Deputy", null, null, false));
        Assert.False(Assert.Single(await reader.Pickers.SearchPeopleAsync("Deputy", null, null, true)).IsActive);

        // role: users with the role, and administrators count for every role.
        var owners = await reader.Pickers.SearchPeopleAsync(null, 50, "ContractOwner", false);
        Assert.Contains(owners, p => p.Id == world.Owner);
        Assert.Contains(owners, p => p.Id == world.Admin);
        Assert.DoesNotContain(owners, p => p.Id == world.Outsider);

        Assert.Equal(2, (await reader.Pickers.SearchPeopleAsync(null, 2, null, false)).Count);
        await Assert.ThrowsAsync<InputValidationException>(() => reader.Pickers.SearchPeopleAsync(null, null, "Emperor", false));
    }

    [Fact]
    public async Task CreateCounterparty_FollowsTheSetting_AndRefusesDuplicates()
    {
        using var world = new TestWorld();
        var input = new CounterpartyInput { Name = "Nordic Polymer A/S", Country = "dk", VatNo = "DK12345678" };

        // Default AdminOnly: an owner and a reader are refused with not-permitted (403).
        var refused = await Assert.ThrowsAsync<NotPermittedException>(() => world.As(world.Owner).Pickers.CreateCounterpartyAsync(input));
        Assert.Equal(403, refused.StatusCode);
        Assert.Equal(ProblemTypesV4.NotPermitted, refused.ProblemType);
        Assert.False(await world.As(world.Owner).Pickers.MayCreateCounterpartyAsync());

        var settings = await world.As(world.Admin).Settings.GetAppSettingsAsync();
        Assert.Equal("AdminOnly", settings.CounterpartyCreation);
        settings.CounterpartyCreation = "ContractOwners";
        await world.As(world.Admin).Settings.UpdateAppSettingsAsync(settings);

        await Assert.ThrowsAsync<NotPermittedException>(() => world.As(world.Outsider).Pickers.CreateCounterpartyAsync(input));
        var created = await world.As(world.Owner).Pickers.CreateCounterpartyAsync(input);
        Assert.Equal("Manual", created.Source);
        Assert.Equal("DK", created.Country);
        Assert.True(created.IsActive);

        // Same name (case-insensitive, trimmed) or same VAT number: 409 duplicate with the existing id.
        var sameName = await Assert.ThrowsAsync<ConflictException>(
            () => world.As(world.Owner).Pickers.CreateCounterpartyAsync(new CounterpartyInput { Name = "  nordic polymer a/s " }));
        Assert.Equal(ProblemTypes.Duplicate, sameName.ProblemType);
        Assert.Equal(created.Id, sameName.Extensions!["existingId"]);
        await Assert.ThrowsAsync<ConflictException>(
            () => world.As(world.Owner).Pickers.CreateCounterpartyAsync(new CounterpartyInput { Name = "Another name", VatNo = "DK12345678" }));

        settings.CounterpartyCreation = "AllUsers";
        await world.As(world.Admin).Settings.UpdateAppSettingsAsync(settings);
        Assert.Equal("Reader Made ApS", (await world.As(world.Outsider).Pickers.CreateCounterpartyAsync(new CounterpartyInput { Name = "Reader Made ApS" })).Name);

        settings.CounterpartyCreation = "Whoever";
        var bad = await Assert.ThrowsAsync<InputValidationException>(() => world.As(world.Admin).Settings.UpdateAppSettingsAsync(settings));
        Assert.True(bad.Errors.ContainsKey("counterpartyCreation"));

        using var db = world.NewDb();
        Assert.True(db.AuditEvents.Count(e => e.Action == "counterparty.created") >= 2);
    }

    [Fact]
    public async Task Contract_RequiresAnActiveCounterpartyFromTheList_AndActivePeople()
    {
        using var world = new TestWorld();
        var owner = world.As(world.Owner);

        // Free text is no longer a counterparty.
        var freeText = R4.Input(world, "Free text");
        freeText.SupplierId = null;
        freeText.CounterpartyName = "Typed By Hand A/S";
        var noSupplier = await Assert.ThrowsAsync<InputValidationException>(() => owner.Commands.CreateAsync(freeText));
        Assert.True(noSupplier.Errors.ContainsKey("supplierId"));

        var unknown = R4.Input(world, "Unknown");
        unknown.SupplierId = Guid.NewGuid();
        Assert.True((await Assert.ThrowsAsync<InputValidationException>(() => owner.Commands.CreateAsync(unknown))).Errors.ContainsKey("supplierId"));

        var inactive = R4.Input(world, "Inactive");
        inactive.SupplierId = world.AddSupplier("Closed Down A/S", isActive: false);
        Assert.True((await Assert.ThrowsAsync<InputValidationException>(() => owner.Commands.CreateAsync(inactive))).Errors.ContainsKey("supplierId"));

        // Owner, delegate and responsible must be existing, active users.
        world.UpdateUser(world.Deputy, u => u.IsActive = false);
        var people = R4.Input(world, "People");
        people.DelegateId = world.Deputy;
        people.ResponsibleId = Guid.NewGuid();
        var peopleErrors = await Assert.ThrowsAsync<InputValidationException>(() => owner.Commands.CreateAsync(people));
        Assert.True(peopleErrors.Errors.ContainsKey("delegateId"));
        Assert.True(peopleErrors.Errors.ContainsKey("responsibleId"));

        // The name always comes from the master record, whatever the client sends.
        var ok = R4.Input(world, "From the list");
        ok.CounterpartyName = "Something else";
        var id = await owner.Commands.CreateAsync(ok);
        Assert.Equal("Test Supplier A/S", ReviewTestHelpers.Reload(world, id).CounterpartyName);

        // A counterparty cannot be removed again.
        var remove = ContractValidator.ToInput(ReviewTestHelpers.Reload(world, id));
        remove.SupplierId = null;
        Assert.True((await Assert.ThrowsAsync<InputValidationException>(() => owner.Commands.UpdateAsync(id, remove))).Errors.ContainsKey("supplierId"));
    }

    [Fact]
    public async Task OlderContract_WithoutMasterRecord_KeepsItsName_UntilACounterpartyIsChosen()
    {
        using var world = new TestWorld();
        var legacy = world.AddContract(c => { c.Status = ContractStatus.Draft; c.CounterpartyName = "Imported By Name A/S"; });
        var input = ContractValidator.ToInput(ReviewTestHelpers.Reload(world, legacy.Id));
        input.Title = "Renamed";
        input.CounterpartyName = "Typed over";       // ignored

        await world.As(world.Owner).Commands.UpdateAsync(legacy.Id, input);
        var stored = ReviewTestHelpers.Reload(world, legacy.Id);
        Assert.Equal("Renamed", stored.Title);
        Assert.Equal("Imported By Name A/S", stored.CounterpartyName);
        Assert.Null(stored.SupplierId);

        input.SupplierId = world.DefaultSupplier;
        await world.As(world.Owner).Commands.UpdateAsync(legacy.Id, input);
        Assert.Equal("Test Supplier A/S", ReviewTestHelpers.Reload(world, legacy.Id).CounterpartyName);
    }
}

public class Release4TemplateTests
{
    [Fact]
    public async Task Categories_Crud_Order_Active_AndTemplateFiltering()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);
        var supply = await admin.Templates.CreateCategoryAsync(new TemplateCategoryInput { Name = "Supply", Description = "Suppliers" });
        var nda = await admin.Templates.CreateCategoryAsync(new TemplateCategoryInput { Name = "Confidentiality" });
        Assert.Equal(1, supply.Order);
        Assert.Equal(2, nda.Order);
        Assert.Equal(ProblemTypes.Duplicate,
            (await Assert.ThrowsAsync<ConflictException>(() => admin.Templates.CreateCategoryAsync(new TemplateCategoryInput { Name = "supply" }))).ProblemType);
        await Assert.ThrowsAsync<InputValidationException>(() => admin.Templates.CreateCategoryAsync(new TemplateCategoryInput { Name = " " }));

        var supplyTemplate = await admin.Admin.CreateTemplateAsync(
            new TemplateInput { Name = "Supply agreement", Description = "Direct materials", CategoryId = supply.Id, TypeIds = new List<Guid> { world.TypeSupplier }, Language = "en" }, null, null);
        var ndaTemplate = await admin.Admin.CreateTemplateAsync(
            new TemplateInput { Name = "Mutual NDA", CategoryId = nda.Id, TypeIds = new List<Guid> { world.TypeNda }, Language = "da" }, null, null);
        var loose = await admin.Admin.CreateTemplateAsync(new TemplateInput { Name = "General letter" }, null, null);
        Assert.Equal("Supply", supplyTemplate.CategoryName);
        Assert.Equal(new List<Guid> { world.TypeSupplier }, supplyTemplate.TypeIds);
        Assert.Equal(world.TypeSupplier, supplyTemplate.TypeId);
        Assert.Empty(loose.TypeIds);

        await Assert.ThrowsAsync<InputValidationException>(() => admin.Admin.CreateTemplateAsync(new TemplateInput { Name = "Bad", CategoryId = Guid.NewGuid() }, null, null));
        await Assert.ThrowsAsync<InputValidationException>(() => admin.Admin.CreateTemplateAsync(new TemplateInput { Name = "Bad", Language = "xx" }, null, null));

        var reader = world.As(world.Outsider);
        // By category order, then name; templates without a category last.
        Assert.Equal(new[] { "Supply agreement", "Mutual NDA", "General letter" }, (await reader.Templates.ListAsync(null, null, null, null)).Select(t => t.Name).ToArray());
        Assert.Equal("Mutual NDA", Assert.Single(await reader.Templates.ListAsync(nda.Id, null, null, null)).Name);
        // A type filter keeps templates for that type and templates for every type.
        Assert.Equal(new[] { "Supply agreement", "General letter" }, (await reader.Templates.ListAsync(null, world.TypeSupplier, null, null)).Select(t => t.Name).ToArray());
        Assert.Equal("Mutual NDA", Assert.Single(await reader.Templates.ListAsync(null, null, "da", null)).Name);
        Assert.Equal("Supply agreement", Assert.Single(await reader.Templates.ListAsync(null, null, null, "materials")).Name);

        // Order.
        var reordered = await admin.Templates.ReorderCategoriesAsync(new IdsInput { Ids = new List<Guid> { nda.Id, supply.Id } });
        Assert.Equal(new[] { "Confidentiality", "Supply" }, reordered.Select(c => c.Name).ToArray());
        Assert.Equal("Mutual NDA", (await reader.Templates.ListAsync(null, null, null, null)).First().Name);
        await Assert.ThrowsAsync<InputValidationException>(() => admin.Templates.ReorderCategoriesAsync(new IdsInput { Ids = new List<Guid> { nda.Id } }));

        // An inactive category hides its templates from users; administrators still see everything.
        await admin.Templates.UpdateCategoryAsync(nda.Id, new TemplateCategoryInput { Name = "Confidentiality", IsActive = false });
        Assert.DoesNotContain(await reader.Templates.ListAsync(null, null, null, null), t => t.Id == ndaTemplate.Id);
        Assert.Equal("Supply", Assert.Single(await reader.Templates.GetActiveCategoriesAsync()).Name);
        Assert.Equal(1, (await reader.Templates.GetActiveCategoriesAsync()).Single().TemplateCount);
        Assert.Equal(3, (await admin.Admin.GetTemplatesAsync()).Count);
        Assert.Equal(2, (await admin.Templates.GetAllCategoriesAsync()).Count);

        // Delete: refused while it has templates.
        Assert.Equal(ProblemTypesV3.InUse, (await Assert.ThrowsAsync<ConflictException>(() => admin.Templates.DeleteCategoryAsync(supply.Id))).ProblemType);
        var empty = await admin.Templates.CreateCategoryAsync(new TemplateCategoryInput { Name = "Empty" });
        await admin.Templates.DeleteCategoryAsync(empty.Id);
        Assert.Equal(2, (await admin.Templates.GetAllCategoriesAsync()).Count);

        using var db = world.NewDb();
        Assert.True(db.AuditEvents.Count(e => e.Action == "settings.template-categories") >= 5);
    }

    [Fact]
    public async Task Preview_ListsPlaceholders_WithLabelsAndSamples()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);
        var template = await admin.Admin.CreateTemplateAsync(new TemplateInput { Name = "Starter", Description = "A starter" }, null, null);

        var preview = await world.As(world.Outsider).Templates.PreviewAsync(template.Id);

        Assert.Equal("Starter", preview.Name);
        Assert.Equal(template.Placeholders.Count, preview.Placeholders.Count);
        Assert.NotEmpty(preview.Placeholders);
        Assert.All(preview.Placeholders, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Label));
            Assert.False(string.IsNullOrWhiteSpace(p.Sample));
        });
        Assert.True(preview.Excerpt.Length <= TemplateCatalogService.ExcerptLength + 1);
        Assert.Equal("Counterparty name", TemplateCatalog.LabelFor("counterpartyName"));
        Assert.Equal("Start date", TemplateCatalog.LabelFor("start_date"));
        await Assert.ThrowsAsync<NotFoundException>(() => world.As(world.Outsider).Templates.PreviewAsync(Guid.NewGuid()));
    }
}

public class Release4BrandingTests
{
    [Fact]
    public void LogoRules_AcceptTheFourTypes_ByContent_NotByName()
    {
        Assert.Equal(LogoRules.Png, LogoRules.Validate("logo.png", R4.Png()));
        Assert.Equal(LogoRules.Jpeg, LogoRules.Validate("logo.JPG", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }));
        Assert.Equal(LogoRules.Webp, LogoRules.Validate("logo.webp", Encoding.ASCII.GetBytes("RIFF").Concat(new byte[] { 1, 0, 0, 0 }).Concat(Encoding.ASCII.GetBytes("WEBPVP8 ")).ToArray()));
        Assert.Equal(LogoRules.Svg, LogoRules.Validate("logo.svg", R4.Svg()));
        Assert.Equal(LogoRules.Svg, LogoRules.Validate("logo.svg",
            Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- made by hand -->\n<svg xmlns=\"http://www.w3.org/2000/svg\"><defs><linearGradient id=\"g\"/></defs><rect fill=\"url(#g)\"/><use href=\"#g\"/></svg>")));
    }

    [Theory]
    [InlineData("logo.gif")]            // type not allowed
    [InlineData("logo.exe")]
    [InlineData("logo")]
    public void LogoRules_RejectOtherExtensions(string fileName)
    {
        Assert.True(Assert.Throws<InputValidationException>(() => LogoRules.Validate(fileName, R4.Png())).Errors.ContainsKey("file"));
    }

    [Fact]
    public void LogoRules_RejectWrongContent_EmptyAndTooLarge()
    {
        // A PNG named .svg, an SVG named .png, an executable named .png, HTML named .svg.
        Assert.Throws<InputValidationException>(() => LogoRules.Validate("logo.svg", R4.Png()));
        Assert.Throws<InputValidationException>(() => LogoRules.Validate("logo.png", R4.Svg()));
        Assert.Throws<InputValidationException>(() => LogoRules.Validate("logo.png", new byte[] { 0x4D, 0x5A, 0x90, 0x00 }));
        Assert.Throws<InputValidationException>(() => LogoRules.Validate("logo.svg", Encoding.UTF8.GetBytes("<html><body><svg></svg></body></html>")));
        Assert.Throws<InputValidationException>(() => LogoRules.Validate("logo.png", Array.Empty<byte>()));

        var big = new byte[LogoRules.MaxBytes + 1];
        R4.Png().CopyTo(big, 0);
        var tooLarge = Assert.Throws<InputValidationException>(() => LogoRules.Validate("logo.png", big));
        Assert.Contains("1 MB", tooLarge.Errors["file"][0]);
        var justFits = new byte[LogoRules.MaxBytes];
        R4.Png().CopyTo(justFits, 0);
        Assert.Equal(LogoRules.Png, LogoRules.Validate("logo.png", justFits));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<SCRIPT xlink:href=\"#a\"/>")]
    [InlineData("<rect width=\"1\" height=\"1\" onload=\"alert(1)\"/>")]
    [InlineData("<rect width=\"1\" height=\"1\" ONCLICK = 'alert(1)'/>")]
    [InlineData("<foreignObject><div xmlns=\"http://www.w3.org/1999/xhtml\">x</div></foreignObject>")]
    [InlineData("<image href=\"https://evil.example/track.png\"/>")]
    [InlineData("<image xlink:href='//evil.example/track.png'/>")]
    [InlineData("<a href=\"javascript:alert(1)\"><rect width=\"1\" height=\"1\"/></a>")]
    [InlineData("<image href=\"data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=\"/>")]
    [InlineData("<rect style=\"fill:url(https://evil.example/x.svg#a)\"/>")]
    [InlineData("<style>@import url('https://evil.example/a.css');</style>")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">x</a>")]
    [InlineData("<iframe src=\"#\"/>")]
    [InlineData("<use href=\"other.svg#icon\"/>")]
    public void LogoRules_RejectSvgThatCouldRunOrLoadSomething(string inner)
    {
        Assert.NotNull(LogoRules.SvgProblem(R4.Svg(inner)));
        Assert.True(Assert.Throws<InputValidationException>(() => LogoRules.Validate("logo.svg", R4.Svg(inner))).Errors.ContainsKey("file"));
    }

    [Fact]
    public void LogoRules_RejectSvgWithEntitiesOrProcessingInstructions()
    {
        Assert.Throws<InputValidationException>(() => LogoRules.Validate("logo.svg",
            Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\"><text>&xxe;</text></svg>")));
        Assert.Throws<InputValidationException>(() => LogoRules.Validate("logo.svg",
            Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><?xml-stylesheet href=\"https://evil.example/a.css\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>")));
    }

    [Fact]
    public async Task Logo_Upload_Replace_Delete_AndVariants_AreAudited()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);
        Assert.Null((await admin.Branding.GetAsync()).LogoUrl);
        Assert.Equal("0", (await admin.Branding.GetAsync()).Version);
        Assert.Null(await admin.Branding.GetFileAsync("full"));

        var first = await admin.Branding.UploadAsync("full", "DAFA logo.png", R4.Png());
        Assert.StartsWith("branding/logo?variant=full&v=", first.LogoUrl);
        Assert.Null(first.CompactLogoUrl);
        var file = await admin.Branding.GetFileAsync(null);      // full is the default variant
        Assert.Equal(LogoRules.Png, file!.ContentType);
        Assert.EndsWith(file.ETag, first.LogoUrl);

        // Replace: the address (and so the cache key) changes.
        var second = await admin.Branding.UploadAsync("full", "logo.svg", R4.Svg());
        Assert.NotEqual(first.LogoUrl, second.LogoUrl);
        Assert.NotEqual(first.Version, second.Version);
        Assert.Equal(LogoRules.Svg, (await admin.Branding.GetFileAsync("full"))!.ContentType);

        // Compact icon and dark-theme logo are separate variants with the same rules.
        var withIcon = await admin.Branding.UploadAsync("compact", "icon.png", R4.Png());
        Assert.NotNull(withIcon.CompactLogoUrl);
        var withDark = await admin.Branding.UploadAsync("dark", "dark.svg", R4.Svg("<rect width=\"10\" height=\"10\" fill=\"#ffffff\"/>"));
        Assert.StartsWith("branding/logo?variant=dark&v=", withDark.DarkLogoUrl);
        await Assert.ThrowsAsync<InputValidationException>(() => admin.Branding.UploadAsync("dark", "dark.svg", R4.Svg("<script/>")));
        Assert.True((await Assert.ThrowsAsync<InputValidationException>(() => admin.Branding.UploadAsync("huge", "logo.png", R4.Png()))).Errors.ContainsKey("variant"));

        var afterDelete = await admin.Branding.DeleteAsync("full");
        Assert.Null(afterDelete.LogoUrl);
        Assert.NotNull(afterDelete.CompactLogoUrl);
        Assert.NotNull(afterDelete.DarkLogoUrl);

        // Exposed to every user in app-config, together with the formats and the storage mode.
        var config = await world.As(world.Outsider).Personal.GetAppConfigAsync();
        Assert.Equal(afterDelete.CompactLogoUrl, config.Branding.CompactLogoUrl);
        Assert.Equal("da-DK", config.NumberFormat);
        Assert.Equal("dd-MM-yyyy", config.DateFormat);
        Assert.Equal("Local", config.StorageMode);
        Assert.False(config.CanCreateCounterparty);
        Assert.Equal("None", config.ESign.Provider);
        Assert.False(config.ESign.Enabled);

        using var db = world.NewDb();
        Assert.Equal(5, db.AuditEvents.Count(e => e.Action == "settings.branding"));
    }

    [Fact]
    public async Task Theme_IsAPreference_ValidatedAndDefaultLight()
    {
        using var world = new TestWorld();
        var user = world.As(world.Outsider);
        var preferences = await user.Personal.GetPreferencesAsync();
        Assert.Equal("light", preferences.Theme);
        Assert.Equal("light", (await user.Personal.GetAppConfigAsync()).Theme);

        preferences.Theme = "dark";
        Assert.Equal("dark", (await user.Personal.UpdatePreferencesAsync(preferences)).Theme);
        preferences.Theme = null;                    // omitted by an older client: unchanged
        Assert.Equal("dark", (await user.Personal.UpdatePreferencesAsync(preferences)).Theme);
        preferences.Theme = "system";
        Assert.Equal("system", (await user.Personal.UpdatePreferencesAsync(preferences)).Theme);
        Assert.Equal("system", (await user.Personal.GetAppConfigAsync()).Theme);

        preferences.Theme = "sepia";
        Assert.True((await Assert.ThrowsAsync<InputValidationException>(() => user.Personal.UpdatePreferencesAsync(preferences))).Errors.ContainsKey("theme"));
    }
}

public class Release4OrgLinkTests
{
    private const string SiteUrl = "https://dafa.sharepoint.com/sites/contracts-entity-a";
    private const string GroupId = "11111111-2222-3333-4444-555555555555";

    private static LegalEntityInput EntityInput(string name, string? siteUrl = null, string? groupId = null) => new()
    {
        Name = name, Country = "DK", Currency = "DKK", IsActive = true, SharePointSiteUrl = siteUrl, ManagementGroupId = groupId
    };

    [Fact]
    public async Task Entity_Mapping_IsValidated_Saved_AndResetsTheLinkWhenChanged()
    {
        using var world = new TestWorld();
        var admin = world.As(world.Admin);

        foreach (var bad in new[] { "http://dafa.sharepoint.com/sites/x", "https://evil.example/sites/x", "https://dafa.sharepoint.com/teams/x", "https://dafa.sharepoint.com/sites/x/Shared Documents" })
        {
            var error = await Assert.ThrowsAsync<InputValidationException>(() => admin.AdminDirectory.UpdateEntityAsync(world.EntityA, EntityInput("Entity A", bad)));
            Assert.True(error.Errors.ContainsKey("sharePointSiteUrl"), bad);
        }
        Assert.True((await Assert.ThrowsAsync<InputValidationException>(
            () => admin.AdminDirectory.UpdateEntityAsync(world.EntityA, EntityInput("Entity A", SiteUrl, "the-managers")))).Errors.ContainsKey("managementGroupId"));

        var saved = await admin.AdminDirectory.UpdateEntityAsync(world.EntityA, EntityInput("Entity A", SiteUrl + "/", GroupId));
        Assert.Equal(SiteUrl, saved.SharePointSiteUrl);
        Assert.Equal(GroupId, saved.ManagementGroupId);
        Assert.Null(saved.SharePointSiteId);
        Assert.Equal("NotLinked", saved.Link.State);

        // Every legal entity has its own site.
        Assert.True((await Assert.ThrowsAsync<InputValidationException>(
            () => admin.AdminDirectory.UpdateEntityAsync(world.EntityB, EntityInput("Entity B", SiteUrl)))).Errors.ContainsKey("sharePointSiteUrl"));

        // Test mode (local files, seeded directory): formats only, nothing is called, and it says so.
        var test = await admin.OrgLinks.TestEntityAsync(world.EntityA);
        Assert.Equal("Linked", test.Link.State);
        Assert.NotNull(test.Link.CheckedAt);
        Assert.Contains(OrgLinkRules.TestModeDetail, test.Details);
        Assert.Null(test.SiteId);
        Assert.Equal("Linked", (await admin.AdminDirectory.GetEntitiesAsync()).Single(e => e.Id == world.EntityA).Link.State);

        // Saving the same values keeps the link; changing the site resets it.
        Assert.Equal("Linked", (await admin.AdminDirectory.UpdateEntityAsync(world.EntityA, EntityInput("Entity A", SiteUrl, GroupId))).Link.State);
        var changed = await admin.AdminDirectory.UpdateEntityAsync(world.EntityA, EntityInput("Entity A", SiteUrl + "-new", GroupId));
        Assert.Equal("NotLinked", changed.Link.State);
        Assert.Null(changed.Link.CheckedAt);

        Assert.Equal("NotLinked", (await admin.OrgLinks.TestEntityAsync(world.EntityB)).Link.State);
        using var db = world.NewDb();
        Assert.Equal(2, db.AuditEvents.Count(e => e.Action == "entity.link-tested"));
    }

    [Fact]
    public async Task SharePointMode_ResolvesTheSite_ChecksLibrariesAndGroups()
    {
        using var world = new TestWorld();
        world.Modes.StorageMode = "SharePoint";
        world.Modes.DirectoryMode = "Graph";
        world.AddContract();                                  // Entity A / Purchasing
        var admin = world.As(world.Admin);
        await admin.AdminDirectory.UpdateEntityAsync(world.EntityA, EntityInput("Entity A", SiteUrl, GroupId));

        // The site does not exist (yet).
        var missingSite = await admin.OrgLinks.TestEntityAsync(world.EntityA);
        Assert.Equal("Error", missingSite.Link.State);
        Assert.Null(missingSite.SiteId);

        // The site exists, but the library of a department with contracts is missing, and the group is unknown.
        world.Probe.Sites[SiteUrl] = new SiteInfo("site-a", SiteUrl, "Contracts Entity A");
        world.Probe.Libraries["site-a"] = new List<string> { "IT" };
        var missingLibrary = await admin.OrgLinks.TestEntityAsync(world.EntityA);
        Assert.Equal("Error", missingLibrary.Link.State);
        Assert.Equal("site-a", missingLibrary.SiteId);
        Assert.Contains(missingLibrary.Details, d => d.Contains("\"Purchasing\"") && d.Contains("missing"));

        // Everything in place: Linked, the site id and the group name are stored.
        world.Probe.Libraries["site-a"] = new List<string> { "IT", "purchasing" };
        world.Probe.Groups[GroupId] = "SG-Contracts-EntityA-Management";
        var linked = await admin.OrgLinks.TestEntityAsync(world.EntityA);
        Assert.Equal("Linked", linked.Link.State);
        Assert.Equal("SG-Contracts-EntityA-Management", linked.GroupName);
        var stored = (await admin.AdminDirectory.GetEntitiesAsync()).Single(e => e.Id == world.EntityA);
        Assert.Equal("site-a", stored.SharePointSiteId);
        Assert.Equal("SG-Contracts-EntityA-Management", stored.ManagementGroupName);

        // Departments: a library name of their own, checked on every linked site.
        var department = await admin.Admin.UpdateDepartmentAsync(world.Purchasing, new NamedItemDto { Name = "Purchasing", DocumentLibrary = "Procurement", GroupId = GroupId });
        Assert.Equal("Procurement", department.DocumentLibrary);
        Assert.Equal("NotLinked", department.Link!.State);
        var departmentTest = await admin.OrgLinks.TestDepartmentAsync(world.Purchasing);
        Assert.Equal("Error", departmentTest.Link.State);
        world.Probe.Libraries["site-a"] = new List<string> { "IT", "Procurement" };
        Assert.Equal("Linked", (await admin.OrgLinks.TestDepartmentAsync(world.Purchasing)).Link.State);
        Assert.True((await Assert.ThrowsAsync<InputValidationException>(
            () => admin.Admin.UpdateDepartmentAsync(world.Purchasing, new NamedItemDto { Name = "Purchasing", DocumentLibrary = "A/B" }))).Errors.ContainsKey("documentLibrary"));

        // A crash of the outside call is 502 link-failed, and the status says Error.
        world.Probe.Failure = new HttpRequestException("connection refused");
        var crash = await Assert.ThrowsAsync<ExternalCallException>(() => admin.OrgLinks.TestEntityAsync(world.EntityA));
        Assert.Equal(502, crash.StatusCode);
        Assert.Equal(ProblemTypesV4.LinkFailed, crash.ProblemType);
        Assert.Equal("Error", (await admin.AdminDirectory.GetEntitiesAsync()).Single(e => e.Id == world.EntityA).Link.State);
    }

    [Fact]
    public async Task AccessMapping_ShowsTheMatrix_WithWarningsForMissingMappings()
    {
        using var world = new TestWorld();
        world.AddContract();                                                   // Entity A / Purchasing
        world.AddContract(c => { c.EntityId = world.EntityB; c.DepartmentId = world.It; c.OwnerId = world.ItHead; });
        var admin = world.As(world.Admin);
        await admin.AdminDirectory.UpdateEntityAsync(world.EntityA, EntityInput("Entity A", SiteUrl, GroupId));
        await admin.Admin.UpdateDepartmentAsync(world.Purchasing, new NamedItemDto { Name = "Purchasing", DocumentLibrary = "Procurement", GroupId = GroupId, GroupName = "SG-Purchasing" });

        var mapping = await admin.OrgLinks.GetAccessMappingAsync();

        Assert.Equal("Local", mapping.Mode);
        Assert.Equal(2, mapping.Entities.Count);
        var mapped = mapping.Cells.Single(c => c.EntityId == world.EntityA && c.DepartmentId == world.Purchasing);
        Assert.Equal(SiteUrl, mapped.SiteUrl);
        Assert.Equal("Procurement", mapped.Library);
        Assert.Equal("SG-Purchasing", mapped.DepartmentGroupName);
        Assert.Equal(1, mapped.ContractCount);
        Assert.Empty(mapped.Warnings);

        var unmapped = mapping.Cells.Single(c => c.EntityId == world.EntityB && c.DepartmentId == world.It);
        Assert.Null(unmapped.SiteUrl);
        Assert.Null(unmapped.Library);
        Assert.Contains(unmapped.Warnings, w => w.Contains("No SharePoint site"));
        Assert.Contains("Entity B: no SharePoint site (1 contracts)", mapping.Warnings);
        Assert.Contains("1 contract in Entity B / IT has no site", mapping.Warnings);
        Assert.Contains("IT: no Entra group", mapping.Warnings);
        Assert.Contains("Entity B: no management group", mapping.Warnings);
        Assert.DoesNotContain(mapping.Warnings, w => w.StartsWith("Entity A: no SharePoint site", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DocumentStore_ReceivesTheMappedSiteAndLibrary_NotAConvention()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();
        var admin = world.As(world.Admin);
        await admin.AdminDirectory.UpdateEntityAsync(world.EntityA, EntityInput("Entity A", SiteUrl));
        await admin.Admin.UpdateDepartmentAsync(world.Purchasing, new NamedItemDto { Name = "Purchasing", DocumentLibrary = "Procurement" });

        await world.As(world.Owner).Documents.UploadAsync(contract.Id, "note.txt", "Other", new MemoryStream(Encoding.UTF8.GetBytes("hello")));

        var location = Assert.Single(world.Store.SavedLocations);
        Assert.Equal(SiteUrl, location.SiteUrl);
        Assert.Null(location.SiteId);                 // not tested yet, so not resolved
        Assert.Equal("Procurement", location.LibraryName);
        Assert.Equal("Purchasing", location.DepartmentName);
        Assert.Equal(contract.ContractNo, location.ContractNo);
    }
}

public class Release4SettingsTests
{
    [Fact]
    public async Task Me_ReflectsARoleAndPermissionChange_Immediately()
    {
        using var world = new TestWorld();
        var before = await world.As(world.Outsider).Queries.MeAsync("Dev");
        Assert.Equal(new List<string> { "Reader" }, before.Roles);
        Assert.False(before.CanSendForSignature);
        Assert.Null(before.EntityId);

        var input = new AdminUserInput
        {
            Roles = new List<string> { "Reader", "ContractOwner", "Admin" },
            CanSendForSignature = true,
            SignatureEntityIds = new List<Guid> { world.EntityB }
        };
        input.SetEntity(world.EntityA);
        var updated = await world.As(world.Admin).AdminDirectory.UpdateUserAsync(world.Outsider, input);
        Assert.True(updated.CanSendForSignature);
        Assert.Equal(world.EntityA, updated.EntityId);

        // The very next read, on a new unit of work, sees it: nothing is cached between requests.
        var after = await world.As(world.Outsider).Queries.MeAsync("Dev");
        Assert.Contains("Admin", after.Roles);
        Assert.Contains("ContractOwner", after.Roles);
        Assert.True(after.CanSendForSignature);
        Assert.Equal(new List<Guid> { world.EntityB }, after.SignatureEntityIds);
        Assert.Equal(world.EntityA, after.EntityId);

        // Omitted fields stay as they are; null clears the home entity; an inactive entity is refused.
        var keep = await world.As(world.Admin).AdminDirectory.UpdateUserAsync(world.Outsider, new AdminUserInput { Title = "Analyst" });
        Assert.True(keep.CanSendForSignature);
        Assert.Equal(world.EntityA, keep.EntityId);
        var clear = new AdminUserInput { CanSendForSignature = false };
        clear.SetEntity(null);
        var cleared = await world.As(world.Admin).AdminDirectory.UpdateUserAsync(world.Outsider, clear);
        Assert.Null(cleared.EntityId);
        Assert.False((await world.As(world.Outsider).Queries.MeAsync("Dev")).CanSendForSignature);

        var unknown = new AdminUserInput();
        unknown.SetEntity(Guid.NewGuid());
        Assert.True((await Assert.ThrowsAsync<InputValidationException>(
            () => world.As(world.Admin).AdminDirectory.UpdateUserAsync(world.Outsider, unknown))).Errors.ContainsKey("entityId"));

        using var db = world.NewDb();
        Assert.Contains(db.AuditEvents.Where(e => e.Action == "settings.users").Select(e => e.Text).ToList(), t => t.Contains("signing permission changed"));
    }

    [Fact]
    public async Task EveryAdminSettingsRead_AnswersOnAnEmptyDatabase_WithDefaults()
    {
        // Nothing has been saved yet: no settings rows, no job rows, no categories. Each read must still answer.
        using var world = new TestWorld();
        var admin = world.As(world.Admin);

        Assert.Equal("DAFA Contracts", (await admin.Settings.GetAppSettingsAsync()).AppName);
        Assert.Equal(NotificationCatalog.All.Count, (await admin.Settings.GetNotificationSettingsAsync()).Events.Count);
        Assert.Equal(JobKeys.All.Length, (await admin.Settings.GetJobsAsync()).Count);
        Assert.Equal("None", (await admin.Signatures.GetSettingsAsync()).Provider);
        Assert.Empty(await admin.Templates.GetAllCategoriesAsync());
        Assert.Empty(await admin.Admin.GetTemplatesAsync());
        Assert.Equal(2, (await admin.AdminDirectory.GetEntitiesAsync()).Count);
        Assert.Equal(2, (await admin.Admin.GetDepartmentsAsync()).Count);
        Assert.NotEmpty(await admin.AdminDirectory.GetUsersAsync(null, null));
        Assert.Equal("Local", (await admin.OrgLinks.GetAccessMappingAsync()).Mode);
        Assert.Equal("Healthy", (await admin.Storage.GetHealthAsync()).State);
        Assert.Null(await admin.Storage.GetLatestAsync());
        Assert.Equal("0", (await admin.Branding.GetAsync()).Version);
        // Reading twice gives the same answer (the job rows are created by the first read).
        Assert.Equal(JobKeys.All.Length, (await world.As(world.Admin).Settings.GetJobsAsync()).Count);
    }
}

public class Release4SchemaTests
{
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Steps3And4_UpgradeAReleaseThreeDatabase_KeepItsRows_AndAreIdempotent()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        // Release 3 style tables with data, without the release 4 columns, and steps 1 and 2 recorded.
        Execute(connection,
            "CREATE TABLE \"LegalEntities\" (\"Id\" TEXT NOT NULL CONSTRAINT \"PK_LegalEntities\" PRIMARY KEY, \"Name\" TEXT NOT NULL, \"SortOrder\" INTEGER NOT NULL, \"Country\" TEXT NULL, \"Currency\" TEXT NULL, \"IsActive\" INTEGER NOT NULL);"
            + "INSERT INTO \"LegalEntities\" VALUES ('3F2504E0-4F89-11D3-9A0C-0305E82C3301', 'Old Entity A/S', 1, 'DK', 'DKK', 1);"
            + "CREATE TABLE \"Departments\" (\"Id\" TEXT NOT NULL CONSTRAINT \"PK_Departments\" PRIMARY KEY, \"Name\" TEXT NOT NULL, \"IsActive\" INTEGER NOT NULL, \"SortOrder\" INTEGER NOT NULL);"
            + "INSERT INTO \"Departments\" VALUES ('3F2504E0-4F89-11D3-9A0C-0305E82C3302', 'Old Department', 1, 1);"
            + "CREATE TABLE \"Users\" (\"Id\" TEXT NOT NULL CONSTRAINT \"PK_Users\" PRIMARY KEY, \"Name\" TEXT NOT NULL, \"Email\" TEXT NOT NULL, \"Initials\" TEXT NOT NULL, \"JobTitle\" TEXT NULL, \"ExternalId\" TEXT NULL, \"Roles\" INTEGER NOT NULL, \"IsGroupManagement\" INTEGER NOT NULL, \"GroupFunction\" INTEGER NOT NULL, \"IsActive\" INTEGER NOT NULL, \"Source\" TEXT NULL, \"LastSignInAt\" TEXT NULL);"
            + "INSERT INTO \"Users\" VALUES ('3F2504E0-4F89-11D3-9A0C-0305E82C3303', 'Old User', 'old@example.test', 'OU', NULL, NULL, 9, 0, 0, 1, 'Seed', NULL);"
            + "CREATE TABLE \"UserPreferences\" (\"UserId\" TEXT NOT NULL CONSTRAINT \"PK_UserPreferences\" PRIMARY KEY, \"Digest\" TEXT NULL, \"EventsJson\" TEXT NOT NULL, \"DefaultEntityId\" TEXT NULL, \"LandingPage\" TEXT NULL, \"AutoWatch\" INTEGER NOT NULL, \"LastDigestAt\" TEXT NULL, \"UpdatedAt\" TEXT NOT NULL);"
            + "INSERT INTO \"UserPreferences\" VALUES ('3F2504E0-4F89-11D3-9A0C-0305E82C3303', 'Weekly', '[]', NULL, NULL, 0, NULL, '2026-01-01 00:00:00');"
            + "CREATE TABLE \"SchemaVersion\" (\"Version\" INTEGER NOT NULL, \"Description\" TEXT NOT NULL, \"AppliedAt\" TEXT NOT NULL, CONSTRAINT \"PK_SchemaVersion\" PRIMARY KEY (\"Version\"));"
            + "INSERT INTO \"SchemaVersion\" VALUES (1, 'baseline', '2026-11-01 00:00:00');"
            + "INSERT INTO \"SchemaVersion\" VALUES (2, 'release 3', '2026-11-20 00:00:00');");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        using (var db = new AppDbContext(options))
        {
            var first = await new SchemaUpgrader(db).UpgradeAsync();
            Assert.Equal(new List<int> { 3, 4 }, first.AppliedVersions);
            Assert.Equal(4, first.CurrentVersion);
            Assert.Contains(first.Statements, s => s.Contains("\"CanSendForSignature\"") && s.Contains("NOT NULL DEFAULT 0"));
            Assert.Contains(first.Statements, s => s.Contains("\"Theme\"") && s.Contains("NOT NULL DEFAULT 'light'"));
            Assert.Contains(first.Statements, s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal) && s.Contains("\"SignatureRequests\""));
            // Additive only.
            Assert.DoesNotContain(first.Statements, s => s.Contains("DROP ", StringComparison.OrdinalIgnoreCase) || s.Contains("DELETE ", StringComparison.OrdinalIgnoreCase));
        }
        using (var db = new AppDbContext(options))
        {
            var second = await new SchemaUpgrader(db).UpgradeAsync();
            Assert.Empty(second.AppliedVersions);
            Assert.Empty(second.Statements);
            Assert.Equal(new List<int> { 1, 2, 3, 4 }, await new SchemaUpgrader(db).GetRecordedVersionsAsync());
        }
        using (var db = new AppDbContext(options))
        {
            // The old rows survived with safe defaults.
            var entity = db.LegalEntities.Single();
            Assert.Equal("Old Entity A/S", entity.Name);
            Assert.Null(entity.SharePointSiteUrl);
            Assert.Null(entity.LinkStatus);
            Assert.Null(db.Departments.Single().LibraryName);
            var user = db.Users.Single();
            Assert.Equal("Old User", user.Name);
            Assert.False(user.CanSendForSignature);          // nobody may send for signature until an administrator says so
            Assert.Null(user.SignatureEntityIdsJson);
            Assert.Null(user.HomeEntityId);
            Assert.Equal("light", db.UserPreferences.Single().Theme);

            // Every release 4 table works through EF.
            var requestId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            db.TemplateCategories.Add(new TemplateCategory { Id = Guid.NewGuid(), Name = "After upgrade", SortOrder = 1 });
            db.BrandingAssets.Add(new BrandingAsset { Kind = "logo", FileName = "a.png", ContentType = "image/png", Content = new byte[] { 1 }, Size = 1, ETag = "x", UpdatedAt = DateTime.UtcNow });
            db.SignatureRequests.Add(new SignatureRequest { Id = requestId, ContractId = contractId, Provider = "Simulated", Status = SignatureRequestStatus.Sent, CreatedById = user.Id, CreatedAt = DateTime.UtcNow });
            db.Signatories.Add(new Signatory { Id = Guid.NewGuid(), RequestId = requestId, ContractId = contractId, Name = "A", Email = "a@example.test", SortOrder = 1 });
            db.StorageReconciliationReports.Add(new StorageReconciliationReport { Id = Guid.NewGuid(), StartedAt = DateTime.UtcNow, Status = "Completed" });
            db.Templates.Add(new ContractTemplate { Id = Guid.NewGuid(), Name = "T", FileName = "t.docx", Language = "en", UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
            Assert.Equal(1, db.SignatureRequests.Count());
            Assert.Equal(0, db.Documents.Count(d => d.ContentHash != null));
            Assert.Equal(0, db.ApprovalInstances.Count(i => i.LogPending));

            // The filtered unique index exists: a second open request for the same contract is refused.
            db.SignatureRequests.Add(new SignatureRequest { Id = Guid.NewGuid(), ContractId = contractId, Provider = "Simulated", Status = SignatureRequestStatus.PartiallySigned, CreatedById = user.Id, CreatedAt = DateTime.UtcNow });
            Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        }
    }

    [Fact]
    public async Task OnAFreshDatabase_AllFourStepsFindTheirWorkDone()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        var result = await new SchemaUpgrader(db).UpgradeAsync();
        Assert.Equal(new List<int> { 1, 2, 3, 4 }, result.AppliedVersions);
        Assert.Single(result.Statements);          // only the SchemaVersion table itself
        Assert.Empty(result.Warnings);
        Assert.Empty((await new SchemaUpgrader(db).UpgradeAsync()).Statements);
    }
}
