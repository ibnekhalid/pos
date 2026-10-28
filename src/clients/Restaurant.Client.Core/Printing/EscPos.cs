using System.Globalization;
using System.Text;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Receipts;

namespace Restaurant.Client.Core.Printing;

/// <summary>
/// Turns a receipt or kitchen-ticket document into ESC/POS bytes for thermal printers (48 columns on
/// 80 mm paper, 32 on 58 mm): bold and double-size text, paper cut and the cash-drawer kick.
/// Text mode prints ASCII only. A line with anything else (Urdu names) is drawn by the rasteriser and sent
/// as a GS v 0 raster image the width of the paper; without one it falls back to "?" (PLAN.md section 10).
/// </summary>
public static class EscPos
{
    private static readonly byte[] Init = [0x1B, 0x40];
    private static readonly byte[] Cut = [0x1D, 0x56, 0x42, 0x00];
    private static readonly byte[] DrawerKick = [0x1B, 0x70, 0x00, 0x19, 0xFA];

    /// <param name="rasterizer">Draws the lines text mode can't print; null prints them with "?".</param>
    public static byte[] Render(ReceiptDocumentDto d, bool openDrawer, ITextRasterizer? rasterizer = null)
    {
        var narrow = d.PaperSize == ReceiptPaperSize.Thermal58mm;
        var p = new Builder(narrow ? 32 : 48, narrow ? 384 : 576, rasterizer);
        p.Raw(Init);
        if (d.Kind == PrintKind.KitchenTicket) Kitchen(p, d);
        else Receipt(p, d);
        p.Feed(4);
        p.Raw(Cut);
        if (openDrawer) p.Raw(DrawerKick);
        return p.ToArray();
    }

    private static void Receipt(Builder p, ReceiptDocumentDto d)
    {
        p.Center();
        p.Size(2);
        p.Bold(true).Line(d.BusinessName).Bold(false);
        p.Size(1);
        foreach (var text in new[] { d.Tagline, d.Address, d.Phone, Tax(d), d.Header }) p.Wrap(text);
        if (d.IsReprint) p.Bold(true).Line($"*** DUPLICATE - REPRINT #{d.CopyNumber - 1} ***").Bold(false);
        p.Left().Rule('=');

        p.Pair("Order", $"{d.OrderNumber} {Type(d.OrderType)}{(d.TableLabel is null ? "" : $" T{d.TableLabel}")}");
        p.Pair("Date", d.BusinessDate);
        p.Pair("Printed", d.PrintedAt);
        if (d.CashierName is not null) p.Pair("Cashier", d.CashierName);
        if (d.CustomerName is not null) p.Pair("Customer", d.CustomerName);
        p.Rule('-');

        foreach (var line in d.Lines)
        {
            p.Pair($"{line.Quantity} x {line.Name}", Money(line.LineTotal, d.CurrencyCode));
            foreach (var modifier in line.Modifiers) p.Wrap("  + " + modifier);
            if (line.Notes is not null) p.Wrap("  > " + line.Notes);
        }
        p.Rule('-');

        p.Pair("Subtotal", Money(d.Subtotal, d.CurrencyCode));
        foreach (var deal in d.Deals ?? []) p.Pair(deal.Quantity > 1 ? $"{deal.Name} x{deal.Quantity}" : deal.Name, "-" + Money(deal.Discount, d.CurrencyCode));
        if (d.OtherDiscount > 0) p.Pair("Discount", "-" + Money(d.OtherDiscount, d.CurrencyCode));
        if (d.ServiceCharge > 0) p.Pair($"Service {d.ServiceChargePercent:0.##}%", Money(d.ServiceCharge, d.CurrencyCode));
        foreach (var tax in d.Taxes) p.Pair($"{tax.Name} {tax.Percent:0.##}%{(tax.Inclusive ? " incl" : "")}", Money(tax.Amount, d.CurrencyCode));
        if (d.Rounding != 0) p.Pair("Rounding", Money(d.Rounding, d.CurrencyCode));
        p.Bold(true).Size(2, height: true).Pair("TOTAL", Money(d.Total, d.CurrencyCode)).Size(1).Bold(false);

        foreach (var payment in d.Payments)
        {
            p.Pair(payment.Method, Money(payment.Amount, d.CurrencyCode));
            if (payment.Change is > 0)
            {
                p.Pair("  Tendered", Money(payment.Tendered ?? 0, d.CurrencyCode));
                p.Pair("  Change", Money(payment.Change.Value, d.CurrencyCode));
            }
        }
        if (d.Refunded > 0) p.Pair("Refunded", "-" + Money(d.Refunded, d.CurrencyCode));
        if (d.Balance > 0) p.Bold(true).Pair("BALANCE DUE", Money(d.Balance, d.CurrencyCode)).Bold(false);

        if (d.Footer is not null)
        {
            p.Rule('-').Center();
            p.Wrap(d.Footer);
        }
    }

    private static void Kitchen(Builder p, ReceiptDocumentDto d)
    {
        p.Center().Bold(true).Size(2);
        p.Line(d.OrderNumber);
        p.Size(1).Line($"{Type(d.OrderType).ToUpperInvariant()}{(d.TableLabel is null ? "" : $" - TABLE {d.TableLabel}")}");
        p.Bold(false);
        p.Line(string.Join(" - ", new[] { d.StationName, d.Round is > 1 ? $"Round {d.Round}" : null, d.PrintedAt }.Where(s => s is not null)));
        if (d.IsReprint) p.Bold(true).Line($"*** REPRINT #{d.CopyNumber - 1} ***").Bold(false);
        p.Left().Rule('=');

        foreach (var line in d.Lines)
        {
            p.Bold(true).Size(1, height: true);
            p.Wrap($"{(line.Voided ? "CANCEL " : "")}{line.Quantity} x {line.Name}");
            p.Size(1).Bold(false);
            foreach (var modifier in line.Modifiers) p.Wrap("   + " + modifier);
            if (line.Notes is not null) p.Wrap("   > " + line.Notes);
        }
        if (d.Notes is not null)
        {
            p.Rule('-');
            p.Wrap("Note: " + d.Notes);
        }
    }

