using FluentAssertions;
using RentalCommand.Api.Data;

namespace RentalCommand.Api.Tests.Auth;

public sealed class WorkspaceInvitationActivationRlsScopeTests
{
    [Fact]
    public void ScopeCommand_IsTransactionLocalParameterizedAndNeverAdministrative()
    {
        var command = WorkspaceInvitationActivationRlsScope.BuildCommand(17);

        command.Format.Should().Contain(
            "set_config('app.current_portfolio_id', {0}, true)");
        command.GetArguments().Should().ContainSingle().Which.Should().Be("17");
        command.Format.Should().Contain("set_config('app.is_admin', 'false', true)");
        command.Format.Should().Contain("set_config('app.rls_bypass_reason', '', true)");
        command.Format.Should().NotContain("set_config('app.is_admin', 'true'");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ScopeCommand_RejectsInvalidPortfolio(int portfolioId)
    {
        var act = () => WorkspaceInvitationActivationRlsScope.BuildCommand(portfolioId);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
