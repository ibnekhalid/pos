using Restaurant.Contracts.Enums;

namespace Restaurant.Domain.Pricing;

public record PricingLine(decimal UnitPrice, decimal ModifiersTotal, int Quantity)
{
    public decimal Total => (UnitPrice + ModifiersTotal) * Quantity;
}

/// <summary>A tax rate as it was when the order was placed.</summary>
public record PricingTax(string Name, decimal Percent, bool Inclusive, OrderTypes AppliesTo, TaxPaymentScope Scope);

public record PricingInput(
    OrderType Type,
    IReadOnlyList<PricingLine> Lines,
    decimal? DiscountPercent,
    decimal? DiscountAmount,
    decimal ServiceChargePercent,
    bool ServiceChargeApplies,
    IReadOnlyList<PricingTax> Taxes,
    TaxTender Tender,
    RoundingMode Rounding,
    decimal DealDiscount = 0);

public record PricedTax(string Name, decimal Percent, bool Inclusive, decimal Amount);

/// <param name="Discount">Deals plus the manual discount.</param>
public record PricingResult(
    decimal Subtotal,
    decimal Discount,
    decimal ServiceCharge,
    IReadOnlyList<PricedTax> Taxes,
    decimal TaxTotal,
    decimal Rounding,
    decimal Total,
    decimal DealDiscount = 0);

/// <summary>
/// The one place order totals are calculated (PLAN.md section 8):
/// <list type="number">
/// <item>Subtotal = Σ (unit price + modifiers) × quantity.</item>
/// <item>Deal discounts come off first; the manual discount (percent or amount) applies to what's left. Never more than the subtotal.</item>
/// <item>Inclusive taxes are taken out of the discounted amount, leaving the taxable base.</item>
/// <item>Service charge (if it applies to the order type) = base × percent. It is not taxed.</item>
/// <item>Exclusive taxes = base × percent each.</item>
/// <item>Total = discounted amount + service charge + exclusive taxes, then rounded per the branch rule;
/// the difference is the rounding adjustment.</item>
/// </list>
/// Cash-only and non-cash-only tax rates follow the tender; an unpaid order is priced as cash.
/// All amounts are rounded to 2 decimals (away from zero).
/// </summary>
public static class OrderPricer
{
    public static PricingResult Price(PricingInput input)
    {
        var subtotal = R(input.Lines.Sum(l => l.Total));

        var dealDiscount = Math.Clamp(R(input.DealDiscount), 0, subtotal);
        var afterDeals = subtotal - dealDiscount;
        var manual = input.DiscountPercent is { } percent
            ? R(afterDeals * Math.Clamp(percent, 0, 100) / 100m)
            : Math.Clamp(R(input.DiscountAmount ?? 0), 0, afterDeals);
        var discount = dealDiscount + manual;
        var net = subtotal - discount;

        var taxes = input.Taxes.Where(t => Applies(t, input.Type, input.Tender)).ToList();
        var inclusive = taxes.Where(t => t.Inclusive).ToList();
        var exclusive = taxes.Where(t => !t.Inclusive).ToList();

        // Inclusive taxes: the base is what's left after taking them out of the net amount.
        var inclusiveRate = inclusive.Sum(t => t.Percent);
        var taxBase = inclusiveRate == 0 ? net : R(net / (1 + inclusiveRate / 100m));
        var inclusiveTotal = net - taxBase;

        var priced = new List<PricedTax>();
        var allocated = 0m;
        for (var i = 0; i < inclusive.Count; i++)
        {
            // The last inclusive tax takes the rounding remainder so they add up exactly.
            var amount = i == inclusive.Count - 1 ? inclusiveTotal - allocated : R(taxBase * inclusive[i].Percent / 100m);
            allocated += amount;
            priced.Add(new PricedTax(inclusive[i].Name, inclusive[i].Percent, true, amount));
        }

        var serviceCharge = input.ServiceChargeApplies ? R(taxBase * input.ServiceChargePercent / 100m) : 0m;

        foreach (var tax in exclusive)
        {
            priced.Add(new PricedTax(tax.Name, tax.Percent, false, R(taxBase * tax.Percent / 100m)));
        }

        var exclusiveTotal = priced.Where(t => !t.Inclusive).Sum(t => t.Amount);
        var beforeRounding = net + serviceCharge + exclusiveTotal;
        var total = RoundTotal(beforeRounding, input.Rounding);

        return new PricingResult(subtotal, discount, serviceCharge, priced, priced.Sum(t => t.Amount), total - beforeRounding, total, dealDiscount);
    }

    public static bool Applies(PricingTax tax, OrderType type, TaxTender tender) =>
        tax.AppliesTo.HasFlag((OrderTypes)(int)type)
        && tax.Scope switch
        {
            TaxPaymentScope.CashOnly => tender != TaxTender.NonCash,
            TaxPaymentScope.NonCashOnly => tender == TaxTender.NonCash,
            _ => true
        };

    public static decimal RoundTotal(decimal amount, RoundingMode mode) => mode switch
    {
        RoundingMode.NearestRupee => Math.Round(amount, 0, MidpointRounding.AwayFromZero),
        RoundingMode.NearestFive => Math.Round(amount / 5m, 0, MidpointRounding.AwayFromZero) * 5m,
        RoundingMode.NearestTen => Math.Round(amount / 10m, 0, MidpointRounding.AwayFromZero) * 10m,
        _ => R(amount)
    };

    private static decimal R(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
