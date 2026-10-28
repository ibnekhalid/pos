using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Contracts.Enums;
using Restaurant.Domain.Entities;
using Restaurant.Domain.Orders;

namespace Restaurant.Application.Features.Orders;

/// <summary>Keeps an order's status in step with its kitchen tickets and payments, with a history row per change.</summary>
public class OrderProgress(IApplicationDbContext db, ITenantContext tenant, TimeProvider clock)
{
    /// <param name="changedTickets">Tickets added or changed in this unit of work (not saved yet).</param>
    public async Task UpdateAsync(Order order, IReadOnlyCollection<KitchenTicket> changedTickets, CancellationToken ct, string? reason = null, Guid? approvedBy = null)
    {
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Voided)
        {
            return;
        }

        var changedIds = changedTickets.Select(t => t.Id).ToList();
        var saved = await db.KitchenTickets
            .Where(t => t.OrderId == order.Id && !t.IsChange && !changedIds.Contains(t.Id))
            .Select(t => t.Status)
            .ToListAsync(ct);
        var statuses = saved
            .Concat(changedTickets.Where(t => t.OrderId == order.Id && !t.IsChange).Select(t => t.Status))
            .Where(s => s != KitchenTicketStatus.Cancelled)
            .ToList();

        var hasUnsent = order.Lines.Any(l => l.Status == OrderLineStatus.Active && l.SentAt is null);
        var next = OrderRules.ServiceStatus(statuses, hasUnsent, paid: order.PaidTotal >= order.Total);
        Set(order, next, reason, approvedBy);
    }

    public void Set(Order order, OrderStatus next, string? reason = null, Guid? approvedBy = null)
    {
        if (order.Status == next) return;

        db.OrderStatusHistory.Add(new OrderStatusHistory
        {
            TenantId = order.TenantId,
            OrderId = order.Id,
            FromStatus = order.Status,
            ToStatus = next,
            Reason = reason,
            ApprovedByUserId = approvedBy,
            CreatedBy = tenant.UserId
        });
        order.Status = next;
        if (next is OrderStatus.Completed or OrderStatus.Cancelled or OrderStatus.Voided)
        {
            order.ClosedAt = clock.GetUtcNow();
            if (next == OrderStatus.Completed) order.CompletedAt = order.ClosedAt;
        }
    }
}
