using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Restaurant.Tests.Integration.Infrastructure;

/// <summary>
/// One PostgreSQL container for the whole test run (Testcontainers' Ryuk removes it afterwards).
/// Each test class gets its own database in it, migrated by the API at start-up.
/// </summary>
public static class TestDatabase
{
    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(async () =>
    {
        var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        return container;
    });

    public static async Task<string> NewDatabaseAsync()
    {
        var container = await Container.Value;
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = "rp_" + Guid.NewGuid().ToString("N")[..16]
        }.ToString();
    }
}

public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string PlatformAdminEmail = "admin@platform.test";
    public const string PlatformAdminPassword = "platform-admin-pass";

    private readonly string _uploads = Path.Combine(Path.GetTempPath(), "rp-tests", Guid.NewGuid().ToString("N"));
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _connectionString = await TestDatabase.NewDatabaseAsync();
        _ = Server; // start the host now: runs the migrations and seeding
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("RateLimiting:AuthPermitLimit", "100000");
        builder.UseSetting("RateLimiting:UserPermitLimit", "100000");
        builder.UseSetting("RateLimiting:AnonymousPermitLimit", "100000");
        builder.UseSetting("Auth:RefreshReuseGraceSeconds", "0");
        builder.UseSetting("FileStorage:RootPath", _uploads);
        builder.UseSetting("PlatformAdmin:Email", PlatformAdminEmail);
        builder.UseSetting("PlatformAdmin:Password", PlatformAdminPassword);
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();
}
