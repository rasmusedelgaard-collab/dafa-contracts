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

        var userId = profile.UserId;
        var departmentIds = profile.DepartmentIds.ToList();
        var entityIds = profile.ManagedEntityIds.ToList();
        var approvalContractIds = await GetApprovalContractIdsAsync(userId, cancellationToken);

        return query.Where(c =>
            departmentIds.Contains(c.DepartmentId)
            || entityIds.Contains(c.EntityId)
            || c.OwnerId == userId
            || c.DelegateId == userId
            || c.ResponsibleId == userId
            || approvalContractIds.Contains(c.Id));
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

    public AuditEvent Record(string action, string text, Guid? contractId = null)
    {
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
}

/// <summary>Small reference tables loaded once per request and joined in memory.</summary>
public sealed class ReferenceData
{
    public Dictionary<Guid, string> Entities { get; private set; } = new();
    public Dictionary<Guid, string> Departments { get; private set; } = new();
    public Dictionary<Guid, string> Types { get; private set; } = new();
    public Dictionary<Guid, AppUser> Users { get; private set; } = new();

    public static async Task<ReferenceData> LoadAsync(IAppDbContext db, CancellationToken cancellationToken = default)
    {
        var data = new ReferenceData();
        data.Entities = (await db.LegalEntities.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(e => e.Id, e => e.Name);
        data.Departments = (await db.Departments.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        data.Types = (await db.ContractTypes.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(t => t.Id, t => t.Name);
        data.Users = (await db.Users.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(u => u.Id, u => u);
        return data;
    }

    public string Entity(Guid id) => Entities.TryGetValue(id, out var name) ? name : "";
    public string Department(Guid id) => Departments.TryGetValue(id, out var name) ? name : "";
    public string Type(Guid id) => Types.TryGetValue(id, out var name) ? name : "";
    public string UserName(Guid? id) => id is not null && Users.TryGetValue(id.Value, out var user) ? user.Name : "";
    public string UserInitials(Guid? id) => id is not null && Users.TryGetValue(id.Value, out var user) ? user.Initials : "";
}
