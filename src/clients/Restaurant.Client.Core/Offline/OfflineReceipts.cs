using System.Globalization;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Receipts;

namespace Restaurant.Client.Core.Offline;

/// <summary>
/// Receipts and kitchen tickets for orders taken offline, laid out from the cached branding and branch like the
/// server's documents. They carry the provisional number and say so; the customer's final receipt (with the real
/// order number and the server's prices) can be reprinted from the order once it has synced.
/// </summary>
public static class OfflineReceipts
{
    public record Line(int Quantity, string Name, IReadOnlyList<string> Modifiers, string? Notes, decimal Total);

    public static ReceiptDocumentDto Build(PrintKind kind, OfflineSnapshot snapshot, string number, OrderType type, string? table, string? cashier,
        string? customer, IReadOnlyList<Line> lines, OfflineEstimate.Result estimate, IReadOnlyList<ReceiptPaymentDto> payments, string offlineNote)
    {
        var branding = snapshot.Branding;
        var paid = payments.Sum(p => p.Amount);
        return new ReceiptDocumentDto(
            kind, ReceiptPaperSize.Thermal80mm, branding?.ReceiptLanguage ?? "en", false, 1,
            branding?.DisplayName ?? snapshot.Tenant?.Name ?? snapshot.Branch.Name, branding?.Tagline, snapshot.Branch.Name,
            branding?.DisplayAddress ?? snapshot.Branch.Address, branding?.DisplayPhone ?? snapshot.Branch.Phone,
            snapshot.Tenant?.NtnNumber, snapshot.Tenant?.StrnNumber,
            // Receipts show the note in the header (kitchen tickets have a notes line of their own).
            kind == PrintKind.Receipt ? string.Join("\n", new[] { offlineNote, branding?.ReceiptHeader }.Where(s => !string.IsNullOrWhiteSpace(s))) : branding?.ReceiptHeader,
            branding?.ReceiptFooter, branding?.PrimaryColor ?? "#0F766E", branding?.AccentColor ?? "#F59E0B", null,
            snapshot.Tenant?.CurrencyCode ?? "PKR",
            number, type, table, snapshot.Session.BusinessDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            DateTime.Now.ToString("dd MMM yyyy, h:mm tt", CultureInfo.InvariantCulture), cashier, customer,
            lines.Select(l => new ReceiptLineDto(l.Quantity, l.Name, l.Modifiers, l.Notes, l.Total, false)).ToList(),
            estimate.Subtotal, 0, null, estimate.ServiceCharge, estimate.ServiceChargePercent, estimate.Taxes, estimate.Rounding, estimate.Total,
            payments, 0, paid, Math.Max(0, estimate.Total - paid),
            kind == PrintKind.KitchenTicket ? "Kitchen" : null, kind == PrintKind.KitchenTicket ? 1 : null, offlineNote);
    }
}
