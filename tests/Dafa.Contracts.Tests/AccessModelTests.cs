using Dafa.Contracts.Core.Common;
using Dafa.Contracts.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dafa.Contracts.Tests;

public class AccessModelTests
{
    private static readonly Guid Department = Guid.NewGuid();
    private static readonly Guid OtherDepartment = Guid.NewGuid();
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly Guid OtherEntity = Guid.NewGuid();
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid DelegateId = Guid.NewGuid();
    private static readonly Guid ResponsibleId = Guid.NewGuid();

    private static Contract NewContract() => new()
    {
        Id = Guid.NewGuid(),
        DepartmentId = Department,
        EntityId = Entity,
        OwnerId = OwnerId,
        DelegateId = DelegateId,
        ResponsibleId = ResponsibleId,
        Status = ContractStatus.Active
    };

    private static AccessProfile Reader() => new() { UserId = Guid.NewGuid(), Roles = AppRole.Reader };

    [Fact]
    public void Path1_DepartmentMember_CanRead()
    {
        var profile = Reader();
        profile.DepartmentIds.Add(Department);
        Assert.True(ContractAccessRules.CanRead(profile, NewContract()));
    }

    [Fact]
    public void Path1_MemberOfAnotherDepartment_CannotRead()
    {
        var profile = Reader();
        profile.DepartmentIds.Add(OtherDepartment);
        Assert.False(ContractAccessRules.CanRead(profile, NewContract()));
    }

    [Fact]
    public void Path2_EntityManagement_CanReadOwnEntityOnly()
    {
        var profile = Reader();
        profile.ManagedEntityIds.Add(Entity);
        Assert.True(ContractAccessRules.CanRead(profile, NewContract()));

        var other = Reader();
        other.ManagedEntityIds.Add(OtherEntity);
        Assert.False(ContractAccessRules.CanRead(other, NewContract()));
    }

    [Fact]
    public void Path3_GroupManagement_SeesAll()
    {
        var profile = Reader();
        profile.IsGroupManagement = true;
        Assert.True(ContractAccessRules.SeesAll(profile));
        Assert.True(ContractAccessRules.CanRead(profile, NewContract()));
    }

    [Fact]
    public void Path4_OwnerDelegateAndResponsible_CanRead()
    {
        foreach (var userId in new[] { OwnerId, DelegateId, ResponsibleId })
        {
            var profile = new AccessProfile { UserId = userId, Roles = AppRole.Reader };
            Assert.True(ContractAccessRules.CanRead(profile, NewContract()));
        }
    }

    [Fact]
    public void Path5_Admin_CanReadAndEdit()
    {
        var profile = new AccessProfile { UserId = Guid.NewGuid(), Roles = AppRole.Reader | AppRole.Admin };
        Assert.True(ContractAccessRules.CanRead(profile, NewContract()));
        Assert.True(ContractAccessRules.CanEdit(profile, NewContract()));
    }

    [Fact]
    public void NoPath_IsDenied_UnlessCurrentApprover()
    {
        var profile = Reader();
        Assert.False(ContractAccessRules.CanRead(profile, NewContract()));
        Assert.True(ContractAccessRules.CanRead(profile, NewContract(), isCurrentApprover: true));
    }

    [Fact]
    public void Edit_RequiresContractOwnerRole_AndBeingOnTheContract()
    {
        var contract = NewContract();
        var ownerWithoutRole = new AccessProfile { UserId = OwnerId, Roles = AppRole.Reader };
        var ownerWithRole = new AccessProfile { UserId = OwnerId, Roles = AppRole.Reader | AppRole.ContractOwner };
        var delegateWithRole = new AccessProfile { UserId = DelegateId, Roles = AppRole.Reader | AppRole.ContractOwner };
        var responsibleWithRole = new AccessProfile { UserId = ResponsibleId, Roles = AppRole.Reader | AppRole.ContractOwner };
        var departmentMemberWithRole = new AccessProfile { UserId = Guid.NewGuid(), Roles = AppRole.Reader | AppRole.ContractOwner };
        departmentMemberWithRole.DepartmentIds.Add(Department);

        Assert.False(ContractAccessRules.CanEdit(ownerWithoutRole, contract));
        Assert.True(ContractAccessRules.CanEdit(ownerWithRole, contract));
        Assert.True(ContractAccessRules.CanEdit(delegateWithRole, contract));
        Assert.True(ContractAccessRules.CanEdit(responsibleWithRole, contract));
        // Seeing a contract (department member) does not grant edit.
        Assert.True(ContractAccessRules.CanRead(departmentMemberWithRole, contract));
        Assert.False(ContractAccessRules.CanEdit(departmentMemberWithRole, contract));
    }

