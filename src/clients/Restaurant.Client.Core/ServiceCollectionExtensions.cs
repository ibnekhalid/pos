using Microsoft.Extensions.DependencyInjection;
using Restaurant.Client.Core.Api;
using Restaurant.Client.Core.Auth;
using Restaurant.Client.Core.Localization;
using Restaurant.Client.Core.Platform;

namespace Restaurant.Client.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Client services shared by both hosts. The host registers HttpClient (with the API base address)
    /// and the Platform/ interfaces first.
    /// </summary>
    public static IServiceCollection AddRestaurantClientCore(this IServiceCollection services)
    {
        // Singletons: one session per running app (the browser tab or the desktop window).
        services.AddSingleton<SessionStore>();
        services.AddSingleton<ApiClient>();
        services.AddSingleton<RestaurantApi>();
        services.AddSingleton<UserContext>();
        services.AddSingleton<BrandingState>();
        services.AddSingleton<Localizer>();
        services.AddSingleton<DeviceRegistration>();
        services.AddSingleton<LiveUpdates>();
        services.AddSingleton<Offline.OfflineStore>();
        services.AddSingleton<Offline.OfflineSync>();
        services.AddSingleton<Offline.OfflinePin>();
        services.AddSingleton<ClientStartup>();
        return services;
    }
}

/// <summary>Loads language, session and device registration before the first screen renders.</summary>
public class ClientStartup(IConnectivity connectivity, Localizer localizer, SessionStore session, DeviceRegistration device,
    UserContext user, BrandingState branding)
{
    private bool _initialized;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        await connectivity.InitializeAsync();
        await localizer.InitializeAsync();
        await session.InitializeAsync();
        await device.InitializeAsync();

        if (session.IsSignedIn)
        {
            _ = branding.LoadAsync();
            try
            {
                await user.LoadAsync();
            }
            catch
            {
                // Offline or the session ended: the layout handles it on first render.
            }
        }
    }
}
