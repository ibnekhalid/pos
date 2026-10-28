using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Tables;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Tables;

/// <summary>
/// Dining areas and tables of the selected branch. A table is occupied while it has an open order, needs
/// cleaning once an order on it closes (until someone marks it clean), and shows a booking from two hours
/// before it starts until it ends.
/// </summary>
public class TableService(IApplicationDbContext db, ITenantContext tenant, IRealtimeNotifier notifier, TimeProvider clock)
{
    public async Task<IReadOnlyList<DiningAreaDto>> ListAreasAsync(CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        return await db.DiningAreas.Where(a => a.BranchId == branchId).OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .Select(a => new DiningAreaDto(a.Id, a.Name, a.SortOrder)).ToListAsync(ct);
    }

    public async Task<DiningAreaDto> SaveAreaAsync(Guid? id, SaveDiningAreaRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 60) throw new ValidationAppException("Name", "Enter a name (up to 60 characters).");

        var area = id is null ? new DiningArea { TenantId = tenantId, BranchId = branchId }
            : await db.DiningAreas.FirstOrDefaultAsync(a => a.Id == id && a.BranchId == branchId, ct) ?? throw new NotFoundException("Area");
        if (await db.DiningAreas.AnyAsync(a => a.BranchId == branchId && a.Name == request.Name.Trim() && a.Id != area.Id, ct))
        {
            throw new ConflictException("An area with this name already exists.");
        }
        area.Name = request.Name.Trim();
        area.SortOrder = request.SortOrder;
        if (id is null) db.DiningAreas.Add(area);
        await db.SaveChangesAsync(ct);
        return new DiningAreaDto(area.Id, area.Name, area.SortOrder);
    }

    public async Task<IReadOnlyList<TableDto>> ListTablesAsync(CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var tables = await db.Tables.Where(t => t.BranchId == branchId).ToListAsync(ct);
        var state = await StateAsync(branchId, ct);
        return tables.OrderBy(t => t.Number.Length).ThenBy(t => t.Number).Select(t => ToDto(t, state)).ToList();
    }

    /// <summary>Someone cleared and reset the table.</summary>
    public async Task<TableDto> MarkCleanAsync(Guid id, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var table = await db.Tables.FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId, ct) ?? throw new NotFoundException("Table");
        table.CleanedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await notifier.OrdersChangedAsync(branchId);
        return ToDto(table, await StateAsync(branchId, ct));
    }

    public async Task<TableDto> SaveTableAsync(Guid? id, SaveTableRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        if (string.IsNullOrWhiteSpace(request.Number) || request.Number.Length > 10) throw new ValidationAppException("Number", "Enter a table number (up to 10 characters).");
        if (request.Seats is < 1 or > 100) throw new ValidationAppException("Seats", "Seats must be between 1 and 100.");
        if (request.AreaId is { } areaId && !await db.DiningAreas.AnyAsync(a => a.Id == areaId && a.BranchId == branchId, ct)) throw new NotFoundException("Area");

        var table = id is null ? new RestaurantTable { TenantId = tenantId, BranchId = branchId }
            : await db.Tables.FirstOrDefaultAsync(t => t.Id == id && t.BranchId == branchId, ct) ?? throw new NotFoundException("Table");
        var number = request.Number.Trim();
        if (await db.Tables.AnyAsync(t => t.BranchId == branchId && t.Number == number && t.Id != table.Id, ct))
        {
            throw new ConflictException($"Table {number} already exists.");
        }
        table.AreaId = request.AreaId;
        table.Number = number;
        table.Seats = request.Seats;
        table.IsActive = request.IsActive;
        if (id is null) db.Tables.Add(table);
        await db.SaveChangesAsync(ct);

        return ToDto(table, await StateAsync(branchId, ct));
    }

    private sealed record TableState(
        Dictionary<Guid, (Guid Id, string OrderNumber)> Open, Dictionary<Guid, DateTimeOffset> LastClosed, Dictionary<Guid, Reservation> Booked);

    private static TableDto ToDto(RestaurantTable t, TableState state)
    {
        var occupied = state.Open.TryGetValue(t.Id, out var order);
        var dirty = !occupied && state.LastClosed.TryGetValue(t.Id, out var closedAt) && closedAt > (t.CleanedAt ?? DateTimeOffset.MinValue);
        var booking = state.Booked.GetValueOrDefault(t.Id);
        return new TableDto(t.Id, t.AreaId, t.Number, t.Seats, t.IsActive, occupied ? order.Id : null, occupied ? order.OrderNumber : null,
            dirty, booking?.Id, booking?.CustomerName, booking?.At);
    }

    private async Task<TableState> StateAsync(Guid branchId, CancellationToken ct)
    {
        var open = await db.Orders
            .Where(o => o.BranchId == branchId && o.TableId != null
                && o.Status != OrderStatus.Completed && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Voided)
            .Select(o => new { TableId = o.TableId!.Value, o.Id, o.OrderNumber })
            .ToListAsync(ct);

        // Guests sat there: completed or voided after the kitchen. A cancelled order never reached the table.
        var since = clock.GetUtcNow().AddDays(-2);
        var closed = await db.Orders
            .Where(o => o.BranchId == branchId && o.TableId != null && o.ClosedAt != null && o.ClosedAt > since
                && (o.Status == OrderStatus.Completed || o.Status == OrderStatus.Voided))
            .GroupBy(o => o.TableId!.Value).Select(g => new { g.Key, At = g.Max(o => o.ClosedAt!.Value) })
            .ToListAsync(ct);

        var now = clock.GetUtcNow();
        var soon = now.AddHours(2);
        var bookings = await db.Reservations
            .Where(r => r.BranchId == branchId && r.TableId != null && r.Status == ReservationStatus.Booked && r.At < soon && r.At > now.AddHours(-12))
            .ToListAsync(ct);

        return new TableState(
            open.GroupBy(r => r.TableId).ToDictionary(g => g.Key, g => (g.First().Id, g.First().OrderNumber)),
            closed.ToDictionary(c => c.Key, c => c.At),
            bookings.Where(r => r.At.AddMinutes(r.DurationMinutes) > now).GroupBy(r => r.TableId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(r => r.At).First()));
    }
}
