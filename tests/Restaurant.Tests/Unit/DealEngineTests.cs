using FluentAssertions;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Inventory;
using Restaurant.Domain.Entities;
using Restaurant.Domain.Pricing;

namespace Restaurant.Tests.Unit;

/// <summary>Automatic deals (T6), deal time windows and unit conversions (PLAN.md sections 6 and 9).</summary>
public class DealEngineTests
{
    private static readonly Guid Burger = Guid.NewGuid();
    private static readonly Guid Drink = Guid.NewGuid();

    private static DealRule Rule(DealType type, DealScope scope = DealScope.Items, bool combinable = true, decimal? percent = null, decimal? amount = null,
        int? buy = null, int? get = null, string? coupon = null, decimal? minimum = null, params Guid[] items) =>
        new(Guid.NewGuid(), type.ToString(), type, scope, combinable, coupon, minimum, percent, amount, buy, get, null,
            items.Select(i => new DealRuleItem(i, null)).ToList());

    [Fact]
    public void Buy_one_get_one_frees_the_cheapest_of_each_pair()
    {
        var bogo = Rule(DealType.BuyXGetY, buy: 1, get: 1, items: [Burger, Drink]);
        var lines = new[] { new DealLine(Burger, null, 650, 2), new DealLine(Drink, null, 300, 1) };

        // Units 650, 650, 300: one full pair (650 + 650) → one 650 free; the drink has no partner.
        DealEngine.Evaluate(lines, [bogo], 1600, null).Single().Discount.Should().Be(650);
        DealEngine.Evaluate([new DealLine(Burger, null, 650, 1), new DealLine(Drink, null, 300, 1)], [bogo], 950, null).Single().Discount.Should().Be(300);
        DealEngine.Evaluate([new DealLine(Burger, null, 650, 1)], [bogo], 650, null).Should().BeEmpty();
    }

    [Fact]
    public void Coupons_and_minimums_gate_a_deal()
    {
        var coupon = Rule(DealType.PercentOff, DealScope.Order, percent: 10, coupon: "SAVE10");
        var minimum = Rule(DealType.AmountOff, DealScope.Order, amount: 200, minimum: 2000);
        var lines = new[] { new DealLine(Burger, null, 600, 3) };

        DealEngine.Evaluate(lines, [coupon, minimum], 1800, null).Should().BeEmpty();
        DealEngine.Evaluate(lines, [coupon, minimum], 1800, "save10").Single().Discount.Should().Be(180);
        DealEngine.Evaluate([new DealLine(Burger, null, 600, 4)], [coupon, minimum], 2400, null).Single().Discount.Should().Be(200);
    }

    [Fact]
    public void A_non_combinable_deal_is_used_only_when_it_beats_the_rest_together()
    {
        var lines = new[] { new DealLine(Burger, null, 500, 2), new DealLine(Drink, null, 200, 1) };
        var drinkOff = Rule(DealType.AmountOff, amount: 50, items: [Drink]);           // 50, combinable
        var burgerPercent = Rule(DealType.PercentOff, percent: 10, items: [Burger]);     // 100, combinable
        var exclusive = Rule(DealType.PercentOff, DealScope.Order, combinable: false, percent: 15); // 180 alone

        DealEngine.Evaluate(lines, [drinkOff, burgerPercent, exclusive], 1200, null).Should().ContainSingle(d => d.Discount == 180);

        var weakExclusive = exclusive with { Percent = 10 }; // 120 < 150
        DealEngine.Evaluate(lines, [drinkOff, burgerPercent, weakExclusive], 1200, null).Sum(d => d.Discount).Should().Be(150);
    }

    [Fact]
    public void Deals_never_take_more_than_the_amount()
    {
        var big = Rule(DealType.AmountOff, DealScope.Order, amount: 900);
        var percent = Rule(DealType.PercentOff, DealScope.Order, percent: 50);
        DealEngine.Evaluate([new DealLine(Burger, null, 1000, 1)], [big, percent], 1000, null).Sum(d => d.Discount).Should().Be(1000);
    }

    [Fact]
    public void Deal_discounts_come_off_before_the_manual_discount_and_tax()
    {
        var result = OrderPricer.Price(new PricingInput(OrderType.Takeaway, [new PricingLine(1000, 0, 1)], 10, null, 0, false,
            [new PricingTax("GST", 16, false, OrderTypes.All, TaxPaymentScope.Any)], TaxTender.Unknown, RoundingMode.None, DealDiscount: 200));

        result.DealDiscount.Should().Be(200);
        result.Discount.Should().Be(280);  // 200 deal + 10% of the remaining 800
        result.TaxTotal.Should().Be(115.2m);
        result.Total.Should().Be(835.2m);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(15, true)]
    [InlineData(17, true)]
    [InlineData(18, false)]
    public void Happy_hour_runs_within_its_window(int hour, bool runs)
    {
        var deal = new Deal { StartTime = new TimeOnly(15, 0), EndTime = new TimeOnly(18, 0) };
        deal.RunsAt(new DateTime(2026, 11, 2, hour, 0, 0)).Should().Be(runs);
    }

    [Fact]
    public void Windows_can_cross_midnight_and_respect_dates_and_days()
    {
        var late = new Deal { StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(2, 0) };
        late.RunsAt(new DateTime(2026, 11, 2, 23, 30, 0)).Should().BeTrue();
        late.RunsAt(new DateTime(2026, 11, 3, 1, 0, 0)).Should().BeTrue();
        late.RunsAt(new DateTime(2026, 11, 3, 3, 0, 0)).Should().BeFalse();

        var weekend = new Deal { Days = WeekDays.Saturday | WeekDays.Sunday, EndDate = new DateOnly(2026, 11, 30) };
        weekend.RunsAt(new DateTime(2026, 11, 7, 12, 0, 0)).Should().BeTrue();   // Saturday
        weekend.RunsAt(new DateTime(2026, 11, 9, 12, 0, 0)).Should().BeFalse();  // Monday
        weekend.RunsAt(new DateTime(2026, 12, 5, 12, 0, 0)).Should().BeFalse();  // after the end date
        new Deal { IsActive = false }.RunsAt(new DateTime(2026, 11, 7, 12, 0, 0)).Should().BeFalse();
    }

    [Fact]
    public void Units_convert_within_a_dimension_or_through_the_items_own_packs()
    {
        IReadOnlyList<InventoryItemUnitDto> tray = [new("tray", 30)];
        UnitCatalog.ToBase("g", [], "kg", 2.5m).Should().Be(2500);
        UnitCatalog.ToBase("ml", [], "l", 1.5m).Should().Be(1500);
        UnitCatalog.ToBase("pc", tray, "tray", 2).Should().Be(60);
        UnitCatalog.ToBase("pc", tray, "dozen", 1).Should().Be(12);
        UnitCatalog.ToBase("g", [], "ml", 100).Should().BeNull();   // no mixing mass and volume
        UnitCatalog.ToBase("g", [], "tray", 1).Should().BeNull();
        UnitCatalog.Describe(2500, "g").Should().Be("2.5 kg");
        UnitCatalog.Describe(750, "g").Should().Be("750 g");
    }
}
