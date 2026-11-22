using Dafa.Contracts.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Abstractions;

/// <summary>The database as seen by the application services. Implemented by AppDbContext.</summary>
public interface IAppDbContext
{
    DbSet<LegalEntity> LegalEntities { get; }
    DbSet<Department> Departments { get; }
    DbSet<ContractType> ContractTypes { get; }
    DbSet<AppUser> Users { get; }
    DbSet<UserDepartment> UserDepartments { get; }
    DbSet<UserEntityManagement> UserEntityManagements { get; }
    DbSet<HolidayCover> HolidayCovers { get; }
    DbSet<Supplier> Suppliers { get; }
    DbSet<Contract> Contracts { get; }
    DbSet<ContractLink> ContractLinks { get; }
    DbSet<Obligation> Obligations { get; }
    DbSet<ContractDocument> Documents { get; }
    DbSet<ApprovalRule> ApprovalRules { get; }
    DbSet<ApprovalInstance> ApprovalInstances { get; }
    DbSet<ApprovalStep> ApprovalSteps { get; }
    DbSet<Reminder> Reminders { get; }
    DbSet<RetentionRule> RetentionRules { get; }
    DbSet<ContractTemplate> Templates { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<ReminderSettings> ReminderSettings { get; }
    DbSet<PendingUpload> PendingUploads { get; }

    // Release 3
    DbSet<DocumentText> DocumentTexts { get; }
    DbSet<CustomFieldDefinition> CustomFieldDefinitions { get; }
    DbSet<CustomFieldValue> CustomFieldValues { get; }
    DbSet<ContractTypeFieldRule> FieldRules { get; }
    DbSet<CurrencyDefinition> Currencies { get; }
    DbSet<Tag> Tags { get; }
    DbSet<ContractTag> ContractTags { get; }
    DbSet<ImportFile> ImportFiles { get; }
    DbSet<ImportRun> ImportRuns { get; }
    DbSet<SyncSource> SyncSources { get; }
    DbSet<NotificationSetting> NotificationSettings { get; }
    DbSet<ReminderSchedule> ReminderSchedules { get; }
    DbSet<JobSchedule> JobSchedules { get; }
    DbSet<AppSetting> AppSettings { get; }
    DbSet<SavedView> SavedViews { get; }
    DbSet<ContractWatcher> ContractWatchers { get; }
    DbSet<ContractComment> ContractComments { get; }
    DbSet<InAppNotification> Notifications { get; }
    DbSet<UserPreference> UserPreferences { get; }
    DbSet<DataRoomExport> DataRoomExports { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets every tracked entity and pending change. Used after a failed save (concurrency
    /// conflict, unique index) before the operation is retried from freshly loaded rows.
    /// </summary>
    void DiscardChanges();
}
