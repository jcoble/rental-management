using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public sealed class PortalServiceLeaseTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int AccessContextId = 23;
    private const int TenantId = 41;

    private readonly RentalCommandDbContext _db = NewContext();
    private readonly PortalService _sut;

    public PortalServiceLeaseTests()
    {
        _sut = new PortalService(_db, new NoopLeaseQaService(), TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void BuildLeaseRelationshipQuery_IsOneCanonicalAccessScopedStatement()
    {
        var sql = _sut.BuildLeaseRelationshipQuery(
                PortfolioId, AccessContextId, TenantId)
            .ToQueryString();

        sql.Should().Contain("vw_effective_tenant_access");
        sql.Should().Contain("LeaseManagementParties");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("vw_lease_agreement_status");
        sql.Should().Contain("LegalDocumentArtifacts");
        sql.Should().Contain("StoredFiles");
        sql.Should().Contain("ORDER BY");
        sql.Should().NotContain("FROM \"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void BuildOwnedLeaseManagementQuery_ReturnsCanonicalRelationshipId()
    {
        var sql = _sut.BuildOwnedLeaseManagementQuery(
                PortfolioId, AccessContextId, TenantId, leaseManagementId: 77)
            .ToQueryString();

        sql.Should().Contain("vw_effective_tenant_access");
        sql.Should().Contain("LeaseManagementParties");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("CurrentAgreementId");
        sql.Should().NotContain("FROM \"Leases\"");
    }

    [Fact]
    public void PortalContract_UsesRelationshipAndAgreementDtosInsteadOfLegacyLeaseDto()
    {
        var method = typeof(IPortalService).GetMethod(nameof(IPortalService.GetLeasesAsync));

        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(
            typeof(Task<IReadOnlyList<PortalLeaseRelationshipResponse>>));
        typeof(PortalLeaseRelationshipResponse)
            .GetProperty(nameof(PortalLeaseRelationshipResponse.Agreement))!
            .PropertyType.Should().Be(typeof(PortalLeaseAgreementResponse));
    }

    private static RentalCommandDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);
}
