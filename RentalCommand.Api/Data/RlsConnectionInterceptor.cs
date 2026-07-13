using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Auth;
using RentalCommand.Core.Authorization;
using RentalCommand.Data.Security;

namespace RentalCommand.Api.Data;

internal readonly record struct RlsSessionState(
    int PortfolioId,
    Guid? AuthSessionId = null,
    int? UserId = null,
    int? AccessContextId = null,
    long? AccessRevision = null);

/// <summary>
/// Sets PostgreSQL RLS coordinates from the middleware-validated canonical access context and
/// verifies that the configured credential is the restricted direct-login API role. The database
/// revalidates these coordinates against active session/access rows; changing GUC text alone cannot
/// widen visibility.
/// </summary>
public sealed class RlsConnectionInterceptor : DbConnectionInterceptor
{
    internal const string RuntimeRole = DatabaseRuntimeIdentity.ApiRole;

    private readonly IHttpContextAccessor _httpContextAccessor;
    public RlsConnectionInterceptor(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        SetSessionVariables(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        await SetSessionVariablesAsync(connection, cancellationToken);

    private void SetSessionVariables(DbConnection connection)
    {
        DatabaseRuntimeIdentity.ValidateOpenedConnection(connection, RuntimeRole);
        var state = ResolveSessionState(_httpContextAccessor.HttpContext);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildSql(state);
        cmd.ExecuteNonQuery();
    }

    private async Task SetSessionVariablesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await DatabaseRuntimeIdentity.ValidateOpenedConnectionAsync(connection, RuntimeRole, cancellationToken);
        var state = ResolveSessionState(_httpContextAccessor.HttpContext);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildSql(state);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static RlsSessionState ResolveSessionState(HttpContext? httpContext)
    {
        if (httpContext is not null &&
            httpContext.Items.TryGetValue(CanonicalAccessContextHttpItem.Key, out var value) &&
            value is ActiveAccessContext active &&
            active.PortfolioId > 0)
        {
            return new RlsSessionState(
                active.PortfolioId,
                active.SessionId,
                active.UserId,
                active.AccessContextId,
                active.AccessRevision);
        }

        // Authentication validates the JWT signature before this interceptor runs. Supplying the
        // compact coordinates here breaks the resolver/RLS bootstrap cycle; PostgreSQL still joins
        // them to live canonical rows, so stale or forged coordinates see no portfolio data.
        if (httpContext?.User.Identity?.IsAuthenticated == true &&
            Guid.TryParse(httpContext.User.FindFirstValue("sid"), out var sessionId) &&
            int.TryParse(httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) &&
            int.TryParse(httpContext.User.FindFirstValue("ctx"), out var accessContextId) &&
            long.TryParse(httpContext.User.FindFirstValue("ar"), out var accessRevision))
        {
            return new RlsSessionState(
                PortfolioId: 0,
                sessionId,
                userId,
                accessContextId,
                accessRevision);
        }

        // This also permits the canonical resolver to read the non-portfolio authority tables before
        // it has validated the context, while every portfolio-scoped table remains invisible.
        return new RlsSessionState(0);
    }

    internal static string BuildSql(RlsSessionState state) =>
        $"SELECT set_config('app.current_portfolio_id', '{state.PortfolioId}', false), " +
        $"set_config('app.auth_session_id', '{state.AuthSessionId?.ToString() ?? string.Empty}', false), " +
        $"set_config('app.current_user_id', '{state.UserId?.ToString() ?? string.Empty}', false), " +
        $"set_config('app.current_access_context_id', '{state.AccessContextId?.ToString() ?? string.Empty}', false), " +
        $"set_config('app.access_revision', '{state.AccessRevision?.ToString() ?? string.Empty}', false);";
}
