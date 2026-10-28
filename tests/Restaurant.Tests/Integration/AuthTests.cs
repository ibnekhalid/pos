using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Staff;
using Restaurant.Contracts.Tenancy;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

public class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Owner_sign_up_creates_the_restaurant_and_signs_in()
    {
        var owner = await factory.RegisterOwnerAsync("Karachi Grill");

        var me = await owner.GetAsync<MeDto>("/api/me");
        me.TenantName.Should().Be("Karachi Grill");
        me.IsOwner.Should().BeTrue();
        me.Permissions.Should().BeEquivalentTo(Permissions.All);
        me.Branches.Should().ContainSingle().Which.Id.Should().Be(owner.BranchId);

        var registers = await owner.GetAsync<List<RegisterDto>>($"/api/branches/{owner.BranchId}/registers");
        registers.Should().ContainSingle(r => r.IsDefault);

        var roles = await owner.GetAsync<List<RoleDto>>("/api/roles");
        roles.Select(r => r.Name).Should().Contain(["Owner", "Manager", "Cashier", "Waiter", "Kitchen staff", "Inventory staff"]);

        var methods = await owner.GetAsync<List<PaymentMethodDto>>("/api/payment-methods");
        methods.Should().ContainSingle(m => m.IsCash);
    }

    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        var email = TestApi.UniqueEmail();
        await factory.RegisterOwnerAsync(email: email);

        var again = await factory.AnonymousPostAsync("/api/auth/register-owner",
            new RegisterOwnerRequest("Someone", email.ToUpperInvariant(), null, TestApi.Password, "Another", null, null, null));
        again.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Sign_in_works_with_email_or_mobile_number()
    {
        var email = TestApi.UniqueEmail();
        var phone = TestApi.UniquePhone();
        await factory.RegisterOwnerAsync(email: email, phone: phone);

        (await factory.LoginAsync(email.ToUpperInvariant())).TenantId.Should().NotBeNull();
        // 03001234567 → +923001234567
        (await factory.LoginAsync("+92 " + phone[1..4] + " " + phone[4..])).TenantId.Should().NotBeNull();
    }

    [Fact]
    public async Task Wrong_password_is_401_and_five_failures_lock_the_account()
    {
        var email = TestApi.UniqueEmail();
        await factory.RegisterOwnerAsync(email: email);

        for (var i = 0; i < 5; i++)
        {
            (await factory.AnonymousPostAsync("/api/auth/login", new LoginRequest(email, "wrong-password"))).ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Locked: even the right password is refused for now.
        (await factory.AnonymousPostAsync("/api/auth/login", new LoginRequest(email, TestApi.Password))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reuse_ends_every_session()
    {
        var owner = await factory.RegisterOwnerAsync();
        var first = owner.Auth.RefreshToken!;

        var second = await TestApi.ReadAsync<AuthResultDto>(owner.Http.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(first), TestApi.Json));
        second.RefreshToken.Should().NotBe(first);

        // The rotated token presented again = stolen copy: refused, and the newer session ends too.
        (await factory.AnonymousPostAsync("/api/auth/refresh", new RefreshTokenRequest(first))).ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.AnonymousPostAsync("/api/auth/refresh", new RefreshTokenRequest(second.RefreshToken!))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var owner = await factory.RegisterOwnerAsync();
        (await factory.AnonymousPostAsync("/api/auth/logout", new RefreshTokenRequest(owner.Auth.RefreshToken!))).ShouldBe(HttpStatusCode.NoContent);
        (await factory.AnonymousPostAsync("/api/auth/refresh", new RefreshTokenRequest(owner.Auth.RefreshToken!))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Change_password_replaces_the_old_one()
    {
        var email = TestApi.UniqueEmail();
        var owner = await factory.RegisterOwnerAsync(email: email);

        (await owner.RawPostAsync("/api/auth/change-password", new ChangePasswordRequest("not-it", "new-password-1"))).ShouldBe(HttpStatusCode.BadRequest);
        (await owner.RawPostAsync("/api/auth/change-password", new ChangePasswordRequest(TestApi.Password, "new-password-1"))).ShouldBe(HttpStatusCode.NoContent);

        (await factory.AnonymousPostAsync("/api/auth/login", new LoginRequest(email, TestApi.Password))).ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.LoginAsync(email, "new-password-1")).TenantId.Should().Be(owner.TenantId);
    }

    [Fact]
    public async Task Someone_in_two_restaurants_chooses_one_after_sign_in()
    {
        var email = TestApi.UniqueEmail();
        var restaurantA = await factory.RegisterOwnerAsync(email: email);
        var restaurantB = await factory.RegisterOwnerAsync();

        // B invites A's owner as a manager; they confirm with their existing password.
        var invite = await restaurantB.PostAsync<StaffInviteDto>("/api/staff", new CreateStaffRequest(
            "Ali", email, null, [await restaurantB.RoleIdAsync("Manager")], true, []));
        (await factory.AnonymousPostAsync("/api/auth/accept-invite", new AcceptInviteRequest(invite.InviteCode, "wrong-password-1"))).ShouldBe(HttpStatusCode.Unauthorized);
        await TestApi.ReadAsync<AuthResultDto>(factory.AnonymousPostAsync("/api/auth/accept-invite", new AcceptInviteRequest(invite.InviteCode, TestApi.Password)));

        var login = await factory.LoginAsync(email);
        login.RequiresTenantSelection.Should().BeTrue();
        login.RefreshToken.Should().BeNull();
        login.Tenants.Select(t => t.TenantId).Should().BeEquivalentTo([restaurantA.TenantId, restaurantB.TenantId]);

        var pending = factory.CallerFor(login);
        (await pending.RawGetAsync("/api/tenant")).ShouldBe(HttpStatusCode.Forbidden);

        var selected = await pending.PostAsync<AuthResultDto>("/api/auth/select-tenant", new SelectTenantRequest(restaurantB.TenantId));
        pending.Use(selected);
        (await pending.GetAsync<TenantDto>("/api/tenant")).Id.Should().Be(restaurantB.TenantId);
        (await pending.GetAsync<MeDto>("/api/me")).IsOwner.Should().BeFalse();
    }
}
