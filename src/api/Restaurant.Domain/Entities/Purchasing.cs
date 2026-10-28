using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>Someone the restaurant buys stock from. Shared by every branch.</summary>
public class Supplier : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// An order to a supplier for one branch. It changes no stock by itself: goods are booked by stock receipts
/// against it, which fill in each line's received quantity (PLAN.md section 9).
/// </summary>
public class PurchaseOrder : BranchEntity
{
    public int Sequence { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;
    public DateOnly? ExpectedDate { get; set; }
    public string? Notes { get; set; }
    public decimal Total { get; set; }
    public DateTimeOffset? OrderedAt { get; set; }
    public uint Version { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];
}

public class PurchaseOrderLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid InventoryItemId { get; set; }

    /// <summary>As ordered: quantity in Unit, cost per Unit.</summary>
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "pc";
    public decimal UnitCost { get; set; }

    /// <summary>In the item's base unit, so ordered and received compare whatever unit the receipt used.</summary>
    public decimal QuantityBase { get; set; }
    public decimal ReceivedBase { get; set; }
}

/// <summary>
/// Stock sent from one branch to another. Sending books it out of the source at the source's average cost;
/// receiving books it into the destination at that cost. Cancelling while in transit puts it back.
/// </summary>
public class StockTransfer : TenantEntity
{
    public int Sequence { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid FromBranchId { get; set; }
    public Guid ToBranchId { get; set; }
    public StockTransferStatus Status { get; set; } = StockTransferStatus.InTransit;
    public string? Notes { get; set; }
    public decimal TotalCost { get; set; }
    public Guid ClientRequestId { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public Guid? ReceivedByUserId { get; set; }
    public uint Version { get; set; }
    public List<StockTransferLine> Lines { get; set; } = [];
}

public class StockTransferLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid StockTransferId { get; set; }
    public Guid InventoryItemId { get; set; }

    /// <summary>In the item's base unit.</summary>
    public decimal Quantity { get; set; }

    /// <summary>The source branch's average cost per base unit when it was sent.</summary>
    public decimal UnitCost { get; set; }
}
