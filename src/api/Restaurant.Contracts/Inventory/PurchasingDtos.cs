using Restaurant.Contracts.Enums;

namespace Restaurant.Contracts.Inventory;

public record SupplierDto(Guid Id, string Name, string? ContactName, string? Phone, string? Address, string? Notes, bool IsActive);

public record SaveSupplierRequest(string Name, string? ContactName, string? Phone, string? Address, string? Notes, bool IsActive = true);

/// <param name="Quantity">In Unit, as ordered; UnitCost is per Unit.</param>
public record PurchaseOrderLineRequest(Guid InventoryItemId, decimal Quantity, string Unit, decimal UnitCost);

public record SavePurchaseOrderRequest(Guid SupplierId, DateOnly? ExpectedDate, string? Notes, IReadOnlyList<PurchaseOrderLineRequest> Lines);

/// <param name="QuantityBase">Ordered, in the item's base unit.</param>
/// <param name="ReceivedBase">Received so far, in the item's base unit.</param>
public record PurchaseOrderLineDto(Guid Id, Guid ItemId, string ItemName, string BaseUnit, decimal Quantity, string Unit, decimal UnitCost, decimal LineTotal,
    decimal QuantityBase, decimal ReceivedBase);

public record PurchaseOrderDto(Guid Id, string Number, Guid SupplierId, string SupplierName, string? SupplierPhone, PurchaseOrderStatus Status,
    DateOnly? ExpectedDate, string? Notes, decimal Total, DateTimeOffset CreatedAt, DateTimeOffset? OrderedAt, string? CreatedByName,
    IReadOnlyList<PurchaseOrderLineDto> Lines);

/// <param name="Quantity">In Unit (converted to the item's base unit).</param>
public record TransferLineRequest(Guid InventoryItemId, decimal Quantity, string Unit);

/// <summary>Sends stock from the selected branch to ToBranchId.</summary>
public record CreateStockTransferRequest(Guid ClientRequestId, Guid ToBranchId, string? Notes, IReadOnlyList<TransferLineRequest> Lines);

/// <param name="Quantity">In the base unit.</param>
/// <param name="UnitCost">The sending branch's average cost per base unit when it was sent.</param>
public record StockTransferLineDto(Guid ItemId, string ItemName, string BaseUnit, decimal Quantity, decimal UnitCost);

public record StockTransferDto(Guid Id, string Number, Guid FromBranchId, string FromBranchName, Guid ToBranchId, string ToBranchName,
    StockTransferStatus Status, string? Notes, decimal TotalCost, DateTimeOffset SentAt, string? SentByName, DateTimeOffset? ReceivedAt,
    string? ReceivedByName, IReadOnlyList<StockTransferLineDto> Lines);
