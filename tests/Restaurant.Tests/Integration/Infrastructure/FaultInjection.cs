using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Restaurant.Infrastructure.Persistence;

namespace Restaurant.Tests.Integration.Infrastructure;

/// <summary>
/// Fails the database command that inserts into <see cref="FailAfterInsertInto"/> <em>after</em> it has run,
/// so the failure lands in the middle of the operation's transaction (T15).
/// </summary>
public sealed class FaultInjector : DbCommandInterceptor
{
    public volatile string? FailAfterInsertInto;

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    private void Check(DbCommand command)
    {
        if (FailAfterInsertInto is { } table && command.CommandText.Contains($"INSERT INTO \"{table}\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Forced failure after inserting into {table}.");
        }
    }
}

public class FaultApiFactory : ApiFactory
{
    public FaultInjector Faults { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.ConfigureDbContext<RestaurantDbContext>(options => options.AddInterceptors(Faults)));
    }

    /// <summary>Row counts straight from the database, past the tenant filters.</summary>
    public async Task<int> CountAsync<T>(Func<IQueryable<T>, IQueryable<T>>? where = null) where T : class
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantDbContext>();
        var query = db.Set<T>().IgnoreQueryFilters();
        return await (where is null ? query : where(query)).CountAsync();
    }
}
