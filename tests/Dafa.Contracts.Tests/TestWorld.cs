using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Services;
using Dafa.Contracts.Infrastructure.Data;
using Dafa.Contracts.Infrastructure.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dafa.Contracts.Tests;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTime utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTime UtcNow { get; set; }
    public DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public void AdvanceDays(int days)
    {
        UtcNow = UtcNow.AddDays(days);
    }
}

public sealed class FakeCurrentUser : ICurrentUser
{
    private readonly IAppDbContext _db;

    public FakeCurrentUser(IAppDbContext db, Guid? userId, string? userName)
    {
        _db = db;
        UserId = userId;
        UserName = userName;
    }

    public Guid? UserId { get; }
    public string? UserName { get; }

    public async Task<AccessProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        if (UserId is null)
        {
            throw new UnauthorizedException("No user.");
        }
        var profile = await AccessProfileLoader.LoadAsync(_db, UserId.Value, cancellationToken);
        if (profile is null)
        {
            throw new UnauthorizedException("Unknown user.");
        }
        return profile;
    }
}

public sealed class MemoryDocumentStore : IDocumentStore
{
    public Dictionary<string, byte[]> Files { get; } = new();

    /// <summary>Release 4: when set, every call fails with it (the store is down, or not configured).</summary>
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }
    public List<DocumentLocation> SavedLocations { get; } = new();

    private void Touch()
    {
        Calls++;
        if (Failure is not null)
        {
            throw Failure;
        }
    }

    public async Task<StoredDocument> SaveAsync(DocumentLocation location, string fileName, int version, Stream content, CancellationToken cancellationToken = default)
    {
        Touch();
        SavedLocations.Add(location);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var key = location.ContractId.ToString("N") + "/v" + version + "_" + fileName;
        Files[key] = buffer.ToArray();
        return new StoredDocument(key, buffer.Length);
    }

    public Task<Stream> OpenReadAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        Touch();
        if (!Files.TryGetValue(storeKey, out var bytes))
        {
            throw new FileNotFoundException("The stored file is missing.", storeKey);
        }
        Stream stream = new MemoryStream(bytes, false);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        Touch();
        Files.Remove(storeKey);
        return Task.CompletedTask;
    }

    public Task<string?> GetFolderUrlAsync(DocumentLocation location, CancellationToken cancellationToken = default)
    {
        Touch();
        return Task.FromResult<string?>(null);
    }

    /// <summary>The health check: fails the same way as every other call when the store is "down".</summary>
    public Task<StoreProbe> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            throw Failure;
        }
        return Task.FromResult(new StoreProbe(true, null));
    }
}

/// <summary>Release 4: a link probe with canned answers, standing in for Microsoft Graph.</summary>
public sealed class FakeOrgLinkProbe : IOrgLinkProbe
{
    public Dictionary<string, SiteInfo> Sites { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Libraries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? Failure { get; set; }

    public Task<SiteInfo?> ResolveSiteAsync(string siteUrl, CancellationToken cancellationToken = default)
    {
        if (Failure is not null) throw Failure;
        return Task.FromResult(Sites.TryGetValue(siteUrl, out var site) ? site : null);
    }

    public Task<IReadOnlyList<string>> GetLibraryNamesAsync(string siteId, CancellationToken cancellationToken = default)
    {
        if (Failure is not null) throw Failure;
        IReadOnlyList<string> names = Libraries.TryGetValue(siteId, out var list) ? list : new List<string>();
        return Task.FromResult(names);
    }

    public Task<string?> GetGroupNameAsync(string groupId, CancellationToken cancellationToken = default)
    {
        if (Failure is not null) throw Failure;
        return Task.FromResult(Groups.TryGetValue(groupId, out var name) ? name : null);
    }
}

/// <summary>Data room zips in memory.</summary>
public sealed class MemoryExportStorage : IExportStorage
{
    public Dictionary<string, byte[]> Files { get; } = new();

