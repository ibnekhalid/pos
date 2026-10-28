using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>A payment toward an order. Never deleted or edited: mistakes are corrected with a refund.</summary>
public class Payment : BranchEntity
{
    public Guid OrderId { get; set; }
    public Guid RegisterSessionId { get; set; }
    public Guid PaymentMethodId { get; set; }
    public string MethodName { get; set; } = string.Empty;
    public bool IsCash { get; set; }

    /// <summary>Applied to the bill.</summary>
    public decimal Amount { get; set; }

    public decimal? Tendered { get; set; }
    public decimal? Change { get; set; }
    public string? Reference { get; set; }
    public Guid ClientRequestId { get; set; }
    public Guid TakenByUserId { get; set; }
    public string? TakenByName { get; set; }
    public decimal RefundedAmount { get; set; }
}

public class Refund : BranchEntity
{
    public Guid PaymentId { get; set; }
    public Guid OrderId { get; set; }
    public Guid RegisterSessionId { get; set; }
    public decimal Amount { get; set; }
    public bool IsCash { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? ApprovedByUserId { get; set; }
    public string? ByName { get; set; }
    public Guid ClientRequestId { get; set; }
}

/// <summary>Every receipt or kitchen-ticket print. The second print of the same thing is a reprint (T11).</summary>
public class ReceiptPrint : BranchEntity
{
    public Guid OrderId { get; set; }
    public PrintKind Kind { get; set; }
    public Guid? TicketId { get; set; }
    public int CopyNumber { get; set; }
    public Guid PrintedByUserId { get; set; }
}
