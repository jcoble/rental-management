using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RentalCommand.Api.Data;

/// <summary>
/// Sets the per-connection PostgreSQL session variables that the Row-Level Security (RLS) policies
/// read, on every connection the API opens. This is the defense-in-depth backstop behind the
/// application-layer <c>.Where(x =&gt; x.PortfolioId == portfolioId)</c> filters (audit M-1): even a
/// controller/report/join that forgets the predicate cannot read or write another portfolio's rows,
/// because the policies filter at the database layer.
///
/// <para>The session variables are:
/// <list type="bullet">
///   <item><c>app.current_portfolio_id</c> — the authenticated caller's <c>portfolioId</c> claim
///   (an int; parsed defensively so a non-numeric value can never be interpolated into SQL).</item>
///   <item><c>app.is_admin</c> — the legacy GUC name retained by the current policies, set to
///   <c>true</c> only while server-owned code explicitly enters Platform or Background actor mode.
///   Customer Workspace Administrator roles and missing workspace context never bypass RLS.</item>
/// </list>
/// </para>
///
/// <para>RLS is <c>FORCE</c>d on every portfolio-scoped table, so the policies fire even for the
/// table owner — but PostgreSQL still exempts <c>SUPERUSER</c>/<c>BYPASSRLS</c> roles. For the
/// backstop to actually bite at runtime the API must connect as the dedicated, non-superuser
/// <c>rentalcommand_api</c> login role created by the RLS migration (see the connection-string note
/// in the deploy compose files). When it connects as a superuser the policies are silently bypassed
/// and only the app-layer filters remain.</para>
/// </summary>
public sealed class RlsConnectionInterceptor : DbConnectionInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IRlsActorModeAccessor _actorModeAccessor;

    public RlsConnectionInterceptor(
        IHttpContextAccessor httpContextAccessor,
        IRlsActorModeAccessor actorModeAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
        _actorModeAccessor = actorModeAccessor;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => SetSessionVariables(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => await SetSessionVariablesAsync(connection, cancellationToken);

    private void SetSessionVariables(DbConnection connection)
    {
        var (portfolioId, isAdmin) = GetContextValues();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildSql(portfolioId, isAdmin);
        cmd.ExecuteNonQuery();
    }

    private async Task SetSessionVariablesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var (portfolioId, isAdmin) = GetContextValues();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildSql(portfolioId, isAdmin);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // portfolioId is an int (validated) and isAdmin is a bool — no SQL-injection surface.
    private static string BuildSql(int portfolioId, bool isAdmin) =>
        $"SET app.current_portfolio_id = '{portfolioId}'; SET app.is_admin = '{(isAdmin ? "true" : "false")}';";

    internal (int portfolioId, bool isAdmin) GetContextValues()
    {
        if (_actorModeAccessor.Current is RlsActorMode.Platform or RlsActorMode.Background)
        {
            return (0, true);
        }

        var httpContext = _httpContextAccessor.HttpContext;

        // An absent request is not authority. Background/startup code must enter an explicit scope.
        if (httpContext is null)
        {
            return (0, false);
        }

        var portfolioClaim = httpContext.User.FindFirst("portfolioId");

        // Parse as int so a non-numeric claim can never reach the SQL string.
        var portfolioId = 0;
        if (portfolioClaim is not null)
        {
            int.TryParse(portfolioClaim.Value, out portfolioId);
        }

        return (portfolioId, false);
    }
}
