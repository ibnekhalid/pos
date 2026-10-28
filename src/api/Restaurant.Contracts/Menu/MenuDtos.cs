using Restaurant.Contracts.Deals;
using Restaurant.Contracts.Enums;

namespace Restaurant.Contracts.Menu;

public record KitchenStationDto(Guid Id, string Name, int SortOrder, bool IsActive);
public record SaveKitchenStationRequest(string Name, int SortOrder, bool IsActive = true);

public record MenuCategoryDto(Guid Id, string Name, string? NameUr, int SortOrder, string? Color, bool IsActive);
public record SaveMenuCategoryRequest(string Name, string? NameUr, int SortOrder, string? Color, bool IsActive = true);

public record ModifierOptionDto(Guid Id, string Name, string? NameUr, decimal PriceDelta, int SortOrder, bool IsActive);
/// <param name="MinSelect">0 = optional. MaxSelect 1 = pick one.</param>
public record ModifierGroupDto(Guid Id, string Name, string? NameUr, int MinSelect, int MaxSelect, int SortOrder, IReadOnlyList<ModifierOptionDto> Options);
public record SaveModifierOptionRequest(Guid? Id, string Name, string? NameUr, decimal PriceDelta, int SortOrder, bool IsActive = true);
public record SaveModifierGroupRequest(string Name, string? NameUr, int MinSelect, int MaxSelect, int SortOrder, IReadOnlyList<SaveModifierOptionRequest> Options);

public record MenuItemVariantDto(Guid Id, string Name, string? NameUr, decimal Price, string? Sku, int SortOrder, bool IsActive);
public record SaveMenuItemVariantRequest(Guid? Id, string Name, string? NameUr, decimal Price, string? Sku, int SortOrder, bool IsActive = true);

/// <param name="Price">Used when the item has no variants.</param>
public record MenuItemDto(
    Guid Id, Guid CategoryId, string Name, string? NameUr, string? Description, string? Sku, decimal Price,
    Guid? KitchenStationId, int SortOrder, bool IsActive, IReadOnlyList<MenuItemVariantDto> Variants, IReadOnlyList<Guid> ModifierGroupIds);

public record SaveMenuItemRequest(
    Guid CategoryId, string Name, string? NameUr, string? Description, string? Sku, decimal Price,
    Guid? KitchenStationId, int SortOrder, bool IsActive, IReadOnlyList<SaveMenuItemVariantRequest> Variants, IReadOnlyList<Guid> ModifierGroupIds);

/// <param name="PriceOverride">This branch's price for an item without variants (null = menu price).</param>
public record SetAvailabilityRequest(MenuAvailability Availability, decimal? PriceOverride);

/// <summary>Everything the POS screen needs for the selected branch, in one call.</summary>
/// <param name="Deals">Combo deals the cashier can pick right now at this branch.</param>
public record PosMenuDto(IReadOnlyList<MenuCategoryDto> Categories, IReadOnlyList<PosMenuItemDto> Items, IReadOnlyList<ModifierGroupDto> ModifierGroups,
    IReadOnlyList<DealDto>? Deals = null);

public record PosMenuItemDto(
    Guid Id, Guid CategoryId, string Name, string? NameUr, string? Description, decimal Price, MenuAvailability Availability,
    decimal? PriceOverride, IReadOnlyList<MenuItemVariantDto> Variants, IReadOnlyList<Guid> ModifierGroupIds);
