namespace Restaurant.Contracts.Enums;

// Shared by the domain model, the API and both clients (sent over JSON as strings).

/// <summary>Which order types a tax rate or service charge applies to.</summary>
[Flags]
public enum OrderTypes
{
    None = 0,
    DineIn = 1,
    Takeaway = 2,
    Delivery = 4,
    All = DineIn | Takeaway | Delivery
}

/// <summary>Some provinces tax card payments at a different rate than cash, so a rate can be limited to one.</summary>
public enum TaxPaymentScope
{
    Any = 0,
    CashOnly = 1,
    NonCashOnly = 2
}

public enum PaymentMethodKind
{
    Cash = 0,
    Card = 1,
    BankTransfer = 2,
    MobileWallet = 3,
    Other = 4
}

/// <summary>Bill rounding; the difference is recorded on the order as a rounding adjustment.</summary>
public enum RoundingMode
{
    None = 0,
    NearestRupee = 1,
    NearestFive = 2,
    NearestTen = 3
}

/// <summary>One open business session per branch (default) or one per register.</summary>
public enum SessionScope
{
    PerBranch = 0,
    PerRegister = 1
}

public enum InsufficientStockBehavior
{
    Allow = 0,
    Warn = 1,
    Block = 2
}

public enum ReceiptPaperSize
{
    Thermal80mm = 0,
    Thermal58mm = 1,
    A4 = 2
}

public enum ReceiptKind
{
    CustomerReceipt = 0,
    KitchenTicket = 1
}

public enum MembershipStatus
{
    /// <summary>Created by the owner; becomes Active when the staff member accepts the invite code.</summary>
    Invited = 0,
    Active = 1,
    Disabled = 2
}

public enum TenantStatus
{
    Active = 0,
    Suspended = 1
}
