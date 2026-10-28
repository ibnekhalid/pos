using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>Where an item is prepared (Kitchen, Grill, Drinks...). Tickets are split per station.</summary>
public class KitchenStation : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class MenuCategory : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public string? NameUr { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Tile colour on the POS screen.</summary>
    public string? Color { get; set; }

    public bool IsActive { get; set; } = true;
}

public class MenuItem : TenantEntity
{
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameUr { get; set; }
    public string? Description { get; set; }
    public string? Sku { get; set; }

    /// <summary>The price when the item has no variants.</summary>
    public decimal Price { get; set; }

    public Guid? KitchenStationId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public List<MenuItemVariant> Variants { get; set; } = [];
    public List<MenuItemModifierGroup> ModifierGroups { get; set; } = [];
}

/// <summary>Regular / Large / Family: each with its own price.</summary>
public class MenuItemVariant : TenantEntity
{
    public Guid MenuItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameUr { get; set; }
    public decimal Price { get; set; }
    public string? Sku { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>"Add-ons" (choose any) or "Spice level" (choose one): options with a price change.</summary>
public class ModifierGroup : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public string? NameUr { get; set; }
    public int MinSelect { get; set; }
    public int MaxSelect { get; set; } = 1;
    public int SortOrder { get; set; }
    public List<ModifierOption> Options { get; set; } = [];
}

public class ModifierOption : TenantEntity
{
    public Guid ModifierGroupId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameUr { get; set; }
    public decimal PriceDelta { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class MenuItemModifierGroup : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid MenuItemId { get; set; }
    public Guid ModifierGroupId { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>A branch's own availability and price for an item. No row = available at the menu price.</summary>
public class BranchMenuItem : BranchEntity
{
    public Guid MenuItemId { get; set; }
    public MenuAvailability Availability { get; set; } = MenuAvailability.Available;
    public decimal? PriceOverride { get; set; }
}
