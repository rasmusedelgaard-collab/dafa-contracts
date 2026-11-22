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

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
