using FluentAssertions;
using RentalCommand.Engine.Data;

namespace RentalCommand.Engine.Tests.Data;

public sealed class EngineRlsInterceptorTests
{
    [Fact]
    public void SessionInitialization_AssumesEngineRoleBeforeSettingRlsState()
    {
        EngineRlsInterceptor.SessionInitializationSql
            .Should().StartWith("SET ROLE rentalcommand_engine;");
        EngineRlsInterceptor.SessionInitializationSql
            .Should().Contain("SET app.current_portfolio_id = '0';");
        EngineRlsInterceptor.SessionInitializationSql
            .Should().Contain("SET app.is_admin = 'true';");
    }
}
