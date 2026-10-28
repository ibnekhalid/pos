using Restaurant.Contracts.Deals;
using Restaurant.Contracts.Menu;
using Restaurant.Contracts.Orders;

namespace Restaurant.Client.UI.Pages.Portal.Pos;

/// <summary>A combo deal in the POS cart: one choice per part of the deal. Prices here are for display only.</summary>
public sealed class DealCartEntry
{
    public required DealDto Deal { get; init; }
    public int Quantity { get; set; } = 1;

    /// <summary>One per group, in group order (null = not chosen yet).</summary>
    public List<CartLine?> Choices { get; init; } = [];

    public IReadOnlyList<IGrouping<int, DealComponentDto>> Groups => Deal.Items.GroupBy(i => i.Group).OrderBy(g => g.Key).ToList();

    public decimal Total => ((Deal.BundlePrice ?? 0) + Choices.Zip(Groups).Sum(x => (x.First?.Modifiers.Sum(m => m.PriceDelta) ?? 0) * x.Second.First().Quantity)) * Quantity;

    public DealSelection ToSelection() => new(Deal.Id, Quantity,
        Choices.Select(c => new OrderLineRequest(c!.Item.Id, c.Variant?.Id, 1, c.Modifiers.Select(m => m.Id).ToList(), null)).ToList());

    public static DealCartEntry For(DealDto deal) => new() { Deal = deal, Choices = deal.Items.Select(i => i.Group).Distinct().Select(_ => (CartLine?)null).ToList() };
}
