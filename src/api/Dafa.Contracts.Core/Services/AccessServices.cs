using Dafa.Contracts.Core.Abstractions;
using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Core.Services;

public static class AccessProfileLoader
{
    /// <summary>Loads the access profile of an active user, or null when unknown or inactive.</summary>
    public static async Task<AccessProfile?> LoadAsync(IAppDbContext db, Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return null;
        }
        var departmentIds = await db.UserDepartments.AsNoTracking()
            .Where(d => d.UserId == userId)
            .Select(d => d.DepartmentId)
            .ToListAsync(cancellationToken);
        var entityIds = await db.UserEntityManagements.AsNoTracking()
            .Where(e => e.UserId == userId)
            .Select(e => e.EntityId)
            .ToListAsync(cancellationToken);

        return new AccessProfile
        {
            UserId = user.Id,
            Name = user.Name,
            Email = user.Email,
            Initials = user.Initials,
            Roles = user.Roles,
            IsGroupManagement = user.IsGroupManagement,
            DepartmentIds = new HashSet<Guid>(departmentIds),
            ManagedEntityIds = new HashSet<Guid>(entityIds)
        };
    }
}

public class ContractAccessService : IContractAccessService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private List<Guid>? _approvalContractIds;

    public ContractAccessService(IAppDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>Contracts where the user is the approver of a step that is open right now.</summary>
    private async Task<List<Guid>> GetApprovalContractIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_approvalContractIds is null)
        {
            _approvalContractIds = await _db.ApprovalSteps.AsNoTracking()
                .Where(s => s.ApproverId == userId && s.Status == ApprovalStepStatus.Pending)
                .Select(s => s.ContractId)
                .Distinct()
                .ToListAsync(cancellationToken);
        }
        return _approvalContractIds;
    }

    public async Task<IQueryable<Contract>> FilterAsync(IQueryable<Contract> query, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (ContractAccessRules.SeesAll(profile))
        {
            return query;
        }

        var approvalContractIds = await GetApprovalContractIdsAsync(profile.UserId, cancellationToken);
        return ApplyFilter(query, profile, approvalContractIds);
    }

    /// <summary>
    /// The access filter for any user profile (the same SQL as FilterAsync). Used by FilterAsync for
    /// the signed-in user and by administration ("effective access") for another user.
    /// </summary>
    public static IQueryable<Contract> ApplyFilter(IQueryable<Contract> query, AccessProfile profile, List<Guid> approvalContractIds)
    {
        if (ContractAccessRules.SeesAll(profile))
        {
            return query;
        }
        var userId = profile.UserId;
        var departmentIds = profile.DepartmentIds.ToList();
        var entityIds = profile.ManagedEntityIds.ToList();
        return query.Where(c =>
            departmentIds.Contains(c.DepartmentId)
            || entityIds.Contains(c.EntityId)
            || c.OwnerId == userId
            || c.DelegateId == userId
            || c.ResponsibleId == userId
            || approvalContractIds.Contains(c.Id));
    }

    /// <summary>Contracts on which the user holds a Pending approval step (read access while the step is open).</summary>
    public static Task<List<Guid>> LoadApprovalContractIdsAsync(IAppDbContext db, Guid userId, CancellationToken cancellationToken = default)
    {
        return db.ApprovalSteps.AsNoTracking()
            .Where(s => s.ApproverId == userId && s.Status == ApprovalStepStatus.Pending)
            .Select(s => s.ContractId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <summary>Whether another user (not the signed-in one) may read a contract. Used before notifying people.</summary>
    public static async Task<bool> CanUserReadAsync(IAppDbContext db, Guid userId, Contract contract, CancellationToken cancellationToken = default)
    {
        var profile = await AccessProfileLoader.LoadAsync(db, userId, cancellationToken);
        if (profile is null)
        {
            return false;
        }
        if (ContractAccessRules.CanRead(profile, contract))
        {
            return true;
        }
        var contractId = contract.Id;
        return await db.ApprovalSteps.AsNoTracking()
            .AnyAsync(s => s.ApproverId == userId && s.ContractId == contractId && s.Status == ApprovalStepStatus.Pending, cancellationToken);
    }

    public async Task<bool> CanReadAsync(Contract contract, CancellationToken cancellationToken = default)
    {
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (ContractAccessRules.CanRead(profile, contract))
        {
            return true;
        }
        var approvalContractIds = await GetApprovalContractIdsAsync(profile.UserId, cancellationToken);
        return approvalContractIds.Contains(contract.Id);
    }

    public async Task<Contract> GetReadableAsync(Guid contractId, bool tracked = false, CancellationToken cancellationToken = default)
    {
        var query = tracked ? _db.Contracts : _db.Contracts.AsNoTracking();
        var contract = await query.FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken);
        if (contract is null)
        {
            throw new NotFoundException("Contract not found.");
        }
        if (!await CanReadAsync(contract, cancellationToken))
        {
            throw new ForbiddenException("You do not have access to this contract.");
        }
        return contract;
    }

    public async Task<Contract> GetEditableAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        var contract = await GetReadableAsync(contractId, true, cancellationToken);
        var profile = await _currentUser.GetProfileAsync(cancellationToken);
        if (!ContractAccessRules.CanEdit(profile, contract))
        {
            throw new ForbiddenException("Only the contract owner, the delegate or an administrator can change this contract.");
        }
        return contract;
    }
}

