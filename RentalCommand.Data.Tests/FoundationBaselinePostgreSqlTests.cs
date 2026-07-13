using FluentAssertions;
using RentalCommand.Data;

namespace RentalCommand.Data.Tests;

public sealed class FoundationBaselinePostgreSqlTests
{
    private static readonly string CreateSql =
        string.Join(Environment.NewLine, FoundationBaselinePostgreSql.CreateStatements);
    private static readonly string DropSql =
        string.Join(Environment.NewLine, FoundationBaselinePostgreSql.DropStatements);

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
    public void DurableDeletePolicy_RequiresExactSandboxReasonAndPortfolioScope()
    {
        CreateSql.Should().Contain(
            "CREATE POLICY tenant_delete ON \"TenantLedgerEntries\" FOR DELETE USING\n" +
            "  (\"PortfolioId\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
            "AND current_setting('app.rls_bypass_reason', true) = 'SandboxGraduation');");
        CreateSql.Should().NotContain(
            "CREATE POLICY tenant_delete ON \"TenantLedgerEntries\" FOR DELETE USING\n" +
            "  (current_setting('app.is_admin', true) = 'true');");
    }

    [Fact]
    public void SandboxGraduation_ClassifiesEveryPortfolioScopedTableExactlyOnce()
    {
        var mappedPortfolioTables = FoundationBaselinePostgreSql.DirectPortfolioTables
            .Concat(FoundationBaselinePostgreSql.NullablePortfolioTables)
            .Concat(FoundationBaselinePostgreSql.ChildPortfolioTables.Select(policy => policy.Table))
            .ToHashSet(StringComparer.Ordinal);
        var portfolioDeleteTables = FoundationBaselinePostgreSql.SandboxGraduationDeleteTables
            .Except(FoundationBaselinePostgreSql.SandboxGraduationGlobalDeleteTables)
            .ToHashSet(StringComparer.Ordinal);
        var preservedTables = FoundationBaselinePostgreSql.SandboxGraduationPreservedTables
            .ToHashSet(StringComparer.Ordinal);

        portfolioDeleteTables.Intersect(preservedTables).Should().BeEmpty();
        portfolioDeleteTables.Union(preservedTables).Should().BeEquivalentTo(mappedPortfolioTables);
        FoundationBaselinePostgreSql.SandboxGraduationSelectiveDeleteTables.Should().BeEquivalentTo(
            [
                "AccountingEntityMappings", "AccountingSyncMaps", "DocumentTemplateFields",
                "DocumentTemplates", "MembershipRoleAssignmentProperties", "StoredFiles",
                "TeamRoutingRuleRecipients", "TeamRoutingRules",
            ]);
    }

    [Fact]
    public void SandboxGraduation_PreservesIdentityAndReusableConfiguration()
    {
        FoundationBaselinePostgreSql.SandboxGraduationPreservedTables.Should().Contain(
        [
            "AccountingConnections", "BankConnections",
        ]);
        FoundationBaselinePostgreSql.SandboxGraduationDeleteTables.Should().NotContain(
        [
            "AccountingConnections", "BankConnections",
        ]);
    }

    [Fact]
    public void GlobalQueueDeletes_RequireTheExactSandboxGraduationReason()
    {
        FoundationBaselinePostgreSql.SandboxGraduationGlobalDeleteTables.Should().BeEquivalentTo(
            ["AtomicCommandReceipts", "OutboxMessages", "ProviderInboxEvents"]);
        CreateSql.Should().Contain("current_setting('app.rls_bypass_reason', true) IS DISTINCT FROM 'SandboxGraduation'");
        CreateSql.Should().Contain("BEFORE DELETE ON \"AtomicCommandReceipts\"");
        CreateSql.Should().Contain("BEFORE DELETE ON \"OutboxMessages\"");
        CreateSql.Should().Contain("BEFORE DELETE ON \"ProviderInboxEvents\"");
        CreateSql.Should().Contain("Sandbox graduation requires an explicit portfolio scope");
        CreateSql.Should().Contain("audit.\"PortfolioId\" = target_portfolio_id");
        CreateSql.Should().Contain("row_portfolio_id IS DISTINCT FROM target_portfolio_id");
        FoundationBaselinePostgreSql.CreateSandboxGraduationGlobalDeleteGuards.Should().NotContain("app.is_admin");
        FoundationBaselinePostgreSql.CreateSandboxGraduationGlobalDeleteGuards.Should().NotContain("BackgroundWorker");
        FoundationBaselinePostgreSql.CreateSandboxGraduationGlobalDeleteGuards.Should().NotContain("PlatformOperation");
    }

    [Fact]
    public void Down_RemovesDatabaseGrantsWithoutRevokingClusterWideRoleMembership()
    {
        DropSql.Should().Contain("REVOKE CONNECT ON DATABASE %I FROM rentalcommand_api");
        DropSql.Should().Contain("REVOKE CONNECT ON DATABASE %I FROM rentalcommand_engine");
        DropSql.Should().NotContain("REVOKE rentalcommand_api, rentalcommand_engine FROM %I");
    }

    [Fact]
    public void Up_CreatesOrValidatesSharedRolesWithoutAlteringExistingRoleAttributes()
    {
        CreateSql.Should().Contain("CREATE ROLE rentalcommand_api NOLOGIN NOSUPERUSER");
        CreateSql.Should().Contain("Existing role rentalcommand_api has incompatible cluster-wide attributes");
        CreateSql.Should().Contain("Existing role rentalcommand_engine has incompatible cluster-wide attributes");
        CreateSql.Should().NotContain("ALTER ROLE rentalcommand_api");
        CreateSql.Should().NotContain("ALTER ROLE rentalcommand_engine");
    }
}
