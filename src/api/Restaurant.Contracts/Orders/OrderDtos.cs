using Restaurant.Contracts.Deals;
using Restaurant.Contracts.Enums;

namespace Restaurant.Contracts.Orders;

/// <summary>A manager's on-the-spot approval for something the cashier can't do alone.</summary>
public record Approval(Guid MembershipId, string Pin);

public record ApproverDto(Guid MembershipId, string DisplayName);

/// <summary>What the client sends for a line: ids and quantity only — the server prices everything.</summary>
public record OrderLineRequest(Guid MenuItemId, Guid? VariantId, int Quantity, IReadOnlyList<Guid> ModifierOptionIds, string? Notes);

/// <summary>Percent or Amount (not both), with a reason. Needs discounts.manual or an approval.</summary>
public record ManualDiscountRequest(decimal? Percent, decimal? Amount, string Reason);

public record PricePreviewRequest(OrderType Type, IReadOnlyList<OrderLineRequest> Lines, ManualDiscountRequest? Discount, Guid? ExistingOrderId = null,
    IReadOnlyList<DealSelection>? Deals = null, string? CouponCode = null);

public record TaxLineDto(string Name, decimal Percent, bool Inclusive, decimal Amount);

public record PricedLineDto(Guid MenuItemId, Guid? VariantId, string Name, string? VariantName, int Quantity, decimal UnitPrice,
    decimal ModifiersTotal, decimal LineTotal, IReadOnlyList<string> Modifiers);

/// <param name="Discount">Deals plus any manual discount.</param>
public record PriceBreakdownDto(decimal Subtotal, decimal Discount, decimal ServiceCharge, decimal ServiceChargePercent,
    IReadOnlyList<TaxLineDto> Taxes, decimal TaxTotal, decimal Rounding, decimal Total, IReadOnlyList<PricedLineDto> Lines,
    decimal DealDiscount = 0, IReadOnlyList<AppliedDealDto>? Deals = null);

/// <param name="ClientRequestId">Made once per cart and reused on every retry: the same id never creates a second order.</param>
public record CreateOrderRequest(Guid ClientRequestId, OrderType Type, Guid? TableId, int? Covers, string? CustomerName, string? CustomerPhone,
    string? Notes, IReadOnlyList<OrderLineRequest> Lines, ManualDiscountRequest? Discount, bool SendToKitchen, Approval? Approval = null,
    IReadOnlyList<DealSelection>? Deals = null, string? CouponCode = null, Guid? CustomerId = null);

public record AddOrderLinesRequest(Guid ClientRequestId, IReadOnlyList<OrderLineRequest> Lines, bool SendToKitchen, IReadOnlyList<DealSelection>? Deals = null);

public record ReasonRequest(string? Reason, Approval? Approval = null);

public record SetDiscountRequest(ManualDiscountRequest? Discount, Approval? Approval = null);

public record OrderLineDto(Guid Id, Guid MenuItemId, Guid? VariantId, string Name, string? VariantName, int Quantity, decimal UnitPrice,
    decimal ModifiersTotal, decimal LineTotal, IReadOnlyList<string> Modifiers, string? Notes, OrderLineStatus Status, string? VoidReason, bool SentToKitchen,
    Guid? DealApplicationId = null);

public record PaymentDto(Guid Id, string Method, bool IsCash, decimal Amount, decimal? Tendered, decimal? Change, string? Reference,
    DateTimeOffset At, string? TakenByName, decimal Refunded);

public record RefundDto(Guid Id, Guid PaymentId, decimal Amount, string Reason, DateTimeOffset At, bool IsCash, string? ByName);

public record OrderDto(
    Guid Id, Guid BranchId, Guid BusinessSessionId, string OrderNumber, OrderType Type, OrderStatus Status, PaymentStatus PaymentStatus,
    Guid? TableId, string? TableLabel, int? Covers, string? CustomerName, string? CustomerPhone, string? Notes,
    decimal Subtotal, decimal Discount, string? DiscountReason, decimal ServiceCharge, decimal ServiceChargePercent,
    IReadOnlyList<TaxLineDto> Taxes, decimal TaxTotal, decimal Rounding, decimal Total, decimal Paid, decimal Refunded, decimal Balance,
    IReadOnlyList<OrderLineDto> Lines, IReadOnlyList<PaymentDto> Payments, IReadOnlyList<RefundDto> Refunds,
    DateTimeOffset CreatedAt, string? CreatedByName, string? VoidReason, bool SessionOpen,
    Guid? CustomerId = null, string? CouponCode = null, decimal DealDiscount = 0, IReadOnlyList<AppliedDealDto>? Deals = null,
    IReadOnlyList<string>? StockWarnings = null);

public record OrderSummaryDto(Guid Id, string OrderNumber, OrderType Type, OrderStatus Status, PaymentStatus PaymentStatus,
    string? TableLabel, string? CustomerName, decimal Total, decimal Balance, int ItemCount, DateTimeOffset CreatedAt);

/// <param name="Amount">Applied to the bill. For cash, Tendered is what the customer handed over (change = Tendered − Amount).</param>
/// <param name="RegisterId">Null = the branch's default register.</param>
public record TakePaymentRequest(Guid ClientRequestId, Guid PaymentMethodId, decimal Amount, decimal? Tendered, string? Reference, Guid? RegisterId = null);

public record RefundRequest(Guid ClientRequestId, decimal Amount, string Reason, Approval? Approval = null);
