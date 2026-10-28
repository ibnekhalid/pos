using System.Text;
using FluentAssertions;
using Restaurant.Client.Core.Printing;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Receipts;

namespace Restaurant.Tests.Unit;

/// <summary>The desktop app's thermal printout (PLAN.md section 10).</summary>
public class EscPosTests
{
    private static ReceiptDocumentDto Receipt(ReceiptPaperSize paper = ReceiptPaperSize.Thermal80mm, PrintKind kind = PrintKind.Receipt, bool reprint = false) => new(
        kind, paper, "en", reprint, reprint ? 2 : 1, "Karachi Grill House", "Since 1985", "Clifton", "Block 5, Clifton", "021-111", "1234567-8", null,
        "Sales tax invoice", "Thank you!", "#0F766E", "#F59E0B", null, "PKR", "G-042", OrderType.DineIn, "7", "28 Oct 2026", "28 Oct 2026, 8:41 PM",
        "Ayesha", null,
        [new(2, "Zinger Burger", ["Extra cheese"], "No onions", 1300, false), new(1, "Mint Margarita", [], null, 300, kind == PrintKind.KitchenTicket)],
        1600, 0, null, 80, 5, [new TaxLineDto("GST", 16, false, 256)], 0, 1936, [new("Cash", 1936, 2000, 64, null)], 0, 1936, 0, "Grill", 1, null);

    /// <summary>The printed text: ESC/GS command sequences skipped, line feeds kept.</summary>
    private static string Text(byte[] bytes)
    {
        var text = new StringBuilder();
        for (var i = 0; i < bytes.Length; i++)
        {
            switch (bytes[i])
            {
                case 0x1B: i += bytes[i + 1] switch { (byte)'@' => 1, (byte)'p' => 4, _ => 2 }; break; // ESC @ / ESC p m t1 t2 / ESC x n
                case 0x1D: i += bytes[i + 1] == (byte)'V' ? 3 : 2; break;                          // GS V m n / GS ! n
                default: text.Append((char)bytes[i]); break;
            }
        }
        return text.ToString();
    }

    [Fact]
    public void Receipt_has_the_items_totals_and_a_paper_cut()
    {
        var bytes = EscPos.Render(Receipt(), openDrawer: false);
        var text = Text(bytes);

        bytes.Take(2).Should().Equal(0x1B, 0x40);                 // initialise
        text.Should().Contain("Karachi Grill House").And.Contain("2 x Zinger Burger").And.Contain("+ Extra cheese")
            .And.Contain("GST 16%").And.Contain("Rs 1,936").And.Contain("Change").And.Contain("NTN 1234567-8");
        bytes.TakeLast(4).Should().Equal(0x1D, 0x56, 0x42, 0x00); // cut, and no drawer kick
        text.Split('\n').Should().OnlyContain(line => line.Length <= 48);
    }

    [Fact]
    public void Cash_sales_kick_the_drawer_after_the_cut()
    {
        EscPos.Render(Receipt(), openDrawer: true).TakeLast(5).Should().Equal(0x1B, 0x70, 0x00, 0x19, 0xFA);
    }

    [Fact]
    public void Narrow_paper_wraps_at_32_columns_and_reprints_are_marked()
    {
        var text = Text(EscPos.Render(Receipt(ReceiptPaperSize.Thermal58mm, reprint: true), openDrawer: false));
        text.Should().Contain("REPRINT #1");
        text.Split('\n').Should().OnlyContain(line => line.Length <= 32);
    }

    [Fact]
    public void Kitchen_tickets_show_items_without_prices()
    {
        var text = Text(EscPos.Render(Receipt(kind: PrintKind.KitchenTicket), openDrawer: false));
        text.Should().Contain("G-042").And.Contain("TABLE 7").And.Contain("2 x Zinger Burger").And.Contain("CANCEL 1 x Mint Margarita");
        text.Should().NotContain("Rs ");
    }
}
