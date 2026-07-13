using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RentalCommand.Engine.Data;

/// <summary>
/// Row-Level Security (RLS) interceptor for the background Engine. The Engine has no HTTP context and
/// legitimately operates across every portfolio (late-fee sweeps, autopay charges, the outbox drain,
/// notifications). It therefore always sets <c>app.is_admin = true</c>, which makes the
/// <c>tenant_isolation</c> policies' admin branch match and gives the worker unfiltered access — the
/// same model EdiPlatform's Engine uses.
///
/// <para>Each pooled connection first assumes the NOLOGIN <c>rentalcommand_engine</c> role, then
/// sets the session variables used by the RLS policies. The configured login credential therefore
/// retains owner authority only for the separate startup migration context.</para>
/// </summary>
public sealed class EngineRlsInterceptor : DbConnectionInterceptor
{
    internal const string RuntimeRole = "rentalcommand_engine";
    internal const string SessionInitializationSql =
        "SET ROLE rentalcommand_engine; SET app.current_portfolio_id = '0'; SET app.is_admin = 'true';";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => SetAdminSession(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => await SetAdminSessionAsync(connection, cancellationToken);

    private static void SetAdminSession(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SessionInitializationSql;
        cmd.ExecuteNonQuery();
    }

    private static async Task SetAdminSessionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SessionInitializationSql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
