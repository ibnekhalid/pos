using System.Globalization;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Receipts;

namespace Restaurant.Infrastructure.Receipts;

/// <summary>
/// Lays out a ReceiptDocument as a PDF: continuous 80 mm / 58 mm thermal paper or A4 (Tailor Book's
/// renderer approach). Customer receipts show prices; kitchen tickets show large item text and none.
/// Noto Naskh fonts in ./fonts (copied into the Docker image) cover Urdu names.
/// </summary>
public class QuestPdfReceiptRenderer : IReceiptPdfRenderer
{
    private const string UrduFont = "Noto Naskh Arabic";

    /// <summary>Lato (bundled with QuestPDF), plus the Urdu font as a fallback where its files were found.</summary>
    private static readonly string[] Families;

    static QuestPdfReceiptRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var urdu = false;
        var fonts = Path.Combine(AppContext.BaseDirectory, "fonts");
        if (Directory.Exists(fonts))
        {
            foreach (var file in Directory.GetFiles(fonts, "*.ttf"))
            {
                using var stream = File.OpenRead(file);
                FontManager.RegisterFontFromStream(stream);
                urdu |= Path.GetFileName(file).StartsWith("NotoNaskhArabic", StringComparison.OrdinalIgnoreCase);
            }
        }
        // QuestPDF refuses font families that aren't registered, so the fallback is only named when present.
        Families = urdu ? ["Lato", UrduFont] : ["Lato"];
    }

    public byte[] Render(ReceiptDocumentDto d, byte[]? logo)
    {
        var widthMm = d.PaperSize switch
        {
            ReceiptPaperSize.Thermal58mm => 58f,
            ReceiptPaperSize.Thermal80mm => 80f,
            _ => 0f
        };
        var baseSize = widthMm switch { 0 => 10f, < 70 => 7.5f, _ => 9f };

        return Document.Create(container => container.Page(page =>
        {
            if (widthMm > 0)
            {
                page.ContinuousSize(widthMm, Unit.Millimetre);
                page.Margin(3, Unit.Millimetre);
            }
            else
            {
                page.Size(PageSizes.A4);
                page.Margin(18, Unit.Millimetre);
            }
            page.DefaultTextStyle(t => t.FontSize(baseSize).FontFamily(Families));
            page.Content().Column(col =>
            {
                if (d.Kind == PrintKind.KitchenTicket) Kitchen(col, d, baseSize);
                else Receipt(col, d, logo, baseSize);
            });
        })).GeneratePdf();
    }

    private static void Receipt(ColumnDescriptor col, ReceiptDocumentDto d, byte[]? logo, float size)
    {
        col.Spacing(1.5f);
        if (logo is not null)
        {
            col.Item().AlignCenter().Height(size * 4.5f).Image(logo).FitHeight();
        }
        col.Item().AlignCenter().Text(d.BusinessName).Bold().FontSize(size * 1.45f).FontColor(d.PrimaryColor);
        Centered(col, d.Tagline, size * 0.9f);
        Centered(col, d.Address, size * 0.9f);
        Centered(col, d.Phone, size * 0.9f);
        if (d.Ntn is not null || d.Strn is not null)
        {
            Centered(col, string.Join("   ", new[] { d.Ntn is null ? null : $"NTN {d.Ntn}", d.Strn is null ? null : $"STRN {d.Strn}" }.Where(s => s is not null)), size * 0.9f);
        }
        Centered(col, d.Header, size);
        Reprint(col, d, size);
        col.Item().PaddingVertical(3).LineHorizontal(1.5f).LineColor(d.AccentColor);

        Row(col, "Order", $"{d.OrderNumber} · {TypeText(d.OrderType)}{(d.TableLabel is null ? "" : $" · Table {d.TableLabel}")}");
        Row(col, "Business date", d.BusinessDate);
        Row(col, "Printed", d.PrintedAt);
        if (d.CashierName is not null) Row(col, "Cashier", d.CashierName);
        if (d.CustomerName is not null) Row(col, "Customer", d.CustomerName);
        Dash(col);

        foreach (var line in d.Lines)
        {
            Row(col, $"{line.Quantity} × {line.Name}", Money(line.LineTotal, d.CurrencyCode));
            foreach (var modifier in line.Modifiers) col.Item().PaddingLeft(8).Text(modifier).FontSize(size * 0.85f).FontColor(Colors.Grey.Darken2);
            if (line.Notes is not null) col.Item().PaddingLeft(8).Text(line.Notes).Italic().FontSize(size * 0.85f);
        }
        Dash(col);

        Row(col, "Subtotal", Money(d.Subtotal, d.CurrencyCode));
        foreach (var deal in d.Deals ?? []) Row(col, deal.Quantity > 1 ? $"{deal.Name} × {deal.Quantity}" : deal.Name, "-" + Money(deal.Discount, d.CurrencyCode));
        if (d.OtherDiscount > 0) Row(col, d.DiscountReason is null ? "Discount" : $"Discount ({d.DiscountReason})", "-" + Money(d.OtherDiscount, d.CurrencyCode));
        if (d.ServiceCharge > 0) Row(col, $"Service charge {d.ServiceChargePercent:0.##}%", Money(d.ServiceCharge, d.CurrencyCode));
        foreach (var tax in d.Taxes)
        {
            Row(col, $"{tax.Name} {tax.Percent:0.##}%{(tax.Inclusive ? " (incl.)" : "")}", Money(tax.Amount, d.CurrencyCode));
        }
        if (d.Rounding != 0) Row(col, "Rounding", Money(d.Rounding, d.CurrencyCode));
        col.Item().PaddingVertical(2).BorderTop(1).BorderBottom(1).BorderColor(d.AccentColor).PaddingVertical(2).Row(row =>
        {
            row.RelativeItem().Text("TOTAL").Bold().FontSize(size * 1.25f);
            row.AutoItem().Text(Money(d.Total, d.CurrencyCode)).Bold().FontSize(size * 1.25f);
        });

        foreach (var payment in d.Payments)
        {
            Row(col, payment.Reference is null ? payment.Method : $"{payment.Method} ({payment.Reference})", Money(payment.Amount, d.CurrencyCode));
            if (payment.Tendered is { } tendered && payment.Change is { } change && change > 0)
            {
                Row(col, "  Tendered", Money(tendered, d.CurrencyCode));
                Row(col, "  Change", Money(change, d.CurrencyCode));
            }
        }
        if (d.Refunded > 0) Row(col, "Refunded", "-" + Money(d.Refunded, d.CurrencyCode));
        if (d.Balance > 0) Row(col, "Balance due", Money(d.Balance, d.CurrencyCode), bold: true);

        if (d.Footer is not null)
        {
            col.Item().PaddingTop(6).AlignCenter().Text(d.Footer).FontSize(size * 0.95f);
        }
    }

    private static void Kitchen(ColumnDescriptor col, ReceiptDocumentDto d, float size)
    {
        col.Spacing(2);
        col.Item().AlignCenter().Text(d.OrderNumber).Bold().FontSize(size * 2.2f);
        col.Item().AlignCenter().Text($"{TypeText(d.OrderType).ToUpperInvariant()}{(d.TableLabel is null ? "" : $"  ·  TABLE {d.TableLabel}")}").Bold().FontSize(size * 1.2f);
        Centered(col, string.Join("  ·  ", new[] { d.StationName, d.Round is > 1 ? $"Round {d.Round}" : null, d.PrintedAt }.Where(s => s is not null)), size);
        Reprint(col, d, size);
        Dash(col);

        foreach (var line in d.Lines)
        {
            col.Item().Text(text =>
            {
                var span = text.Span($"{(line.Voided ? "CANCEL " : "")}{line.Quantity} × {line.Name}").Bold().FontSize(size * 1.35f);
                if (line.Voided) span.Strikethrough();
            });
            foreach (var modifier in line.Modifiers) col.Item().PaddingLeft(10).Text($"+ {modifier}").FontSize(size * 1.1f);
            if (line.Notes is not null) col.Item().PaddingLeft(10).Text($"» {line.Notes}").Italic().FontSize(size * 1.1f);
        }
        if (d.Notes is not null)
        {
            Dash(col);
            col.Item().Text($"Note: {d.Notes}").Italic();
        }
    }

    private static void Reprint(ColumnDescriptor col, ReceiptDocumentDto d, float size)
    {
        if (d.IsReprint)
        {
            col.Item().PaddingVertical(2).Background(Colors.Black).AlignCenter()
                .Text($"DUPLICATE — REPRINT #{d.CopyNumber - 1}").FontColor(Colors.White).Bold().FontSize(size);
        }
    }

    private static void Centered(ColumnDescriptor col, string? text, float size)
    {
        if (!string.IsNullOrWhiteSpace(text)) col.Item().AlignCenter().Text(text).FontSize(size);
    }

    private static void Row(ColumnDescriptor col, string left, string right, bool bold = false) => col.Item().Row(row =>
    {
        var l = row.RelativeItem().Text(left);
        var r = row.AutoItem().PaddingLeft(6).Text(right);
        if (bold)
        {
            l.Bold();
            r.Bold();
        }
    });

    private static void Dash(ColumnDescriptor col) => col.Item().PaddingVertical(3).LineHorizontal(0.5f).LineDashPattern([2f, 2f]).LineColor(Colors.Grey.Medium);

    private static string TypeText(OrderType type) => type switch
    {
        OrderType.DineIn => "Dine-in",
        OrderType.Takeaway => "Takeaway",
        _ => "Delivery"
    };

    private static string Money(decimal amount, string currency)
    {
        var symbol = currency == "PKR" ? "Rs" : currency;
        var number = Math.Abs(amount).ToString(amount % 1 == 0 ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);
        return amount < 0 ? $"-{symbol} {number}" : $"{symbol} {number}";
    }
}
