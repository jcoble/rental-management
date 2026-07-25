using System.Data;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Auth;
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
            TimeProvider.System,
            db);

        nextCalled.Should().BeTrue();
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
            TimeProvider.System,
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
            TimeProvider.System,
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
            TimeProvider.System,
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
            TimeProvider.System,
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

    private static DefaultHttpContext AuthenticatedHttpContext(ActiveAccessContext active)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sid", active.SessionId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, active.UserId.ToString()),
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

        public Task<ActiveAccessContext> ResolveAsync(
            Guid sessionId,
            int userId,
            int accessContextId,
            long presentedAccessRevision,
            DateTime utcNow,
            CancellationToken cancellationToken = default) =>
            _failure is null
                ? Task.FromResult(_active!)
                : Task.FromException<ActiveAccessContext>(_failure);
    }
}
