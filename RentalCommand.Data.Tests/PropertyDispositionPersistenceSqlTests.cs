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
        CountStatementTerminators(sql).Should().Be(0,
            "the composable disposition command must not contain a statement boundary; " +
            "semicolons inside PostgreSQL string literals are ordinary punctuation");
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

        sql.Should().NotContain("INSERT INTO \"AtomicAuditLogs\"");
        sql.Should().NotContain("INSERT INTO \"OutboxMessages\"");
        sql.Should().Contain("jsonb_agg", "the handler needs exact IDs for canonical semantic events");
    }

    private static string CreateSql() =>
        (string)(typeof(AtomicLeaseMutationPersistence)
            .GetField("CreateSql", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()
            ?? throw new InvalidOperationException("Missing property-disposition SQL contract."));

    private static int CountStatementTerminators(string sql)
    {
        var count = 0;
        var inSingleQuotedLiteral = false;
        var inDoubleQuotedIdentifier = false;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';

            if (inSingleQuotedLiteral)
            {
                if (current != '\'')
                    continue;
                if (next == '\'')
                {
                    index++;
                    continue;
                }
                inSingleQuotedLiteral = false;
                continue;
            }

            if (inDoubleQuotedIdentifier)
            {
                if (current != '"')
                    continue;
                if (next == '"')
                {
                    index++;
                    continue;
                }
                inDoubleQuotedIdentifier = false;
                continue;
            }

            if (current == '\'')
                inSingleQuotedLiteral = true;
            else if (current == '"')
                inDoubleQuotedIdentifier = true;
            else if (current == ';')
                count++;
        }

        inSingleQuotedLiteral.Should().BeFalse("the SQL must not end inside a string literal");
        inDoubleQuotedIdentifier.Should().BeFalse("the SQL must not end inside a quoted identifier");
        return count;
    }
}
