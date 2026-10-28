using System.Text.Json;
using System.Text.Json.Serialization;
using Restaurant.Contracts.Deals;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;
using Restaurant.Domain.Entities;
using Restaurant.Domain.Orders;
using Restaurant.Domain.Pricing;

namespace Restaurant.Application.Features.Orders;

/// <summary>Applies <see cref="OrderPricer"/> to an order using the rules captured when it was placed.</summary>
public static class OrderPricing
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The branch's current rules for a new order (they are then frozen on the order).</summary>
    public static (decimal ServiceChargePercent, bool ServiceChargeApplies, RoundingMode Rounding, List<PricingTax> Taxes) CurrentRules(
        Branch branch, OrderType type, IEnumerable<TaxRate> rates) =>
    (
        branch.ServiceChargePercent,
        branch.ServiceChargePercent > 0 && branch.ServiceChargeAppliesTo.HasFlag((OrderTypes)(int)type),
        branch.Rounding,
        rates.Where(r => r.IsActive && (r.BranchId == null || r.BranchId == branch.Id) && r.AppliesTo.HasFlag((OrderTypes)(int)type))
            .OrderBy(r => r.Name)
            .Select(r => new PricingTax(r.Name, r.Percent, r.IsInclusive, r.AppliesTo, r.PaymentScope))
            .ToList()
    );

    public static List<PricingTax> Taxes(Order order) => JsonSerializer.Deserialize<List<PricingTax>>(order.TaxRatesJson, Json) ?? [];

    public static List<DealRule> DealRules(Order order) => JsonSerializer.Deserialize<List<DealRule>>(order.DealRulesJson, Json) ?? [];

    /// <summary>
    /// Prices the order's active lines plus <paramref name="extraLines"/> (a preview of lines not added yet).
    /// Combo discounts come from the order's deal applications; automatic deals are evaluated on the lines
    /// that aren't part of a combo.
    /// </summary>
    public static (PricingResult Result, IReadOnlyList<DealResult> AutoDeals) Calculate(
        Order order, IReadOnlyList<OrderLine> extraLines, IReadOnlyList<OrderDealApplication> extraBundles)
    {
        var lines = order.Lines.Where(l => l.Status == OrderLineStatus.Active).Concat(extraLines).ToList();
        var bundles = order.DealApplications.Where(a => !a.IsVoided).Concat(extraBundles).ToList();

        var dealLines = lines.Where(l => l.DealApplicationId is null)
            .Select(l => new DealLine(l.MenuItemId, l.VariantId, l.UnitPrice + l.ModifiersTotal, l.Quantity)).ToList();
        var autoDeals = DealEngine.Evaluate(dealLines, DealRules(order), dealLines.Sum(l => l.UnitPrice * l.Quantity), order.CouponCode);

        var result = OrderPricer.Price(new PricingInput(order.Type, lines.Select(l => new PricingLine(l.UnitPrice, l.ModifiersTotal, l.Quantity)).ToList(),
            order.DiscountPercent, order.DiscountAmount, order.ServiceChargePercent, order.ServiceChargeApplies, Taxes(order), order.TaxTender, order.Rounding,
            bundles.Sum(b => b.Discount) + autoDeals.Sum(d => d.Discount)));
        return (result, autoDeals);
    }

    /// <summary>Recalculates the totals after lines, deals, discount or tender changed.</summary>
    public static void Reprice(Order order)
    {
        var (result, autoDeals) = Calculate(order, [], []);
        order.Subtotal = result.Subtotal;
        order.DiscountTotal = result.Discount;
        order.DealDiscountTotal = result.DealDiscount;
        order.AutoDealsJson = JsonSerializer.Serialize(autoDeals.Select(d => new AppliedDealDto(d.DealId, null, d.Name, d.Type, 1, 0, d.Discount)), Json);
        order.ServiceCharge = result.ServiceCharge;
        order.TaxTotal = result.TaxTotal;
        order.TaxBreakdownJson = JsonSerializer.Serialize(result.Taxes.Select(t => new TaxLineDto(t.Name, t.Percent, t.Inclusive, t.Amount)), Json);
        order.RoundingAdjustment = result.Rounding;
        order.Total = result.Total;
        order.PaymentStatus = OrderRules.PaymentStatusFor(order.Total, order.PaidTotal, order.RefundedTotal);
    }

    public static IReadOnlyList<TaxLineDto> TaxLines(Order order) =>
        JsonSerializer.Deserialize<List<TaxLineDto>>(order.TaxBreakdownJson, Json) ?? [];

    /// <summary>The deals on the order: combos first, then the automatic ones from the last repricing.</summary>
    public static IReadOnlyList<AppliedDealDto> AppliedDeals(Order order) =>
        order.DealApplications.Where(a => !a.IsVoided).OrderBy(a => a.CreatedAt)
            .Select(a => new AppliedDealDto(a.DealId, a.Id, a.Name, DealType.Bundle, a.Quantity, a.OriginalTotal, a.Discount))
            .Concat(JsonSerializer.Deserialize<List<AppliedDealDto>>(order.AutoDealsJson, Json) ?? [])
            .ToList();
}
