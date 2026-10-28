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
public class InventoryController(StockService stock) : ControllerBase
{
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
}
