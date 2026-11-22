using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Dafa.Contracts.Core.Services;
using Dafa.Contracts.Infrastructure.Data;
using Dafa.Contracts.Infrastructure.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    public async Task<StoredDocument> SaveAsync(DocumentLocation location, string fileName, int version, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var key = location.ContractId.ToString("N") + "/v" + version + "_" + fileName;
        Files[key] = buffer.ToArray();
        return new StoredDocument(key, buffer.Length);
    }

    public Task<Stream> OpenReadAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        Stream stream = new MemoryStream(Files[storeKey], false);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        Files.Remove(storeKey);
        return Task.CompletedTask;
    }

    public Task<string?> GetFolderUrlAsync(DocumentLocation location, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
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

/// <summary>The application services wired by hand for one user, on a fresh DbContext.</summary>
public sealed class Actor
{
    public Actor(TestWorld world, Guid? userId)
    {
        Db = world.NewDb();
        string? name = null;
        if (userId is not null)
        {
            name = Db.Users.AsNoTracking().Where(u => u.Id == userId.Value).Select(u => u.Name).FirstOrDefault();
        }
        var user = new FakeCurrentUser(Db, userId, name);
        Access = new ContractAccessService(Db, user);
        var audit = new AuditService(Db, user, world.Clock);
        var writer = new DocumentWriter(Db, world.Store, world.Clock);
        var notifier = new OutboxNotifier(Db, world.Clock);
        Approvals = new ApprovalService(Db, user, world.Clock, notifier, audit, writer);
        Queries = new ContractQueryService(Db, user, Access, world.Clock, world.Store, writer, audit);
        Commands = new ContractCommandService(Db, user, Access, world.Clock, audit, Approvals, writer);
        Jobs = new JobService(Db, world.Clock, notifier, new EmptySupplierSource(), new SeededDirectoryService(),
            Approvals, audit, NullLogger<JobService>.Instance);
    }

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
