using System.Text;
using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Services;
using Dafa.Contracts.Infrastructure.Data;
using Dafa.Contracts.Infrastructure.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dafa.Contracts.Infrastructure.Seed;

/// <summary>
/// Creates the schema (EnsureCreated, no migrations yet) and seeds data. Safe to run at every start:
/// reference data is added only to empty tables, demo data only when Seed:DemoData=true and absent.
/// </summary>
public class DatabaseInitializer
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;
    private readonly ApprovalService _approvals;
    private readonly DocumentWriter _documents;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        AppDbContext db, IConfiguration configuration, IClock clock, ApprovalService approvals,
        DocumentWriter documents, ILogger<DatabaseInitializer> logger)
    {
        _db = db;
        _configuration = configuration;
        _clock = clock;
        _approvals = approvals;
        _documents = documents;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsureSqliteFolder();
        await _db.Database.EnsureCreatedAsync(cancellationToken);
        // Release 3: bring an existing database up to the current model (adds tables, columns and
        // indexes; never drops). On a database EnsureCreated just made, every step finds its work done.
        var upgrade = await new SchemaUpgrader(_db, _logger).UpgradeAsync(cancellationToken);
        if (upgrade.AppliedVersions.Count > 0)
        {
            _logger.LogInformation("Schema upgraded to version {Version} ({Count} statements)", upgrade.CurrentVersion, upgrade.Statements.Count);
        }
        foreach (var warning in upgrade.Warnings)
        {
            _logger.LogWarning("Schema upgrade: {Warning}", warning);
        }
        await SeedReferenceDataAsync(cancellationToken);
        // Demo users and contracts are for the test environment only. Off unless Seed:DemoData=true
        // is set explicitly, and only with Dev sign-in: never with Entra, never with an unknown mode.
        var devMode = string.Equals((_configuration["Auth:Mode"] ?? "").Trim(), "Dev", StringComparison.OrdinalIgnoreCase);
        if (_configuration.GetValue("Seed:DemoData", false) && devMode)
        {
            await SeedDemoUsersAsync(cancellationToken);
            await SeedDemoContractsAsync(cancellationToken);
            await SeedDemoTagsAsync(cancellationToken);
            await SeedRelease4DemoAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Release 4 demo data, once (when there are no template categories yet; also on a test database
    /// that was seeded by an earlier release): template categories for the seeded templates, the
    /// Simulated e-signature provider, and the signing permission for two demo users (the Group CFO
    /// for every legal entity, the Head of Purchasing for two). Administrators get nothing implicitly.
    /// </summary>
    private async Task SeedRelease4DemoAsync(CancellationToken cancellationToken)
    {
        if (await _db.TemplateCategories.AnyAsync(cancellationToken))
        {
            return;
        }
        var supply = new TemplateCategory { Id = SeedIds.For("template-category:supply"), Name = "Supply and purchasing", Description = "Agreements with suppliers of materials and services.", SortOrder = 1, IsActive = true };
        var confidentiality = new TemplateCategory { Id = SeedIds.For("template-category:nda"), Name = "Confidentiality", Description = "Non-disclosure agreements.", SortOrder = 2, IsActive = true };
        var services = new TemplateCategory { Id = SeedIds.For("template-category:services"), Name = "IT and services", Description = "Software, hosting and service agreements.", SortOrder = 3, IsActive = true };
        _db.TemplateCategories.AddRange(supply, confidentiality, services);
        var templates = await _db.Templates.ToListAsync(cancellationToken);
        foreach (var template in templates.Where(t => t.CategoryId == null))
        {
            if (template.Name.Contains("NDA", StringComparison.OrdinalIgnoreCase))
            {
                template.CategoryId = confidentiality.Id;
                template.Language = "en";
            }
            else if (template.Name.Contains("Supply", StringComparison.OrdinalIgnoreCase))
            {
                template.CategoryId = supply.Id;
                template.Language = "en";
            }
        }

        if (!await _db.AppSettings.AnyAsync(s => s.Key == ESignSettingsStore.Key, cancellationToken))
        {
            _db.AppSettings.Add(new AppSetting
            {
                Key = ESignSettingsStore.Key,
                Value = JsonDefaults.Write(new Dafa.Contracts.Core.Dtos.ESignSettingsDto { Provider = SignatureProviderNames.Simulated }),
                UpdatedAt = _clock.UtcNow
            });
        }

        var cfoId = SeedIds.User("lars");
        var cfo = await _db.Users.FirstOrDefaultAsync(u => u.Id == cfoId, cancellationToken);
        if (cfo is not null)
        {
            cfo.CanSendForSignature = true;
        }
        var buyerId = SeedIds.User("jonas");
        var buyer = await _db.Users.FirstOrDefaultAsync(u => u.Id == buyerId, cancellationToken);
        if (buyer is not null)
        {
            buyer.CanSendForSignature = true;
            buyer.SignatureEntityIdsJson = JsonDefaults.Write(new List<Guid> { SeedIds.Entity("DAFA A/S"), SeedIds.Entity("DAFA Building Solutions A/S") });
            buyer.HomeEntityId = SeedIds.Entity("DAFA A/S");
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Release 3: a few demo tags on demo contracts (only when there are no tags yet).</summary>
    private async Task SeedDemoTagsAsync(CancellationToken cancellationToken)
    {
        if (await _db.Tags.AnyAsync(cancellationToken))
        {
            return;
        }
        var strategic = new Tag { Id = SeedIds.For("tag:strategic"), Name = "Strategic", Color = "blue", Description = "Contracts that matter for the group strategy." };
        var critical = new Tag { Id = SeedIds.For("tag:critical"), Name = "Critical supplier", Color = "red", Description = "Single source or hard to replace." };
        var diligence = new Tag { Id = SeedIds.For("tag:dd"), Name = "Due diligence", Color = "purple", Description = "Part of the exit data room." };
        _db.Tags.AddRange(strategic, critical, diligence);
        foreach (var (contractKey, tag) in new[] { ("epdm", critical), ("hq-lease", strategic), ("shareholders", diligence), ("credit", diligence) })
        {
            var contractId = SeedIds.For("contract:" + contractKey);
            if (await _db.Contracts.AnyAsync(c => c.Id == contractId, cancellationToken))
            {
                _db.ContractTags.Add(new ContractTag { ContractId = contractId, TagId = tag.Id });
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    private void EnsureSqliteFolder()
    {
        if (!_db.Database.IsSqlite())
        {
            return;
        }
        try
        {
            var connection = new SqliteConnectionStringBuilder(_db.Database.GetConnectionString());
            var dataSource = connection.DataSource;
            if (connection.Mode == SqliteOpenMode.Memory || string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:" || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var folder = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not prepare the folder for the Sqlite database file");
        }
    }

    // ---------- reference data (always) ----------

    private static ApprovalStageDefinition Stage(int order, params ApproverKind[] kinds)
    {
        return new ApprovalStageDefinition
        {
            Order = order,
            Approvers = kinds.Select(k => new ApproverDefinition { Kind = k }).ToList()
        };
    }

    private static ApprovalRule Rule(string name, decimal? min, decimal? max, int priority, Guid? typeId, params ApprovalStageDefinition[] stages)
    {
        return new ApprovalRule
        {
            Id = SeedIds.For("rule:" + name),
            Name = name,
            MinValue = min,
            MaxValue = max,
            Priority = priority,
            TypeId = typeId,
            IsActive = true,
            StagesJson = JsonDefaults.Write(stages.ToList())
        };
    }

    private async Task SeedReferenceDataAsync(CancellationToken cancellationToken)
    {
        if (!await _db.LegalEntities.AnyAsync(cancellationToken))
        {
            var order = 0;
            foreach (var name in SeedNames.Entities)
            {
                _db.LegalEntities.Add(new LegalEntity { Id = SeedIds.Entity(name), Name = name, SortOrder = ++order });
            }
        }
        if (!await _db.Departments.AnyAsync(cancellationToken))
        {
            var order = 0;
            foreach (var name in SeedNames.Departments)
            {
                _db.Departments.Add(new Department { Id = SeedIds.Department(name), Name = name, IsActive = true, SortOrder = ++order });
            }
        }
        if (!await _db.ContractTypes.AnyAsync(cancellationToken))
        {
            var order = 0;
            foreach (var name in SeedNames.ContractTypes)
            {
                _db.ContractTypes.Add(new ContractType { Id = SeedIds.ContractType(name), Name = name, IsActive = true, SortOrder = ++order });
            }
        }
        if (!await _db.ReminderSettings.AnyAsync(cancellationToken))
        {
            _db.ReminderSettings.Add(new ReminderSettings
            {
                Id = SeedIds.For("reminder-settings"),
                NoticeDays = "120,60,30",
                EscalationDays = 14,
                ObligationDays = 14
            });
        }
        if (!await _db.ApprovalRules.AnyAsync(cancellationToken))
        {
            // Placeholder value bands in DKK; the real authority matrix is entered in admin later.
            _db.ApprovalRules.Add(Rule("Under DKK 1m", null, 1_000_000m, 0, null,
                Stage(1, ApproverKind.DepartmentHead)));
            _db.ApprovalRules.Add(Rule("DKK 1–5m", 1_000_000m, 5_000_000m, 0, null,
                Stage(1, ApproverKind.DepartmentHead),
                Stage(2, ApproverKind.EntityManager)));
            _db.ApprovalRules.Add(Rule("DKK 5–20m", 5_000_000m, 20_000_000m, 0, null,
                Stage(1, ApproverKind.DepartmentHead, ApproverKind.Legal),
                Stage(2, ApproverKind.EntityManager),
                Stage(3, ApproverKind.GroupCFO)));
            _db.ApprovalRules.Add(Rule("Over DKK 20m", 20_000_000m, null, 0, null,
                Stage(1, ApproverKind.DepartmentHead, ApproverKind.Legal),
                Stage(2, ApproverKind.EntityManager),
                Stage(3, ApproverKind.GroupCFO, ApproverKind.GroupCEO)));
            // Type rules need a maximum value, otherwise labelling a large contract "NDA" would skip the
            // value bands (M3). With the cap, type + band is more specific than a band alone, and an
            // "NDA" of DKK 1m or more falls through to the bands.
            _db.ApprovalRules.Add(Rule("NDA", null, 1_000_000m, 10, SeedIds.ContractType(SeedNames.TypeNda),
                Stage(1, ApproverKind.Legal)));
            _db.ApprovalRules.Add(Rule("Corporate", null, null, 10, SeedIds.ContractType(SeedNames.TypeCorporate),
                Stage(1, ApproverKind.Legal, ApproverKind.GroupCFO),
                Stage(2, ApproverKind.GroupCEO)));
        }
        if (!await _db.RetentionRules.AnyAsync(cancellationToken))
        {
            var finance = SeedIds.Department("Finance");
            var management = SeedIds.Department("Management");
            AddRetention(SeedNames.TypeDirect, 5, RetentionAction.ReviewThenDelete, finance);
            AddRetention(SeedNames.TypeIndirect, 5, RetentionAction.ReviewThenDelete, finance);
            AddRetention(SeedNames.TypeLogistics, 5, RetentionAction.ReviewThenDelete, finance);
            AddRetention(SeedNames.TypeIt, 5, RetentionAction.ReviewThenDelete, SeedIds.Department("IT"));
            AddRetention(SeedNames.TypeLease, 10, RetentionAction.ReviewThenDelete, finance);
            AddRetention(SeedNames.TypeFinance, 10, RetentionAction.ReviewThenDelete, finance);
            AddRetention(SeedNames.TypeNda, 5, RetentionAction.DeleteAutomatically, null);
            AddRetention(SeedNames.TypeRd, 10, RetentionAction.ReviewThenDelete, SeedIds.Department("R&D and engineering"));
            AddRetention(SeedNames.TypeCorporate, null, RetentionAction.Keep, management);
            AddRetention(SeedNames.TypeOther, 5, RetentionAction.ReviewThenDelete, finance);
        }
        if (!await _db.Currencies.AnyAsync(cancellationToken))
        {
            var names = new Dictionary<string, string>
            {
                ["DKK"] = "Danish krone", ["EUR"] = "Euro", ["USD"] = "US dollar", ["SEK"] = "Swedish krona",
                ["NOK"] = "Norwegian krone", ["GBP"] = "Pound sterling", ["PLN"] = "Polish zloty", ["CNY"] = "Chinese yuan"
            };
            var currencyOrder = 0;
            foreach (var code in CurrencyRates.Currencies)
            {
                _db.Currencies.Add(new CurrencyDefinition
                {
                    Code = code,
                    Name = names.TryGetValue(code, out var currencyName) ? currencyName : code,
                    RateToDkk = CurrencyRates.ToDkk(1m, code),
                    IsActive = true,
                    SortOrder = ++currencyOrder
                });
            }
        }
        if (!await _db.Templates.AnyAsync(cancellationToken))
        {
            AddTemplate("Supply agreement", "Standard DAFA supply agreement for direct materials.", "Supply agreement.docx", SeedNames.TypeDirect, SupplyAgreementLines);
            AddTemplate("Mutual NDA", "Mutual non-disclosure agreement.", "Mutual NDA.docx", SeedNames.TypeNda, MutualNdaLines);
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    private void AddRetention(string typeName, int? years, RetentionAction action, Guid? reviewerDepartmentId)
    {
        _db.RetentionRules.Add(new RetentionRule
        {
            Id = SeedIds.For("retention:" + typeName),
            TypeId = SeedIds.ContractType(typeName),
            Years = years,
            Action = action,
            ReviewerDepartmentId = reviewerDepartmentId
        });
    }

    private void AddTemplate(string name, string description, string fileName, string typeName, string[] lines)
    {
        var content = DocxBuilder.Create(lines);
        var engine = new OpenXmlTemplateEngine();
        _db.Templates.Add(new ContractTemplate
        {
            Id = SeedIds.For("template:" + name),
            Name = name,
            Description = description,
            TypeId = SeedIds.ContractType(typeName),
            FileName = fileName,
            Content = content,
            Placeholders = string.Join(",", engine.GetPlaceholders(content)),
            IsActive = true,
            UpdatedAt = _clock.UtcNow
        });
    }

    private static readonly string[] SupplyAgreementLines =
    {
        "# {{title}}",
        "This supply agreement is entered into between {{entityName}} (\"DAFA\") and {{counterpartyName}} (the \"Supplier\").",
        "1. Scope. The Supplier shall supply the products described in appendix 1 to DAFA on the terms of this agreement.",
        "2. Term. The agreement takes effect on {{startDate}} and remains in force until {{endDate}}.",
        "3. Termination. Either party may terminate the agreement with {{noticePeriodDays}} days' written notice.",
        "4. Prices and payment. Prices are stated in {{currency}}. The expected annual value is {{currency}} {{annualValue}}. Payment terms: {{paymentTerms}}.",
        "5. Price adjustment. Prices may only be adjusted once a year in line with the published net price index.",
        "6. Liability. Each party's total liability under the agreement is limited to an amount equal to 12 months' fees.",
        "7. Governing law. The agreement is governed by {{governingLaw}}.",
        "Signed for {{entityName}}: ____________________",
        "Signed for {{counterpartyName}}: ____________________"
    };

    private static readonly string[] MutualNdaLines =
    {
        "# {{title}}",
        "This mutual non-disclosure agreement is entered into between {{entityName}} and {{counterpartyName}} (each a \"Party\").",
        "1. Purpose. The Parties wish to exchange confidential information for the purpose of evaluating a possible cooperation.",
        "2. Confidentiality. Each Party shall keep the other Party's confidential information secret and use it only for the purpose.",
        "3. Term. The agreement takes effect on {{startDate}} and remains in force until {{endDate}}. The duty of confidentiality continues for three years after expiry.",
        "4. Governing law. The agreement is governed by {{governingLaw}}.",
        "Signed for {{entityName}}: ____________________",
        "Signed for {{counterpartyName}}: ____________________"
    };

    // ---------- demo users ----------

    private void AddUser(
        string key, string name, string jobTitle, AppRole roles, bool groupManagement = false,
        GroupFunction function = GroupFunction.None, string? department = null, bool head = false, string[]? managedEntities = null)
    {
        var id = SeedIds.User(key);
        _db.Users.Add(new AppUser
        {
            Id = id,
            Name = name,
            Email = key + "@dafa-demo.example",
            Initials = UserNames.Initials(name),
            JobTitle = jobTitle,
            Roles = roles,
            IsGroupManagement = groupManagement,
            GroupFunction = function,
            IsActive = true
        });
        if (department is not null)
        {
            _db.UserDepartments.Add(new UserDepartment { UserId = id, DepartmentId = SeedIds.Department(department), IsHead = head });
        }
        if (managedEntities is not null)
        {
            foreach (var entity in managedEntities)
            {
                _db.UserEntityManagements.Add(new UserEntityManagement { UserId = id, EntityId = SeedIds.Entity(entity) });
            }
        }
    }

    /// <summary>Fictional demo users; together they cover every access path of the access model.</summary>
    private async Task SeedDemoUsersAsync(CancellationToken cancellationToken)
    {
        var adminId = SeedIds.User("mette");
        if (await _db.Users.AnyAsync(u => u.Id == adminId, cancellationToken))
        {
            return;
        }

        // 5. admin
        AddUser("mette", "Mette Holm", "IT administrator", AppRole.Reader | AppRole.Admin, department: "IT");
        // 3. group management (+ approver functions)
        AddUser("lars", "Lars Winther", "Group CFO", AppRole.Reader | AppRole.ContractOwner | AppRole.Approver,
            groupManagement: true, function: GroupFunction.GroupCFO, department: "Finance", head: true);
        AddUser("henrik", "Henrik Dahl", "Group CEO", AppRole.Reader | AppRole.ContractOwner | AppRole.Approver,
            groupManagement: true, function: GroupFunction.GroupCEO, department: "Management", head: true,
            managedEntities: new[] { "DAFA Group A/S", "DAFA A/S" });
        // open approval step only: no department, not management
        AddUser("sofie", "Sofie Lund", "Legal counsel", AppRole.Reader | AppRole.Approver, function: GroupFunction.Legal);
        // 1. department member (and head) + 4. owner
        AddUser("jonas", "Jonas Berg", "Head of Purchasing", AppRole.Reader | AppRole.ContractOwner | AppRole.Approver,
            department: "Purchasing", head: true);
        AddUser("peter", "Peter Madsen", "IT manager", AppRole.Reader | AppRole.ContractOwner | AppRole.Approver,
            department: "IT", head: true);
        // 2. entity management
        AddUser("karin", "Karin Svensson", "Managing director, DAFA Sverige AB", AppRole.Reader | AppRole.Approver,
            managedEntities: new[] { "DAFA Sverige AB" });
        // 4. responsible on a contract outside her own department
        AddUser("emma", "Emma Nowak", "HR partner", AppRole.Reader, department: "HR");
        // 1. plain department member
        AddUser("thomas", "Thomas Krogh", "Production planner", AppRole.Reader, department: "Production");
        // no access path at all
        AddUser("nina", "Nina Vester", "New employee", AppRole.Reader);

        if (!await _db.Suppliers.AnyAsync(cancellationToken))
        {
            foreach (var record in SeedSupplierSource.Records)
            {
                _db.Suppliers.Add(new Supplier
                {
                    Id = SeedIds.Supplier(record.ExternalId),
                    ExternalId = record.ExternalId,
                    Name = record.Name,
                    Country = record.Country,
                    VatNo = record.VatNo,
                    IsActive = true
                });
            }
        }

        var today = _clock.Today;
        _db.HolidayCovers.Add(new HolidayCover
        {
            Id = SeedIds.For("cover:sofie"),
            UserId = SeedIds.User("sofie"),
            DeputyId = SeedIds.User("lars"),
            FromDate = today.AddDays(20),
            ToDate = today.AddDays(34)
        });

        await _db.SaveChangesAsync(cancellationToken);
    }

    // ---------- demo contracts ----------

    private readonly Dictionary<string, Contract> _contracts = new();
    private readonly Dictionary<int, int> _numbers = new();

    private Contract AddContract(
        string key, string title, string counterparty, string type, string entity, string department, string owner,
        int startMonthsAgo, int? endInDays, int? noticeDays, bool autoRenew, decimal? annualValue, string currency,
        ContractStatus status = ContractStatus.Active, string? supplierNo = null, string? delegateKey = null,
        string? responsibleKey = null, bool? changeOfControl = false, string? governingLaw = "Danish law",
        decimal? totalValue = null, string? liabilityCap = "12 months' fees", string? priceAdjustment = null,
        string? exclusivity = null, RenewalDecision? decision = null, int? renewalMonths = null)
    {
        var today = _clock.Today;
        var now = _clock.UtcNow;
        var start = today.AddMonths(-startMonthsAgo);
        var year = start.Year > today.Year ? today.Year : start.Year;
        _numbers.TryGetValue(year, out var last);
        _numbers[year] = last + 1;

        var created = start.AddDays(-30).ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);
        if (created > now.AddDays(-3))
        {
            created = now.AddDays(-3);
        }
        var notSigned = status == ContractStatus.Draft || status == ContractStatus.InApproval || status == ContractStatus.Approved;

        var contract = new Contract
        {
            Id = SeedIds.For("contract:" + key),
            ContractNo = ContractCommandService.FormatContractNo(year, last + 1),
            Title = title,
            TypeId = SeedIds.ContractType(type),
            SupplierId = supplierNo is null ? (Guid?)null : SeedIds.Supplier(supplierNo),
            CounterpartyName = counterparty,
            EntityId = SeedIds.Entity(entity),
            DepartmentId = SeedIds.Department(department),
            OwnerId = SeedIds.User(owner),
            DelegateId = delegateKey is null ? (Guid?)null : SeedIds.User(delegateKey),
            ResponsibleId = responsibleKey is null ? (Guid?)null : SeedIds.User(responsibleKey),
            ReminderRecipient = responsibleKey is null ? ReminderRecipient.Owner : ReminderRecipient.Responsible,
            StartDate = start,
            EndDate = endInDays is null ? (DateOnly?)null : today.AddDays(endInDays.Value),
            NoticePeriodDays = noticeDays,
            AutoRenew = autoRenew,
            RenewalMonths = renewalMonths ?? (autoRenew ? 12 : (int?)null),
            AnnualValue = annualValue,
            TotalValue = totalValue,
            Currency = currency,
            PaymentTerms = "Net 30 days",
            PriceAdjustment = priceAdjustment,
            GoverningLaw = governingLaw,
            LiabilityCap = liabilityCap,
            Exclusivity = exclusivity,
            ChangeOfControl = changeOfControl,
            Summary = title + " with " + counterparty + " for " + entity + ". Demo data.",
            Status = status,
            SignedDate = notSigned ? (DateOnly?)null : start.AddDays(-14),
            SigningMethod = notSigned ? null : "Manual",
            RenewalDecision = decision,
            RenewalDecidedById = decision is null ? (Guid?)null : SeedIds.User(owner),
            RenewalDecidedAt = decision is null ? (DateTime?)null : now.AddDays(-10),
            RenewalComment = decision is null ? null : "Recorded in demo data.",
            CreatedById = SeedIds.User(owner),
            CreatedAt = created,
            UpdatedAt = created
        };
        _db.Contracts.Add(contract);
        _contracts[key] = contract;
        return contract;
    }

    private void AddObligation(string contractKey, string description, int dueInDays, int? frequencyMonths, string responsible, bool done = false)
    {
        _db.Obligations.Add(new Obligation
        {
            Id = Guid.NewGuid(),
            ContractId = _contracts[contractKey].Id,
            Description = description,
            DueDate = _clock.Today.AddDays(dueInDays),
            FrequencyMonths = frequencyMonths,
            ResponsibleId = SeedIds.User(responsible),
            Status = done ? ObligationStatus.Done : ObligationStatus.Open,
            LastCompletedAt = done ? _clock.UtcNow.AddDays(-20) : (DateTime?)null
        });
    }

    private void AddLink(string fromKey, string toKey, LinkKind kind)
    {
        _db.ContractLinks.Add(new ContractLink
        {
            Id = Guid.NewGuid(),
            FromId = _contracts[fromKey].Id,
            ToId = _contracts[toKey].Id,
            Kind = kind
        });
    }

    private async Task SeedDemoContractsAsync(CancellationToken cancellationToken)
    {
        if (await _db.Contracts.AnyAsync(cancellationToken))
        {
            return;
        }
        _contracts.Clear();
        _numbers.Clear();

        const string groupEntity = "DAFA Group A/S";
        const string dafa = "DAFA A/S";
        const string building = "DAFA Building Solutions A/S";
        const string sverige = "DAFA Sverige AB";
        const string deutschland = "DAFA Deutschland GmbH";
        const string tianjin = "DAFA Sealing Technology (Tianjin) Co., Ltd.";
        const string us = "DAFA US Inc.";
        const string polska = "DAFA Polska sp. z o.o.";
        const string italia = "DAFA Italia S.r.l.";
        const string logistics = "Supply chain and logistics";
        const string facilities = "Maintenance and facilities";
        const string rd = "R&D and engineering";

        // Direct materials
        AddContract("epdm", "Supply agreement, EPDM compounds", "Nordic Polymer Compounds AB", SeedNames.TypeDirect, dafa, "Purchasing", "jonas",
            30, 150, 90, true, 14_200_000m, "DKK", supplierNo: "S-10010", priceAdjustment: "Index-linked (net price index)");
        AddContract("pefoam", "Supply agreement, PE foam", "Rhein Schaumstoff GmbH", SeedNames.TypeDirect, dafa, "Purchasing", "jonas",
            22, 200, 180, true, 1_100_000m, "EUR", supplierNo: "S-10020", governingLaw: "German law");
        AddContract("tapes", "Frame agreement, adhesive tapes", "Jutland Adhesive Tapes A/S", SeedNames.TypeDirect, building, "Purchasing", "jonas",
            14, 400, 90, false, 4_600_000m, "DKK", supplierNo: "S-10030");
        AddContract("silicone", "Supply agreement, silicone raw materials", "Alpen Silikon Chemie AG", SeedNames.TypeDirect, dafa, "Purchasing", "jonas",
            34, 95, 90, true, 2_400_000m, "EUR", supplierNo: "S-10040", changeOfControl: true, governingLaw: "German law",
            priceAdjustment: "Fixed increase of 3% per year");
        AddContract("profiles", "Supply agreement, rubber profiles", "Baltic Rubber Profiles sp. z o.o.", SeedNames.TypeDirect, polska, "Purchasing", "jonas",
            10, 520, 120, true, 3_900_000m, "PLN", supplierNo: "S-10050", governingLaw: "Polish law");
        AddContract("tianjin-seal", "Supply agreement, sealing materials", "Tianjin Haihe Sealing Materials Co., Ltd.", SeedNames.TypeDirect, tianjin, "Production", "henrik",
            18, 260, 60, true, 5_200_000m, "CNY", supplierNo: "S-10060", governingLaw: "Chinese law");
        AddContract("gaskets-it", "Supply agreement, gaskets", "Lombardia Guarnizioni S.r.l.", SeedNames.TypeDirect, italia, "Purchasing", "jonas",
            8, 640, 90, false, 380_000m, "EUR", supplierNo: "S-10070", governingLaw: "Italian law");
        AddContract("gaskets-us", "Supply agreement, gaskets for US distribution", "Midwest Gasket Supply Inc.", SeedNames.TypeDirect, us, "Purchasing", "jonas",
            26, 75, 60, true, 900_000m, "USD", supplierNo: "S-10080", governingLaw: "New York law", liabilityCap: "6 months' fees");

        // Indirect and services
        AddContract("packaging", "Packaging materials", "Vestkyst Emballage A/S", SeedNames.TypeIndirect, dafa, "Purchasing", "jonas",
            12, 330, 90, true, 1_250_000m, "DKK", supplierNo: "S-10090");
        AddContract("staffing", "Temporary staffing services", "Østjysk Vikarservice A/S", SeedNames.TypeIndirect, dafa, "Purchasing", "jonas",
            16, 300, 60, true, 3_200_000m, "DKK", supplierNo: "S-20010", responsibleKey: "emma");
        AddContract("facility", "Facility service, Brabrand site", "Brabrand Ejendomsservice ApS", SeedNames.TypeIndirect, dafa, facilities, "peter",
            20, 110, 90, true, 860_000m, "DKK", supplierNo: "S-20020");
        AddContract("cleaning", "Cleaning services", "Aarhus Erhvervsrengøring A/S", SeedNames.TypeIndirect, building, facilities, "peter",
            9, 450, 90, true, 540_000m, "DKK", supplierNo: "S-20030");
        AddContract("canteen", "Canteen services", "Kattegat Kantineservice ApS", SeedNames.TypeIndirect, dafa, "HR", "lars",
            6, 560, 90, true, 1_050_000m, "DKK", supplierNo: "S-20050");
        AddContract("legal", "Legal services framework", "Sønderby & Krag Advokater P/S", SeedNames.TypeIndirect, groupEntity, "Management", "lars",
            18, null, 30, false, 600_000m, "DKK", supplierNo: "S-80010");
        AddContract("waste", "Waste handling agreement", "Østjysk Miljøservice A/S", SeedNames.TypeIndirect, building, "HSE", "jonas",
            15, 420, 90, true, 210_000m, "DKK", changeOfControl: null);

        // Logistics
        AddContract("freight-nordic", "Road freight, Nordics", "Nordfragt Transport A/S", SeedNames.TypeLogistics, dafa, logistics, "jonas",
            24, 130, 90, true, 6_800_000m, "DKK", supplierNo: "S-30010", priceAdjustment: "Index-linked (fuel and wage index)");
        AddContract("freight-de", "Freight forwarding, Germany", "Hansa Spedition GmbH", SeedNames.TypeLogistics, deutschland, logistics, "jonas",
            15, 380, 90, true, 480_000m, "EUR", supplierNo: "S-30020", governingLaw: "German law");
        AddContract("warehouse", "External warehouse", "Øresund Lagerhotel A/S", SeedNames.TypeLogistics, dafa, logistics, "jonas",
            40, 30, 180, false, 2_100_000m, "DKK", supplierNo: "S-30030", decision: RenewalDecision.Renegotiate);

        // IT and SaaS
        AddContract("erp", "ERP licence and support", "Fjordbyte ERP A/S", SeedNames.TypeIt, groupEntity, "IT", "peter",
            36, 185, 90, true, 2_750_000m, "DKK", supplierNo: "S-40010", changeOfControl: true);
        AddContract("hosting", "Hosting and operations", "Cloudhaven Hosting ApS", SeedNames.TypeIt, groupEntity, "IT", "peter",
            20, 100, 90, true, 1_480_000m, "DKK", supplierNo: "S-40020");
        AddContract("security", "Managed security services", "Securon Cyber Defence AB", SeedNames.TypeIt, groupEntity, "IT", "peter",
            11, 420, 90, true, 890_000m, "DKK", supplierNo: "S-40030", governingLaw: "Swedish law");
        AddContract("planning", "Production planning SaaS", "Planwise Software B.V.", SeedNames.TypeIt, dafa, "IT", "peter",
            7, 600, 30, true, 96_000m, "EUR", supplierNo: "S-40040", delegateKey: "mette", governingLaw: "Dutch law",
            liabilityCap: "6 months' fees", renewalMonths: 24);

        // Lease and property
        AddContract("forklifts", "Forklift leasing", "Skandia Truck Leasing AB", SeedNames.TypeLease, sverige, logistics, "jonas",
            28, 240, 180, false, 420_000m, "SEK", supplierNo: "S-20040", governingLaw: "Swedish law");
        AddContract("hq-lease", "Office lease, Brabrand headquarters", "Jyske Erhvervsejendomme P/S", SeedNames.TypeLease, groupEntity, "Management", "henrik",
            60, 1200, 365, false, 5_400_000m, "DKK", supplierNo: "S-50010", changeOfControl: true, priceAdjustment: "Index-linked (net price index)");
        AddContract("tianjin-lease", "Factory lease, Tianjin", "Tianjin Binhai Industrial Park Co., Ltd.", SeedNames.TypeLease, tianjin, "Management", "henrik",
            48, 700, 180, false, 3_100_000m, "CNY", supplierNo: "S-50020", governingLaw: "Chinese law");
        AddContract("printers", "Printer and copier lease", "Kontorteknik Danmark A/S", SeedNames.TypeLease, dafa, "IT", "peter",
            46, 45, 90, false, 130_000m, "DKK", status: ContractStatus.NoticeGiven, decision: RenewalDecision.GiveNotice);

        // Finance and insurance
        AddContract("insurance", "Property and liability insurance", "Nordlys Forsikring A/S", SeedNames.TypeFinance, groupEntity, "Finance", "lars",
            11, 50, 30, true, 1_900_000m, "DKK", supplierNo: "S-60010");
        AddContract("credit", "Credit facility", "Limfjord Erhvervsbank A/S", SeedNames.TypeFinance, groupEntity, "Finance", "lars",
            30, 880, 90, false, null, "DKK", supplierNo: "S-60020", changeOfControl: true, totalValue: 150_000_000m, liabilityCap: null);
        AddContract("cashpool", "Cash pool agreement", "Limfjord Erhvervsbank A/S", SeedNames.TypeFinance, groupEntity, "Finance", "lars",
            30, null, 90, false, 250_000m, "DKK", supplierNo: "S-60020", liabilityCap: null);

        // R&D, NDA, corporate, other
        AddContract("testing", "Material testing framework", "Institut for Materialeprøvning", SeedNames.TypeRd, dafa, rd, "henrik",
            13, 350, 60, true, 310_000m, "DKK", supplierNo: "S-70010");
        AddContract("jointdev", "Joint development, low-emission foam", "PolyLab Engineering GmbH", SeedNames.TypeRd, dafa, rd, "henrik",
            5, 900, 90, false, 1_200_000m, "EUR", supplierNo: "S-70020", exclusivity: "Exclusive within building insulation");
        AddContract("nda-polylab", "Mutual NDA, PolyLab Engineering", "PolyLab Engineering GmbH", SeedNames.TypeNda, dafa, rd, "henrik",
            6, 1400, 0, false, 0m, "DKK", supplierNo: "S-70020", liabilityCap: null);
        AddContract("nda-nordic", "Mutual NDA, Nordic Polymer Compounds", "Nordic Polymer Compounds AB", SeedNames.TypeNda, dafa, "Purchasing", "jonas",
            32, 60, 0, false, 0m, "DKK", supplierNo: "S-10010", liabilityCap: null);
        AddContract("shareholders", "Shareholders' agreement", "Majority shareholder and management investors", SeedNames.TypeCorporate, groupEntity, "Management", "henrik",
            50, null, 0, false, 0m, "DKK", changeOfControl: true, liabilityCap: null);
        AddContract("calibration", "Calibration services", "Midtjysk Kalibrering ApS", SeedNames.TypeOther, dafa, "Quality", "lars",
            9, 270, null, false, null, "DKK", governingLaw: null, liabilityCap: null);
        AddContract("marketstudy", "Market study, Nordic building sector", "Nordic Insight Analyse ApS", SeedNames.TypeOther, groupEntity, "Marketing", "henrik",
            20, -120, 30, false, 180_000m, "DKK", status: ContractStatus.Expired);
        AddContract("telephony", "Telephony services", "Telenet Erhverv A/S", SeedNames.TypeIt, dafa, "IT", "peter",
            50, -60, 90, false, 240_000m, "DKK", status: ContractStatus.Terminated, decision: RenewalDecision.GiveNotice);
        AddContract("uniforms", "Workwear rental", "Dansk Arbejdstøj Service A/S", SeedNames.TypeIndirect, dafa, "Production", "jonas",
            25, -20, 60, false, 95_000m, "DKK", status: ContractStatus.Expired);

        // Drafts and approvals (future start dates)
        AddContract("granulate", "Supply agreement, recycled rubber granulate", "Baltic Rubber Profiles sp. z o.o.", SeedNames.TypeDirect, dafa, "Purchasing", "jonas",
            -1, 700, 90, true, 2_800_000m, "DKK", status: ContractStatus.Draft, supplierNo: "S-10050");
        AddContract("webshop", "E-commerce platform subscription", "Planwise Software B.V.", SeedNames.TypeIt, building, "IT", "peter",
            -1, 365, 30, true, 45_000m, "EUR", status: ContractStatus.Draft, supplierNo: "S-40040");
        AddContract("draft-nda", "Mutual NDA, Lombardia Guarnizioni", "Lombardia Guarnizioni S.r.l.", SeedNames.TypeNda, italia, "Purchasing", "jonas",
            -1, 1095, 0, false, 0m, "EUR", status: ContractStatus.Draft, supplierNo: "S-10070", liabilityCap: null);
        AddContract("freight-se", "Road freight, Sweden", "Nordfragt Transport A/S", SeedNames.TypeLogistics, sverige, logistics, "jonas",
            -1, 730, 90, true, 2_300_000m, "SEK", status: ContractStatus.Draft, supplierNo: "S-30010");
        AddContract("tpe", "Supply agreement, TPE compounds", "Nordic Polymer Compounds AB", SeedNames.TypeDirect, dafa, "Purchasing", "jonas",
            -1, 1095, 180, true, 9_500_000m, "DKK", status: ContractStatus.Draft, supplierNo: "S-10010");
        AddContract("cyber", "Cyber insurance", "Nordlys Forsikring A/S", SeedNames.TypeFinance, groupEntity, "Finance", "peter",
            -1, 365, 30, true, 450_000m, "DKK", status: ContractStatus.Draft, supplierNo: "S-60010");
        AddContract("nda-hansa", "Mutual NDA, Hansa Spedition", "Hansa Spedition GmbH", SeedNames.TypeNda, deutschland, logistics, "jonas",
            -1, 1095, 0, false, 0m, "EUR", status: ContractStatus.Draft, supplierNo: "S-30020", liabilityCap: null);
        AddContract("compressors", "Service agreement, compressed air systems", "Jysk Trykluft Service A/S", SeedNames.TypeIndirect, dafa, facilities, "peter",
            -1, 730, 90, true, 320_000m, "DKK", status: ContractStatus.Draft);

        // Obligations
        AddObligation("epdm", "Hold the annual price review meeting", 25, 12, "jonas");
        AddObligation("staffing", "Send the quarterly headcount report to the supplier", 10, 3, "emma");
        AddObligation("erp", "Report user counts for the licence true-up", 40, 12, "peter");
        AddObligation("hosting", "Review the monthly SLA report", -3, 1, "peter");
        AddObligation("hq-lease", "Check the index adjustment of the rent", 80, 12, "lars");
        AddObligation("insurance", "Send updated asset values to the insurer", 12, null, "lars");
        AddObligation("credit", "Deliver the covenant compliance certificate", 18, 3, "lars");
        AddObligation("jointdev", "Deliver the milestone 2 report", 55, null, "henrik");
        AddObligation("freight-nordic", "Settle the annual volume bonus", 130, 12, "jonas");
        AddObligation("tapes", "Deliver the rolling 12-month forecast", -25, null, "jonas", done: true);

        // Links
        AddLink("nda-nordic", "epdm", LinkKind.Related);
        AddLink("nda-polylab", "jointdev", LinkKind.Related);
        AddLink("cashpool", "credit", LinkKind.Related);
        AddLink("tpe", "epdm", LinkKind.Framework);
        AddLink("cleaning", "facility", LinkKind.Related);

        // A small placeholder file per contract that has been signed
        var users = await _db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);
        foreach (var contract in _contracts.Values)
        {
            var ownerName = users.TryGetValue(contract.OwnerId, out var name) ? name : "System";
            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                At = contract.CreatedAt,
                UserId = contract.OwnerId,
                UserName = ownerName,
                Action = "contract.created",
                ContractId = contract.Id,
                Text = "Created draft " + contract.ContractNo
            });
            if (contract.SignedDate is null)
            {
                continue;
            }
            var text = new StringBuilder();
            text.AppendLine("DEMO DOCUMENT - not a real contract");
            text.AppendLine(contract.Title);
            text.AppendLine("Contract no: " + contract.ContractNo);
            text.AppendLine("Counterparty: " + contract.CounterpartyName);
            text.AppendLine("This file stands in for the signed agreement in the test environment.");
            await _documents.AddAsync(contract, contract.Title + ".txt", DocumentKind.Main, Encoding.UTF8.GetBytes(text.ToString()), contract.OwnerId, cancellationToken);
            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                At = contract.SignedDate.Value.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
                UserId = contract.OwnerId,
                UserName = ownerName,
                Action = "contract.signed",
                ContractId = contract.Id,
                Text = "Marked as signed (Manual); contract is active"
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        await SeedDemoApprovalsAsync(cancellationToken);
    }

    /// <summary>Approvals in different states, created through the real approval engine.</summary>
    private async Task SeedDemoApprovalsAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        // Waiting in stage 1 (department head not assigned -> administrator), stage 2 entity manager.
        await StartApprovalAsync("freight-se", cancellationToken);

        // Two parallel approvers in stage 1; the department head slot has approved, Legal is still open.
        // Jonas is head of Purchasing but owns this contract, so the engine gave his slot to the
        // entity manager (separation of duties).
        var tpeSteps = await StartApprovalAsync("tpe", cancellationToken);
        var legalId = SeedIds.User("sofie");
        var approvedStep = tpeSteps.FirstOrDefault(s => s.StageOrder == 1 && s.ApproverId != legalId);
        if (approvedStep is not null)
        {
            approvedStep.Status = ApprovalStepStatus.Approved;
            approvedStep.DecidedAt = now.AddDays(-1);
            approvedStep.Comment = "Commercial terms agreed with the supplier.";
        }

        await StartApprovalAsync("cyber", cancellationToken);
        await StartApprovalAsync("nda-hansa", cancellationToken);

        // Rejected earlier: back in draft with the comment on the step.
        var webshop = _contracts["webshop"];
        var rejectedInstance = await _approvals.StartAsync(webshop, webshop.OwnerId, cancellationToken);
        var rejectedSteps = _db.ApprovalSteps.Local.Where(s => s.InstanceId == rejectedInstance.Id).ToList();
        foreach (var step in rejectedSteps)
        {
            if (step.StageOrder == 1 && rejectedInstance.Status == ApprovalStatus.Pending)
            {
                step.Status = ApprovalStepStatus.Rejected;
                step.DecidedAt = now.AddDays(-2);
                step.Comment = "The data processing agreement is missing. Add it and submit again.";
                rejectedInstance.Status = ApprovalStatus.Rejected;
                rejectedInstance.CompletedAt = now.AddDays(-2);
            }
            else if (step.Status == ApprovalStepStatus.Pending)
            {
                step.Status = ApprovalStepStatus.Waiting;
            }
        }
        webshop.Status = ContractStatus.Draft;

        // Fully approved, waiting for the owner to mark it as signed.
        var compressors = _contracts["compressors"];
        var approvedInstance = await _approvals.StartAsync(compressors, compressors.OwnerId, cancellationToken);
        var approvedSteps = _db.ApprovalSteps.Local.Where(s => s.InstanceId == approvedInstance.Id).ToList();
        foreach (var step in approvedSteps)
        {
            step.Status = ApprovalStepStatus.Approved;
            step.ActivatedAt ??= now.AddDays(-4);
            step.DecidedAt = now.AddDays(-3);
        }
        approvedInstance.Status = ApprovalStatus.Approved;
        approvedInstance.CompletedAt = now.AddDays(-3);
        compressors.Status = ContractStatus.Approved;
        var directory = await ApproverDirectory.LoadAsync(_db, cancellationToken);
        var log = ApprovalService.BuildApprovalLog(compressors, approvedInstance, approvedSteps, directory);
        await _documents.AddAsync(compressors, "Approval log " + compressors.ContractNo + ".txt", DocumentKind.ApprovalLog,
            Encoding.UTF8.GetBytes(log), null, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<ApprovalStep>> StartApprovalAsync(string contractKey, CancellationToken cancellationToken)
    {
        var contract = _contracts[contractKey];
        var instance = await _approvals.StartAsync(contract, contract.OwnerId, cancellationToken);
        contract.Status = ContractStatus.InApproval;
        return _db.ApprovalSteps.Local.Where(s => s.InstanceId == instance.Id).ToList();
    }
}
