using FluentAssertions;
using RentalCommand.Engine.Data;

namespace RentalCommand.Engine.Tests.Data;

public sealed class EngineRlsInterceptorTests
{
    [Fact]
    public void SessionInitialization_ClearsRequestCoordinatesWithoutRoleAssumptionOrAdminFlags()
    {
        EngineRlsInterceptor.SessionInitializationSql
            .Should().NotContain("SET ROLE");
        EngineRlsInterceptor.SessionInitializationSql
            .Should().Contain("app.current_portfolio_id");
        EngineRlsInterceptor.SessionInitializationSql
            .Should().NotContain("app.is_admin");
    }
}
