using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Contracts.Tables;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Tables;

/// <summary>Dining areas and tables of the selected branch. A table is occupied while it has an open order.</summary>
public class TableService(IApplicationDbContext db, ITenantContext tenant)
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
        var open = await OpenOrdersByTableAsync(branchId, ct);
        return tables
            .OrderBy(t => t.Number.Length).ThenBy(t => t.Number)
            .Select(t =>
            {
                var found = open.TryGetValue(t.Id, out var order);
                return new TableDto(t.Id, t.AreaId, t.Number, t.Seats, t.IsActive, found ? order.Id : null, found ? order.OrderNumber : null);
            })
            .ToList();
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

        var open = await OpenOrdersByTableAsync(branchId, ct);
        var found = open.TryGetValue(table.Id, out var order);
        return new TableDto(table.Id, table.AreaId, table.Number, table.Seats, table.IsActive, found ? order.Id : null, found ? order.OrderNumber : null);
    }

    private async Task<Dictionary<Guid, (Guid Id, string OrderNumber)>> OpenOrdersByTableAsync(Guid branchId, CancellationToken ct)
    {
        var rows = await db.Orders
            .Where(o => o.BranchId == branchId && o.TableId != null
                && o.Status != Contracts.Enums.OrderStatus.Completed && o.Status != Contracts.Enums.OrderStatus.Cancelled && o.Status != Contracts.Enums.OrderStatus.Voided)
            .Select(o => new { TableId = o.TableId!.Value, o.Id, o.OrderNumber })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.TableId).ToDictionary(g => g.Key, g => (g.First().Id, g.First().OrderNumber));
    }
}
