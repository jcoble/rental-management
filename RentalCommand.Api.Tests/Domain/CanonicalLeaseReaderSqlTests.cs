using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Translation-only proofs for the clean-replacement readers. These tests never open a database
/// connection; Npgsql must be able to render each full query as one SQL statement over the
/// canonical relationship projections.
/// </summary>
public sealed class CanonicalLeaseReaderSqlTests
{
    [Fact]
    public void Unit_health_reader_joins_occupancy_lifecycle_and_governing_agreement_in_sql()
    {
        using var db = NewContext();
        var service = new UnitService(
            db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System);

        var sql = service.BuildHealthQuery(17)
            .OrderBy(row => row.UnitNumber)
            .Skip(20)
            .Take(20)
            .ToQueryString();

        sql.Should().Contain("vw_unit_occupancy");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("LegalDocumentArtifacts");
        sql.Should().Contain("TenantAccountId");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void Tenant_relationship_counts_are_distinct_database_aggregates_over_canonical_records()
    {
        using var db = NewContext();
        var service = new TenantService(
            db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<ITenantPortalProvisioningService>(),
            NullLogger<TenantService>.Instance,
            TimeProvider.System);

        var sql = service.BuildRelationshipCountQuery(
                db.Tenants.AsNoTracking().Where(tenant => tenant.PortfolioId == 17),
                17)
            .OrderBy(row => row.Entity.LastName)
            .Take(25)
            .ToQueryString();

        sql.Should().Contain("LeaseManagementParties");
        sql.Should().Contain("vw_unit_occupancy");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("COUNT");
        sql.Should().Contain("DISTINCT");
        sql.Should().Contain("LIMIT");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    private static RentalCommandDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);
}
