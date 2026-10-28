using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Sessions;
using Restaurant.Contracts.Tenancy;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>A counter PC set to its own till: it finds its register, and its cash goes to that drawer.</summary>
public class DeviceTillTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_counter_pc_knows_its_till_and_books_cash_to_that_drawer()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;
        var till2 = await owner.PostAsync<RegisterDto>($"/api/branches/{owner.BranchId}/registers",
            new SaveRegisterRequest("Till 2", false, true, "Receipt-2", null, true));
        var registered = await owner.PostAsync<RegisterDeviceResult>("/api/devices", new RegisterDeviceRequest(owner.BranchId, null, "Counter 2"));

        var device = await owner.PutAsync<DeviceDto>($"/api/devices/{registered.Device.Id}", new UpdateDeviceRequest("Counter 2 (front)", till2.Id));
        device.RegisterId.Should().Be(till2.Id);
        device.Name.Should().Be("Counter 2 (front)");

        // The PC reads its own record with its token; no token, or another restaurant's caller, gets nothing.
        owner.Http.DefaultRequestHeaders.Add("X-Device-Token", registered.DeviceToken);
        (await owner.GetAsync<DeviceDto>("/api/devices/current")).RegisterId.Should().Be(till2.Id);
        owner.Http.DefaultRequestHeaders.Remove("X-Device-Token");
        (await owner.RawGetAsync("/api/devices/current")).ShouldBe(HttpStatusCode.NoContent);
        var other = await factory.SetUpRestaurantAsync(startDay: false);
        other.Owner.Http.DefaultRequestHeaders.Add("X-Device-Token", registered.DeviceToken);
        (await other.Owner.RawGetAsync("/api/devices/current")).ShouldBe(HttpStatusCode.NoContent);
        (await other.Owner.RawPutAsync($"/api/devices/{registered.Device.Id}", new UpdateDeviceRequest("x", null))).ShouldBe(HttpStatusCode.NotFound);

        // Another branch's register can't be picked.
        (await owner.RawPutAsync($"/api/devices/{registered.Device.Id}", new UpdateDeviceRequest("Counter 2", Guid.NewGuid()))).ShouldBe(HttpStatusCode.NotFound);

        // Till 2 must be opened before it takes cash; then sales and refunds land in its drawer.
        var order = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());
        var pay = new TakePaymentRequest(Guid.NewGuid(), pos.CashMethod, 1856, 2000, null, till2.Id);
        (await owner.RawPostAsync($"/api/orders/{order.Id}/payments", pay)).ShouldBe(HttpStatusCode.Conflict);
        await owner.PostAsync<BusinessSessionDto>("/api/sessions/current/registers", new OpenRegisterRequest(till2.Id, 1000));
        var paid = await owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/payments", pay);
        await owner.PostAsync<OrderDto>($"/api/payments/{paid.Payments[0].Id}/refunds", new RefundRequest(Guid.NewGuid(), 56, "Short a drink", null, till2.Id));

        var cash = (await owner.GetAsync<ClosePreviewDto>("/api/sessions/current/close-preview")).Summary.Cash;
        cash.Single(c => c.RegisterId == till2.Id).Should().Match<RegisterCashDto>(c => c.CashSales == 1856 && c.CashRefunds == 56 && c.ExpectedCash == 1000 + 1856 - 56);
        cash.Single(c => c.RegisterId != till2.Id).CashSales.Should().Be(0);
    }
}
