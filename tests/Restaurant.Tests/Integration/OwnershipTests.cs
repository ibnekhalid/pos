using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Staff;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>Handing the restaurant to someone else (ownership.transfer).</summary>
public class OwnershipTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task An_owner_hands_over_the_restaurant_and_steps_down_to_manager()
    {
        var owner = await factory.RegisterOwnerAsync();
        var (manager, managerMember, _) = await factory.AddStaffAsync(owner, ["Manager"]);
        var url = $"/api/staff/{managerMember.MembershipId}/transfer-ownership";

        // Only an owner, only with their own password, never to themselves.
        (await manager.RawPostAsync(url, new TransferOwnershipRequest(TestApi.Password, false))).ShouldBe(HttpStatusCode.Forbidden);
        (await owner.RawPostAsync(url, new TransferOwnershipRequest("wrong-password", true))).ShouldBe(HttpStatusCode.BadRequest);
        var me = await owner.GetAsync<MeDto>("/api/me");
        (await owner.RawPostAsync($"/api/staff/{me.MembershipId}/transfer-ownership", new TransferOwnershipRequest(TestApi.Password, true)))
            .ShouldBe(HttpStatusCode.BadRequest);

        var newOwner = await owner.PostAsync<StaffMemberDto>(url, new TransferOwnershipRequest(TestApi.Password, true));
        newOwner.IsOwner.Should().BeTrue();
        newOwner.AllBranches.Should().BeTrue();

        // The roles apply to the existing sessions straight away.
        (await manager.GetAsync<MeDto>("/api/me")).IsOwner.Should().BeTrue();
        var former = await owner.GetAsync<MeDto>("/api/me");
        former.IsOwner.Should().BeFalse();
        former.Permissions.Should().Contain(Permissions.Sessions.Close).And.NotContain(Permissions.Tenant.TransferOwnership);
        (await owner.RawGetAsync("/api/audit")).ShouldBe(HttpStatusCode.Forbidden);

        var audit = await manager.GetAsync<Restaurant.Contracts.Common.PagedResult<Restaurant.Contracts.Audit.AuditLogDto>>("/api/audit");
        audit.Items.Should().Contain(a => a.Action == "ownership.transferred");

        // Already an owner: refused.
        (await manager.RawPostAsync(url, new TransferOwnershipRequest(TestApi.Password, false))).ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Without_stepping_down_both_are_owners()
    {
        var owner = await factory.RegisterOwnerAsync();
        var (cashier, member, _) = await factory.AddStaffAsync(owner, ["Cashier"]);

        await owner.PostAsync<StaffMemberDto>($"/api/staff/{member.MembershipId}/transfer-ownership", new TransferOwnershipRequest(TestApi.Password, false));

        (await owner.GetAsync<MeDto>("/api/me")).IsOwner.Should().BeTrue();
        (await cashier.GetAsync<MeDto>("/api/me")).IsOwner.Should().BeTrue();
        (await owner.RawPostAsync($"/api/staff/{member.MembershipId}/transfer-ownership", new TransferOwnershipRequest(TestApi.Password, false)))
            .ShouldBe(HttpStatusCode.Conflict);
    }
}
