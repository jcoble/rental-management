using System.Data;
using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Data;

/// <summary>
/// Applies EF Core migrations under a PostgreSQL session advisory lock so the API and the Engine —
/// which both self-migrate on startup — never run migrations concurrently. Without the lock, two
/// processes applying the same fresh migration batch race and one crashes with e.g.
/// <c>42701: column "X" of relation "Y" already exists</c>. With it, the second process blocks until
/// the first finishes, then runs <see cref="RelationalDatabaseFacadeExtensions.MigrateAsync"/> and
/// finds nothing pending.
/// </summary>
public static class DatabaseMigrator
{
    // Distinct from the Engine's single-instance worker advisory lock (59484).
    private const long MigrationLockKey = 59485;

    public static async Task MigrateWithLockAsync(RentalCommandDbContext db, CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            // Session-level advisory lock held on this connection for the duration of the migration.
            await using (var lockCmd = connection.CreateCommand())
            {
                lockCmd.CommandText = $"SELECT pg_advisory_lock({MigrationLockKey})";
                await lockCmd.ExecuteNonQueryAsync(ct);
            }

            try
            {
                // MigrateAsync runs on this same (already-open) connection, so the advisory lock is held
                // throughout and EF won't close the connection it didn't open.
                await db.Database.MigrateAsync(ct);
            }
            finally
            {
                await using var unlockCmd = connection.CreateCommand();
                unlockCmd.CommandText = $"SELECT pg_advisory_unlock({MigrationLockKey})";
                await unlockCmd.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }
}
