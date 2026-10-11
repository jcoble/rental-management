using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Api.Services;

internal static class AtomicCommandDbClock
{
    public static async Task<DateTime> ReadDatabaseClockUtcAsync(
        RentalCommandDbContext db,
        CancellationToken ct = default)
    {
        var rows = await db.QuerySqlAsync<DateTime>(
            $"SELECT clock_timestamp() AS \"Value\"",
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
