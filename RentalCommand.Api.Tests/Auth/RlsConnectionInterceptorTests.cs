using FluentAssertions;
using Microsoft.AspNetCore.Http;
using RentalCommand.Api.Data;

namespace RentalCommand.Api.Tests.Auth;

public sealed class RlsConnectionInterceptorTests
{
    [Fact]
    public void ResolveSessionState_UsesValidatedTokenOnlyForPublicApplicationRoute()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/v1/public/applications/opaque-token";
        http.Request.RouteValues["token"] = "abcdefghijklmnopqrstuv_-123456789";

        var state = RlsConnectionInterceptor.ResolveSessionState(http);

        state.PortfolioId.Should().Be(0);
        state.PublicApplicationToken.Should().Be("abcdefghijklmnopqrstuv_-123456789");
        RlsConnectionInterceptor.BuildSql(state).Should().Contain(
            "set_config('app.public_application_token', 'abcdefghijklmnopqrstuv_-123456789', false)");
    }

    [Theory]
    [InlineData("/api/v1/public/applications/opaque-token", "too-short")]
    [InlineData("/api/v1/public/applications/opaque-token", "abcdefghijklmnopqrstuv'unsafe")]
    [InlineData("/api/v1/applications/opaque-token", "abcdefghijklmnopqrstuv_-123456789")]
    public void ResolveSessionState_RejectsInvalidOrNonPublicRouteTokens(string path, string token)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.RouteValues["token"] = token;

        var state = RlsConnectionInterceptor.ResolveSessionState(http);

        state.PublicApplicationToken.Should().BeNull();
        RlsConnectionInterceptor.BuildSql(state).Should().Contain(
            "set_config('app.public_application_token', '', false)");
    }
}
