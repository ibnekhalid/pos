using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Staff;
using Restaurant.Contracts.Tenancy;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>Permissions are enforced on the server, can't be escalated, and changes apply immediately.</summary>
public class PermissionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static SaveTaxRateRequest Gst => new("GST", 16m, false, OrderTypes.All, TaxPaymentScope.Any, null);

    [Fact]
    public async Task A_cashier_can_read_settings_but_not_change_them()
    {
        var owner = await factory.RegisterOwnerAsync();
        var (cashier, _, _) = await factory.AddStaffAsync(owner, ["Cashier"]);

        (await cashier.RawGetAsync("/api/tax-rates")).ShouldBe(HttpStatusCode.OK);
        (await cashier.RawPostAsync("/api/tax-rates", Gst)).ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.RawGetAsync("/api/staff")).ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.RawPutAsync("/api/tenant", new UpdateTenantRequest("Mine now", null, null, null))).ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.RawGetAsync("/api/audit")).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Staff_managers_cannot_grant_more_than_they_have()
    {
        var owner = await factory.RegisterOwnerAsync();
        var cashierRole = (await owner.GetAsync<List<RoleDto>>("/api/roles")).Single(r => r.Name == "Cashier");
        var supervisorRole = await owner.PostAsync<RoleDto>("/api/roles", new SaveRoleRequest("Supervisor", null,
            [.. cashierRole.Permissions, Permissions.Staff.View, Permissions.Staff.Manage, Permissions.Staff.ManageRoles]));
        var (supervisor, _, _) = await factory.AddStaffAsync(owner, ["Supervisor"]);

        // Granting a role inside their own permissions is fine.
        (await supervisor.RawPostAsync("/api/staff", new CreateStaffRequest("New cashier", null, TestApi.UniquePhone(), [cashierRole.Id], true, []))).ShouldBe(HttpStatusCode.OK);

        // Manager and Owner hold permissions the supervisor doesn't.
        var managerRole = await owner.RoleIdAsync("Manager");
        var ownerRole = await owner.RoleIdAsync("Owner");
        (await supervisor.RawPostAsync("/api/staff", new CreateStaffRequest("X", null, TestApi.UniquePhone(), [managerRole], true, []))).ShouldBe(HttpStatusCode.Forbidden);
        (await supervisor.RawPostAsync("/api/staff", new CreateStaffRequest("X", null, TestApi.UniquePhone(), [ownerRole], true, []))).ShouldBe(HttpStatusCode.Forbidden);

        // Nor can they write a role with permissions they lack, or touch the Owner role.
        (await supervisor.RawPostAsync("/api/roles", new SaveRoleRequest("Sneaky", null, [Permissions.Reports.Financial]))).ShouldBe(HttpStatusCode.Forbidden);
        (await supervisor.RawPutAsync($"/api/roles/{supervisorRole.Id}", new SaveRoleRequest("Supervisor", null, [.. supervisorRole.Permissions, Permissions.Tenant.Manage]))).ShouldBe(HttpStatusCode.Forbidden);
        (await owner.RawPutAsync($"/api/roles/{ownerRole}", new SaveRoleRequest("Boss", null, []))).ShouldBe(HttpStatusCode.Forbidden);

        // Or change the owner's own access.
        var ownerMember = (await owner.GetAsync<List<StaffMemberDto>>("/api/staff")).Single(s => s.IsOwner);
        (await supervisor.RawPutAsync($"/api/staff/{ownerMember.MembershipId}", new UpdateStaffRequest("Owner", [cashierRole.Id], true, [], false))).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_removed()
    {
        var owner = await factory.RegisterOwnerAsync();
        var me = (await owner.GetAsync<List<StaffMemberDto>>("/api/staff")).Single(s => s.IsOwner);
        var manager = await owner.RoleIdAsync("Manager");

        (await owner.RawPutAsync($"/api/staff/{me.MembershipId}", new UpdateStaffRequest(me.DisplayName, me.RoleIds, true, [], true))).ShouldBe(HttpStatusCode.Conflict);
        (await owner.RawPutAsync($"/api/staff/{me.MembershipId}", new UpdateStaffRequest(me.DisplayName, [manager], true, [], false))).ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Disabling_a_member_ends_their_access_immediately()
    {
        var owner = await factory.RegisterOwnerAsync();
        var (cashier, member, _) = await factory.AddStaffAsync(owner, ["Cashier"]);
        (await cashier.RawGetAsync("/api/me")).ShouldBe(HttpStatusCode.OK);

        await owner.PutAsync<StaffMemberDto>($"/api/staff/{member.MembershipId}", new UpdateStaffRequest(member.DisplayName, member.RoleIds, true, [], true));

        // The still-valid access token is refused, and so is the refresh token.
        (await cashier.RawGetAsync("/api/me")).ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.AnonymousPostAsync("/api/auth/refresh", new RefreshTokenRequest(cashier.Auth.RefreshToken!))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Role_changes_apply_to_existing_sessions()
    {
        var owner = await factory.RegisterOwnerAsync();
        var (cashier, _, _) = await factory.AddStaffAsync(owner, ["Cashier"]);
        (await cashier.RawPostAsync("/api/tax-rates", Gst)).ShouldBe(HttpStatusCode.Forbidden);

        var cashierRole = (await owner.GetAsync<List<RoleDto>>("/api/roles")).Single(r => r.Name == "Cashier");
        await owner.PutAsync<RoleDto>($"/api/roles/{cashierRole.Id}", new SaveRoleRequest("Cashier", null, [.. cashierRole.Permissions, Permissions.Tenant.ManageSettings]));

        (await cashier.RawPostAsync("/api/tax-rates", Gst)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changes_are_audited_and_only_owners_can_read_the_log()
    {
        var owner = await factory.RegisterOwnerAsync();
        var rate = await owner.PostAsync<TaxRateDto>("/api/tax-rates", Gst);
        await owner.PutAsync<TaxRateDto>($"/api/tax-rates/{rate.Id}", Gst with { Percent = 15m });

        var log = await owner.GetAsync<Restaurant.Contracts.Common.PagedResult<Restaurant.Contracts.Audit.AuditLogDto>>("/api/audit?entityType=TaxRate");
        log.Items.Should().HaveCount(2);
        var update = log.Items.First();
        update.BeforeJson.Should().Contain("16");
        update.AfterJson.Should().Contain("15");
        update.ActorUserId.Should().NotBeNull();

        var (manager, _, _) = await factory.AddStaffAsync(owner, ["Manager"]);
        (await manager.RawGetAsync("/api/audit")).ShouldBe(HttpStatusCode.Forbidden);
    }
}
