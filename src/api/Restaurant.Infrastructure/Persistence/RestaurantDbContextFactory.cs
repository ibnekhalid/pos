using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Restaurant.Application.Common.Security;

namespace Restaurant.Infrastructure.Persistence;

/// <summary>
/// Used by `dotnet ef` (migrations add / database update). The deploy script passes the production
/// connection string in RESTAURANT_CONNECTION_STRING; locally it defaults to the docker/ dev database.
/// </summary>
public class RestaurantDbContextFactory : IDesignTimeDbContextFactory<RestaurantDbContext>
{
    public RestaurantDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("RESTAURANT_CONNECTION_STRING")
            ?? "Host=localhost;Port=5434;Database=restaurant;Username=restaurant;Password=restaurant_dev";

        var options = new DbContextOptionsBuilder<RestaurantDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new RestaurantDbContext(options, new TenantContext());
    }
}
