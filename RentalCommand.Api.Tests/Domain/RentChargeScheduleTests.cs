using FluentAssertions;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Tests.Domain;

public sealed class RentChargeScheduleTests
{
    [Fact]
    public void GetDuePeriods_FirstOfMonthDueDate_UsesLeadWindowAcrossMonthBoundary()
    {
        var periods = RentChargeSchedule.GetDuePeriods(
            Date(2026, 1, 1),
            Date(2027, 1, 1),
            rentDueDay: 1,
            businessToday: Date(2026, 7, 27),
            leadDays: 5);

        periods.Should().ContainSingle(p => p.PeriodKey == "2026-08");
        periods.Single(p => p.PeriodKey == "2026-08").DueDate.Should().Be(Date(2026, 8, 1));
    }

    [Fact]
    public void GetDuePeriods_FirstPartialMonth_IsNeverDueBeforeLeaseBegins()
    {
        var periods = RentChargeSchedule.GetDuePeriods(
            Date(2026, 7, 15),
            Date(2027, 7, 1),
            rentDueDay: 1,
            businessToday: Date(2026, 7, 20),
            generationStart: Date(2026, 7, 15));

        periods.Should().ContainSingle();
        periods[0].PeriodKey.Should().Be("2026-07");
        periods[0].PeriodStart.Should().Be(Date(2026, 7, 15));
        periods[0].DueDate.Should().Be(Date(2026, 7, 15));
    }

    [Fact]
    public void GetDuePeriods_FirstPartialMonth_UsesLeadWindowBeforeLeaseBegins()
    {
        var periods = RentChargeSchedule.GetDuePeriods(
            Date(2026, 7, 15),
            Date(2027, 7, 1),
            rentDueDay: 1,
            businessToday: Date(2026, 7, 10),
            leadDays: 5,
            generationStart: Date(2026, 7, 15));

        periods.Should().ContainSingle();
        periods[0].PeriodKey.Should().Be("2026-07");
        periods[0].DueDate.Should().Be(Date(2026, 7, 15));
    }

    [Fact]
    public void GetDuePeriods_FirstPartialMonth_WithLaterDueDay_LeadsFromActualDueDate()
    {
        var tooEarly = RentChargeSchedule.GetDuePeriods(
            Date(2026, 7, 15),
            Date(2027, 7, 1),
            rentDueDay: 20,
            businessToday: Date(2026, 7, 10),
            leadDays: 5,
            generationStart: Date(2026, 7, 15));
        var inLeadWindow = RentChargeSchedule.GetDuePeriods(
            Date(2026, 7, 15),
            Date(2027, 7, 1),
            rentDueDay: 20,
            businessToday: Date(2026, 7, 15),
            leadDays: 5,
            generationStart: Date(2026, 7, 15));

        tooEarly.Should().BeEmpty();
        inLeadWindow.Should().ContainSingle();
        inLeadWindow[0].DueDate.Should().Be(Date(2026, 7, 20));
    }

    private static DateTime Date(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
