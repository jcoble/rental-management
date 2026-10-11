using FluentAssertions;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Services;

namespace RentalCommand.Core.Tests;

public sealed class ProrationCalculatorTests
{
    [Fact]
    public void Prorate_ActualDays_UsesCalendarMonthLength()
    {
        var amount = ProrationCalculator.Prorate(
            3100m,
            Date(2026, 7, 15),
            Date(2026, 7, 31),
            ProrationConvention.ActualDays);

        amount.Should().Be(1700m);
    }

    [Fact]
    public void Prorate_ThirtyDay_TreatsMonthEndAsDayThirty()
    {
        var amount = ProrationCalculator.Prorate(
            3000m,
            Date(2026, 2, 15),
            Date(2026, 2, 28),
            ProrationConvention.ThirtyDay);

        amount.Should().Be(1600m);
    }

    [Fact]
    public void Prorate_FullMonth_ReturnsMonthlyAmount()
    {
        var amount = ProrationCalculator.Prorate(
            2750m,
            Date(2026, 8, 1),
            Date(2026, 8, 31),
            ProrationConvention.ThirtyDay);

        amount.Should().Be(2750m);
    }

    [Fact]
    public void ReadConvention_DefaultsToActualDaysForMissingOrBadSettings()
    {
        PortfolioProrationSettings.ReadConvention(null).Should().Be(ProrationConvention.ActualDays);
        PortfolioProrationSettings.ReadConvention("""{"prorationConvention":"bad"}""")
            .Should().Be(ProrationConvention.ActualDays);
    }

    [Fact]
    public void ReadConvention_ReadsThirtyDayString()
    {
        PortfolioProrationSettings.ReadConvention("""{"prorationConvention":"ThirtyDay"}""")
            .Should().Be(ProrationConvention.ThirtyDay);
    }

    private static DateTime Date(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
