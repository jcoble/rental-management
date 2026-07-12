using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
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
    public void Basic_unit_reader_derives_presentation_status_from_canonical_occupancy()
    {
        using var db = NewContext();
        var service = new UnitService(
            db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System);

        var sql = service.BuildCanonicalResponseQuery(17)
            .OrderBy(unit => unit.UnitNumber)
            .Take(20)
            .ToQueryString();

        sql.Should().Contain("vw_unit_occupancy");
        sql.Should().Contain("IsOccupied");
        sql.Should().Contain("HasScheduledMoveIn");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseStatus");
    }

    [Fact]
    public void Unit_delete_guard_is_one_canonical_database_projection()
    {
        using var db = NewContext();
        var service = new UnitService(
            db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System);

        var sql = service.BuildDeletionGuardQuery(17, 42).ToQueryString();

        sql.Should().Contain("vw_unit_occupancy");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("EXISTS");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseStatus");
    }

    [Fact]
    public void Property_delete_guards_are_canonical_database_projections()
    {
        using var db = NewContext();
        var service = new PropertyService(db, Mock.Of<IDataUpdateService>(), TimeProvider.System);

        var propertySql = service.BuildPropertyDeletionGuardQuery(17, 9).ToQueryString();
        var canonicalUnitSql = service.BuildUnitDeletionGuardQuery(17, 42).ToQueryString();
        var canonicalUnitResponseSql = service.BuildCanonicalUnitResponseQuery(17, 42).ToQueryString();

        foreach (var sql in new[] { propertySql, canonicalUnitSql })
        {
            sql.Should().Contain("vw_unit_occupancy");
            sql.Should().Contain("vw_lease_management_lifecycle");
            sql.Should().Contain("LeaseManagements");
            sql.Should().Contain("EXISTS");
            sql.Should().NotContain("\"Leases\"");
            sql.Should().NotContain("LeaseStatus");
        }

        canonicalUnitResponseSql.Should().Contain("vw_unit_occupancy");
        canonicalUnitResponseSql.Should().NotContain("\"Leases\"");
    }

    [Fact]
    public void Unit_mutation_contracts_do_not_accept_source_status()
    {
        typeof(CreateUnitRequest).GetProperty("Status").Should().BeNull();
        typeof(UpdateUnitRequest).GetProperty("Status").Should().BeNull();
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
        sql.Should().Contain("count");
        sql.Should().Contain("DISTINCT");
        sql.Should().Contain("LIMIT");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void Unit_dashboard_header_joins_canonical_occupancy_agreement_and_account_in_one_statement()
    {
        using var db = NewContext();
        var service = new UnitDashboardService(
            db,
            new AuditDescriber(),
            new AuditDiffBuilder(),
            TimeProvider.System);

        var sql = service.BuildCanonicalDashboardQuery(17, 42).ToQueryString();

        sql.Should().Contain("vw_unit_occupancy");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("vw_lease_agreement_status");
        sql.Should().Contain("vw_tenant_account_balances");
        sql.Should().Contain("vw_security_deposit_balances");
        sql.Should().Contain("TenantAccountId");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void Tenant_account_ledger_header_authorizes_and_aggregates_in_one_canonical_statement()
    {
        using var db = NewContext();
        var service = NewLeaseService(db);

        var sql = service.BuildCanonicalLedgerHeaderQuery(17, 42, restrictToTenantId: 9)
            .ToQueryString();

        sql.Should().Contain("TenantAccounts");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseManagementParties");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("vw_tenant_account_balances");
        sql.Should().Contain("TenantLedgerEntries");
        sql.Should().Contain("count");
        sql.Should().NotContain("\"Payments\"");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("OpeningBalances");
    }

    [Fact]
    public void Tenant_account_ledger_entries_sort_and_page_in_the_database()
    {
        using var db = NewContext();
        var service = NewLeaseService(db);

        var sql = service.BuildCanonicalLedgerEntriesQuery(17, 81)
            .OrderByDescending(entry => entry.EffectiveOn)
            .ThenByDescending(entry => entry.Id)
            .Skip(20)
            .Take(10)
            .ToQueryString();

        sql.Should().Contain("TenantLedgerEntries");
        sql.Should().Contain("TenantPaymentAttempts");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().NotContain("\"Payments\"");
        sql.Should().NotContain("OpeningBalances");
    }

    [Fact]
    public void Scan_originated_tenant_money_activity_resolves_account_label_and_unit_canonically()
    {
        using var db = NewContext();
        var dashboard = new DashboardService(db, new AuditDescriber(), TimeProvider.System);
        var audit = new AuditQueryService(
            db,
            new AuditDescriber(),
            new AuditDiffBuilder(),
            Mock.Of<RentalCommand.Core.Time.IAppTimeZoneProvider>());

        var dashboardSql = dashboard.BuildTenantAccountActivityRefsQuery(17, [81, 82])
            .ToQueryString();
        var auditSql = audit.BuildTenantAccountUnitRefsQuery(17, [81, 82])
            .ToQueryString();

        foreach (var sql in new[] { dashboardSql, auditSql })
        {
            sql.Should().Contain("TenantAccounts");
            sql.Should().Contain("LeaseManagements");
            sql.Should().Contain("UnitId");
            sql.Should().Contain(" IN ");
            sql.Should().NotContain("\"Payments\"");
            sql.Should().NotContain("\"Leases\"");
        }

        dashboardSql.Should().Contain("AccountNumber");
    }

    private static LeaseService NewLeaseService(RentalCommandDbContext db) => new(
        db,
        Mock.Of<IDataUpdateService>(),
        Mock.Of<IFileStorage>(),
        Mock.Of<ILeaseAgreementPdfGenerator>(),
        Mock.Of<IAuditTrailService>(),
        NullLogger<LeaseService>.Instance,
        TimeProvider.System);

    private static RentalCommandDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);
}
