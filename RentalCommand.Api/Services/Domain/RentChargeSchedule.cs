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

        if (occupancyStart >= endExclusive)
        {
            return [];
        }

        // A lead window is a rolling date horizon, not a same-calendar-month rule. For example,
        // July 27 + five days must include an August 1 charge. It also lets a first partial period
        // become visible shortly before the lease begins without making it due before occupancy.
        var cutoff = today.AddDays(Math.Max(0, leadDays));
        if (cutoff < generationStartDate)
        {
            return [];
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
            // A partial first period cannot be due before either occupancy or the configured tracking
            // cutoff. This prevents a July 15 move-in from producing an already-overdue July 1 charge.
            var dueDate = MaxDate(
                GetDueDate(cursor.Year, cursor.Month, rentDueDay),
                MaxDate(periodStart, generationStartDate));
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
