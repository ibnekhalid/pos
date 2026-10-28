namespace Restaurant.Contracts.Enums;

/// <summary>An order's type. Values match <see cref="OrderTypes"/> flags.</summary>
public enum OrderType
{
    DineIn = 1,
    Takeaway = 2,
    Delivery = 4
}

/// <summary>
/// Kitchen/service progress (PLAN.md section 8). Payment is tracked separately in <see cref="PaymentStatus"/>.
/// Confirmed → SentToKitchen → Preparing → Ready → Served → Completed; Cancelled before the kitchen, Voided after.
/// </summary>
public enum OrderStatus
{
    Confirmed = 0,
    SentToKitchen = 1,
    Preparing = 2,
    Ready = 3,
    Served = 4,
    Completed = 5,
    Cancelled = 6,
    Voided = 7
}

public enum PaymentStatus
{
    Unpaid = 0,
    PartiallyPaid = 1,
    Paid = 2,
    PartiallyRefunded = 3,
    Refunded = 4
}

public enum OrderLineStatus
{
    Active = 0,
    Voided = 1
}

public enum KitchenTicketStatus
{
    New = 0,
    Preparing = 1,
    Ready = 2,
    Served = 3,
    Cancelled = 4
}

public enum MenuAvailability
{
    Available = 0,
    OutOfStock = 1,
    Unavailable = 2
}

public enum SessionStatus
{
    Open = 0,
    Closed = 1
}

public enum CashMovementType
{
    OpeningFloat = 0,
    CashSale = 1,
    CashRefund = 2,
    PaidOut = 3,
    CashIn = 4
}

/// <summary>Which tax rates apply: cash-only and non-cash-only rates depend on how the order is paid.</summary>
public enum TaxTender
{
    /// <summary>Not paid yet: priced as cash, re-priced if the first payment isn't cash.</summary>
    Unknown = 0,
    Cash = 1,
    NonCash = 2
}

public enum PrintKind
{
    Receipt = 0,
    KitchenTicket = 1
}

public enum ReservationStatus
{
    Booked = 0,
    Seated = 1,
    Cancelled = 2,
    NoShow = 3
}
