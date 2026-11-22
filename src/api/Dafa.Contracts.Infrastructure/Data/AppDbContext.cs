using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Dafa.Contracts.Infrastructure.Data;

/// <summary>
/// One model for Sqlite and SQL Server. Rules that keep it portable:
/// no navigation properties or cascades, DateTime (UTC) instead of DateTimeOffset, and no
/// server-side arithmetic, ordering or aggregation on decimal columns (done in memory).
/// </summary>
public class AppDbContext : DbContext, IAppDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<LegalEntity> LegalEntities => Set<LegalEntity>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<ContractType> ContractTypes => Set<ContractType>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserDepartment> UserDepartments => Set<UserDepartment>();
    public DbSet<UserEntityManagement> UserEntityManagements => Set<UserEntityManagement>();
    public DbSet<HolidayCover> HolidayCovers => Set<HolidayCover>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<ContractLink> ContractLinks => Set<ContractLink>();
    public DbSet<Obligation> Obligations => Set<Obligation>();
    public DbSet<ContractDocument> Documents => Set<ContractDocument>();
    public DbSet<ApprovalRule> ApprovalRules => Set<ApprovalRule>();
    public DbSet<ApprovalInstance> ApprovalInstances => Set<ApprovalInstance>();
    public DbSet<ApprovalStep> ApprovalSteps => Set<ApprovalStep>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<RetentionRule> RetentionRules => Set<RetentionRule>();
    public DbSet<ContractTemplate> Templates => Set<ContractTemplate>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ReminderSettings> ReminderSettings => Set<ReminderSettings>();
    public DbSet<PendingUpload> PendingUploads => Set<PendingUpload>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<LegalEntity>(e =>
        {
            e.ToTable("LegalEntities");
            e.Property(x => x.Name).HasMaxLength(200);
        });
        modelBuilder.Entity<Department>(e =>
        {
            e.ToTable("Departments");
            e.Property(x => x.Name).HasMaxLength(200);
        });
        modelBuilder.Entity<ContractType>(e =>
        {
            e.ToTable("ContractTypes");
            e.Property(x => x.Name).HasMaxLength(200);
        });
        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Initials).HasMaxLength(8);
            e.Property(x => x.ExternalId).HasMaxLength(100);
            e.HasIndex(x => x.Email);
        });
        modelBuilder.Entity<UserDepartment>(e =>
        {
            e.ToTable("UserDepartments");
            e.HasKey(x => new { x.UserId, x.DepartmentId });
        });
        modelBuilder.Entity<UserEntityManagement>(e =>
        {
            e.ToTable("UserEntityManagements");
            e.HasKey(x => new { x.UserId, x.EntityId });
        });
        modelBuilder.Entity<HolidayCover>(e =>
        {
            e.ToTable("HolidayCovers");
            e.HasIndex(x => x.UserId);
        });
        modelBuilder.Entity<Supplier>(e =>
        {
            e.ToTable("Suppliers");
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.ExternalId).HasMaxLength(100);
            e.HasIndex(x => x.ExternalId);
        });
        modelBuilder.Entity<Contract>(e =>
        {
            e.ToTable("Contracts");
            e.Property(x => x.ContractNo).HasMaxLength(32);
            e.HasIndex(x => x.ContractNo).IsUnique();
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.CounterpartyName).HasMaxLength(300);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.AnnualValue).HasPrecision(18, 2);
            e.Property(x => x.TotalValue).HasPrecision(18, 2);
            e.HasIndex(x => x.DepartmentId);
            e.HasIndex(x => x.EntityId);
            e.HasIndex(x => x.OwnerId);
        });
        modelBuilder.Entity<ContractLink>(e =>
        {
            e.ToTable("ContractLinks");
            e.HasIndex(x => x.FromId);
            e.HasIndex(x => x.ToId);
        });
        modelBuilder.Entity<Obligation>(e =>
        {
            e.ToTable("Obligations");
            e.HasIndex(x => x.ContractId);
            e.HasIndex(x => x.ResponsibleId);
        });
        modelBuilder.Entity<ContractDocument>(e =>
        {
            e.ToTable("Documents");
            e.Property(x => x.Name).HasMaxLength(260);
            e.Property(x => x.StoreKey).HasMaxLength(1000);
            e.HasIndex(x => x.ContractId);
        });
        modelBuilder.Entity<ApprovalRule>(e =>
        {
            e.ToTable("ApprovalRules");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.MinValue).HasPrecision(18, 2);
            e.Property(x => x.MaxValue).HasPrecision(18, 2);
        });
        modelBuilder.Entity<ApprovalInstance>(e =>
        {
            e.ToTable("ApprovalInstances");
            e.HasIndex(x => x.ContractId);
        });
        modelBuilder.Entity<ApprovalStep>(e =>
        {
            e.ToTable("ApprovalSteps");
            e.Property(x => x.RoleLabel).HasMaxLength(300);
            e.HasIndex(x => x.InstanceId);
            e.HasIndex(x => x.ApproverId);
        });
        modelBuilder.Entity<Reminder>(e =>
        {
            e.ToTable("Reminders");
            e.Property(x => x.Kind).HasMaxLength(40);
            e.HasIndex(x => x.ContractId);
        });
        modelBuilder.Entity<RetentionRule>(e =>
        {
            e.ToTable("RetentionRules");
        });
        modelBuilder.Entity<ContractTemplate>(e =>
        {
            e.ToTable("Templates");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.FileName).HasMaxLength(260);
        });
        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvents");
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.UserName).HasMaxLength(200);
            e.HasIndex(x => x.ContractId);
            e.HasIndex(x => x.At);
        });
        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.RecipientEmail).HasMaxLength(256);
            e.Property(x => x.Subject).HasMaxLength(400);
            e.HasIndex(x => x.CreatedAt);
        });
        modelBuilder.Entity<ReminderSettings>(e =>
        {
            e.ToTable("ReminderSettings");
            e.Property(x => x.NoticeDays).HasMaxLength(100);
        });
        modelBuilder.Entity<PendingUpload>(e =>
        {
            e.ToTable("PendingUploads");
            e.Property(x => x.FileName).HasMaxLength(260);
        });

        // DateTime values are always UTC; make sure they come back from the database marked as such.
        var utc = new ValueConverter<DateTime, DateTime>(
            value => value,
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            value => value,
            value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(utc);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(utcNullable);
                }
            }
        }
    }
}
