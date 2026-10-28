using Restaurant.Client.Core.Api;
using Restaurant.Client.Core.Auth;
using Restaurant.Client.Core.Platform;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Receipts;
using Restaurant.Contracts.Tenancy;

namespace Restaurant.Client.UI.Services;

/// <summary>
/// Prints receipts and kitchen tickets: logs the print with the API (which marks reprints), renders the
/// document into the hidden PrintHost, and hands it to the platform printer with the register's printer
/// name and cash-drawer setting (PLAN.md section 10).
/// </summary>
public class PrintService(RestaurantApi api, IReceiptPrinter printer, SessionStore session)
{
    public const string ElementId = "rp-print-area";

    private TaskCompletionSource? _rendered;
    private static readonly TimeSpan RegisterCacheTime = TimeSpan.FromMinutes(5);

    private (Guid BranchId, RegisterDto? Register, DateTimeOffset At)? _register;

    public ReceiptDocumentDto? Document { get; private set; }

    /// <summary>PrintHost re-renders when this fires and calls <see cref="Rendered"/> afterwards.</summary>
    public event Action? DocumentChanged;

    public bool SilentPrinting => printer.SupportsSilentPrinting;

    /// <summary>Prints; returns an error message instead of throwing (printing must never lose a sale).</summary>
    public async Task<string?> PrintAsync(Guid orderId, PrintKind kind, Guid? ticketId = null, bool openDrawer = false)
    {
        try
        {
            var document = await api.PrintReceiptAsync(orderId, new PrintReceiptRequest(kind, ticketId));
            await PrintDocumentAsync(document, openDrawer);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The kitchen tickets of the latest send, on the register's kitchen printer (desktop only, when one is set).</summary>
    public async Task<string?> PrintKitchenRoundAsync(Guid orderId)
    {
        var register = await RegisterAsync();
        if (!printer.SupportsSilentPrinting || string.IsNullOrWhiteSpace(register?.KitchenPrinterName)) return null;

        try
        {
            var tickets = (await api.GetKitchenTicketsAsync(null)).Where(t => t.OrderId == orderId && !t.IsChange).ToList();
            var round = tickets.Count == 0 ? 0 : tickets.Max(t => t.Round);
            foreach (var ticket in tickets.Where(t => t.Round == round))
            {
                var error = await PrintAsync(orderId, PrintKind.KitchenTicket, ticket.Id);
                if (error is not null) return error;
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task PrintDocumentAsync(ReceiptDocumentDto document, bool openDrawer)
    {
        Document = document;
        _rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DocumentChanged?.Invoke();
        await Task.WhenAny(_rendered.Task, Task.Delay(2000));

        var register = await RegisterAsync();
        var printerName = document.Kind == PrintKind.KitchenTicket ? register?.KitchenPrinterName : register?.ReceiptPrinterName;
        await printer.PrintAsync(new PrintRequest(
            document.Kind == PrintKind.KitchenTicket ? PrintTarget.KitchenTicket : PrintTarget.Receipt,
            document, ElementId, string.IsNullOrWhiteSpace(printerName) ? null : printerName,
            openDrawer && register?.CashDrawerEnabled == true));
    }

    public void Rendered() => _rendered?.TrySetResult();

    /// <summary>
    /// The branch's default register (its printers and drawer), cached per branch for a few minutes, so a
    /// printer change made on another PC reaches this one without restarting the app.
    /// </summary>
    private async Task<RegisterDto?> RegisterAsync()
    {
        var branchId = session.Current?.BranchId;
        if (branchId is null) return null;
        if (_register is { } cached && cached.BranchId == branchId && DateTimeOffset.UtcNow - cached.At < RegisterCacheTime) return cached.Register;

        var registers = await api.GetRegistersAsync(branchId.Value);
        var register = registers.FirstOrDefault(r => r.IsDefault);
        _register = (branchId.Value, register, DateTimeOffset.UtcNow);
        return register;
    }

    /// <summary>Register settings changed: read them again next time.</summary>
    public void Forget() => _register = null;
}