    private sealed class CapturingStream : MemoryStream
    {
        private readonly Action<byte[]> _onClose;
        private bool _closed;

        public CapturingStream(Action<byte[]> onClose)
        {
            _onClose = onClose;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_closed)
            {
                _closed = true;
                _onClose(ToArray());
            }
            base.Dispose(disposing);
        }
    }

    public Task<Stream> CreateAsync(string key, CancellationToken cancellationToken = default)
    {
        Stream stream = new CapturingStream(bytes => Files[key] = bytes);
        return Task.FromResult(stream);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        Stream? stream = Files.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, false) : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        Files.Remove(key);
        return Task.CompletedTask;
    }
}

public sealed class EmptySupplierSource : ISupplierSource
{
    public Task<IReadOnlyList<SupplierRecord>> GetSuppliersAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SupplierRecord> none = new List<SupplierRecord>();
        return Task.FromResult(none);
    }
}

/// <summary>
/// A clock that runs an action the first time it is read after being armed. Lets a test slip
/// another user's request in between the moment a service has loaded its rows and its save.
/// </summary>
public sealed class InterleavingClock : IClock
{
    private readonly IClock _inner;
    private Action? _onNextRead;

    public InterleavingClock(IClock inner)
    {
        _inner = inner;
    }

    public void Arm(Action onNextRead)
    {
        _onNextRead = onNextRead;
    }

    public DateTime UtcNow
    {
        get
        {
            var action = _onNextRead;
            if (action is not null)
            {
                _onNextRead = null;
                action();
            }
            return _inner.UtcNow;
        }
    }

    public DateOnly Today => _inner.Today;
}

/// <summary>A notifier whose deliveries can be made to fail, like Graph mail during an outage.</summary>
public sealed class SwitchableNotifier : INotifier
{
    public bool Fail { get; set; }
    public int Attempts { get; private set; }
    public int Delivered { get; private set; }

    public Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        Attempts++;
        if (Fail)
        {
            return Task.FromResult(false);
        }
        Delivered++;
        return Task.FromResult(true);
    }
}

/// <summary>The application services wired by hand for one user, on a fresh DbContext.</summary>
public sealed class Actor
{
    public Actor(TestWorld world, Guid? userId) : this(world, userId, null, null)
    {
    }

