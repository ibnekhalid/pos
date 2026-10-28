using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Inventory;
using Restaurant.Contracts.Kitchen;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Sessions;
using Restaurant.Domain.Entities;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>T15: a failure part-way through an operation leaves nothing behind, and the retry then works once.</summary>
public class TransactionFailureTests(FaultApiFactory factory) : IClassFixture<FaultApiFactory>
{
    private async Task<(PosSetup Pos, Guid Chicken)> SetUpAsync()
    {
        factory.Faults.FailAfterInsertInto = null;
        var pos = await factory.SetUpRestaurantAsync();
        var chicken = await pos.Owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Chicken", null, "g", null, 0, true, []));
        await pos.Owner.PutAsync<RecipeDto>("/api/inventory/recipes", new RecipeDto(pos.Burger, null, null, [new(chicken.Id, 150, "g")]));
        return (pos, chicken.Id);
    }

    private Task<int> CountAsync<T>(Guid tenantId) where T : Restaurant.Domain.Common.TenantEntity =>
        factory.CountAsync<T>(q => q.Where(e => e.TenantId == tenantId));

    [Fact]
    public async Task A_failed_send_to_kitchen_leaves_no_order_lines_tickets_or_stock_and_the_retry_succeeds()
    {
        var (pos, _) = await SetUpAsync();
        var tenantId = pos.Owner.TenantId;
        var request = pos.StandardOrder();

        factory.Faults.FailAfterInsertInto = "KitchenTickets";
        (await pos.Owner.RawPostAsync("/api/orders", request)).ShouldBe(HttpStatusCode.InternalServerError);
        factory.Faults.FailAfterInsertInto = null;

        (await CountAsync<Order>(tenantId)).Should().Be(0);
        (await CountAsync<OrderLine>(tenantId)).Should().Be(0);
        (await CountAsync<KitchenTicket>(tenantId)).Should().Be(0);
        (await CountAsync<StockMovement>(tenantId)).Should().Be(0);

        // The retry is the first order: the order number wasn't used up either.
        var order = await pos.Owner.PostAsync<OrderDto>("/api/orders", request);
        order.OrderNumber.Should().Be("001");
        (await CountAsync<Order>(tenantId)).Should().Be(1);
        (await CountAsync<KitchenTicket>(tenantId)).Should().Be(2);
        (await CountAsync<StockMovement>(tenantId)).Should().Be(1);
    }

    [Fact]
    public async Task A_failed_payment_books_no_payment_or_cash()
    {
        var (pos, _) = await SetUpAsync();
        var tenantId = pos.Owner.TenantId;
        var order = await pos.Owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());
        var cashBefore = await CountAsync<CashMovement>(tenantId);
        var payment = new TakePaymentRequest(Guid.NewGuid(), pos.CashMethod, 1856, 2000, null);

        factory.Faults.FailAfterInsertInto = "CashMovements";
        (await pos.Owner.RawPostAsync($"/api/orders/{order.Id}/payments", payment)).ShouldBe(HttpStatusCode.InternalServerError);
        factory.Faults.FailAfterInsertInto = null;

        (await CountAsync<Payment>(tenantId)).Should().Be(0);
        (await CountAsync<CashMovement>(tenantId)).Should().Be(cashBefore);
        (await pos.Owner.GetAsync<OrderDto>($"/api/orders/{order.Id}")).Should().Match<OrderDto>(o => o.Paid == 0 && o.PaymentStatus == PaymentStatus.Unpaid);

