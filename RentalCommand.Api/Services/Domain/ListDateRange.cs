using RentalCommand.Core.Time;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Shared boundary math for the grid date-range filter (<see cref="DTOs.ListQuery.From"/> /
/// <see cref="DTOs.ListQuery.To"/>). Every list service uses this so the filters are consistent and,
/// crucially, translate to ONE SQL predicate against the target column — never materialized-then-filtered.
///
/// The range is half-open <c>[fromUtc, toUtcExclusive)</c>. Callers apply it inline, e.g.
/// <code>
/// var (from, to) = ListDateRange.UtcDay(query.From, query.To);
/// if (from is { } f) q = q.Where(p => p.PaidDate >= f);
/// if (to   is { } t) q = q.Where(p => p.PaidDate &lt; t);
/// </code>
/// </summary>
public static class ListDateRange
{
    /// <summary>
    /// For date-only calendar columns stored at UTC-midnight (<c>Payment.PaidDate</c>,
    /// <c>Expense.PaidAt</c>/<c>IncurredAt</c>, <c>DueDate</c>): filter in UTC-day terms —
    /// <c>&gt;= from</c> and <c>&lt; to + 1 day</c>. These are timezone-less calendar dates, so no zone
    /// conversion; this matches <c>AccountingService.GetTransactionsAsync</c> and the web's UTC-pinned
    /// date-only handling, keeping the money grids in agreement with the Accounting grid.
    /// </summary>
    public static (DateTime? FromUtc, DateTime? ToUtcExclusive) UtcDay(DateTime? from, DateTime? to) =>
        (from?.ToUtc(),
         to.HasValue ? to.Value.ToUtc().Date.AddDays(1) : null);

    /// <summary>
    /// For true-instant columns with a real time-of-day (<c>AtomicAuditLog.Timestamp</c>,
    /// <c>Appointment.ScheduledStart</c>, <c>CreatedAt</c>): convert the picked <c>[from, to]</c> day
    /// boundaries from the business timezone to UTC instants, half-open
    /// <c>[startOfDay(from), startOfDay(to + 1 day))</c>. So "this month" means the landlord's LOCAL
    /// month — an 11pm-ET-Dec-31 event stays in December, not the next UTC year.
    /// </summary>
    public static (DateTime? FromUtc, DateTime? ToUtcExclusive) BusinessTzDay(
        DateTime? from, DateTime? to, TimeZoneInfo businessTimeZone)
    {
        DateTime? fromUtc = from.HasValue ? ToUtcStartOfDay(from.Value, businessTimeZone) : null;
        DateTime? toUtc = to.HasValue ? ToUtcStartOfDay(to.Value.Date.AddDays(1), businessTimeZone) : null;
        return (fromUtc, toUtc);
    }

    // Midnight (start of day) of the given calendar date IN the business zone, expressed as a UTC instant.
    private static DateTime ToUtcStartOfDay(DateTime day, TimeZoneInfo tz) =>
        LocalDateTimeResolver.ConvertToUtc(DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified), tz);
}
