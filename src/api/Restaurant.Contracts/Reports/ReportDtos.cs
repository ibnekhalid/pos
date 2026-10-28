using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Inventory;
using Restaurant.Contracts.Sessions;

namespace Restaurant.Contracts.Reports;

/// <summary>
/// What a report covers (PLAN.md section 14). Dates are business dates (inclusive). The branch is the
/// selected one (X-Branch-Id) unless AllBranches is set, which needs access to every branch.
/// </summary>
/// <param name="CashierId">Orders created by, or payments taken by, this staff member.</param>
/// <param name="PaymentMethodId">Payments report only.</param>
public record ReportFilter(
    DateOnly From, DateOnly To, bool AllBranches = false, Guid? SessionId = null, OrderType? OrderType = null,
    Guid? CashierId = null, Guid? PaymentMethodId = null);

public record ReportScopeDto(DateOnly From, DateOnly To, IReadOnlyList<string> Branches, DateTimeOffset GeneratedAt);

/// <param name="Discounts">Deal discounts + manual discounts.</param>
/// <param name="NetSales">Gross sales − discounts (before tax and service charge).</param>
/// <param name="TotalBilled">Net sales + service charge + tax ± rounding.</param>
/// <param name="AverageOrderValue">Net sales ÷ orders (cancelled and voided orders excluded).</param>
/// <param name="Collected">Null when the caller can't see financial figures (reports.financial).</param>
public record SalesFiguresDto(
    int OrderCount, int CancelledCount, int VoidedCount, decimal GrossSales, decimal DealDiscounts, decimal ManualDiscounts,
    decimal Discounts, decimal NetSales, decimal ServiceCharge, decimal Tax, decimal Rounding, decimal TotalBilled,
    decimal VoidedValue, decimal AverageOrderValue, decimal? Collected, decimal? Refunds);

public record SalesByDayDto(DateOnly Date, int Orders, decimal GrossSales, decimal Discounts, decimal NetSales, decimal Tax, decimal ServiceCharge, decimal TotalBilled);

/// <param name="Payments">Null without reports.financial.</param>
public record SalesReportDto(ReportScopeDto Scope, SalesFiguresDto Figures, IReadOnlyList<SalesByDayDto> ByDay, IReadOnlyList<PaymentMethodTotalDto>? Payments);

public record OrderCountDto(string Key, int Count, decimal NetSales);

/// <param name="Kind">"voided_order", "cancelled_order" or "voided_line".</param>
/// <param name="Value">What was taken off the bill (the order's lines, or the line).</param>
public record VoidEntryDto(Guid OrderId, string OrderNumber, DateOnly BusinessDate, string Kind, string Description, int Quantity, decimal Value, string? Reason, DateTimeOffset At);

public record OutstandingOrderDto(Guid OrderId, string OrderNumber, DateOnly BusinessDate, OrderType Type, OrderStatus Status, decimal Total, decimal Paid, decimal Balance);

/// <param name="ByHour">Local hour of the branch (0–23), from when the order was placed.</param>
/// <param name="ByWeekday">"Monday" … "Sunday", by business date.</param>
public record OrdersReportDto(
    ReportScopeDto Scope, IReadOnlyList<OrderCountDto> ByStatus, IReadOnlyList<OrderCountDto> ByType, IReadOnlyList<OrderCountDto> ByHour,
    IReadOnlyList<OrderCountDto> ByWeekday, IReadOnlyList<VoidEntryDto> Voids, IReadOnlyList<OutstandingOrderDto> Outstanding);

/// <param name="Gross">Σ (unit price + add-ons) × quantity, before deals and discounts.</param>
public record ProductSalesDto(Guid MenuItemId, string Name, string? Variant, string Category, int Quantity, decimal Gross, decimal ShareOfGross);

public record CategorySalesDto(string Category, int Quantity, decimal Gross, decimal ShareOfGross);

/// <param name="Orders">Orders the deal applied to.</param>
public record DealPerformanceDto(string Name, DealType Type, int Orders, int Quantity, decimal Discount);

public record ProductsReportDto(ReportScopeDto Scope, IReadOnlyList<ProductSalesDto> Items, IReadOnlyList<CategorySalesDto> Categories, IReadOnlyList<DealPerformanceDto> Deals);

public record PaymentsByDayDto(DateOnly Date, decimal Collected, decimal Refunded);

public record PaymentsByCashierDto(string Name, int Count, decimal Collected, decimal Refunded);

public record RefundEntryDto(Guid OrderId, string OrderNumber, string Method, decimal Amount, string Reason, string? ByName, DateTimeOffset At);

/// <param name="Net">Collected − refunded.</param>
public record PaymentsReportDto(
    ReportScopeDto Scope, decimal Collected, decimal Refunded, decimal Net, IReadOnlyList<PaymentMethodTotalDto> ByMethod,
    IReadOnlyList<PaymentsByDayDto> ByDay, IReadOnlyList<PaymentsByCashierDto> ByCashier, IReadOnlyList<RefundEntryDto> Refunds);

/// <summary>An item's movements in the period, in its base unit. Consumed is net of reversals (voids).</summary>
/// <param name="Transferred">Net stock moved between branches: + in, − out (a cancelled transfer nets to zero).</param>
public record StockActivityDto(
    Guid ItemId, string Name, string BaseUnit, decimal Received, decimal ReceivedValue, decimal Consumed, decimal ConsumedValue,
    decimal Wasted, decimal WastedValue, decimal Adjusted, decimal CountVariance, decimal VarianceValue, decimal Transferred = 0);

/// <param name="InventoryValue">Estimated: Σ on hand × weighted-average cost (current, not as of the period end).</param>
public record InventoryReportDto(
    ReportScopeDto Scope, decimal InventoryValue, int LowStockCount, IReadOnlyList<StockLevelDto> Levels, IReadOnlyList<StockActivityDto> Activity,
    decimal ReceivedValue, decimal ConsumedValue, decimal WastedValue, decimal VarianceValue);

/// <summary>Who can be picked in the cashier filter.</summary>
public record ReportStaffDto(Guid UserId, string Name);

/// <summary>The caller's own figures for the open business day: orders they placed and payments they took.</summary>
public record MyShiftDto(DateOnly BusinessDate, int Orders, decimal NetSales, decimal TotalBilled, decimal Collected, decimal Refunded,
    IReadOnlyList<PaymentMethodTotalDto> ByMethod);
