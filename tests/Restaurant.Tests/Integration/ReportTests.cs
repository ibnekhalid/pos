using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Inventory;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Reports;
using Restaurant.Contracts.Sessions;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>T14: every report figure is the sum over the seeded orders, payments and stock movements.</summary>
public class ReportTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>
    /// A: takeaway 1856, cash, 100 refunded. B: dine-in, 10% off (1600 − 160 = 1440 + 16% = 1670.40), card.
    /// C: voided after the kitchen (1600). D: burger line voided (1300), drink left (300 + 48 = 348), unpaid.
    /// E: held, then cancelled before the kitchen.
    /// </summary>
    private async Task<(PosSetup Pos, BusinessSessionDto Session, OrderDto D)> SeedDayAsync()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;

        var a = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());
        a = await owner.PayCashAsync(a.Id, 1856, 2000, pos);
        await owner.PostAsync<OrderDto>($"/api/payments/{a.Payments[0].Id}/refunds", new RefundRequest(Guid.NewGuid(), 100, "Cold fries"));

        var b = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(table: pos.Table1));
        b = await owner.PutAsync<OrderDto>($"/api/orders/{b.Id}/discount", new SetDiscountRequest(new ManualDiscountRequest(10, null, "Regular")));
        b.Total.Should().Be(1670.40m);
        await owner.PostAsync<OrderDto>($"/api/orders/{b.Id}/payments", new TakePaymentRequest(Guid.NewGuid(), pos.CardMethod, 1670.40m, null, "1234"));

        var c = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());
        await owner.PostAsync<OrderDto>($"/api/orders/{c.Id}/void", new ReasonRequest("Walked out"));

        var d = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());
        d = await owner.PostAsync<OrderDto>($"/api/orders/{d.Id}/lines/{d.Lines.Single(l => l.MenuItemId == pos.Burger).Id}/void", new ReasonRequest("Out of buns"));
        d.Total.Should().Be(348);

        var e = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(send: false));
        await owner.PostAsync<OrderDto>($"/api/orders/{e.Id}/cancel", new ReasonRequest("Changed mind"));

        var session = await owner.GetAsync<BusinessSessionDto>("/api/sessions/current");
        return (pos, session, d);
    }

    private static string Range(BusinessSessionDto session, string extra = "") =>
        $"?from={session.BusinessDate:yyyy-MM-dd}&to={session.BusinessDate:yyyy-MM-dd}{extra}";

    [Fact]
    public async Task Sales_figures_are_the_sums_of_the_seeded_orders_and_match_the_close_day_summary()
    {
        var (pos, session, _) = await SeedDayAsync();
        var owner = pos.Owner;

        var sales = await owner.GetAsync<SalesReportDto>("/api/reports/sales" + Range(session));
        var f = sales.Figures;
        f.OrderCount.Should().Be(3);
        f.CancelledCount.Should().Be(1);
        f.VoidedCount.Should().Be(1);
        f.GrossSales.Should().Be(1600 + 1600 + 300);
        f.ManualDiscounts.Should().Be(160);
        f.DealDiscounts.Should().Be(0);
        f.Discounts.Should().Be(160);
        f.NetSales.Should().Be(3340);
        f.Tax.Should().Be(256 + 230.40m + 48);
        f.ServiceCharge.Should().Be(0);
        f.TotalBilled.Should().Be(1856 + 1670.40m + 348);
        f.VoidedValue.Should().Be(1600 + 1300);
        f.AverageOrderValue.Should().Be(1113.33m);
        f.Collected.Should().Be(1856 + 1670.40m);
        f.Refunds.Should().Be(100);
        sales.ByDay.Should().ContainSingle().Which.TotalBilled.Should().Be(f.TotalBilled);
        sales.Payments!.Single(m => m.IsCash).Should().Match<PaymentMethodTotalDto>(m => m.Collected == 1856 && m.Refunded == 100 && m.Count == 1);

        // The same numbers as the business day's own summary (one query path, one set of formulas).
        var summary = (await owner.GetAsync<ClosePreviewDto>("/api/sessions/current/close-preview")).Summary;
        (summary.OrderCount, summary.GrossSales, summary.Discounts, summary.NetSales, summary.Tax, summary.TotalBilled, summary.VoidedValue, summary.Collected, summary.Refunds)
            .Should().Be((f.OrderCount, f.GrossSales, f.Discounts, f.NetSales, f.Tax, f.TotalBilled, f.VoidedValue, f.Collected!.Value, f.Refunds!.Value));
        (await owner.GetAsync<SalesReportDto>($"/api/reports/sales?sessionId={session.Id}&from=2000-01-01&to=2000-01-01")).Figures.Should().Be(f);

        // Filters.
        (await owner.GetAsync<SalesReportDto>("/api/reports/sales" + Range(session, "&orderType=DineIn"))).Figures.TotalBilled.Should().Be(1670.40m);
        (await owner.GetAsync<SalesReportDto>("/api/reports/sales" + Range(session, $"&cashierId={Guid.NewGuid()}"))).Figures.OrderCount.Should().Be(0);
        (await owner.GetAsync<SalesReportDto>($"/api/reports/sales?from={session.BusinessDate.AddDays(1):yyyy-MM-dd}&to={session.BusinessDate.AddDays(3):yyyy-MM-dd}"))
            .Figures.OrderCount.Should().Be(0);
    }

    [Fact]
    public async Task Orders_products_and_payments_reports_add_up()
    {
        var (pos, session, d) = await SeedDayAsync();
        var owner = pos.Owner;

        var orders = await owner.GetAsync<OrdersReportDto>("/api/reports/orders" + Range(session));
        orders.ByType.Should().BeEquivalentTo([new OrderCountDto("DineIn", 1, 1440), new OrderCountDto("Takeaway", 2, 1900)]);
        orders.ByStatus.Sum(s => s.Count).Should().Be(5);
        orders.ByHour.Sum(h => h.Count).Should().Be(3);
        orders.ByWeekday.Should().ContainSingle().Which.Key.Should().Be(session.BusinessDate.DayOfWeek.ToString());
        orders.Voids.Should().HaveCount(3);
        orders.Voids.Single(v => v.Kind == "voided_order").Should().Match<VoidEntryDto>(v => v.Reason == "Walked out" && v.Value == 1600);
        orders.Voids.Single(v => v.Kind == "voided_line").Should().Match<VoidEntryDto>(v => v.Reason == "Out of buns" && v.Value == 1300 && v.Quantity == 2);
        orders.Voids.Single(v => v.Kind == "cancelled_order").Reason.Should().Be("Changed mind");
        orders.Outstanding.Should().ContainSingle().Which.Should().Match<OutstandingOrderDto>(o => o.OrderId == d.Id && o.Balance == 348);

        var products = await owner.GetAsync<ProductsReportDto>("/api/reports/products" + Range(session));
        products.Items[0].Should().Match<ProductSalesDto>(i => i.Name == "Zinger Burger" && i.Quantity == 4 && i.Gross == 2600 && i.Category == "Mains");
        products.Items.Single(i => i.Name == "Mint Margarita").Quantity.Should().Be(3);
        products.Items.Sum(i => i.Gross).Should().Be(3500);
        products.Categories.Should().BeEquivalentTo([new CategorySalesDto("Mains", 4, 2600, 74.3m), new CategorySalesDto("Drinks", 3, 900, 25.7m)]);

        var payments = await owner.GetAsync<PaymentsReportDto>("/api/reports/payments" + Range(session));
        payments.Collected.Should().Be(3526.40m);
        payments.Refunded.Should().Be(100);
        payments.Net.Should().Be(3426.40m);
        payments.ByMethod.Should().HaveCount(2);
        payments.ByCashier.Should().ContainSingle().Which.Count.Should().Be(2);
        payments.Refunds.Should().ContainSingle().Which.Reason.Should().Be("Cold fries");
        (await owner.GetAsync<PaymentsReportDto>("/api/reports/payments" + Range(session, $"&paymentMethodId={pos.CardMethod}")))
            .Should().Match<PaymentsReportDto>(p => p.Collected == 1670.40m && p.Refunded == 0);
    }

    [Fact]
    public async Task Inventory_report_sums_the_ledger_for_the_period()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;
        var chicken = await owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Chicken", null, "g", "Meat", 2000, true, []));
        await owner.PostAsync<StockReceiptDto>("/api/inventory/receipts", new CreateStockReceiptRequest(Guid.NewGuid(), null, null, null, [new(chicken.Id, 5, "kg", 1000)]));
        await owner.PutAsync<RecipeDto>("/api/inventory/recipes", new RecipeDto(pos.Burger, null, null, [new(chicken.Id, 150, "g")]));
        await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder());
        await owner.PostAsync<StockMovementDto>("/api/inventory/adjustments", new StockAdjustmentRequest(Guid.NewGuid(), chicken.Id, StockMovementType.Wastage, 200, "g", "Dropped"));
        var session = await owner.GetAsync<BusinessSessionDto>("/api/sessions/current");

        var report = await owner.GetAsync<InventoryReportDto>("/api/reports/inventory" + Range(session));
        var activity = report.Activity.Single(a => a.ItemId == chicken.Id);
        activity.Received.Should().Be(5000);
        activity.ReceivedValue.Should().Be(5000);
        activity.Consumed.Should().Be(300);
        activity.ConsumedValue.Should().Be(300);
        activity.Wasted.Should().Be(200);
        activity.WastedValue.Should().Be(200);
        var level = report.Levels.Single(l => l.ItemId == chicken.Id);
        level.OnHand.Should().Be(4500);
        report.InventoryValue.Should().Be(report.Levels.Sum(l => l.Value)).And.Be(4500);
        report.LowStockCount.Should().Be(0);
    }

    [Fact]
    public async Task Reports_need_report_permissions_and_stay_inside_the_tenant()
    {
        var (pos, session, _) = await SeedDayAsync();
        var (cashier, _, _) = await factory.AddStaffAsync(pos.Owner, ["Cashier"]);
        var (manager, _, _) = await factory.AddStaffAsync(pos.Owner, ["Manager"]);
        cashier.UseBranch(pos.Owner.BranchId);
        manager.UseBranch(pos.Owner.BranchId);

        (await cashier.RawGetAsync("/api/reports/sales" + Range(session))).ShouldBe(HttpStatusCode.Forbidden);

        // Managers see sales but not money collected (reports.financial).
        var managerSales = await manager.GetAsync<SalesReportDto>("/api/reports/sales" + Range(session));
        managerSales.Figures.TotalBilled.Should().Be(3874.40m);
        managerSales.Figures.Collected.Should().BeNull();
        managerSales.Payments.Should().BeNull();
        (await manager.RawGetAsync("/api/reports/payments" + Range(session))).ShouldBe(HttpStatusCode.Forbidden);
        (await manager.RawGetAsync("/api/reports/sales" + Range(session, "&allBranches=true"))).ShouldBe(HttpStatusCode.OK);

        (await pos.Owner.RawGetAsync($"/api/reports/sales?from={session.BusinessDate:yyyy-MM-dd}&to={session.BusinessDate.AddDays(-1):yyyy-MM-dd}")).ShouldBe(HttpStatusCode.BadRequest);
        (await pos.Owner.RawGetAsync($"/api/reports/sales?from=2025-01-01&to=2026-06-01")).ShouldBe(HttpStatusCode.BadRequest);

        // Another restaurant sees nothing of this one.
        var other = await factory.SetUpRestaurantAsync();
        (await other.Owner.RawGetAsync($"/api/reports/sales?sessionId={session.Id}&from=2000-01-01&to=2000-01-01")).ShouldBe(HttpStatusCode.NotFound);
        (await other.Owner.GetAsync<SalesReportDto>("/api/reports/sales" + Range(session, "&allBranches=true"))).Figures.OrderCount.Should().Be(0);
    }
}
