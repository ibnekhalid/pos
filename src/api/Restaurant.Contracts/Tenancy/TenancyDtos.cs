using Restaurant.Contracts.Enums;

namespace Restaurant.Contracts.Tenancy;

public record TenantDto(Guid Id, string Name, string? LegalName, string? NtnNumber, string? StrnNumber, string CurrencyCode, TenantStatus Status);

public record UpdateTenantRequest(string Name, string? LegalName, string? NtnNumber, string? StrnNumber);

/// <summary>Operating rules for one branch (PLAN.md sections 6–7).</summary>
/// <param name="TimeZoneId">IANA id; business dates are calculated in it. Default Asia/Karachi.</param>
/// <param name="DayStartHour">Local hour (0–23) a new business date begins, for restaurants open past midnight.</param>
/// <param name="CashDifferenceThreshold">A cash count further off than this needs a reason at closing.</param>
/// <param name="LateTicketMinutes">Kitchen tickets older than this are shown as late.</param>
public record BranchSettingsDto(
    string TimeZoneId,
    int DayStartHour,
    decimal ServiceChargePercent,
    OrderTypes ServiceChargeAppliesTo,
    RoundingMode Rounding,
    SessionScope SessionScope,
    decimal CashDifferenceThreshold,
    InsufficientStockBehavior InsufficientStock,
    int LateTicketMinutes);

public record BranchDto(
    Guid Id,
    string Name,
    string? Address,
    string? City,
    string? Phone,
    string OrderNumberPrefix,
    bool IsActive,
    BranchSettingsDto Settings);

public record SaveBranchRequest(string Name, string? Address, string? City, string? Phone, string OrderNumberPrefix, bool IsActive = true);

public record RegisterDto(
    Guid Id,
    Guid BranchId,
    string Name,
    bool IsDefault,
    bool IsActive,
    string? ReceiptPrinterName,
    string? KitchenPrinterName,
    bool CashDrawerEnabled);

public record SaveRegisterRequest(
    string Name,
    bool IsDefault,
    bool IsActive,
    string? ReceiptPrinterName,
    string? KitchenPrinterName,
    bool CashDrawerEnabled);

/// <param name="BranchId">Null applies to every branch.</param>
public record TaxRateDto(Guid Id, string Name, decimal Percent, bool IsInclusive, OrderTypes AppliesTo, TaxPaymentScope PaymentScope, Guid? BranchId, bool IsActive);

public record SaveTaxRateRequest(string Name, decimal Percent, bool IsInclusive, OrderTypes AppliesTo, TaxPaymentScope PaymentScope, Guid? BranchId, bool IsActive = true);

public record PaymentMethodDto(Guid Id, string Name, PaymentMethodKind Kind, bool IsCash, bool RequiresReference, int SortOrder, bool IsActive);

public record SavePaymentMethodRequest(string Name, PaymentMethodKind Kind, bool RequiresReference, int SortOrder, bool IsActive = true);

public record DeviceDto(Guid Id, Guid BranchId, Guid? RegisterId, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool IsRevoked);

public record RegisterDeviceRequest(Guid BranchId, Guid? RegisterId, string Name);

/// <param name="RegisterId">The till this PC is: its printers, cash drawer and payments. Null = the branch's default register.</param>
public record UpdateDeviceRequest(string Name, Guid? RegisterId);

/// <param name="DeviceToken">Shown once. The desktop app keeps it in secure storage for PIN sign-in.</param>
public record RegisterDeviceResult(DeviceDto Device, string DeviceToken);

public record PlatformTenantDto(Guid Id, string Name, TenantStatus Status, DateTimeOffset CreatedAt, int BranchCount, int MemberCount);
