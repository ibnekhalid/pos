using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Tenancy;

namespace Restaurant.Client.Core.Offline;

/// <summary>
/// The bill for an order taken offline, worked out on the counter PC from the cached tax rates and branch rules.
/// It follows the server's OrderPricer step for step (a unit test keeps them equal) without deals or discounts,
/// which aren't offered offline. The server prices the order again when it syncs and flags any difference.
/// </summary>
public static class OfflineEstimate
{
    public record Result(decimal Subtotal, decimal ServiceCharge, decimal ServiceChargePercent, IReadOnlyList<TaxLineDto> Taxes, decimal TaxTotal,
        decimal Rounding, decimal Total);

    /// <param name="lineTotals">(unit price + add-ons) × quantity, per line.</param>
    /// <param name="cash">How it is paid: cash-only and non-cash-only rates follow the tender (unpaid counts as cash, as on the server).</param>
    public static Result Price(IEnumerable<decimal> lineTotals, OrderType type, bool cash, IEnumerable<TaxRateDto> rates, BranchDto branch)
    {
        var subtotal = R(lineTotals.Sum());
        var net = subtotal;
        var typeFlag = (OrderTypes)(int)type;
        var taxes = rates
            .Where(r => r.IsActive && (r.BranchId == null || r.BranchId == branch.Id) && r.AppliesTo.HasFlag(typeFlag))
            .Where(r => r.PaymentScope switch
            {
                TaxPaymentScope.CashOnly => cash,
                TaxPaymentScope.NonCashOnly => !cash,
                _ => true
            })
            .ToList();
        var inclusive = taxes.Where(t => t.IsInclusive).ToList();
        var exclusive = taxes.Where(t => !t.IsInclusive).ToList();

        var inclusiveRate = inclusive.Sum(t => t.Percent);
        var taxBase = inclusiveRate == 0 ? net : R(net / (1 + inclusiveRate / 100m));
        var inclusiveTotal = net - taxBase;

        var lines = new List<TaxLineDto>();
        var allocated = 0m;
        for (var i = 0; i < inclusive.Count; i++)
        {
            var amount = i == inclusive.Count - 1 ? inclusiveTotal - allocated : R(taxBase * inclusive[i].Percent / 100m);
            allocated += amount;
            lines.Add(new TaxLineDto(inclusive[i].Name, inclusive[i].Percent, true, amount));
        }

        var settings = branch.Settings;
        var serviceApplies = settings.ServiceChargePercent > 0 && settings.ServiceChargeAppliesTo.HasFlag(typeFlag);
        var serviceCharge = serviceApplies ? R(taxBase * settings.ServiceChargePercent / 100m) : 0m;

        foreach (var tax in exclusive)
        {
            lines.Add(new TaxLineDto(tax.Name, tax.Percent, false, R(taxBase * tax.Percent / 100m)));
        }

        var beforeRounding = net + serviceCharge + lines.Where(t => !t.Inclusive).Sum(t => t.Amount);
        var total = settings.Rounding switch
        {
            RoundingMode.NearestRupee => Math.Round(beforeRounding, 0, MidpointRounding.AwayFromZero),
            RoundingMode.NearestFive => Math.Round(beforeRounding / 5m, 0, MidpointRounding.AwayFromZero) * 5m,
            RoundingMode.NearestTen => Math.Round(beforeRounding / 10m, 0, MidpointRounding.AwayFromZero) * 10m,
            _ => R(beforeRounding)
        };
        return new Result(subtotal, serviceCharge, serviceApplies ? settings.ServiceChargePercent : 0, lines, lines.Sum(t => t.Amount), total - beforeRounding, total);
    }

    private static decimal R(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
