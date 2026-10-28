using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Staff;
using Restaurant.Contracts.Tenancy;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

public class InviteAndPinTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Invited_staff_set_a_password_once_and_sign_in_with_their_mobile()
    {
        var owner = await factory.RegisterOwnerAsync();
        var phone = TestApi.UniquePhone();
        var invite = await owner.PostAsync<StaffInviteDto>("/api/staff", new CreateStaffRequest(
            "Usman", null, phone, [await owner.RoleIdAsync("Waiter")], true, []));
        invite.Member.Status.Should().Be(Restaurant.Contracts.Enums.MembershipStatus.Invited);

        // Not active yet.
        (await factory.AnonymousPostAsync("/api/auth/login", new LoginRequest(phone, TestApi.Password))).ShouldBe(HttpStatusCode.Unauthorized);

        var accepted = await TestApi.ReadAsync<AuthResultDto>(factory.AnonymousPostAsync("/api/auth/accept-invite",
            new AcceptInviteRequest(invite.InviteCode.ToLowerInvariant().Replace("-", " "), TestApi.Password)));
        accepted.TenantId.Should().Be(owner.TenantId);

        // Single use.
        (await factory.AnonymousPostAsync("/api/auth/accept-invite", new AcceptInviteRequest(invite.InviteCode, "another-pass-1"))).ShouldBe(HttpStatusCode.Unauthorized);

        (await factory.LoginAsync(phone)).TenantId.Should().Be(owner.TenantId);
    }

    [Fact]
    public async Task A_new_invite_lets_the_owner_reset_a_staff_password()
    {
        var owner = await factory.RegisterOwnerAsync();
        var (_, member, email) = await factory.AddStaffAsync(owner, ["Cashier"]);

        var invite = await owner.PostAsync<StaffInviteDto>($"/api/staff/{member.MembershipId}/invite", new { });
        await TestApi.ReadAsync<AuthResultDto>(factory.AnonymousPostAsync("/api/auth/accept-invite", new AcceptInviteRequest(invite.InviteCode, "brand-new-pass")));

        (await factory.AnonymousPostAsync("/api/auth/login", new LoginRequest(email, TestApi.Password))).ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.LoginAsync(email, "brand-new-pass")).TenantId.Should().Be(owner.TenantId);
    }

    [Fact]
    public async Task Pin_sign_in_works_only_on_a_registered_device()
    {
        var owner = await factory.RegisterOwnerAsync();
        var (cashier, member, _) = await factory.AddStaffAsync(owner, ["Cashier"]);
        var device = await owner.PostAsync<RegisterDeviceResult>("/api/devices", new RegisterDeviceRequest(owner.BranchId, null, "Front counter"));

        (await cashier.RawPutAsync("/api/me/pin", new SetPinRequest(TestApi.Password, "12ab"))).ShouldBe(HttpStatusCode.BadRequest);
        (await cashier.RawPutAsync("/api/me/pin", new SetPinRequest("wrong-password", "4321"))).ShouldBe(HttpStatusCode.BadRequest);
        (await cashier.RawPutAsync("/api/me/pin", new SetPinRequest(TestApi.Password, "4321"))).ShouldBe(HttpStatusCode.NoContent);

        var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Device-Token", device.DeviceToken);
        var staff = await TestApi.ReadAsync<List<DeviceStaffDto>>(anonymous.GetAsync("/api/auth/device-staff"));
        staff.Should().ContainSingle(s => s.MembershipId == member.MembershipId);

        var session = await TestApi.ReadAsync<AuthResultDto>(anonymous.PostAsJsonAsync("/api/auth/pin-login",
            new PinLoginRequest(device.DeviceToken, member.MembershipId, "4321"), TestApi.Json));
        session.DefaultBranchId.Should().Be(owner.BranchId);
        (await factory.CallerFor(session).GetAsync<MeDto>("/api/me")).MembershipId.Should().Be(member.MembershipId);

        // A made-up device token gets nothing.
        (await factory.AnonymousPostAsync("/api/auth/pin-login", new PinLoginRequest("not-a-device", member.MembershipId, "4321"))).ShouldBe(HttpStatusCode.Unauthorized);

        // Revoking the device ends PIN sign-in and the PIN sessions started on it.
        (await owner.RawDeleteAsync($"/api/devices/{device.Device.Id}")).ShouldBe(HttpStatusCode.NoContent);
        (await factory.AnonymousPostAsync("/api/auth/pin-login", new PinLoginRequest(device.DeviceToken, member.MembershipId, "4321"))).ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.AnonymousPostAsync("/api/auth/refresh", new RefreshTokenRequest(session.RefreshToken!))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_wrong_pins_lock_pin_sign_in()
    {
        var owner = await factory.RegisterOwnerAsync();
        var (cashier, member, _) = await factory.AddStaffAsync(owner, ["Cashier"]);
        await cashier.RawPutAsync("/api/me/pin", new SetPinRequest(TestApi.Password, "2468"));
        var device = await owner.PostAsync<RegisterDeviceResult>("/api/devices", new RegisterDeviceRequest(owner.BranchId, null, "Counter"));

        for (var i = 0; i < 5; i++)
        {
            (await factory.AnonymousPostAsync("/api/auth/pin-login", new PinLoginRequest(device.DeviceToken, member.MembershipId, "0000"))).ShouldBe(HttpStatusCode.Unauthorized);
        }
        (await factory.AnonymousPostAsync("/api/auth/pin-login", new PinLoginRequest(device.DeviceToken, member.MembershipId, "2468"))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Staff_without_access_to_the_device_branch_cannot_use_its_pin_sign_in()
    {
        var owner = await factory.RegisterOwnerAsync();
        var other = await owner.PostAsync<BranchDto>("/api/branches", new SaveBranchRequest("Clifton", null, "Karachi", null, "C"));
        var (cashier, member, _) = await factory.AddStaffAsync(owner, ["Cashier"], [owner.BranchId]);
        await cashier.RawPutAsync("/api/me/pin", new SetPinRequest(TestApi.Password, "1357"));
        var device = await owner.PostAsync<RegisterDeviceResult>("/api/devices", new RegisterDeviceRequest(other.Id, null, "Clifton counter"));

        (await factory.AnonymousPostAsync("/api/auth/pin-login", new PinLoginRequest(device.DeviceToken, member.MembershipId, "1357"))).ShouldBe(HttpStatusCode.Unauthorized);
    }
}
