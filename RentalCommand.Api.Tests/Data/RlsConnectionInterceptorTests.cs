using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using RentalCommand.Api.Data;

namespace RentalCommand.Api.Tests.Data;

public sealed class RlsConnectionInterceptorTests
{
    [Fact]
    public void CustomerAdminRole_DoesNotGrantDatabaseBypass()
    {
        var http = new HttpContextAccessor
        {
            HttpContext = ContextWithClaims(
                new Claim("portfolioId", "17"),
                new Claim(ClaimTypes.Role, "Admin")),
        };
        var actorMode = new RlsActorModeAccessor();
        var interceptor = new RlsConnectionInterceptor(http, actorMode);

        interceptor.GetContextValues().Should().Be((17, false));
    }

    [Fact]
    public void MissingWorkspaceContext_FailsClosed()
    {
        var http = new HttpContextAccessor { HttpContext = ContextWithClaims() };
        var interceptor = new RlsConnectionInterceptor(http, new RlsActorModeAccessor());

        interceptor.GetContextValues().Should().Be((0, false));
    }

    [Fact]
    public void MissingHttpContext_FailsClosed()
    {
        var interceptor = new RlsConnectionInterceptor(
            new HttpContextAccessor(),
            new RlsActorModeAccessor());

        interceptor.GetContextValues().Should().Be((0, false));
    }

    [Theory]
    [InlineData(RlsActorMode.Platform)]
    [InlineData(RlsActorMode.Background)]
    public void ExplicitServerActorMode_GrantsDatabaseBypass(RlsActorMode mode)
    {
        var actorMode = new RlsActorModeAccessor();
        var interceptor = new RlsConnectionInterceptor(new HttpContextAccessor(), actorMode);

        using (actorMode.Begin(mode))
        {
            interceptor.GetContextValues().Should().Be((0, true));
        }

        interceptor.GetContextValues().Should().Be((0, false),
            "disposing the explicit actor scope must restore fail-closed behavior");
    }

    private static DefaultHttpContext ContextWithClaims(params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return context;
    }
}
