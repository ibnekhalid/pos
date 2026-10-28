using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Restaurant.Client.Core;
using Restaurant.Client.Core.Platform;
using Restaurant.Client.UI.Services;

namespace Restaurant.Client.UI;

public static class UiServiceCollectionExtensions
{
    /// <summary>Shared client services for the screens. Hosts add platform services and the API HttpClient.</summary>
    public static IServiceCollection AddRestaurantUi(this IServiceCollection services)
    {
        services.AddRestaurantClientCore();
        services.AddScoped<UiInterop>();
        services.AddScoped<HtmlReceiptPrinter>();
        // The desktop app registers its own printer (ESC/POS) before calling this.
        services.TryAddScoped<IReceiptPrinter>(sp => sp.GetRequiredService<HtmlReceiptPrinter>());
        services.AddScoped<PrintService>();
        services.AddScoped<StockAlerts>();
        return services;
    }
}
