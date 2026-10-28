using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>
/// An order. Lines, prices, taxes and the service charge are snapshots taken when the order was placed:
/// later menu, price or tax changes never alter it (PLAN.md section 8).
/// </summary>
public class Order : BranchEntity
{
    public Guid BusinessSessionId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public OrderType Type { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Confirmed;
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Unpaid;

    public Guid? TableId { get; set; }
    public string? TableLabel { get; set; }
    public int? Covers { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? Notes { get; set; }

    public Guid? CustomerId { get; set; }
    public string? CouponCode { get; set; }

    /// <summary>The automatic deals that could apply, as they were when the order was placed (DealRule JSON).</summary>
    public string DealRulesJson { get; set; } = "[]";

    /// <summary>The automatic deals applied by the last repricing (AppliedDealDto JSON).</summary>
    public string AutoDealsJson { get; set; } = "[]";

    public decimal DealDiscountTotal { get; set; }

    public Guid CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }

    /// <summary>Unique per tenant: a retried create returns this order instead of a second one.</summary>
    public Guid ClientRequestId { get; set; }

    // Pricing rules as they were when the order was placed.
    public decimal ServiceChargePercent { get; set; }
    public bool ServiceChargeApplies { get; set; }
    public RoundingMode Rounding { get; set; }
    public string TaxRatesJson { get; set; } = "[]";
    public TaxTender TaxTender { get; set; }

    public decimal? DiscountPercent { get; set; }
    public decimal? DiscountAmount { get; set; }
    public string? DiscountReason { get; set; }

    // Totals (recalculated whenever lines, discount or tender change).
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal ServiceCharge { get; set; }
    public decimal TaxTotal { get; set; }
    public string TaxBreakdownJson { get; set; } = "[]";
    public decimal RoundingAdjustment { get; set; }
    public decimal Total { get; set; }
    public decimal PaidTotal { get; set; }
    public decimal RefundedTotal { get; set; }

    public DateTimeOffset? SentToKitchenAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? VoidReason { get; set; }

    /// <summary>PostgreSQL xmin: two cashiers changing the same order at once get a conflict, not lost updates.</summary>
    public uint Version { get; set; }

    public List<OrderLine> Lines { get; set; } = [];
    public List<OrderDealApplication> DealApplications { get; set; } = [];

    public bool IsOpen => Status is not (OrderStatus.Completed or OrderStatus.Cancelled or OrderStatus.Voided);
    public decimal Balance => Math.Max(0, Total - PaidTotal);
}

public class OrderLine : TenantEntity
{
    public Guid OrderId { get; set; }
    public Guid MenuItemId { get; set; }
    public Guid? VariantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameUr { get; set; }
    public string? VariantName { get; set; }
    public string? Sku { get; set; }
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal ModifiersTotal { get; set; }
    public decimal LineTotal { get; set; }
    public string? Notes { get; set; }
    public Guid? KitchenStationId { get; set; }
    public OrderLineStatus Status { get; set; } = OrderLineStatus.Active;
    public string? VoidReason { get; set; }
    public Guid? VoidedByUserId { get; set; }

    /// <summary>When it went to the kitchen; null while it's only on the bill.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Part of a combo deal: the deal's lines are voided together.</summary>
    public Guid? DealApplicationId { get; set; }

    /// <summary>The add-items request that created it (a retry doesn't add the lines twice).</summary>
    public Guid? ClientRequestId { get; set; }

    public List<OrderLineModifier> Modifiers { get; set; } = [];
}

public class OrderLineModifier : TenantEntity
{
    public Guid OrderLineId { get; set; }
    public Guid ModifierOptionId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal PriceDelta { get; set; }
}

public class OrderStatusHistory : TenantEntity
{
    public Guid OrderId { get; set; }
    public OrderStatus FromStatus { get; set; }
    public OrderStatus ToStatus { get; set; }
    public string? Reason { get; set; }
    public Guid? ApprovedByUserId { get; set; }
}

/// <summary>Per branch and business date; incremented atomically with an upsert (no tenant filter needed).</summary>
public class OrderSequence
{
    public Guid BranchId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public int LastNumber { get; set; }
}
