using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Restaurant.Domain.Constants;
using Restaurant.Domain.Entities;

namespace Restaurant.Infrastructure.Persistence;

/// <summary>
/// Idempotent start-up seeding that can't live in migrations. Receipt templates and the
/// PlatformAdmin role are migration seed data; the first platform administrator comes from
/// configuration (PlatformAdmin:Email / PlatformAdmin:Password), only when both are set.
/// </summary>
public static class DbInitializer
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var email = configuration["PlatformAdmin:Email"];
        var password = configuration["PlatformAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var users = services.GetRequiredService<UserManager<AppUser>>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DbInitializer));
        email = email.Trim().ToLowerInvariant();

        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new AppUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Platform administrator" };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                logger.LogError("Could not create the platform administrator: {Errors}", string.Join("; ", created.Errors.Select(e => e.Description)));
                return;
            }
            logger.LogInformation("Created platform administrator {UserId}", user.Id);
        }

        if (!await users.IsInRoleAsync(user, SystemRoleKeys.PlatformAdminRole))
        {
            await users.AddToRoleAsync(user, SystemRoleKeys.PlatformAdminRole);
        }
    }
}
