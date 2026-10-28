namespace Restaurant.Contracts.Enums;

public enum UnitDimension
{
    Mass = 0,
    Volume = 1,
    Count = 2
}

/// <summary>Every change to stock is one of these ledger entries (PLAN.md section 9).</summary>
public enum StockMovementType
{
    Opening = 0,
    Receipt = 1,
    SaleConsumption = 2,
    SaleReversal = 3,
    Wastage = 4,
    Adjustment = 5,
    CountVariance = 6,
    TransferOut = 7,
    TransferIn = 8
}

public enum PurchaseOrderStatus
{
    Draft = 0,
    Ordered = 1,
    PartiallyReceived = 2,
    Received = 3,
    Cancelled = 4
}

public enum StockTransferStatus
{
    InTransit = 0,
    Received = 1,
    Cancelled = 2
}

public enum DealType
{
    /// <summary>Fixed-price combo the cashier picks (burger + fries + drink).</summary>
    Bundle = 0,

    /// <summary>Buy X, get Y free (or at a percentage off): the cheapest qualifying items are discounted.</summary>
    BuyXGetY = 1,

    PercentOff = 2,
    AmountOff = 3
}

/// <summary>PercentOff / AmountOff: the whole order, or only the listed items.</summary>
public enum DealScope
{
    Order = 0,
    Items = 1
}

[Flags]
public enum WeekDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    All = 127
}
