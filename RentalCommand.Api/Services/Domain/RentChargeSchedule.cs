namespace RentalCommand.Api.Services.Domain;

public readonly record struct RentChargePeriod(
    string PeriodKey,
    DateTime DueDate,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    bool IsPartial);

public static class RentChargeSchedule
{
    public static IReadOnlyList<RentChargePeriod> GetDuePeriods(
        DateTime leaseStart,
        DateTime leaseEnd,
        int rentDueDay,
        DateTime businessToday,
        int leadDays = 0,
        DateTime? generationStart = null)
    {
        var occupancyStart = leaseStart.Date;
        var endExclusive = leaseEnd.Date;
        var generationStartDate = (generationStart ?? occupancyStart).Date;
        var today = businessToday.Date;

        if (occupancyStart >= endExclusive || today < generationStartDate)
        {
            return [];
        }

        var cutoff = today;
        var currentDueDate = GetDueDate(today.Year, today.Month, rentDueDay);
        var leadWindowStart = currentDueDate.AddDays(-Math.Max(0, leadDays));
        if (today <= currentDueDate && today >= leadWindowStart)
        {
            cutoff = currentDueDate;
        }

        var cursor = new DateTime(generationStartDate.Year, generationStartDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonth = new DateTime(cutoff.Year, cutoff.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periods = new List<RentChargePeriod>();

        while (cursor <= lastMonth)
        {
            var monthStart = cursor;
            var monthEnd = cursor.AddMonths(1).AddDays(-1);
            var periodStart = MaxDate(occupancyStart, monthStart);
            var periodEnd = MinDate(endExclusive.AddDays(-1), monthEnd);
            var dueDate = GetDueDate(cursor.Year, cursor.Month, rentDueDay);
            if (periodStart <= periodEnd && periodEnd >= generationStartDate && dueDate <= cutoff)
            {
                var isPartial = periodStart > monthStart || periodEnd < monthEnd;
                periods.Add(new RentChargePeriod(
                    dueDate.ToString("yyyy-MM"),
                    dueDate,
                    periodStart,
                    periodEnd,
                    isPartial));
            }

            cursor = cursor.AddMonths(1);
        }

        return periods;
    }

    public static DateTime GetDueDate(int year, int month, int rentDueDay)
    {
        var dueDay = Math.Clamp(rentDueDay, 1, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, dueDay, 0, 0, 0, DateTimeKind.Utc);
    }

    private static DateTime MaxDate(DateTime left, DateTime right)
        => left >= right ? left : right;

    private static DateTime MinDate(DateTime left, DateTime right)
        => left <= right ? left : right;
}
