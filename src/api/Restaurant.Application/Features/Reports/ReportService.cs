using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Application.Features.Inventory;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Deals;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Reports;
using Restaurant.Contracts.Sessions;

namespace Restaurant.Application.Features.Reports;

/// <summary>
/// Reports (PLAN.md section 14). Every figure is a query over the same order, payment, cash and stock
/// rows the business day uses, with the same formulas as the close-day summary, so a one-day report
/// always matches that day's closing report (T14). Nothing is pre-aggregated.
/// </summary>
public class ReportService(IApplicationDbContext db, ITenantContext tenant, StockService stock, TimeProvider clock)
{
    private const int MaxDays = 366;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- sales

    public async Task<SalesReportDto> SalesAsync(ReportFilter filter, CancellationToken ct)
    {
        var scope = await ResolveAsync(filter, ct);
        var orders = await OrderRowsAsync(scope, ct);
        var counted = orders.Where(o => o.Counted).ToList();
        var voidedValue = await VoidedValueAsync(orders, ct);

        IReadOnlyList<PaymentMethodTotalDto>? methods = null;
        decimal? collected = null, refunded = null;
        if (tenant.HasPermission(Permissions.Reports.Financial))
        {
            var payments = await PaymentRowsAsync(scope, ct);
            var refunds = await RefundRowsAsync(scope, ct);
            methods = ByMethod(payments, refunds);
            collected = payments.Sum(p => p.Amount);
            refunded = refunds.Sum(r => r.Amount);
        }

        var gross = counted.Sum(o => o.Subtotal);
        var discounts = counted.Sum(o => o.DiscountTotal);
        var dealDiscounts = counted.Sum(o => o.DealDiscountTotal);
        var figures = new SalesFiguresDto(
            counted.Count,
            orders.Count(o => o.Status == OrderStatus.Cancelled),
            orders.Count(o => o.Status == OrderStatus.Voided),
            gross,
            dealDiscounts,
            discounts - dealDiscounts,
            discounts,
            gross - discounts,
            counted.Sum(o => o.ServiceCharge),
            counted.Sum(o => o.TaxTotal),
            counted.Sum(o => o.Rounding),
            counted.Sum(o => o.Total),
            voidedValue,
            counted.Count == 0 ? 0 : Math.Round((gross - discounts) / counted.Count, 2, MidpointRounding.AwayFromZero),
            collected,
            refunded);

        var byDay = counted.GroupBy(o => o.BusinessDate).OrderBy(g => g.Key)
            .Select(g => new SalesByDayDto(g.Key, g.Count(), g.Sum(o => o.Subtotal), g.Sum(o => o.DiscountTotal), g.Sum(o => o.Subtotal - o.DiscountTotal),
                g.Sum(o => o.TaxTotal), g.Sum(o => o.ServiceCharge), g.Sum(o => o.Total)))
            .ToList();

        return new SalesReportDto(scope.Dto, figures, byDay, methods);
    }

    // ---------------------------------------------------------------- orders

