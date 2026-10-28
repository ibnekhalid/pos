using Restaurant.Contracts.Deals;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;

namespace Restaurant.Contracts.Receipts;

public record ReceiptLineDto(int Quantity, string Name, IReadOnlyList<string> Modifiers, string? Notes, decimal LineTotal, bool Voided);

public record ReceiptPaymentDto(string Method, decimal Amount, decimal? Tendered, decimal? Change, string? Reference);

/// <summary>
/// A receipt or kitchen ticket with everything resolved by the server: branding fallbacks, branch-local
/// times as text, totals. Every renderer (PDF on the server, HTML in the browser, ESC/POS on the desktop)
/// only lays it out (Tailor Book's ReceiptDocument approach).
/// </summary>
/// <param name="IsReprint">Printed before: shown as "DUPLICATE — REPRINT #n".</param>
public record ReceiptDocumentDto(
    PrintKind Kind, ReceiptPaperSize PaperSize, string Language, bool IsReprint, int CopyNumber,
    string BusinessName, string? Tagline, string? BranchName, string? Address, string? Phone, string? Ntn, string? Strn,
    string? Header, string? Footer, string PrimaryColor, string AccentColor, string? LogoUrl, string CurrencyCode,
    string OrderNumber, OrderType OrderType, string? TableLabel, string BusinessDate, string PrintedAt, string? CashierName, string? CustomerName,
    IReadOnlyList<ReceiptLineDto> Lines, decimal Subtotal, decimal Discount, string? DiscountReason, decimal ServiceCharge, decimal ServiceChargePercent,
    IReadOnlyList<TaxLineDto> Taxes, decimal Rounding, decimal Total, IReadOnlyList<ReceiptPaymentDto> Payments, decimal Refunded,
    decimal Paid, decimal Balance, string? StationName, int? Round, string? Notes, IReadOnlyList<AppliedDealDto>? Deals = null)
{
    /// <summary>The discount not explained by a deal line (the manual discount).</summary>
    public decimal OtherDiscount => Discount - (Deals?.Sum(x => x.Discount) ?? 0);
}

/// <param name="TicketId">For KitchenTicket: which ticket (round/station) to print.</param>
public record PrintReceiptRequest(PrintKind Kind, Guid? TicketId = null);