    private static string? Tax(ReceiptDocumentDto d) =>
        d.Ntn is null && d.Strn is null ? null : string.Join("  ", new[] { d.Ntn is null ? null : $"NTN {d.Ntn}", d.Strn is null ? null : $"STRN {d.Strn}" }.Where(s => s is not null));

    private static string Type(OrderType type) => type switch { OrderType.DineIn => "Dine-in", OrderType.Takeaway => "Takeaway", _ => "Delivery" };

    private static string Money(decimal amount, string currency) =>
        (currency == "PKR" ? "Rs " : currency + " ") + amount.ToString(amount % 1 == 0 ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);

    /// <param name="dots">Paper width in dots, for raster lines.</param>
    private sealed class Builder(int columns, int dots, ITextRasterizer? rasterizer)
    {
        private readonly MemoryStream _out = new();
        private int _width = 1;
        private int _height = 1;
        private bool _center;
        private bool _bold;

        public void Raw(byte[] bytes) => _out.Write(bytes);

        public Builder Center() { _center = true; Raw([0x1B, 0x61, 0x01]); return this; }
        public Builder Left() { _center = false; Raw([0x1B, 0x61, 0x00]); return this; }
        public Builder Bold(bool on) { _bold = on; Raw([0x1B, 0x45, (byte)(on ? 1 : 0)]); return this; }

        /// <param name="factor">1 = normal, 2 = double width (and height unless height-only).</param>
        public Builder Size(int factor, bool height = false)
        {
            _width = height ? 1 : factor;
            var w = height ? 0 : factor - 1;
            var h = factor == 1 && !height ? 0 : 1;
            _height = h + 1;
            Raw([0x1D, 0x21, (byte)((w << 4) | h)]);
            return this;
        }

        private int Columns => columns / _width;

        public Builder Line(string text)
        {
            if (NeedsRaster(text)) { Picture(text, null); return this; }
            Raw(Encode(text.Length > Columns ? text[..Columns] : text));
            Raw([0x0A]);
            return this;
        }

        public void Wrap(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (NeedsRaster(text)) { Picture(text, null); return; }
            foreach (var part in Split(text, Columns)) Line(part);
        }

        /// <summary>Left text and a right-aligned amount on one line (left wraps if it's too long).</summary>
        public Builder Pair(string left, string right)
        {
            if (NeedsRaster(left) || NeedsRaster(right)) { Picture(left, right); return this; }
            var room = Columns - right.Length - 1;
            var parts = Split(left, Math.Max(room, 8)).ToList();
            for (var i = 0; i < parts.Count - 1; i++) Line(parts[i]);
            var last = parts.Count == 0 ? "" : parts[^1];
            Line(last + new string(' ', Math.Max(1, Columns - last.Length - right.Length)) + right);
            return this;
        }

        public Builder Rule(char c) => Line(new string(c, Columns));

        public void Feed(int lines) => Raw(Enumerable.Repeat((byte)0x0A, lines).ToArray());

        public byte[] ToArray() => _out.ToArray();

        private static IEnumerable<string> Split(string text, int width)
        {
            var line = new StringBuilder();
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var w = word;
                while (w.Length > width)
                {
                    if (line.Length > 0) { yield return line.ToString(); line.Clear(); }
                    yield return w[..width];
                    w = w[width..];
                }
                if (line.Length + w.Length + (line.Length > 0 ? 1 : 0) > width)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(w);
            }
            if (line.Length > 0) yield return line.ToString();
        }

        /// <summary>
        /// The line as a GS v 0 raster image, wrapped by the rasteriser. A line that starts with Urdu reads
        /// right to left and is right-aligned, unless centred; amounts in a pair stay on the right.
        /// </summary>
        private void Picture(string text, string? end)
        {
            var rtl = RightToLeft(text);
            var align = _center ? RasterAlign.Center : rtl && end is null ? RasterAlign.Right : RasterAlign.Left;
            var image = rasterizer!.Rasterize(new RasterText(text, end, dots, align, rtl, _bold, _width, _height));
            var row = image.Width / 8;
            if (image.Width != dots || image.Bits.Length != row * image.Height) throw new InvalidOperationException("The rasteriser returned an image of the wrong size.");
            if (image.Height == 0) return;
            Raw([0x1D, 0x76, 0x30, 0x00, (byte)row, (byte)(row >> 8), (byte)image.Height, (byte)(image.Height >> 8)]);
            Raw(image.Bits);
        }

        private bool NeedsRaster(string text) => rasterizer is not null && !text.All(Printable);

        /// <summary>The first strong character decides the direction (Unicode bidi rule P2); digits are neutral.</summary>
        private static bool RightToLeft(string text)
        {
            foreach (var ch in text)
            {
                if (char.IsDigit(ch)) continue;
                if (ch is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= 'ﻼ') return true;
                if (char.IsLetter(ch)) return false;
            }
            return false;
        }

        private static bool Printable(char ch) => ch is >= ' ' and <= '~';

        /// <summary>Code page 437 text: anything outside ASCII becomes "?" (printers differ on the rest).</summary>
        private static byte[] Encode(string text) => text.Select(ch => Printable(ch) ? (byte)ch : (byte)'?').ToArray();
    }
}
