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

    public DbSet<DocumentText> DocumentTexts => Set<DocumentText>();
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();
    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();
    public DbSet<ContractTypeFieldRule> FieldRules => Set<ContractTypeFieldRule>();
    public DbSet<CurrencyDefinition> Currencies => Set<CurrencyDefinition>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ContractTag> ContractTags => Set<ContractTag>();
    public DbSet<ImportFile> ImportFiles => Set<ImportFile>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<SyncSource> SyncSources => Set<SyncSource>();
    public DbSet<NotificationSetting> NotificationSettings => Set<NotificationSetting>();
    public DbSet<ReminderSchedule> ReminderSchedules => Set<ReminderSchedule>();
    public DbSet<JobSchedule> JobSchedules => Set<JobSchedule>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();
    public DbSet<ContractWatcher> ContractWatchers => Set<ContractWatcher>();
    public DbSet<ContractComment> ContractComments => Set<ContractComment>();
    public DbSet<InAppNotification> Notifications => Set<InAppNotification>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    public DbSet<DataRoomExport> DataRoomExports => Set<DataRoomExport>();

    public DbSet<TemplateCategory> TemplateCategories => Set<TemplateCategory>();
    public DbSet<BrandingAsset> BrandingAssets => Set<BrandingAsset>();
    public DbSet<SignatureRequest> SignatureRequests => Set<SignatureRequest>();
    public DbSet<Signatory> Signatories => Set<Signatory>();
    public DbSet<StorageReconciliationReport> StorageReconciliationReports => Set<StorageReconciliationReport>();

    public void DiscardChanges()
    {
        ChangeTracker.Clear();
    }

    /// <summary>Gives every new or changed <see cref="IVersioned"/> row a new concurrency token.</summary>
    private void StampVersions()
    {
        foreach (var entry in ChangeTracker.Entries<IVersioned>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                // The original value stays in the WHERE clause of the UPDATE, so a stale copy fails.
                entry.Entity.Version = Guid.NewGuid();
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<LegalEntity>(e =>
        {
            e.ToTable("LegalEntities");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.Currency).HasMaxLength(3);
            // Release 4 (schema step 3)
            e.Property(x => x.SharePointSiteUrl).HasMaxLength(400);
            e.Property(x => x.SharePointSiteId).HasMaxLength(300);
            e.Property(x => x.ManagementGroupId).HasMaxLength(100);
            e.Property(x => x.ManagementGroupName).HasMaxLength(200);
            e.Property(x => x.LinkStatus).HasMaxLength(20);
            e.Property(x => x.LinkMessage).HasMaxLength(1000);
        });
        modelBuilder.Entity<Department>(e =>
        {
            e.ToTable("Departments");
            e.Property(x => x.Name).HasMaxLength(200);
            // Release 4 (schema step 3)
            e.Property(x => x.LibraryName).HasMaxLength(200);
            e.Property(x => x.GroupId).HasMaxLength(100);
            e.Property(x => x.GroupName).HasMaxLength(200);
            e.Property(x => x.LinkStatus).HasMaxLength(20);
            e.Property(x => x.LinkMessage).HasMaxLength(1000);
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
            e.Property(x => x.Source).HasMaxLength(20);
            e.HasIndex(x => x.Email);
            // One user per directory object id. Null (demo and not yet linked users) is not indexed
            // on SQL Server (EF adds the IS NOT NULL filter) and never clashes on Sqlite.
            e.HasIndex(x => x.ExternalId).IsUnique();
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
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Phone).HasMaxLength(50);
            e.Property(x => x.Source).HasMaxLength(20);
        });
        modelBuilder.Entity<Contract>(e =>
        {
            e.ToTable("Contracts");
            e.Property(x => x.ContractNo).HasMaxLength(32);
            e.HasIndex(x => x.ContractNo).IsUnique();
            e.Property(x => x.ExternalRef).HasMaxLength(100);
            e.HasIndex(x => x.ExternalRef);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.CounterpartyName).HasMaxLength(300);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.AnnualValue).HasPrecision(18, 2);
            e.Property(x => x.TotalValue).HasPrecision(18, 2);
            e.Property(x => x.Version).IsConcurrencyToken();
            // SharePoint hardening (schema step 4)
            e.Property(x => x.StoreDriveId).HasMaxLength(200);
            e.Property(x => x.StoreFolderId).HasMaxLength(200);
            e.Property(x => x.StoreFolderUrl).HasMaxLength(1000);
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
            // SharePoint hardening (schema step 4)
            e.Property(x => x.SiteId).HasMaxLength(300);
            e.Property(x => x.DriveId).HasMaxLength(200);
            e.Property(x => x.ItemId).HasMaxLength(200);
            e.Property(x => x.RelativePath).HasMaxLength(1000);
            e.Property(x => x.ETag).HasMaxLength(200);
            e.Property(x => x.CTag).HasMaxLength(200);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            // Two uploads of the same name can never share a version (and so never one file).
            e.HasIndex(x => new { x.ContractId, x.Name, x.Version }).IsUnique();
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
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.ContractId);
            // At most one open approval per contract. Status is stored as an int; Pending = 0.
            // The bracket syntax is accepted by both SQL Server and Sqlite.
            e.HasIndex(x => x.ContractId, "IX_ApprovalInstances_OnePendingPerContract")
                .IsUnique()
                .HasFilter("[Status] = 0");
        });
        modelBuilder.Entity<ApprovalStep>(e =>
        {
            e.ToTable("ApprovalSteps");
            e.Property(x => x.RoleLabel).HasMaxLength(300);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.InstanceId);
            e.HasIndex(x => x.ApproverId);
        });
        modelBuilder.Entity<Reminder>(e =>
        {
            e.ToTable("Reminders");
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.TermKey).HasMaxLength(80);
            // Idempotency in the database: one reminder per contract, kind and term.
            e.HasIndex(x => new { x.ContractId, x.Kind, x.TermKey }).IsUnique();
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
            // Release 4 (schema step 3)
            e.Property(x => x.Language).HasMaxLength(10);
            e.HasIndex(x => x.CategoryId);
        });
        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvents");
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.UserName).HasMaxLength(200);
            e.HasIndex(x => x.ContractId);
            e.HasIndex(x => x.Action);
            e.HasIndex(x => x.At);
        });
        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.RecipientEmail).HasMaxLength(256);
            e.Property(x => x.Subject).HasMaxLength(400);
            e.Property(x => x.Channel).HasMaxLength(20);
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

        // ---------- release 3 ----------
        // Tables below are also created on existing databases by SchemaUpgrader, which reads this
        // model; a column added to an existing table must be nullable or get a default there.
        modelBuilder.Entity<DocumentText>(e =>
        {
            e.ToTable("DocumentTexts");
            e.HasKey(x => x.DocumentId);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(400);
            e.HasIndex(x => x.ContractId);
        });
        modelBuilder.Entity<CustomFieldDefinition>(e =>
        {
            e.ToTable("CustomFieldDefinitions");
            e.Property(x => x.Key).HasMaxLength(40);
            e.Property(x => x.Label).HasMaxLength(80);
            e.Property(x => x.HelpText).HasMaxLength(500);
            e.HasIndex(x => x.Key).IsUnique();
        });
        modelBuilder.Entity<CustomFieldValue>(e =>
        {
            e.ToTable("CustomFieldValues");
            e.Property(x => x.TextValue).HasMaxLength(1000);
            e.HasIndex(x => new { x.ContractId, x.FieldId }).IsUnique();
            e.HasIndex(x => x.FieldId);
        });
        modelBuilder.Entity<ContractTypeFieldRule>(e =>
        {
            e.ToTable("ContractTypeFieldRules");
            e.Property(x => x.Field).HasMaxLength(64);
            e.Property(x => x.DefaultValue).HasMaxLength(400);
            e.HasIndex(x => new { x.ContractTypeId, x.Field }).IsUnique();
        });
        modelBuilder.Entity<CurrencyDefinition>(e =>
        {
            e.ToTable("Currencies");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(3);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.RateToDkk).HasPrecision(18, 6);
        });
        modelBuilder.Entity<Tag>(e =>
        {
            e.ToTable("Tags");
            e.Property(x => x.Name).HasMaxLength(40);
            e.Property(x => x.Color).HasMaxLength(20);
            e.Property(x => x.Description).HasMaxLength(400);
        });
        modelBuilder.Entity<ContractTag>(e =>
        {
            e.ToTable("ContractTags");
            e.HasKey(x => new { x.ContractId, x.TagId });
            e.HasIndex(x => x.TagId);
        });
        modelBuilder.Entity<ImportFile>(e =>
        {
            e.ToTable("ImportFiles");
            e.Property(x => x.FileName).HasMaxLength(260);
        });
        modelBuilder.Entity<ImportRun>(e =>
        {
            e.ToTable("ImportRuns");
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.Trigger).HasMaxLength(20);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.Message).HasMaxLength(1000);
            e.Property(x => x.StartedByName).HasMaxLength(200);
            e.HasIndex(x => x.StartedAt);
            e.HasIndex(x => x.SyncSourceId);
        });
        modelBuilder.Entity<SyncSource>(e =>
        {
            e.ToTable("SyncSources");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.WorkspaceId).HasMaxLength(100);
            e.Property(x => x.DatasetId).HasMaxLength(100);
            e.Property(x => x.TableName).HasMaxLength(200);
            e.Property(x => x.ConnectionName).HasMaxLength(100);
            e.Property(x => x.Time).HasMaxLength(5);
        });
        modelBuilder.Entity<NotificationSetting>(e =>
        {
            e.ToTable("NotificationSettings");
            e.Property(x => x.EventType).HasMaxLength(64);
            e.Property(x => x.Channels).HasMaxLength(100);
            e.Property(x => x.Recipients).HasMaxLength(300);
            e.Property(x => x.SubjectTemplate).HasMaxLength(400);
            e.HasIndex(x => x.EventType).IsUnique();
        });
        modelBuilder.Entity<ReminderSchedule>(e =>
        {
            e.ToTable("ReminderSchedules");
            e.Property(x => x.NoticeDays).HasMaxLength(100);
            e.HasIndex(x => x.ContractTypeId).IsUnique();
        });
        modelBuilder.Entity<JobSchedule>(e =>
        {
            e.ToTable("JobSchedules");
            e.HasKey(x => x.JobKey);
            e.Property(x => x.JobKey).HasMaxLength(64);
            e.Property(x => x.Time).HasMaxLength(5);
            e.Property(x => x.LastStatus).HasMaxLength(30);
            e.Property(x => x.LastMessage).HasMaxLength(1000);
        });
        modelBuilder.Entity<AppSetting>(e =>
        {
            e.ToTable("AppSettings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });
        modelBuilder.Entity<SavedView>(e =>
        {
            e.ToTable("SavedViews");
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Scope).HasMaxLength(20);
            e.HasIndex(x => x.OwnerId);
        });
        modelBuilder.Entity<ContractWatcher>(e =>
        {
            e.ToTable("ContractWatchers");
            e.HasKey(x => new { x.ContractId, x.UserId });
            e.HasIndex(x => x.UserId);
        });
        modelBuilder.Entity<ContractComment>(e =>
        {
            e.ToTable("ContractComments");
            e.HasIndex(x => x.ContractId);
        });
        modelBuilder.Entity<InAppNotification>(e =>
        {
            e.ToTable("InAppNotifications");
            e.Property(x => x.Event).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(400);
            e.Property(x => x.Link).HasMaxLength(400);
            e.Property(x => x.ActorName).HasMaxLength(200);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
        });
        modelBuilder.Entity<UserPreference>(e =>
        {
            e.ToTable("UserPreferences");
            e.HasKey(x => x.UserId);
            e.Property(x => x.Digest).HasMaxLength(20);
            e.Property(x => x.LandingPage).HasMaxLength(40);
            e.Property(x => x.Theme).HasMaxLength(10);
        });
        modelBuilder.Entity<DataRoomExport>(e =>
        {
            e.ToTable("DataRoomExports");
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Documents).HasMaxLength(20);
            e.Property(x => x.Versions).HasMaxLength(20);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.StorageKey).HasMaxLength(400);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.Property(x => x.CreatedByName).HasMaxLength(200);
            e.HasIndex(x => x.CreatedById);
        });

        // ---------- release 4 ----------
        // Created on existing databases by schema step 3 (SchemaUpgrader).
        modelBuilder.Entity<TemplateCategory>(e =>
        {
            e.ToTable("TemplateCategories");
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Description).HasMaxLength(400);
        });
        modelBuilder.Entity<BrandingAsset>(e =>
        {
            e.ToTable("BrandingAssets");
            e.HasKey(x => x.Kind);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.ETag).HasMaxLength(64);
        });
        modelBuilder.Entity<SignatureRequest>(e =>
        {
            e.ToTable("SignatureRequests");
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Provider).HasMaxLength(20);
            e.Property(x => x.ProviderReference).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.CreatedByName).HasMaxLength(200);
            e.Property(x => x.StatusMessage).HasMaxLength(1000);
            e.HasIndex(x => x.ContractId);
            e.HasIndex(x => x.ProviderReference);
            // At most one active request per contract. Status is stored as an int and the active
            // statuses are Draft = 0, Sent = 1, PartiallySigned = 2 (see SignatureRequestStatus).
            // The bracket syntax is accepted by both SQL Server and Sqlite.
            e.HasIndex(x => x.ContractId, "IX_SignatureRequests_OneActivePerContract")
                .IsUnique()
                .HasFilter("[Status] < 3");
        });
        modelBuilder.Entity<StorageReconciliationReport>(e =>
        {
            e.ToTable("StorageReconciliationReports");
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.Property(x => x.StartedByName).HasMaxLength(200);
            e.HasIndex(x => x.StartedAt);
        });
        modelBuilder.Entity<Signatory>(e =>
        {
            e.ToTable("Signatories");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.DeclineReason).HasMaxLength(1000);
            e.HasIndex(x => x.RequestId);
            e.HasIndex(x => x.ContractId);
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
