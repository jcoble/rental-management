using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Services;

public static class ProrationCalculator
{
    public static decimal Prorate(
        decimal monthlyAmount,
        DateTime periodStart,
        DateTime periodEndInclusive,
        ProrationConvention convention)
    {
        if (monthlyAmount <= 0m)
        {
            return 0m;
        }

        var start = periodStart.Date;
        var end = periodEndInclusive.Date;
        if (end < start)
        {
            return 0m;
        }

        var fullMonthStart = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var fullMonthEnd = fullMonthStart.AddMonths(1).AddDays(-1);
        if (start == fullMonthStart && end == fullMonthEnd)
        {
            return monthlyAmount;
        }

        var amount = convention switch
        {
            ProrationConvention.ThirtyDay => monthlyAmount * ThirtyDayBillableDays(start, end) / 30m,
            _ => monthlyAmount * ActualBillableDays(start, end) / DateTime.DaysInMonth(start.Year, start.Month),
        };

        return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    private static int ActualBillableDays(DateTime start, DateTime end)
        => (end - start).Days + 1;

    private static int ThirtyDayBillableDays(DateTime start, DateTime end)
    {
        var lastDay = DateTime.DaysInMonth(start.Year, start.Month);
        var startDay = Math.Min(start.Day, 30);
        var endDay = end.Day == lastDay ? 30 : Math.Min(end.Day, 30);
        return Math.Max(0, endDay - startDay + 1);
    }
}
