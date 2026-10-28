using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Infrastructure.Persistence;
using Restaurant.Infrastructure.Receipts;
using Restaurant.Infrastructure.Services;

namespace Restaurant.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<TenantSaveChangesInterceptor>();

        // Read lazily, so a test host's configuration overrides apply.
        services.AddDbContext<RestaurantDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
                ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
            options.UseNpgsql(connectionString);
            options.AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>());
        });
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<RestaurantDbContext>());

        services.AddOptions<FileStorageSettings>().BindConfiguration("FileStorage");
        services.AddSingleton<IFileStorageService, LocalFileStorageService>();

        services.AddOptions<PasswordResetSettings>().BindConfiguration("PasswordReset");
        services.AddScoped<IPasswordResetNotifier, LoggingPasswordResetNotifier>();

        services.AddSingleton<IReceiptPdfRenderer, QuestPdfReceiptRenderer>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
