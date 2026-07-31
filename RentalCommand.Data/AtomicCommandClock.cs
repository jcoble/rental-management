using RentalCommand.Core.Atomic;
using RentalCommand.Core.Time;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data;

public static class AtomicCommandClock
{
    public static async Task<DateOnly> ReadBusinessDateAsync(
        RentalCommandDbContext db,
        int portfolioId,
        CancellationToken ct = default)
    {
        var rows = await db.QuerySqlAsync<DateOnly>(
            $"SELECT rc_business_date({portfolioId}) AS \"Value\"",
            ct);
        return rows.Single();
    }

    public static async Task<AtomicCommandTimes> ReadCommandTimesAsync(
        RentalCommandDbContext db,
        int portfolioId,
        CancellationToken ct = default)
    {
        var rows = await db.QuerySqlAsync<AtomicCommandTimes>(
            $"""
             SELECT clock_timestamp() AS "WallClockUtc",
                    rc_business_date({portfolioId}) AS "BusinessDate",
                    rc_effective_now_utc({portfolioId}) AS "EffectiveNowUtc"
             """,
            ct);
        return rows.Single();
    }
}
