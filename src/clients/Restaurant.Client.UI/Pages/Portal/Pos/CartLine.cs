using Restaurant.Contracts.Menu;

namespace Restaurant.Client.UI.Pages.Portal.Pos;

/// <summary>A line in the POS cart. Prices here are for display; the server prices the order.</summary>
public sealed class CartLine
{
    public Guid Key { get; } = Guid.NewGuid();
    public required PosMenuItemDto Item { get; init; }
    public MenuItemVariantDto? Variant { get; set; }
    public List<ModifierOptionDto> Modifiers { get; set; } = [];
    public int Quantity { get; set; } = 1;
    public string? Notes { get; set; }

    public decimal UnitPrice => (Variant?.Price ?? Item.Price) + Modifiers.Sum(m => m.PriceDelta);
    public decimal Total => UnitPrice * Quantity;

    /// <summary>Same item, size, add-ons and note: tapping it again adds to the quantity.</summary>
    public bool SameAs(CartLine other) =>
        other.Item.Id == Item.Id && other.Variant?.Id == Variant?.Id && other.Notes == Notes
        && other.Modifiers.Select(m => m.Id).Order().SequenceEqual(Modifiers.Select(m => m.Id).Order());
}