    public async Task<OrdersReportDto> OrdersAsync(ReportFilter filter, CancellationToken ct)
    {
        var scope = await ResolveAsync(filter, ct);
        var orders = await OrderRowsAsync(scope, ct);
        var counted = orders.Where(o => o.Counted).ToList();

        var byStatus = orders.GroupBy(o => o.Status).OrderBy(g => g.Key)
            .Select(g => new OrderCountDto(g.Key.ToString(), g.Count(), g.Where(o => o.Counted).Sum(o => o.NetSales))).ToList();
        var byType = counted.GroupBy(o => o.Type).OrderBy(g => g.Key)
            .Select(g => new OrderCountDto(g.Key.ToString(), g.Count(), g.Sum(o => o.NetSales))).ToList();
        var byHour = counted.GroupBy(o => TimeZoneInfo.ConvertTime(o.CreatedAt, scope.TimeZones[o.BranchId]).Hour).OrderBy(g => g.Key)
            .Select(g => new OrderCountDto(g.Key.ToString("00"), g.Count(), g.Sum(o => o.NetSales))).ToList();
        var byWeekday = counted.GroupBy(o => o.BusinessDate.DayOfWeek).OrderBy(g => ((int)g.Key + 6) % 7)
            .Select(g => new OrderCountDto(g.Key.ToString(), g.Count(), g.Sum(o => o.NetSales))).ToList();

        // Voids and cancellations, with their reasons.
        var dates = orders.ToDictionary(o => o.Id, o => o.BusinessDate);
        var numbers = orders.ToDictionary(o => o.Id, o => o.OrderNumber);
        var closedIds = orders.Where(o => !o.Counted).Select(o => o.Id).ToList();
        var countedIds = counted.Select(o => o.Id).ToList();
        var closedValues = await db.OrderLines.Where(l => closedIds.Contains(l.OrderId))
            .GroupBy(l => l.OrderId).Select(g => new { g.Key, Quantity = g.Sum(l => l.Quantity), Value = g.Sum(l => l.LineTotal) })
            .ToDictionaryAsync(x => x.Key, ct);
        var voids = orders.Where(o => !o.Counted).Select(o => new VoidEntryDto(o.Id, o.OrderNumber, o.BusinessDate,
                o.Status == OrderStatus.Voided ? "voided_order" : "cancelled_order", o.OrderNumber,
                closedValues.GetValueOrDefault(o.Id)?.Quantity ?? 0, closedValues.GetValueOrDefault(o.Id)?.Value ?? 0, o.VoidReason, o.UpdatedAt ?? o.CreatedAt))
            .ToList();
        var voidedLines = await db.OrderLines
            .Where(l => countedIds.Contains(l.OrderId) && l.Status == OrderLineStatus.Voided && l.SentAt != null)
            .Select(l => new { l.OrderId, l.Name, l.VariantName, l.Quantity, l.LineTotal, l.VoidReason, At = l.UpdatedAt ?? l.CreatedAt })
            .ToListAsync(ct);
        voids.AddRange(voidedLines.Select(l => new VoidEntryDto(l.OrderId, numbers[l.OrderId], dates[l.OrderId], "voided_line",
            l.VariantName is null ? l.Name : $"{l.Name} ({l.VariantName})", l.Quantity, l.LineTotal, l.VoidReason, l.At)));

        var outstanding = counted.Where(o => o.Paid < o.Total).OrderBy(o => o.BusinessDate).ThenBy(o => o.OrderNumber)
            .Select(o => new OutstandingOrderDto(o.Id, o.OrderNumber, o.BusinessDate, o.Type, o.Status, o.Total, o.Paid, o.Total - o.Paid)).ToList();

        return new OrdersReportDto(scope.Dto, byStatus, byType, byHour, byWeekday, voids.OrderByDescending(v => v.At).ToList(), outstanding);
    }

    // ---------------------------------------------------------------- products and deals

