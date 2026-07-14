using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Data;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Security;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Proves that request-scoped PostgreSQL RLS coordinates cannot survive logical connections in the
/// API pool. The test intentionally disables Npgsql's reset-on-close safety net: every successful
/// logical open must therefore replace all five coordinates through <see cref="RlsConnectionInterceptor"/>.
/// </summary>
[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class RlsPooledConnectionScopeSafetyTests : IAsyncLifetime
{
    private const string ApiPassword = "pooled-rls-test-password";

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly HttpContextAccessor _httpContextAccessor = new();
    private MigratedPostgreSqlTestContext _owner = null!;
    private RlsConnectionInterceptor _rls = null!;
    private string _apiConnectionString = string.Empty;
    private ActiveAccessContext _scopeA = null!;
    private ActiveAccessContext _scopeB = null!;

    public RlsPooledConnectionScopeSafetyTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _owner = await _fixture.CreateContextAsync();
        _rls = new RlsConnectionInterceptor(_httpContextAccessor);

        await _owner.Db.Database.ExecuteSqlRawAsync(
            $"ALTER ROLE {DatabaseRuntimeIdentity.ApiRole} PASSWORD '{ApiPassword}'");

        var now = DateTime.UtcNow;
        var portfolioA = await _owner.Db.Portfolios.SingleAsync(row => row.Id == 1);
        var userA = await _owner.Db.Users.SingleAsync(row => row.Id == 1);
        var portfolioB = new Portfolio
        {
            Name = "Pooled RLS Workspace B",
            ManagementCompanyName = "Pooled RLS Workspace B",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var userB = User("pooled-rls-b@example.test", now);
        _owner.Db.AddRange(portfolioB, userB);
        await _owner.Db.SaveChangesAsync();

        var authorityA = Authority(userA, portfolioA.Id, now);
        var authorityB = Authority(userB, portfolioB.Id, now);
        _owner.Db.AddRange(
            authorityA.Context,
            authorityA.Membership,
            authorityA.Session,
            authorityB.Context,
            authorityB.Membership,
            authorityB.Session,
            Tenant(portfolioA.Id, "Workspace", "A", now),
            Tenant(portfolioB.Id, "Workspace", "B", now));
        await _owner.Db.SaveChangesAsync();

        _scopeA = Active(authorityA, userA.Id, portfolioA.Id);
        _scopeB = Active(authorityB, userB.Id, portfolioB.Id);

        var builder = new NpgsqlConnectionStringBuilder(_owner.ConnectionString)
        {
            Username = DatabaseRuntimeIdentity.ApiRole,
            Password = ApiPassword,
            Pooling = true,
            MinPoolSize = 1,
            MaxPoolSize = 1,
            Timeout = 5,
            CommandTimeout = 5,
            ApplicationName = $"rc-pooled-rls-{Guid.NewGuid():N}",

            // This deliberately makes the test harsher than the production default. If the
            // interceptor misses even one logical open, the previous request's GUCs remain live.
            NoResetOnClose = true,
        };
        _apiConnectionString = builder.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_apiConnectionString))
        {
            await using var pooledConnection = new NpgsqlConnection(_apiConnectionString);
            NpgsqlConnection.ClearPool(pooledConnection);
        }

        if (_owner is not null)
        {
            await _owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task OnePhysicalConnection_FailsClosedAcrossWorkspaceAnonymousAndCanceledInitialization()
    {
        var workspaceA = await ObserveAsync(HttpContextFor(_scopeA));
        workspaceA.PortfolioIds.Should().Equal(_scopeA.PortfolioId);
        workspaceA.SessionState.Should().Be(ExpectedState(_scopeA));

        // Cancellation before the RLS interceptor executes leaves workspace A's GUCs on this
        // deliberately non-resetting physical connection. The failed logical open must not make
        // those values visible to the next successful anonymous logical connection.
        var cancellation = new CancelConnectionInitializationInterceptor();
        _httpContextAccessor.HttpContext = new DefaultHttpContext();
        await using (var canceled = NewApiContext(cancellation, _rls))
        {
            var act = async () => await canceled.Database.OpenConnectionAsync();
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        cancellation.BackendProcessId.Should().Be(workspaceA.BackendProcessId,
            "MaxPoolSize=1 must route the canceled logical open through the same physical session");

        var anonymous = await ObserveAsync(new DefaultHttpContext());
        anonymous.BackendProcessId.Should().Be(workspaceA.BackendProcessId);
        anonymous.PortfolioIds.Should().BeEmpty(
            "an anonymous request must not inherit workspace A visibility after cancellation");
        anonymous.SessionState.Should().Be(new SessionState("0", "", "", "", ""));

        var workspaceB = await ObserveAsync(HttpContextFor(_scopeB));
        workspaceB.BackendProcessId.Should().Be(workspaceA.BackendProcessId);
        workspaceB.PortfolioIds.Should().Equal(_scopeB.PortfolioId);
        workspaceB.SessionState.Should().Be(ExpectedState(_scopeB));
    }

    private async Task<ConnectionObservation> ObserveAsync(HttpContext httpContext)
    {
        _httpContextAccessor.HttpContext = httpContext;
        await using var db = NewApiContext(_rls);
        await db.Database.OpenConnectionAsync();
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var portfolioIds = await db.Tenants
                .OrderBy(row => row.PortfolioId)
                .Select(row => row.PortfolioId)
                .ToListAsync();
            var state = await ReadSessionStateAsync(connection);
            return new ConnectionObservation(connection.ProcessID, portfolioIds, state);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private RentalCommandDbContext NewApiContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_apiConnectionString)
            .AddInterceptors(interceptors)
            .Options;
        return new RentalCommandDbContext(options);
    }

    private static async Task<SessionState> ReadSessionStateAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT current_setting('app.current_portfolio_id', true),
                   current_setting('app.auth_session_id', true),
                   current_setting('app.current_user_id', true),
                   current_setting('app.current_access_context_id', true),
                   current_setting('app.access_revision', true);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new SessionState(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4));
    }

    private static DefaultHttpContext HttpContextFor(ActiveAccessContext active)
    {
        var context = new DefaultHttpContext();
        context.Items[CanonicalAccessContextHttpItem.Key] = active;
        return context;
    }

    private static SessionState ExpectedState(ActiveAccessContext active) => new(
        active.PortfolioId.ToString(),
        active.SessionId.ToString(),
        active.UserId.ToString(),
        active.AccessContextId.ToString(),
        active.AccessRevision.ToString());

    private static ActiveAccessContext Active(AuthorityRows authority, int userId, int portfolioId) =>
        new(
            authority.Session.Id,
            userId,
            authority.Context.Id,
            portfolioId,
            authority.Context.AccessRevision,
            WorkspaceExperience.Management,
            authority.Membership.Id,
            WorkspaceExperience.Management);

    private static AuthorityRows Authority(ApplicationUser user, int portfolioId, DateTime now)
    {
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        return new AuthorityRows(context, membership, session);
    }

    private static ApplicationUser User(string email, DateTime now) => new()
    {
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        DisplayName = "Pooled RLS User B",
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        CreatedAt = now,
    };

    private static Tenant Tenant(int portfolioId, string firstName, string lastName, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        FirstName = firstName,
        LastName = lastName,
        Email = $"pooled-rls-{lastName.ToLowerInvariant()}@example.test",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private sealed class CancelConnectionInitializationInterceptor : DbConnectionInterceptor
    {
        public int? BackendProcessId { get; private set; }

        public override Task ConnectionOpenedAsync(
            System.Data.Common.DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            BackendProcessId = ((NpgsqlConnection)connection).ProcessID;
            throw new OperationCanceledException("Canceled before request RLS scope initialization.");
        }
    }

    private sealed record AuthorityRows(
        WorkspaceAccessContext Context,
        WorkspaceMembership Membership,
        AuthSession Session);

    private sealed record SessionState(
        string PortfolioId,
        string AuthSessionId,
        string UserId,
        string AccessContextId,
        string AccessRevision);

    private sealed record ConnectionObservation(
        int BackendProcessId,
        IReadOnlyList<int> PortfolioIds,
        SessionState SessionState);
}
