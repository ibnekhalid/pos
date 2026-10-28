using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Kitchen;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Receipts;
using Restaurant.Contracts.Sessions;
using Restaurant.Contracts.Tables;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

public class PosFlowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_dine_in_order_goes_from_the_counter_through_the_kitchen_to_payment_and_receipt()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;

        var order = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(table: pos.Table1));
        order.OrderNumber.Should().Be("001");
        order.Subtotal.Should().Be(1600);
        order.TaxTotal.Should().Be(256);
        order.Total.Should().Be(1856);
        order.Status.Should().Be(OrderStatus.SentToKitchen);
        order.Lines.Should().OnlyContain(l => l.SentToKitchen);

        (await owner.GetAsync<List<TableDto>>("/api/tables")).Single(t => t.Id == pos.Table1).OpenOrderId.Should().Be(order.Id);

        // Two stations: the burger goes to the grill, the drink to the general kitchen.
        var tickets = await owner.GetAsync<List<KitchenTicketDto>>("/api/kitchen/tickets");
        tickets.Should().HaveCount(2);
        tickets.Single(t => t.StationId == pos.Grill).Items.Single().Should().Match<KitchenTicketItemDto>(i =>
            i.Quantity == 2 && i.Name == "Zinger Burger" && i.Modifiers.Contains("Extra cheese") && i.Notes == "No onions");

        foreach (var ticket in tickets)
        {
            (await owner.RawPutAsync($"/api/kitchen/tickets/{ticket.Id}/status", new UpdateTicketStatusRequest(KitchenTicketStatus.Preparing))).ShouldBe(HttpStatusCode.NoContent);
        }
        (await owner.GetAsync<OrderDto>($"/api/orders/{order.Id}")).Status.Should().Be(OrderStatus.Preparing);
        foreach (var ticket in tickets)
        {
            await owner.RawPutAsync($"/api/kitchen/tickets/{ticket.Id}/status", new UpdateTicketStatusRequest(KitchenTicketStatus.Ready));
            await owner.RawPutAsync($"/api/kitchen/tickets/{ticket.Id}/status", new UpdateTicketStatusRequest(KitchenTicketStatus.Served));
        }
        (await owner.RawPutAsync($"/api/kitchen/tickets/{tickets[0].Id}/status", new UpdateTicketStatusRequest(KitchenTicketStatus.Preparing))).ShouldBe(HttpStatusCode.Conflict);
        (await owner.GetAsync<OrderDto>($"/api/orders/{order.Id}")).Status.Should().Be(OrderStatus.Served);

        // Split tender: card first (no card-only tax rate here, so the total doesn't change), then cash with change.
        await owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/payments", new TakePaymentRequest(Guid.NewGuid(), pos.CardMethod, 856, null, null));
        var paid = await owner.PayCashAsync(order.Id, 1000, 5000, pos);
        paid.PaymentStatus.Should().Be(PaymentStatus.Paid);
        paid.Status.Should().Be(OrderStatus.Completed);
        paid.Payments.Should().Contain(p => p.IsCash && p.Change == 4000);

        // T11: printing logs a copy and creates no sale; the second print is a reprint.
        var first = await owner.PostAsync<ReceiptDocumentDto>($"/api/orders/{order.Id}/receipt/print", new PrintReceiptRequest(PrintKind.Receipt));
        first.IsReprint.Should().BeFalse();
        first.Total.Should().Be(1856);
        first.Payments.Should().HaveCount(2);
        var second = await owner.PostAsync<ReceiptDocumentDto>($"/api/orders/{order.Id}/receipt/print", new PrintReceiptRequest(PrintKind.Receipt));
        second.IsReprint.Should().BeTrue();
        second.CopyNumber.Should().Be(2);
        (await owner.GetAsync<List<OrderSummaryDto>>("/api/orders?scope=today")).Should().ContainSingle();
        (await owner.GetAsync<OrderDto>($"/api/orders/{order.Id}")).Payments.Should().HaveCount(2);

        var pdf = await owner.RawGetAsync($"/api/orders/{order.Id}/receipt/pdf");
        pdf.ShouldBe(HttpStatusCode.OK);
        (await pdf.Content.ReadAsByteArrayAsync()).Take(4).Should().Equal("%PDF"u8.ToArray());
        (await owner.RawGetAsync($"/api/orders/{order.Id}/receipt/pdf?kind=KitchenTicket")).ShouldBe(HttpStatusCode.OK);

        (await owner.GetAsync<List<TableDto>>("/api/tables")).Single(t => t.Id == pos.Table1).OpenOrderId.Should().BeNull();
    }

    [Fact]
    public async Task Orders_need_an_open_business_day()
    {
        var pos = await factory.SetUpRestaurantAsync(startDay: false);
        (await pos.Owner.RawPostAsync("/api/orders", pos.StandardOrder())).ShouldBe(HttpStatusCode.Conflict);

        await pos.Owner.PostAsync<BusinessSessionDto>("/api/sessions/start", new StartSessionRequest(0, null));
        (await pos.Owner.RawPostAsync("/api/orders", pos.StandardOrder())).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_same_request_never_creates_two_orders_or_payments()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var requestId = Guid.NewGuid();

        var first = await pos.Owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(clientRequestId: requestId));
        var again = await pos.Owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(clientRequestId: requestId));
        again.Id.Should().Be(first.Id);

        // Five taps at once.
        var racing = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => pos.Owner.RawPostAsync("/api/orders", pos.StandardOrder(clientRequestId: racing))));
        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        (await pos.Owner.GetAsync<List<OrderSummaryDto>>("/api/orders?scope=today")).Should().HaveCount(2);

        var payment = new TakePaymentRequest(Guid.NewGuid(), pos.CashMethod, 500, 500, null);
        await pos.Owner.PostAsync<OrderDto>($"/api/orders/{first.Id}/payments", payment);
        var replay = await pos.Owner.PostAsync<OrderDto>($"/api/orders/{first.Id}/payments", payment);
        replay.Payments.Should().ContainSingle();
        replay.Paid.Should().Be(500);
    }

    [Fact]
    public async Task Existing_orders_keep_their_prices_after_a_menu_change()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var before = await pos.Owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(send: false));

        var drink = await pos.Owner.GetAsync<Restaurant.Contracts.Menu.MenuItemDto>($"/api/menu/items/{pos.Drink}");
        await pos.Owner.PutAsync<Restaurant.Contracts.Menu.MenuItemDto>($"/api/menu/items/{pos.Drink}", new Restaurant.Contracts.Menu.SaveMenuItemRequest(
            drink.CategoryId, drink.Name, null, null, null, 400m, null, 1, true, [], []));

        (await pos.Owner.GetAsync<OrderDto>($"/api/orders/{before.Id}")).Total.Should().Be(1856);
        (await pos.Owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(send: false))).Total.Should().Be(1972); // 1700 + 16%
    }

    [Fact]
    public async Task The_menu_rules_are_enforced_on_the_server()
    {
        var pos = await factory.SetUpRestaurantAsync();
        OrderLineRequest Line(Guid item, Guid? variant = null, params Guid[] modifiers) => new(item, variant, 1, modifiers, null);
        CreateOrderRequest With(params OrderLineRequest[] lines) => new(Guid.NewGuid(), OrderType.Takeaway, null, null, null, null, null, lines, null, false);

        (await pos.Owner.RawPostAsync("/api/orders", With(Line(pos.Biryani)))).ShouldBe(HttpStatusCode.BadRequest); // size required
        (await pos.Owner.PostAsync<OrderDto>("/api/orders", With(Line(pos.Biryani, pos.BiryaniFull)))).Subtotal.Should().Be(850);
        (await pos.Owner.RawPostAsync("/api/orders", With(Line(pos.Drink, null, pos.ExtraCheese)))).ShouldBe(HttpStatusCode.NotFound); // not its add-on

        await pos.Owner.RawPutAsync($"/api/menu/items/{pos.Burger}/availability", new Restaurant.Contracts.Menu.SetAvailabilityRequest(MenuAvailability.OutOfStock, null));
        (await pos.Owner.RawPostAsync("/api/orders", With(Line(pos.Burger)))).ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_table_takes_one_open_order_and_more_items_go_to_it()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var order = await pos.Owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(table: pos.Table2));
        (await pos.Owner.RawPostAsync("/api/orders", pos.StandardOrder(table: pos.Table2))).ShouldBe(HttpStatusCode.Conflict);

        // T8: a second round becomes a second kitchen ticket; nothing already sent is resent.
        var more = await pos.Owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/lines",
            new AddOrderLinesRequest(Guid.NewGuid(), [new(pos.Drink, null, 2, [], null)], SendToKitchen: true));
        more.Total.Should().Be(1856 + 696);
        var tickets = await pos.Owner.GetAsync<List<KitchenTicketDto>>("/api/kitchen/tickets");
        tickets.Where(t => t.OrderId == order.Id).Select(t => t.Round).Should().BeEquivalentTo([1, 1, 2]);
        tickets.Single(t => t.Round == 2).Items.Single().Quantity.Should().Be(2);

        // Reading again (a reconnecting kitchen screen) returns the same persisted tickets.
        (await pos.Owner.GetAsync<List<KitchenTicketDto>>("/api/kitchen/tickets")).Should().BeEquivalentTo(tickets);
    }
}
