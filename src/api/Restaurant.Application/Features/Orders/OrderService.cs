using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Application.Features.Approvals;
using Restaurant.Application.Features.Customers;
using Restaurant.Application.Features.Deals;
using Restaurant.Application.Features.Inventory;
using Restaurant.Application.Features.Sessions;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Deals;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;
using Restaurant.Domain.Entities;
using Restaurant.Domain.Pricing;

namespace Restaurant.Application.Features.Orders;

/// <summary>
/// Orders from the POS: priced on the server from ids and quantities (never client totals), numbered per
/// branch and business day, idempotent by ClientRequestId, with kitchen tickets written in the same
/// transaction (PLAN.md section 8).
/// </summary>
public class OrderService(
    IApplicationDbContext db,
    ITenantContext tenant,
    SessionService sessions,
    ApprovalService approvals,
    OrderProgress progress,
    StockService stock,
    DealService deals,
    CustomerService customers,
    AuditLogger audit,
    IRealtimeNotifier notifier,
    TimeProvider clock)
{
    private sealed record ResolvedLine(MenuItem Item, MenuItemVariant? Variant, decimal UnitPrice, List<(ModifierGroup Group, ModifierOption Option)> Modifiers, int Quantity, string? Notes)
    {
        public decimal ModifiersTotal => Modifiers.Sum(m => m.Option.PriceDelta);
    }

    public async Task<PriceBreakdownDto> PreviewAsync(PricePreviewRequest request, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var hasLines = request.Lines is { Count: > 0 };
        var resolved = hasLines ? await ResolveLinesAsync(branchId, request.Lines!, ct) : [];

        // Priced on an order that is never saved: the same code path as a real order.
        Order order;
        if (request.ExistingOrderId is { } orderId)
        {
            order = await LoadOrderAsync(orderId, ct);
        }
        else
        {
            if (!hasLines && request.Deals is not { Count: > 0 }) throw new ValidationAppException("Lines", "Add at least one item.");
            ValidateDiscount(request.Discount);
            order = await NewOrderAsync(branchId, request.Type, request.CouponCode, ct);
            order.DiscountPercent = request.Discount?.Percent;
            order.DiscountAmount = request.Discount?.Amount;
        }

        var extraLines = BuildLines(order, resolved, null, null);
        var bundles = await BuildBundlesAsync(order, request.Deals, null, ct);
        var (result, autoDeals) = OrderPricing.Calculate(order, extraLines.Concat(bundles.SelectMany(b => b.Lines)).ToList(), bundles.Select(b => b.Application).ToList());

        var applied = (request.ExistingOrderId is null ? [] : order.DealApplications.Where(a => !a.IsVoided))
            .Concat(bundles.Select(b => b.Application))
            .Select(a => new AppliedDealDto(a.DealId, null, a.Name, DealType.Bundle, a.Quantity, a.OriginalTotal, a.Discount))
            .Concat(autoDeals.Select(d => new AppliedDealDto(d.DealId, null, d.Name, d.Type, 1, 0, d.Discount)))
            .ToList();

        return new PriceBreakdownDto(result.Subtotal, result.Discount, result.ServiceCharge,
            order.ServiceChargeApplies ? order.ServiceChargePercent : 0,
            result.Taxes.Select(t => new TaxLineDto(t.Name, t.Percent, t.Inclusive, t.Amount)).ToList(), result.TaxTotal, result.Rounding, result.Total,
            resolved.Select(l => new PricedLineDto(l.Item.Id, l.Variant?.Id, l.Item.Name, l.Variant?.Name, l.Quantity, l.UnitPrice, l.ModifiersTotal,
                (l.UnitPrice + l.ModifiersTotal) * l.Quantity, l.Modifiers.Select(m => m.Option.Name).ToList())).ToList(),
            result.DealDiscount, applied);
    }

    private bool _alreadyServed;

    /// <summary>Creates an order that was taken offline: like CreateAsync, but a stock shortfall never refuses it.</summary>
    public async Task<OrderDto> CreateServedAsync(CreateOrderRequest request, CancellationToken ct)
    {
        _alreadyServed = true;
        try
        {
            return await CreateAsync(request with { SendToKitchen = true }, ct);
        }
        finally
        {
            _alreadyServed = false;
        }
    }

    public async Task<OrderDto> CreateAsync(CreateOrderRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();

        if (await FindByClientRequestAsync(request.ClientRequestId, ct) is { } already)
        {
            return await ToDtoAsync(already, ct);
        }

        var session = await sessions.OpenSessionQuery(branchId).FirstOrDefaultAsync(ct)
            ?? throw new ConflictException("Start the day first: there is no open business day at this branch.");

        if (!Enum.IsDefined(request.Type)) throw new ValidationAppException(nameof(request.Type), "Choose dine-in, takeaway or delivery.");
        if (request.Covers is < 1 or > 100) throw new ValidationAppException(nameof(request.Covers), "Guests must be between 1 and 100.");
        if (request.CustomerName?.Length > 100 || request.CustomerPhone?.Length > 30 || request.Notes?.Length > 300)
        {
            throw new ValidationAppException(nameof(request.Notes), "The customer details or note are too long.");
        }

        RestaurantTable? table = null;
        if (request.TableId is { } tableId)
        {
            if (request.Type != OrderType.DineIn) throw new ValidationAppException(nameof(request.TableId), "Only dine-in orders have a table.");
            table = await db.Tables.FirstOrDefaultAsync(t => t.Id == tableId && t.BranchId == branchId && t.IsActive, ct) ?? throw new NotFoundException("Table");
            var occupied = await db.Orders.Where(o => o.TableId == tableId
                    && o.Status != OrderStatus.Completed && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Voided)
                .Select(o => o.OrderNumber).FirstOrDefaultAsync(ct);
            if (occupied is not null)
            {
                throw new ConflictException($"Table {table.Number} already has an open order ({occupied}). Add items to that order instead.");
            }
        }

        var resolved = request.Lines is { Count: > 0 } || request.Deals is not { Count: > 0 } ? await ResolveLinesAsync(branchId, request.Lines ?? [], ct) : [];
        Guid? discountApprover = null;
        if (request.Discount is not null)
        {
            ValidateDiscount(request.Discount);
            discountApprover = await approvals.RequireAsync(Permissions.Deals.ManualDiscount, request.Approval, branchId, ct);
        }

        var now = clock.GetUtcNow();
        var branch = await db.Branches.FirstAsync(b => b.Id == branchId, ct);
        var order = await NewOrderAsync(branchId, request.Type, request.CouponCode, ct);
        order.TenantId = tenantId;
        order.BusinessSessionId = session.Id;
        order.TableId = table?.Id;
        order.TableLabel = table?.Number;
        order.Covers = request.Covers;
        order.CustomerName = Blank(request.CustomerName);
        order.CustomerPhone = Blank(request.CustomerPhone);
        order.Notes = Blank(request.Notes);
        order.CreatedByUserId = tenant.RequireUserId();
        order.CreatedByName = await db.CallerNameAsync(tenant, ct);
        order.ClientRequestId = request.ClientRequestId;
        order.DiscountPercent = request.Discount?.Percent;
        order.DiscountAmount = request.Discount?.Amount;
        order.DiscountReason = Blank(request.Discount?.Reason);

        if (await customers.ForOrderAsync(request.CustomerId, order.CustomerName, order.CustomerPhone, ct) is { } customer)
        {
            order.CustomerId = customer.Id;
            order.CustomerName ??= customer.Name;
            order.CustomerPhone ??= customer.Phone;
        }

        Track(order, BuildLines(order, resolved, null, null));
        foreach (var bundle in await BuildBundlesAsync(order, request.Deals, null, ct)) Track(order, bundle);
        OrderPricing.Reprice(order);
        if (order.CouponCode is not null && !OrderPricing.AppliedDeals(order).Any(d => d.Type != DealType.Bundle && DealRuleHasCoupon(order, d.DealId)))
        {
            throw new ValidationAppException(nameof(request.CouponCode), "This coupon doesn't apply to this order.");
        }

        var (tickets, warnings) = request.SendToKitchen ? await SendLinesAsync(order, round: 1, now, ct) : ([], []);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            order.Sequence = await db.NextOrderSequenceAsync(branchId, session.BusinessDate, ct);
            order.OrderNumber = string.IsNullOrEmpty(branch.OrderNumberPrefix) ? order.Sequence.ToString("000") : $"{branch.OrderNumberPrefix}-{order.Sequence:000}";
            foreach (var ticket in tickets) ticket.OrderNumber = order.OrderNumber;
            foreach (var movement in db.ChangeTracker.Entries<StockMovement>().Where(e => e.State == EntityState.Added && e.Entity.Reason == "Order "))
            {
                movement.Entity.Reason = $"Order {order.OrderNumber}";
            }

            db.Orders.Add(order);
            await progress.UpdateAsync(order, tickets, ct);
            if (request.Discount is not null)
            {
                audit.Record(AuditActions.OrderDiscount, nameof(Order), order.Id, after: request.Discount, reason: order.DiscountReason, approverUserId: discountApprover);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            // The same request arrived twice at once (double tap, retry): return the one that won.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var winner = await FindByClientRequestAsync(request.ClientRequestId, ct);
            if (winner is null) throw;
            return await ToDtoAsync(winner, ct);
        }

        await NotifyAsync(branchId, tickets.Count > 0);
        return await ToDtoAsync(order, ct, warnings);
    }

    public async Task<OrderDto> AddLinesAsync(Guid orderId, AddOrderLinesRequest request, CancellationToken ct)
    {
        var order = await LoadOpenOrderAsync(orderId, ct);
        if (order.Lines.Any(l => l.ClientRequestId == request.ClientRequestId))
        {
            return await ToDtoAsync(order, ct);
        }

        var resolved = request.Lines is { Count: > 0 } || request.Deals is not { Count: > 0 } ? await ResolveLinesAsync(order.BranchId, request.Lines ?? [], ct) : [];
        Track(order, BuildLines(order, resolved, request.ClientRequestId, null));
        foreach (var bundle in await BuildBundlesAsync(order, request.Deals, request.ClientRequestId, ct)) Track(order, bundle);
        OrderPricing.Reprice(order);

        var (tickets, warnings) = request.SendToKitchen ? await SendLinesAsync(order, await NextRoundAsync(order.Id, ct), clock.GetUtcNow(), ct) : ([], []);
        await progress.UpdateAsync(order, tickets, ct);
        await SaveAsync(ct);
        await NotifyAsync(order.BranchId, tickets.Count > 0);
        return await ToDtoAsync(order, ct, warnings);
    }

    public async Task<OrderDto> SendToKitchenAsync(Guid orderId, CancellationToken ct)
    {
        var order = await LoadOpenOrderAsync(orderId, ct);
        var (tickets, warnings) = await SendLinesAsync(order, await NextRoundAsync(order.Id, ct), clock.GetUtcNow(), ct);
        if (tickets.Count == 0) throw new ConflictException("Nothing new to send to the kitchen.");

        await progress.UpdateAsync(order, tickets, ct);
        await SaveAsync(ct);
        await NotifyAsync(order.BranchId, true);
        return await ToDtoAsync(order, ct, warnings);
    }

    /// <summary>
    /// Removes a line. Before the kitchen has it, that's a simple edit; afterwards it is a void that needs
    /// orders.void (or a manager's approval) and a reason, and the kitchen gets a change notice.
    /// </summary>
    public async Task<OrderDto> VoidLineAsync(Guid orderId, Guid lineId, ReasonRequest request, CancellationToken ct)
    {
        var order = await LoadOpenOrderAsync(orderId, ct);
        var line = order.Lines.FirstOrDefault(l => l.Id == lineId && l.Status == OrderLineStatus.Active) ?? throw new NotFoundException("Order line");

        // A combo's lines are removed together (the combo price only holds for the whole set).
        var lines = line.DealApplicationId is { } applicationId
            ? order.Lines.Where(l => l.DealApplicationId == applicationId && l.Status == OrderLineStatus.Active).ToList()
            : [line];
        var sent = lines.Where(l => l.SentAt is not null).ToList();

        var tickets = new List<KitchenTicket>();
        Guid? approver = null;
        if (sent.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ValidationAppException(nameof(request.Reason), "Give a reason: the kitchen already has this item.");
            approver = await approvals.RequireAsync(Permissions.Orders.Void, request.Approval, order.BranchId, ct);
            tickets.Add(ChangeTicket(order, sent, await NextRoundAsync(order.Id, ct), clock.GetUtcNow()));
            await stock.ReverseAsync(order, sent, ct);
            audit.Record(AuditActions.OrderLineVoided, nameof(Order), order.Id,
                before: lines.Select(l => new { l.Name, l.VariantName, l.Quantity, l.LineTotal }).ToList(),
                reason: request.Reason, approverUserId: approver, branchId: order.BranchId);
        }

        foreach (var voided in lines)
        {
            voided.Status = OrderLineStatus.Voided;
            voided.VoidReason = Blank(request.Reason);
            voided.VoidedByUserId = tenant.UserId;
        }
        if (order.DealApplications.FirstOrDefault(a => a.Id == line.DealApplicationId) is { } application) application.IsVoided = true;
        OrderPricing.Reprice(order);
        EnsureNotOverpaid(order);

        await progress.UpdateAsync(order, tickets, ct);
        await SaveAsync(ct);
        await NotifyAsync(order.BranchId, tickets.Count > 0);
        return await ToDtoAsync(order, ct);
    }

    /// <summary>Cancels an order the kitchen hasn't seen. Nothing to undo, so no approval is needed.</summary>
    public async Task<OrderDto> CancelAsync(Guid orderId, ReasonRequest request, CancellationToken ct)
    {
        var order = await LoadOpenOrderAsync(orderId, ct);
        if (order.Lines.Any(l => l.SentAt is not null)) throw new ConflictException("This order has gone to the kitchen. Void it instead.");
        if (order.PaidTotal - order.RefundedTotal > 0) throw new ConflictException("Refund the payments first, then cancel the order.");

        order.VoidReason = Blank(request.Reason);
        progress.Set(order, OrderStatus.Cancelled, order.VoidReason);
        audit.Record(AuditActions.OrderCancelled, nameof(Order), order.Id, reason: order.VoidReason, branchId: order.BranchId);
        await SaveAsync(ct);
        await NotifyAsync(order.BranchId, false);
        return await ToDtoAsync(order, ct);
    }

    /// <summary>Voids an order after the kitchen had it: reason plus orders.void or a manager's approval.</summary>
    public async Task<OrderDto> VoidAsync(Guid orderId, ReasonRequest request, CancellationToken ct)
    {
        var order = await LoadOpenOrderAsync(orderId, ct);
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ValidationAppException(nameof(request.Reason), "Give a reason for voiding the order.");
        var approver = await approvals.RequireAsync(Permissions.Orders.Void, request.Approval, order.BranchId, ct);
        if (order.PaidTotal - order.RefundedTotal > 0) throw new ConflictException("Refund the payments first, then void the order.");

        var now = clock.GetUtcNow();
        var openTickets = await db.KitchenTickets
            .Where(t => t.OrderId == order.Id && (t.Status == KitchenTicketStatus.New || t.Status == KitchenTicketStatus.Preparing || t.Status == KitchenTicketStatus.Ready))
            .ToListAsync(ct);
        foreach (var ticket in openTickets.Where(t => !t.IsChange))
        {
            ticket.Status = KitchenTicketStatus.Cancelled;
        }

        var sentLines = order.Lines.Where(l => l.Status == OrderLineStatus.Active && l.SentAt is not null).ToList();
        if (sentLines.Count > 0)
        {
            ChangeTicket(order, sentLines, await NextRoundAsync(order.Id, ct), now);
            await stock.ReverseAsync(order, sentLines, ct);
        }
        foreach (var application in order.DealApplications) application.IsVoided = true;
        foreach (var line in order.Lines.Where(l => l.Status == OrderLineStatus.Active))
        {
            line.Status = OrderLineStatus.Voided;
            line.VoidReason = request.Reason.Trim();
            line.VoidedByUserId = tenant.UserId;
        }

        order.VoidReason = request.Reason.Trim();
        progress.Set(order, OrderStatus.Voided, order.VoidReason, approver);
        audit.Record(AuditActions.OrderVoided, nameof(Order), order.Id, before: new { order.OrderNumber, order.Total }, reason: order.VoidReason,
            approverUserId: approver, branchId: order.BranchId);
        await SaveAsync(ct);
        await NotifyAsync(order.BranchId, true);
        return await ToDtoAsync(order, ct);
    }

    public async Task<OrderDto> SetDiscountAsync(Guid orderId, SetDiscountRequest request, CancellationToken ct)
    {
        var order = await LoadOpenOrderAsync(orderId, ct);
        ValidateDiscount(request.Discount);
        var approver = await approvals.RequireAsync(Permissions.Deals.ManualDiscount, request.Approval, order.BranchId, ct);

        var before = new { order.DiscountPercent, order.DiscountAmount, order.DiscountTotal };
        order.DiscountPercent = request.Discount?.Percent;
        order.DiscountAmount = request.Discount?.Amount;
        order.DiscountReason = Blank(request.Discount?.Reason);
        OrderPricing.Reprice(order);
        EnsureNotOverpaid(order);
        await progress.UpdateAsync(order, [], ct);

        audit.Record(AuditActions.OrderDiscount, nameof(Order), order.Id, before, new { order.DiscountPercent, order.DiscountAmount, order.DiscountTotal },
            reason: order.DiscountReason, approverUserId: approver, branchId: order.BranchId);
        await SaveAsync(ct);
        await NotifyAsync(order.BranchId, false);
        return await ToDtoAsync(order, ct);
    }

    /// <summary>Served at the table / handed over at the counter: closes the kitchen tickets.</summary>
    public async Task<OrderDto> MarkServedAsync(Guid orderId, CancellationToken ct)
    {
        var order = await LoadOpenOrderAsync(orderId, ct);
        if (order.Lines.Any(l => l.Status == OrderLineStatus.Active && l.SentAt is null))
        {
            throw new ConflictException("Some items haven't been sent to the kitchen yet.");
        }

        var now = clock.GetUtcNow();
        var tickets = await db.KitchenTickets
            .Where(t => t.OrderId == order.Id && !t.IsChange && t.Status != KitchenTicketStatus.Served && t.Status != KitchenTicketStatus.Cancelled)
            .ToListAsync(ct);
        foreach (var ticket in tickets)
        {
            ticket.Status = KitchenTicketStatus.Served;
            ticket.ServedAt = now;
        }

        await progress.UpdateAsync(order, tickets, ct);
        if (order.Status == OrderStatus.Confirmed && order.Lines.All(l => l.Status != OrderLineStatus.Active || l.SentAt is not null))
        {
            progress.Set(order, order.PaidTotal >= order.Total ? OrderStatus.Completed : OrderStatus.Served);
        }
        await SaveAsync(ct);
        await NotifyAsync(order.BranchId, tickets.Count > 0);
        return await ToDtoAsync(order, ct);
    }

    /// <param name="scope">"open" (default) or "today" (everything in the current business day).</param>
    public async Task<IReadOnlyList<OrderSummaryDto>> ListAsync(string? scope, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var query = db.Orders.Where(o => o.BranchId == branchId);
        if (scope == "review")
        {
            query = query.Where(o => o.NeedsReview);
        }
        else if (scope == "today")
        {
            var sessionId = await db.BusinessSessions.Where(s => s.BranchId == branchId)
                .OrderByDescending(s => s.StartedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            query = query.Where(o => o.BusinessSessionId == sessionId);
        }
        else
        {
            query = query.Where(o => o.Status != OrderStatus.Completed && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Voided);
        }

        return await query.OrderByDescending(o => o.CreatedAt).Take(300)
            .Select(o => new OrderSummaryDto(o.Id, o.OrderNumber, o.Type, o.Status, o.PaymentStatus, o.TableLabel, o.CustomerName,
                o.Total, o.Total > o.PaidTotal ? o.Total - o.PaidTotal : 0,
                o.Lines.Where(l => l.Status == OrderLineStatus.Active).Sum(l => l.Quantity), o.CreatedAt, o.OfflineNumber, o.NeedsReview))
            .ToListAsync(ct);
    }

    public async Task<OrderDto> GetAsync(Guid orderId, CancellationToken ct) => await ToDtoAsync(await LoadOrderAsync(orderId, ct), ct);

    // ---------------------------------------------------------------- helpers

    public async Task<Order> LoadOrderAsync(Guid orderId, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var order = await db.Orders.Include(o => o.Lines).ThenInclude(l => l.Modifiers).Include(o => o.DealApplications)
            .AsSplitQuery().FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new NotFoundException("Order");
        tenant.EnsureBranch(order.BranchId);
        return order;
    }

    private async Task<Order> LoadOpenOrderAsync(Guid orderId, CancellationToken ct)
    {
        var order = await LoadOrderAsync(orderId, ct);
        if (!order.IsOpen) throw new ConflictException($"Order {order.OrderNumber} is already {order.Status.ToString().ToLowerInvariant()}.");
        if (!await db.BusinessSessions.AnyAsync(s => s.Id == order.BusinessSessionId && s.Status == SessionStatus.Open, ct))
        {
            throw new ConflictException("This order's business day is closed.");
        }
        return order;
    }

    private async Task<Order?> FindByClientRequestAsync(Guid clientRequestId, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Lines).ThenInclude(l => l.Modifiers).Include(o => o.DealApplications)
            .AsSplitQuery().FirstOrDefaultAsync(o => o.ClientRequestId == clientRequestId, ct);
        if (order is not null) tenant.EnsureBranch(order.BranchId);
        return order;
    }

    private async Task<IReadOnlyList<ResolvedLine>> ResolveLinesAsync(Guid branchId, IReadOnlyList<OrderLineRequest> lines, CancellationToken ct)
    {
        if (lines is null || lines.Count == 0) throw new ValidationAppException("Lines", "Add at least one item.");
        if (lines.Count > 100) throw new ValidationAppException("Lines", "At most 100 lines per request.");
        if (lines.Any(l => l.Quantity is < 1 or > 99)) throw new ValidationAppException("Lines", "Quantities must be between 1 and 99.");
        if (lines.Any(l => l.Notes?.Length > 200)) throw new ValidationAppException("Lines", "Item notes can be at most 200 characters.");

        var itemIds = lines.Select(l => l.MenuItemId).Distinct().ToList();
        var items = await db.MenuItems.Include(i => i.Variants).Include(i => i.ModifierGroups)
            .Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var branchRows = await db.BranchMenuItems.Where(b => b.BranchId == branchId && itemIds.Contains(b.MenuItemId)).ToDictionaryAsync(b => b.MenuItemId, ct);
        var groupIds = items.Values.SelectMany(i => i.ModifierGroups.Select(g => g.ModifierGroupId)).Distinct().ToList();
        var groups = await db.ModifierGroups.Include(g => g.Options).Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        var activeCategories = (await db.MenuCategories.Where(c => c.IsActive).Select(c => c.Id).ToListAsync(ct)).ToHashSet();

        var result = new List<ResolvedLine>();
        foreach (var line in lines)
        {
            var item = items.GetValueOrDefault(line.MenuItemId) ?? throw new NotFoundException("Menu item");
            if (!item.IsActive || !activeCategories.Contains(item.CategoryId)) throw new ConflictException($"{item.Name} is no longer on the menu.");

            var row = branchRows.GetValueOrDefault(item.Id);
            if (row is not null && row.Availability != MenuAvailability.Available)
            {
                throw new ConflictException($"{item.Name} is {(row.Availability == MenuAvailability.OutOfStock ? "sold out" : "not available")} at this branch.");
            }

            var activeVariants = item.Variants.Where(v => v.IsActive).ToList();
            MenuItemVariant? variant = null;
            if (activeVariants.Count > 0)
            {
                variant = activeVariants.FirstOrDefault(v => v.Id == line.VariantId)
                    ?? throw new ValidationAppException("Lines", $"Choose a size for {item.Name}.");
            }
            else if (line.VariantId is not null)
            {
                throw new NotFoundException("Size");
            }

            var linkedGroups = item.ModifierGroups.Select(l => groups.GetValueOrDefault(l.ModifierGroupId)).Where(g => g is not null).Select(g => g!).ToList();
            var modifiers = new List<(ModifierGroup, ModifierOption)>();
            foreach (var optionId in (line.ModifierOptionIds ?? []).Distinct())
            {
                var match = linkedGroups.Select(g => (Group: g, Option: g.Options.FirstOrDefault(o => o.Id == optionId && o.IsActive)))
                    .FirstOrDefault(x => x.Option is not null);
                if (match.Option is null) throw new NotFoundException("Add-on");
                modifiers.Add((match.Group, match.Option));
            }
            foreach (var group in linkedGroups)
            {
                var chosen = modifiers.Count(m => m.Item1.Id == group.Id);
                if (chosen < group.MinSelect || chosen > group.MaxSelect)
                {
                    throw new ValidationAppException("Lines", group.MinSelect == group.MaxSelect
                        ? $"{item.Name}: choose {group.MinSelect} from {group.Name}."
                        : $"{item.Name}: choose {group.MinSelect} to {group.MaxSelect} from {group.Name}.");
                }
            }

            var unitPrice = variant?.Price ?? row?.PriceOverride ?? item.Price;
            result.Add(new ResolvedLine(item, variant, unitPrice, modifiers, line.Quantity, Blank(line.Notes)));
        }
        return result;
    }

    /// <summary>Order lines for resolved menu choices (not yet on the order: see <see cref="Track"/>).</summary>
    private static List<OrderLine> BuildLines(Order order, IEnumerable<ResolvedLine> resolved, Guid? clientRequestId, Guid? dealApplicationId)
    {
        var lines = new List<OrderLine>();
        foreach (var r in resolved)
        {
            var line = new OrderLine
            {
                TenantId = order.TenantId,
                OrderId = order.Id,
                MenuItemId = r.Item.Id,
                VariantId = r.Variant?.Id,
                Name = r.Item.Name,
                NameUr = r.Item.NameUr,
                VariantName = r.Variant?.Name,
                Sku = r.Variant?.Sku ?? r.Item.Sku,
                UnitPrice = r.UnitPrice,
                Quantity = r.Quantity,
                ModifiersTotal = r.ModifiersTotal,
                LineTotal = (r.UnitPrice + r.ModifiersTotal) * r.Quantity,
                Notes = r.Notes,
                KitchenStationId = r.Item.KitchenStationId,
                ClientRequestId = clientRequestId,
                DealApplicationId = dealApplicationId
            };
            line.Modifiers = r.Modifiers.Select(m => new OrderLineModifier
            {
                TenantId = order.TenantId,
                OrderLineId = line.Id,
                ModifierOptionId = m.Option.Id,
                GroupName = m.Group.Name,
                Name = m.Option.Name,
                PriceDelta = m.Option.PriceDelta
            }).ToList();
            lines.Add(line);
        }
        return lines;
    }

    /// <remarks>Rows are added to the context explicitly: their ids are set in code, so EF would otherwise take them for existing rows.</remarks>
    private void Track(Order order, IReadOnlyList<OrderLine> lines)
    {
        foreach (var line in lines)
        {
            order.Lines.Add(line);
            db.OrderLines.Add(line);
        }
    }

    private void Track(Order order, (OrderDealApplication Application, List<OrderLine> Lines) bundle)
    {
        order.DealApplications.Add(bundle.Application);
        db.OrderDealApplications.Add(bundle.Application);
        Track(order, bundle.Lines);
    }

    /// <summary>A new order with the branch's current tax, service charge, rounding and automatic deals frozen on it.</summary>
    private async Task<Order> NewOrderAsync(Guid branchId, OrderType type, string? couponCode, CancellationToken ct)
    {
        var branch = await db.Branches.FirstAsync(b => b.Id == branchId, ct);
        var rules = OrderPricing.CurrentRules(branch, type, await db.TaxRates.ToListAsync(ct));
        var autoDeals = (await deals.RunningAsync(branchId, ct)).Where(d => d.Type != DealType.Bundle).Select(DealService.ToRule).ToList();
        return new Order
        {
            TenantId = tenant.RequireTenantId(),
            BranchId = branchId,
            Type = type,
            ServiceChargePercent = rules.ServiceChargePercent,
            ServiceChargeApplies = rules.ServiceChargeApplies,
            Rounding = rules.Rounding,
            TaxRatesJson = JsonSerializer.Serialize(rules.Taxes, OrderPricing.Json),
            DealRulesJson = JsonSerializer.Serialize(autoDeals, OrderPricing.Json),
            CouponCode = string.IsNullOrWhiteSpace(couponCode) ? null : couponCode.Trim().ToUpperInvariant()
        };
    }

    private static bool DealRuleHasCoupon(Order order, Guid? dealId) =>
        OrderPricing.DealRules(order).Any(r => r.DealId == dealId && r.CouponCode is not null);

    /// <summary>
    /// Combo deals picked on the POS: one choice per group (in group order), checked against the deal. The
    /// lines carry their normal prices; the application holds the discount (normal price − combo price).
    /// Add-ons stay charged on top.
    /// </summary>
    private async Task<List<(OrderDealApplication Application, List<OrderLine> Lines)>> BuildBundlesAsync(
        Order order, IReadOnlyList<DealSelection>? selections, Guid? clientRequestId, CancellationToken ct)
    {
        var result = new List<(OrderDealApplication, List<OrderLine>)>();
        if (selections is not { Count: > 0 }) return result;
        if (selections.Count > 20) throw new ValidationAppException("Deals", "At most 20 deals per request.");

        var running = (await deals.RunningAsync(order.BranchId, ct)).Where(d => d.Type == DealType.Bundle).ToDictionary(d => d.Id);
        foreach (var selection in selections)
        {
            var deal = running.GetValueOrDefault(selection.DealId) ?? throw new ConflictException("That deal isn't available right now.");
            if (selection.Quantity is < 1 or > 20) throw new ValidationAppException("Deals", "Deal quantities must be between 1 and 20.");

            var groups = deal.Items.GroupBy(i => i.Group).OrderBy(g => g.Key).ToList();
            var components = selection.Components ?? [];
            if (components.Count != groups.Count) throw new ValidationAppException("Deals", $"{deal.Name}: make one choice for each part of the deal.");

            var requests = new List<OrderLineRequest>();
            for (var g = 0; g < groups.Count; g++)
            {
                var choice = components[g];
                var allowed = groups[g].FirstOrDefault(i => i.MenuItemId == choice.MenuItemId && (i.VariantId is null || i.VariantId == choice.VariantId))
                    ?? throw new ValidationAppException("Deals", $"{deal.Name}: that choice isn't part of the deal.");
                requests.Add(choice with { Quantity = allowed.Quantity * selection.Quantity });
            }

            var resolved = await ResolveLinesAsync(order.BranchId, requests, ct);
            var application = new OrderDealApplication
            {
                TenantId = order.TenantId,
                OrderId = order.Id,
                DealId = deal.Id,
                Name = deal.Name,
                Quantity = selection.Quantity
            };
            var lines = BuildLines(order, resolved, clientRequestId, application.Id);
            application.OriginalTotal = lines.Sum(l => l.UnitPrice * l.Quantity);
            application.Discount = Math.Max(0, application.OriginalTotal - (deal.BundlePrice ?? 0) * selection.Quantity);
            result.Add((application, lines));
        }
        return result;
    }

    /// <summary>Sends the unsent lines to the kitchen and books the stock they use, in the same unit of work.</summary>
    private async Task<(List<KitchenTicket> Tickets, IReadOnlyList<string> Warnings)> SendLinesAsync(Order order, int round, DateTimeOffset now, CancellationToken ct)
    {
        var unsent = order.Lines.Where(l => l.Status == OrderLineStatus.Active && l.SentAt is null).ToList();
        var warnings = await stock.DeductAsync(order, unsent, ct, _alreadyServed);
        return (CreateTickets(order, round, now), warnings);
    }

    /// <summary>One ticket per kitchen station for the lines not sent yet (they are marked sent).</summary>
    private List<KitchenTicket> CreateTickets(Order order, int round, DateTimeOffset now)
    {
        var unsent = order.Lines.Where(l => l.Status == OrderLineStatus.Active && l.SentAt is null).ToList();
        var tickets = unsent.GroupBy(l => l.KitchenStationId).Select(group => new KitchenTicket
        {
            TenantId = order.TenantId,
            BranchId = order.BranchId,
            OrderId = order.Id,
            StationId = group.Key,
            Round = round,
            OrderNumber = order.OrderNumber,
            OrderType = order.Type,
            TableLabel = order.TableLabel,
            SentAt = now,
            Items = group.Select(l => TicketItem(order, l, cancelled: false)).ToList()
        }).ToList();

        foreach (var line in unsent) line.SentAt = now;
        if (tickets.Count > 0) order.SentToKitchenAt ??= now;
        db.KitchenTickets.AddRange(tickets);
        return tickets;
    }

    /// <summary>A change notice for the kitchen: these items are cancelled.</summary>
    private KitchenTicket ChangeTicket(Order order, IReadOnlyList<OrderLine> lines, int round, DateTimeOffset now)
    {
        var ticket = new KitchenTicket
        {
            TenantId = order.TenantId,
            BranchId = order.BranchId,
            OrderId = order.Id,
            StationId = lines.Select(l => l.KitchenStationId).Distinct().Count() == 1 ? lines[0].KitchenStationId : null,
            Round = round,
            IsChange = true,
            OrderNumber = order.OrderNumber,
            OrderType = order.Type,
            TableLabel = order.TableLabel,
            SentAt = now,
            Items = lines.Select(l => TicketItem(order, l, cancelled: true)).ToList()
        };
        db.KitchenTickets.Add(ticket);
        return ticket;
    }

    private static KitchenTicketItem TicketItem(Order order, OrderLine line, bool cancelled) => new()
    {
        TenantId = order.TenantId,
        OrderLineId = line.Id,
        Quantity = line.Quantity,
        Name = line.VariantName is null ? line.Name : $"{line.Name} ({line.VariantName})",
        ModifiersText = line.Modifiers.Count == 0 ? null : string.Join(", ", line.Modifiers.Select(m => m.Name)),
        Notes = line.Notes,
        Cancelled = cancelled
    };

    private async Task<int> NextRoundAsync(Guid orderId, CancellationToken ct) =>
        (await db.KitchenTickets.Where(t => t.OrderId == orderId).MaxAsync(t => (int?)t.Round, ct) ?? 0) + 1;

    private static void ValidateDiscount(ManualDiscountRequest? discount)
    {
        if (discount is null) return;
        if ((discount.Percent is null) == (discount.Amount is null)) throw new ValidationAppException("Discount", "Give either a percentage or an amount.");
        if (discount.Percent is < 0 or > 100) throw new ValidationAppException("Discount", "The percentage must be between 0 and 100.");
        if (discount.Amount is < 0) throw new ValidationAppException("Discount", "The amount can't be negative.");
        if (string.IsNullOrWhiteSpace(discount.Reason)) throw new ValidationAppException("Discount", "Give a reason for the discount.");
    }

    private static void EnsureNotOverpaid(Order order)
    {
        if (order.PaidTotal - order.RefundedTotal > order.Total)
        {
            throw new ConflictException("More has been paid than the new total. Refund the difference first.");
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Someone else changed this order at the same moment. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            // The same change (a void's stock reversal, a retried add) was saved by a request running alongside this one.
            throw new ConflictException("Someone else changed this order at the same moment. Reload it and try again.");
        }
    }

    private async Task NotifyAsync(Guid branchId, bool tickets)
    {
        await notifier.OrdersChangedAsync(branchId);
        if (tickets) await notifier.TicketsChangedAsync(branchId);
    }

    public async Task<OrderDto> ToDtoAsync(Order o, CancellationToken ct, IReadOnlyList<string>? stockWarnings = null)
    {
        var payments = await db.Payments.Where(p => p.OrderId == o.Id).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var refunds = await db.Refunds.Where(r => r.OrderId == o.Id).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var sessionOpen = await db.BusinessSessions.AnyAsync(s => s.Id == o.BusinessSessionId && s.Status == SessionStatus.Open, ct);

        return new OrderDto(o.Id, o.BranchId, o.BusinessSessionId, o.OrderNumber, o.Type, o.Status, o.PaymentStatus, o.TableId, o.TableLabel, o.Covers,
            o.CustomerName, o.CustomerPhone, o.Notes, o.Subtotal, o.DiscountTotal, o.DiscountReason, o.ServiceCharge, o.ServiceChargeApplies ? o.ServiceChargePercent : 0,
            OrderPricing.TaxLines(o), o.TaxTotal, o.RoundingAdjustment, o.Total, o.PaidTotal, o.RefundedTotal, o.Balance,
            o.Lines.OrderBy(l => l.CreatedAt).Select(l => new OrderLineDto(l.Id, l.MenuItemId, l.VariantId, l.Name, l.VariantName, l.Quantity, l.UnitPrice,
                l.ModifiersTotal, l.LineTotal, l.Modifiers.Select(m => m.Name).ToList(), l.Notes, l.Status, l.VoidReason, l.SentAt is not null,
                l.DealApplicationId)).ToList(),
            payments.Select(p => new PaymentDto(p.Id, p.MethodName, p.IsCash, p.Amount, p.Tendered, p.Change, p.Reference, p.CreatedAt, p.TakenByName, p.RefundedAmount)).ToList(),
            refunds.Select(r => new RefundDto(r.Id, r.PaymentId, r.Amount, r.Reason, r.CreatedAt, r.IsCash, r.ByName)).ToList(),
            o.CreatedAt, o.CreatedByName, o.VoidReason, sessionOpen,
            o.CustomerId, o.CouponCode, o.DealDiscountTotal, OrderPricing.AppliedDeals(o), stockWarnings is { Count: > 0 } ? stockWarnings : null,
            o.OfflineNumber, o.NeedsReview, o.ReviewNote);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
