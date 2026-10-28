using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Application.Features.Payments;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Orders;

/// <summary>
/// Books orders a counter PC took while the API couldn't be reached (Phase 6, decision D8 revisited). Each one goes
/// through the normal create and payment code, so it is priced, numbered, deducted from stock and audited like any
/// other order. Every step is idempotent (the order's and each payment's ClientRequestId), so a sync that fails half
/// way is simply sent again. What can't be booked exactly as taken is booked anyway and flagged for review: the
/// food has already been served and the money taken.
/// </summary>
public class OfflineSyncService(
    IApplicationDbContext db,
    ITenantContext tenant,
    OrderService orders,
    PaymentService payments,
    OrderProgress progress,
    AuditLogger audit,
    TimeProvider clock)
{
    /// <summary>How far apart the PC's total and the server's may be before someone has to look (rounding differences).</summary>
    private const decimal PriceTolerance = 1m;

    public async Task<OfflineOrderResult> SyncAsync(OfflineOrderRequest request, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        var number = request.OfflineNumber?.Trim() ?? "";
        if (number.Length is 0 or > 20) throw new ValidationAppException(nameof(request.OfflineNumber), "The offline number is missing or too long.");
        var now = clock.GetUtcNow();
        if (request.TakenAt > now.AddMinutes(10) || request.TakenAt < now.AddDays(-7))
        {
            throw new ValidationAppException(nameof(request.TakenAt), "The time it was taken is off. Check the counter PC's clock.");
        }

        var notes = new List<string>();
        var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.ClientRequestId == request.Order.ClientRequestId, ct);
        if (order is null)
        {
            // The day it was taken in may have been closed (on another PC) since: book it to the day that is open now.
            var openSessionId = await db.BusinessSessions.Where(s => s.BranchId == branchId && s.Status == SessionStatus.Open)
                .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct) ?? throw new ConflictException("Start the day first: offline orders are booked to an open business day.");
            if (openSessionId != request.BusinessSessionId)
            {
                var takenOn = await db.BusinessSessions.Where(s => s.Id == request.BusinessSessionId).Select(s => (DateOnly?)s.BusinessDate).FirstOrDefaultAsync(ct);
                notes.Add($"Taken offline in the business day of {takenOn?.ToString("d MMM") ?? "another day"}, which was closed before it synced; booked to today.");
            }

            OrderDto created;
            try
            {
                created = await orders.CreateServedAsync(request.Order, ct);
            }
            catch (ConflictException) when (request.Order.TableId is not null)
            {
                // The table was given to someone else while this PC was offline.
                created = await orders.CreateServedAsync(request.Order with { TableId = null }, ct);
                var table = await db.Tables.Where(t => t.Id == request.Order.TableId).Select(t => t.Number).FirstOrDefaultAsync(ct);
                notes.Add($"Table {table} already had an open order, so this one was booked without a table.");
            }
            order = await db.Orders.Include(o => o.Lines).FirstAsync(o => o.Id == created.Id, ct);

            order.OfflineNumber = number;
            order.TakenOfflineAt = request.TakenAt;
            order.CreatedAt = request.TakenAt;
            if (await db.Memberships.Where(m => m.UserId == request.TakenByUserId).Select(m => m.DisplayName).FirstOrDefaultAsync(ct) is { } takenBy)
            {
                order.CreatedByUserId = request.TakenByUserId;
                order.CreatedByName = takenBy;
            }

            // The kitchen cooked from the paper ticket: nothing for the kitchen screen to do, nothing to block closing the day.
            var tickets = await db.KitchenTickets.Where(t => t.OrderId == order.Id && t.Status != KitchenTicketStatus.Served).ToListAsync(ct);
            foreach (var ticket in tickets)
            {
                ticket.Status = KitchenTicketStatus.Served;
                ticket.ServedAt = now;
            }
            await progress.UpdateAsync(order, tickets, ct, reason: "Taken offline; served from the paper ticket");

            if (Math.Abs(order.Total - request.EstimatedTotal) > PriceTolerance)
            {
                notes.Add($"The counter showed {request.EstimatedTotal:0.##} offline; the order is priced {order.Total:0.##} (deals, taxes or prices differ).");
            }
            Flag(order, notes);
            audit.Record(AuditActions.OrderSyncedOffline, nameof(Order), order.Id,
                after: new { order.OrderNumber, order.OfflineNumber, request.TakenAt, request.EstimatedTotal, order.Total, order.NeedsReview });
            await db.SaveChangesAsync(ct);
        }

        // Payments: each one once. A payment that no longer fits the server's total is booked as far as it can and flagged.
        foreach (var payment in request.Payments)
        {
            if (await db.Payments.AnyAsync(p => p.ClientRequestId == payment.ClientRequestId, ct)) continue;
            order = await db.Orders.Include(o => o.Lines).FirstAsync(o => o.Id == order.Id, ct);
            var balance = order.Total - order.PaidTotal;
            if (balance <= 0)
            {
                Flag(order, [$"A payment of {payment.Amount:0.##} was taken offline but the order was already paid: refund or check it."]);
                await db.SaveChangesAsync(ct);
                continue;
            }

            var amount = Math.Min(payment.Amount, balance);
            try
            {
                await payments.TakeAsync(order.Id, new TakePaymentRequest(payment.ClientRequestId, payment.PaymentMethodId, amount,
                    payment.Tendered is { } tendered ? Math.Max(tendered, amount) : null, payment.Reference, request.RegisterId), ct);
            }
            catch (Exception ex) when (ex is ConflictException or ValidationAppException or NotFoundException)
            {
                db.ChangeTracker.Clear();
                order = await db.Orders.Include(o => o.Lines).FirstAsync(o => o.Id == order.Id, ct);
                Flag(order, [$"A payment of {payment.Amount:0.##} taken offline couldn't be booked ({ex.Message}). Take it on the order by hand."]);
                await db.SaveChangesAsync(ct);
                continue;
            }

            var booked = await db.Payments.FirstAsync(p => p.ClientRequestId == payment.ClientRequestId, ct);
            if (await db.Memberships.Where(m => m.UserId == request.TakenByUserId).Select(m => m.DisplayName).FirstOrDefaultAsync(ct) is { } name)
            {
                booked.TakenByUserId = request.TakenByUserId;
                booked.TakenByName = name;
            }
            booked.CreatedAt = request.TakenAt;
            if (amount < payment.Amount)
            {
                order = await db.Orders.FirstAsync(o => o.Id == order.Id, ct);
                Flag(order, [$"{payment.Amount - amount:0.##} more was taken offline than the order now costs: give it back or check it."]);
            }
            await db.SaveChangesAsync(ct);
        }

        var final = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id, ct);
        return new OfflineOrderResult(final.Id, final.OrderNumber, final.NeedsReview, final.ReviewNote);
    }

    /// <summary>A manager has looked at a flagged order (and corrected it with the usual refunds, payments or voids).</summary>
    public async Task<OrderDto> MarkReviewedAsync(Guid orderId, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var order = await db.Orders.Include(o => o.Lines).ThenInclude(l => l.Modifiers).FirstOrDefaultAsync(o => o.Id == orderId && o.BranchId == branchId, ct)
            ?? throw new NotFoundException("Order");
        if (order.NeedsReview)
        {
            order.NeedsReview = false;
            audit.Record(AuditActions.OrderReviewed, nameof(Order), order.Id, before: new { order.ReviewNote });
            await db.SaveChangesAsync(ct);
        }
        return await orders.GetAsync(order.Id, ct);
    }

    private static void Flag(Order order, IReadOnlyCollection<string> notes)
    {
        if (notes.Count == 0) return;
        order.NeedsReview = true;
        var text = string.Join(" ", new[] { order.ReviewNote }.Concat(notes).Where(n => !string.IsNullOrEmpty(n)));
        order.ReviewNote = text.Length > 1000 ? text[..1000] : text;
    }
}
