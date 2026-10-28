using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Deals;
using Restaurant.Contracts.Enums;
using Restaurant.Domain.Entities;
using Restaurant.Domain.Pricing;

namespace Restaurant.Application.Features.Deals;

/// <summary>
/// Promotions (PLAN.md section 6). Combo deals are picked by the cashier; the others apply on their own
/// when the order qualifies. What an order got is frozen on the order, so editing a deal never changes it.
/// </summary>
public class DealService(IApplicationDbContext db, ITenantContext tenant, AuditLogger audit, TimeProvider clock)
{
    public async Task<IReadOnlyList<DealDto>> ListAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        var deals = await db.Deals.Include(d => d.Items).Include(d => d.Branches).OrderBy(d => d.Name).ToListAsync(ct);
        return deals.Select(ToDto).ToList();
    }

    public async Task<DealDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, ct));

    public async Task<DealDto> SaveAsync(Guid? id, SaveDealRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        await ValidateAsync(request, ct);

        Deal deal;
        if (id is { } existingId)
        {
            deal = await LoadAsync(existingId, ct);
            db.DealItems.RemoveRange(deal.Items);
            db.DealBranches.RemoveRange(deal.Branches);
            deal.Items.Clear();
            deal.Branches.Clear();
        }
        else
        {
            deal = new Deal { TenantId = tenantId };
            db.Deals.Add(deal);
        }

        var coupon = Blank(request.CouponCode)?.ToUpperInvariant();
        deal.Name = request.Name.Trim();
        deal.NameUr = Blank(request.NameUr);
        deal.Description = Blank(request.Description);
        deal.Type = request.Type;
        deal.Scope = request.Type is DealType.PercentOff or DealType.AmountOff ? request.Scope : DealScope.Items;
        deal.IsActive = request.IsActive;
        deal.Combinable = request.Combinable;
        deal.StartDate = request.StartDate;
        deal.EndDate = request.EndDate;
        deal.Days = request.Days == WeekDays.None ? WeekDays.All : request.Days;
        deal.StartTime = request.StartTime;
        deal.EndTime = request.EndTime;
        deal.CouponCode = request.Type == DealType.Bundle ? null : coupon;
        deal.MinimumSubtotal = request.Type == DealType.Bundle ? null : request.MinimumSubtotal;
        deal.Percent = request.Type == DealType.PercentOff ? request.Percent : null;
        deal.Amount = request.Type == DealType.AmountOff ? request.Amount : null;
        deal.BundlePrice = request.Type == DealType.Bundle ? request.BundlePrice : null;
        deal.BuyQuantity = request.Type == DealType.BuyXGetY ? request.BuyQuantity : null;
        deal.GetQuantity = request.Type == DealType.BuyXGetY ? request.GetQuantity : null;
        deal.GetPercentOff = request.Type == DealType.BuyXGetY ? request.GetPercentOff ?? 100 : null;
        deal.AllBranches = request.AllBranches;

        var items = deal.Scope == DealScope.Order ? [] : request.Items ?? [];
        foreach (var item in items)
        {
            var row = new DealItem
            {
                TenantId = tenantId,
                DealId = deal.Id,
                Group = request.Type == DealType.Bundle ? item.Group : 0,
                MenuItemId = item.MenuItemId,
                VariantId = item.VariantId,
                Quantity = request.Type == DealType.Bundle ? item.Quantity : 1
            };
            deal.Items.Add(row);
            db.DealItems.Add(row);
        }
        foreach (var branchId in request.AllBranches ? [] : request.BranchIds.Distinct())
        {
            var row = new DealBranch { TenantId = tenantId, DealId = deal.Id, BranchId = branchId };
            deal.Branches.Add(row);
            db.DealBranches.Add(row);
        }

        audit.Record(AuditActions.DealSaved, nameof(Deal), deal.Id, after: request);
        await db.SaveChangesAsync(ct);
        return ToDto(deal);
    }

    /// <summary>Deals that run at this branch right now (its local time).</summary>
    public async Task<IReadOnlyList<Deal>> RunningAsync(Guid branchId, CancellationToken ct)
    {
        var branch = await db.Branches.FirstAsync(b => b.Id == branchId, ct);
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), CallerExtensions.TimeZone(branch.TimeZoneId)).DateTime;
        var deals = await db.Deals.Include(d => d.Items).Include(d => d.Branches)
            .Where(d => d.IsActive && (d.AllBranches || d.Branches.Any(b => b.BranchId == branchId)))
            .OrderBy(d => d.Name)
            .ToListAsync(ct);
        return deals.Where(d => d.RunsAt(local)).ToList();
    }

    public async Task<IReadOnlyList<DealDto>> PosDealsAsync(Guid branchId, CancellationToken ct) =>
        (await RunningAsync(branchId, ct)).Where(d => d.Type == DealType.Bundle).Select(ToDto).ToList();

    /// <summary>The automatic deals as rules for the pricing engine (frozen on the order).</summary>
    public static DealRule ToRule(Deal d) => new(d.Id, d.Name, d.Type, d.Scope, d.Combinable, d.CouponCode, d.MinimumSubtotal, d.Percent, d.Amount,
        d.BuyQuantity, d.GetQuantity, d.GetPercentOff, d.Items.Select(i => new DealRuleItem(i.MenuItemId, i.VariantId)).ToList());

    private async Task ValidateAsync(SaveDealRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 100) throw new ValidationAppException(nameof(r.Name), "Enter a name (up to 100 characters).");
        if (r.NameUr?.Length > 100 || r.Description?.Length > 300) throw new ValidationAppException(nameof(r.Description), "The Urdu name or description is too long.");
        if (!Enum.IsDefined(r.Type)) throw new ValidationAppException(nameof(r.Type), "Choose a deal type.");
        if (r.StartDate > r.EndDate) throw new ValidationAppException(nameof(r.EndDate), "The end date is before the start date.");
        if ((r.StartTime is null) != (r.EndTime is null) || (r.StartTime is not null && r.StartTime == r.EndTime))
        {
            throw new ValidationAppException(nameof(r.StartTime), "Give both a start and an end time (different), or neither.");
        }
        if (r.CouponCode is { } code && (code.Trim().Length is < 3 or > 30 || !code.Trim().All(char.IsLetterOrDigit)))
        {
            throw new ValidationAppException(nameof(r.CouponCode), "Coupon codes are 3–30 letters and digits.");
        }
        if (r.MinimumSubtotal is < 0) throw new ValidationAppException(nameof(r.MinimumSubtotal), "The minimum can't be negative.");

        var items = r.Items ?? [];
        var needsItems = r.Type is DealType.Bundle or DealType.BuyXGetY || r.Scope == DealScope.Items;
        switch (r.Type)
        {
            case DealType.PercentOff when r.Percent is not (> 0 and <= 100):
                throw new ValidationAppException(nameof(r.Percent), "The percentage must be above 0 and at most 100.");
            case DealType.AmountOff when r.Amount is not > 0:
                throw new ValidationAppException(nameof(r.Amount), "Enter the amount off.");
            case DealType.Bundle when r.BundlePrice is not >= 0:
                throw new ValidationAppException(nameof(r.BundlePrice), "Enter the combo price.");
            case DealType.Bundle when items.Any(i => i.Quantity is < 1 or > 20) || items.Select(i => i.Group).Distinct().Count() < 2 && items.Sum(i => i.Quantity) < 2:
                throw new ValidationAppException(nameof(r.Items), "A combo needs at least two items (or two of one), each 1–20.");
            case DealType.BuyXGetY when r.BuyQuantity is not (>= 1 and <= 20) || r.GetQuantity is not (>= 1 and <= 20) || r.GetPercentOff is not (null or (> 0 and <= 100)):
                throw new ValidationAppException(nameof(r.BuyQuantity), "Buy and get are 1–20 items; the get-discount is up to 100%.");
        }
        if (needsItems && items.Count == 0) throw new ValidationAppException(nameof(r.Items), "Choose the items this deal applies to.");

        var itemIds = items.Select(i => i.MenuItemId).Distinct().ToList();
        var menu = await db.MenuItems.Include(i => i.Variants).Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        foreach (var item in items)
        {
            var menuItem = menu.GetValueOrDefault(item.MenuItemId) ?? throw new NotFoundException("Menu item");
            if (item.VariantId is { } variantId && menuItem.Variants.All(v => v.Id != variantId)) throw new NotFoundException("Size");
        }
        if (!r.AllBranches)
        {
            var branchIds = (r.BranchIds ?? []).Distinct().ToList();
            if (branchIds.Count == 0) throw new ValidationAppException(nameof(r.BranchIds), "Choose at least one branch.");
            if (await db.Branches.CountAsync(b => branchIds.Contains(b.Id), ct) != branchIds.Count) throw new NotFoundException("Branch");
        }
    }

    private async Task<Deal> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Deals.Include(d => d.Items).Include(d => d.Branches).FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Deal");

    public static DealDto ToDto(Deal d) => new(d.Id, d.Name, d.NameUr, d.Description, d.Type, d.Scope, d.IsActive, d.Combinable, d.StartDate, d.EndDate,
        d.Days, d.StartTime, d.EndTime, d.CouponCode, d.MinimumSubtotal, d.Percent, d.Amount, d.BundlePrice, d.BuyQuantity, d.GetQuantity, d.GetPercentOff,
        d.AllBranches, d.Branches.Select(b => b.BranchId).ToList(),
        d.Items.OrderBy(i => i.Group).Select(i => new DealComponentDto(i.Group, i.MenuItemId, i.VariantId, i.Quantity)).ToList());

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