    [Fact]
    public async Task QueryFilter_ReturnsExactlyTheVisibleContracts()
    {
        using var world = new TestWorld();
        var purchasingInA = world.AddContract();
        var itInB = world.AddContract(c =>
        {
            c.EntityId = world.EntityB;
            c.DepartmentId = world.It;
            c.OwnerId = world.ItHead;
        });
        var itInBWithResponsible = world.AddContract(c =>
        {
            c.EntityId = world.EntityB;
            c.DepartmentId = world.It;
            c.OwnerId = world.ItHead;
            c.ResponsibleId = world.PurchasingMember;
        });

        async Task<HashSet<Guid>> VisibleTo(Guid userId)
        {
            var actor = world.As(userId);
            var query = await actor.Access.FilterAsync(actor.Db.Contracts.AsNoTracking());
            var ids = await query.Select(c => c.Id).ToListAsync();
            return new HashSet<Guid>(ids);
        }

        var all = new HashSet<Guid> { purchasingInA.Id, itInB.Id, itInBWithResponsible.Id };

        Assert.True(all.SetEquals(await VisibleTo(world.Admin)));                      // 5. admin
        Assert.True(all.SetEquals(await VisibleTo(world.Cfo)));                        // 3. group management
        Assert.True(new HashSet<Guid> { purchasingInA.Id, itInBWithResponsible.Id }
            .SetEquals(await VisibleTo(world.PurchasingMember)));                      // 1. department + 4. responsible
        Assert.True(new HashSet<Guid> { purchasingInA.Id }
            .SetEquals(await VisibleTo(world.EntityAManager)));                        // 2. entity management
        Assert.True(new HashSet<Guid> { purchasingInA.Id }
            .SetEquals(await VisibleTo(world.Owner)));                                 // 4. owner
        Assert.True(new HashSet<Guid> { itInB.Id, itInBWithResponsible.Id }
            .SetEquals(await VisibleTo(world.ItHead)));                                // 1. department
        Assert.Empty(await VisibleTo(world.Outsider));                                 // no path
    }

    [Fact]
    public async Task GetReadable_Forbidden_ForUserWithoutAccess_AndNotFound_ForMissingContract()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();

        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.Outsider).Access.GetReadableAsync(contract.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => world.As(world.Admin).Access.GetReadableAsync(Guid.NewGuid()));

        var visible = await world.As(world.PurchasingMember).Access.GetReadableAsync(contract.Id);
        Assert.Equal(contract.Id, visible.Id);
    }

    [Fact]
    public async Task ListAndDetail_GoThroughTheFilter()
    {
        using var world = new TestWorld();
        var contract = world.AddContract();

        var outsiderList = await world.As(world.Outsider).Queries.ListAsync(new Dafa.Contracts.Core.Services.ContractListQuery());
        Assert.Equal(0, outsiderList.Total);

        var memberList = await world.As(world.PurchasingMember).Queries.ListAsync(new Dafa.Contracts.Core.Services.ContractListQuery());
        Assert.Equal(1, memberList.Total);
        Assert.Equal(contract.Id, memberList.Items[0].Id);

        await Assert.ThrowsAsync<ForbiddenException>(() => world.As(world.Outsider).Queries.GetDetailAsync(contract.Id, false));
        var detail = await world.As(world.Owner).Queries.GetDetailAsync(contract.Id, false);
        Assert.True(detail.CanEdit);
        Assert.Equal("Owner User", detail.OwnerName);
    }
}
