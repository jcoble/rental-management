using FluentAssertions;
using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Tests.Domain;

public sealed class OwnerStatementPeriodTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 10, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void MissingPeriodDefaultsToThePreviousCompletedMonth()
    {
        var period = OwnerStatementPeriod.Resolve(null, null, NowUtc);

        period.Kind.Should().Be(OwnerStatementPeriodKind.Monthly);
        period.StartOn.Should().Be(new DateOnly(2026, 7, 1));
        period.EndOnExclusive.Should().Be(new DateOnly(2026, 8, 1));
        period.Label.Should().Be("July 2026");
    }

    [Fact]
    public void MonthlyAndQuarterlyUseTheSelectedAsOfMonth()
    {
        var monthly = OwnerStatementPeriod.Resolve("monthly", new DateOnly(2026, 5, 18), NowUtc);
        var quarterly = OwnerStatementPeriod.Resolve("quarterly", new DateOnly(2026, 5, 18), NowUtc);

        monthly.StartOn.Should().Be(new DateOnly(2026, 5, 1));
        monthly.EndOnExclusive.Should().Be(new DateOnly(2026, 6, 1));
        quarterly.StartOn.Should().Be(new DateOnly(2026, 4, 1));
        quarterly.EndOnExclusive.Should().Be(new DateOnly(2026, 7, 1));
        quarterly.Label.Should().Be("Q2 2026");
    }

    [Fact]
    public void YearToDateIncludesTheAsOfDateThroughTheFollowingMidnight()
    {
        var period = OwnerStatementPeriod.Resolve("year-to-date", new DateOnly(2026, 5, 18), NowUtc);

        period.Kind.Should().Be(OwnerStatementPeriodKind.YearToDate);
        period.StartOn.Should().Be(new DateOnly(2026, 1, 1));
        period.EndOnExclusive.Should().Be(new DateOnly(2026, 5, 19));
        period.EndOn.Should().Be(new DateOnly(2026, 5, 18));
    }

    [Fact]
    public void LegacyAnnualYearRemainsAnExplicitCompatibilityPeriod()
    {
        var period = OwnerStatementPeriod.Resolve("annual", null, NowUtc, 2025);

        period.Kind.Should().Be(OwnerStatementPeriodKind.Annual);
        period.StartOn.Should().Be(new DateOnly(2025, 1, 1));
        period.EndOnExclusive.Should().Be(new DateOnly(2026, 1, 1));
    }
}
