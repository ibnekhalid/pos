using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Restaurant.Tests.Integration.Infrastructure;

/// <summary>A clock the test moves by hand (business days across midnight, T4).</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}

public class ClockApiFactory : ApiFactory
{
    // Monday 3 Nov 2026, 10:00 in Karachi (UTC+5).
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(5)));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }
}
