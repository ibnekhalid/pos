using FluentAssertions;
using Restaurant.Domain.Entities;

namespace Restaurant.Tests.Unit;

/// <summary>The rule behind T4: a session running past midnight keeps its business date.</summary>
public class BusinessDateTests
{
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    [Theory]
    [InlineData("2026-11-02T10:00:00+05:00", "2026-11-02")] // Monday morning
    [InlineData("2026-11-02T23:59:00+05:00", "2026-11-02")]
    [InlineData("2026-11-03T02:00:00+05:00", "2026-11-02")] // 2 AM Tuesday still counts as Monday
    [InlineData("2026-11-03T05:59:00+05:00", "2026-11-02")]
    [InlineData("2026-11-03T06:00:00+05:00", "2026-11-03")] // day starts at 06:00
    [InlineData("2026-11-02T21:30:00+00:00", "2026-11-02")] // UTC input: 02:30 local on the 3rd
    public void Business_date_uses_local_time_and_the_day_start_hour(string moment, string expected)
    {
        var branch = new Branch { DayStartHour = 6 };
        branch.BusinessDateFor(DateTimeOffset.Parse(moment), Karachi).Should().Be(DateOnly.Parse(expected));
    }

    [Fact]
    public void Midnight_day_start_uses_the_calendar_date()
    {
        var branch = new Branch { DayStartHour = 0 };
        branch.BusinessDateFor(DateTimeOffset.Parse("2026-11-03T00:30:00+05:00"), Karachi).Should().Be(new DateOnly(2026, 11, 3));
    }
}
