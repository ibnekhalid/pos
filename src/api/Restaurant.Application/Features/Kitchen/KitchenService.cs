using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Application.Features.Orders;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Kitchen;
using Restaurant.Domain.Orders;

namespace Restaurant.Application.Features.Kitchen;

/// <summary>The kitchen display: persisted tickets, read fresh on every load (PLAN.md section 8, T8).</summary>
public class KitchenService(IApplicationDbContext db, ITenantContext tenant, OrderProgress progress, IRealtimeNotifier notifier, TimeProvider clock)
{
    /// <summary>Open tickets of the selected branch (optionally one station), oldest first.</summary>
    public async Task<IReadOnlyList<KitchenTicketDto>> ListAsync(Guid? stationId, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var tickets = await db.KitchenTickets.Include(t => t.Items)
            .Where(t => t.BranchId == branchId
                && (t.Status == KitchenTicketStatus.New || t.Status == KitchenTicketStatus.Preparing || t.Status == KitchenTicketStatus.Ready)
                && (stationId == null || t.StationId == stationId || t.StationId == null))
            .OrderBy(t => t.SentAt)
            .ToListAsync(ct);

        var orderIds = tickets.Select(t => t.OrderId).Distinct().ToList();
        var notes = await db.Orders.Where(o => orderIds.Contains(o.Id) && o.Notes != null).ToDictionaryAsync(o => o.Id, o => o.Notes, ct);
        return tickets.Select(t => new KitchenTicketDto(t.Id, t.OrderId, t.OrderNumber, t.OrderType, t.TableLabel, t.StationId, t.Round, t.Status, t.IsChange,
            t.SentAt, t.StartedAt, t.ReadyAt,
            t.Items.Select(i => new KitchenTicketItemDto(i.Id, i.Quantity, i.Name,
                i.ModifiersText is null ? [] : i.ModifiersText.Split(", ").ToList(), i.Notes, i.Cancelled)).ToList(),
            notes.GetValueOrDefault(t.OrderId))).ToList();
    }

    public async Task UpdateStatusAsync(Guid ticketId, UpdateTicketStatusRequest request, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var ticket = await db.KitchenTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct) ?? throw new NotFoundException("Kitchen ticket");
        tenant.EnsureBranch(ticket.BranchId);

        var allowed = ticket.IsChange
            ? ticket.Status == KitchenTicketStatus.New && request.Status == KitchenTicketStatus.Served
            : OrderRules.CanMoveTicket(ticket.Status, request.Status);
        if (!allowed) throw new ConflictException("This ticket has already moved on.");

        var now = clock.GetUtcNow();
        ticket.Status = request.Status;
        if (request.Status >= KitchenTicketStatus.Preparing) ticket.StartedAt ??= now;
        if (request.Status >= KitchenTicketStatus.Ready) ticket.ReadyAt ??= now;
        if (request.Status == KitchenTicketStatus.Served) ticket.ServedAt = now;

        var order = await db.Orders.Include(o => o.Lines).FirstAsync(o => o.Id == ticket.OrderId, ct);
        await progress.UpdateAsync(order, [ticket], ct);
        await db.SaveChangesAsync(ct);

        await notifier.TicketsChangedAsync(ticket.BranchId);
        await notifier.OrdersChangedAsync(ticket.BranchId);
    }
}
