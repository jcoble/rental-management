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
///   <item><c>app.is_admin</c> — <c>true</c> for platform-admin sessions, for background/migration
///   paths with no HTTP context, and for unauthenticated requests (register/login/health) that have
///   no portfolio scope. An admin context bypasses the policies' portfolio predicate.</item>
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

    public RlsConnectionInterceptor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
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

    private (int portfolioId, bool isAdmin) GetContextValues()
    {
        var httpContext = _httpContextAccessor.HttpContext;

        // No HTTP context = background service / startup migration → admin bypass.
        if (httpContext is null)
        {
            return (0, true);
        }

        var portfolioClaim = httpContext.User.FindFirst("portfolioId");
        var isAdminRole = httpContext.User.IsInRole("Admin");

        // Parse as int so a non-numeric claim can never reach the SQL string.
        var portfolioId = 0;
        if (portfolioClaim is not null)
        {
            int.TryParse(portfolioClaim.Value, out portfolioId);
        }

        // No portfolio claim = an unauthenticated request (register/login/health) or a non-portfolio
        // principal (platform admin, owner/tenant-only token) → bypass RLS at the DB layer. The app
        // layer still controls access; RLS is the defense-in-depth backstop for portfolio sessions.
        var isAdmin = isAdminRole || portfolioId == 0;

        return (portfolioId, isAdmin);
    }
}
