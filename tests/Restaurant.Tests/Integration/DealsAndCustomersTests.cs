using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Customers;
using Restaurant.Contracts.Deals;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Menu;
using Restaurant.Contracts.Orders;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>Deals (T6, PLAN.md section 6) on a hand-moved clock, and customer profiles linked from the POS.</summary>
public class DealsTests(ClockApiFactory factory) : IClassFixture<ClockApiFactory>
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(5));
    private static readonly DateTimeOffset HappyHour = new(2026, 11, 2, 16, 0, 0, TimeSpan.FromHours(5));

    public static SaveDealRequest Deal(string name, DealType type, DealScope scope = DealScope.Items, bool combinable = true, decimal? percent = null,
        decimal? bundlePrice = null, string? coupon = null, TimeOnly? start = null, TimeOnly? end = null, params DealComponentDto[] items) =>
        new(name, null, null, type, scope, true, combinable, null, null, WeekDays.All, start, end, coupon, null, percent, null, bundlePrice,
            null, null, null, true, [], items);

    [Fact]
    public async Task A_combo_is_charged_at_its_price_plus_add_ons_and_its_lines_are_voided_together()
    {
        factory.Clock.Now = Morning;
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;
        var meal = await owner.PostAsync<DealDto>("/api/deals", Deal("Burger meal", DealType.Bundle, bundlePrice: 750,
            items: [new(0, pos.Burger, null, 1), new(1, pos.Drink, null, 1)]));
        (await owner.GetAsync<PosMenuDto>("/api/menu/pos")).Deals.Should().ContainSingle(d => d.Id == meal.Id);

        var selection = new DealSelection(meal.Id, 1, [new(pos.Burger, null, 1, [], null), new(pos.Drink, null, 1, [], null)]);
        var preview = await owner.PostAsync<PriceBreakdownDto>("/api/orders/price-preview", new PricePreviewRequest(OrderType.Takeaway, [], null, Deals: [selection]));
        preview.Subtotal.Should().Be(850);
        preview.DealDiscount.Should().Be(100);
        preview.Total.Should().Be(870);

        // Add-ons stay charged: combo (750) + cheese (100) + a second drink (300) = 1150 net, GST 184.
        var withCheese = selection with { Components = [new(pos.Burger, null, 1, [pos.ExtraCheese], null), new(pos.Drink, null, 1, [], null)] };
        var order = await owner.PostAsync<OrderDto>("/api/orders", new CreateOrderRequest(Guid.NewGuid(), OrderType.Takeaway, null, null, null, null, null,
            [new(pos.Drink, null, 1, [], null)], null, false, Deals: [withCheese]));
        order.Subtotal.Should().Be(1250);
        order.Discount.Should().Be(100);
        order.Total.Should().Be(1334);
        order.Deals.Should().ContainSingle(d => d.Name == "Burger meal" && d.Discount == 100);
        order.Lines.Count(l => l.DealApplicationId != null).Should().Be(2);

        var comboBurger = order.Lines.Single(l => l.MenuItemId == pos.Burger);
        order = await owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/lines/{comboBurger.Id}/void", new ReasonRequest(null));
        order.Lines.Where(l => l.DealApplicationId != null).Should().OnlyContain(l => l.Status == OrderLineStatus.Voided);
        order.Deals.Should().BeEmpty();
        order.Total.Should().Be(348);

        var wrongChoice = selection with { Components = [new(pos.Biryani, pos.BiryaniHalf, 1, [], null), new(pos.Drink, null, 1, [], null)] };
        (await owner.RawPostAsync("/api/orders", new CreateOrderRequest(Guid.NewGuid(), OrderType.Takeaway, null, null, null, null, null, [], null, false,
            Deals: [wrongChoice]))).ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Automatic_deals_follow_the_clock_and_coupons_and_orders_keep_the_rules_they_were_placed_with()
    {
        factory.Clock.Now = Morning;
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;
        var happyHour = await owner.PostAsync<DealDto>("/api/deals", Deal("Happy hour drinks", DealType.PercentOff, percent: 20,
            start: new TimeOnly(15, 0), end: new TimeOnly(18, 0), items: [new(0, pos.Drink, null, 1)]));
        await owner.PostAsync<DealDto>("/api/deals", Deal("Ten off", DealType.PercentOff, DealScope.Order, combinable: false, percent: 10, coupon: "save10"));

        var lines = new List<OrderLineRequest> { new(pos.Burger, null, 1, [], null), new(pos.Drink, null, 1, [], null) };
        (await owner.PostAsync<PriceBreakdownDto>("/api/orders/price-preview", new PricePreviewRequest(OrderType.Takeaway, lines, null))).Discount.Should().Be(0);

        factory.Clock.Now = HappyHour;
        var preview = await owner.PostAsync<PriceBreakdownDto>("/api/orders/price-preview", new PricePreviewRequest(OrderType.Takeaway, lines, null));
        preview.Discount.Should().Be(60);
        preview.Deals.Should().ContainSingle(d => d.Name == "Happy hour drinks");

        // The coupon (85, not combinable) beats happy hour (60) on its own.
        var withCoupon = await owner.PostAsync<OrderDto>("/api/orders", new CreateOrderRequest(Guid.NewGuid(), OrderType.Takeaway, null, null, null, null, null,
            lines, null, false, CouponCode: "SAVE10"));
        withCoupon.Deals.Should().ContainSingle(d => d.Name == "Ten off" && d.Discount == 85);
        (await owner.RawPostAsync("/api/orders", new CreateOrderRequest(Guid.NewGuid(), OrderType.Takeaway, null, null, null, null, null,
            lines, null, false, CouponCode: "NOPE"))).ShouldBe(HttpStatusCode.BadRequest);

        // Changing the deal later doesn't change an order that already has it.
        var order = await owner.PostAsync<OrderDto>("/api/orders", new CreateOrderRequest(Guid.NewGuid(), OrderType.Takeaway, null, null, null, null, null,
            lines, null, false));
        order.Discount.Should().Be(60);
        await owner.PutAsync<DealDto>($"/api/deals/{happyHour.Id}", Deal("Happy hour drinks", DealType.PercentOff, percent: 50,
            start: new TimeOnly(15, 0), end: new TimeOnly(18, 0), items: [new(0, pos.Drink, null, 1)]));
        order = await owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/lines", new AddOrderLinesRequest(Guid.NewGuid(), [new(pos.Drink, null, 1, [], null)], false));
        order.Discount.Should().Be(120);
    }

    [Fact]
    public async Task Only_deal_managers_edit_deals_and_bad_deals_are_refused()
    {
        factory.Clock.Now = Morning;
        var pos = await factory.SetUpRestaurantAsync();
        var (cashier, _, _) = await factory.AddStaffAsync(pos.Owner, ["Cashier"]);
        cashier.UseBranch(pos.Owner.BranchId);

        (await cashier.RawPostAsync("/api/deals", Deal("Sneaky", DealType.PercentOff, DealScope.Order, percent: 90))).ShouldBe(HttpStatusCode.Forbidden);
        (await pos.Owner.RawPostAsync("/api/deals", Deal("Nothing", DealType.Bundle, bundlePrice: 500, items: [new(0, pos.Burger, null, 1)])))
            .ShouldBe(HttpStatusCode.BadRequest);
        (await pos.Owner.RawPostAsync("/api/deals", Deal("Too much", DealType.PercentOff, DealScope.Order, percent: 150))).ShouldBe(HttpStatusCode.BadRequest);
        (await pos.Owner.RawPostAsync("/api/deals", Deal("Half window", DealType.PercentOff, DealScope.Order, percent: 5, start: new TimeOnly(15, 0))))
            .ShouldBe(HttpStatusCode.BadRequest);
    }
}

