using FluentAssertions;
using RentalCommand.Core.Time;

namespace RentalCommand.Core.Tests.Time;

public class TimeProviderExtensionsTests
{
    /// <summary>Minimal fixed-instant provider so the extensions can be tested without a package dep.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void UtcNow_ReturnsProviderInstant_AsUtcKindDateTime()
    {
        var instant = new DateTimeOffset(2025, 3, 15, 8, 30, 0, TimeSpan.Zero);
        var sut = new FixedTimeProvider(instant);

        sut.UtcNow().Should().Be(instant.UtcDateTime);
        sut.UtcNow().Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void TodayUtc_ReturnsUtcCalendarDay()
    {
        // 00:30 UTC on Mar 15 is the UTC calendar day Mar 15 regardless of any local zone.
        var instant = new DateTimeOffset(2025, 3, 15, 0, 30, 0, TimeSpan.Zero);
        var sut = new FixedTimeProvider(instant);

        sut.TodayUtc().Should().Be(new DateOnly(2025, 3, 15));
    }

    [Fact]
    public void NowOffset_ReturnsProviderOffset()
    {
        var instant = new DateTimeOffset(2025, 3, 15, 8, 30, 0, TimeSpan.Zero);
        var sut = new FixedTimeProvider(instant);

        sut.NowOffset().Should().Be(instant);
    }

    [Fact]
    public void BusinessToday_UsesConfiguredBusinessTimeZone()
    {
        var instant = new DateTimeOffset(2026, 8, 1, 2, 30, 0, TimeSpan.Zero);
        var sut = new FixedTimeProvider(instant);
        var tz = new FixedTimeZoneProvider(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        sut.BusinessToday(tz).Should().Be(new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc));
    }

    private sealed class FixedTimeZoneProvider(TimeZoneInfo timeZone) : IAppTimeZoneProvider
    {
        public TimeZoneInfo BusinessTimeZone => timeZone;
    }
}
