namespace Restaurant.Client.Core.Platform;

// What differs between the web app and the Windows desktop app. Each host registers its own
// implementations; screens depend only on these interfaces (PLAN.md section 11).

/// <summary>Which host the shared screens are running in.</summary>
public interface IPlatformInfo
{
    /// <summary>"Web" or "Windows".</summary>
    string Name { get; }

    /// <summary>True in the desktop app: PIN sign-in, auto-lock, silent printing, cash drawer.</summary>
    bool IsDesktop { get; }

    /// <summary>Suggested name when registering this computer ("FRONT-COUNTER").</summary>
    string DeviceName { get; }

    /// <summary>The installed app's version ("0.3") for the update check; null where nothing is installed (web).</summary>
    string? AppVersion => null;
}

/// <summary>Small persistent values (language, preferences). Web: localStorage. Desktop: files.</summary>
public interface IKeyValueStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
}

/// <summary>Secrets: the remembered session and the device token. Desktop: Windows Data Protection.</summary>
public interface ISecureStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
}

/// <summary>Whether the device thinks it is online. The API client's errors remain the truth.</summary>
public interface IConnectivity
{
    bool IsOnline { get; }
    event Action<bool>? Changed;
    Task InitializeAsync();
}

/// <summary>Full-screen / kiosk mode for counter screens (desktop window or browser full screen).</summary>
public interface IWindowService
{
    bool SupportsFullScreen { get; }
    Task ToggleFullScreenAsync();
}

public enum PrintTarget
{
    Receipt,
    KitchenTicket
}

/// <param name="Document">What to print, resolved by the server.</param>
/// <param name="HtmlElementId">The same document rendered on the page, for the browser print dialog.</param>
/// <param name="PrinterName">The Windows printer configured on the register (desktop raw printing); null = print dialog.</param>
public record PrintRequest(PrintTarget Target, Restaurant.Contracts.Receipts.ReceiptDocumentDto Document, string HtmlElementId,
    string? PrinterName, bool OpenCashDrawer);

/// <summary>Web: the browser print dialog. Desktop: raw ESC/POS to the register's printer, else the dialog.</summary>
public interface IReceiptPrinter
{
    bool SupportsSilentPrinting { get; }
    Task PrintAsync(PrintRequest request, CancellationToken cancellationToken = default);
}
