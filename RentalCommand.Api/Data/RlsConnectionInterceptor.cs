using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Auth;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Data;

public enum RlsBypassReason
{
    StartupMigrationAndSeed = 1,
    RegistrationBootstrap = 2,
    BackgroundWorker = 3,
    PlatformOperation = 4,
}

public interface IRlsExecutionContext
{
    bool IsBypassActive { get; }
    IDisposable BeginBypass(RlsBypassReason reason);
}

/// <summary>
/// Explicit, async-flow-local lease for the few non-request operations that must cross workspace
/// boundaries. Merely having no HTTP context is never enough to bypass RLS.
/// </summary>
public sealed class RlsExecutionContext : IRlsExecutionContext
{
    private readonly AsyncLocal<BypassLease?> _current = new();

    public bool IsBypassActive => _current.Value is not null;

    public IDisposable BeginBypass(RlsBypassReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        var prior = _current.Value;
        var lease = new BypassLease(this, prior, reason);
        _current.Value = lease;
        return lease;
    }

    private sealed class BypassLease : IDisposable
    {
        private readonly RlsExecutionContext _owner;
        private readonly BypassLease? _prior;
        private bool _disposed;

        public BypassLease(RlsExecutionContext owner, BypassLease? prior, RlsBypassReason reason)
        {
            _owner = owner;
            _prior = prior;
            Reason = reason;
        }

        public RlsBypassReason Reason { get; }

        public void Dispose()
        {
            if (_disposed) return;
            if (!ReferenceEquals(_owner._current.Value, this))
            {
                throw new InvalidOperationException("RLS bypass leases must be disposed in LIFO order.");
            }

            _owner._current.Value = _prior;
            _disposed = true;
        }
    }
}

internal readonly record struct RlsSessionState(int PortfolioId, bool IsAdmin);

/// <summary>
/// Sets PostgreSQL RLS session state from the middleware-validated canonical access context. Legacy
/// portfolio and role claims are deliberately ignored. Authenticated requests without a validated
/// context receive a closed (portfolio 0, non-admin) session. Cross-workspace access is available
/// only while an explicit <see cref="IRlsExecutionContext"/> bypass lease is active.
/// </summary>
public sealed class RlsConnectionInterceptor : DbConnectionInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IRlsExecutionContext _executionContext;

    public RlsConnectionInterceptor(
        IHttpContextAccessor httpContextAccessor,
        IRlsExecutionContext executionContext)
    {
        _httpContextAccessor = httpContextAccessor;
        _executionContext = executionContext;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        SetSessionVariables(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        await SetSessionVariablesAsync(connection, cancellationToken);

    private void SetSessionVariables(DbConnection connection)
    {
        var state = ResolveSessionState(
            _httpContextAccessor.HttpContext,
            _executionContext.IsBypassActive);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildSql(state);
        cmd.ExecuteNonQuery();
    }

    private async Task SetSessionVariablesAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var state = ResolveSessionState(
            _httpContextAccessor.HttpContext,
            _executionContext.IsBypassActive);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildSql(state);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static RlsSessionState ResolveSessionState(HttpContext? httpContext, bool bypassActive)
    {
        if (bypassActive)
        {
            return new RlsSessionState(0, true);
        }

        if (httpContext is not null &&
            httpContext.Items.TryGetValue(CanonicalAccessContextHttpItem.Key, out var value) &&
            value is ActiveAccessContext active &&
            active.PortfolioId > 0)
        {
            return new RlsSessionState(active.PortfolioId, false);
        }

        // This also permits the canonical resolver to read the non-portfolio authority tables before
        // it has validated the context, while every portfolio-scoped table remains invisible.
        return new RlsSessionState(0, false);
    }

    private static string BuildSql(RlsSessionState state) =>
        $"SET app.current_portfolio_id = '{state.PortfolioId}'; " +
        $"SET app.is_admin = '{(state.IsAdmin ? "true" : "false")}';";
}
