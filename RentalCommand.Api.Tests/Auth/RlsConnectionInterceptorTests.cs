using FluentAssertions;
using Microsoft.AspNetCore.Http;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Data;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Tests.Auth;

public sealed class RlsConnectionInterceptorTests
{
    [Fact]
    public void CanonicalContext_WinsOverLegacyClaims()
    {
        var http = new DefaultHttpContext();
        http.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("portfolioId", "999"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin"),
            ], "legacy"));
        http.Items[CanonicalAccessContextHttpItem.Key] = Active(portfolioId: 17);

        var state = RlsConnectionInterceptor.ResolveSessionState(http, bypassActive: false);

        state.PortfolioId.Should().Be(17);
        state.IsAdmin.Should().BeFalse();
    }

    [Fact]
    public void AuthenticatedMissingCanonicalContext_FailsClosed()
    {
        var http = new DefaultHttpContext();
        http.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("portfolioId", "17")], "legacy"));

        var state = RlsConnectionInterceptor.ResolveSessionState(http, bypassActive: false);

        state.Should().Be(new RlsSessionState(0, false));
    }

    [Fact]
    public void ExplicitBackgroundLease_IsTheOnlyBypass()
    {
        var execution = new RlsExecutionContext();
        RlsConnectionInterceptor.ResolveSessionState(null, execution.IsBypassActive)
            .Should().Be(new RlsSessionState(0, false));

        using (execution.BeginBypass(RlsBypassReason.BackgroundWorker))
        {
            RlsConnectionInterceptor.ResolveSessionState(null, execution.IsBypassActive)
                .Should().Be(new RlsSessionState(0, true));
        }
    }

    private static ActiveAccessContext Active(int portfolioId) =>
        new(Guid.NewGuid(), 3, 5, portfolioId, 1,
            null, 7, null);
}
