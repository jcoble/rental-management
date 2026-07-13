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
    public void ExplicitLease_EnablesBypassOnlyForItsLifetime()
    {
        var execution = new RlsExecutionContext();
        RlsConnectionInterceptor.ResolveSessionState(null, execution.IsBypassActive)
            .Should().Be(new RlsSessionState(0, false));

        using (execution.BeginBypass(RlsBypassReason.BackgroundWorker))
        {
            RlsConnectionInterceptor.ResolveSessionState(
                    null, execution.IsBypassActive, execution.ActiveBypassReason)
                .Should().Be(new RlsSessionState(0, true, RlsBypassReason.BackgroundWorker));
        }
    }

    [Fact]
    public void NarrowPreContextLease_IsExceptionSafe()
    {
        var execution = new RlsExecutionContext();

        Action act = () =>
        {
            using var lease = execution.BeginBypass(RlsBypassReason.CanonicalJwtAuthorityResolution);
            execution.IsBypassActive.Should().BeTrue();
            throw new InvalidOperationException("resolver failed");
        };

        act.Should().Throw<InvalidOperationException>();
        execution.IsBypassActive.Should().BeFalse();
    }

    [Fact]
    public void SessionInitialization_AssumesApiRoleBeforeSettingRlsState()
    {
        var sql = RlsConnectionInterceptor.BuildSql(new RlsSessionState(17, false));

        sql.Should().StartWith("SET ROLE rentalcommand_api;");
        sql.Should().Contain("SET app.current_portfolio_id = '17';");
        sql.Should().Contain("SET app.is_admin = 'false';");
    }

    [Fact]
    public void StartupMigrationAndSeed_RetainsMigratorAuthority()
    {
        var sql = RlsConnectionInterceptor.BuildSql(
            new RlsSessionState(0, true, RlsBypassReason.StartupMigrationAndSeed));

        sql.Should().StartWith("RESET ROLE;");
        sql.Should().NotContain("SET ROLE rentalcommand_api;",
            "startup catalog seeding runs through the explicit migrator-only lease");
    }

    [Fact]
    public void ActiveBypassWithoutReason_FailsClosed()
    {
        var act = () => RlsConnectionInterceptor.ResolveSessionState(
            null, bypassActive: true, bypassReason: null);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SessionSql_EmitsAndClearsReasonGuc()
    {
        RlsConnectionInterceptor.BuildSql(
                new RlsSessionState(0, true, RlsBypassReason.SandboxGraduation))
            .Should().Contain("SET app.rls_bypass_reason = 'SandboxGraduation';");

        RlsConnectionInterceptor.BuildSql(new RlsSessionState(17, false))
            .Should().Contain("SET app.rls_bypass_reason = '';",
                "a pooled connection must not retain a prior destructive bypass reason");
    }

    private static ActiveAccessContext Active(int portfolioId) =>
        new(Guid.NewGuid(), 3, 5, portfolioId, 1,
            null, 7, null);
}