/// <summary>Adds audit events to the current unit of work. The caller saves.</summary>
public class AuditService
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public AuditService(IAppDbContext db, ICurrentUser currentUser, IClock clock)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
    }

    public const string ContractViewed = "contract.viewed";

    /// <summary>
    /// Release 3: actions that notify a contract's watchers (status changes, renewal decisions,
    /// details updated, documents uploaded, comments added).
    /// </summary>
    public static readonly HashSet<string> WatchedActions = new(StringComparer.Ordinal)
    {
        "contract.updated", "contract.signed", "contract.expired", "contract.terminated",
        "approval.started", "approval.completed", "approval.rejected",
        "renewal.decided", "renewal.renewed", "renewal.automatic",
        "document.uploaded", "comment.added", "contract.reassigned", "import.contract-updated"
    };

    private readonly HashSet<Guid> _watchersNotified = new();

    /// <summary>Records an audit event with structured data (DataJson).</summary>
    public AuditEvent Record(string action, string text, Guid? contractId, string? dataJson)
    {
        var auditEvent = Record(action, text, contractId);
        auditEvent.DataJson = dataJson;
        return auditEvent;
    }

    public AuditEvent Record(string action, string text, Guid? contractId = null)
    {
        if (contractId is not null && WatchedActions.Contains(action))
        {
            NotifyWatchers(contractId.Value, text);
        }
        var auditEvent = new AuditEvent
        {
            Id = Guid.NewGuid(),
            At = _clock.UtcNow,
            UserId = _currentUser.UserId,
            UserName = string.IsNullOrWhiteSpace(_currentUser.UserName) ? "System" : _currentUser.UserName!,
            Action = action,
            ContractId = contractId,
            Text = text
        };
        _db.AuditEvents.Add(auditEvent);
        return auditEvent;
    }

    /// <summary>
    /// In-app WatchedContractChanged notifications to the contract's watchers (not the actor), at
    /// most once per contract and unit of work. Synchronous on purpose: Record is synchronous and is
    /// called from many places. Respects the event's settings (enabled, InApp channel, templates)
    /// and the watcher's preferences. Mail for watched contracts goes through the digest. A failure
    /// here never stops the audited action.
    /// </summary>
    private void NotifyWatchers(Guid contractId, string text)
    {
        if (!_watchersNotified.Add(contractId))
        {
            return;
        }
        try
        {
            var actorId = _currentUser.UserId;
            var watchers = _db.ContractWatchers.AsNoTracking()
                .Where(w => w.ContractId == contractId)
                .Select(w => w.UserId)
                .ToList()
                .Where(id => id != actorId)
                .ToList();
            if (watchers.Count == 0)
            {
                return;
            }
            var setting = _db.NotificationSettings.AsNoTracking().FirstOrDefault(n => n.EventType == NotificationCatalog.WatchedContractChanged);
            if (setting is not null && (!setting.Enabled || !NotificationRouting.SplitList(setting.Channels).Contains("InApp")))
            {
                return;
            }
            var contract = _db.Contracts.Local.FirstOrDefault(c => c.Id == contractId)
                ?? _db.Contracts.AsNoTracking().FirstOrDefault(c => c.Id == contractId);
            if (contract is null)
            {
                return;
            }
            var actor = string.IsNullOrWhiteSpace(_currentUser.UserName) ? "System" : _currentUser.UserName!;
            var values = new Dictionary<string, string>
            {
                ["contract.title"] = contract.Title,
                ["contract.no"] = contract.ContractNo,
                ["contract.counterparty"] = contract.CounterpartyName,
                ["contract.url"] = "/contracts/" + contract.Id,
                ["contract.entity"] = "",
                ["actor.name"] = actor,
                ["app.name"] = "DAFA Contracts",
                ["comment"] = text
            };
            var preferences = _db.UserPreferences.AsNoTracking().Where(p => watchers.Contains(p.UserId)).ToList();
            foreach (var watcherId in watchers)
            {
                var preference = preferences.FirstOrDefault(p => p.UserId == watcherId);
                if (!PreferenceReader.Allows(preference, NotificationCatalog.WatchedContractChanged, "InApp"))
                {
                    continue;
                }
                var title = setting?.SubjectTemplate is null ? contract.Title + " was updated" : NotificationCatalog.Render(setting.SubjectTemplate, values);
                var body = setting?.BodyTemplate is null ? actor + ": " + text : NotificationCatalog.Render(setting.BodyTemplate, values);
                _db.Notifications.Add(new InAppNotification
                {
                    Id = Guid.NewGuid(),
                    UserId = watcherId,
                    Event = NotificationCatalog.WatchedContractChanged,
                    Title = title.Length > 400 ? title.Substring(0, 400) : title,
                    Body = body,
                    ContractId = contract.Id,
                    Link = "/contracts/" + contract.Id,
                    ActorName = actor,
                    CreatedAt = _clock.UtcNow
                });
            }
        }
        catch (Exception)
        {
            // Notifications are best effort; the audited change must go through.
        }
    }
}
