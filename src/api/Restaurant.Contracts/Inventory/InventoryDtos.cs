using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;

namespace Restaurant.Contracts.Inventory;

public record UnitDto(string Code, string Name, UnitDimension Dimension, decimal ToBase);

/// <summary>
/// Standard units. Each stock item keeps its quantities in one base unit (g, ml or pc); kg, l and dozen
/// convert by fixed factors, and an item can add its own units ("tray" = 30 pc). Grams and kilograms are
/// never mixed without converting (PLAN.md section 9).
/// </summary>
public static class UnitCatalog
{
    public static IReadOnlyList<UnitDto> All { get; } =
    [
        new("g", "gram", UnitDimension.Mass, 1),
        new("kg", "kilogram", UnitDimension.Mass, 1000),
        new("ml", "millilitre", UnitDimension.Volume, 1),
        new("l", "litre", UnitDimension.Volume, 1000),
        new("pc", "piece", UnitDimension.Count, 1),
        new("dozen", "dozen", UnitDimension.Count, 12)
    ];

    public static IReadOnlyList<string> BaseUnits { get; } = ["g", "ml", "pc"];

    public static UnitDto? Find(string? code) => All.FirstOrDefault(u => string.Equals(u.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Quantity in the item's base unit, or null when the unit can't be used for the item. Standard units
    /// convert within their dimension; anything else must be one of the item's own units.
    /// </summary>
    public static decimal? ToBase(string baseUnit, IEnumerable<InventoryItemUnitDto> itemUnits, string unit, decimal quantity)
    {
        var target = Find(baseUnit);
        if (Find(unit) is { } standard)
        {
            return target is not null && standard.Dimension == target.Dimension ? quantity * standard.ToBase : null;
        }
        var own = itemUnits.FirstOrDefault(u => string.Equals(u.Name, unit, StringComparison.OrdinalIgnoreCase));
        return own is null ? null : quantity * own.FactorToBase;
    }

    /// <summary>"2.5 kg" for 2500 g, "750 g" for 750 g: the largest standard unit that keeps it readable.</summary>
    public static string Describe(decimal baseQuantity, string baseUnit)
    {
        var dimension = Find(baseUnit)?.Dimension;
        var big = All.Where(u => u.Dimension == dimension && u.ToBase > 1 && Math.Abs(baseQuantity) >= u.ToBase && u.Code != "dozen")
            .OrderByDescending(u => u.ToBase).FirstOrDefault();
        return big is null
            ? $"{Math.Round(baseQuantity, 3).ToString("0.###", global::System.Globalization.CultureInfo.InvariantCulture)} {baseUnit}"
            : $"{Math.Round(baseQuantity / big.ToBase, 3).ToString("0.###", global::System.Globalization.CultureInfo.InvariantCulture)} {big.Code}";
    }
}

public record InventoryItemUnitDto(string Name, decimal FactorToBase);

public record InventoryItemDto(Guid Id, string Name, string? NameUr, string BaseUnit, string? Category, decimal ReorderLevel, bool IsActive,
    IReadOnlyList<InventoryItemUnitDto> Units);

/// <param name="ReorderLevel">In the base unit: at or below it the item shows as low.</param>
public record SaveInventoryItemRequest(string Name, string? NameUr, string BaseUnit, string? Category, decimal ReorderLevel, bool IsActive,
    IReadOnlyList<InventoryItemUnitDto> Units);

/// <param name="AverageCost">Per base unit, weighted average of receipts at this branch.</param>
/// <param name="Value">Estimated: on hand × average cost.</param>
public record StockLevelDto(Guid ItemId, string Name, string BaseUnit, string? Category, decimal OnHand, decimal ReorderLevel,
    decimal AverageCost, decimal Value, bool IsLow, IReadOnlyList<InventoryItemUnitDto> Units);

public record StockMovementDto(Guid Id, Guid ItemId, string ItemName, string BaseUnit, StockMovementType Type, decimal Quantity, decimal UnitCost,
    string? Reference, string? Reason, DateTimeOffset At, string? ByName);

/// <param name="UnitCost">Cost per entered unit (per kg if the quantity is in kg).</param>
public record ReceiptLineRequest(Guid InventoryItemId, decimal Quantity, string Unit, decimal UnitCost);

public record CreateStockReceiptRequest(Guid ClientRequestId, string? Supplier, string? InvoiceNumber, string? Notes, IReadOnlyList<ReceiptLineRequest> Lines);

public record StockReceiptDto(Guid Id, string? Supplier, string? InvoiceNumber, decimal TotalCost, int LineCount, DateTimeOffset At, string? ByName);

/// <param name="Type">Wastage (Quantity = how much was thrown away) or Adjustment (Quantity signed: + found, − missing).</param>
public record StockAdjustmentRequest(Guid ClientRequestId, Guid InventoryItemId, StockMovementType Type, decimal Quantity, string Unit, string Reason,
    Approval? Approval = null);

public record StockCountLineRequest(Guid InventoryItemId, decimal Counted, string Unit);

/// <summary>A physical count: the difference to the books is posted as a count variance.</summary>
public record CreateStockCountRequest(Guid ClientRequestId, IReadOnlyList<StockCountLineRequest> Lines, string? Notes);

public record StockCountLineDto(Guid ItemId, string ItemName, string BaseUnit, decimal SystemQuantity, decimal Counted, decimal Variance, decimal VarianceValue);

public record StockCountDto(Guid Id, DateTimeOffset At, string? ByName, string? Notes, decimal VarianceValue, IReadOnlyList<StockCountLineDto> Lines);

/// <param name="Quantity">In <paramref name="Unit"/>; stored converted to the stock item's base unit.</param>
public record RecipeLineDto(Guid InventoryItemId, decimal Quantity, string Unit);

/// <summary>What one menu item (all sizes), one size, or one add-on option uses from stock.</summary>
public record RecipeDto(Guid? MenuItemId, Guid? VariantId, Guid? ModifierOptionId, IReadOnlyList<RecipeLineDto> Lines);
