using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Application.Features.Approvals;
using Restaurant.Application.Features.Orders;
using Restaurant.Application.Features.Sessions;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;
using Restaurant.Domain.Entities;
using Restaurant.Domain.Orders;

namespace Restaurant.Application.Features.Payments;

/// <summary>
/// Payments and refunds (PLAN.md section 8). Split tender is several payments. Cash records the amount
/// tendered and the change, and moves the drawer's expected cash. Card and wallets are recorded only.
/// Nothing is ever deleted: a refund is a new record.
/// </summary>
public class PaymentService(
    IApplicationDbContext db,
    ITenantContext tenant,
    OrderService orders,
    SessionService sessions,
    ApprovalService approvals,
    OrderProgress progress,
    AuditLogger audit,
    IRealtimeNotifier notifier)
{
    public async Task<OrderDto> TakeAsync(Guid orderId, TakePaymentRequest request, CancellationToken ct)
    {
        if (await db.Payments.AnyAsync(p => p.ClientRequestId == request.ClientRequestId, ct))
        {
            return await orders.GetAsync(orderId, ct);
        }

        var order = await orders.LoadOrderAsync(orderId, ct);
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Voided) throw new ConflictException("This order was cancelled.");
        if (!await db.BusinessSessions.AnyAsync(s => s.Id == order.BusinessSessionId && s.Status == SessionStatus.Open, ct))
        {
            throw new ConflictException("This order's business day is closed.");
        }

        var method = await db.PaymentMethods.FirstOrDefaultAsync(m => m.Id == request.PaymentMethodId && m.IsActive, ct)
            ?? throw new NotFoundException("Payment method");
        if (method.RequiresReference && string.IsNullOrWhiteSpace(request.Reference))
        {
            throw new ValidationAppException(nameof(request.Reference), $"Enter the {method.Name} reference.");
        }
        if (request.Amount <= 0) throw new ValidationAppException(nameof(request.Amount), "Enter an amount above zero.");

        // The first payment decides which cash-only / non-cash-only tax rates apply.
        if (order.PaidTotal == 0)
        {
            var tender = method.IsCash ? TaxTender.Cash : TaxTender.NonCash;
            if (order.TaxTender != tender)
            {
                order.TaxTender = tender;
                OrderPricing.Reprice(order);
            }
        }

        var balance = order.Total - order.PaidTotal;
        if (balance <= 0) throw new ConflictException("This order is already paid.");
        var amount = Math.Round(request.Amount, 2);
        if (amount > balance) throw new ValidationAppException(nameof(request.Amount), $"The bill is {balance:0.##}. Enter that amount or less.");

        decimal? tendered = null, change = null;
        if (method.IsCash)
        {
            tendered = Math.Round(request.Tendered ?? amount, 2);
            if (tendered < amount) throw new ValidationAppException(nameof(request.Tendered), "The cash handed over is less than the amount.");
            change = tendered - amount;
        }

        var registerSession = await sessions.OpenRegisterSessionAsync(order.BranchId, request.RegisterId, ct);
        if (registerSession.BusinessSessionId != order.BusinessSessionId) throw new ConflictException("This order belongs to another business day.");

        var payment = new Payment
        {
            TenantId = order.TenantId,
            BranchId = order.BranchId,
            OrderId = order.Id,
            RegisterSessionId = registerSession.Id,
            PaymentMethodId = method.Id,
            MethodName = method.Name,
            IsCash = method.IsCash,
            Amount = amount,
            Tendered = tendered,
            Change = change,
            Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim(),
            ClientRequestId = request.ClientRequestId,
            TakenByUserId = tenant.RequireUserId(),
            TakenByName = await db.CallerNameAsync(tenant, ct)
        };
        db.Payments.Add(payment);
        if (method.IsCash)
        {
            db.CashMovements.Add(new CashMovement
            {
                TenantId = order.TenantId,
                BranchId = order.BranchId,
                RegisterSessionId = registerSession.Id,
                Type = CashMovementType.CashSale,
                Amount = amount,
                PaymentId = payment.Id
            });
        }

        order.PaidTotal += amount;
        order.PaymentStatus = OrderRules.PaymentStatusFor(order.Total, order.PaidTotal, order.RefundedTotal);
        await progress.UpdateAsync(order, [], ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return await orders.GetAsync(orderId, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Someone else changed this order at the same moment. Reload it and try again.");
        }

        await notifier.OrdersChangedAsync(order.BranchId);
        return await orders.ToDtoAsync(order, ct);
    }

    /// <summary>Refunds (part of) a payment: payments.refund or a manager's approval, and a reason.</summary>
    public async Task<OrderDto> RefundAsync(Guid paymentId, RefundRequest request, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct) ?? throw new NotFoundException("Payment");
        tenant.EnsureBranch(payment.BranchId);

        if (await db.Refunds.AnyAsync(r => r.ClientRequestId == request.ClientRequestId, ct))
        {
            return await orders.GetAsync(payment.OrderId, ct);
        }
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ValidationAppException(nameof(request.Reason), "Give a reason for the refund.");
        var amount = Math.Round(request.Amount, 2);
        if (amount <= 0 || amount > payment.Amount - payment.RefundedAmount)
        {
            throw new ValidationAppException(nameof(request.Amount), $"You can refund up to {payment.Amount - payment.RefundedAmount:0.##} of this payment.");
        }

        var approver = await approvals.RequireAsync(Permissions.Payments.Refund, request.Approval, payment.BranchId, ct);
        var order = await orders.LoadOrderAsync(payment.OrderId, ct);
        // The money leaves today's drawer, whichever day the sale was.
        var registerSession = await sessions.OpenRegisterSessionAsync(payment.BranchId, null, ct);

        var refund = new Refund
        {
            TenantId = payment.TenantId,
            BranchId = payment.BranchId,
            PaymentId = payment.Id,
            OrderId = order.Id,
            RegisterSessionId = registerSession.Id,
            Amount = amount,
            IsCash = payment.IsCash,
            Reason = request.Reason.Trim(),
            ApprovedByUserId = approver,
            ByName = await db.CallerNameAsync(tenant, ct),
            ClientRequestId = request.ClientRequestId
        };
        db.Refunds.Add(refund);
        if (payment.IsCash)
        {
            db.CashMovements.Add(new CashMovement
            {
                TenantId = payment.TenantId,
                BranchId = payment.BranchId,
                RegisterSessionId = registerSession.Id,
                Type = CashMovementType.CashRefund,
                Amount = amount,
                RefundId = refund.Id,
                Reason = refund.Reason
            });
        }

        payment.RefundedAmount += amount;
        order.RefundedTotal += amount;
        order.PaymentStatus = OrderRules.PaymentStatusFor(order.Total, order.PaidTotal, order.RefundedTotal);
        audit.Record(AuditActions.PaymentRefunded, nameof(Payment), payment.Id, after: new { amount, payment.MethodName, order.OrderNumber },
            reason: refund.Reason, approverUserId: approver, branchId: payment.BranchId);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return await orders.GetAsync(payment.OrderId, ct);
        }

        await notifier.OrdersChangedAsync(payment.BranchId);
        return await orders.ToDtoAsync(order, ct);
    }
}
