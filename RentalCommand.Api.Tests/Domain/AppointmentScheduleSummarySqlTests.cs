using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Translation-only proof: Npgsql must render the shell schedule badge as one access-scoped,
/// date-filtered aggregate statement. No database connection is opened.
/// </summary>
public sealed class AppointmentScheduleSummarySqlTests
{
    private static readonly WorkspaceReadScope Scope = new(
        17,
        23,
        Guid.Parse("935e3585-4950-4296-949f-556037dcfbaa"),
        31,
        7);

    [Fact]
    public void Summary_AuthorizesFiltersAndCountsInOnePostgreSqlStatement()
    {
        using var db = NewContext();
        var service = new AppointmentService(
            db,
            Mock.Of<IDataUpdateService>(),
            TimeProvider.System);
        var windowStartUtc = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        var windowEndUtc = windowStartUtc.AddDays(7);

        var sql = service
            .BuildScheduleSummaryQuery(Scope, windowStartUtc, windowEndUtc)
            .ToQueryString();

        sql.Should().ContainEquivalentOf("count(*)");
        sql.Should().Contain("Appointments");
        sql.Should().Contain("ScheduledStart");
        sql.Should().Contain(">=");
        sql.Should().Contain("<");
        sql.Should().Contain("Status");
        sql.Should().Contain("public.rc_api_effective_capability_scopes(");
        sql.Should().Contain(Scope.SessionId.ToString());
        sql.Should().NotContain("AuthSessions");
        sql.Should().NotContain("RoleProfileCapabilities");
        sql.Should().NotContain("CapabilityDefinitions");
        sql.Should().NotContain("MembershipRoleAssignmentProperties");
        sql.Should().NotContain("LIMIT");
        sql.Should().NotContain("OFFSET");
    }

    private static RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);
}
