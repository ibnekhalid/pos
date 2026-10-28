using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Features.Menu;
using Restaurant.Application.Features.Tables;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Menu;
using Restaurant.Contracts.Tables;

namespace Restaurant.Api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class MenuController(MenuService menu, TableService tables) : ControllerBase
{
    /// <summary>The selected branch's menu for the POS screen.</summary>
    [HttpGet("menu/pos")]
    public Task<PosMenuDto> PosMenu(CancellationToken ct) => menu.GetPosMenuAsync(ct);

    [HttpGet("menu/items")]
    public Task<IReadOnlyList<MenuItemDto>> Items(CancellationToken ct) => menu.ListItemsAsync(ct);

    [HttpGet("menu/items/{id:guid}")]
    public Task<MenuItemDto> Item(Guid id, CancellationToken ct) => menu.GetItemAsync(id, ct);

    [HttpPost("menu/items")]
    [RequirePermission(Permissions.Menu.Manage)]
    public Task<MenuItemDto> CreateItem(SaveMenuItemRequest request, CancellationToken ct) => menu.SaveItemAsync(null, request, ct);

    [HttpPut("menu/items/{id:guid}")]
    [RequirePermission(Permissions.Menu.Manage)]
    public Task<MenuItemDto> UpdateItem(Guid id, SaveMenuItemRequest request, CancellationToken ct) => menu.SaveItemAsync(id, request, ct);

    /// <summary>Sold out / unavailable / own price at the selected branch.</summary>
    [HttpPut("menu/items/{id:guid}/availability")]
    [RequirePermission(Permissions.Menu.Availability, Permissions.Menu.Manage)]
    public async Task<IActionResult> SetAvailability(Guid id, SetAvailabilityRequest request, CancellationToken ct)
    {
        await menu.SetAvailabilityAsync(id, request, ct);
        return NoContent();
    }

    [HttpGet("menu/categories")]
    public Task<IReadOnlyList<MenuCategoryDto>> Categories(CancellationToken ct) => menu.ListCategoriesAsync(ct);

    [HttpPost("menu/categories")]
    [RequirePermission(Permissions.Menu.Manage)]
    public Task<MenuCategoryDto> CreateCategory(SaveMenuCategoryRequest request, CancellationToken ct) => menu.SaveCategoryAsync(null, request, ct);

    [HttpPut("menu/categories/{id:guid}")]
    [RequirePermission(Permissions.Menu.Manage)]
    public Task<MenuCategoryDto> UpdateCategory(Guid id, SaveMenuCategoryRequest request, CancellationToken ct) => menu.SaveCategoryAsync(id, request, ct);

    [HttpGet("menu/modifier-groups")]
    public Task<IReadOnlyList<ModifierGroupDto>> ModifierGroups(CancellationToken ct) => menu.ListModifierGroupsAsync(ct);

    [HttpPost("menu/modifier-groups")]
    [RequirePermission(Permissions.Menu.Manage)]
    public Task<ModifierGroupDto> CreateModifierGroup(SaveModifierGroupRequest request, CancellationToken ct) => menu.SaveModifierGroupAsync(null, request, ct);

    [HttpPut("menu/modifier-groups/{id:guid}")]
    [RequirePermission(Permissions.Menu.Manage)]
    public Task<ModifierGroupDto> UpdateModifierGroup(Guid id, SaveModifierGroupRequest request, CancellationToken ct) => menu.SaveModifierGroupAsync(id, request, ct);

    [HttpGet("menu/stations")]
    public Task<IReadOnlyList<KitchenStationDto>> Stations(CancellationToken ct) => menu.ListStationsAsync(ct);

    [HttpPost("menu/stations")]
    [RequirePermission(Permissions.Menu.Manage)]
    public Task<KitchenStationDto> CreateStation(SaveKitchenStationRequest request, CancellationToken ct) => menu.SaveStationAsync(null, request, ct);

    [HttpPut("menu/stations/{id:guid}")]
    [RequirePermission(Permissions.Menu.Manage)]
    public Task<KitchenStationDto> UpdateStation(Guid id, SaveKitchenStationRequest request, CancellationToken ct) => menu.SaveStationAsync(id, request, ct);

    [HttpGet("dining-areas")]
    public Task<IReadOnlyList<DiningAreaDto>> Areas(CancellationToken ct) => tables.ListAreasAsync(ct);

    [HttpPost("dining-areas")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<DiningAreaDto> CreateArea(SaveDiningAreaRequest request, CancellationToken ct) => tables.SaveAreaAsync(null, request, ct);

    [HttpPut("dining-areas/{id:guid}")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<DiningAreaDto> UpdateArea(Guid id, SaveDiningAreaRequest request, CancellationToken ct) => tables.SaveAreaAsync(id, request, ct);

    [HttpGet("tables")]
    public Task<IReadOnlyList<TableDto>> Tables(CancellationToken ct) => tables.ListTablesAsync(ct);

    [HttpPost("tables")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<TableDto> CreateTable(SaveTableRequest request, CancellationToken ct) => tables.SaveTableAsync(null, request, ct);

    [HttpPut("tables/{id:guid}")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<TableDto> UpdateTable(Guid id, SaveTableRequest request, CancellationToken ct) => tables.SaveTableAsync(id, request, ct);

    /// <summary>The table has been cleared and reset after the last guests.</summary>
    [HttpPost("tables/{id:guid}/clean")]
    [RequirePermission(Permissions.Orders.Create)]
    public Task<TableDto> MarkClean(Guid id, CancellationToken ct) => tables.MarkCleanAsync(id, ct);
}
