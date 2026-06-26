namespace RentalCommand.Api.Services.Domain;

public readonly record struct RentChargePeriod(string PeriodKey, DateTime DueDate);

public static class RentChargeSchedule
{
    public static IReadOnlyList<RentChargePeriod> GetDuePeriods(
        DateTime leaseStart,
        DateTime leaseEnd,
        int rentDueDay,
        DateTime businessToday,
        int leadDays = 0)
    {
        var start = leaseStart.Date;
        var endExclusive = leaseEnd.Date;
        var today = businessToday.Date;

        if (start >= endExclusive || today < start)
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

        var cursor = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonth = new DateTime(cutoff.Year, cutoff.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periods = new List<RentChargePeriod>();

        while (cursor <= lastMonth)
        {
            var dueDate = GetDueDate(cursor.Year, cursor.Month, rentDueDay);
            if (dueDate >= start && dueDate < endExclusive && dueDate <= cutoff)
            {
                periods.Add(new RentChargePeriod(dueDate.ToString("yyyy-MM"), dueDate));
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
}