    public Actor(TestWorld world, Guid? userId, IClock? clockOverride, INotifier? notifierOverride)
    {
        IClock clock = clockOverride ?? world.Clock;
        Db = world.NewDb();
        string? name = null;
        if (userId is not null)
        {
            name = Db.Users.AsNoTracking().Where(u => u.Id == userId.Value).Select(u => u.Name).FirstOrDefault();
        }
        var user = new FakeCurrentUser(Db, userId, name);
        Access = new ContractAccessService(Db, user);
        var audit = new AuditService(Db, user, clock);
        var writer = new DocumentWriter(Db, world.Store, clock);
        INotifier notifier = notifierOverride ?? new OutboxNotifier(Db, clock);
        Approvals = new ApprovalService(Db, user, clock, notifier, audit, writer);
        Queries = new ContractQueryService(Db, user, Access, clock, world.Store, writer, audit);
        Commands = new ContractCommandService(Db, user, Access, clock, audit, Approvals, writer);
        Documents = new DocumentService(Db, user, Access, world.Store, writer, audit);
        Jobs = new JobService(Db, clock, notifier, new EmptySupplierSource(), new SeededDirectoryService(),
            Approvals, audit, NullLogger<JobService>.Instance);

        // Release 3 services, wired the same way the host wires them.
        Audit = audit;
        Dispatcher = new NotificationDispatcher(Db, new OutboxNotifier(Db, clock), clock, user);
        Indexer = new DocumentTextIndexer(Db, world.Store, new DocumentTextExtractor(), clock);
        IndexedDocuments = new DocumentService(Db, user, Access, world.Store, writer, audit, Indexer);
        CustomFields = new CustomFieldService(Db, audit, user);
        Collaboration = new CollaborationService(Db, user, Access, clock, audit, Queries, Dispatcher);
        Search = new SearchService(Db, user, Access, clock, Queries);
        Analytics = new AnalyticsService(Db, user, Access, clock);
        Versions = new VersionService(Db, Access);
        DataRoom = new DataRoomService(Db, user, Access, clock, world.Store, world.Exports, audit);
        AdminDirectory = new AdminDirectoryService(Db, user, clock, audit);
        Counterparties = new CounterpartyService(Db, clock, audit);
        Imports = new ImportService(Db, user, clock, audit, new TabularFileReader(), Dispatcher);
        Sync = new SyncService(Db, clock, audit, new DemoSyncSourceReader(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()), Imports, Dispatcher);
        Settings = new SettingsService(Db, clock, audit);
        Personal = new PersonalService(Db, user, clock, audit, world.Modes);

        // Release 4 services, wired the same way the host wires them (Simulated e-signature provider).
        Providers = new SignatureProviderRegistry(new ISignatureProvider[] { new SimulatedSignatureProvider(world.Configuration) });
        Signatures = new SignatureService(Db, user, Access, clock, audit, writer, world.Store, Dispatcher, Providers);
        Pickers = new PickerService(Db, user, clock, audit);
        Distinct = new DistinctValuesService(Db, user, Access, clock);
        Templates = new TemplateCatalogService(Db, user, audit, new DocumentTextExtractor());
        Branding = new BrandingService(Db, user, clock, audit);
        OrgLinks = new OrgLinkService(Db, clock, audit, world.Probe, world.Modes);
        Storage = new StorageService(Db, world.Store, clock, audit, user, world.Modes, world.Health);
        Admin = new AdminService(Db, clock, audit, new OpenXmlTemplateEngine());
    }

    public SignatureProviderRegistry Providers { get; }
    public SignatureService Signatures { get; }
    public PickerService Pickers { get; }
    public DistinctValuesService Distinct { get; }
    public TemplateCatalogService Templates { get; }
    public BrandingService Branding { get; }
    public OrgLinkService OrgLinks { get; }
    public StorageService Storage { get; }
    public AdminService Admin { get; }

    public AuditService Audit { get; }
    public NotificationDispatcher Dispatcher { get; }
    public DocumentTextIndexer Indexer { get; }
    /// <summary>A DocumentService that extracts text at upload (as in the host).</summary>
    public DocumentService IndexedDocuments { get; }
    public CustomFieldService CustomFields { get; }
    public CollaborationService Collaboration { get; }
    public SearchService Search { get; }
    public AnalyticsService Analytics { get; }
    public VersionService Versions { get; }
    public DataRoomService DataRoom { get; }
    public AdminDirectoryService AdminDirectory { get; }
    public CounterpartyService Counterparties { get; }
    public ImportService Imports { get; }
    public SyncService Sync { get; }
    public SettingsService Settings { get; }
    public PersonalService Personal { get; }

    public DocumentService Documents { get; }

    public AppDbContext Db { get; }
    public ContractAccessService Access { get; }
    public ApprovalService Approvals { get; }
    public ContractQueryService Queries { get; }
    public ContractCommandService Commands { get; }
    public JobService Jobs { get; }
}

/// <summary>A small organisation in a private in-memory Sqlite database.</summary>
public sealed class TestWorld : IDisposable
{
    private readonly SqliteConnection _connection;

