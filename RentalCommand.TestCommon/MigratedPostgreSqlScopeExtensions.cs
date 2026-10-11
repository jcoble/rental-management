using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;

namespace RentalCommand.TestCommon;

public static class MigratedPostgreSqlScopeExtensions
{
    public static async Task ActivateApiScopeAsync(
        this MigratedPostgreSqlTestContext context,
        WorkspaceReadScope scope,
        CancellationToken cancellationToken = default)
    {
        await context.Db.Database.OpenConnectionAsync(cancellationToken);
        await context.Db.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {scope.AccessRevision.ToString()}, false);
            """, cancellationToken);
    }
}
