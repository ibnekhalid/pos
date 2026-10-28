using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using Restaurant.Client.Core.Platform;
using Restaurant.Client.UI;
using Restaurant.Desktop.Platform;
using IConnectivity = Restaurant.Client.Core.Platform.IConnectivity;

namespace Restaurant.Desktop;

public static class MauiProgram
{
    // Production API (Nginx Proxy Manager → rp-api). Debug builds talk to the local dev API instead.
    private const string ProductionApi = "https://api.restaurant.dsnoopdoc.com/";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Counter PCs: open maximised.
        builder.ConfigureLifecycleEvents(events => events.AddWindows(windows => windows.OnWindowCreated(window =>
        {
            if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        })));

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton(new HttpClient { BaseAddress = new Uri(ApiBaseUrl), Timeout = TimeSpan.FromSeconds(60) });
        builder.Services.AddSingleton<IKeyValueStore, FileKeyValueStore>();
        builder.Services.AddSingleton<ISecureStore, DpapiSecureStore>();
        builder.Services.AddSingleton<IConnectivity, MauiConnectivity>();
        builder.Services.AddSingleton<IWindowService, DesktopWindowService>();
        builder.Services.AddSingleton<IPlatformInfo, DesktopPlatformInfo>();
        // Before AddRestaurantUi, so it replaces the default (print dialog) printer.
        builder.Services.AddScoped<IReceiptPrinter, DesktopReceiptPrinter>();
        builder.Services.AddRestaurantUi();

        return builder.Build();
    }

    private static string ApiBaseUrl
    {
        get
        {
#if DEBUG
            // RESTAURANT_API_URL overrides it, e.g. http://localhost:8097/ for the local production-image stack.
            var overrideUrl = Environment.GetEnvironmentVariable("RESTAURANT_API_URL");
            return string.IsNullOrWhiteSpace(overrideUrl) ? "http://localhost:5080/" : overrideUrl.TrimEnd('/') + "/";
#else
            return ProductionApi;
#endif
        }
    }
}
