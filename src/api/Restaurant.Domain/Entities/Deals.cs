using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>A promotion (PLAN.md section 6). Orders keep a snapshot of what applied, so editing a deal never changes them.</summary>
public class Deal : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public string? NameUr { get; set; }
    public string? Description { get; set; }
    public DealType Type { get; set; }
    public DealScope Scope { get; set; }
    public bool IsActive { get; set; } = true;
    public bool Combinable { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public WeekDays Days { get; set; } = WeekDays.All;
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public string? CouponCode { get; set; }
    public decimal? MinimumSubtotal { get; set; }
    public decimal? Percent { get; set; }
    public decimal? Amount { get; set; }
    public decimal? BundlePrice { get; set; }
    public int? BuyQuantity { get; set; }
    public int? GetQuantity { get; set; }
    public decimal? GetPercentOff { get; set; }
    public bool AllBranches { get; set; } = true;
    public List<DealItem> Items { get; set; } = [];
    public List<DealBranch> Branches { get; set; } = [];

    /// <summary>Runs at this local moment (dates, weekday, daily window — a window may cross midnight).</summary>
    public bool RunsAt(DateTime local)
    {
        var date = DateOnly.FromDateTime(local);
        if (!IsActive || date < StartDate || date > EndDate) return false;
        var day = local.DayOfWeek switch
        {
            DayOfWeek.Monday => WeekDays.Monday,
            DayOfWeek.Tuesday => WeekDays.Tuesday,
            DayOfWeek.Wednesday => WeekDays.Wednesday,
            DayOfWeek.Thursday => WeekDays.Thursday,
            DayOfWeek.Friday => WeekDays.Friday,
            DayOfWeek.Saturday => WeekDays.Saturday,
            _ => WeekDays.Sunday
        };
        if (!Days.HasFlag(day)) return false;
        if (StartTime is not { } start || EndTime is not { } end) return true;
        var time = TimeOnly.FromDateTime(local);
        return start <= end ? time >= start && time < end : time >= start || time < end;
    }
}

public class DealItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid DealId { get; set; }
    public int Group { get; set; }
    public Guid MenuItemId { get; set; }
    public Guid? VariantId { get; set; }
    public int Quantity { get; set; } = 1;
}

public class DealBranch : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid DealId { get; set; }
    public Guid BranchId { get; set; }
}

/// <summary>A combo deal on an order: its lines carry this id; the discount is original − deal price.</summary>
public class OrderDealApplication : TenantEntity
{
    public Guid OrderId { get; set; }
    public Guid? DealId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal OriginalTotal { get; set; }
    public decimal Discount { get; set; }
    public bool IsVoided { get; set; }
}

/// <summary>
/// An optional customer profile. Walk-ins need none. Only name, phone, address and notes are kept;
/// "Remove personal data" anonymises the profile and the orders' copies (PLAN.md section 12).
/// </summary>
public class Customer : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? AnonymizedAt { get; set; }
}
