using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Extensions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Application.Features.Deals;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Menu;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Menu;

public class SaveMenuItemRequestValidator : AbstractValidator<SaveMenuItemRequest>
{
    public SaveMenuItemRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.NameUr).MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(300);
        RuleFor(x => x.Sku).MaximumLength(40);
        RuleFor(x => x.Price).InclusiveBetween(0m, 10_000_000m);
        RuleForEach(x => x.Variants).ChildRules(v =>
        {
            v.RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
            v.RuleFor(x => x.Price).InclusiveBetween(0m, 10_000_000m);
        });
    }
}

public class SaveModifierGroupRequestValidator : AbstractValidator<SaveModifierGroupRequest>
{
    public SaveModifierGroupRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.MinSelect).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxSelect).GreaterThanOrEqualTo(1).GreaterThanOrEqualTo(x => x.MinSelect)
            .WithMessage("Maximum choices must be at least 1 and not less than the minimum.");
        RuleFor(x => x.Options).NotEmpty().WithMessage("Add at least one option.");
        RuleForEach(x => x.Options).ChildRules(o =>
        {
            o.RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
            o.RuleFor(x => x.PriceDelta).InclusiveBetween(-1_000_000m, 1_000_000m);
        });
    }
}

/// <summary>
/// Menu management (categories, items with sizes, add-on groups, kitchen stations) and the branch view
/// the POS uses. Price changes are audited; existing orders keep their snapshot prices.
/// </summary>
public class MenuService(
    IApplicationDbContext db,
    ITenantContext tenant,
    AuditLogger audit,
    DealService deals,
    IValidator<SaveMenuItemRequest> itemValidator,
    IValidator<SaveModifierGroupRequest> groupValidator)
{
    // ---------------------------------------------------------------- stations

    public async Task<IReadOnlyList<KitchenStationDto>> ListStationsAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        return await db.KitchenStations.OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new KitchenStationDto(s.Id, s.Name, s.SortOrder, s.IsActive)).ToListAsync(ct);
    }

    public async Task<KitchenStationDto> SaveStationAsync(Guid? id, SaveKitchenStationRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        RequireName(request.Name, 60);
        var station = id is null ? new KitchenStation { TenantId = tenantId }
            : await db.KitchenStations.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Kitchen station");
        if (await db.KitchenStations.AnyAsync(s => s.Name == request.Name.Trim() && s.Id != station.Id, ct))
        {
            throw new ConflictException("A station with this name already exists.");
        }
        station.Name = request.Name.Trim();
        station.SortOrder = request.SortOrder;
        station.IsActive = request.IsActive;
        if (id is null) db.KitchenStations.Add(station);
        await db.SaveChangesAsync(ct);
        return new KitchenStationDto(station.Id, station.Name, station.SortOrder, station.IsActive);
    }

    // ---------------------------------------------------------------- categories

    public async Task<IReadOnlyList<MenuCategoryDto>> ListCategoriesAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        return await db.MenuCategories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => ToDto(c)).ToListAsync(ct);
    }

    public async Task<MenuCategoryDto> SaveCategoryAsync(Guid? id, SaveMenuCategoryRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        RequireName(request.Name, 60);
        var category = id is null ? new MenuCategory { TenantId = tenantId }
            : await db.MenuCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Category");
        if (await db.MenuCategories.AnyAsync(c => c.Name == request.Name.Trim() && c.Id != category.Id, ct))
        {
            throw new ConflictException("A category with this name already exists.");
        }
        category.Name = request.Name.Trim();
        category.NameUr = Blank(request.NameUr);
        category.SortOrder = request.SortOrder;
        category.Color = Blank(request.Color);
        category.IsActive = request.IsActive;
        if (id is null) db.MenuCategories.Add(category);
        await db.SaveChangesAsync(ct);
        return ToDto(category);
    }

    // ---------------------------------------------------------------- modifier groups

    public async Task<IReadOnlyList<ModifierGroupDto>> ListModifierGroupsAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        var groups = await db.ModifierGroups.Include(g => g.Options).OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(ct);
        return groups.Select(g => ToDto(g, activeOnly: false)).ToList();
    }

    public async Task<ModifierGroupDto> SaveModifierGroupAsync(Guid? id, SaveModifierGroupRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        await groupValidator.ValidateAndThrowAppExceptionAsync(request, ct);
        var group = id is null ? new ModifierGroup { TenantId = tenantId }
            : await db.ModifierGroups.Include(g => g.Options).FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException("Add-on group");

        group.Name = request.Name.Trim();
        group.NameUr = Blank(request.NameUr);
        group.MinSelect = request.MinSelect;
        group.MaxSelect = request.MaxSelect;
        group.SortOrder = request.SortOrder;

        // Options are never deleted (orders keep their snapshot anyway): missing ones are deactivated.
        foreach (var existing in group.Options.Where(o => request.Options.All(r => r.Id != o.Id)))
        {
            existing.IsActive = false;
        }
        foreach (var option in request.Options)
        {
            var entity = option.Id is { } optionId ? group.Options.FirstOrDefault(o => o.Id == optionId) : null;
            if (entity is null)
            {
                entity = new ModifierOption { TenantId = tenantId, ModifierGroupId = group.Id };
                group.Options.Add(entity);
                db.ModifierOptions.Add(entity);
            }
            entity.Name = option.Name.Trim();
            entity.NameUr = Blank(option.NameUr);
            entity.PriceDelta = option.PriceDelta;
            entity.SortOrder = option.SortOrder;
            entity.IsActive = option.IsActive;
        }

        if (id is null) db.ModifierGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return ToDto(group, activeOnly: false);
    }

    // ---------------------------------------------------------------- items

    public async Task<IReadOnlyList<MenuItemDto>> ListItemsAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        var items = await db.MenuItems.Include(i => i.Variants).Include(i => i.ModifierGroups)
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync(ct);
        return items.Select(ToDto).ToList();
    }

    public async Task<MenuItemDto> GetItemAsync(Guid id, CancellationToken ct)
    {
        tenant.RequireTenantId();
        return ToDto(await LoadItemAsync(id, ct));
    }

    public async Task<MenuItemDto> SaveItemAsync(Guid? id, SaveMenuItemRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        await itemValidator.ValidateAndThrowAppExceptionAsync(request, ct);

        if (!await db.MenuCategories.AnyAsync(c => c.Id == request.CategoryId, ct)) throw new NotFoundException("Category");
        if (request.KitchenStationId is { } stationId && !await db.KitchenStations.AnyAsync(s => s.Id == stationId, ct)) throw new NotFoundException("Kitchen station");
        var groupIds = request.ModifierGroupIds.Distinct().ToList();
        if (await db.ModifierGroups.CountAsync(g => groupIds.Contains(g.Id), ct) != groupIds.Count) throw new NotFoundException("Add-on group");

        var item = id is null ? new MenuItem { TenantId = tenantId } : await LoadItemAsync(id.Value, ct);
        var before = id is null ? null : ToDto(item);

        item.CategoryId = request.CategoryId;
        item.Name = request.Name.Trim();
        item.NameUr = Blank(request.NameUr);
        item.Description = Blank(request.Description);
        item.Sku = Blank(request.Sku);
        item.Price = request.Price;
        item.KitchenStationId = request.KitchenStationId;
        item.SortOrder = request.SortOrder;
        item.IsActive = request.IsActive;

        foreach (var existing in item.Variants.Where(v => request.Variants.All(r => r.Id != v.Id)))
        {
            existing.IsActive = false;
        }
        foreach (var variant in request.Variants)
        {
            var entity = variant.Id is { } variantId ? item.Variants.FirstOrDefault(v => v.Id == variantId) : null;
            if (entity is null)
            {
                entity = new MenuItemVariant { TenantId = tenantId, MenuItemId = item.Id };
                item.Variants.Add(entity);
                db.MenuItemVariants.Add(entity);
            }
            entity.Name = variant.Name.Trim();
            entity.NameUr = Blank(variant.NameUr);
            entity.Price = variant.Price;
            entity.Sku = Blank(variant.Sku);
            entity.SortOrder = variant.SortOrder;
            entity.IsActive = variant.IsActive;
        }

        foreach (var link in item.ModifierGroups.Where(l => !groupIds.Contains(l.ModifierGroupId)).ToList())
        {
            item.ModifierGroups.Remove(link);
            db.MenuItemModifierGroups.Remove(link);
        }
        for (var i = 0; i < groupIds.Count; i++)
        {
            var link = item.ModifierGroups.FirstOrDefault(l => l.ModifierGroupId == groupIds[i]);
            if (link is null)
            {
                item.ModifierGroups.Add(new MenuItemModifierGroup { TenantId = tenantId, MenuItemId = item.Id, ModifierGroupId = groupIds[i], SortOrder = i });
            }
            else
            {
                link.SortOrder = i;
            }
        }

        if (id is null) db.MenuItems.Add(item);
        var after = ToDto(item);
        audit.Record(AuditActions.MenuItemSaved, nameof(MenuItem), item.Id, before, after);
        await db.SaveChangesAsync(ct);
        return after;
    }

    /// <summary>This branch's availability (sold out, unavailable) and optional own price.</summary>
    public async Task SetAvailabilityAsync(Guid itemId, SetAvailabilityRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        if (request.PriceOverride is < 0) throw new ValidationAppException(nameof(request.PriceOverride), "The price can't be negative.");
        if (!await db.MenuItems.AnyAsync(i => i.Id == itemId, ct)) throw new NotFoundException("Menu item");

        var row = await db.BranchMenuItems.FirstOrDefaultAsync(b => b.MenuItemId == itemId && b.BranchId == branchId, ct);
        if (row is null)
        {
            row = new BranchMenuItem { TenantId = tenantId, BranchId = branchId, MenuItemId = itemId };
            db.BranchMenuItems.Add(row);
        }
        var before = new { row.Availability, row.PriceOverride };
        row.Availability = request.Availability;
        row.PriceOverride = request.PriceOverride;
        if (before.PriceOverride != request.PriceOverride)
        {
            audit.Record(AuditActions.MenuPriceOverride, nameof(BranchMenuItem), itemId, before, new { row.Availability, row.PriceOverride }, branchId: branchId);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The selected branch's menu for the POS: active items, sizes, add-ons, availability and prices.</summary>
    public async Task<PosMenuDto> GetPosMenuAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();

        var categories = await db.MenuCategories.Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var items = await db.MenuItems.Include(i => i.Variants).Include(i => i.ModifierGroups)
            .Where(i => i.IsActive).OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync(ct);
        var branchRows = await db.BranchMenuItems.Where(b => b.BranchId == branchId).ToDictionaryAsync(b => b.MenuItemId, ct);
        var groups = await db.ModifierGroups.Include(g => g.Options).ToListAsync(ct);
        var activeCategoryIds = categories.Select(c => c.Id).ToHashSet();

        var posItems = items
            .Where(i => activeCategoryIds.Contains(i.CategoryId))
            .Select(i =>
            {
                branchRows.TryGetValue(i.Id, out var row);
                return new PosMenuItemDto(i.Id, i.CategoryId, i.Name, i.NameUr, i.Description, row?.PriceOverride ?? i.Price,
                    row?.Availability ?? MenuAvailability.Available, row?.PriceOverride,
                    i.Variants.Where(v => v.IsActive).OrderBy(v => v.SortOrder).Select(ToDto).ToList(),
                    i.ModifierGroups.OrderBy(g => g.SortOrder).Select(g => g.ModifierGroupId).ToList());
            })
            .ToList();

        var usedGroups = posItems.SelectMany(i => i.ModifierGroupIds).ToHashSet();
        return new PosMenuDto(
            categories.Select(ToDto).ToList(),
            posItems,
            groups.Where(g => usedGroups.Contains(g.Id)).OrderBy(g => g.SortOrder).Select(g => ToDto(g, activeOnly: true)).ToList(),
            await deals.PosDealsAsync(branchId, ct));
    }

    private async Task<MenuItem> LoadItemAsync(Guid id, CancellationToken ct) =>
        await db.MenuItems.Include(i => i.Variants).Include(i => i.ModifierGroups).FirstOrDefaultAsync(i => i.Id == id, ct)
        ?? throw new NotFoundException("Menu item");

    private static void RequireName(string? name, int max)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > max)
        {
            throw new ValidationAppException("Name", $"Enter a name (up to {max} characters).");
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static MenuCategoryDto ToDto(MenuCategory c) => new(c.Id, c.Name, c.NameUr, c.SortOrder, c.Color, c.IsActive);

    private static MenuItemVariantDto ToDto(MenuItemVariant v) => new(v.Id, v.Name, v.NameUr, v.Price, v.Sku, v.SortOrder, v.IsActive);

    private static MenuItemDto ToDto(MenuItem i) => new(i.Id, i.CategoryId, i.Name, i.NameUr, i.Description, i.Sku, i.Price,
        i.KitchenStationId, i.SortOrder, i.IsActive,
        i.Variants.OrderBy(v => v.SortOrder).Select(ToDto).ToList(),
        i.ModifierGroups.OrderBy(g => g.SortOrder).Select(g => g.ModifierGroupId).ToList());

    private static ModifierGroupDto ToDto(ModifierGroup g, bool activeOnly) => new(g.Id, g.Name, g.NameUr, g.MinSelect, g.MaxSelect, g.SortOrder,
        g.Options.Where(o => !activeOnly || o.IsActive).OrderBy(o => o.SortOrder)
            .Select(o => new ModifierOptionDto(o.Id, o.Name, o.NameUr, o.PriceDelta, o.SortOrder, o.IsActive)).ToList());
}