public class CustomerTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_phone_number_at_the_POS_links_orders_to_one_customer_with_history_and_spend()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;

        var first = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder() with { CustomerName = "Ayesha", CustomerPhone = "+92 300 1234567" });
        var second = await owner.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(send: false) with { CustomerPhone = "0300-1234567" });
        first.CustomerId.Should().NotBeNull();
        second.CustomerId.Should().Be(first.CustomerId);
        second.CustomerName.Should().Be("Ayesha");

        await owner.PostAsync<OrderDto>($"/api/orders/{first.Id}/served", new { });
        await owner.PayCashAsync(first.Id, 1856, 2000, pos);

        var found = await owner.PostAsync<List<CustomerDto>>("/api/customers/search", new CustomerSearchRequest("0300123"));
        found.Should().ContainSingle().Which.Should().Match<CustomerDto>(c => c.Name == "Ayesha" && c.Phone == "03001234567" && c.OrderCount == 1 && c.TotalSpend == 1856);
        var detail = await owner.GetAsync<CustomerDetailDto>($"/api/customers/{first.CustomerId}");
        detail.Orders.Select(o => o.Id).Should().BeEquivalentTo([first.Id, second.Id]);

        (await owner.RawPostAsync("/api/customers", new SaveCustomerRequest("Imposter", "03001234567", null, null))).ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cashiers_look_customers_up_but_only_managers_edit_them_or_remove_personal_data()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;
        var (cashier, _, _) = await factory.AddStaffAsync(owner, ["Cashier"]);
        cashier.UseBranch(owner.BranchId);

        var customer = await owner.PostAsync<CustomerDto>("/api/customers", new SaveCustomerRequest("Bilal Khan", "0321 7654321", "House 4, DHA", "Allergic to nuts"));
        var order = await cashier.PostAsync<OrderDto>("/api/orders", pos.StandardOrder(send: false) with { CustomerId = customer.Id });
        order.CustomerName.Should().Be("Bilal Khan");
        order.CustomerPhone.Should().Be("03217654321");

        (await cashier.PostAsync<List<CustomerDto>>("/api/customers/search", new CustomerSearchRequest("bilal"))).Should().ContainSingle();
        (await cashier.GetAsync<CustomerDetailDto>($"/api/customers/{customer.Id}")).Orders.Should().ContainSingle();
        (await cashier.RawPutAsync($"/api/customers/{customer.Id}", new SaveCustomerRequest("Renamed", null, null, null))).ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.RawPostAsync($"/api/customers/{customer.Id}/remove-personal-data", new { })).ShouldBe(HttpStatusCode.Forbidden);

        (await owner.RawPostAsync($"/api/customers/{customer.Id}/remove-personal-data", new { })).ShouldBe(HttpStatusCode.NoContent);
        var detail = await owner.GetAsync<CustomerDetailDto>($"/api/customers/{customer.Id}");
        detail.Customer.Should().Match<CustomerDto>(c => c.Phone == null && c.Address == null && c.Notes == null && c.Name != "Bilal Khan");
        var anonymised = await owner.GetAsync<OrderDto>($"/api/orders/{order.Id}");
        anonymised.CustomerName.Should().BeNull();
        anonymised.CustomerPhone.Should().BeNull();
        (await owner.PostAsync<List<CustomerDto>>("/api/customers/search", new CustomerSearchRequest("bilal"))).Should().BeEmpty();
    }
}
