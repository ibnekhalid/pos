using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Windowing;
using Restaurant.Client.Core.Platform;
using IConnectivity = Restaurant.Client.Core.Platform.IConnectivity;

namespace Restaurant.Desktop.Platform;

public class DesktopPlatformInfo : IPlatformInfo
{
    public string Name => "Windows";
    public bool IsDesktop => true;
    public string DeviceName => Environment.MachineName;

    /// <summary>ApplicationDisplayVersion from the .csproj (MAUI stamps it as the informational version).</summary>
    public string? AppVersion { get; } = typeof(DesktopPlatformInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
}

/// <summary>
/// The remembered session and the device token, encrypted with Windows Data Protection (DPAPI, current
/// user). Works for the unpackaged app, where MAUI's SecureStorage isn't available.
/// </summary>
public class DpapiSecureStore() : FileKeyValueStore(Path.Combine(FileSystem.AppDataDirectory, "secure")), ISecureStore
{
    private static readonly byte[] Entropy = "Restaurant POS desktop"u8.ToArray();

    protected override byte[] Encode(string value) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);

    protected override string? Decode(byte[] bytes)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // Written by another Windows user or profile: treat as nothing stored.
            return null;
        }
    }
}

/// <summary>Full-screen (kiosk-style) window for counter screens; F11 or the user menu toggles it.</summary>
public class DesktopWindowService : IWindowService
{
    public bool SupportsFullScreen => true;

    public Task ToggleFullScreenAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window)
        {
            return;
        }

        var appWindow = window.AppWindow;
        appWindow.SetPresenter(appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Overlapped
            : AppWindowPresenterKind.FullScreen);
    });

    /// <summary>
    /// A second window for the customer display. With a second monitor it opens there, full screen; with one
    /// monitor it opens as a normal window to drag where it's needed. A second tap brings the open one forward.
    /// </summary>
    public Task<bool> OpenCustomerDisplayAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        var app = Application.Current;
        if (app is null) return false;
        if (app.Windows.FirstOrDefault(w => w.Page is CustomerDisplayPage) is { } open)
        {
            app.ActivateWindow(open);
            return true;
        }

        var window = new Window(new CustomerDisplayPage()) { Title = "Customer display" };
        window.Created += (_, _) => window.Dispatcher.Dispatch(() => MoveToOtherScreen(window));
        app.OpenWindow(window);
        return true;
    });

    private static void MoveToOtherScreen(Window window)
    {
        if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native) return;
        var main = Application.Current?.Windows.FirstOrDefault(w => w != window)?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        var current = main is null ? null : DisplayArea.GetFromWindowId(main.AppWindow.Id, DisplayAreaFallback.Nearest);
        var other = DisplayArea.FindAll().FirstOrDefault(d => current is null || d.DisplayId.Value != current.DisplayId.Value);
        if (other is null) return; // one monitor: leave it where Windows put it

        native.AppWindow.Move(new Windows.Graphics.PointInt32(other.WorkArea.X, other.WorkArea.Y));
        native.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }
}
