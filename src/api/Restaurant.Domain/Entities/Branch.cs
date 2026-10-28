using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>One outlet of a tenant, with its own operating rules (PLAN.md sections 6–7).</summary>
public class Branch : TenantEntity
{
    public const string DefaultTimeZone = "Asia/Karachi";

    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Phone { get; set; }
    public string OrderNumberPrefix { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public string TimeZoneId { get; set; } = DefaultTimeZone;
    public int DayStartHour { get; set; } = 6;
    public decimal ServiceChargePercent { get; set; }
    public OrderTypes ServiceChargeAppliesTo { get; set; } = OrderTypes.DineIn;
    public RoundingMode Rounding { get; set; } = RoundingMode.None;
    public SessionScope SessionScope { get; set; } = SessionScope.PerBranch;
    public decimal CashDifferenceThreshold { get; set; } = 500m;
    public InsufficientStockBehavior InsufficientStock { get; set; } = InsufficientStockBehavior.Allow;
    public int LateTicketMinutes { get; set; } = 15;

    /// <summary>
    /// The business date a moment belongs to: the branch's local date, moved back a day before
    /// DayStartHour so a session running past midnight keeps its date (PLAN.md section 7).
    /// </summary>
    public DateOnly BusinessDateFor(DateTimeOffset moment, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(moment, timeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        return local.Hour < DayStartHour ? date.AddDays(-1) : date;
    }
}
