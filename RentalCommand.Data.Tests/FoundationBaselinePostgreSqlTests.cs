using FluentAssertions;
using RentalCommand.Data;

namespace RentalCommand.Data.Tests;

public sealed class FoundationBaselinePostgreSqlTests
{
    private static readonly string CreateSql =
        string.Join(Environment.NewLine, FoundationBaselinePostgreSql.CreateStatements);

    [Fact]
    public void SandboxGraduation_AddsDeleteWithoutWeakeningAppendOnlyRows()
    {
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, DELETE ON TABLE \"AuditLogs\" TO rentalcommand_api;");
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, DELETE ON TABLE \"TenantLedgerEntries\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"AuditLogs\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"TenantLedgerEntries\" TO rentalcommand_api;");
    }

    [Fact]
    public void DurableDeletePolicy_RequiresExactSandboxReason()
    {
        CreateSql.Should().Contain(
            "CREATE POLICY tenant_delete ON \"TenantLedgerEntries\" FOR DELETE USING\n" +
            "  (current_setting('app.rls_bypass_reason', true) = 'SandboxGraduation');");
        CreateSql.Should().NotContain(
            "CREATE POLICY tenant_delete ON \"TenantLedgerEntries\" FOR DELETE USING\n" +
            "  (current_setting('app.is_admin', true) = 'true');");
    }
}
