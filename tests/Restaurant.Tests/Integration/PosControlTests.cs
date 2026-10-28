using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Kitchen;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Sessions;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>T3, T12, T13 and isolation for the POS: sessions, voids, refunds, approvals, closing the day.</summary>
public class PosControlTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(Caller Cashier, Caller Manager, Approval Approval)> StaffAsync(PosSetup pos)
    {
        var (cashier, _, _) = await factory.AddStaffAsync(pos.Owner, ["Cashier"]);
        var (manager, managerMember, _) = await factory.AddStaffAsync(pos.Owner, ["Manager"]);
        await manager.RawPutAsync("/api/me/pin", new SetPinRequest(TestApi.Password, "2580"));
        cashier.UseBranch(pos.Owner.BranchId);
        manager.UseBranch(pos.Owner.BranchId);
        return (cashier, manager, new Approval(managerMember.MembershipId, "2580"));
    }

    [Fact]
    public async Task Only_permitted_staff_start_the_day_and_two_starts_make_one_day()
    {
        var pos = await factory.SetUpRestaurantAsync(startDay: false);
        var (waiter, _, _) = await factory.AddStaffAsync(pos.Owner, ["Waiter"]);
        waiter.UseBranch(pos.Owner.BranchId);
        (await waiter.RawPostAsync("/api/sessions/start", new StartSessionRequest(0, null))).ShouldBe(HttpStatusCode.Forbidden);

        var starts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => pos.Owner.RawPostAsync("/api/sessions/start", new StartSessionRequest(1000, null))));
        starts.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        starts.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(2);
        (await waiter.RawPostAsync("/api/sessions/current/close", new CloseSessionRequest([], null))).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Voiding_a_sent_item_needs_permission_or_a_managers_pin_and_tells_the_kitchen()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var (cashier, _, approval) = await StaffAsync(pos);
        var order = await cashier.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());
        var burgerLine = order.Lines.Single(l => l.Name == "Zinger Burger");

        (await cashier.RawPostAsync($"/api/orders/{order.Id}/lines/{burgerLine.Id}/void", new ReasonRequest("Customer changed mind"))).ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.RawPostAsync($"/api/orders/{order.Id}/lines/{burgerLine.Id}/void", new ReasonRequest("Customer changed mind", approval with { Pin = "1111" }))).ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.RawPostAsync($"/api/orders/{order.Id}/lines/{burgerLine.Id}/void", new ReasonRequest(null, approval))).ShouldBe(HttpStatusCode.BadRequest);

        var after = await cashier.PostAsync<OrderDto>($"/api/orders/{order.Id}/lines/{burgerLine.Id}/void", new ReasonRequest("Customer changed mind", approval));
        after.Total.Should().Be(348); // 300 + 16%
        after.Lines.Single(l => l.Id == burgerLine.Id).Status.Should().Be(OrderLineStatus.Voided);

        var change = (await pos.Owner.GetAsync<List<KitchenTicketDto>>("/api/kitchen/tickets")).Single(t => t.IsChange);
        change.Items.Single().Cancelled.Should().BeTrue();
        (await pos.Owner.RawPutAsync($"/api/kitchen/tickets/{change.Id}/status", new UpdateTicketStatusRequest(KitchenTicketStatus.Served))).ShouldBe(HttpStatusCode.NoContent);

        var approvers = await cashier.GetAsync<List<ApproverDto>>($"/api/approvers?permission={Permissions.Orders.Void}");
        approvers.Should().ContainSingle(a => a.MembershipId == approval.MembershipId);
    }

    [Fact]
    public async Task Refunds_need_permission_or_approval_and_never_exceed_the_payment()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var (cashier, _, approval) = await StaffAsync(pos);
        var order = await cashier.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());
        var paid = await cashier.PayCashAsync(order.Id, 1856, 2000, pos);
        var payment = paid.Payments.Single();

        (await cashier.RawPostAsync($"/api/payments/{payment.Id}/refunds", new RefundRequest(Guid.NewGuid(), 100, "Cold food"))).ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.RawPostAsync($"/api/payments/{payment.Id}/refunds", new RefundRequest(Guid.NewGuid(), 5000, "Too much", approval))).ShouldBe(HttpStatusCode.BadRequest);

        var refunded = await cashier.PostAsync<OrderDto>($"/api/payments/{payment.Id}/refunds", new RefundRequest(Guid.NewGuid(), 100, "Cold food", approval));
        refunded.Refunded.Should().Be(100);
        refunded.PaymentStatus.Should().Be(PaymentStatus.PartiallyRefunded);
    }

    [Fact]
    public async Task Closing_the_day_reconciles_the_drawer_and_blocks_on_unfinished_orders()
    {
        var pos = await factory.SetUpRestaurantAsync(openingCash: 5000);
        var owner = pos.Owner;

        // Cash sale 1856, card sale 1856, cash refund 100, cash-in 500, paid-out 200.
        var cashOrder = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());
        var cashPaid = await owner.PayCashAsync(cashOrder.Id, 1856, 2000, pos);
        await owner.PostAsync<OrderDto>($"/api/payments/{cashPaid.Payments[0].Id}/refunds", new RefundRequest(Guid.NewGuid(), 100, "Late"));
        var cardOrder = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());
        await owner.PostAsync<OrderDto>($"/api/orders/{cardOrder.Id}/payments", new TakePaymentRequest(Guid.NewGuid(), pos.CardMethod, 1856, null, null));
        await owner.PostAsync<CashMovementDto>("/api/sessions/current/cash-movements", new RecordCashMovementRequest(Guid.NewGuid(), null, CashMovementType.CashIn, 500, "Change float"));
        await owner.PostAsync<CashMovementDto>("/api/sessions/current/cash-movements", new RecordCashMovementRequest(Guid.NewGuid(), null, CashMovementType.PaidOut, 200, "Ice"));

        // Unserved orders block closing.
        var preview = await owner.GetAsync<ClosePreviewDto>("/api/sessions/current/close-preview");
        preview.Blockers.Should().HaveCount(2);
        var register = preview.Summary.Cash.Single();
        register.ExpectedCash.Should().Be(5000 + 1856 + 500 - 100 - 200);
        (await owner.RawPostAsync("/api/sessions/current/close", new CloseSessionRequest([new(register.RegisterSessionId, register.ExpectedCash, null)], null))).ShouldBe(HttpStatusCode.Conflict);

        await owner.PostAsync<OrderDto>($"/api/orders/{cashOrder.Id}/served", new { });
        await owner.PostAsync<OrderDto>($"/api/orders/{cardOrder.Id}/served", new { });

        preview = await owner.GetAsync<ClosePreviewDto>("/api/sessions/current/close-preview");
        preview.Blockers.Should().BeEmpty();
        preview.Summary.TotalBilled.Should().Be(3712);
        preview.Summary.Collected.Should().Be(3712);
        preview.Summary.Refunds.Should().Be(100);
        preview.Summary.Payments.Should().HaveCount(2);

        // Off by more than the 500 threshold without a reason: refused.
        (await owner.RawPostAsync("/api/sessions/current/close", new CloseSessionRequest([new(register.RegisterSessionId, register.ExpectedCash - 600, null)], null))).ShouldBe(HttpStatusCode.BadRequest);

        var closed = await owner.PostAsync<ClosedSessionDto>("/api/sessions/current/close",
            new CloseSessionRequest([new(register.RegisterSessionId, register.ExpectedCash - 50, null)], "Smooth day"));
        closed.Session.Status.Should().Be(SessionStatus.Closed);
        closed.Session.Registers.Single().CashDifference.Should().Be(-50);

        // Closed is closed: no more orders, no more payments until it's reopened.
        (await owner.RawPostAsync("/api/orders", pos.StandardOrder())).ShouldBe(HttpStatusCode.Conflict);
        (await owner.RawPostAsync($"/api/sessions/{closed.Session.Id}/reopen", new ReopenSessionRequest(""))).ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PostAsync<BusinessSessionDto>($"/api/sessions/{closed.Session.Id}/reopen", new ReopenSessionRequest("Missed a card slip"))).Status.Should().Be(SessionStatus.Open);
    }

    [Fact]
    public async Task Another_restaurant_cannot_touch_these_orders()
    {
        var a = await factory.SetUpRestaurantAsync();
        var b = await factory.SetUpRestaurantAsync();
        var order = await a.Owner.PostAsync<OrderDto>("/api/orders", a.StandardOrder());
        var ticket = (await a.Owner.GetAsync<List<KitchenTicketDto>>("/api/kitchen/tickets")).First();

        (await b.Owner.RawGetAsync($"/api/orders/{order.Id}")).ShouldBe(HttpStatusCode.NotFound);
        (await b.Owner.RawPostAsync($"/api/orders/{order.Id}/payments", new TakePaymentRequest(Guid.NewGuid(), b.CashMethod, 10, 10, null))).ShouldBe(HttpStatusCode.NotFound);
        (await b.Owner.RawPostAsync($"/api/orders/{order.Id}/void", new ReasonRequest("x"))).ShouldBe(HttpStatusCode.NotFound);
        (await b.Owner.RawPutAsync($"/api/kitchen/tickets/{ticket.Id}/status", new UpdateTicketStatusRequest(KitchenTicketStatus.Ready))).ShouldBe(HttpStatusCode.NotFound);
        (await b.Owner.RawPostAsync($"/api/orders/{order.Id}/receipt/print", new Restaurant.Contracts.Receipts.PrintReceiptRequest(PrintKind.Receipt))).ShouldBe(HttpStatusCode.NotFound);
        (await b.Owner.GetAsync<List<KitchenTicketDto>>("/api/kitchen/tickets")).Should().BeEmpty();
        (await b.Owner.RawPostAsync("/api/orders", new CreateOrderRequest(Guid.NewGuid(), OrderType.Takeaway, null, null, null, null, null,
            [new(a.Drink, null, 1, [], null)], null, false))).ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Kitchen_staff_see_tickets_but_not_orders_or_money()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var (kitchen, _, _) = await factory.AddStaffAsync(pos.Owner, ["Kitchen staff"]);
        kitchen.UseBranch(pos.Owner.BranchId);
        var order = await pos.Owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());

        (await kitchen.GetAsync<List<KitchenTicketDto>>("/api/kitchen/tickets")).Should().NotBeEmpty();
        (await kitchen.RawGetAsync($"/api/orders/{order.Id}")).ShouldBe(HttpStatusCode.Forbidden);
        (await kitchen.RawGetAsync("/api/orders")).ShouldBe(HttpStatusCode.Forbidden);
        (await kitchen.RawPostAsync($"/api/orders/{order.Id}/payments", new TakePaymentRequest(Guid.NewGuid(), pos.CashMethod, 1, 1, null))).ShouldBe(HttpStatusCode.Forbidden);
    }
}
