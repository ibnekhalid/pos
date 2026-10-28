using Restaurant.Contracts.Enums;

namespace Restaurant.Domain.Pricing;

/// <summary>An automatic deal as it was when the order was placed (frozen on the order).</summary>
public record DealRule(Guid DealId, string Name, DealType Type, DealScope Scope, bool Combinable, string? CouponCode, decimal? MinimumSubtotal,
    decimal? Percent, decimal? Amount, int? BuyQuantity, int? GetQuantity, decimal? GetPercentOff, IReadOnlyList<DealRuleItem> Items);

public record DealRuleItem(Guid MenuItemId, Guid? VariantId);

/// <param name="UnitPrice">Price of one unit including its add-ons.</param>
public record DealLine(Guid MenuItemId, Guid? VariantId, decimal UnitPrice, int Quantity);

public record DealResult(Guid DealId, string Name, DealType Type, decimal Discount);

/// <summary>
/// Automatic deals (PLAN.md section 6): buy-X-get-Y, percent and amount off — on the order or on listed
/// items, optionally behind a coupon or a minimum subtotal. Combo lines are not included (they're already
/// discounted). Combinable deals stack; a non-combinable deal is used only if it alone beats everything
/// else together. The total never exceeds the amount it applies to.
/// </summary>
public static class DealEngine
{
    public static IReadOnlyList<DealResult> Evaluate(IReadOnlyList<DealLine> lines, IReadOnlyList<DealRule> rules, decimal orderAmount, string? couponCode)
    {
        var candidates = rules
            .Where(r => r.CouponCode is null || string.Equals(r.CouponCode, couponCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(r => r.MinimumSubtotal is null || orderAmount >= r.MinimumSubtotal)
            .Select(r => new DealResult(r.DealId, r.Name, r.Type, Math.Round(Discount(r, lines, orderAmount), 2, MidpointRounding.AwayFromZero)))
            .Where(d => d.Discount > 0)
            .ToList();

        var combinable = candidates.Where(d => rules.First(r => r.DealId == d.DealId).Combinable).ToList();
        var bestAlone = candidates.Where(d => !rules.First(r => r.DealId == d.DealId).Combinable).OrderByDescending(d => d.Discount).FirstOrDefault();

        var chosen = bestAlone is not null && bestAlone.Discount > combinable.Sum(d => d.Discount) ? [bestAlone] : combinable;

        // Never discount more than there is.
        var remaining = orderAmount;
        var result = new List<DealResult>();
        foreach (var deal in chosen.OrderByDescending(d => d.Discount))
        {
            var amount = Math.Min(deal.Discount, remaining);
            if (amount <= 0) break;
            result.Add(deal with { Discount = amount });
            remaining -= amount;
        }
        return result;
    }

    private static decimal Discount(DealRule rule, IReadOnlyList<DealLine> lines, decimal orderAmount)
    {
        var matching = lines.Where(l => rule.Items.Any(i => i.MenuItemId == l.MenuItemId && (i.VariantId is null || i.VariantId == l.VariantId))).ToList();
        return (rule.Type, rule.Scope) switch
        {
            (DealType.PercentOff, DealScope.Order) => orderAmount * (rule.Percent ?? 0) / 100m,
            (DealType.PercentOff, DealScope.Items) => matching.Sum(l => l.UnitPrice * l.Quantity) * (rule.Percent ?? 0) / 100m,
            (DealType.AmountOff, DealScope.Order) => Math.Min(rule.Amount ?? 0, orderAmount),
            (DealType.AmountOff, DealScope.Items) => matching.Sum(l => Math.Min(rule.Amount ?? 0, l.UnitPrice) * l.Quantity),
            (DealType.BuyXGetY, _) => BuyGet(rule, matching),
            _ => 0
        };
    }

    /// <summary>Every full group of (buy + get) qualifying units: the cheapest "get" units of the group are discounted.</summary>
    private static decimal BuyGet(DealRule rule, IReadOnlyList<DealLine> matching)
    {
        var buy = rule.BuyQuantity ?? 0;
        var get = rule.GetQuantity ?? 0;
        if (buy < 1 || get < 1) return 0;

        var units = matching.SelectMany(l => Enumerable.Repeat(l.UnitPrice, l.Quantity)).OrderByDescending(p => p).ToList();
        var groups = units.Count / (buy + get);
        var percent = (rule.GetPercentOff ?? 100) / 100m;
        var discount = 0m;
        for (var g = 0; g < groups; g++)
        {
            discount += units.Skip(g * (buy + get) + buy).Take(get).Sum() * percent;
        }
        return discount;
    }
}
