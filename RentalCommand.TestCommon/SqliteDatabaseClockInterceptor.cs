using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RentalCommand.TestCommon;

public sealed class SqliteDatabaseClockInterceptor : DbConnectionInterceptor
{
    public static readonly SqliteDatabaseClockInterceptor Instance = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Register(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Register(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void Register(DbConnection connection)
    {
        if (connection is SqliteConnection sqlite)
        {
            sqlite.CreateFunction("clock_timestamp", () => DateTime.UtcNow);
            sqlite.CreateFunction("rc_business_date", (long _) => DateTime.UtcNow.ToString("yyyy-MM-dd"));
        }
    }
}
