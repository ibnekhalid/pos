using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Tenancy;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>T2: staff only reach the branches they are assigned to.</summary>
public class BranchAccessTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Staff_assigned_to_one_branch_cannot_use_another()
    {
        var owner = await factory.RegisterOwnerAsync();
        var mainBranch = owner.BranchId;
        var secondBranch = await owner.PostAsync<BranchDto>("/api/branches", new SaveBranchRequest("Gulberg", "Main Boulevard", "Lahore", null, "G"));

        var (manager, _, _) = await factory.AddStaffAsync(owner, ["Manager"], [mainBranch]);
        manager.Auth.DefaultBranchId.Should().Be(mainBranch);

        (await manager.GetAsync<List<BranchDto>>("/api/branches")).Select(b => b.Id).Should().Equal(mainBranch);
        (await manager.GetAsync<MeDto>("/api/me")).Branches.Select(b => b.Id).Should().Equal(mainBranch);

        (await manager.RawGetAsync($"/api/branches/{secondBranch.Id}")).ShouldBe(HttpStatusCode.Forbidden);
        (await manager.RawGetAsync($"/api/branches/{secondBranch.Id}/registers")).ShouldBe(HttpStatusCode.Forbidden);

        manager.UseBranch(secondBranch.Id);
        (await manager.RawGetAsync("/api/branches/current")).ShouldBe(HttpStatusCode.Forbidden);

        manager.UseBranch(mainBranch);
        (await manager.GetAsync<BranchDto>("/api/branches/current")).Id.Should().Be(mainBranch);

        // The owner has every branch, including the new one.
        owner.UseBranch(secondBranch.Id);
        (await owner.GetAsync<BranchDto>("/api/branches/current")).Id.Should().Be(secondBranch.Id);
        (await owner.GetAsync<List<BranchDto>>("/api/branches")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Branch_access_changes_apply_to_existing_sessions()
    {
        var owner = await factory.RegisterOwnerAsync();
        var second = await owner.PostAsync<BranchDto>("/api/branches", new SaveBranchRequest("DHA", null, null, null, "D"));
        var (cashier, member, _) = await factory.AddStaffAsync(owner, ["Cashier"], [owner.BranchId]);

        cashier.UseBranch(second.Id);
        (await cashier.RawGetAsync("/api/branches/current")).ShouldBe(HttpStatusCode.Forbidden);

        await owner.PutAsync<object>($"/api/staff/{member.MembershipId}", new Restaurant.Contracts.Staff.UpdateStaffRequest(
            member.DisplayName, member.RoleIds, false, [owner.BranchId, second.Id], false));

        (await cashier.GetAsync<BranchDto>("/api/branches/current")).Id.Should().Be(second.Id);
    }

    [Fact]
    public async Task Branch_settings_are_validated_and_saved()
    {
        var owner = await factory.RegisterOwnerAsync();
        var settings = (await owner.GetAsync<BranchDto>($"/api/branches/{owner.BranchId}")).Settings;
        settings.TimeZoneId.Should().Be("Asia/Karachi");

        (await owner.RawPutAsync($"/api/branches/{owner.BranchId}/settings", settings with { TimeZoneId = "Mars/Olympus" })).ShouldBe(HttpStatusCode.BadRequest);
        (await owner.RawPutAsync($"/api/branches/{owner.BranchId}/settings", settings with { DayStartHour = 24 })).ShouldBe(HttpStatusCode.BadRequest);

        var saved = await owner.PutAsync<BranchDto>($"/api/branches/{owner.BranchId}/settings",
            settings with { DayStartHour = 5, ServiceChargePercent = 10m, Rounding = Restaurant.Contracts.Enums.RoundingMode.NearestRupee });
        saved.Settings.DayStartHour.Should().Be(5);
        saved.Settings.ServiceChargePercent.Should().Be(10m);
    }

    [Fact]
    public async Task A_branch_always_keeps_one_default_register()
    {
        var owner = await factory.RegisterOwnerAsync();
        var url = $"/api/branches/{owner.BranchId}/registers";
        var original = (await owner.GetAsync<List<RegisterDto>>(url)).Single();

        (await owner.RawPutAsync($"{url}/{original.Id}", new SaveRegisterRequest("Counter 1", false, true, null, null, false))).ShouldBe(HttpStatusCode.BadRequest);

        var second = await owner.PostAsync<RegisterDto>(url, new SaveRegisterRequest("Counter 2", true, true, "EPSON TM-T88", null, true));
        var registers = await owner.GetAsync<List<RegisterDto>>(url);
        registers.Should().ContainSingle(r => r.IsDefault).Which.Id.Should().Be(second.Id);
    }
}
