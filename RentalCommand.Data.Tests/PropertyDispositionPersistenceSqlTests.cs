using FluentAssertions;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Tests;

/// <summary>
/// Static contract for the destructive property-disposition statement. PostgreSQL integration
/// verification runs in the serialized remote lane; these checks prevent the legacy Lease path or
/// a load-and-loop implementation from returning unnoticed.
/// </summary>
public sealed class PropertyDispositionPersistenceSqlTests
{
    [Fact]
    public void Disposition_is_one_set_based_canonical_statement()
    {
        var sql = CreateSql();

        sql.TrimStart().Should().StartWith("WITH target_property AS MATERIALIZED");
        sql.Should().NotContain(";",
            "the composable disposition command must remain one atomic PostgreSQL statement");
        sql.Should().Contain("UPDATE \"LeaseManagements\"");
        sql.Should().Contain("UPDATE \"TenantAccounts\"");
        sql.Should().Contain("UPDATE \"TenantAutopayEnrollments\"");
        sql.Should().Contain("UPDATE \"LeaseManagementParties\"");
        sql.Should().Contain("UPDATE \"TenantUserAccesses\"");
        sql.Should().Contain("UPDATE \"WorkspaceAccessContexts\"");
        sql.Should().Contain("INSERT INTO \"UnitOperationalPeriods\"");
        sql.Should().Contain("UPDATE \"CapitalAssets\"");
        sql.Should().Contain("management.\"PortfolioId\"");
        sql.Should().Contain("management.\"PropertyId\"");
        sql.Should().Contain("FROM target_relationships AS target");
    }

    [Fact]
    public void Disposition_preserves_legal_artifacts_and_removes_legacy_lease_mutation()
    {
        var sql = CreateSql();

        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("UPDATE \"LeaseAgreements\"");
        sql.Should().NotContain("UPDATE \"LeaseAddenda\"");
        sql.Should().Contain("\"PossessionReturnedAtUtc\"");
        sql.Should().Contain("\"CanceledAtUtc\"");
        sql.Should().Contain("\"AccountClosedAtUtc\"");
        sql.Should().Contain("\"ClosedAtUtc\"");
    }

    [Fact]
    public void Disposition_leaves_audit_and_outbox_materialization_to_atomic_kernel()
    {
        var sql = CreateSql();

        sql.Should().NotContain("INSERT INTO \"AuditLogs\"");
        sql.Should().NotContain("INSERT INTO \"OutboxMessages\"");
        sql.Should().Contain("jsonb_agg", "the handler needs exact IDs for canonical semantic events");
    }

    private static string CreateSql() =>
        (string)(typeof(AtomicLeaseMutationPersistence)
            .GetField("CreateSql", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()
            ?? throw new InvalidOperationException("Missing property-disposition SQL contract."));
}
