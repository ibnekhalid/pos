using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Sessions;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Sessions;

/// <summary>
/// The business day and its cash drawers (PLAN.md section 7, decisions D5 and D7): start with opening
/// cash, record paid-outs and cash-in, close with a cash count, reopen only through an audited action.
/// </summary>
public class SessionService(
    IApplicationDbContext db,
    ITenantContext tenant,
    AuditLogger audit,
    IRealtimeNotifier notifier,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BusinessSessionDto?> GetCurrentAsync(CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var session = await OpenSessionQuery(branchId).FirstOrDefaultAsync(ct);
        return session is null ? null : await ToDtoAsync(session, ct);
    }

    public async Task<BusinessSessionDto> StartAsync(StartSessionRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        if (request.OpeningCash < 0) throw new ValidationAppException(nameof(request.OpeningCash), "Opening cash can't be negative.");

        var branch = await db.Branches.FirstAsync(b => b.Id == branchId, ct);
        if (!branch.IsActive) throw new ConflictException("This branch is closed.");
        if (await OpenSessionQuery(branchId).AnyAsync(ct)) throw new ConflictException("The day has already been started.");

        var register = await db.Registers.FirstOrDefaultAsync(r => r.BranchId == branchId && r.IsDefault, ct)
            ?? throw new ConflictException("This branch has no default register.");

        var now = clock.GetUtcNow();
        var userId = tenant.RequireUserId();
        var session = new BusinessSession
        {
            TenantId = tenantId,
            BranchId = branchId,
            BusinessDate = branch.BusinessDateFor(now, CallerExtensions.TimeZone(branch.TimeZoneId)),
            StartedAt = now,
            StartedByUserId = userId,
            Notes = request.Notes?.Trim()
        };
        var registerSession = OpenRegister(session, register.Id, request.OpeningCash, now, userId);
        db.BusinessSessions.Add(session);

        audit.Record(AuditActions.SessionStarted, nameof(BusinessSession), session.Id, after: new { session.BusinessDate, request.OpeningCash });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            // Two people pressed "Start day" at the same moment: the partial unique index let one through.
            throw new ConflictException("The day has already been started.");
        }

        await notifier.OrdersChangedAsync(branchId);
        return await ToDtoAsync(session, ct);
    }

    /// <summary>Opens another register's drawer in the current day (per-register cash handling).</summary>
    public async Task<BusinessSessionDto> OpenRegisterAsync(OpenRegisterRequest request, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        if (request.OpeningCash < 0) throw new ValidationAppException(nameof(request.OpeningCash), "Opening cash can't be negative.");
        var session = await OpenSessionQuery(branchId).FirstOrDefaultAsync(ct) ?? throw new ConflictException("Start the day first.");
        if (!await db.Registers.AnyAsync(r => r.Id == request.RegisterId && r.BranchId == branchId && r.IsActive, ct)) throw new NotFoundException("Register");
        if (session.Registers.Any(r => r.RegisterId == request.RegisterId && r.Status == SessionStatus.Open))
        {
            throw new ConflictException("This register is already open.");
        }

        OpenRegister(session, request.RegisterId, request.OpeningCash, clock.GetUtcNow(), tenant.RequireUserId());
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(session, ct);
    }

    public async Task<CashMovementDto> RecordCashMovementAsync(RecordCashMovementRequest request, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        if (request.Type is not (CashMovementType.PaidOut or CashMovementType.CashIn))
        {
            throw new ValidationAppException(nameof(request.Type), "Only paid-outs and cash-in can be recorded by hand.");
        }
        if (request.Amount <= 0) throw new ValidationAppException(nameof(request.Amount), "Enter an amount above zero.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ValidationAppException(nameof(request.Reason), "Say what the cash was for.");

        var existing = await db.CashMovements.FirstOrDefaultAsync(m => m.ClientRequestId == request.ClientRequestId, ct);
        if (existing is not null) return await ToDtoAsync(existing, ct);

        var registerSession = await OpenRegisterSessionAsync(branchId, request.RegisterId, ct);
        var movement = new CashMovement
        {
            TenantId = registerSession.TenantId,
            BranchId = branchId,
            RegisterSessionId = registerSession.Id,
            Type = request.Type,
            Amount = Math.Round(request.Amount, 2),
            Reason = request.Reason.Trim(),
            ClientRequestId = request.ClientRequestId
        };
        db.CashMovements.Add(movement);
        audit.Record(AuditActions.CashMovement, nameof(CashMovement), movement.Id, after: new { movement.Type, movement.Amount }, reason: movement.Reason);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(movement, ct);
    }

    public async Task<IReadOnlyList<CashMovementDto>> ListCashMovementsAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await LoadSessionAsync(sessionId, ct);
        var registerIds = session.Registers.Select(r => r.Id).ToList();
        var movements = await db.CashMovements.Where(m => registerIds.Contains(m.RegisterSessionId)).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var names = await NamesAsync(movements.Select(m => m.CreatedBy), ct);
        return movements.Select(m => new CashMovementDto(m.Id, m.RegisterSessionId, m.Type, m.Amount, m.Reason, m.CreatedAt, NameOf(names, m.CreatedBy))).ToList();
    }

    public async Task<ClosePreviewDto> ClosePreviewAsync(CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var session = await OpenSessionQuery(branchId).FirstOrDefaultAsync(ct) ?? throw new ConflictException("The day hasn't been started.");
        var branch = await db.Branches.FirstAsync(b => b.Id == branchId, ct);
        return new ClosePreviewDto(await ToDtoAsync(session, ct), await BlockersAsync(session, ct), await SummaryAsync(session, ct), branch.CashDifferenceThreshold);
    }

    public async Task<ClosedSessionDto> CloseAsync(CloseSessionRequest request, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var session = await OpenSessionQuery(branchId).FirstOrDefaultAsync(ct) ?? throw new ConflictException("The day hasn't been started.");
        var branch = await db.Branches.FirstAsync(b => b.Id == branchId, ct);

        var blockers = await BlockersAsync(session, ct);
        if (blockers.Count > 0)
        {
            throw new ConflictException($"The day can't be closed yet: {blockers.Count} order(s) or ticket(s) still need attention.");
        }

        var now = clock.GetUtcNow();
        var userId = tenant.RequireUserId();
        var expected = await ExpectedCashAsync(session.Registers.Select(r => r.Id).ToList(), ct);

        foreach (var register in session.Registers.Where(r => r.Status == SessionStatus.Open))
        {
            var count = request.Counts.FirstOrDefault(c => c.RegisterSessionId == register.Id)
                ?? throw new ValidationAppException("Counts", "Count the cash in every open register.");
            if (count.ActualCash < 0) throw new ValidationAppException("Counts", "Counted cash can't be negative.");

            var difference = count.ActualCash - expected.GetValueOrDefault(register.Id);
            if (Math.Abs(difference) > branch.CashDifferenceThreshold && string.IsNullOrWhiteSpace(count.DifferenceReason))
            {
                throw new ValidationAppException("DifferenceReason", "The cash is off by more than the allowed amount. Give a reason.");
            }

            register.ExpectedCash = expected.GetValueOrDefault(register.Id);
            register.ActualCash = count.ActualCash;
            register.CashDifference = difference;
            register.DifferenceReason = string.IsNullOrWhiteSpace(count.DifferenceReason) ? null : count.DifferenceReason.Trim();
            register.Status = SessionStatus.Closed;
            register.ClosedAt = now;
            register.ClosedByUserId = userId;
        }

        session.Status = SessionStatus.Closed;
        session.ClosedAt = now;
        session.ClosedByUserId = userId;
        if (!string.IsNullOrWhiteSpace(request.Notes)) session.Notes = request.Notes.Trim();

        var summary = await SummaryAsync(session, ct);
        session.ClosingSummaryJson = JsonSerializer.Serialize(summary, Json);
        audit.Record(AuditActions.SessionClosed, nameof(BusinessSession), session.Id, after: summary);
        await db.SaveChangesAsync(ct);

        await notifier.OrdersChangedAsync(branchId);
        return new ClosedSessionDto(await ToDtoAsync(session, ct), summary);
    }

    /// <summary>Owner-only correction path: the day becomes open again, with a reason in the audit log.</summary>
    public async Task<BusinessSessionDto> ReopenAsync(Guid sessionId, ReopenSessionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ValidationAppException(nameof(request.Reason), "Give a reason for reopening the day.");
        var session = await LoadSessionAsync(sessionId, ct);
        if (session.Status != SessionStatus.Closed) throw new ConflictException("This day is still open.");
        if (await OpenSessionQuery(session.BranchId).AnyAsync(ct)) throw new ConflictException("Close the current day before reopening another one.");

        var before = session.ClosingSummaryJson;
        session.Status = SessionStatus.Open;
        session.ReopenedAt = clock.GetUtcNow();
        session.ReopenedByUserId = tenant.RequireUserId();
        session.ReopenReason = request.Reason.Trim();
        session.ClosedAt = null;
        session.ClosedByUserId = null;
        foreach (var register in session.Registers)
        {
            register.Status = SessionStatus.Open;
            register.ClosedAt = null;
            register.ActualCash = null;
            register.ExpectedCash = null;
            register.CashDifference = null;
            register.DifferenceReason = null;
        }

        audit.Record(AuditActions.SessionReopened, nameof(BusinessSession), session.Id, before: before, reason: session.ReopenReason, branchId: session.BranchId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            throw new ConflictException("Close the current day before reopening another one.");
        }
        await notifier.OrdersChangedAsync(session.BranchId);
        return await ToDtoAsync(session, ct);
    }

    public async Task<IReadOnlyList<SessionHistoryItemDto>> HistoryAsync(CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var sessions = await db.BusinessSessions.Include(s => s.Registers)
            .Where(s => s.BranchId == branchId).OrderByDescending(s => s.StartedAt).Take(60).ToListAsync(ct);
        var ids = sessions.Select(s => s.Id).ToList();
        var totals = await db.Orders
            .Where(o => ids.Contains(o.BusinessSessionId) && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Voided)
            .GroupBy(o => o.BusinessSessionId).Select(g => new { g.Key, Total = g.Sum(o => o.Total) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        return sessions.Select(s => new SessionHistoryItemDto(s.Id, s.BusinessDate, s.Status, s.StartedAt, s.ClosedAt,
            totals.GetValueOrDefault(s.Id), s.Status == SessionStatus.Closed ? s.Registers.Sum(r => r.CashDifference ?? 0) : null)).ToList();
    }

    public async Task<ClosedSessionDto> GetReportAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await LoadSessionAsync(sessionId, ct);
        return new ClosedSessionDto(await ToDtoAsync(session, ct), await SummaryAsync(session, ct));
    }

    // ---------------------------------------------------------------- shared

    /// <summary>The open register session to book cash against: the given register, or the branch's default.</summary>
    public async Task<RegisterSession> OpenRegisterSessionAsync(Guid branchId, Guid? registerId, CancellationToken ct)
    {
        var session = await OpenSessionQuery(branchId).FirstOrDefaultAsync(ct) ?? throw new ConflictException("Start the day first.");
        var id = registerId ?? await db.Registers.Where(r => r.BranchId == branchId && r.IsDefault).Select(r => r.Id).FirstOrDefaultAsync(ct);
        return session.Registers.FirstOrDefault(r => r.RegisterId == id && r.Status == SessionStatus.Open)
            ?? throw new ConflictException("This register isn't open. Open it from the business day screen first.");
    }

    public IQueryable<BusinessSession> OpenSessionQuery(Guid branchId) =>
        db.BusinessSessions.Include(s => s.Registers).Where(s => s.BranchId == branchId && s.Status == SessionStatus.Open);

    private RegisterSession OpenRegister(BusinessSession session, Guid registerId, decimal openingCash, DateTimeOffset now, Guid userId)
    {
        var registerSession = new RegisterSession
        {
            TenantId = session.TenantId,
            BranchId = session.BranchId,
            BusinessSessionId = session.Id,
            RegisterId = registerId,
            OpenedAt = now,
            OpenedByUserId = userId,
            OpeningCash = Math.Round(openingCash, 2)
        };
        session.Registers.Add(registerSession);
        db.CashMovements.Add(new CashMovement
        {
            TenantId = session.TenantId,
            BranchId = session.BranchId,
            RegisterSessionId = registerSession.Id,
            Type = CashMovementType.OpeningFloat,
            Amount = registerSession.OpeningCash,
            Reason = "Opening cash"
        });
        return registerSession;
    }

    private async Task<BusinessSession> LoadSessionAsync(Guid id, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var session = await db.BusinessSessions.Include(s => s.Registers).FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new NotFoundException("Business day");
        tenant.EnsureBranch(session.BranchId);
        return session;
    }

    /// <summary>What prevents closing (decision D7): unfinished or unpaid orders and kitchen tickets still open.</summary>
    private async Task<IReadOnlyList<SessionBlockerDto>> BlockersAsync(BusinessSession session, CancellationToken ct)
    {
        var openOrders = await db.Orders
            .Where(o => o.BusinessSessionId == session.Id && o.Status != OrderStatus.Completed && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Voided)
            .OrderBy(o => o.Sequence)
            .Select(o => new { o.Id, o.OrderNumber, o.PaymentStatus, o.Status, o.Total, o.PaidTotal })
            .ToListAsync(ct);

        var blockers = openOrders.Select(o => o.PaidTotal < o.Total
            ? new SessionBlockerDto("unpaid_order", o.Id, o.OrderNumber, $"Order {o.OrderNumber} is not fully paid.")
            : new SessionBlockerDto("open_order", o.Id, o.OrderNumber, $"Order {o.OrderNumber} hasn't been served yet.")).ToList();

        var openOrderIds = openOrders.Select(o => o.Id).ToHashSet();
        var openTickets = await db.KitchenTickets
            .Where(t => t.BranchId == session.BranchId && (t.Status == KitchenTicketStatus.New || t.Status == KitchenTicketStatus.Preparing || t.Status == KitchenTicketStatus.Ready)
                && db.Orders.Any(o => o.Id == t.OrderId && o.BusinessSessionId == session.Id))
            .Select(t => new { t.OrderId, t.OrderNumber, t.IsChange })
            .ToListAsync(ct);
        blockers.AddRange(openTickets.Where(t => !openOrderIds.Contains(t.OrderId))
            .Select(t => new SessionBlockerDto("kitchen_ticket", t.OrderId, t.OrderNumber,
                t.IsChange ? $"The kitchen hasn't acknowledged a change to {t.OrderNumber}." : $"A kitchen ticket for {t.OrderNumber} is still open.")));
        return blockers;
    }

    private async Task<Dictionary<Guid, decimal>> ExpectedCashAsync(IReadOnlyCollection<Guid> registerSessionIds, CancellationToken ct)
    {
        var movements = await db.CashMovements.Where(m => registerSessionIds.Contains(m.RegisterSessionId)).ToListAsync(ct);
        return movements.GroupBy(m => m.RegisterSessionId).ToDictionary(g => g.Key, g => g.Sum(m => m.SignedAmount));
    }

    /// <summary>The session's figures (PLAN.md section 14) — the same numbers the close screen and reports show.</summary>
    public async Task<SessionSummaryDto> SummaryAsync(BusinessSession session, CancellationToken ct)
    {
        var orders = await db.Orders.Include(o => o.Lines).Where(o => o.BusinessSessionId == session.Id).ToListAsync(ct);
        var counted = orders.Where(o => o.Status is not (OrderStatus.Cancelled or OrderStatus.Voided)).ToList();

        var registerSessionIds = session.Registers.Select(r => r.Id).ToList();
        var payments = await db.Payments.Where(p => registerSessionIds.Contains(p.RegisterSessionId)).ToListAsync(ct);
        var refunds = await db.Refunds.Where(r => registerSessionIds.Contains(r.RegisterSessionId)).ToListAsync(ct);
        var movements = await db.CashMovements.Where(m => registerSessionIds.Contains(m.RegisterSessionId)).ToListAsync(ct);
        var registerNames = await db.Registers.Where(r => r.BranchId == session.BranchId).ToDictionaryAsync(r => r.Id, r => r.Name, ct);

        var refundsByPayment = await db.Refunds.Where(r => payments.Select(p => p.Id).Contains(r.PaymentId)).ToListAsync(ct);
        var byMethod = payments.GroupBy(p => (p.MethodName, p.IsCash)).Select(g => new PaymentMethodTotalDto(g.Key.MethodName, g.Key.IsCash, g.Count(),
            g.Sum(p => p.Amount), refundsByPayment.Where(r => g.Any(p => p.Id == r.PaymentId)).Sum(r => r.Amount))).OrderByDescending(m => m.Collected).ToList();

        var cash = session.Registers.Select(r =>
        {
            var own = movements.Where(m => m.RegisterSessionId == r.Id).ToList();
            decimal Sum(CashMovementType type) => own.Where(m => m.Type == type).Sum(m => m.Amount);
            var expected = own.Sum(m => m.SignedAmount);
            return new RegisterCashDto(r.Id, r.RegisterId, registerNames.GetValueOrDefault(r.RegisterId, "Register"), r.OpeningCash,
                Sum(CashMovementType.CashSale), Sum(CashMovementType.CashIn), Sum(CashMovementType.CashRefund), Sum(CashMovementType.PaidOut),
                expected, r.ActualCash, r.ActualCash is null ? null : r.ActualCash - expected);
        }).ToList();

        var grossSales = counted.Sum(o => o.Subtotal);
        var discounts = counted.Sum(o => o.DiscountTotal);
        var voidedValue = orders.Where(o => o.Status == OrderStatus.Voided).Sum(o => o.Lines.Sum(l => l.LineTotal))
            + counted.Sum(o => o.Lines.Where(l => l.Status == OrderLineStatus.Voided && l.SentAt != null).Sum(l => l.LineTotal));

        return new SessionSummaryDto(
            counted.Count,
            orders.Count - counted.Count,
            grossSales,
            discounts,
            grossSales - discounts,
            counted.Sum(o => o.ServiceCharge),
            counted.Sum(o => o.TaxTotal),
            counted.Sum(o => o.RoundingAdjustment),
            counted.Sum(o => o.Total),
            voidedValue,
            payments.Sum(p => p.Amount),
            refunds.Sum(r => r.Amount),
            byMethod,
            cash);
    }

    private async Task<BusinessSessionDto> ToDtoAsync(BusinessSession s, CancellationToken ct)
    {
        var expected = await ExpectedCashAsync(s.Registers.Select(r => r.Id).ToList(), ct);
        var registerNames = await db.Registers.Where(r => r.BranchId == s.BranchId).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var names = await NamesAsync(s.Registers.Select(r => (Guid?)r.OpenedByUserId).Append(s.StartedByUserId).Append(s.ClosedByUserId), ct);
        return new BusinessSessionDto(s.Id, s.BranchId, s.BusinessDate, s.Status, s.StartedAt, NameOf(names, s.StartedByUserId),
            s.ClosedAt, NameOf(names, s.ClosedByUserId), s.Notes, s.ReopenReason,
            s.Registers.OrderBy(r => r.OpenedAt).Select(r => new RegisterSessionDto(r.Id, r.RegisterId, registerNames.GetValueOrDefault(r.RegisterId, "Register"),
                r.Status, r.OpenedAt, NameOf(names, r.OpenedByUserId), r.OpeningCash, r.ExpectedCash ?? expected.GetValueOrDefault(r.Id),
                r.ActualCash, r.CashDifference, r.DifferenceReason)).ToList());
    }

    private async Task<CashMovementDto> ToDtoAsync(CashMovement m, CancellationToken ct)
    {
        var names = await NamesAsync([m.CreatedBy], ct);
        return new CashMovementDto(m.Id, m.RegisterSessionId, m.Type, m.Amount, m.Reason, m.CreatedAt, NameOf(names, m.CreatedBy));
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        return await db.Memberships.Where(m => ids.Contains(m.UserId)).ToDictionaryAsync(m => m.UserId, m => m.DisplayName, ct);
    }

    private static string? NameOf(Dictionary<Guid, string> names, Guid? userId) =>
        userId is { } id && names.TryGetValue(id, out var name) ? name : null;
}
