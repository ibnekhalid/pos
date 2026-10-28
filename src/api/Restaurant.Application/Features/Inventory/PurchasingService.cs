using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Inventory;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Inventory;

/// <summary>
/// Suppliers (whole restaurant) and purchase orders (selected branch). A purchase order is a draft until it is
/// placed; after that only receiving changes it (StockService.ReceiveAsync with its id) or a cancel. It never
/// moves stock itself.
/// </summary>
public class PurchasingService(IApplicationDbContext db, ITenantContext tenant, StockService stock, AuditLogger audit, TimeProvider clock)
{
    // ---------------------------------------------------------------- suppliers

    public async Task<IReadOnlyList<SupplierDto>> SuppliersAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        return await db.Suppliers.OrderBy(s => s.Name).Select(s => ToDto(s)).ToListAsync(ct);
    }

    public async Task<SupplierDto> SaveSupplierAsync(Guid? id, SaveSupplierRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) throw new ValidationAppException(nameof(request.Name), "Enter the supplier's name (up to 100 characters).");
        if (request.ContactName?.Length > 100 || request.Phone?.Length > 30 || request.Address?.Length > 300 || request.Notes?.Length > 500)
        {
            throw new ValidationAppException(nameof(request.Notes), "One of the details is too long.");
        }

        var supplier = id is null ? new Supplier { TenantId = tenantId }
            : await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Supplier");
        if (await db.Suppliers.AnyAsync(s => s.Name == name && s.Id != supplier.Id, ct))
        {
            throw new ConflictException("A supplier with this name already exists.");
        }
        var before = id is null ? null : ToDto(supplier);
        supplier.Name = name;
        supplier.ContactName = Blank(request.ContactName);
        supplier.Phone = Blank(request.Phone);
        supplier.Address = Blank(request.Address);
        supplier.Notes = Blank(request.Notes);
        supplier.IsActive = request.IsActive;
        if (id is null) db.Suppliers.Add(supplier);
        audit.Record(AuditActions.SupplierSaved, nameof(Supplier), supplier.Id, before, ToDto(supplier));
        await db.SaveChangesAsync(ct);
        return ToDto(supplier);
    }

    // ---------------------------------------------------------------- purchase orders

    /// <summary>The selected branch's purchase orders: open ones first, then the latest 100 closed.</summary>
    public async Task<IReadOnlyList<PurchaseOrderDto>> PurchaseOrdersAsync(CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var open = await Query().Where(p => p.BranchId == branchId && p.Status != PurchaseOrderStatus.Received && p.Status != PurchaseOrderStatus.Cancelled)
            .OrderByDescending(p => p.Sequence).ToListAsync(ct);
        var closed = await Query().Where(p => p.BranchId == branchId && (p.Status == PurchaseOrderStatus.Received || p.Status == PurchaseOrderStatus.Cancelled))
            .OrderByDescending(p => p.Sequence).Take(100).ToListAsync(ct);
        return await ToDtosAsync(open.Concat(closed).ToList(), ct);
    }

    public async Task<PurchaseOrderDto> GetAsync(Guid id, CancellationToken ct) => (await ToDtosAsync([await LoadAsync(id, ct)], ct))[0];

    /// <summary>Creates a draft, or replaces a draft's supplier, date, note and lines.</summary>
    public async Task<PurchaseOrderDto> SaveAsync(Guid? id, SavePurchaseOrderRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        if (request.Lines is not { Count: > 0 }) throw new ValidationAppException(nameof(request.Lines), "Add at least one item.");
        if (request.Lines.Any(l => l.Quantity <= 0 || l.UnitCost < 0)) throw new ValidationAppException(nameof(request.Lines), "Quantities must be above zero and costs can't be negative.");
        if (request.Lines.GroupBy(l => l.InventoryItemId).Any(g => g.Count() > 1)) throw new ValidationAppException(nameof(request.Lines), "Each item can be on the order once.");
        if (request.Notes?.Length > 500) throw new ValidationAppException(nameof(request.Notes), "The note is too long.");
        if (!await db.Suppliers.AnyAsync(s => s.Id == request.SupplierId && s.IsActive, ct)) throw new NotFoundException("Supplier");
        var items = await stock.LoadItemsAsync(request.Lines.Select(l => l.InventoryItemId), ct);

        PurchaseOrder order;
        if (id is null)
        {
            order = new PurchaseOrder { TenantId = tenantId, BranchId = branchId };
            db.PurchaseOrders.Add(order);
        }
        else
        {
            order = await LoadAsync(id.Value, ct);
            if (order.Status != PurchaseOrderStatus.Draft) throw new ConflictException("Only a draft can be changed. Cancel it and make a new one instead.");
            db.PurchaseOrderLines.RemoveRange(order.Lines);
            order.Lines.Clear();
        }

        order.SupplierId = request.SupplierId;
        order.ExpectedDate = request.ExpectedDate;
        order.Notes = Blank(request.Notes);
        foreach (var line in request.Lines)
        {
            var item = items[line.InventoryItemId];
            var row = new PurchaseOrderLine
            {
                TenantId = tenantId,
                PurchaseOrderId = order.Id,
                InventoryItemId = item.Id,
                Quantity = Math.Round(line.Quantity, 3),
                Unit = line.Unit.Trim(),
                UnitCost = Math.Round(line.UnitCost, 4),
                QuantityBase = Math.Round(StockService.ToBase(item, line.Quantity, line.Unit), 3)
            };
            order.Lines.Add(row);
            db.PurchaseOrderLines.Add(row);
        }
        order.Total = order.Lines.Sum(l => Math.Round(l.Quantity * l.UnitCost, 2));

        if (id is null)
        {
            order.Sequence = (await db.PurchaseOrders.Where(p => p.BranchId == branchId).MaxAsync(p => (int?)p.Sequence, ct) ?? 0) + 1;
            order.Number = $"PO-{order.Sequence:000}";
        }
        audit.Record(AuditActions.PurchaseOrderSaved, nameof(PurchaseOrder), order.Id, after: new { order.Number, order.Total, Lines = order.Lines.Count });
        await SaveAsync(ct);
        return await GetAsync(order.Id, ct);
    }

    /// <summary>Draft → Ordered: it was sent to the supplier and can now be received against.</summary>
    public async Task<PurchaseOrderDto> PlaceAsync(Guid id, CancellationToken ct)
    {
        var order = await LoadAsync(id, ct);
        if (order.Status != PurchaseOrderStatus.Draft) throw new ConflictException("This purchase order has already been placed.");
        order.Status = PurchaseOrderStatus.Ordered;
        order.OrderedAt = clock.GetUtcNow();
        audit.Record(AuditActions.PurchaseOrderOrdered, nameof(PurchaseOrder), order.Id, after: new { order.Number, order.Total });
        await SaveAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Stops a draft or an order still waiting for goods. What was already received stays booked.</summary>
    public async Task<PurchaseOrderDto> CancelAsync(Guid id, CancellationToken ct)
    {
        var order = await LoadAsync(id, ct);
        if (order.Status is PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled) throw new ConflictException("This purchase order is already closed.");
        order.Status = PurchaseOrderStatus.Cancelled;
        audit.Record(AuditActions.PurchaseOrderCancelled, nameof(PurchaseOrder), order.Id, after: new { order.Number });
        await SaveAsync(ct);
        return await GetAsync(id, ct);
    }

    // ---------------------------------------------------------------- shared

    private IQueryable<PurchaseOrder> Query() => db.PurchaseOrders.Include(p => p.Lines);

    private async Task<PurchaseOrder> LoadAsync(Guid id, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        return await Query().FirstOrDefaultAsync(p => p.Id == id && p.BranchId == branchId, ct) ?? throw new NotFoundException("Purchase order");
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("This purchase order was changed at the same moment. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            throw new ConflictException("Another purchase order took the same number at the same moment. Try again.");
        }
    }

    private async Task<List<PurchaseOrderDto>> ToDtosAsync(List<PurchaseOrder> orders, CancellationToken ct)
    {
        var supplierIds = orders.Select(o => o.SupplierId).Distinct().ToList();
        var suppliers = await db.Suppliers.Where(s => supplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var itemIds = orders.SelectMany(o => o.Lines).Select(l => l.InventoryItemId).Distinct().ToList();
        var items = await db.InventoryItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => (i.Name, i.BaseUnit), ct);
        var userIds = orders.Select(o => o.CreatedBy).Where(u => u is not null).Distinct().ToList();
        var names = await db.Memberships.Where(m => userIds.Contains(m.UserId)).ToDictionaryAsync(m => m.UserId, m => m.DisplayName, ct);

        return orders.Select(o =>
        {
            var supplier = suppliers.GetValueOrDefault(o.SupplierId);
            return new PurchaseOrderDto(o.Id, o.Number, o.SupplierId, supplier?.Name ?? "—", supplier?.Phone, o.Status, o.ExpectedDate, o.Notes, o.Total,
                o.CreatedAt, o.OrderedAt, o.CreatedBy is { } by ? names.GetValueOrDefault(by) : null,
                o.Lines.Select(l =>
                {
                    var (name, baseUnit) = items.GetValueOrDefault(l.InventoryItemId, ("—", "pc"));
                    return new PurchaseOrderLineDto(l.Id, l.InventoryItemId, name, baseUnit, l.Quantity, l.Unit, l.UnitCost, Math.Round(l.Quantity * l.UnitCost, 2),
                        l.QuantityBase, l.ReceivedBase);
                }).OrderBy(l => l.ItemName).ToList());
        }).ToList();
    }

    private static SupplierDto ToDto(Supplier s) => new(s.Id, s.Name, s.ContactName, s.Phone, s.Address, s.Notes, s.IsActive);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
