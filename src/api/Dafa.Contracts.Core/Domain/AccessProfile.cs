namespace Dafa.Contracts.Core.Domain;

/// <summary>Everything the access model needs to know about the signed-in user.</summary>
public sealed class AccessProfile
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Initials { get; set; } = "";
    public AppRole Roles { get; set; } = AppRole.Reader;
    public bool IsGroupManagement { get; set; }
    public HashSet<Guid> DepartmentIds { get; set; } = new();
    public HashSet<Guid> ManagedEntityIds { get; set; } = new();
    /// <summary>Release 4: the user's home legal entity (display only; not part of the access model).</summary>
    public Guid? HomeEntityId { get; set; }
    /// <summary>Release 4: may send contracts for e-signature. Administrators do not get this implicitly.</summary>
    public bool CanSendForSignature { get; set; }
    /// <summary>Release 4: legal entities the signing permission is limited to; empty = every entity.</summary>
    public HashSet<Guid> SignatureEntityIds { get; set; } = new();

    public bool IsAdmin => (Roles & AppRole.Admin) == AppRole.Admin;
    public bool HasRole(AppRole role) => (Roles & role) == role;
}

/// <summary>
/// The access model from the build brief, section 3, as pure functions. The database query
/// filter in ContractAccessService must stay equivalent to <see cref="CanRead"/>.
/// </summary>
public static class ContractAccessRules
{
    public static bool SeesAll(AccessProfile profile) => profile.IsAdmin || profile.IsGroupManagement;

    public static bool IsOnContract(AccessProfile profile, Contract contract) =>
        contract.OwnerId == profile.UserId
        || contract.DelegateId == profile.UserId
        || contract.ResponsibleId == profile.UserId;

    public static bool CanRead(AccessProfile profile, Contract contract, bool isCurrentApprover = false)
    {
        if (profile.IsAdmin) return true;                                          // 5. admin
        if (profile.IsGroupManagement) return true;                                // 3. group management
        if (profile.DepartmentIds.Contains(contract.DepartmentId)) return true;    // 1. department member
        if (profile.ManagedEntityIds.Contains(contract.EntityId)) return true;     // 2. entity management
        if (IsOnContract(profile, contract)) return true;                          // 4. owner/delegate/responsible
        return isCurrentApprover;                                                  // open approval step
    }

    public static bool CanCreate(AccessProfile profile) =>
        profile.IsAdmin || profile.HasRole(AppRole.ContractOwner);

    /// <summary>Admin, or role ContractOwner AND owner/delegate/responsible. Seeing a contract does not grant edit.</summary>
    public static bool CanEdit(AccessProfile profile, Contract contract)
    {
        if (profile.IsAdmin) return true;
        return profile.HasRole(AppRole.ContractOwner) && IsOnContract(profile, contract);
    }

    /// <summary>
    /// People on the contract or an admin, on an active contract. An open-ended contract
    /// (no end date) only accepts the decision GiveNotice.
    /// </summary>
    public static bool CanDecideRenewal(AccessProfile profile, Contract contract)
    {
        if (contract.Status != ContractStatus.Active)
        {
            return false;
        }
        return profile.IsAdmin || IsOnContract(profile, contract);
    }

    /// <summary>
    /// Release 3: analytics and the overview are for every signed-in user; the figures cover only
    /// the contracts the user can see. Kept for callers that still ask.
    /// </summary>
    public static bool CanSeeOverview(AccessProfile profile) => true;

    /// <summary>
    /// Every rule of the access model that gives this user read access to the contract
    /// (empty = no access). Rule names: Admin, GroupManagement, Department, EntityManagement, Owner,
    /// Delegate, Responsible, CurrentApprover.
    /// </summary>
    public static List<string> GrantingRules(AccessProfile profile, Contract contract, bool isCurrentApprover)
    {
        var rules = new List<string>();
        if (profile.IsAdmin) rules.Add("Admin");
        if (profile.IsGroupManagement) rules.Add("GroupManagement");
        if (profile.DepartmentIds.Contains(contract.DepartmentId)) rules.Add("Department");
        if (profile.ManagedEntityIds.Contains(contract.EntityId)) rules.Add("EntityManagement");
        if (contract.OwnerId == profile.UserId) rules.Add("Owner");
        if (contract.DelegateId == profile.UserId) rules.Add("Delegate");
        if (contract.ResponsibleId == profile.UserId) rules.Add("Responsible");
        if (isCurrentApprover) rules.Add("CurrentApprover");
        return rules;
    }
}