        (await pos.Owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/payments", payment)).PaymentStatus.Should().Be(PaymentStatus.Paid);
        (await pos.Owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/payments", payment)).Payments.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_stock_receipt_and_a_failed_close_change_nothing()
    {
        var (pos, chicken) = await SetUpAsync();
        var owner = pos.Owner;
        var tenantId = owner.TenantId;

        factory.Faults.FailAfterInsertInto = "StockMovements";
        (await owner.RawPostAsync("/api/inventory/receipts", new CreateStockReceiptRequest(Guid.NewGuid(), null, null, null, [new(chicken, 2, "kg", 900)])))
            .ShouldBe(HttpStatusCode.InternalServerError);
        factory.Faults.FailAfterInsertInto = null;
        (await CountAsync<StockReceipt>(tenantId)).Should().Be(0);
        (await owner.GetAsync<List<StockLevelDto>>("/api/inventory/levels")).Single(l => l.ItemId == chicken)
            .Should().Match<StockLevelDto>(l => l.OnHand == 0 && l.AverageCost == 0);

        var preview = await owner.GetAsync<ClosePreviewDto>("/api/sessions/current/close-preview");
        var register = preview.Summary.Cash.Single();
        factory.Faults.FailAfterInsertInto = "AuditLogs";
        (await owner.RawPostAsync("/api/sessions/current/close", new CloseSessionRequest([new(register.RegisterSessionId, register.ExpectedCash, null)], null)))
            .ShouldBe(HttpStatusCode.InternalServerError);
        factory.Faults.FailAfterInsertInto = null;

        var current = await owner.GetAsync<BusinessSessionDto>("/api/sessions/current");
        current.Status.Should().Be(SessionStatus.Open);
        current.Registers.Single().Status.Should().Be(SessionStatus.Open);
    }
}

/// <summary>Several counters doing the same thing at the same moment.</summary>
public class ConcurrencyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Orders_placed_at_the_same_moment_get_distinct_consecutive_numbers()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => pos.Owner.RawPostAsync("/api/orders", pos.StandardOrder())));
        results.Should().OnlyContain(r => r.IsSuccessStatusCode);

        var orders = await pos.Owner.GetAsync<List<OrderSummaryDto>>("/api/orders");
        orders.Select(o => o.OrderNumber).Should().BeEquivalentTo(Enumerable.Range(1, 10).Select(n => n.ToString("000")));
    }

    [Fact]
    public async Task Two_cashiers_paying_the_same_bill_at_once_take_it_only_once()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var order = await pos.Owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            pos.Owner.RawPostAsync($"/api/orders/{order.Id}/payments", new TakePaymentRequest(Guid.NewGuid(), pos.CardMethod, 1856, null, null))));
        results.Should().NotContain(r => r.StatusCode == HttpStatusCode.InternalServerError);
        results.Count(r => r.IsSuccessStatusCode).Should().BeGreaterThanOrEqualTo(1);

        var after = await pos.Owner.GetAsync<OrderDto>($"/api/orders/{order.Id}");
        after.Paid.Should().Be(1856);
        after.Payments.Should().ContainSingle();
    }

    [Fact]
    public async Task Simultaneous_voids_reverse_stock_once()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var chicken = await pos.Owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Chicken", null, "g", null, 0, true, []));
        await pos.Owner.PutAsync<RecipeDto>("/api/inventory/recipes", new RecipeDto(pos.Burger, null, null, [new(chicken.Id, 150, "g")]));
        var order = await pos.Owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => pos.Owner.RawPostAsync($"/api/orders/{order.Id}/void", new ReasonRequest("Walked out"))));
        results.Should().NotContain(r => r.StatusCode == HttpStatusCode.InternalServerError);

        var ledger = await pos.Owner.GetAsync<List<StockMovementDto>>($"/api/inventory/movements?itemId={chicken.Id}");
        ledger.Where(m => m.Type == StockMovementType.SaleReversal).Sum(m => m.Quantity).Should().Be(300);
        ledger.Sum(m => m.Quantity).Should().Be(0);
        (await pos.Owner.GetAsync<List<KitchenTicketDto>>("/api/kitchen/tickets")).Should().OnlyContain(t => t.IsChange || t.Status == KitchenTicketStatus.Cancelled);
    }

    [Fact]
    public async Task Simultaneous_stock_receipts_never_lose_stock()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var chicken = await pos.Owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Chicken", null, "g", null, 0, true, []));

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => pos.Owner.RawPostAsync("/api/inventory/receipts",
            new CreateStockReceiptRequest(Guid.NewGuid(), null, null, null, [new(chicken.Id, 1, "kg", 1000)]))));
        results.Should().NotContain(r => r.StatusCode == HttpStatusCode.InternalServerError);
        var booked = results.Count(r => r.IsSuccessStatusCode);
        booked.Should().BeGreaterThanOrEqualTo(1);

        var level = (await pos.Owner.GetAsync<List<StockLevelDto>>("/api/inventory/levels")).Single(l => l.ItemId == chicken.Id);
        level.OnHand.Should().Be(booked * 1000);
        level.AverageCost.Should().Be(1);
        (await pos.Owner.GetAsync<List<StockReceiptDto>>("/api/inventory/receipts")).Should().HaveCount(booked);
    }
}
