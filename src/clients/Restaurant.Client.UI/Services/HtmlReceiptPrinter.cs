using Restaurant.Client.Core.Platform;
using Restaurant.Contracts.Enums;

namespace Restaurant.Client.UI.Services;

/// <summary>
/// Prints the receipt rendered on the page through the print dialog, sized for the paper. The web app's
/// printer, and the desktop app's fallback when no Windows printer is set on the register. In Chrome,
/// <c>--kiosk-printing</c> makes this silent (docs/PRINTING.md).
/// </summary>
public class HtmlReceiptPrinter(UiInterop ui) : IReceiptPrinter
{
    public bool SupportsSilentPrinting => false;

    public async Task PrintAsync(PrintRequest request, CancellationToken cancellationToken = default)
    {
        int? width = request.Document.PaperSize switch
        {
            ReceiptPaperSize.Thermal58mm => 58,
            ReceiptPaperSize.Thermal80mm => 80,
            _ => null
        };
        if (!await ui.PrintElementAsync(request.HtmlElementId, width))
        {
            throw new InvalidOperationException("The receipt could not be prepared for printing.");
        }
    }
}
