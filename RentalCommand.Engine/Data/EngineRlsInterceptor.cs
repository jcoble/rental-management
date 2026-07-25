using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Data.Security;

namespace RentalCommand.Engine.Data;

/// <summary>
/// Verifies that every Engine connection is the restricted direct-login Engine role. RLS recognizes
/// the Engine by immutable <c>session_user</c>; no mutable admin GUC or runtime role assumption can
/// grant cross-workspace authority.
/// </summary>
public sealed class EngineRlsInterceptor : DbConnectionInterceptor
{
    internal const string RuntimeRole = DatabaseRuntimeIdentity.EngineRole;
    internal const string SessionInitializationSql =
        "SELECT set_config('app.current_portfolio_id', '', false), " +
        "set_config('app.auth_session_id', '', false), " +
        "set_config('app.current_user_id', '', false), " +
        "set_config('app.current_access_context_id', '', false), " +
        "set_config('app.access_revision', '', false);";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => SetAdminSession(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => await SetAdminSessionAsync(connection, cancellationToken);

    private static void SetAdminSession(DbConnection connection)
    {
        DatabaseRuntimeIdentity.ValidateOpenedConnection(connection, RuntimeRole);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SessionInitializationSql;
        cmd.ExecuteNonQuery();
    }

    private static async Task SetAdminSessionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await DatabaseRuntimeIdentity.ValidateOpenedConnectionAsync(connection, RuntimeRole, cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SessionInitializationSql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
