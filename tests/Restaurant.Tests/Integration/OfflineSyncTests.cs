using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Inventory;
using Restaurant.Contracts.Kitchen;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Sessions;
using Restaurant.Contracts.Tenancy;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>Orders taken on a counter PC without the API, sent when it's back (Phase 6).</summary>
public class OfflineSyncTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<OfflineOrderRequest> OfflineAsync(PosSetup pos, decimal estimated = 1856, decimal? paid = 1856, Guid? method = null, Guid? table = null,
        Guid? sessionId = null)
    {
        var me = await pos.Owner.GetAsync<MeDto>("/api/me");
        sessionId ??= (await pos.Owner.GetAsync<BusinessSessionDto>("/api/sessions/current")).Id;
        var payments = paid is { } amount
            ? new List<OfflinePaymentRequest> { new(Guid.NewGuid(), method ?? pos.CashMethod, amount, method is null ? 2000 : null, null) }
            : [];
        return new OfflineOrderRequest(pos.StandardOrder(table: table), "K7-01", DateTimeOffset.UtcNow.AddMinutes(-20), me.UserId, sessionId.Value,
            estimated, payments, null);
    }

    [Fact]
    public async Task An_offline_order_is_booked_once_as_served_and_paid()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;
        var chicken = await owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Chicken", null, "g", null, 0, true, []));
        await owner.PutAsync<RecipeDto>("/api/inventory/recipes", new RecipeDto(pos.Burger, null, null, [new(chicken.Id, 150, "g")]));
        var request = await OfflineAsync(pos);

        var result = await owner.PostAsync<OfflineOrderResult>("/api/orders/offline", request);
        result.NeedsReview.Should().BeFalse();
        result.OrderNumber.Should().Be("001");

        var order = await owner.GetAsync<OrderDto>($"/api/orders/{result.OrderId}");
        order.OfflineNumber.Should().Be("K7-01");
        order.Status.Should().Be(OrderStatus.Completed);
        order.PaymentStatus.Should().Be(PaymentStatus.Paid);
        order.CreatedAt.Should().BeCloseTo(request.TakenAt, TimeSpan.FromSeconds(1));
        order.Payments.Single().Change.Should().Be(144);
        (await owner.GetAsync<List<KitchenTicketDto>>("/api/kitchen/tickets")).Should().BeEmpty(); // cooked from paper
        (await owner.GetAsync<List<StockLevelDto>>("/api/inventory/levels")).Single(l => l.ItemId == chicken.Id).OnHand.Should().Be(-300);

        // Sent again (the PC never saw the answer): nothing is booked twice.
        (await owner.PostAsync<OfflineOrderResult>("/api/orders/offline", request)).OrderId.Should().Be(result.OrderId);
        (await owner.GetAsync<OrderDto>($"/api/orders/{result.OrderId}")).Payments.Should().ContainSingle();
        (await owner.GetAsync<List<OrderSummaryDto>>("/api/orders?scope=today")).Should().ContainSingle();
        (await owner.GetAsync<ClosePreviewDto>("/api/sessions/current/close-preview")).Blockers.Should().BeEmpty();
    }

    [Fact]
    public async Task A_different_price_is_booked_and_flagged_and_a_manager_clears_it()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;

        var result = await owner.PostAsync<OfflineOrderResult>("/api/orders/offline", await OfflineAsync(pos, estimated: 1600, paid: 1600));
        result.NeedsReview.Should().BeTrue();
        result.ReviewNote.Should().Contain("1600").And.Contain("1856");
        var order = await owner.GetAsync<OrderDto>($"/api/orders/{result.OrderId}");
        order.Balance.Should().Be(256);
        order.Status.Should().Be(OrderStatus.Served);
        (await owner.GetAsync<List<OrderSummaryDto>>("/api/orders?scope=review")).Should().ContainSingle(o => o.Id == result.OrderId && o.NeedsReview);

        var (cashier, _, _) = await factory.AddStaffAsync(owner, ["Cashier"]);
        cashier.UseBranch(owner.BranchId);
        (await cashier.RawPostAsync($"/api/orders/{result.OrderId}/reviewed", new { })).ShouldBe(HttpStatusCode.Forbidden);
        (await owner.PostAsync<OrderDto>($"/api/orders/{result.OrderId}/reviewed", new { })).NeedsReview.Should().BeFalse();
        (await owner.GetAsync<List<OrderSummaryDto>>("/api/orders?scope=review")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_taken_table_a_closed_day_and_too_much_card_money_are_booked_and_flagged()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;

        // Someone seated table 1 online meanwhile.
        await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(table: pos.Table1));
        var tableResult = await owner.PostAsync<OfflineOrderResult>("/api/orders/offline", await OfflineAsync(pos, table: pos.Table1, paid: null));
        tableResult.NeedsReview.Should().BeTrue();
        tableResult.ReviewNote.Should().Contain("Table 1");
        (await owner.GetAsync<OrderDto>($"/api/orders/{tableResult.OrderId}")).TableId.Should().BeNull();

        // Card money above the server total is capped and flagged (cash would just be change).
        var card = await owner.PostAsync<OfflineOrderResult>("/api/orders/offline", await OfflineAsync(pos, estimated: 1856, paid: 2000, method: pos.CardMethod));
        card.NeedsReview.Should().BeTrue();
        (await owner.GetAsync<OrderDto>($"/api/orders/{card.OrderId}")).Paid.Should().Be(1856);

    }

    [Fact]
    public async Task An_order_from_a_day_closed_meanwhile_goes_to_todays_day_or_waits_for_one()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;

        // The day it was taken in was closed on another PC: it goes to today's day, flagged. With no day open it waits.
        var oldDay = (await owner.GetAsync<BusinessSessionDto>("/api/sessions/current")).Id;
        var late = await OfflineAsync(pos, sessionId: oldDay);
        var register = (await owner.GetAsync<ClosePreviewDto>("/api/sessions/current/close-preview")).Summary.Cash.Single();
        await owner.PostAsync<ClosedSessionDto>("/api/sessions/current/close", new CloseSessionRequest([new(register.RegisterSessionId, register.ExpectedCash, null)], null));
        (await owner.RawPostAsync("/api/orders/offline", late)).ShouldBe(HttpStatusCode.Conflict);

        var today = await owner.PostAsync<BusinessSessionDto>("/api/sessions/start", new StartSessionRequest(1000, null));
        var booked = await owner.PostAsync<OfflineOrderResult>("/api/orders/offline", late);
        booked.NeedsReview.Should().BeTrue();
        booked.ReviewNote.Should().Contain("closed");
        (await owner.GetAsync<OrderDto>($"/api/orders/{booked.OrderId}")).BusinessSessionId.Should().Be(today.Id);
    }

    [Fact]
    public async Task Food_already_served_is_booked_even_when_the_branch_blocks_short_stock()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;
        var branch = await owner.GetAsync<BranchDto>($"/api/branches/{owner.BranchId}");
        await owner.PutAsync<BranchDto>($"/api/branches/{owner.BranchId}/settings", branch.Settings with { InsufficientStock = InsufficientStockBehavior.Block });
        var chicken = await owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Chicken", null, "g", null, 0, true, []));
        await owner.PutAsync<RecipeDto>("/api/inventory/recipes", new RecipeDto(pos.Burger, null, null, [new(chicken.Id, 150, "g")]));

        (await owner.RawPostAsync("/api/orders", pos.StandardOrder())).ShouldBe(HttpStatusCode.Conflict); // online it's refused
        var result = await owner.PostAsync<OfflineOrderResult>("/api/orders/offline", await OfflineAsync(pos));
        (await owner.GetAsync<OrderDto>($"/api/orders/{result.OrderId}")).Status.Should().Be(OrderStatus.Completed);

        // A bad clock is refused rather than booked to the wrong day.
        var skewed = await OfflineAsync(pos) with { TakenAt = DateTimeOffset.UtcNow.AddHours(3) };
        (await owner.RawPostAsync("/api/orders/offline", skewed)).ShouldBe(HttpStatusCode.BadRequest);
    }
}
