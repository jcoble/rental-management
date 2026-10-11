using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class PortalServiceAppointmentTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();
    private readonly PortalService _sut;

    public PortalServiceAppointmentTests()
    {
        _sut = new PortalService(
            _ctx.Db, new NoopLeaseQaService(), TimeProvider.System,
            Mock.Of<RentalCommand.Core.Atomic.IWriteExecutor>());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void BuildAppointmentsQuery_AuthorizesAndFallsBackThroughCanonicalRelationshipInOneStatement()
    {
        var sql = _sut.BuildAppointmentsQuery(
                portfolioId: 1,
                accessContextId: 23,
                tenantId: 41,
                now: DateTime.UtcNow)
            .ToQueryString();

        sql.Should().Contain("FROM \"Appointments\"");
        sql.Should().Contain("vw_effective_tenant_access");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().NotContain("FROM \"WorkOrders\"");
        sql.Should().NotContain("UNION ALL");
        sql.Should().NotContain("FROM \"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }
}
