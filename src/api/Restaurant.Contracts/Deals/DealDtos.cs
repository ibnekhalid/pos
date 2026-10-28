using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;

namespace Restaurant.Contracts.Deals;

/// <param name="Group">Bundle: the component number ("choose one drink" is one group). Other deals: always 0.</param>
/// <param name="VariantId">Only this size; null = any size.</param>
/// <param name="Quantity">Bundle: how many of the group's choice come in the deal.</param>
public record DealComponentDto(int Group, Guid MenuItemId, Guid? VariantId, int Quantity);

/// <param name="Days">Days of the week it runs (branch time).</param>
/// <param name="StartTime">With EndTime: a daily window, e.g. 15:00–18:00 (may run past midnight).</param>
/// <param name="CouponCode">When set, the deal only applies to orders with this code.</param>
/// <param name="Combinable">May stack with other deals; otherwise the better of "this alone" and "the rest" wins.</param>
/// <param name="GetPercentOff">BuyXGetY: 100 = free.</param>
public record DealDto(
    Guid Id, string Name, string? NameUr, string? Description, DealType Type, DealScope Scope, bool IsActive, bool Combinable,
    DateOnly? StartDate, DateOnly? EndDate, WeekDays Days, TimeOnly? StartTime, TimeOnly? EndTime, string? CouponCode, decimal? MinimumSubtotal,
    decimal? Percent, decimal? Amount, decimal? BundlePrice, int? BuyQuantity, int? GetQuantity, decimal? GetPercentOff,
    bool AllBranches, IReadOnlyList<Guid> BranchIds, IReadOnlyList<DealComponentDto> Items);

public record SaveDealRequest(
    string Name, string? NameUr, string? Description, DealType Type, DealScope Scope, bool IsActive, bool Combinable,
    DateOnly? StartDate, DateOnly? EndDate, WeekDays Days, TimeOnly? StartTime, TimeOnly? EndTime, string? CouponCode, decimal? MinimumSubtotal,
    decimal? Percent, decimal? Amount, decimal? BundlePrice, int? BuyQuantity, int? GetQuantity, decimal? GetPercentOff,
    bool AllBranches, IReadOnlyList<Guid> BranchIds, IReadOnlyList<DealComponentDto> Items);

/// <summary>A bundle chosen on the POS: one choice per group, in group order.</summary>
public record DealSelection(Guid DealId, int Quantity, IReadOnlyList<OrderLineRequest> Components);

/// <summary>A deal applied to an order, as it was when applied (original price, discount).</summary>
public record AppliedDealDto(Guid? DealId, Guid? ApplicationId, string Name, DealType Type, int Quantity, decimal OriginalTotal, decimal Discount);
