using Restaurant.Contracts.Enums;

namespace Restaurant.Contracts.Sessions;

public record RegisterSessionDto(
    Guid Id, Guid RegisterId, string RegisterName, SessionStatus Status, DateTimeOffset OpenedAt, string? OpenedByName,
    decimal OpeningCash, decimal ExpectedCash, decimal? ActualCash, decimal? CashDifference, string? DifferenceReason);

/// <param name="BusinessDate">The day the session's sales count toward (may differ from the calendar date after midnight).</param>
public record BusinessSessionDto(
    Guid Id, Guid BranchId, DateOnly BusinessDate, SessionStatus Status, DateTimeOffset StartedAt, string? StartedByName,
    DateTimeOffset? ClosedAt, string? ClosedByName, string? Notes, string? ReopenReason, IReadOnlyList<RegisterSessionDto> Registers);

public record StartSessionRequest(decimal OpeningCash, string? Notes);
public record OpenRegisterRequest(Guid RegisterId, decimal OpeningCash);

public record CashMovementDto(Guid Id, Guid RegisterSessionId, CashMovementType Type, decimal Amount, string? Reason, DateTimeOffset At, string? ByName);

/// <param name="Type">PaidOut or CashIn (the others are recorded automatically).</param>
/// <param name="RegisterId">Null = the branch's default register.</param>
public record RecordCashMovementRequest(Guid ClientRequestId, Guid? RegisterId, CashMovementType Type, decimal Amount, string Reason);

/// <param name="Kind">"open_order", "unpaid_order", "kitchen_ticket".</param>
public record SessionBlockerDto(string Kind, Guid? OrderId, string? OrderNumber, string Description);

public record PaymentMethodTotalDto(string Method, bool IsCash, int Count, decimal Collected, decimal Refunded);

/// <summary>
/// Expected = Opening + CashSales + CashIn − CashRefunds − PaidOuts (PLAN.md section 7).
/// CashSales are net of change given.
/// </summary>
public record RegisterCashDto(
    Guid RegisterSessionId, Guid RegisterId, string RegisterName, decimal OpeningCash, decimal CashSales, decimal CashIn,
    decimal CashRefunds, decimal PaidOuts, decimal ExpectedCash, decimal? ActualCash, decimal? Difference);

/// <summary>Figures for a business session; the formulas are in PLAN.md section 14.</summary>
public record SessionSummaryDto(
    int OrderCount, int CancelledOrVoidedCount, decimal GrossSales, decimal Discounts, decimal NetSales, decimal ServiceCharge,
    decimal Tax, decimal Rounding, decimal TotalBilled, decimal VoidedValue, decimal Collected, decimal Refunds,
    IReadOnlyList<PaymentMethodTotalDto> Payments, IReadOnlyList<RegisterCashDto> Cash);

public record ClosePreviewDto(BusinessSessionDto Session, IReadOnlyList<SessionBlockerDto> Blockers, SessionSummaryDto Summary, decimal DifferenceThreshold);

public record RegisterCountRequest(Guid RegisterSessionId, decimal ActualCash, string? DifferenceReason);
public record CloseSessionRequest(IReadOnlyList<RegisterCountRequest> Counts, string? Notes);
public record ReopenSessionRequest(string Reason);

public record SessionHistoryItemDto(Guid Id, DateOnly BusinessDate, SessionStatus Status, DateTimeOffset StartedAt, DateTimeOffset? ClosedAt, decimal TotalBilled, decimal? CashDifference);

public record ClosedSessionDto(BusinessSessionDto Session, SessionSummaryDto Summary);
