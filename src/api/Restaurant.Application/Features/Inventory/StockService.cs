using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Application.Features.Approvals;
using Restaurant.Application.Features.Orders;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Inventory;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Inventory;

/// <summary>
/// Stock (PLAN.md section 9): a ledger of movements per branch. On hand is always the ledger's sum;
/// the balance row only keeps the weighted-average cost. Every entry has an idempotency key, so a retry
/// or a double tap never books the same event twice.
/// </summary>
public class StockService(IApplicationDbContext db, ITenantContext tenant, ApprovalService approvals, AuditLogger audit, TimeProvider clock)
{
    // ---------------------------------------------------------------- stock items

    public async Task<IReadOnlyList<InventoryItemDto>> ListItemsAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        var items = await db.InventoryItems.Include(i => i.Units).OrderBy(i => i.Name).ToListAsync(ct);
        return items.Select(ToDto).ToList();
    }

    public async Task<InventoryItemDto> SaveItemAsync(Guid? id, SaveInventoryItemRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) throw new ValidationAppException(nameof(request.Name), "Enter a name (up to 100 characters).");
        if (!UnitCatalog.BaseUnits.Contains(request.BaseUnit)) throw new ValidationAppException(nameof(request.BaseUnit), "Choose g, ml or pc as the base unit.");
        if (request.ReorderLevel < 0) throw new ValidationAppException(nameof(request.ReorderLevel), "The reorder level can't be negative.");
        var units = request.Units ?? [];
        if (units.Any(u => string.IsNullOrWhiteSpace(u.Name) || u.Name.Trim().Length > 30 || u.FactorToBase <= 0 || UnitCatalog.Find(u.Name.Trim()) is not null)
            || units.Select(u => u.Name.Trim().ToLowerInvariant()).Distinct().Count() != units.Count)
        {
            throw new ValidationAppException(nameof(request.Units), "Each pack size needs its own name (not g, kg, ml, l, pc or dozen) and a size above zero.");
        }
        if (await db.InventoryItems.AnyAsync(i => i.Name == name && i.Id != id, ct))
        {
            throw new ConflictException($"There is already a stock item called {name}.");
        }

        InventoryItem item;
        if (id is { } existingId)
        {
            item = await db.InventoryItems.Include(i => i.Units).FirstOrDefaultAsync(i => i.Id == existingId, ct) ?? throw new NotFoundException("Stock item");
            if (item.BaseUnit != request.BaseUnit && await db.StockMovements.AnyAsync(m => m.InventoryItemId == item.Id, ct))
            {
                throw new ConflictException("The base unit can't change once stock has been booked. Add a new item instead.");
            }
            db.InventoryItemUnits.RemoveRange(item.Units);
            item.Units.Clear();
        }
        else
        {
            item = new InventoryItem { TenantId = tenantId };
            db.InventoryItems.Add(item);
        }

        item.Name = name;
        item.NameUr = Blank(request.NameUr);
        item.BaseUnit = request.BaseUnit;
        item.Category = Blank(request.Category);
        item.ReorderLevel = request.ReorderLevel;
        item.IsActive = request.IsActive;
        foreach (var unit in units)
        {
            var row = new InventoryItemUnit { TenantId = tenantId, InventoryItemId = item.Id, Name = unit.Name.Trim(), FactorToBase = unit.FactorToBase };
            item.Units.Add(row);
            db.InventoryItemUnits.Add(row);
        }

        audit.Record(AuditActions.InventoryItemSaved, nameof(InventoryItem), item.Id, after: new { item.Name, item.BaseUnit, item.ReorderLevel, item.IsActive });
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    // ---------------------------------------------------------------- levels and ledger

    public async Task<IReadOnlyList<StockLevelDto>> LevelsAsync(CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var items = await db.InventoryItems.Include(i => i.Units).Where(i => i.IsActive).OrderBy(i => i.Name).ToListAsync(ct);
        var onHand = await OnHandAsync(branchId, null, ct);
        var costs = await db.InventoryBalances.Where(b => b.BranchId == branchId).ToDictionaryAsync(b => b.InventoryItemId, b => b.AverageCost, ct);

        return items.Select(i =>
        {
            var quantity = onHand.GetValueOrDefault(i.Id);
            var cost = costs.GetValueOrDefault(i.Id);
            return new StockLevelDto(i.Id, i.Name, i.BaseUnit, i.Category, quantity, i.ReorderLevel, cost, Math.Round(Math.Max(quantity, 0) * cost, 2),
                quantity <= i.ReorderLevel, i.Units.Select(u => new InventoryItemUnitDto(u.Name, u.FactorToBase)).ToList());
        }).ToList();
    }

    /// <summary>
    /// What needs ordering at the selected branch (home screen and navigation alert). Items without a reorder
    /// level only show once they go negative, so an untracked item never nags.
    /// </summary>
    public async Task<IReadOnlyList<LowStockDto>> LowStockAsync(CancellationToken ct) =>
        (await LevelsAsync(ct))
            .Where(l => l.OnHand < 0 || (l.ReorderLevel > 0 && l.OnHand <= l.ReorderLevel))
            .OrderByDescending(l => l.ReorderLevel == 0 ? decimal.MaxValue : (l.ReorderLevel - l.OnHand) / l.ReorderLevel)
            .Select(l => new LowStockDto(l.ItemId, l.Name, l.BaseUnit, l.Category, l.OnHand, l.ReorderLevel, l.ReorderLevel - l.OnHand))
            .ToList();

    public async Task<IReadOnlyList<StockMovementDto>> MovementsAsync(Guid? itemId, int take, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var query = db.StockMovements.Where(m => m.BranchId == branchId);
        if (itemId is { } id) query = query.Where(m => m.InventoryItemId == id);

        var rows = await query.OrderByDescending(m => m.CreatedAt).Take(Math.Clamp(take, 1, 500))
            .Join(db.InventoryItems, m => m.InventoryItemId, i => i.Id, (m, i) => new { Movement = m, i.Name, i.BaseUnit })
            .ToListAsync(ct);
        var userIds = rows.Select(r => r.Movement.CreatedBy).Where(u => u is not null).Distinct().ToList();
        var names = await db.Memberships.Where(m => userIds.Contains(m.UserId)).ToDictionaryAsync(m => m.UserId, m => m.DisplayName, ct);

        return rows.Select(r => new StockMovementDto(r.Movement.Id, r.Movement.InventoryItemId, r.Name, r.BaseUnit, r.Movement.Type, r.Movement.Quantity,
            r.Movement.UnitCost, Reference(r.Movement), r.Movement.Reason, r.Movement.CreatedAt,
            r.Movement.CreatedBy is { } by ? names.GetValueOrDefault(by) : null)).ToList();
    }

    // ---------------------------------------------------------------- receipts, wastage, adjustments, counts

    /// <summary>Goods in from a supplier. Each line updates the item's weighted-average cost at this branch.</summary>
    public async Task<StockReceiptDto> ReceiveAsync(CreateStockReceiptRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        if (await db.StockReceipts.FirstOrDefaultAsync(r => r.ClientRequestId == request.ClientRequestId, ct) is { } already)
        {
            return ToDto(already, null);
        }
        if (request.Lines is not { Count: > 0 }) throw new ValidationAppException(nameof(request.Lines), "Add at least one item.");
        if (request.Lines.Any(l => l.Quantity <= 0 || l.UnitCost < 0)) throw new ValidationAppException(nameof(request.Lines), "Quantities must be above zero and costs can't be negative.");
        if (request.Supplier?.Length > 100 || request.InvoiceNumber?.Length > 50 || request.Notes?.Length > 300)
        {
            throw new ValidationAppException(nameof(request.Notes), "The supplier, invoice number or note is too long.");
        }

        var items = await LoadItemsAsync(request.Lines.Select(l => l.InventoryItemId), ct);

        // Against a purchase order: it must be this branch's and still waiting for goods; its supplier is the receipt's.
        PurchaseOrder? order = null;
        if (request.PurchaseOrderId is { } orderId)
        {
            order = await db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == orderId && p.BranchId == branchId, ct)
                ?? throw new NotFoundException("Purchase order");
            if (order.Status is not (PurchaseOrderStatus.Ordered or PurchaseOrderStatus.PartiallyReceived))
            {
                throw new ConflictException(order.Status == PurchaseOrderStatus.Draft ? "Place the purchase order before receiving against it." : "This purchase order is closed.");
            }
        }
        var supplierId = order?.SupplierId ?? request.SupplierId;
        var supplier = supplierId is { } sid ? await db.Suppliers.FirstOrDefaultAsync(s => s.Id == sid, ct) ?? throw new NotFoundException("Supplier") : null;

        var receipt = new StockReceipt
        {
            TenantId = tenantId,
            BranchId = branchId,
            Supplier = supplier?.Name ?? Blank(request.Supplier),
            SupplierId = supplier?.Id,
            PurchaseOrderId = order?.Id,
            InvoiceNumber = Blank(request.InvoiceNumber),
            Notes = Blank(request.Notes),
            ClientRequestId = request.ClientRequestId,
            LineCount = request.Lines.Count
        };
        db.StockReceipts.Add(receipt);

        var onHand = await OnHandAsync(branchId, items.Keys, ct);
        var balances = await BalancesAsync(branchId, items.Keys, ct);
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var item = items[line.InventoryItemId];
            var quantity = ToBase(item, line.Quantity, line.Unit);
            var lineCost = Math.Round(line.Quantity * line.UnitCost, 2);
            var unitCost = lineCost / quantity;

            // Weighted average over what is physically there (a negative balance counts as none).
            var balance = Balance(balances, tenantId, branchId, item.Id);
            var existing = Math.Max(onHand.GetValueOrDefault(item.Id), 0);
            balance.AverageCost = Math.Round((existing * balance.AverageCost + quantity * unitCost) / (existing + quantity), 4);
            onHand[item.Id] = onHand.GetValueOrDefault(item.Id) + quantity;

            receipt.TotalCost += lineCost;
            db.StockMovements.Add(Movement(tenantId, branchId, item.Id, StockMovementType.Receipt, quantity, unitCost, nameof(StockReceipt), receipt.Id,
                $"receipt:{receipt.Id}:{i}", order is null ? null : order.Number));
            if (order?.Lines.FirstOrDefault(l => l.InventoryItemId == item.Id) is { } ordered)
            {
                ordered.ReceivedBase += Math.Round(quantity, 3);
            }
        }
        if (order is not null)
        {
            // Items not on the order are fine (the supplier sent something extra); the order is done when every line is in.
            order.Status = order.Lines.All(l => l.ReceivedBase >= l.QuantityBase) ? PurchaseOrderStatus.Received : PurchaseOrderStatus.PartiallyReceived;
        }

        audit.Record(AuditActions.StockReceived, nameof(StockReceipt), receipt.Id, after: new { receipt.Supplier, receipt.InvoiceNumber, receipt.TotalCost, receipt.LineCount },
            branchId: branchId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            // A retry of this receipt returns the one already booked. Otherwise another receipt created the item's
            // first balance row at the same moment, and this one can simply be sent again.
            db.ChangeTracker.Clear();
            var booked = await db.StockReceipts.FirstOrDefaultAsync(r => r.ClientRequestId == request.ClientRequestId, ct)
                ?? throw new ConflictException("Stock was updated at the same moment. Try again.");
            return ToDto(booked, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Stock was updated at the same moment. Try again.");
        }
        return ToDto(receipt, await db.CallerNameAsync(tenant, ct));
    }

    // ---------------------------------------------------------------- transfers between branches

    /// <summary>The selected branch's transfers, sent or incoming: those in transit first, then the latest 100.</summary>
    public async Task<IReadOnlyList<StockTransferDto>> TransfersAsync(CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var mine = db.StockTransfers.Include(t => t.Lines).Where(t => t.FromBranchId == branchId || t.ToBranchId == branchId);
        var open = await mine.Where(t => t.Status == StockTransferStatus.InTransit).OrderByDescending(t => t.Sequence).ToListAsync(ct);
        var closed = await mine.Where(t => t.Status != StockTransferStatus.InTransit).OrderByDescending(t => t.Sequence).Take(100).ToListAsync(ct);
        return await TransferDtosAsync(open.Concat(closed).ToList(), ct);
    }

    /// <summary>
    /// Sends stock from the selected branch. It leaves the source now, at the source's average cost, and is
    /// in transit until the other branch receives it. The source's stock policy applies (Block refuses
    /// sending more than it has).
    /// </summary>
    public async Task<StockTransferDto> SendTransferAsync(CreateStockTransferRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        if (await db.StockTransfers.Include(t => t.Lines).FirstOrDefaultAsync(t => t.ClientRequestId == request.ClientRequestId, ct) is { } already)
        {
            return (await TransferDtosAsync([already], ct))[0];
        }
        if (request.ToBranchId == branchId) throw new ValidationAppException(nameof(request.ToBranchId), "Choose another branch to send to.");
        if (!await db.Branches.AnyAsync(b => b.Id == request.ToBranchId && b.IsActive, ct)) throw new NotFoundException("Branch");
        if (request.Lines is not { Count: > 0 }) throw new ValidationAppException(nameof(request.Lines), "Add at least one item.");
        if (request.Lines.Any(l => l.Quantity <= 0)) throw new ValidationAppException(nameof(request.Lines), "Quantities must be above zero.");
        if (request.Lines.GroupBy(l => l.InventoryItemId).Any(g => g.Count() > 1)) throw new ValidationAppException(nameof(request.Lines), "Each item can be on the transfer once.");
        if (request.Notes?.Length > 300) throw new ValidationAppException(nameof(request.Notes), "The note is too long.");

        var items = await LoadItemsAsync(request.Lines.Select(l => l.InventoryItemId), ct);
        var onHand = await OnHandAsync(branchId, items.Keys, ct);
        var costs = await BalancesAsync(branchId, items.Keys, ct);
        var lines = request.Lines.Select(l => (Item: items[l.InventoryItemId], Quantity: Math.Round(ToBase(items[l.InventoryItemId], l.Quantity, l.Unit), 3))).ToList();

        var policy = await db.Branches.Where(b => b.Id == branchId).Select(b => b.InsufficientStock).FirstAsync(ct);
        var missing = lines.Where(l => onHand.GetValueOrDefault(l.Item.Id) < l.Quantity).Select(l => l.Item.Name).ToList();
        if (policy == InsufficientStockBehavior.Block && missing.Count > 0)
        {
            throw new ConflictException($"Not enough in stock to send: {string.Join(", ", missing)}.");
        }

        var transfer = new StockTransfer
        {
            TenantId = tenantId,
            FromBranchId = branchId,
            ToBranchId = request.ToBranchId,
            Notes = Blank(request.Notes),
            ClientRequestId = request.ClientRequestId,
            Sequence = (await db.StockTransfers.MaxAsync(t => (int?)t.Sequence, ct) ?? 0) + 1
        };
        transfer.Number = $"TR-{transfer.Sequence:000}";
        db.StockTransfers.Add(transfer);
        foreach (var (item, quantity) in lines)
        {
            var unitCost = costs.GetValueOrDefault(item.Id)?.AverageCost ?? 0;
            var line = new StockTransferLine { TenantId = tenantId, StockTransferId = transfer.Id, InventoryItemId = item.Id, Quantity = quantity, UnitCost = unitCost };
            transfer.Lines.Add(line);
            db.StockTransferLines.Add(line);
            transfer.TotalCost += Math.Round(quantity * unitCost, 2);
            db.StockMovements.Add(Movement(tenantId, branchId, item.Id, StockMovementType.TransferOut, -quantity, unitCost, nameof(StockTransfer), transfer.Id,
                $"xfer-out:{transfer.Id}:{item.Id}", transfer.Number));
        }

        audit.Record(AuditActions.StockTransferSent, nameof(StockTransfer), transfer.Id,
            after: new { transfer.Number, transfer.ToBranchId, transfer.TotalCost, Lines = transfer.Lines.Count }, branchId: branchId);
        await SaveTransferAsync(ct);
        return (await TransferDtosAsync([transfer], ct))[0];
    }

    /// <summary>The destination books what arrived, at the cost it left with; its weighted-average cost follows.</summary>
    public async Task<StockTransferDto> ReceiveTransferAsync(Guid id, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var transfer = await LoadTransferAsync(id, ct);
        if (transfer.ToBranchId != branchId) throw new ConflictException("Only the branch it was sent to can receive it. Switch to that branch.");
        if (transfer.Status != StockTransferStatus.InTransit) throw new ConflictException("This transfer has already been received or cancelled.");

        var itemIds = transfer.Lines.Select(l => l.InventoryItemId).ToList();
        var onHand = await OnHandAsync(branchId, itemIds, ct);
        var balances = await BalancesAsync(branchId, itemIds, ct);
        foreach (var line in transfer.Lines)
        {
            var balance = Balance(balances, transfer.TenantId, branchId, line.InventoryItemId);
            var existing = Math.Max(onHand.GetValueOrDefault(line.InventoryItemId), 0);
            balance.AverageCost = Math.Round((existing * balance.AverageCost + line.Quantity * line.UnitCost) / (existing + line.Quantity), 4);
            db.StockMovements.Add(Movement(transfer.TenantId, branchId, line.InventoryItemId, StockMovementType.TransferIn, line.Quantity, line.UnitCost,
                nameof(StockTransfer), transfer.Id, $"xfer-in:{transfer.Id}:{line.InventoryItemId}", transfer.Number));
        }
        transfer.Status = StockTransferStatus.Received;
        transfer.ReceivedAt = clock.GetUtcNow();
        transfer.ReceivedByUserId = tenant.UserId;

        audit.Record(AuditActions.StockTransferReceived, nameof(StockTransfer), transfer.Id, after: new { transfer.Number }, branchId: branchId);
        await SaveTransferAsync(ct);
        return (await TransferDtosAsync([transfer], ct))[0];
    }

    /// <summary>The sender calls it back while it's in transit: the stock returns to the source.</summary>
    public async Task<StockTransferDto> CancelTransferAsync(Guid id, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var transfer = await LoadTransferAsync(id, ct);
        if (transfer.FromBranchId != branchId) throw new ConflictException("Only the branch that sent it can cancel it.");
        if (transfer.Status != StockTransferStatus.InTransit) throw new ConflictException("This transfer has already been received or cancelled.");

        foreach (var line in transfer.Lines)
        {
            db.StockMovements.Add(Movement(transfer.TenantId, branchId, line.InventoryItemId, StockMovementType.TransferIn, line.Quantity, line.UnitCost,
                nameof(StockTransfer), transfer.Id, $"xfer-back:{transfer.Id}:{line.InventoryItemId}", $"{transfer.Number} cancelled"));
        }
        transfer.Status = StockTransferStatus.Cancelled;

        audit.Record(AuditActions.StockTransferCancelled, nameof(StockTransfer), transfer.Id, after: new { transfer.Number }, branchId: branchId);
        await SaveTransferAsync(ct);
        return (await TransferDtosAsync([transfer], ct))[0];
    }

    private async Task<StockTransfer> LoadTransferAsync(Guid id, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        return await db.StockTransfers.Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id && (t.FromBranchId == branchId || t.ToBranchId == branchId), ct)
            ?? throw new NotFoundException("Transfer");
    }

    private async Task SaveTransferAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || (ex is DbUpdateException update && db.IsUniqueViolation(update)))
        {
            // Two people received, cancelled or sent at the same moment: the ledger keys and the row version let one through.
            throw new ConflictException("This transfer was changed at the same moment. Reload and try again.");
        }
    }

    private async Task<List<StockTransferDto>> TransferDtosAsync(List<StockTransfer> transfers, CancellationToken ct)
    {
        // Names from the whole restaurant: the other branch may be one the caller can't open.
        var branchIds = transfers.SelectMany(t => new[] { t.FromBranchId, t.ToBranchId }).Distinct().ToList();
        var branches = await db.Branches.Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var itemIds = transfers.SelectMany(t => t.Lines).Select(l => l.InventoryItemId).Distinct().ToList();
        var items = await db.InventoryItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => (i.Name, i.BaseUnit), ct);
        var userIds = transfers.Select(t => t.CreatedBy).Concat(transfers.Select(t => t.ReceivedByUserId)).Where(u => u is not null).Distinct().ToList();
        var names = await db.Memberships.Where(m => userIds.Contains(m.UserId)).ToDictionaryAsync(m => m.UserId, m => m.DisplayName, ct);
        string? NameOf(Guid? id) => id is { } u ? names.GetValueOrDefault(u) : null;

        return transfers.Select(t => new StockTransferDto(t.Id, t.Number, t.FromBranchId, branches.GetValueOrDefault(t.FromBranchId, "—"), t.ToBranchId,
            branches.GetValueOrDefault(t.ToBranchId, "—"), t.Status, t.Notes, t.TotalCost, t.CreatedAt, NameOf(t.CreatedBy), t.ReceivedAt, NameOf(t.ReceivedByUserId),
            t.Lines.Select(l =>
            {
                var (name, baseUnit) = items.GetValueOrDefault(l.InventoryItemId, ("—", "pc"));
                return new StockTransferLineDto(l.InventoryItemId, name, baseUnit, l.Quantity, l.UnitCost);
            }).OrderBy(l => l.ItemName).ToList())).ToList();
    }

    public async Task<IReadOnlyList<StockReceiptDto>> ReceiptsAsync(CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var receipts = await db.StockReceipts.Where(r => r.BranchId == branchId).OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
        var userIds = receipts.Select(r => r.CreatedBy).Distinct().ToList();
        var names = await db.Memberships.Where(m => userIds.Contains(m.UserId)).ToDictionaryAsync(m => m.UserId, m => m.DisplayName, ct);
        return receipts.Select(r => ToDto(r, r.CreatedBy is { } by ? names.GetValueOrDefault(by) : null)).ToList();
    }

    /// <summary>
    /// Wastage (spoiled, dropped: needs inventory.wastage) or an adjustment (found / missing: needs
    /// inventory.adjust or a manager's approval). Always with a reason.
    /// </summary>
    public async Task<StockMovementDto> AdjustAsync(StockAdjustmentRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        var key = $"adj:{request.ClientRequestId}";
        if (await db.StockMovements.FirstOrDefaultAsync(m => m.IdempotencyKey == key, ct) is { } already)
        {
            return await MovementDtoAsync(already, ct);
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 300) throw new ValidationAppException(nameof(request.Reason), "Give a reason (up to 300 characters).");
        if (request.Quantity == 0) throw new ValidationAppException(nameof(request.Quantity), "Enter a quantity.");
        Guid? approver = null;
        switch (request.Type)
        {
            case StockMovementType.Wastage:
                if (!tenant.HasPermission(Permissions.Inventory.Wastage)) throw new ForbiddenAccessException("You can't record wastage.");
                if (request.Quantity < 0) throw new ValidationAppException(nameof(request.Quantity), "Enter how much was wasted.");
                break;
            case StockMovementType.Adjustment:
                approver = await approvals.RequireAsync(Permissions.Inventory.Adjust, request.Approval, branchId, ct);
                break;
            default:
                throw new ValidationAppException(nameof(request.Type), "Choose wastage or an adjustment.");
        }

        var item = (await LoadItemsAsync([request.InventoryItemId], ct))[request.InventoryItemId];
        var quantity = ToBase(item, Math.Abs(request.Quantity), request.Unit) * (request.Type == StockMovementType.Wastage || request.Quantity < 0 ? -1 : 1);
        var cost = await db.InventoryBalances.Where(b => b.BranchId == branchId && b.InventoryItemId == item.Id).Select(b => b.AverageCost).FirstOrDefaultAsync(ct);

        var movement = Movement(tenantId, branchId, item.Id, request.Type, quantity, cost, null, null, key, request.Reason.Trim());
        movement.ApprovedByUserId = approver;
        db.StockMovements.Add(movement);
        audit.Record(request.Type == StockMovementType.Wastage ? AuditActions.StockWastage : AuditActions.StockAdjusted, nameof(InventoryItem), item.Id,
            after: new { item.Name, Quantity = quantity, item.BaseUnit }, reason: movement.Reason, approverUserId: approver, branchId: branchId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            movement = await db.StockMovements.FirstAsync(m => m.IdempotencyKey == key, ct);
        }
        return await MovementDtoAsync(movement, ct);
    }

    /// <summary>A physical count. The books are set to what was counted; each difference is posted as a count variance.</summary>
    public async Task<StockCountDto> CountAsync(CreateStockCountRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        if (await db.StockCounts.FirstOrDefaultAsync(c => c.ClientRequestId == request.ClientRequestId, ct) is { } already)
        {
            return ToDto(already, null);
        }
        if (request.Lines is not { Count: > 0 }) throw new ValidationAppException(nameof(request.Lines), "Count at least one item.");
        if (request.Lines.Any(l => l.Counted < 0)) throw new ValidationAppException(nameof(request.Lines), "Counts can't be negative.");
        if (request.Lines.Select(l => l.InventoryItemId).Distinct().Count() != request.Lines.Count) throw new ValidationAppException(nameof(request.Lines), "Each item once, please.");

        var items = await LoadItemsAsync(request.Lines.Select(l => l.InventoryItemId), ct);
        var onHand = await OnHandAsync(branchId, items.Keys, ct);
        var costs = await db.InventoryBalances.Where(b => b.BranchId == branchId && items.Keys.Contains(b.InventoryItemId))
            .ToDictionaryAsync(b => b.InventoryItemId, b => b.AverageCost, ct);

        var count = new StockCount { TenantId = tenantId, BranchId = branchId, Notes = Blank(request.Notes), ClientRequestId = request.ClientRequestId };
        var lines = new List<StockCountLineDto>();
        foreach (var line in request.Lines)
        {
            var item = items[line.InventoryItemId];
            var counted = ToBase(item, line.Counted, line.Unit);
            var system = onHand.GetValueOrDefault(item.Id);
            var variance = counted - system;
            var cost = costs.GetValueOrDefault(item.Id);
            lines.Add(new StockCountLineDto(item.Id, item.Name, item.BaseUnit, system, counted, variance, Math.Round(variance * cost, 2)));
            if (variance != 0)
            {
                db.StockMovements.Add(Movement(tenantId, branchId, item.Id, StockMovementType.CountVariance, variance, cost, nameof(StockCount), count.Id,
                    $"count:{count.Id}:{item.Id}", count.Notes));
            }
        }
        count.VarianceValue = lines.Sum(l => l.VarianceValue);
        count.LinesJson = JsonSerializer.Serialize(lines, OrderPricing.Json);
        db.StockCounts.Add(count);
        audit.Record(AuditActions.StockCounted, nameof(StockCount), count.Id, after: new { Items = lines.Count, count.VarianceValue }, reason: count.Notes, branchId: branchId);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return ToDto(await db.StockCounts.FirstAsync(c => c.ClientRequestId == request.ClientRequestId, ct), null);
        }
        return ToDto(count, await db.CallerNameAsync(tenant, ct));
    }

    public async Task<IReadOnlyList<StockCountDto>> CountsAsync(CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var counts = await db.StockCounts.Where(c => c.BranchId == branchId).OrderByDescending(c => c.CreatedAt).Take(50).ToListAsync(ct);
        var userIds = counts.Select(c => c.CreatedBy).Distinct().ToList();
        var names = await db.Memberships.Where(m => userIds.Contains(m.UserId)).ToDictionaryAsync(m => m.UserId, m => m.DisplayName, ct);
        return counts.Select(c => ToDto(c, c.CreatedBy is { } by ? names.GetValueOrDefault(by) : null)).ToList();
    }

    // ---------------------------------------------------------------- recipes

    public async Task<IReadOnlyList<RecipeDto>> RecipesAsync(CancellationToken ct)
    {
        tenant.RequireTenantId();
        var lines = await db.RecipeLines.OrderBy(l => l.CreatedAt).ToListAsync(ct);
        return lines.GroupBy(l => (l.MenuItemId, l.VariantId, l.ModifierOptionId))
            .Select(g => new RecipeDto(g.Key.MenuItemId, g.Key.VariantId, g.Key.ModifierOptionId,
                g.Select(l => new RecipeLineDto(l.InventoryItemId, l.EnteredQuantity, l.EnteredUnit)).ToList()))
            .ToList();
    }

    /// <summary>Replaces one recipe (no lines = remove it).</summary>
    public async Task<RecipeDto> SaveRecipeAsync(RecipeDto request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        if ((request.MenuItemId is null) == (request.ModifierOptionId is null) || (request.VariantId is not null && request.MenuItemId is null))
        {
            throw new ValidationAppException("Recipe", "A recipe belongs to a menu item (optionally one size) or to an add-on.");
        }
        if (request.MenuItemId is { } menuItemId)
        {
            var item = await db.MenuItems.Include(i => i.Variants).FirstOrDefaultAsync(i => i.Id == menuItemId, ct) ?? throw new NotFoundException("Menu item");
            if (request.VariantId is { } variantId && item.Variants.All(v => v.Id != variantId)) throw new NotFoundException("Size");
        }
        else if (!await db.ModifierOptions.AnyAsync(o => o.Id == request.ModifierOptionId, ct))
        {
            throw new NotFoundException("Add-on");
        }

        var lines = request.Lines ?? [];
        if (lines.Any(l => l.Quantity <= 0)) throw new ValidationAppException("Lines", "Quantities must be above zero.");
        if (lines.Select(l => l.InventoryItemId).Distinct().Count() != lines.Count) throw new ValidationAppException("Lines", "List each stock item once.");
        var items = await LoadItemsAsync(lines.Select(l => l.InventoryItemId), ct);

        var existing = await db.RecipeLines
            .Where(l => l.MenuItemId == request.MenuItemId && l.VariantId == request.VariantId && l.ModifierOptionId == request.ModifierOptionId)
            .ToListAsync(ct);
        db.RecipeLines.RemoveRange(existing);
        foreach (var line in lines)
        {
            db.RecipeLines.Add(new RecipeLine
            {
                TenantId = tenantId,
                MenuItemId = request.MenuItemId,
                VariantId = request.VariantId,
                ModifierOptionId = request.ModifierOptionId,
                InventoryItemId = line.InventoryItemId,
                Quantity = ToBase(items[line.InventoryItemId], line.Quantity, line.Unit),
                EnteredQuantity = line.Quantity,
                EnteredUnit = line.Unit
            });
        }
        audit.Record(AuditActions.RecipeSaved, nameof(RecipeLine), request.MenuItemId ?? request.ModifierOptionId, after: request);
        await db.SaveChangesAsync(ct);
        return request with { Lines = lines };
    }

    // ---------------------------------------------------------------- sales (called by OrderService, saved with the order)

    /// <summary>
    /// Books what these lines use, when they go to the kitchen (T9). Keyed "sale:{line}:{item}", so a line is
    /// never deducted twice. The branch's InsufficientStock policy decides what happens when there isn't
    /// enough: Allow books it (stock goes negative), Warn books it and returns warnings, Block refuses.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeductAsync(Order order, IReadOnlyList<OrderLine> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return [];
        var usage = await UsageAsync(lines, ct);
        if (usage.Count == 0) return [];

        var keys = usage.Select(u => SaleKey(u.LineId, u.ItemId)).ToList();
        var booked = (await db.StockMovements.Where(m => keys.Contains(m.IdempotencyKey)).Select(m => m.IdempotencyKey).ToListAsync(ct)).ToHashSet();
        usage = usage.Where(u => !booked.Contains(SaleKey(u.LineId, u.ItemId))).ToList();
        if (usage.Count == 0) return [];

        var itemIds = usage.Select(u => u.ItemId).Distinct().ToList();
        var items = await db.InventoryItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var costs = await db.InventoryBalances.Where(b => b.BranchId == order.BranchId && itemIds.Contains(b.InventoryItemId))
            .ToDictionaryAsync(b => b.InventoryItemId, b => b.AverageCost, ct);

        var warnings = new List<string>();
        var policy = await db.Branches.Where(b => b.Id == order.BranchId).Select(b => b.InsufficientStock).FirstAsync(ct);
        if (policy != InsufficientStockBehavior.Allow)
        {
            var onHand = await OnHandAsync(order.BranchId, itemIds, ct);
            foreach (var need in usage.GroupBy(u => u.ItemId).Select(g => (Item: items[g.Key], Quantity: g.Sum(u => u.Quantity))))
            {
                var have = onHand.GetValueOrDefault(need.Item.Id);
                if (have >= need.Quantity) continue;
                var message = $"Not enough {need.Item.Name}: needs {UnitCatalog.Describe(need.Quantity, need.Item.BaseUnit)}, " +
                    $"{UnitCatalog.Describe(Math.Max(have, 0), need.Item.BaseUnit)} in stock.";
                if (policy == InsufficientStockBehavior.Block) throw new ConflictException(message);
                warnings.Add(message);
            }
        }

        foreach (var use in usage)
        {
            db.StockMovements.Add(Movement(order.TenantId, order.BranchId, use.ItemId, StockMovementType.SaleConsumption, -use.Quantity,
                costs.GetValueOrDefault(use.ItemId), nameof(OrderLine), use.LineId, SaleKey(use.LineId, use.ItemId), $"Order {order.OrderNumber}"));
        }
        return warnings;
    }

    /// <summary>Puts back what voided lines had used (T10). Keyed "rev:{line}:{item}": a line is reversed at most once.</summary>
    public async Task ReverseAsync(Order order, IReadOnlyList<OrderLine> lines, CancellationToken ct)
    {
        var lineIds = lines.Select(l => l.Id).ToList();
        if (lineIds.Count == 0) return;

        var sales = await db.StockMovements
            .Where(m => m.SourceId != null && lineIds.Contains(m.SourceId.Value) && m.Type == StockMovementType.SaleConsumption)
            .ToListAsync(ct);
        var keys = sales.Select(s => ReversalKey(s.SourceId!.Value, s.InventoryItemId)).ToList();
        var reversed = (await db.StockMovements.Where(m => keys.Contains(m.IdempotencyKey)).Select(m => m.IdempotencyKey).ToListAsync(ct)).ToHashSet();

        foreach (var sale in sales.Where(s => !reversed.Contains(ReversalKey(s.SourceId!.Value, s.InventoryItemId))))
        {
            db.StockMovements.Add(Movement(order.TenantId, order.BranchId, sale.InventoryItemId, StockMovementType.SaleReversal, -sale.Quantity, sale.UnitCost,
                nameof(OrderLine), sale.SourceId, ReversalKey(sale.SourceId!.Value, sale.InventoryItemId), $"Order {order.OrderNumber} void"));
        }
    }

    /// <summary>
    /// What the lines use, in base units: the size's recipe if it has one, otherwise the item's; plus each
    /// add-on's recipe — all times the line quantity.
    /// </summary>
    private async Task<List<(Guid LineId, Guid ItemId, decimal Quantity)>> UsageAsync(IReadOnlyList<OrderLine> lines, CancellationToken ct)
    {
        var menuItemIds = lines.Select(l => l.MenuItemId).Distinct().ToList();
        var optionIds = lines.SelectMany(l => l.Modifiers.Select(m => m.ModifierOptionId)).Distinct().ToList();
        var recipes = await db.RecipeLines
            .Where(r => (r.MenuItemId != null && menuItemIds.Contains(r.MenuItemId.Value)) || (r.ModifierOptionId != null && optionIds.Contains(r.ModifierOptionId.Value)))
            .ToListAsync(ct);
        if (recipes.Count == 0) return [];

        var usage = new List<(Guid LineId, Guid ItemId, decimal Quantity)>();
        foreach (var line in lines)
        {
            var forSize = recipes.Where(r => r.MenuItemId == line.MenuItemId && r.VariantId != null && r.VariantId == line.VariantId).ToList();
            var recipe = forSize.Count > 0 ? forSize : recipes.Where(r => r.MenuItemId == line.MenuItemId && r.VariantId == null).ToList();
            recipe.AddRange(line.Modifiers.SelectMany(m => recipes.Where(r => r.ModifierOptionId == m.ModifierOptionId)));
            usage.AddRange(recipe.GroupBy(r => r.InventoryItemId).Select(g => (line.Id, g.Key, g.Sum(r => r.Quantity) * line.Quantity)));
        }
        return usage;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>On hand per item at a branch: the sum of its ledger.</summary>
    private async Task<Dictionary<Guid, decimal>> OnHandAsync(Guid branchId, IEnumerable<Guid>? itemIds, CancellationToken ct)
    {
        var query = db.StockMovements.Where(m => m.BranchId == branchId);
        if (itemIds is not null)
        {
            var ids = itemIds.ToList();
            query = query.Where(m => ids.Contains(m.InventoryItemId));
        }
        return await query.GroupBy(m => m.InventoryItemId).Select(g => new { g.Key, Quantity = g.Sum(m => m.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Quantity, ct);
    }

    private async Task<Dictionary<Guid, InventoryBalance>> BalancesAsync(Guid branchId, IEnumerable<Guid> itemIds, CancellationToken ct)
    {
        var ids = itemIds.ToList();
        return await db.InventoryBalances.Where(b => b.BranchId == branchId && ids.Contains(b.InventoryItemId)).ToDictionaryAsync(b => b.InventoryItemId, ct);
    }

    private InventoryBalance Balance(Dictionary<Guid, InventoryBalance> balances, Guid tenantId, Guid branchId, Guid itemId)
    {
        if (!balances.TryGetValue(itemId, out var balance))
        {
            balance = new InventoryBalance { TenantId = tenantId, BranchId = branchId, InventoryItemId = itemId };
            balances[itemId] = balance;
            db.InventoryBalances.Add(balance);
        }
        return balance;
    }

    internal async Task<Dictionary<Guid, InventoryItem>> LoadItemsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        var items = await db.InventoryItems.Include(i => i.Units).Where(i => wanted.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        if (items.Count != wanted.Count) throw new NotFoundException("Stock item");
        return items;
    }

    internal static decimal ToBase(InventoryItem item, decimal quantity, string unit) =>
        UnitCatalog.ToBase(item.BaseUnit, item.Units.Select(u => new InventoryItemUnitDto(u.Name, u.FactorToBase)), unit?.Trim() ?? "", quantity)
        ?? throw new ValidationAppException("Unit", $"{unit} can't be used for {item.Name} (it is kept in {item.BaseUnit}).");

    private StockMovement Movement(Guid tenantId, Guid branchId, Guid itemId, StockMovementType type, decimal quantity, decimal unitCost,
        string? sourceType, Guid? sourceId, string key, string? reason) => new()
    {
        TenantId = tenantId,
        BranchId = branchId,
        InventoryItemId = itemId,
        Type = type,
        Quantity = Math.Round(quantity, 3),
        UnitCost = unitCost,
        SourceType = sourceType,
        SourceId = sourceId,
        IdempotencyKey = key,
        Reason = reason,
        CreatedAt = clock.GetUtcNow(),
        CreatedBy = tenant.UserId
    };

    private async Task<StockMovementDto> MovementDtoAsync(StockMovement m, CancellationToken ct)
    {
        var item = await db.InventoryItems.FirstAsync(i => i.Id == m.InventoryItemId, ct);
        var by = m.CreatedBy is { } userId ? await db.Memberships.Where(x => x.UserId == userId).Select(x => x.DisplayName).FirstOrDefaultAsync(ct) : null;
        return new StockMovementDto(m.Id, item.Id, item.Name, item.BaseUnit, m.Type, m.Quantity, m.UnitCost, Reference(m), m.Reason, m.CreatedAt, by);
    }

    private static string? Reference(StockMovement m) => m.Type is StockMovementType.SaleConsumption or StockMovementType.SaleReversal ? m.Reason : m.SourceType;

    private static string SaleKey(Guid lineId, Guid itemId) => $"sale:{lineId}:{itemId}";
    private static string ReversalKey(Guid lineId, Guid itemId) => $"rev:{lineId}:{itemId}";

    private static InventoryItemDto ToDto(InventoryItem i) => new(i.Id, i.Name, i.NameUr, i.BaseUnit, i.Category, i.ReorderLevel, i.IsActive,
        i.Units.OrderBy(u => u.FactorToBase).Select(u => new InventoryItemUnitDto(u.Name, u.FactorToBase)).ToList());

    private static StockReceiptDto ToDto(StockReceipt r, string? byName) => new(r.Id, r.Supplier, r.InvoiceNumber, r.TotalCost, r.LineCount, r.CreatedAt, byName,
        r.PurchaseOrderId);

    private static StockCountDto ToDto(StockCount c, string? byName) => new(c.Id, c.CreatedAt, byName, c.Notes, c.VarianceValue,
        JsonSerializer.Deserialize<List<StockCountLineDto>>(c.LinesJson, OrderPricing.Json) ?? []);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