    public FakeClock Clock { get; } = new(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc));
    public MemoryDocumentStore Store { get; } = new();
    public MemoryExportStorage Exports { get; } = new();

    // Release 4
    /// <summary>Test mode by default (local files, seeded directory); a test may switch to SharePoint / Graph.</summary>
    public RuntimeModes Modes { get; } = new();
    public FakeOrgLinkProbe Probe { get; } = new();
    public StorageHealthState Health { get; } = new();
    /// <summary>Auth:Mode=Dev, so the Simulated e-signature provider is allowed.</summary>
    public Microsoft.Extensions.Configuration.IConfiguration Configuration { get; } =
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Mode"] = "Dev" })
            .Build();
    private Guid? _defaultSupplier;

    /// <summary>
    /// An active counterparty for contract inputs (the counterparty is always a master record).
    /// Created on first use, so tests that count counterparties start from none.
    /// </summary>
    public Guid DefaultSupplier
    {
        get
        {
            _defaultSupplier ??= AddSupplier("Test Supplier A/S");
            return _defaultSupplier.Value;
        }
    }

    public Guid EntityA { get; } = Guid.NewGuid();
    public Guid EntityB { get; } = Guid.NewGuid();
    public Guid Purchasing { get; } = Guid.NewGuid();
    public Guid It { get; } = Guid.NewGuid();
    public Guid TypeSupplier { get; } = Guid.NewGuid();
    public Guid TypeNda { get; } = Guid.NewGuid();

    public Guid Admin { get; private set; }
    public Guid Cfo { get; private set; }
    public Guid Ceo { get; private set; }
    public Guid Legal { get; private set; }
    public Guid PurchasingHead { get; private set; }
    public Guid PurchasingMember { get; private set; }
    public Guid ItHead { get; private set; }
    public Guid EntityAManager { get; private set; }
    public Guid Owner { get; private set; }
    public Guid Deputy { get; private set; }
    public Guid Outsider { get; private set; }

    public TestWorld()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var db = NewDb();
        db.Database.EnsureCreated();

        db.LegalEntities.Add(new LegalEntity { Id = EntityA, Name = "Entity A", SortOrder = 1 });
        db.LegalEntities.Add(new LegalEntity { Id = EntityB, Name = "Entity B", SortOrder = 2 });
        db.Departments.Add(new Department { Id = Purchasing, Name = "Purchasing", SortOrder = 1 });
        db.Departments.Add(new Department { Id = It, Name = "IT", SortOrder = 2 });
        db.ContractTypes.Add(new ContractType { Id = TypeSupplier, Name = "Supplier", SortOrder = 1 });
        db.ContractTypes.Add(new ContractType { Id = TypeNda, Name = "NDA", SortOrder = 2 });
        db.ReminderSettings.Add(new ReminderSettings { Id = Guid.NewGuid(), NoticeDays = "120,60,30", EscalationDays = 14, ObligationDays = 14 });

        Admin = AddUser(db, "Admin User", AppRole.Reader | AppRole.Admin);
        Cfo = AddUser(db, "Cfo User", AppRole.Reader | AppRole.Approver, groupManagement: true, function: GroupFunction.GroupCFO);
        Ceo = AddUser(db, "Ceo User", AppRole.Reader | AppRole.Approver, groupManagement: true, function: GroupFunction.GroupCEO);
        Legal = AddUser(db, "Legal User", AppRole.Reader | AppRole.Approver, function: GroupFunction.Legal);
        PurchasingHead = AddUser(db, "Purchasing Head", AppRole.Reader | AppRole.ContractOwner | AppRole.Approver, departmentId: Purchasing, head: true);
        PurchasingMember = AddUser(db, "Purchasing Member", AppRole.Reader, departmentId: Purchasing);
        ItHead = AddUser(db, "It Head", AppRole.Reader | AppRole.ContractOwner | AppRole.Approver, departmentId: It, head: true);
        EntityAManager = AddUser(db, "Entity Manager", AppRole.Reader | AppRole.Approver, managedEntityId: EntityA);
        Owner = AddUser(db, "Owner User", AppRole.Reader | AppRole.ContractOwner);
        Deputy = AddUser(db, "Deputy User", AppRole.Reader | AppRole.Approver);
        Outsider = AddUser(db, "Outsider User", AppRole.Reader);

        db.SaveChanges();
    }

    public AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        return new AppDbContext(options);
    }

    public Actor As(Guid? userId) => new(this, userId);

    public Actor As(Guid? userId, IClock? clock, INotifier? notifier = null) => new(this, userId, clock, notifier);

    private static Guid AddUser(
        AppDbContext db, string name, AppRole roles, bool groupManagement = false, GroupFunction function = GroupFunction.None,
        Guid? departmentId = null, bool head = false, Guid? managedEntityId = null)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new AppUser
        {
            Id = id,
            Name = name,
            Email = name.Replace(" ", ".").ToLowerInvariant() + "@example.test",
            Initials = UserNames.Initials(name),
            Roles = roles,
            IsGroupManagement = groupManagement,
            GroupFunction = function,
            IsActive = true
        });
        if (departmentId is not null)
        {
            db.UserDepartments.Add(new UserDepartment { UserId = id, DepartmentId = departmentId.Value, IsHead = head });
        }
        if (managedEntityId is not null)
        {
            db.UserEntityManagements.Add(new UserEntityManagement { UserId = id, EntityId = managedEntityId.Value });
        }
        return id;
    }

    private int _sequence;

    /// <summary>Adds a contract in Entity A / Purchasing owned by Owner; 500,000 DKK; ends in 150 days with 90 days' notice.</summary>
    public Contract AddContract(Action<Contract>? customize = null)
    {
        _sequence++;
        var now = Clock.UtcNow;
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            ContractNo = ContractCommandService.FormatContractNo(2026, _sequence),
            Title = "Contract " + _sequence,
            TypeId = TypeSupplier,
            CounterpartyName = "Counterparty " + _sequence,
            EntityId = EntityA,
            DepartmentId = Purchasing,
            OwnerId = Owner,
            ReminderRecipient = ReminderRecipient.Owner,
            StartDate = Clock.Today.AddMonths(-12),
            EndDate = Clock.Today.AddDays(150),
            NoticePeriodDays = 90,
            AutoRenew = false,
            AnnualValue = 500_000m,
            Currency = "DKK",
            ChangeOfControl = false,
            SignedDate = Clock.Today.AddMonths(-12),
            SigningMethod = "Manual",
            Status = ContractStatus.Active,
            CreatedById = Owner,
            CreatedAt = now,
            UpdatedAt = now
        };
        customize?.Invoke(contract);
        using var db = NewDb();
        db.Contracts.Add(contract);
        db.SaveChanges();
        return contract;
    }

    /// <summary>Adds a counterparty master record and returns its id.</summary>
    public Guid AddSupplier(string name, bool isActive = true, string? vatNo = null)
    {
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = name, IsActive = isActive, VatNo = vatNo };
        using var db = NewDb();
        db.Suppliers.Add(supplier);
        db.SaveChanges();
        return supplier.Id;
    }

    /// <summary>Changes one user row (for example to grant the signing permission).</summary>
    public void UpdateUser(Guid userId, Action<AppUser> change)
    {
        using var db = NewDb();
        var user = db.Users.Single(u => u.Id == userId);
        change(user);
        db.SaveChanges();
    }

    public ApprovalRule AddRule(string name, decimal? min, decimal? max, Guid? typeId, int priority, params ApproverKind[][] stages)
    {
        var definitions = new List<ApprovalStageDefinition>();
        for (var i = 0; i < stages.Length; i++)
        {
            definitions.Add(new ApprovalStageDefinition
            {
                Order = i + 1,
                Approvers = stages[i].Select(k => new ApproverDefinition { Kind = k }).ToList()
            });
        }
        var rule = new ApprovalRule
        {
            Id = Guid.NewGuid(),
            Name = name,
            MinValue = min,
            MaxValue = max,
            TypeId = typeId,
            Priority = priority,
            IsActive = true,
            StagesJson = JsonDefaults.Write(definitions)
        };
        using var db = NewDb();
        db.ApprovalRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
