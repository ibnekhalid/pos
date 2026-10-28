using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>Something the kitchen stocks (chicken, bread, oil). Quantities are kept in BaseUnit (g, ml or pc).</summary>
public class InventoryItem : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public string? NameUr { get; set; }
    public string BaseUnit { get; set; } = "pc";
    public string? Category { get; set; }
    public decimal ReorderLevel { get; set; }
    public bool IsActive { get; set; } = true;
    public List<InventoryItemUnit> Units { get; set; } = [];
}

/// <summary>An item's own unit: "tray" = 30 pc, "bottle" = 1500 ml.</summary>
public class InventoryItemUnit : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid InventoryItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal FactorToBase { get; set; }
}

/// <summary>
/// A branch's weighted-average cost for an item. On-hand quantity is NOT stored here: it is always the
/// sum of the ledger, so it can never drift from it (PLAN.md section 9).
/// </summary>
public class InventoryBalance : BranchEntity
{
    public Guid InventoryItemId { get; set; }
    public decimal AverageCost { get; set; }
    public uint Version { get; set; }
}

/// <summary>The stock ledger. Never edited; corrections are new entries.</summary>
public class StockMovement : BranchEntity
{
    public Guid InventoryItemId { get; set; }
    public StockMovementType Type { get; set; }

    /// <summary>Signed, in the item's base unit: + in, − out.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Per base unit.</summary>
    public decimal UnitCost { get; set; }

    public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }

    /// <summary>Unique per tenant ("sale:{line}:{item}", "rev:{line}:{item}"...): the same event is never booked twice.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public string? Reason { get; set; }
    public Guid? ApprovedByUserId { get; set; }
}

public class StockReceipt : BranchEntity
{
    /// <summary>The supplier's name as it was (also for receipts without a supplier record).</summary>
    public string? Supplier { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? Notes { get; set; }
    public decimal TotalCost { get; set; }
    public int LineCount { get; set; }
    public Guid ClientRequestId { get; set; }
}

public class StockCount : BranchEntity
{
    public string? Notes { get; set; }
    public decimal VarianceValue { get; set; }
    public string LinesJson { get; set; } = "[]";
    public Guid? ApprovedByUserId { get; set; }
    public Guid ClientRequestId { get; set; }
}

/// <summary>
/// One ingredient of a recipe: for a menu item (all sizes: VariantId null), one size (overrides the item's
/// recipe), or an add-on option (added on top).
/// </summary>
public class RecipeLine : TenantEntity
{
    public Guid? MenuItemId { get; set; }
    public Guid? VariantId { get; set; }
    public Guid? ModifierOptionId { get; set; }
    public Guid InventoryItemId { get; set; }

    /// <summary>In the stock item's base unit.</summary>
    public decimal Quantity { get; set; }

    public decimal EnteredQuantity { get; set; }
    public string EnteredUnit { get; set; } = "pc";
}
