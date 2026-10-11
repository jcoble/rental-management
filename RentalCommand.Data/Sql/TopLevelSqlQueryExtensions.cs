using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace RentalCommand.Data.Atomic; // Shared SQL query-shape helpers.

/// <summary>
/// Executes PostgreSQL statements whose top-level WITH clause contains data-modifying CTEs.
/// EF query operators such as SingleAsync compose an outer SELECT, which PostgreSQL rejects for
/// data-modifying CTEs. Materializing the uncomposed raw query preserves one top-level statement.
/// </summary>
internal static class TopLevelSqlQueryExtensions
{
    public static async Task<TResult> SingleTopLevelResultAsync<TResult>(
        this DatabaseFacade database,
        string sql,
        object[] parameters,
        CancellationToken ct)
    {
        var rows = await database.SqlQueryRaw<TResult>(sql, parameters).ToListAsync(ct);
        if (rows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Top-level SQL result {typeof(TResult).Name} returned {rows.Count} rows instead of one.");
        }

        return rows[0];
    }

    public static async Task<TResult?> SingleOrDefaultTopLevelResultAsync<TResult>(
        this DatabaseFacade database,
        string sql,
        object[] parameters,
        CancellationToken ct)
    {
        var rows = await database.SqlQueryRaw<TResult>(sql, parameters).ToListAsync(ct);
        return rows.Count switch
        {
            0 => default,
            1 => rows[0],
            _ => throw new InvalidOperationException(
                $"Top-level SQL result {typeof(TResult).Name} returned {rows.Count} rows instead of at most one."),
        };
    }
}
