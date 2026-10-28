using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Staff;

namespace Restaurant.Tests.Integration.Infrastructure;

public static class StaffHelper
{
    public static async Task<Guid> RoleIdAsync(this Caller owner, string roleName) =>
        (await owner.GetAsync<List<RoleDto>>("/api/roles")).Single(r => r.Name == roleName).Id;

    /// <summary>Invites a staff member with the given roles and branches, accepts the invite, returns their signed-in caller.</summary>
    public static async Task<(Caller Caller, StaffMemberDto Member, string Email)> AddStaffAsync(
        this ApiFactory factory, Caller owner, string[] roleNames, Guid[]? branchIds = null)
    {
        var roleIds = new List<Guid>();
        foreach (var name in roleNames)
        {
            roleIds.Add(await owner.RoleIdAsync(name));
        }

        var email = TestApi.UniqueEmail("staff");
        var invite = await owner.PostAsync<StaffInviteDto>("/api/staff", new CreateStaffRequest(
            "Staff " + Guid.NewGuid().ToString("N")[..6], email, null, roleIds, branchIds is null, branchIds ?? []));

        var auth = await TestApi.ReadAsync<AuthResultDto>(factory.CreateClient().PostAsync(
            "/api/auth/accept-invite", System.Net.Http.Json.JsonContent.Create(new AcceptInviteRequest(invite.InviteCode, TestApi.Password), options: TestApi.Json)));
        return (factory.CallerFor(auth), invite.Member, email);
    }
}
