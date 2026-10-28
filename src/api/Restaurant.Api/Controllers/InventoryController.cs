using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Features.Inventory;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Inventory;

namespace Restaurant.Api.Controllers;

/// <summary>Stock items and recipes (whole restaurant); levels, ledger, receipts and counts for the selected branch.</summary>
[ApiController]
[Route("api/inventory")]
[Authorize]
public class InventoryController(StockService stock, PurchasingService purchasing) : ControllerBase
{
    private const string See = Permissions.Reports.Inventory;

    [HttpGet("units")]
    public IReadOnlyList<UnitDto> Units() => UnitCatalog.All;

    [HttpGet("items")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Count, Permissions.Inventory.Wastage, Permissions.Inventory.Adjust, Permissions.Reports.Inventory, Permissions.Menu.Manage)]
    public Task<IReadOnlyList<InventoryItemDto>> Items(CancellationToken ct) => stock.ListItemsAsync(ct);

    [HttpPost("items")]
    [RequirePermission(Permissions.Inventory.Adjust, Permissions.Menu.Manage)]
    public Task<InventoryItemDto> CreateItem(SaveInventoryItemRequest request, CancellationToken ct) => stock.SaveItemAsync(null, request, ct);

    [HttpPut("items/{id:guid}")]
    [RequirePermission(Permissions.Inventory.Adjust, Permissions.Menu.Manage)]
    public Task<InventoryItemDto> UpdateItem(Guid id, SaveInventoryItemRequest request, CancellationToken ct) => stock.SaveItemAsync(id, request, ct);

    [HttpGet("levels")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Count, Permissions.Inventory.Wastage, Permissions.Inventory.Adjust, Permissions.Reports.Inventory)]
    public Task<IReadOnlyList<StockLevelDto>> Levels(CancellationToken ct) => stock.LevelsAsync(ct);

    [HttpGet("low-stock")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Count, Permissions.Inventory.Wastage, Permissions.Inventory.Adjust, Permissions.Reports.Inventory)]
    public Task<IReadOnlyList<LowStockDto>> LowStock(CancellationToken ct) => stock.LowStockAsync(ct);

    [HttpGet("movements")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Count, Permissions.Inventory.Wastage, Permissions.Inventory.Adjust, Permissions.Reports.Inventory)]
    public Task<IReadOnlyList<StockMovementDto>> Movements([FromQuery] Guid? itemId, [FromQuery] int take = 100, CancellationToken ct = default) =>
        stock.MovementsAsync(itemId, take, ct);

    [HttpGet("receipts")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Reports.Inventory)]
    public Task<IReadOnlyList<StockReceiptDto>> Receipts(CancellationToken ct) => stock.ReceiptsAsync(ct);

    [HttpPost("receipts")]
    [RequirePermission(Permissions.Inventory.Receive)]
    public Task<StockReceiptDto> Receive(CreateStockReceiptRequest request, CancellationToken ct) => stock.ReceiveAsync(request, ct);

    /// <summary>Wastage (inventory.wastage) or an adjustment (inventory.adjust, or anyone with a manager's approval).</summary>
    [HttpPost("adjustments")]
    public Task<StockMovementDto> Adjust(StockAdjustmentRequest request, CancellationToken ct) => stock.AdjustAsync(request, ct);

    [HttpGet("counts")]
    [RequirePermission(Permissions.Inventory.Count, Permissions.Reports.Inventory)]
    public Task<IReadOnlyList<StockCountDto>> Counts(CancellationToken ct) => stock.CountsAsync(ct);

    [HttpPost("counts")]
    [RequirePermission(Permissions.Inventory.Count)]
    public Task<StockCountDto> Count(CreateStockCountRequest request, CancellationToken ct) => stock.CountAsync(request, ct);

    [HttpGet("recipes")]
    [RequirePermission(Permissions.Menu.Manage, Permissions.Inventory.Adjust, Permissions.Reports.Inventory)]
    public Task<IReadOnlyList<RecipeDto>> Recipes(CancellationToken ct) => stock.RecipesAsync(ct);

    [HttpPut("recipes")]
    [RequirePermission(Permissions.Menu.Manage)]
    public Task<RecipeDto> SaveRecipe(RecipeDto request, CancellationToken ct) => stock.SaveRecipeAsync(request, ct);

    // ---------------------------------------------------------------- suppliers and purchase orders

    [HttpGet("suppliers")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust, See)]
    public Task<IReadOnlyList<SupplierDto>> Suppliers(CancellationToken ct) => purchasing.SuppliersAsync(ct);

    [HttpPost("suppliers")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust)]
    public Task<SupplierDto> CreateSupplier(SaveSupplierRequest request, CancellationToken ct) => purchasing.SaveSupplierAsync(null, request, ct);

    [HttpPut("suppliers/{id:guid}")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust)]
    public Task<SupplierDto> UpdateSupplier(Guid id, SaveSupplierRequest request, CancellationToken ct) => purchasing.SaveSupplierAsync(id, request, ct);

    [HttpGet("purchase-orders")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust, See)]
    public Task<IReadOnlyList<PurchaseOrderDto>> PurchaseOrders(CancellationToken ct) => purchasing.PurchaseOrdersAsync(ct);

    [HttpGet("purchase-orders/{id:guid}")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust, See)]
    public Task<PurchaseOrderDto> PurchaseOrder(Guid id, CancellationToken ct) => purchasing.GetAsync(id, ct);

    [HttpPost("purchase-orders")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust)]
    public Task<PurchaseOrderDto> CreatePurchaseOrder(SavePurchaseOrderRequest request, CancellationToken ct) => purchasing.SaveAsync(null, request, ct);

    [HttpPut("purchase-orders/{id:guid}")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust)]
    public Task<PurchaseOrderDto> UpdatePurchaseOrder(Guid id, SavePurchaseOrderRequest request, CancellationToken ct) => purchasing.SaveAsync(id, request, ct);

    /// <summary>Draft → Ordered (sent to the supplier). Goods are then received with POST receipts and its id.</summary>
    [HttpPost("purchase-orders/{id:guid}/place")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust)]
    public Task<PurchaseOrderDto> PlacePurchaseOrder(Guid id, CancellationToken ct) => purchasing.PlaceAsync(id, ct);

    [HttpPost("purchase-orders/{id:guid}/cancel")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust)]
    public Task<PurchaseOrderDto> CancelPurchaseOrder(Guid id, CancellationToken ct) => purchasing.CancelAsync(id, ct);

    // ---------------------------------------------------------------- transfers between branches

    [HttpGet("transfers")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust, See)]
    public Task<IReadOnlyList<StockTransferDto>> Transfers(CancellationToken ct) => stock.TransfersAsync(ct);

    /// <summary>Sends stock from the selected branch to another one.</summary>
    [HttpPost("transfers")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust)]
    public Task<StockTransferDto> SendTransfer(CreateStockTransferRequest request, CancellationToken ct) => stock.SendTransferAsync(request, ct);

    /// <summary>The selected branch (the destination) books it in.</summary>
    [HttpPost("transfers/{id:guid}/receive")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust)]
    public Task<StockTransferDto> ReceiveTransfer(Guid id, CancellationToken ct) => stock.ReceiveTransferAsync(id, ct);

    /// <summary>The selected branch (the sender) takes it back while it's in transit.</summary>
    [HttpPost("transfers/{id:guid}/cancel")]
    [RequirePermission(Permissions.Inventory.Receive, Permissions.Inventory.Adjust)]
    public Task<StockTransferDto> CancelTransfer(Guid id, CancellationToken ct) => stock.CancelTransferAsync(id, ct);
}
