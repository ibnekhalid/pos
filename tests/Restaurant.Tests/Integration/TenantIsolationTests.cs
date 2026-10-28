using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Security;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Staff;
using Restaurant.Contracts.Tenancy;
using Restaurant.Domain.Entities;
using Restaurant.Infrastructure.Persistence;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>T1: one restaurant can't read or change another's data, through the API or the DbContext.</summary>
public class TenantIsolationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Another_restaurants_data_is_invisible_and_unchangeable_through_the_api()
    {
        var a = await factory.RegisterOwnerAsync();
        var b = await factory.RegisterOwnerAsync();

        var taxRate = await a.PostAsync<TaxRateDto>("/api/tax-rates", new SaveTaxRateRequest("GST", 16m, false, OrderTypes.All, TaxPaymentScope.Any, null));
        var method = (await a.GetAsync<List<PaymentMethodDto>>("/api/payment-methods")).First();
        var role = await a.PostAsync<RoleDto>("/api/roles", new SaveRoleRequest("Shift lead", null, ["orders.create"]));
        var invite = await a.PostAsync<StaffInviteDto>("/api/staff", new CreateStaffRequest("Bilal", null, TestApi.UniquePhone(), [role.Id], true, []));
        var device = await a.PostAsync<RegisterDeviceResult>("/api/devices", new RegisterDeviceRequest(a.BranchId, null, "Counter PC"));

        // Reads by id: not found.
        (await b.RawGetAsync($"/api/branches/{a.BranchId}")).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawGetAsync($"/api/branches/{a.BranchId}/registers")).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawGetAsync($"/api/staff/{invite.Member.MembershipId}")).ShouldBe(HttpStatusCode.NotFound);

        // Writes by id: not found.
        (await b.RawPutAsync($"/api/tax-rates/{taxRate.Id}", new SaveTaxRateRequest("Hacked", 0m, false, OrderTypes.All, TaxPaymentScope.Any, null))).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawPutAsync($"/api/payment-methods/{method.Id}", new SavePaymentMethodRequest("Hacked", PaymentMethodKind.Other, false, 0))).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawPutAsync($"/api/roles/{role.Id}", new SaveRoleRequest("Hacked", null, []))).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawDeleteAsync($"/api/roles/{role.Id}")).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawPutAsync($"/api/staff/{invite.Member.MembershipId}", new UpdateStaffRequest("Hacked", [role.Id], true, [], true))).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawPostAsync($"/api/staff/{invite.Member.MembershipId}/invite", new { })).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawPutAsync($"/api/branches/{a.BranchId}", new SaveBranchRequest("Hacked", null, null, null, ""))).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawDeleteAsync($"/api/devices/{device.Device.Id}")).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawPostAsync("/api/devices", new RegisterDeviceRequest(a.BranchId, null, "Sneaky"))).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawPostAsync($"/api/branches/{a.BranchId}/registers", new SaveRegisterRequest("Sneaky", false, true, null, null, false))).ShouldBe(HttpStatusCode.NotFound);

        // Using A's role or branch inside B.
        var bRole = await b.RoleIdAsync("Cashier");
        (await b.RawPostAsync("/api/staff", new CreateStaffRequest("Mixed", null, TestApi.UniquePhone(), [role.Id], true, []))).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawPostAsync("/api/staff", new CreateStaffRequest("Mixed", null, TestApi.UniquePhone(), [bRole], false, [a.BranchId]))).ShouldBe(HttpStatusCode.NotFound);
        (await b.RawPostAsync("/api/tax-rates", new SaveTaxRateRequest("X", 1m, false, OrderTypes.All, TaxPaymentScope.Any, a.BranchId))).ShouldBe(HttpStatusCode.NotFound);

        // Selecting A's branch with B's token.
        b.UseBranch(a.BranchId);
        (await b.RawGetAsync("/api/tenant")).ShouldBe(HttpStatusCode.Forbidden);
        b.UseBranch(null);

        // Lists only show B's own data.
        (await b.GetAsync<List<TaxRateDto>>("/api/tax-rates")).Should().NotContain(t => t.Id == taxRate.Id);
        (await b.GetAsync<List<PaymentMethodDto>>("/api/payment-methods")).Should().NotContain(m => m.Id == method.Id);
        (await b.GetAsync<List<RoleDto>>("/api/roles")).Should().NotContain(r => r.Id == role.Id);
        (await b.GetAsync<List<StaffMemberDto>>("/api/staff")).Should().NotContain(s => s.MembershipId == invite.Member.MembershipId);
        (await b.GetAsync<List<BranchDto>>("/api/branches")).Should().NotContain(x => x.Id == a.BranchId);
        (await b.GetAsync<List<DeviceDto>>("/api/devices")).Should().BeEmpty();

        // And nothing of A's changed.
        (await a.GetAsync<List<TaxRateDto>>("/api/tax-rates")).Should().ContainSingle(t => t.Id == taxRate.Id && t.Name == "GST" && t.Percent == 16m);
        (await a.GetAsync<List<RoleDto>>("/api/roles")).Should().ContainSingle(r => r.Id == role.Id && r.Name == "Shift lead");
        (await a.GetAsync<StaffMemberDto>($"/api/staff/{invite.Member.MembershipId}")).Status.Should().Be(MembershipStatus.Invited);
        (await a.GetAsync<List<DeviceDto>>("/api/devices")).Should().ContainSingle(d => !d.IsRevoked);
    }

    [Fact]
    public async Task The_save_guard_rejects_writes_to_another_restaurants_rows()
    {
        var a = await factory.RegisterOwnerAsync();
        var b = await factory.RegisterOwnerAsync();
        var taxRate = await a.PostAsync<TaxRateDto>("/api/tax-rates", new SaveTaxRateRequest("GST", 16m, false, OrderTypes.All, TaxPaymentScope.Any, null));

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<TenantContext>();
        var access = await services.GetRequiredService<MembershipAccessService>().GetAsync(b.TenantId, b.Auth.Tenants.Single().MembershipId);
        context.SetMembership(access!);
        var db = services.GetRequiredService<RestaurantDbContext>();

        // The query filter hides A's row from B...
        (await db.TaxRates.AnyAsync(t => t.Id == taxRate.Id)).Should().BeFalse();

        // ...and even bypassing the filter, saving a change to it fails.
        var row = await db.TaxRates.IgnoreQueryFilters().SingleAsync(t => t.Id == taxRate.Id);
        row.Percent = 0m;
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<TenantIsolationException>();
        db.ChangeTracker.Clear();

        db.TaxRates.Add(new TaxRate { TenantId = a.TenantId, Name = "Planted", Percent = 1m });
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<TenantIsolationException>();
        db.ChangeTracker.Clear();

        var own = await db.TaxRates.IgnoreQueryFilters().SingleAsync(t => t.Id == taxRate.Id);
        db.TaxRates.Remove(own);
        await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<TenantIsolationException>();
        db.ChangeTracker.Clear();

        // A row added without a tenant gets the current one.
        var stamped = new TaxRate { Name = "Stamped", Percent = 2m };
        db.TaxRates.Add(stamped);
        await db.SaveChangesAsync();
        stamped.TenantId.Should().Be(b.TenantId);
    }
}
