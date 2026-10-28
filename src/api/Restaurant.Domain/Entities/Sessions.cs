using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>
/// A branch's business day (PLAN.md section 7). BusinessDate is fixed when the day starts, so a session
/// running past midnight keeps it. Closed sessions are immutable except through an audited reopen.
/// </summary>
public class BusinessSession : BranchEntity
{
    public DateOnly BusinessDate { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Open;
    public DateTimeOffset StartedAt { get; set; }
    public Guid StartedByUserId { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public string? Notes { get; set; }

    /// <summary>The closing figures as they were when the day was closed (SessionSummaryDto JSON).</summary>
    public string? ClosingSummaryJson { get; set; }

    public DateTimeOffset? ReopenedAt { get; set; }
    public Guid? ReopenedByUserId { get; set; }
    public string? ReopenReason { get; set; }

    public List<RegisterSession> Registers { get; set; } = [];
}

/// <summary>One cash drawer's shift inside a business session.</summary>
public class RegisterSession : BranchEntity
{
    public Guid BusinessSessionId { get; set; }
    public Guid RegisterId { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Open;
    public DateTimeOffset OpenedAt { get; set; }
    public Guid OpenedByUserId { get; set; }
    public decimal OpeningCash { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? ActualCash { get; set; }
    public decimal? CashDifference { get; set; }
    public string? DifferenceReason { get; set; }
}

/// <summary>Every change to a drawer's cash. Expected cash is always the sum of these.</summary>
public class CashMovement : BranchEntity
{
    public Guid RegisterSessionId { get; set; }
    public CashMovementType Type { get; set; }

    /// <summary>Always positive; the type says which way it moves.</summary>
    public decimal Amount { get; set; }

    public string? Reason { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? RefundId { get; set; }
    public Guid? ClientRequestId { get; set; }

    public decimal SignedAmount => Type is CashMovementType.CashRefund or CashMovementType.PaidOut ? -Amount : Amount;
}
