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
/// <para>This sets the session variables on the worker's pooled connections via the same GUC names
/// the API interceptor and the policies use, so behaviour is identical whether the Engine connects
/// as the dedicated <c>rentalcommand_api</c> role or as the table-owner/superuser. (As a superuser
/// the policies are bypassed regardless; setting <c>is_admin</c> keeps the contract explicit and
/// correct if the Engine is ever pointed at a non-superuser role.)</para>
/// </summary>
public sealed class EngineRlsInterceptor : DbConnectionInterceptor
{
    private const string Sql = "SET app.current_portfolio_id = '0'; SET app.is_admin = 'true';";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => SetAdminSession(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => await SetAdminSessionAsync(connection, cancellationToken);

    private static void SetAdminSession(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task SetAdminSessionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