    public async Task<ProductsReportDto> ProductsAsync(ReportFilter filter, CancellationToken ct)
    {
        var scope = await ResolveAsync(filter, ct);
        var orders = await OrderRowsAsync(scope, ct);
        var countedIds = orders.Where(o => o.Counted).Select(o => o.Id).ToList();

        var lines = await db.OrderLines
            .Where(l => countedIds.Contains(l.OrderId) && l.Status == OrderLineStatus.Active)
            .GroupBy(l => new { l.MenuItemId, l.Name, l.VariantName })
            .Select(g => new { g.Key.MenuItemId, g.Key.Name, g.Key.VariantName, Quantity = g.Sum(l => l.Quantity), Gross = g.Sum(l => l.LineTotal) })
            .ToListAsync(ct);
        var itemIds = lines.Select(l => l.MenuItemId).Distinct().ToList();
        var categories = await db.MenuItems.Where(i => itemIds.Contains(i.Id))
            .Join(db.MenuCategories, i => i.CategoryId, c => c.Id, (i, c) => new { i.Id, c.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var total = lines.Sum(l => l.Gross);
        decimal Share(decimal value) => total == 0 ? 0 : Math.Round(value / total * 100, 1, MidpointRounding.AwayFromZero);
        string CategoryOf(Guid itemId) => categories.GetValueOrDefault(itemId) ?? "—";

        var items = lines.OrderByDescending(l => l.Quantity).ThenByDescending(l => l.Gross)
            .Select(l => new ProductSalesDto(l.MenuItemId, l.Name, l.VariantName, CategoryOf(l.MenuItemId), l.Quantity, l.Gross, Share(l.Gross))).ToList();
        var byCategory = lines.GroupBy(l => CategoryOf(l.MenuItemId)).OrderByDescending(g => g.Sum(l => l.Gross))
            .Select(g => new CategorySalesDto(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.Gross), Share(g.Sum(l => l.Gross)))).ToList();

        // Combos are rows; automatic deals are the snapshot on each order.
        var combos = await db.OrderDealApplications.Where(a => countedIds.Contains(a.OrderId) && !a.IsVoided)
            .Select(a => new { a.OrderId, a.Name, a.Quantity, a.Discount }).ToListAsync(ct);
        var autoJson = await db.Orders.Where(o => countedIds.Contains(o.Id) && o.AutoDealsJson != "[]")
            .Select(o => new { o.Id, o.AutoDealsJson }).ToListAsync(ct);
        var applied = combos.Select(c => (c.OrderId, c.Name, Type: DealType.Bundle, c.Quantity, c.Discount))
            .Concat(autoJson.SelectMany(o => (JsonSerializer.Deserialize<List<AppliedDealDto>>(o.AutoDealsJson, Json) ?? [])
                .Where(d => d.ApplicationId is null)
                .Select(d => (OrderId: o.Id, d.Name, d.Type, d.Quantity, d.Discount))));
        var deals = applied.GroupBy(d => (d.Name, d.Type)).OrderByDescending(g => g.Sum(d => d.Discount))
            .Select(g => new DealPerformanceDto(g.Key.Name, g.Key.Type, g.Select(d => d.OrderId).Distinct().Count(), g.Sum(d => d.Quantity), g.Sum(d => d.Discount)))
            .ToList();

        return new ProductsReportDto(scope.Dto, items, byCategory, deals);
    }

    // ---------------------------------------------------------------- payments

    public async Task<PaymentsReportDto> PaymentsAsync(ReportFilter filter, CancellationToken ct)
    {
        var scope = await ResolveAsync(filter, ct);
        var payments = await PaymentRowsAsync(scope, ct);
        var refunds = await RefundRowsAsync(scope, ct);

        var byDay = payments.Select(p => (p.BusinessDate, Collected: p.Amount, Refunded: 0m))
            .Concat(refunds.Select(r => (r.BusinessDate, Collected: 0m, Refunded: r.Amount)))
            .GroupBy(x => x.BusinessDate).OrderBy(g => g.Key)
            .Select(g => new PaymentsByDayDto(g.Key, g.Sum(x => x.Collected), g.Sum(x => x.Refunded))).ToList();

        var names = await NamesAsync(payments.Select(p => p.TakenBy).Concat(refunds.Select(r => r.By)), ct);
        var byCashier = payments.Select(p => (By: p.TakenBy, Count: 1, Collected: p.Amount, Refunded: 0m))
            .Concat(refunds.Select(r => (r.By, Count: 0, Collected: 0m, Refunded: r.Amount)))
            .GroupBy(x => x.By).Select(g => new PaymentsByCashierDto(NameOf(names, g.Key), g.Sum(x => x.Count), g.Sum(x => x.Collected), g.Sum(x => x.Refunded)))
            .OrderByDescending(c => c.Collected).ToList();

        var refundList = refunds.OrderByDescending(r => r.At)
            .Select(r => new RefundEntryDto(r.OrderId, r.OrderNumber, r.Method, r.Amount, r.Reason, r.ByName ?? NameOf(names, r.By), r.At)).ToList();

        var collected = payments.Sum(p => p.Amount);
        var refunded = refunds.Sum(r => r.Amount);
        return new PaymentsReportDto(scope.Dto, collected, refunded, collected - refunded, ByMethod(payments, refunds), byDay, byCashier, refundList);
    }

    // ---------------------------------------------------------------- inventory

    /// <summary>Stock is per branch: always the selected branch. Levels are current; activity is the period's ledger.</summary>
    public async Task<InventoryReportDto> InventoryAsync(ReportFilter filter, CancellationToken ct)
    {
        var scope = await ResolveAsync(filter with { AllBranches = false }, ct);
        var branchId = scope.BranchIds.Single();
        var (start, end) = scope.UtcBounds(branchId);

        var levels = await stock.LevelsAsync(ct);
        var movements = await db.StockMovements
            .Where(m => m.BranchId == branchId && m.CreatedAt >= start && m.CreatedAt < end)
            .GroupBy(m => new { m.InventoryItemId, m.Type })
            .Select(g => new { g.Key.InventoryItemId, g.Key.Type, Quantity = g.Sum(m => m.Quantity), Value = g.Sum(m => m.Quantity * m.UnitCost) })
            .ToListAsync(ct);
        var itemIds = movements.Select(m => m.InventoryItemId).Distinct().ToList();
        var items = await db.InventoryItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        var activity = movements.GroupBy(m => m.InventoryItemId).Where(g => items.ContainsKey(g.Key)).Select(g =>
        {
            decimal Qty(params StockMovementType[] types) => g.Where(m => types.Contains(m.Type)).Sum(m => m.Quantity);
            decimal Value(params StockMovementType[] types) => R(g.Where(m => types.Contains(m.Type)).Sum(m => m.Value));
            var item = items[g.Key];
            return new StockActivityDto(item.Id, item.Name, item.BaseUnit,
                Qty(StockMovementType.Receipt, StockMovementType.Opening), Value(StockMovementType.Receipt, StockMovementType.Opening),
                -Qty(StockMovementType.SaleConsumption, StockMovementType.SaleReversal), -Value(StockMovementType.SaleConsumption, StockMovementType.SaleReversal),
                -Qty(StockMovementType.Wastage), -Value(StockMovementType.Wastage),
                Qty(StockMovementType.Adjustment), Qty(StockMovementType.CountVariance),
                Value(StockMovementType.Adjustment, StockMovementType.CountVariance));
        }).OrderBy(a => a.Name).ToList();

        return new InventoryReportDto(scope.Dto, levels.Sum(l => l.Value), levels.Count(l => l.IsLow), levels, activity,
            activity.Sum(a => a.ReceivedValue), activity.Sum(a => a.ConsumedValue), activity.Sum(a => a.WastedValue), activity.Sum(a => a.VarianceValue));
    }

    /// <summary>Staff of the restaurant, for the cashier filter.</summary>
    public async Task<IReadOnlyList<ReportStaffDto>> StaffAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        return await db.Memberships.OrderBy(m => m.DisplayName).Select(m => new ReportStaffDto(m.UserId, m.DisplayName)).ToListAsync(ct);
    }

