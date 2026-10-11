using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Authorization;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Auth;

public sealed class CanonicalAccessContextMiddlewareConnectionTests
{
    [Fact]
    public async Task Authenticated_request_holds_scoped_connection_open_through_next_then_closes_it()
    {
        await using var db = CreateDbContext();
        var active = ValidAccessContext();
        var nextCalled = false;
        var middleware = new CanonicalAccessContextMiddleware(context =>
        {
            nextCalled = true;
            db.Database.GetDbConnection().State.Should().Be(ConnectionState.Open);
            context.Items[CanonicalAccessContextHttpItem.Key].Should().Be(active);
            return Task.CompletedTask;
        });
        var httpContext = AuthenticatedHttpContext(active);

        await middleware.InvokeAsync(
            httpContext,
            new StubAccessContextResolver(active),
            new FixedAuthSecurityClock(DateTime.UtcNow),
            db);

        nextCalled.Should().BeTrue();
        db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public async Task Authenticated_request_resolves_canonical_context_with_security_clock_not_simulated_business_clock()
    {
        await using var db = CreateDbContext();
        var active = ValidAccessContext();
        var realSecurityNowUtc = new DateTime(2026, 7, 27, 20, 0, 0, DateTimeKind.Utc);
        var resolver = new StubAccessContextResolver(active);
        var nextCalled = false;
        var middleware = new CanonicalAccessContextMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            AuthenticatedHttpContext(active),
            resolver,
            new FixedAuthSecurityClock(realSecurityNowUtc),
            db);

        nextCalled.Should().BeTrue();
        resolver.LastUtcNow.Should().Be(realSecurityNowUtc,
            "auth sessions and access envelopes are real security-time boundaries even when business time is simulated ahead");
    }

    [Fact]
    public async Task Authenticated_request_accepts_raw_jwt_subject_claim()
    {
        await using var db = CreateDbContext();
        var active = ValidAccessContext();
        var resolver = new StubAccessContextResolver(active);
        var nextCalled = false;
        var middleware = new CanonicalAccessContextMiddleware(context =>
        {
            nextCalled = true;
            context.Items[CanonicalAccessContextHttpItem.Key].Should().Be(active);
            return Task.CompletedTask;
        });
        var httpContext = AuthenticatedHttpContext(active, useRawSubjectClaim: true);

        await middleware.InvokeAsync(
            httpContext,
            resolver,
            new FixedAuthSecurityClock(DateTime.UtcNow),
            db);

        nextCalled.Should().BeTrue();
        resolver.LastUserId.Should().Be(active.UserId);
        db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public async Task Authenticated_request_closes_scoped_connection_when_downstream_throws()
    {
        await using var db = CreateDbContext();
        var active = ValidAccessContext();
        var middleware = new CanonicalAccessContextMiddleware(_ =>
        {
            db.Database.GetDbConnection().State.Should().Be(ConnectionState.Open);
            throw new InvalidOperationException("downstream failure");
        });

        var action = () => middleware.InvokeAsync(
            AuthenticatedHttpContext(active),
            new StubAccessContextResolver(active),
            new FixedAuthSecurityClock(DateTime.UtcNow),
            db);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("downstream failure");
        db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public async Task SignalR_transport_request_resolves_context_without_holding_connection_open()
    {
        await using var db = CreateDbContext();
        var active = ValidAccessContext();
        var nextCalled = false;
        var middleware = new CanonicalAccessContextMiddleware(context =>
        {
            nextCalled = true;
            db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
            context.Items[CanonicalAccessContextHttpItem.Key].Should().Be(active);
            return Task.CompletedTask;
        });
        var httpContext = AuthenticatedHttpContext(active);
        httpContext.Request.Path = "/api/v1/hubs/updates";

        await middleware.InvokeAsync(
            httpContext,
            new StubAccessContextResolver(active),
            new FixedAuthSecurityClock(DateTime.UtcNow),
            db);

        nextCalled.Should().BeTrue();
        db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public async Task Malformed_authenticated_coordinates_return_unauthorized_without_opening_connection()
    {
        await using var db = CreateDbContext();
        var nextCalled = false;
        var middleware = new CanonicalAccessContextMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var httpContext = AuthenticatedHttpContext(ValidAccessContext());
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sid", "not-a-guid"),
            new Claim(ClaimTypes.NameIdentifier, "41"),
            new Claim("ctx", "73"),
            new Claim("ar", "5"),
        ], "Test"));

        await middleware.InvokeAsync(
            httpContext,
            new StubAccessContextResolver(ValidAccessContext()),
            new FixedAuthSecurityClock(DateTime.UtcNow),
            db);

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        nextCalled.Should().BeFalse();
        db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_or_stale_context_returns_unauthorized_without_opening_connection(
        bool stale)
    {
        await using var db = CreateDbContext();
        var active = ValidAccessContext();
        var nextCalled = false;
        var middleware = new CanonicalAccessContextMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        UnauthorizedAccessException failure = stale
            ? new StaleAccessRevisionException(active.AccessRevision, active.AccessRevision + 1)
            : new AccessContextUnavailableException();
        var httpContext = AuthenticatedHttpContext(active);

        await middleware.InvokeAsync(
            httpContext,
            new StubAccessContextResolver(failure),
            new FixedAuthSecurityClock(DateTime.UtcNow),
            db);

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        httpContext.Response.Headers.ContainsKey("X-Access-Envelope-Refresh")
            .Should().Be(stale);
        nextCalled.Should().BeFalse();
        db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    private static RentalCommandDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new RentalCommandDbContext(options);
    }

    private static DefaultHttpContext AuthenticatedHttpContext(
        ActiveAccessContext active,
        bool useRawSubjectClaim = false)
    {
        var userIdClaim = useRawSubjectClaim
            ? new Claim(JwtRegisteredClaimNames.Sub, active.UserId.ToString())
            : new Claim(ClaimTypes.NameIdentifier, active.UserId.ToString());
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sid", active.SessionId.ToString()),
                userIdClaim,
                new Claim("ctx", active.AccessContextId.ToString()),
                new Claim("ar", active.AccessRevision.ToString()),
            ], "Test")),
        };
        return context;
    }

    private static ActiveAccessContext ValidAccessContext() =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            UserId: 41,
            AccessContextId: 73,
            PortfolioId: 19,
            AccessRevision: 5,
            LastAuthorizedExperience: null,
            WorkspaceMembershipId: 91,
            DefaultExperience: null);

    private sealed class StubAccessContextResolver : IActiveAccessContextResolver
    {
        private readonly ActiveAccessContext? _active;
        private readonly Exception? _failure;

        public StubAccessContextResolver(ActiveAccessContext active) => _active = active;

        public StubAccessContextResolver(Exception failure) => _failure = failure;

        public DateTime? LastUtcNow { get; private set; }
        public int? LastUserId { get; private set; }

        public Task<ActiveAccessContext> ResolveAsync(
            Guid sessionId,
            int userId,
            int accessContextId,
            long presentedAccessRevision,
            DateTime utcNow,
            CancellationToken cancellationToken = default)
        {
            LastUtcNow = utcNow;
            LastUserId = userId;
            return _failure is null
                ? Task.FromResult(_active!)
                : Task.FromException<ActiveAccessContext>(_failure);
        }
    }

    private sealed class FixedAuthSecurityClock(DateTime utcNow) : IAuthSecurityClock
    {
        public DateTime UtcNow() => utcNow;
    }
}
