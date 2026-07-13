using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace RentalCommand.Api.Data;

/// <summary>
/// Applies the workspace scope carried by a locked invitation to its current transaction. The
/// invitation token is resolved before this runs; callers cannot supply an arbitrary portfolio.
/// </summary>
internal static class WorkspaceInvitationActivationRlsScope
{
    internal static async Task ApplyAsync(
        DatabaseFacade database,
        int portfolioId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Invitation activation portfolio scope requires an active database transaction.");
        }

        await database.ExecuteSqlInterpolatedAsync(BuildCommand(portfolioId), ct);
    }

    internal static FormattableString BuildCommand(int portfolioId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        var portfolioIdText = portfolioId.ToString(CultureInfo.InvariantCulture);

        return $"""
            SELECT
              set_config('app.current_portfolio_id', {portfolioIdText}, true),
              set_config('app.is_admin', 'false', true),
              set_config('app.rls_bypass_reason', '', true);
            """;
    }
}