    // ---------------------------------------------------------------- scope

    private sealed record Scope(
        ReportScopeDto Dto, IReadOnlyList<Guid> BranchIds, Dictionary<Guid, TimeZoneInfo> TimeZones, Dictionary<Guid, int> DayStartHours, ReportFilter Filter)
    {
        /// <summary>The period as UTC instants for one branch: from the first day's start hour to the day after the last.</summary>
        public (DateTimeOffset Start, DateTimeOffset End) UtcBounds(Guid branchId)
        {
            var zone = TimeZones[branchId];
            var hour = DayStartHours[branchId];
            DateTimeOffset At(DateOnly date)
            {
                var local = date.ToDateTime(new TimeOnly(hour, 0));
                return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
            }
            return (At(Dto.From), At(Dto.To.AddDays(1)));
        }
    }

    private async Task<Scope> ResolveAsync(ReportFilter filter, CancellationToken ct)
    {
        tenant.RequireTenantId();
        List<Guid> branchIds;
        if (filter.SessionId is { } sessionId)
        {
            var session = await db.BusinessSessions.Where(s => s.Id == sessionId).Select(s => new { s.BranchId, s.BusinessDate }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Business day");
            tenant.EnsureBranch(session.BranchId);
            branchIds = [session.BranchId];
            filter = filter with { From = session.BusinessDate, To = session.BusinessDate, AllBranches = false };
        }
        else if (filter.AllBranches)
        {
            if (!tenant.AllBranches) throw new ForbiddenAccessException("Reports across branches need access to every branch.");
            branchIds = await db.Branches.Select(b => b.Id).ToListAsync(ct);
        }
        else
        {
            branchIds = [tenant.RequireBranchId()];
        }

        if (filter.To < filter.From) throw new ValidationAppException(nameof(filter.To), "The end date is before the start date.");
        if (filter.To.DayNumber - filter.From.DayNumber >= MaxDays) throw new ValidationAppException(nameof(filter.To), $"Pick at most {MaxDays} days.");

        var branches = await db.Branches.Where(b => branchIds.Contains(b.Id)).OrderBy(b => b.Name)
            .Select(b => new { b.Id, b.Name, b.TimeZoneId, b.DayStartHour }).ToListAsync(ct);
        return new Scope(
            new ReportScopeDto(filter.From, filter.To, branches.Select(b => b.Name).ToList(), clock.GetUtcNow()),
            branchIds,
            branches.ToDictionary(b => b.Id, b => CallerExtensions.TimeZone(b.TimeZoneId)),
            branches.ToDictionary(b => b.Id, b => b.DayStartHour),
            filter);
    }

    // ---------------------------------------------------------------- rows

    private sealed record OrderRow(
        Guid Id, Guid BranchId, string OrderNumber, DateOnly BusinessDate, OrderType Type, OrderStatus Status, decimal Subtotal, decimal DiscountTotal,
        decimal DealDiscountTotal, decimal ServiceCharge, decimal TaxTotal, decimal Rounding, decimal Total, decimal Paid, string? VoidReason,
        DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt)
    {
        public bool Counted => Status is not (OrderStatus.Cancelled or OrderStatus.Voided);
        public decimal NetSales => Subtotal - DiscountTotal;
    }

    private async Task<List<OrderRow>> OrderRowsAsync(Scope scope, CancellationToken ct)
    {
        var f = scope.Filter;
        var query =
            from o in db.Orders
            join s in db.BusinessSessions on o.BusinessSessionId equals s.Id
            where scope.BranchIds.Contains(o.BranchId) && s.BusinessDate >= f.From && s.BusinessDate <= f.To
            select new { o, s.BusinessDate };
        if (f.SessionId is { } sessionId) query = query.Where(x => x.o.BusinessSessionId == sessionId);
        if (f.OrderType is { } type) query = query.Where(x => x.o.Type == type);
        if (f.CashierId is { } cashier) query = query.Where(x => x.o.CreatedByUserId == cashier);

        return await query.Select(x => new OrderRow(x.o.Id, x.o.BranchId, x.o.OrderNumber, x.BusinessDate, x.o.Type, x.o.Status, x.o.Subtotal, x.o.DiscountTotal,
                x.o.DealDiscountTotal, x.o.ServiceCharge, x.o.TaxTotal, x.o.RoundingAdjustment, x.o.Total, x.o.PaidTotal, x.o.VoidReason, x.o.CreatedAt, x.o.UpdatedAt))
            .ToListAsync(ct);
    }

    /// <summary>Voided orders' lines plus lines voided after they reached the kitchen (as on the close-day summary).</summary>
    private async Task<decimal> VoidedValueAsync(List<OrderRow> orders, CancellationToken ct)
    {
        var voidedIds = orders.Where(o => o.Status == OrderStatus.Voided).Select(o => o.Id).ToList();
        var countedIds = orders.Where(o => o.Counted).Select(o => o.Id).ToList();
        return await db.OrderLines
            .Where(l => voidedIds.Contains(l.OrderId) || (countedIds.Contains(l.OrderId) && l.Status == OrderLineStatus.Voided && l.SentAt != null))
            .SumAsync(l => l.LineTotal, ct);
    }

    private sealed record PaymentRow(Guid Id, Guid OrderId, DateOnly BusinessDate, string Method, bool IsCash, decimal Amount, Guid? TakenBy);

    private sealed record RefundRow(Guid PaymentId, Guid OrderId, string OrderNumber, DateOnly BusinessDate, string Method, bool IsCash, decimal Amount,
        string Reason, Guid? By, string? ByName, DateTimeOffset At);

    /// <summary>Payments booked to the period's drawers (as on the close-day summary).</summary>
    private async Task<List<PaymentRow>> PaymentRowsAsync(Scope scope, CancellationToken ct)
    {
        var f = scope.Filter;
        var query =
            from p in db.Payments
            join r in db.RegisterSessions on p.RegisterSessionId equals r.Id
            join s in db.BusinessSessions on r.BusinessSessionId equals s.Id
            where scope.BranchIds.Contains(p.BranchId) && s.BusinessDate >= f.From && s.BusinessDate <= f.To
            select new { p, s.BusinessDate, SessionId = s.Id };
        if (f.SessionId is { } sessionId) query = query.Where(x => x.SessionId == sessionId);
        if (f.PaymentMethodId is { } method) query = query.Where(x => x.p.PaymentMethodId == method);
        if (f.CashierId is { } cashier) query = query.Where(x => x.p.TakenByUserId == cashier);
        if (f.OrderType is { } type) query = query.Where(x => db.Orders.Any(o => o.Id == x.p.OrderId && o.Type == type));

        return await query.Select(x => new PaymentRow(x.p.Id, x.p.OrderId, x.BusinessDate, x.p.MethodName, x.p.IsCash, x.p.Amount, x.p.TakenByUserId))
            .ToListAsync(ct);
    }

    /// <summary>Refunds booked to the period's drawers, with the method of the payment they reverse.</summary>
    private async Task<List<RefundRow>> RefundRowsAsync(Scope scope, CancellationToken ct)
    {
        var f = scope.Filter;
        var query =
            from rf in db.Refunds
            join p in db.Payments on rf.PaymentId equals p.Id
            join o in db.Orders on rf.OrderId equals o.Id
            join r in db.RegisterSessions on rf.RegisterSessionId equals r.Id
            join s in db.BusinessSessions on r.BusinessSessionId equals s.Id
            where scope.BranchIds.Contains(rf.BranchId) && s.BusinessDate >= f.From && s.BusinessDate <= f.To
            select new { rf, p.MethodName, p.PaymentMethodId, o.OrderNumber, o.Type, s.BusinessDate, SessionId = s.Id };
        if (f.SessionId is { } sessionId) query = query.Where(x => x.SessionId == sessionId);
        if (f.PaymentMethodId is { } method) query = query.Where(x => x.PaymentMethodId == method);
        if (f.CashierId is { } cashier) query = query.Where(x => x.rf.CreatedBy == cashier);
        if (f.OrderType is { } type) query = query.Where(x => x.Type == type);

        return await query.Select(x => new RefundRow(x.rf.PaymentId, x.rf.OrderId, x.OrderNumber, x.BusinessDate, x.MethodName, x.rf.IsCash, x.rf.Amount,
                x.rf.Reason, x.rf.CreatedBy, x.rf.ByName, x.rf.CreatedAt))
            .ToListAsync(ct);
    }

    private static List<PaymentMethodTotalDto> ByMethod(List<PaymentRow> payments, List<RefundRow> refunds) =>
        payments.Select(p => (p.Method, p.IsCash, Count: 1, Collected: p.Amount, Refunded: 0m))
            .Concat(refunds.Select(r => (r.Method, r.IsCash, Count: 0, Collected: 0m, Refunded: r.Amount)))
            .GroupBy(x => (x.Method, x.IsCash))
            .Select(g => new PaymentMethodTotalDto(g.Key.Method, g.Key.IsCash, g.Sum(x => x.Count), g.Sum(x => x.Collected), g.Sum(x => x.Refunded)))
            .OrderByDescending(m => m.Collected).ToList();

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        return await db.Memberships.Where(m => ids.Contains(m.UserId)).ToDictionaryAsync(m => m.UserId, m => m.DisplayName, ct);
    }

    private static string NameOf(Dictionary<Guid, string> names, Guid? userId) =>
        userId is { } id && names.TryGetValue(id, out var name) ? name : "—";

    private static decimal R(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
