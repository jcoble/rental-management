using FluentAssertions;
using RentalCommand.Core.Time;

namespace RentalCommand.Core.Tests.Time;

public sealed class LocalDateTimeResolverTests
{
    private static readonly TimeZoneInfo NewYork =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void SpringForwardGap_ShiftsForwardByTheDstGap()
    {
        var local = new DateTime(2027, 3, 14, 2, 30, 0, DateTimeKind.Unspecified);

        var utc = LocalDateTimeResolver.ConvertToUtc(local, NewYork);

        utc.Should().Be(new DateTime(2027, 3, 14, 7, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void FallBackAmbiguousTime_UsesTheEarlierDstInstant()
    {
        var local = new DateTime(2027, 11, 7, 1, 30, 0, DateTimeKind.Unspecified);

        var utc = LocalDateTimeResolver.ConvertToUtc(local, NewYork);

        utc.Should().Be(new DateTime(2027, 11, 7, 5, 30, 0, DateTimeKind.Utc));
    }
}
