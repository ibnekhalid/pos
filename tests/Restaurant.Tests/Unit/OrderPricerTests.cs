using FluentAssertions;
using Restaurant.Contracts.Enums;
using Restaurant.Domain.Orders;
using Restaurant.Domain.Pricing;

namespace Restaurant.Tests.Unit;

/// <summary>T6 (calculation part): worked examples for the pricing rules in PLAN.md section 8.</summary>
public class OrderPricerTests
{
    private static readonly PricingTax Gst16 = new("GST", 16m, false, OrderTypes.All, TaxPaymentScope.Any);

    private static PricingResult Price(OrderType type, PricingLine[] lines, PricingTax[] taxes, decimal sc = 0, bool scApplies = false,
        decimal? discountPercent = null, decimal? discountAmount = null, TaxTender tender = TaxTender.Unknown, RoundingMode rounding = RoundingMode.None) =>
        OrderPricer.Price(new PricingInput(type, lines, discountPercent, discountAmount, sc, scApplies, taxes, tender, rounding));

    [Fact]
    public void Exclusive_tax_and_service_charge_are_added_on_the_net_amount()
    {
        // 2 × 550 + 1 × (250 + 50 add-on) = 1400; service 10% = 140; GST 16% of 1400 = 224.
        var result = Price(OrderType.DineIn, [new(550, 0, 2), new(250, 50, 1)], [Gst16], sc: 10, scApplies: true);

        result.Subtotal.Should().Be(1400);
        result.ServiceCharge.Should().Be(140);
        result.TaxTotal.Should().Be(224);
        result.Total.Should().Be(1764);
    }

    [Fact]
    public void Inclusive_tax_is_taken_out_of_the_price()
    {
        var result = Price(OrderType.Takeaway, [new(1160, 0, 1)], [Gst16 with { Inclusive = true }]);

        result.TaxTotal.Should().Be(160);
        result.Total.Should().Be(1160);
    }

    [Fact]
    public void Discount_comes_before_tax_and_never_exceeds_the_subtotal()
    {
        Price(OrderType.Takeaway, [new(1000, 0, 1)], [Gst16], discountPercent: 10).Total.Should().Be(1044); // 900 + 144
        Price(OrderType.Takeaway, [new(300, 0, 1)], [Gst16], discountAmount: 500).Total.Should().Be(0);
    }

    [Fact]
    public void Cash_and_card_rates_follow_the_tender()
    {
        PricingTax[] taxes = [new("GST cash", 16m, false, OrderTypes.All, TaxPaymentScope.CashOnly), new("GST card", 5m, false, OrderTypes.All, TaxPaymentScope.NonCashOnly)];
        Price(OrderType.DineIn, [new(1000, 0, 1)], taxes, tender: TaxTender.Unknown).Total.Should().Be(1160); // unpaid = priced as cash
        Price(OrderType.DineIn, [new(1000, 0, 1)], taxes, tender: TaxTender.NonCash).Total.Should().Be(1050);
    }

    [Fact]
    public void Taxes_only_apply_to_their_order_types()
    {
        var dineInOnly = Gst16 with { AppliesTo = OrderTypes.DineIn };
        Price(OrderType.Takeaway, [new(1000, 0, 1)], [dineInOnly]).Total.Should().Be(1000);
    }

    [Theory]
    [InlineData(RoundingMode.NearestRupee, 1764.49, 1764)]
    [InlineData(RoundingMode.NearestFive, 1763, 1765)]
    [InlineData(RoundingMode.NearestTen, 1764, 1760)]
    public void Rounding_is_recorded_as_an_adjustment(RoundingMode mode, decimal price, decimal expected)
    {
        var result = Price(OrderType.Takeaway, [new(price, 0, 1)], [], rounding: mode);
        result.Total.Should().Be(expected);
        result.Rounding.Should().Be(expected - price);
    }

    [Fact]
    public void Order_status_follows_the_kitchen_and_payment()
    {
        OrderRules.ServiceStatus([], hasUnsentLines: true, paid: false).Should().Be(OrderStatus.Confirmed);
        OrderRules.ServiceStatus([KitchenTicketStatus.New], false, false).Should().Be(OrderStatus.SentToKitchen);
        OrderRules.ServiceStatus([KitchenTicketStatus.New, KitchenTicketStatus.Preparing], false, false).Should().Be(OrderStatus.Preparing);
        OrderRules.ServiceStatus([KitchenTicketStatus.Ready, KitchenTicketStatus.Served], false, false).Should().Be(OrderStatus.Ready);
        OrderRules.ServiceStatus([KitchenTicketStatus.Served], false, false).Should().Be(OrderStatus.Served);
        OrderRules.ServiceStatus([KitchenTicketStatus.Served], false, true).Should().Be(OrderStatus.Completed);
        OrderRules.CanMoveTicket(KitchenTicketStatus.Ready, KitchenTicketStatus.Preparing).Should().BeFalse();
    }

    [Fact]
    public void Expected_cash_is_the_sum_of_drawer_movements()
    {
        Restaurant.Domain.Entities.CashMovement M(CashMovementType type, decimal amount) => new() { Type = type, Amount = amount };
        new[] { M(CashMovementType.OpeningFloat, 5000), M(CashMovementType.CashSale, 1764), M(CashMovementType.CashIn, 500),
                M(CashMovementType.CashRefund, 100), M(CashMovementType.PaidOut, 200) }
            .Sum(m => m.SignedAmount).Should().Be(6964);
    }
}
