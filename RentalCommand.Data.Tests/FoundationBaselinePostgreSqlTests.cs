using FluentAssertions;
using RentalCommand.Data;

namespace RentalCommand.Data.Tests;

public sealed class FoundationBaselinePostgreSqlTests
{
    private static readonly string CreateSql =
        string.Join(Environment.NewLine, FoundationBaselinePostgreSql.CreateStatements);
    private static readonly string DropSql =
        string.Join(Environment.NewLine, FoundationBaselinePostgreSql.DropStatements);
    private static readonly string LeaseLegalCreateSql =
        string.Join(Environment.NewLine, RentalCommand.Data.Leasing.LeaseLegalSchemaSql.CreateStatements);
    private static readonly string LeaseLegalDropSql =
        string.Join(Environment.NewLine, RentalCommand.Data.Leasing.LeaseLegalSchemaSql.DropStatements);

    [Fact]
    public void Lease_successor_lineage_is_unique_reciprocal_and_commit_deferred()
    {
        LeaseLegalCreateSql.Should().Contain("IX_LeaseAgreements_DurableDirectSuccessor");
        LeaseLegalCreateSql.Should().Contain("COALESCE(\"ReplacesAgreementId\", \"RenewsAgreementId\")");
        LeaseLegalCreateSql.Should().Contain("TR_LeaseAgreements_ValidateReciprocalLineage");
        LeaseLegalCreateSql.Should().Contain("TR_LeaseAddenda_ValidateReciprocalLineage");
        LeaseLegalCreateSql.Should().Contain("DEFERRABLE INITIALLY DEFERRED");
        LeaseLegalCreateSql.Should().Contain("successor.\"GoverningFromOn\" = NEW.\"SupersededEffectiveOn\"");
        LeaseLegalCreateSql.Should().Contain("successor.\"EffectiveFromOn\" = NEW.\"SupersededEffectiveOn\"");
        LeaseLegalCreateSql.Should().Contain("decision.\"Decision\" IN ('End', 'IncorporateIntoBase')");
        LeaseLegalCreateSql.Should().Contain("decision.\"Decision\" = 'ReissueAsAddendum'");
        LeaseLegalCreateSql.Should().Contain("decision.\"ReplacementAddendumId\" = NEW.\"Id\"");
        LeaseLegalCreateSql.Should().Contain("renewal.\"IssuedArtifactId\" IS NOT NULL");
        LeaseLegalCreateSql.Should().Contain("renewal.\"GoverningFromOn\" = NEW.\"EffectiveFromOn\"");
        LeaseLegalDropSql.Should().Contain("DROP INDEX IF EXISTS \"IX_LeaseAgreements_DurableDirectSuccessor\"");
        LeaseLegalDropSql.Should().Contain("DROP TRIGGER IF EXISTS \"TR_LeaseAgreements_ValidateReciprocalLineage\"");
    }

    [Fact]
    public void ScheduleEDepreciationFunction_IsInstalledByTheCleanBaseline()
    {
        CreateSql.Should().Contain("CREATE OR REPLACE FUNCTION rc_schedule_e_depreciation_amount");
        CreateSql.Should().Contain("LANGUAGE sql");
        CreateSql.Should().Contain("IMMUTABLE");
        CreateSql.Should().Contain("PARALLEL SAFE");
        CreateSql.Should().Contain("in_service_date AT TIME ZONE 'UTC'");
        CreateSql.Should().Contain("round(manual_annual_depreciation, 2)");
        CreateSql.Should().Contain("LEAST(requested_amount, remaining)");
        CreateSql.Should().Contain("WHEN recovery_years = 15");
        DropSql.Should().Contain("DROP FUNCTION IF EXISTS rc_schedule_e_depreciation_amount");
    }

    [Fact]
    public void WorkspaceInvitations_AreApiOnlyGlobalAuthStateWithoutRlsOrSandboxDeletion()
    {
        FoundationBaselinePostgreSql.GlobalAuthAndSystemTables.Should().Contain("WorkspaceInvitations");
        FoundationBaselinePostgreSql.DirectPortfolioTables.Should().NotContain("WorkspaceInvitations");
        FoundationBaselinePostgreSql.NullablePortfolioTables.Should().NotContain("WorkspaceInvitations");
        FoundationBaselinePostgreSql.ChildPortfolioTables
            .Select(policy => policy.Table)
            .Should().NotContain("WorkspaceInvitations");

        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, UPDATE ON TABLE \"WorkspaceInvitations\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"WorkspaceInvitations\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "ON TABLE \"WorkspaceInvitations\" TO rentalcommand_engine;");
        CreateSql.Should().NotContain(
            "ALTER TABLE \"WorkspaceInvitations\" ENABLE ROW LEVEL SECURITY;");
        CreateSql.Should().NotContain(
            "CREATE POLICY tenant_isolation ON \"WorkspaceInvitations\"");

        FoundationBaselinePostgreSql.SandboxGraduationDeleteTables
            .Should().NotContain("WorkspaceInvitations");
        FoundationBaselinePostgreSql.SandboxGraduationGlobalDeleteTables
            .Should().NotContain("WorkspaceInvitations");
        FoundationBaselinePostgreSql.SandboxGraduationPreservedTables
            .Should().NotContain("WorkspaceInvitations",
                "global auth tables are preserved outside the portfolio-table partition");
    }

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
        CreateSql.Should().Contain(
            "CREATE ROLE rentalcommand_api NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE INHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1;");
        CreateSql.Should().Contain(
            "CREATE ROLE rentalcommand_engine NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE INHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1;");
        CreateSql.Should().Contain("NOT runtime_role.rolinherit");
        CreateSql.Should().Contain("runtime_role.rolconnlimit <> -1");
        CreateSql.Should().Contain("runtime_role.rolvaliduntil IS NOT NULL");
        CreateSql.Should().Contain("runtime_role.rolconfig IS NOT NULL");
        CreateSql.Should().Contain("inherited_membership.member = runtime_role.oid");
        CreateSql.Should().Contain(
            "GRANT rentalcommand_api, rentalcommand_engine TO %I', current_user");
        CreateSql.Should().Contain("Existing role rentalcommand_api has incompatible cluster-wide attributes");
        CreateSql.Should().Contain("Existing role rentalcommand_engine has incompatible cluster-wide attributes");
        CreateSql.Should().NotContain("ALTER ROLE");
        DropSql.Should().NotContain("DROP ROLE");
        DropSql.Should().NotContain("REVOKE rentalcommand_api, rentalcommand_engine FROM %I");
    }
}
