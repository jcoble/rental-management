using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Migrations;

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
    public void RuntimeLogins_HaveOnlyTheNonInheritedAtomicReadOnlyMembership()
    {
        CreateSql.Should().Contain(
            "CREATE ROLE rentalcommand_atomic_readonly\n" +
            "      NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT");
        CreateSql.Should().Contain(
            "GRANT rentalcommand_atomic_readonly TO rentalcommand_api\n" +
            "  WITH INHERIT FALSE, SET TRUE;");
        CreateSql.Should().Contain(
            "GRANT rentalcommand_atomic_readonly TO rentalcommand_engine\n" +
            "  WITH INHERIT FALSE, SET TRUE;");
        CreateSql.Should().Contain("OR membership.admin_option");
        CreateSql.Should().Contain("OR membership.inherit_option");
        CreateSql.Should().Contain("OR NOT membership.set_option");

        var migrationSource = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../RentalCommand.Data/Migrations/20260729017000_AddAtomicReadOnlyRuntimeRole.cs"));
        migrationSource.Should().Contain(
            "FoundationBaselinePostgreSql.AtomicReadOnlyRoleSqlV20260729");
        migrationSource.Should().NotContain("REVOKE rentalcommand_atomic_readonly");
    }

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
    public void NotificationReadState_IsApiOnlyUserStateAndIsNotGrantedToTheEngine()
    {
        FoundationBaselinePostgreSql.DirectPortfolioTables.Should().Contain("NotificationReadStates");
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"NotificationReadStates\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "ON TABLE \"NotificationReadStates\" TO rentalcommand_engine;");
    }

    [Fact]
    public void DebtServiceWorker_HasOnlyTheLoanPermissionsItsClaimAndAppendFlowRequires()
    {
        CreateSql.Should().Contain(
            "GRANT SELECT, UPDATE ON TABLE \"Loans\" TO rentalcommand_engine;");
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT ON TABLE \"LoanPayments\" TO rentalcommand_engine;");
    }

    [Fact]
    public void NativeEsignExecutionWorker_CanAppendExecutedStoredFilesWithoutBroadMutationRights()
    {
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT ON TABLE \"StoredFiles\" TO rentalcommand_engine;");
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT ON TABLE \"LegalDocumentArtifacts\" TO rentalcommand_engine;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE ON TABLE \"StoredFiles\" TO rentalcommand_engine;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"StoredFiles\" TO rentalcommand_engine;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE ON TABLE \"LegalDocumentArtifacts\" TO rentalcommand_engine;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"LegalDocumentArtifacts\" TO rentalcommand_engine;");
        CreateSql.Should().Contain(
            "GRANT SELECT, UPDATE ON TABLE \"LeaseAddenda\" TO rentalcommand_engine;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE ON TABLE \"LeaseAddenda\" TO rentalcommand_engine;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"LeaseAddenda\" TO rentalcommand_engine;");
    }

    [Fact]
    public void SandboxGraduation_AddsDeleteWithoutWeakeningAppendOnlyRows()
    {
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, DELETE ON TABLE \"AtomicAuditLogs\" TO rentalcommand_api;");
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, DELETE ON TABLE \"TenantLedgerEntries\" TO rentalcommand_api;");
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, DELETE ON TABLE \"TenantLedgerAllocations\" TO rentalcommand_api;");
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"TenantAccounts\" TO rentalcommand_api;");
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT ON TABLE \"TenantLedgerEntries\" TO rentalcommand_engine;");
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT ON TABLE \"TenantLedgerAllocations\" TO rentalcommand_engine;");
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, UPDATE ON TABLE \"TenantAccounts\" TO rentalcommand_engine;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"AtomicAuditLogs\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"TenantLedgerEntries\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"TenantLedgerAllocations\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"TenantLedgerEntries\" TO rentalcommand_engine;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"TenantLedgerAllocations\" TO rentalcommand_engine;");
    }

    [Fact]
    public void RuntimeTenantMoneyDmlMigration_GrantsOnlyTenantMoneyRuntimeAccess()
    {
        var migrationSource = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../RentalCommand.Data/Migrations/20260728235000_GrantRuntimeTenantMoneyDml.cs"));

        migrationSource.Should().Contain(
            "GRANT SELECT, UPDATE ON TABLE \"TenantAccounts\" TO rentalcommand_api;");
        migrationSource.Should().Contain(
            "GRANT SELECT, INSERT ON TABLE \"TenantLedgerEntries\" TO rentalcommand_api;");
        migrationSource.Should().Contain(
            "GRANT SELECT, INSERT ON TABLE \"TenantLedgerAllocations\" TO rentalcommand_api;");
        migrationSource.Should().Contain(
            "GRANT SELECT ON TABLE \"TenantAccounts\" TO rentalcommand_engine;");
        migrationSource.Should().Contain(
            "GRANT SELECT, INSERT ON TABLE \"TenantLedgerEntries\" TO rentalcommand_engine;");
        migrationSource.Should().Contain(
            "GRANT SELECT, INSERT ON TABLE \"TenantLedgerAllocations\" TO rentalcommand_engine;");
        migrationSource.Should().Contain(
            "GRANT USAGE, SELECT ON SEQUENCE \"TenantLedgerEntries_Id_seq\" TO rentalcommand_engine;");
        migrationSource.Should().Contain(
            "GRANT USAGE, SELECT ON SEQUENCE \"TenantLedgerAllocations_Id_seq\" TO rentalcommand_engine;");
        migrationSource.Should().Contain(
            "REVOKE SELECT, INSERT ON TABLE \"TenantLedgerEntries\" FROM rentalcommand_engine;");
        migrationSource.Should().NotContain(
            "REVOKE SELECT, INSERT ON TABLE \"TenantLedgerEntries\" FROM rentalcommand_api;");
        migrationSource.Should().NotContain(
            "REVOKE SELECT, UPDATE ON TABLE \"TenantAccounts\" FROM rentalcommand_api;");
        migrationSource.Should().NotContain("GRANT ALL");
        migrationSource.Should().NotContain("BYPASSRLS");
        migrationSource.Should().NotContain("ALTER ROLE");
    }

    [Fact]
    public void VendorDispatchChronologyRecovery_UsesDirectApiRlsWithoutSecurityDefinerFunction()
    {
        CreateSql.Should().Contain(
            "GRANT UPDATE (\"Timestamp\") ON TABLE public.\"AtomicAuditLogs\"\n" +
            "  TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "GRANT UPDATE (\"Timestamp\") ON TABLE public.\"AtomicAuditLogs\"\n" +
            "  TO rentalcommand_rls_authority;");
        DropSql.Should().Contain(
            "REVOKE UPDATE (\"Timestamp\") ON TABLE public.\"AtomicAuditLogs\"\n" +
            "  FROM rentalcommand_api;");

        var directGrantMigrationSource = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../RentalCommand.Data/Migrations/" +
                "20260729014000_GrantApiVendorDispatchChronologyRecovery.cs"));
        var retiredLaneRemovalMigrationSource = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../RentalCommand.Data/Migrations/" +
                "20260729016000_RemoveRetiredVendorDispatchChronologyFunction.cs"));

        directGrantMigrationSource.Should().Contain(
            "GRANT UPDATE (\"Timestamp\") ON TABLE public.\"AtomicAuditLogs\"");
        directGrantMigrationSource.Should().Contain("TO rentalcommand_api;");
        directGrantMigrationSource.Should().Contain(
            "REVOKE UPDATE (\"Timestamp\") ON TABLE public.\"AtomicAuditLogs\"");
        directGrantMigrationSource.Should().NotContain("CREATE OR REPLACE FUNCTION");
        directGrantMigrationSource.Should().NotContain("SECURITY DEFINER");
        directGrantMigrationSource.Should().NotContain("GRANT EXECUTE ON FUNCTION");
        directGrantMigrationSource.Should().NotContain(
            "REVOKE SELECT ON TABLE public.\"AtomicAuditLogs\"");
        retiredLaneRemovalMigrationSource.Should().Contain(
            "DROP FUNCTION IF EXISTS public.rc_recover_vendor_dispatch_audit_chronology(");
        retiredLaneRemovalMigrationSource.Should().Contain(
            "GRANT UPDATE (\"Timestamp\") ON TABLE public.\"AtomicAuditLogs\"");
        retiredLaneRemovalMigrationSource.Should().NotContain("CREATE OR REPLACE FUNCTION");
        retiredLaneRemovalMigrationSource.Should().NotContain("SECURITY DEFINER");
        retiredLaneRemovalMigrationSource.Should().NotContain("GRANT EXECUTE ON FUNCTION");
    }

    [Fact]
    public void RuntimeTenantMoneyRowLockMigration_GrantsOnlyRequiredUpdateLocks()
    {
        var migrationSource = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../RentalCommand.Data/Migrations/20260728235500_GrantRuntimeTenantMoneyRowLockUpdates.cs"));

        migrationSource.Should().Contain(
            "GRANT UPDATE ON TABLE \"TenantLedgerEntries\" TO rentalcommand_api;");
        migrationSource.Should().Contain(
            "GRANT UPDATE ON TABLE \"TenantLedgerAllocations\" TO rentalcommand_api;");
        migrationSource.Should().Contain(
            "GRANT UPDATE ON TABLE \"TenantLedgerEntries\" TO rentalcommand_engine;");
        migrationSource.Should().NotContain(
            "GRANT UPDATE ON TABLE \"TenantLedgerAllocations\" TO rentalcommand_engine;");
        migrationSource.Should().NotContain(
            "GRANT UPDATE ON TABLE \"TenantAccounts\" TO rentalcommand_engine;");
        migrationSource.Should().NotContain("GRANT ALL");
        migrationSource.Should().NotContain("BYPASSRLS");
        migrationSource.Should().NotContain("ALTER ROLE");
    }

    [Fact]
    public void ApprovedNoticeReplay_CanRepairRenderedNoticeChronologyWithoutBroadAppendOnlyUpdates()
    {
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"RenderedNotices\" TO rentalcommand_api;");
        CreateSql.Should().Contain(
            "CREATE POLICY tenant_update ON \"RenderedNotices\" FOR UPDATE USING (rc_api_scope_allows(\"PortfolioId\")) WITH CHECK (rc_api_scope_allows(\"PortfolioId\"));");
        DropSql.Should().Contain(
            "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLE \"RenderedNotices\" FROM rentalcommand_api;");

        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, DELETE ON TABLE \"NoticeDeliveryEvidence\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"NoticeDeliveryEvidence\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE ON TABLE \"NoticeDeliveryEvidence\" TO rentalcommand_api;");
    }

    [Fact]
    public void ApprovedNoticeReplayMigration_GrantsOnlyRenderedNoticeUpdate()
    {
        var migrationSource = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../RentalCommand.Data/Migrations/20260728164500_GrantApiRenderedNoticeChronologyRepair.cs"));

        migrationSource.Should().Contain(
            "GRANT UPDATE ON TABLE \"RenderedNotices\" TO rentalcommand_api;");
        migrationSource.Should().Contain(
            "REVOKE UPDATE ON TABLE \"RenderedNotices\" FROM rentalcommand_api;");
        migrationSource.Should().NotContain("NoticeDeliveryEvidence");
        migrationSource.Should().NotContain("AtomicAuditLogs");
    }

    [Fact]
    public void DurableDeletePolicy_RequiresDatabaseValidatedSandboxAuthority()
    {
        var normalizedSql = Regex.Replace(CreateSql, @"\s+", " ");

        CreateSql.Should().Contain(
            "CREATE POLICY tenant_delete ON \"TenantLedgerEntries\" FOR DELETE USING " +
            "(rc_sandbox_graduation_allows(\"PortfolioId\"));");
        CreateSql.Should().Contain("CREATE POLICY tenant_select ON \"TenantLedgerEntries\" FOR SELECT USING (CASE");
        CreateSql.Should().Contain("CREATE POLICY tenant_insert ON \"TenantLedgerEntries\" FOR INSERT WITH CHECK (CASE");
        CreateSql.Should().Contain("CREATE POLICY tenant_update ON \"TenantLedgerEntries\" FOR UPDATE USING (CASE");
        normalizedSql.Should().Contain(
            "rc_api_resource_scope_allows(\"PortfolioId\", NULL, NULL, NULL, NULL, \"TenantAccountId\", NULL, TRUE, TRUE, FALSE)");
        normalizedSql.Should().Contain(
            "rc_api_resource_scope_allows(\"PortfolioId\", NULL, NULL, NULL, NULL, \"TenantAccountId\", NULL, FALSE, TRUE, FALSE)");
        CreateSql.Should().Contain("rc_api_all_properties_scope_allows(NULLIF(current_setting('app.current_portfolio_id', true), '')::integer)");
        CreateSql.Should().NotContain("app.rls_bypass_reason");
        CreateSql.Should().NotContain("app.is_admin");
    }

    [Fact]
    public void ResourceScopeAssignedWorkAuthorization_UsesSimulationEffectiveTime()
    {
        var functionSql = ExtractSqlSlice(
            CreateSql,
            "CREATE OR REPLACE FUNCTION rc_api_resource_scope_allows(",
            "CREATE OR REPLACE FUNCTION rc_account_bootstrap_audit_allows(");
        var assignedWorkSql = ExtractSqlSlice(
            functionSql,
            "allow_assigned_work AND target_work_order_id IS NOT NULL",
            "ALTER FUNCTION rc_api_resource_scope_allows");

        functionSql.Should().Contain("WITH business_clock AS MATERIALIZED");
        functionSql.Should().Contain("clock.\"Mode\"");
        functionSql.Should().Contain("END\n  FROM business_clock;");
        functionSql.Should().NotContain("CROSS JOIN business_clock");
        assignedWorkSql.Should().Contain("FROM public.\"WorkspaceMemberships\" membership");
        assignedWorkSql.Should().Contain(
            "membership.\"EffectiveFromUtc\" <= business_clock.effective_at_utc");
        assignedWorkSql.Should().Contain(
            "membership.\"EffectiveToUtc\" > business_clock.effective_at_utc");
        assignedWorkSql.Should().Contain(
            "assignment.\"EffectiveFromUtc\" <= business_clock.effective_at_utc");
        assignedWorkSql.Should().Contain(
            "assignment.\"EffectiveToUtc\" > business_clock.effective_at_utc");
        assignedWorkSql.Should().Contain(
            "responsibility.\"EffectiveFromUtc\" <= business_clock.effective_at_utc");
        assignedWorkSql.Should().Contain(
            "responsibility.\"EffectiveToUtc\" > business_clock.effective_at_utc");
        assignedWorkSql.Should().NotContain("CURRENT_TIMESTAMP");
    }

    [Fact]
    public void AccountBootstrapAudit_HasExactTransactionBoundAdmissionForInsertReturning()
    {
        CreateSql.Should().Contain(
            "CREATE POLICY tenant_select ON \"AtomicAuditLogs\" FOR SELECT USING\n" +
            "  (rc_api_scope_allows(\"PortfolioId\") OR rc_account_bootstrap_audit_allows(");
        CreateSql.Should().Contain(
            "CREATE POLICY tenant_insert ON \"AtomicAuditLogs\" FOR INSERT WITH CHECK\n" +
            "  (rc_api_scope_allows(\"PortfolioId\") OR rc_account_bootstrap_audit_allows(");
        CreateSql.Should().Contain("target_command_type = 'auth.account.bootstrap'");
        CreateSql.Should().Contain("target_mutation_ordinal = 1");
        CreateSql.Should().Contain("target_entity_type = 'ApplicationUser'");
        CreateSql.Should().Contain("target_entity_id = target_user_id");
        CreateSql.Should().Contain("target_actor_label = 'authentication:registration'");
        CreateSql.Should().Contain("receipt.\"AttemptId\" = target_attempt_id");
        CreateSql.Should().Contain("receipt.xmin = pg_current_xact_id()::xid");
        CreateSql.Should().Contain("access_context.\"PortfolioId\" = target_portfolio_id");
    }

    [Fact]
    public void PreAuthAudit_HasExactTransactionBoundAdmissionForSessionStartAndContextChallenge()
    {
        CreateSql.Should().Contain(
            "OR rc_pre_auth_audit_allows(\n" +
            "    \"PortfolioId\", \"AttemptId\", \"CommandType\", \"CommandIdempotencyKey\", \"MutationOrdinal\",");
        CreateSql.Should().Contain("target_command_idempotency_key ~ '^operation:[0-9a-f]{32}$'");
        CreateSql.Should().Contain("target_mutation_ordinal = 1");
        CreateSql.Should().Contain("target_entity_type = 'WorkspaceAccessContext'");
        CreateSql.Should().Contain("target_operation = 1");
        CreateSql.Should().Contain("target_new_values ->> 'AuditRootAccessContextId' = target_entity_id::text");
        CreateSql.Should().Contain("target_new_values ->> 'UserId' = target_user_id::text");
        CreateSql.Should().Contain("access_context.\"Status\" = 'Active'");
        CreateSql.Should().Contain("access_context.\"SuspendedAtUtc\" IS NULL");
        CreateSql.Should().Contain("access_context.\"RevokedAtUtc\" IS NULL");
        CreateSql.Should().Contain("target_command_type = 'auth-session:start'");
        CreateSql.Should().Contain("target_actor_label = 'authentication:session'");
        CreateSql.Should().Contain("target_change_reason = 'Authentication session started'");
        CreateSql.Should().Contain("session.\"Id\"::text = target_new_values ->> 'AuthSessionId'");
        CreateSql.Should().Contain("session.\"Status\" = 'Active'");
        CreateSql.Should().Contain("session.\"ExpiresAtUtc\" > CURRENT_TIMESTAMP");
        CreateSql.Should().Contain("session.xmin = pg_current_xact_id()::xid");
        CreateSql.Should().Contain("target_command_type = 'auth-context-selection:issue'");
        CreateSql.Should().Contain("target_actor_label = 'authentication:context-selection'");
        CreateSql.Should().Contain("target_change_reason = 'Login context selection challenge issued'");
        CreateSql.Should().Contain("challenge.\"Id\"::text = target_new_values ->> 'ChallengeId'");
        CreateSql.Should().Contain("challenge.xmin = pg_current_xact_id()::xid");
    }

    [Fact]
    public void PreAuthEmailAudit_HasExactTransactionBoundAdmissionForConfirmationAndReset()
    {
        var normalizedSql = Regex.Replace(CreateSql, @"\s+", " ");

        CreateSql.Should().Contain(
            "OR rc_pre_auth_email_audit_allows(\n" +
            "    \"PortfolioId\", \"AttemptId\", \"CommandType\", \"CommandIdempotencyKey\", \"MutationOrdinal\",");
        normalizedSql.Should().Contain(
            "target_command_type IN ( 'auth.email.email-confirmation', 'auth.email.password-reset')");
        normalizedSql.Should().Contain(
            "target_command_idempotency_key ~ ('^' || target_user_id::text || ':[0-9a-f]{64}$')");
        normalizedSql.Should().Contain("target_mutation_ordinal = 1");
        normalizedSql.Should().Contain("target_entity_type = 'ApplicationUser'");
        normalizedSql.Should().Contain("target_entity_id = target_user_id");
        normalizedSql.Should().Contain("target_operation = 1");
        normalizedSql.Should().Contain("target_actor_label = 'authentication:email-outbox'");
        normalizedSql.Should().Contain("target_change_reason = 'Transactional account email enqueued'");
        normalizedSql.Should().Contain("target_new_values ->> 'TargetUserId' = target_user_id::text");
        normalizedSql.Should().Contain("target_command_type = 'auth.email.' || (target_new_values ->> 'EmailKind')");
        normalizedSql.Should().Contain(
            "target_new_values ->> 'DeliveryIdempotencyKey' = 'auth:' || (target_new_values ->> 'EmailKind') || ':' || target_command_idempotency_key");
        normalizedSql.Should().Contain(
            "FROM public.rc_list_effective_access_contexts( target_user_id, clock_timestamp()) option");
        normalizedSql.Should().Contain("ORDER BY option.\"AccessContextId\" LIMIT 1");
        normalizedSql.Should().Contain(
            "root.\"AccessContextId\" = (target_new_values ->> 'AuditRootAccessContextId')::integer");
        normalizedSql.Should().Contain("receipt.\"AttemptId\" = target_attempt_id");
        normalizedSql.Should().Contain("receipt.xmin = pg_current_xact_id()::xid");
    }

    [Fact]
    public void PreAuthAccountSecurityAudit_HasExactTransactionBoundAdmissionForConfirmationAndReset()
    {
        var normalizedSql = Regex.Replace(CreateSql, @"\s+", " ");

        CreateSql.Should().Contain(
            "OR rc_pre_auth_account_security_audit_allows(\n" +
            "    \"PortfolioId\", \"AttemptId\", \"CommandType\", \"CommandIdempotencyKey\", \"MutationOrdinal\",");
        normalizedSql.Should().Contain(
            "target_command_type IN ( 'auth.email.confirm', 'auth.email.google-confirm', 'auth.password.reset', 'workspace-invitation.activate')");
        normalizedSql.Should().Contain(
            "target_command_idempotency_key ~ ('^' || target_user_id::text || ':[0-9a-f]{64}$')");
        normalizedSql.Should().Contain("target_mutation_ordinal = 1");
        normalizedSql.Should().Contain("target_entity_type = 'ApplicationUser'");
        normalizedSql.Should().Contain("target_entity_id = target_user_id");
        normalizedSql.Should().Contain("target_operation = 1");
        normalizedSql.Should().Contain("target_actor_label = 'authentication:account-security'");
        normalizedSql.Should().Contain("target_new_values ->> 'TargetUserId' = target_user_id::text");
        normalizedSql.Should().Contain(
            "target_new_values ->> 'SecurityIntentHash' ~ '^[0-9a-f]{64}$'");
        normalizedSql.Should().Contain("target_new_values ->> 'SecurityEvent' = 'EmailConfirmed'");
        normalizedSql.Should().Contain("target_change_reason = 'Account email confirmed'");
        normalizedSql.Should().Contain("target_new_values ->> 'SecurityEvent' = 'GoogleEmailConfirmed'");
        normalizedSql.Should().Contain("target_change_reason = 'Google-verified account email confirmed'");
        normalizedSql.Should().Contain("target_new_values ->> 'SecurityEvent' = 'PasswordReset'");
        normalizedSql.Should().Contain("target_change_reason = 'Password reset completed'");
        normalizedSql.Should().Contain(
            "split_part(target_command_idempotency_key, ':', 2) = target_new_values ->> 'SecurityIntentHash'");
        normalizedSql.Should().Contain("user_row.\"EmailConfirmed\" = TRUE");
        normalizedSql.Should().Contain("user_row.\"PasswordHash\" IS NOT NULL");
        normalizedSql.Should().Contain("user_row.\"AccessFailedCount\" = 0");
        normalizedSql.Should().Contain("user_row.\"LockoutEnd\" IS NULL");
        normalizedSql.Should().Contain("user_row.xmin = pg_current_xact_id()::xid");
        normalizedSql.Should().Contain(
            "root.\"AccessContextId\" = (target_new_values ->> 'AuditRootAccessContextId')::integer");
        normalizedSql.Should().Contain("receipt.\"AttemptId\" = target_attempt_id");
        normalizedSql.Should().Contain("receipt.xmin = pg_current_xact_id()::xid");
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
        portfolioDeleteTables.Remove("ExpenseAllocations").Should().BeTrue(
            "the post-baseline L02 migration owns the allocation table's grants and RLS");
        portfolioDeleteTables.Remove("LlmUsageEvidence").Should().BeTrue(
            "the post-baseline L07 migration owns the usage-evidence table's grants and RLS");
        var preservedTables = FoundationBaselinePostgreSql.SandboxGraduationPreservedTables
            .ToHashSet(StringComparer.Ordinal);
        preservedTables.Remove("WorkspaceLlmCredentials").Should().BeTrue(
            "the post-baseline L07 migration owns the reusable credential table's grants and RLS");

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
    public void RlsClassification_CoversEveryMappedBaseTableExactlyOnce()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=rls_classification_contract;Username=contract;Password=contract")
            .Options;
        using var db = new RentalCommandDbContext(options);

        var mappedBaseTables = db.Model.GetEntityTypes()
            .Where(entityType => entityType.GetViewName() is null)
            .Select(entityType => entityType.GetTableName())
            .Where(table => table is not null)
            .Select(table => table!)
            .ToHashSet(StringComparer.Ordinal);
        mappedBaseTables.Remove("ExpenseAllocations").Should().BeTrue(
            "the L02 allocation table is deliberately installed and secured after InitialCreate");
        mappedBaseTables.Remove("WorkspaceLlmCredentials").Should().BeTrue(
            "the L07 credential table is deliberately installed and secured after InitialCreate");
        mappedBaseTables.Remove("LlmUsageEvidence").Should().BeTrue(
            "the L07 usage-evidence table is deliberately installed and secured after InitialCreate");
        mappedBaseTables.Remove("LoanPaymentCorrections").Should().BeTrue(
            "the YS-295 correction table is deliberately installed and secured after InitialCreate");

        var direct = FoundationBaselinePostgreSql.DirectPortfolioTables
            .ToHashSet(StringComparer.Ordinal);
        var nullable = FoundationBaselinePostgreSql.NullablePortfolioTables
            .ToHashSet(StringComparer.Ordinal);
        var children = FoundationBaselinePostgreSql.ChildPortfolioTables
            .Select(policy => policy.Table)
            .ToHashSet(StringComparer.Ordinal);
        var globals = FoundationBaselinePostgreSql.GlobalAuthAndSystemTables
            .ToHashSet(StringComparer.Ordinal);

        direct.Intersect(nullable).Should().BeEmpty();
        direct.Intersect(children).Should().BeEmpty();
        direct.Intersect(globals).Should().BeEmpty();
        nullable.Intersect(children).Should().BeEmpty();
        nullable.Intersect(globals).Should().BeEmpty();
        children.Intersect(globals).Should().BeEmpty();

        direct
            .Concat(nullable)
            .Concat(children)
            .Concat(globals)
            .Append("Portfolios")
            .ToHashSet(StringComparer.Ordinal)
            .Should().BeEquivalentTo(mappedBaseTables,
                "every mapped table must be deliberately classified before it can enter the clean baseline");
    }

    [Fact]
    public void LoanPaymentCorrections_AreSecuredByTheirPostBaselineMigration()
    {
        var migration = new AddLoanPaymentCorrections();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddLoanPaymentCorrections).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);
        var migrationSql = Regex.Replace(
            string.Join(
                Environment.NewLine,
                builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql)),
            @"\s+",
            " ");

        migrationSql.Should().Contain(
            "GRANT SELECT, INSERT ON TABLE \"LoanPaymentCorrections\" TO rentalcommand_api;");
        migrationSql.Should().Contain(
            "GRANT USAGE, SELECT ON SEQUENCE \"LoanPaymentCorrections_Id_seq\" TO rentalcommand_api;");
        migrationSql.Should().Contain(
            "GRANT SELECT ON TABLE \"LoanPaymentCorrections\" TO rentalcommand_engine;");
        migrationSql.Should().Contain(
            "ALTER TABLE \"LoanPaymentCorrections\" ENABLE ROW LEVEL SECURITY;");
        migrationSql.Should().Contain(
            "ALTER TABLE \"LoanPaymentCorrections\" FORCE ROW LEVEL SECURITY;");
        migrationSql.Should().NotContain(
            "GRANT UPDATE ON TABLE \"LoanPaymentCorrections\" TO rentalcommand_api;");
        migrationSql.Should().NotContain(
            "GRANT DELETE ON TABLE \"LoanPaymentCorrections\" TO rentalcommand_api;");
    }

    [Fact]
    public void ExpenseAllocations_AreSecuredByTheirPostBaselineContract()
    {
        var createSql = string.Join(
            Environment.NewLine, ExpenseAllocationPostgreSqlContract.CreateStatements);

        FoundationBaselinePostgreSql.SandboxGraduationDeleteTables
            .Should().Contain("ExpenseAllocations");
        createSql.Should().Contain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"ExpenseAllocations\" TO rentalcommand_api;");
        createSql.Should().Contain(
            "GRANT USAGE, SELECT ON SEQUENCE \"ExpenseAllocations_Id_seq\" TO rentalcommand_api;");
        createSql.Should().Contain(
            "ALTER TABLE \"ExpenseAllocations\" ENABLE ROW LEVEL SECURITY;");
        createSql.Should().Contain(
            "ALTER TABLE \"ExpenseAllocations\" FORCE ROW LEVEL SECURITY;");
        createSql.Should().Contain(
            "CREATE CONSTRAINT TRIGGER trg_expense_allocation_balance_from_allocation");
        createSql.Should().Contain("DEFERRABLE INITIALLY DEFERRED");
        createSql.Should().Contain("pg_advisory_xact_lock(74002, target_expense_id)");
        createSql.Should().Contain("COALESCE(SUM(allocation.\"Amount\"), 0)");
    }

    [Fact]
    public void WorkspaceLlmTables_AreSecuredAndClassifiedByTheirPostBaselineMigration()
    {
        var migration = new AddWorkspaceLlmCredentials();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        var up = typeof(AddWorkspaceLlmCredentials).GetMethod(
            "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        up.Invoke(migration, [builder]);
        var migrationSql = Regex.Replace(
            string.Join(
                Environment.NewLine,
                builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql)),
            @"\s+",
            " ");

        FoundationBaselinePostgreSql.SandboxGraduationPreservedTables
            .Should().Contain("WorkspaceLlmCredentials");
        FoundationBaselinePostgreSql.SandboxGraduationDeleteTables
            .Should().NotContain("WorkspaceLlmCredentials");
        FoundationBaselinePostgreSql.SandboxGraduationDeleteTables
            .Should().Contain("LlmUsageEvidence");
        FoundationBaselinePostgreSql.SandboxGraduationPreservedTables
            .Should().NotContain("LlmUsageEvidence");

        foreach (var table in new[] { "WorkspaceLlmCredentials", "LlmUsageEvidence" })
        {
            migrationSql.Should().Contain(
                $"ALTER TABLE \"{table}\" ENABLE ROW LEVEL SECURITY;");
            migrationSql.Should().Contain(
                $"ALTER TABLE \"{table}\" FORCE ROW LEVEL SECURITY;");
            migrationSql.Should().Contain(
                $"CREATE POLICY tenant_select ON \"{table}\" FOR SELECT USING (rc_api_scope_allows(\"PortfolioId\"));");
            migrationSql.Should().Contain(
                $"CREATE POLICY tenant_insert ON \"{table}\" FOR INSERT WITH CHECK (rc_api_scope_allows(\"PortfolioId\"));");
        }

        migrationSql.Should().Contain(
            "CREATE POLICY tenant_update ON \"WorkspaceLlmCredentials\" FOR UPDATE " +
            "USING (rc_api_scope_allows(\"PortfolioId\")) " +
            "WITH CHECK (rc_api_scope_allows(\"PortfolioId\"));");
        migrationSql.Should().Contain(
            "CREATE POLICY tenant_delete ON \"WorkspaceLlmCredentials\" " +
            "FOR DELETE USING (rc_api_scope_allows(\"PortfolioId\"));");
        migrationSql.Should().Contain(
            "CREATE POLICY tenant_delete ON \"LlmUsageEvidence\" " +
            "FOR DELETE USING (rc_sandbox_graduation_allows(\"PortfolioId\"));");
        migrationSql.Should().NotContain(
            "CREATE POLICY tenant_delete ON \"LlmUsageEvidence\" " +
            "FOR DELETE USING (rc_api_scope_allows(\"PortfolioId\"));");

        migrationSql.Should().Contain(
            "GRANT SELECT, INSERT, UPDATE, DELETE " +
            "ON TABLE \"WorkspaceLlmCredentials\" TO rentalcommand_api;");
        migrationSql.Should().Contain(
            "GRANT SELECT ON TABLE \"WorkspaceLlmCredentials\" TO rentalcommand_engine;");
        migrationSql.Should().Contain(
            "GRANT SELECT, INSERT, DELETE ON TABLE \"LlmUsageEvidence\" TO rentalcommand_api;");
        migrationSql.Should().Contain(
            "GRANT SELECT, INSERT ON TABLE \"LlmUsageEvidence\" TO rentalcommand_engine;");
        migrationSql.Should().NotContain(
            "GRANT SELECT, INSERT, DELETE ON TABLE \"LlmUsageEvidence\" TO rentalcommand_engine;");
        migrationSql.Should().NotContain(
            "GRANT DELETE ON TABLE \"LlmUsageEvidence\" TO rentalcommand_engine;");
        migrationSql.Should().Contain(
            "GRANT USAGE, SELECT ON SEQUENCE \"WorkspaceLlmCredentials_Id_seq\" TO rentalcommand_api;");
        migrationSql.Should().Contain(
            "GRANT USAGE, SELECT ON SEQUENCE \"LlmUsageEvidence_Id_seq\" " +
            "TO rentalcommand_api, rentalcommand_engine;");
    }

    [Fact]
    public void InitialWorkspaceBootstrapRepair_DeploysAlwaysOnRentChargeDefault()
    {
        var migration = new RepairInitialWorkspaceRentChargeBootstrap();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(RepairInitialWorkspaceRentChargeBootstrap).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var sql = Regex.Replace(
            builder.Operations.OfType<SqlOperation>().Should().ContainSingle().Which.Sql,
            @"\s+",
            " ");
        var bootstrap = Regex.Match(
            sql,
            @"CREATE OR REPLACE FUNCTION rc_bootstrap_initial_workspace\(.*?ALTER FUNCTION rc_bootstrap_initial_workspace",
            RegexOptions.Singleline).Value;

        bootstrap.Should().NotBeEmpty();
        bootstrap.Should().Contain(
            "(new_portfolio_id, TRUE, 5, FALSE, 5, TRUE, 60, TRUE, TRUE, 8, FALSE,");
        bootstrap.Should().NotContain(
            "(new_portfolio_id, FALSE, 5, FALSE, 5, TRUE, 60, TRUE, TRUE, 8, FALSE,");
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
    public void GlobalQueueDeletes_RequireDatabaseValidatedSandboxAuthority()
    {
        FoundationBaselinePostgreSql.SandboxGraduationGlobalDeleteTables.Should().BeEquivalentTo(
            ["AtomicCommandReceipts", "OutboxMessages", "ProviderInboxEvents"]);
        CreateSql.Should().Contain("IF NOT rc_sandbox_graduation_allows(target_portfolio_id) THEN");
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
            "CREATE ROLE rentalcommand_api LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1;");
        CreateSql.Should().Contain(
            "CREATE ROLE rentalcommand_engine LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1;");
        CreateSql.Should().Contain(
            "CREATE ROLE rentalcommand_rls_authority NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION BYPASSRLS CONNECTION LIMIT -1;");
        CreateSql.Should().Contain("NOT runtime_role.rolcanlogin OR runtime_role.rolsuper OR runtime_role.rolinherit");
        CreateSql.Should().Contain("runtime_role.rolcanlogin OR runtime_role.rolsuper OR runtime_role.rolinherit");
        CreateSql.Should().Contain("runtime_role.rolconnlimit <> -1");
        CreateSql.Should().Contain("runtime_role.rolvaliduntil IS NOT NULL");
        CreateSql.Should().Contain("runtime_role.rolconfig IS NOT NULL");
        CreateSql.Should().Contain("inherited_membership.member = runtime_role.oid");
        CreateSql.Should().Contain("Existing role rentalcommand_api has incompatible cluster-wide attributes");
        CreateSql.Should().Contain("Existing role rentalcommand_engine has incompatible cluster-wide attributes");
        CreateSql.Should().Contain("Existing role rentalcommand_rls_authority has incompatible cluster-wide attributes");
        CreateSql.Should().NotContain("ALTER ROLE");
        DropSql.Should().NotContain("DROP ROLE");
        DropSql.Should().NotContain("REVOKE rentalcommand_api, rentalcommand_engine FROM %I");
    }

    [Fact]
    public void RlsAuthority_GrantsCoverTheCompleteReviewedDependencySet()
    {
        FoundationBaselinePostgreSql.RlsAuthoritySelectTables.Should().BeEquivalentTo(
        [
            "AspNetUsers", "AtomicCommandReceipts", "AuthSessions", "CapabilityDefinitions", "LeaseManagementParties",
            "LeaseManagements", "LegalDocumentArtifacts", "LoginContextSelectionChallenges", "MembershipRoleAssignmentProperties", "MembershipRoleAssignments",
            "OwnerEntities", "OwnerUserAccesses", "Portfolios", "Properties", "PropertyOwnerships", "RoleProfileCapabilities",
            "RoleProfiles", "SignatureRequests", "SignatureSigners", "SimulationClocks", "StoredFiles", "SystemNoticeTemplateVersions", "TenantAccounts",
            "TenantUserAccesses", "Units", "WorkOrders", "WorkOrderResponsibilities",
            "WorkspaceAccessContexts", "WorkspaceInvitations", "WorkspaceMemberships", "WorkspaceNoticeTemplateVersions",
        ]);
        FoundationBaselinePostgreSql.RlsAuthoritySelectViews.Should().BeEquivalentTo(
            ["vw_access_envelopes", "vw_effective_tenant_access"]);
        FoundationBaselinePostgreSql.RlsAuthorityInsertTables.Should().BeEquivalentTo(
        [
            "AutomationSettings", "MembershipRoleAssignments", "OwnerEntities", "OwnerUserAccesses",
            "Portfolios", "TeamRoutingRules", "TenantNoticePolicies", "UserAlertPreferences",
            "WorkspaceAccessContexts", "WorkspaceMemberships", "WorkspaceNoticeTemplateVersions",
        ]);
        FoundationBaselinePostgreSql.RlsAuthorityUpdateTables.Should().BeEquivalentTo(
            ["AspNetUsers", "WorkspaceInvitations"]);
        FoundationBaselinePostgreSql.RlsAuthorityExecuteFunctions.Should().BeEquivalentTo(
            ["rc_business_date(integer)", "rc_effective_now_utc(integer)"]);
        FoundationBaselinePostgreSql.RlsAuthorityOwnedFunctions.Should().BeEquivalentTo(
        [
            "rc_api_scope_allows(integer)",
            "rc_api_effective_capability_scopes(integer, uuid, integer, integer, bigint, text[], text)",
            "rc_api_all_properties_scope_allows(integer)",
            "rc_public_application_scope_allows(integer)",
            "rc_public_signing_scope_allows(integer)",
            "rc_public_signing_request_allows(integer, integer)",
            "rc_public_signing_artifact_allows(integer, integer)",
            "rc_public_signing_file_allows(integer, integer, text, bigint)",
            "rc_api_resource_scope_allows(integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean)",
            "rc_account_bootstrap_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text)",
            "rc_pre_auth_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)",
            "rc_pre_auth_email_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)",
            "rc_pre_auth_account_security_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)",
            "rc_sandbox_graduation_allows(integer)",
            "rc_access_context_is_effective(integer, integer, timestamp with time zone)",
            "rc_list_effective_access_contexts(integer, timestamp with time zone)",
            "rc_get_access_envelope_for_session(uuid, integer, integer, bigint, timestamp with time zone)",
            "rc_bootstrap_initial_workspace(integer, text, text, text, text, timestamp with time zone)",
            "rc_activate_workspace_invitation(bigint, integer, text, text, text, text)",
        ]);

        var normalizedAuthoritySql = Regex.Replace(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSql, @"\s+", " ");
        Regex.Matches(normalizedAuthoritySql, "SECURITY DEFINER").Should().HaveCount(
            FoundationBaselinePostgreSql.RlsAuthorityOwnedFunctions.Count);
        foreach (var function in FoundationBaselinePostgreSql.RlsAuthorityOwnedFunctions)
        {
            normalizedAuthoritySql.Should().Contain(
                $"ALTER FUNCTION {function} OWNER TO rentalcommand_rls_authority;");
        }

        var directlyReferencedRelations = Regex.Matches(
                FoundationBaselinePostgreSql.RlsAuthorityFunctionSql,
                "public\\.\\\"(?<relation>[^\\\"]+)\\\"")
            .Select(match => match.Groups["relation"].Value)
            .ToHashSet(StringComparer.Ordinal);
        directlyReferencedRelations.Should().BeSubsetOf(
            FoundationBaselinePostgreSql.RlsAuthoritySelectTables
                .Concat(FoundationBaselinePostgreSql.RlsAuthoritySelectViews)
                .Concat(FoundationBaselinePostgreSql.RlsAuthorityInsertTables)
                .Concat(FoundationBaselinePostgreSql.RlsAuthorityUpdateTables),
            "every directly referenced authority relation must have an explicit minimum grant");

        var insertTargets = Regex.Matches(
                FoundationBaselinePostgreSql.RlsAuthorityFunctionSql,
                "INSERT INTO public\\.\\\"(?<relation>[^\\\"]+)\\\"")
            .Select(match => match.Groups["relation"].Value)
            .ToHashSet(StringComparer.Ordinal);
        insertTargets.Should().BeEquivalentTo(FoundationBaselinePostgreSql.RlsAuthorityInsertTables);

        var tenantAccessViewDependencies = new[]
        {
            "Portfolios", "WorkspaceAccessContexts", "TenantUserAccesses",
            "LeaseManagementParties", "LeaseManagements", "TenantAccounts",
        };
        foreach (var table in tenantAccessViewDependencies)
        {
            RelationshipAccessProjectionSql.Create.Should().Contain($"\"{table}\"");
            FoundationBaselinePostgreSql.RlsAuthoritySelectTables.Should().Contain(table);
        }
        LeaseEffectiveClockSql.CreateBusinessDate.Should().Contain("rc_effective_now_utc(portfolio_id)");
        LeaseEffectiveClockSql.CreateBusinessDate.Should().Contain("\"SimulationClocks\"");
        LeaseEffectiveClockSql.CreateEffectiveNowUtc.Should().Contain("\"SimulationClocks\"");

        foreach (var table in FoundationBaselinePostgreSql.RlsAuthoritySelectTables)
        {
            CreateSql.Should().Contain(
                $"GRANT SELECT ON TABLE \"{table}\" TO rentalcommand_rls_authority;");
            DropSql.Should().Contain(
                $"REVOKE SELECT ON TABLE \"{table}\" FROM rentalcommand_rls_authority;");
        }
        foreach (var view in FoundationBaselinePostgreSql.RlsAuthoritySelectViews)
        {
            CreateSql.Should().Contain(
                $"GRANT SELECT ON TABLE \"{view}\" TO rentalcommand_rls_authority;");
            DropSql.Should().Contain(
                $"REVOKE SELECT ON TABLE \"{view}\" FROM rentalcommand_rls_authority;");
        }
        foreach (var table in FoundationBaselinePostgreSql.RlsAuthorityInsertTables)
        {
            CreateSql.Should().Contain(
                $"GRANT INSERT ON TABLE \"{table}\" TO rentalcommand_rls_authority;");
            DropSql.Should().Contain(
                $"REVOKE INSERT ON TABLE \"{table}\" FROM rentalcommand_rls_authority;");
        }
        foreach (var table in FoundationBaselinePostgreSql.RlsAuthorityUpdateTables)
        {
            CreateSql.Should().Contain(
                $"GRANT UPDATE ON TABLE \"{table}\" TO rentalcommand_rls_authority;");
            DropSql.Should().Contain(
                $"REVOKE UPDATE ON TABLE \"{table}\" FROM rentalcommand_rls_authority;");
        }
        foreach (var function in FoundationBaselinePostgreSql.RlsAuthorityExecuteFunctions)
        {
            CreateSql.Should().Contain(
                $"GRANT EXECUTE ON FUNCTION {function} TO rentalcommand_rls_authority;");
            DropSql.Should().Contain(
                $"REVOKE EXECUTE ON FUNCTION {function} FROM rentalcommand_rls_authority;");
        }

        CreateSql.Should().Contain("INSERT INTO public.\"OwnerEntities\"");
        CreateSql.Should().Contain("RETURNING \"Id\" INTO new_owner_entity_id;");
        CreateSql.Should().Contain("FROM public.\"vw_effective_tenant_access\"");
        CreateSql.Should().Contain("GRANT SELECT ON TABLE \"TenantUserAccesses\" TO rentalcommand_rls_authority;");
        CreateSql.Should().Contain("GRANT SELECT ON TABLE \"SimulationClocks\" TO rentalcommand_rls_authority;");
        CreateSql.Should().Contain("Initial workspace bootstrap is API-only");
        CreateSql.Should().NotContain(
            "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO rentalcommand_rls_authority;");
    }

    [Fact]
    public void ResourceScopePolicies_UseOneTimeRequestGatesWithoutChangingPublicScopes()
    {
        var normalizedResourceSql = Regex.Replace(FoundationBaselinePostgreSql.ResourcePoliciesSql, @"\s+", " ");
        normalizedResourceSql.Should().Contain("WHEN session_user = 'rentalcommand_engine' THEN TRUE");
        normalizedResourceSql.Should().Contain(
            "OR \"PortfolioId\" IS DISTINCT FROM NULLIF(current_setting('app.current_portfolio_id', true), '')::integer");
        normalizedResourceSql.Should().Contain(
            "WHEN NOT (SELECT rc_api_scope_allows(NULLIF(current_setting('app.current_portfolio_id', true), '')::integer)) THEN FALSE WHEN (SELECT rc_api_all_properties_scope_allows(NULLIF(current_setting('app.current_portfolio_id', true), '')::integer)) THEN TRUE");
        normalizedResourceSql.IndexOf("WHEN NOT (SELECT rc_api_scope_allows", StringComparison.Ordinal).Should().BeLessThan(
            normalizedResourceSql.IndexOf("WHEN (SELECT rc_api_all_properties_scope_allows", StringComparison.Ordinal),
            "the all-properties helper is secondary-only after canonical live session/context/revision validation");
        normalizedResourceSql.Should().Contain("ELSE rc_api_resource_scope_allows(");

        var normalizedAuthoritySql = Regex.Replace(FoundationBaselinePostgreSql.RlsAuthorityFunctionSql, @"\s+", " ");
        var resourceFunction = Regex.Match(
            normalizedAuthoritySql,
            @"CREATE OR REPLACE FUNCTION rc_api_resource_scope_allows\(.*?ALTER FUNCTION rc_api_resource_scope_allows",
            RegexOptions.Singleline).Value;
        resourceFunction.Should().NotContain("rc_api_scope_allows");

        FoundationBaselinePostgreSql.PublicSigningPoliciesSql.Should().NotContain("rc_api_resource_scope_allows");
        CreateSql.Should().Contain("CREATE POLICY public_application_select ON \"Units\" FOR SELECT");
        CreateSql.Should().Contain("CREATE POLICY public_signing_select ON \"SignatureRequests\" FOR SELECT");

        foreach (var signature in new[]
                 {
                     "rc_public_application_scope_allows(integer)",
                     "rc_public_signing_scope_allows(integer)",
                     "rc_public_signing_request_allows(integer, integer)",
                     "rc_public_signing_artifact_allows(integer, integer)",
                     "rc_public_signing_file_allows(integer, integer, text, bigint)",
                 })
        {
            normalizedAuthoritySql.Should().Contain(
                $"ALTER FUNCTION {signature} OWNER TO rentalcommand_rls_authority;");
            normalizedAuthoritySql.Should().Contain(
                $"REVOKE ALL ON FUNCTION {signature} FROM PUBLIC;");
            normalizedAuthoritySql.Should().Contain(
                $"GRANT EXECUTE ON FUNCTION {signature} TO rentalcommand_api, rentalcommand_engine;");
        }
    }

    [Fact]
    public void RlsAuthorityVersions_PreserveHistoricalL15AndInstallCurrentBootstrapAuthority()
    {
        FoundationBaselinePostgreSql.RlsAuthorityFunctionSql.Should()
            .BeSameAs(FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260728);
        FoundationBaselinePostgreSql.ResourcePoliciesSql.Should()
            .BeSameAs(FoundationBaselinePostgreSql.ResourcePoliciesSqlV20260719);

        const string secondaryFunctionMarker = "-- Secondary scope shortcut only.";
        var historicalAuthority = FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260719;
        var currentAuthority = FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260724;
        var historicalMarkerIndex = historicalAuthority.IndexOf(
            secondaryFunctionMarker,
            StringComparison.Ordinal);
        var currentMarkerIndex = currentAuthority.IndexOf(secondaryFunctionMarker, StringComparison.Ordinal);
        historicalMarkerIndex.Should().BeGreaterThan(0);
        currentMarkerIndex.Should().BeGreaterThan(0);
        var historicalScopeAuthority = historicalAuthority[..historicalMarkerIndex];
        var currentScopeAuthority = currentAuthority[..currentMarkerIndex];

        historicalScopeAuthority.Should().NotContain("receipt.\"CommandType\" = 'auth.account.bootstrap'");
        currentScopeAuthority.Should().Contain("WHEN session_user = 'rentalcommand_engine' THEN TRUE");
        currentScopeAuthority.Should().Contain(
            "WHEN session_user IS DISTINCT FROM 'rentalcommand_api'\n" +
            "      OR target_portfolio_id IS NULL\n" +
            "      OR target_portfolio_id <= 0\n" +
            "    THEN FALSE");
        currentScopeAuthority.Should().Contain("receipt.\"CommandType\" = 'auth.account.bootstrap'");
        currentScopeAuthority.Should().Contain("receipt.xmin = pg_current_xact_id()::xid");
        currentScopeAuthority.Should().Contain("user_row.xmin = pg_current_xact_id()::xid");
        currentScopeAuthority.Should().Contain("portfolio.xmin = pg_current_xact_id()::xid");
        currentScopeAuthority.Should().Contain("access_context.xmin = pg_current_xact_id()::xid");
        currentScopeAuthority.Should().Contain("membership.xmin = pg_current_xact_id()::xid");
        currentScopeAuthority.Should().Contain("assignment.xmin = pg_current_xact_id()::xid");
        currentScopeAuthority.Should().Contain("assignment.\"RoleProfileId\" = 1");
        currentScopeAuthority.Should().Contain("assignment.\"ScopeKind\" = 'AllProperties'");
        currentScopeAuthority.Should().Contain(
            "ALTER FUNCTION rc_api_scope_allows(integer) OWNER TO rentalcommand_rls_authority;");
        currentScopeAuthority.Should().Contain(
            "REVOKE ALL ON FUNCTION rc_api_scope_allows(integer) FROM PUBLIC;");
        currentScopeAuthority.Should().Contain(
            "GRANT EXECUTE ON FUNCTION rc_api_scope_allows(integer)\n  TO rentalcommand_api, rentalcommand_engine;");
        const string canonicalSessionBranch =
            "ELSE EXISTS (\n      SELECT 1\n      FROM public.\"AuthSessions\" session";
        var historicalSessionIndex = historicalScopeAuthority.IndexOf(canonicalSessionBranch, StringComparison.Ordinal);
        var currentSessionIndex = currentScopeAuthority.IndexOf(canonicalSessionBranch, StringComparison.Ordinal);
        historicalSessionIndex.Should().BeGreaterThan(0);
        currentSessionIndex.Should().BeGreaterThan(0);
        currentScopeAuthority[currentSessionIndex..]
            .Should().Be(
                historicalScopeAuthority[historicalSessionIndex..],
                "the ordinary session/access/membership authority and owner/revoke/grant tail must remain exact");
        currentAuthority[currentMarkerIndex..]
            .Should().Be(
                historicalAuthority[historicalMarkerIndex..],
                "V20260724 may extend only rc_api_scope_allows; the remaining reviewed L15 authority stays byte-identical");

        var l15Migration = new OptimizeRlsRequestScope();
        var l15Builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        var up = typeof(OptimizeRlsRequestScope).GetMethod(
            "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        up.Invoke(l15Migration, [l15Builder]);
        l15Builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql).Should().Equal(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260719,
            FoundationBaselinePostgreSql.ResourcePoliciesSqlV20260719);
        l15Builder.Operations.Should().HaveCount(2);

        var currentMigration = new AddSuppliedLegalNoticeTemplateV2();
        var currentBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddSuppliedLegalNoticeTemplateV2).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(currentMigration, [currentBuilder]);
        var currentSql = currentBuilder.Operations.OfType<SqlOperation>()
            .Select(operation => operation.Sql)
            .ToArray();
        currentSql.Should().HaveCount(2);
        currentSql[0].Should().BeSameAs(FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260724);
        currentSql[1].Should().Contain("CREATE OR REPLACE FUNCTION rc_delete_fresh_workspace_notice_templates");
        currentSql[1].Should().NotContain("CREATE OR REPLACE FUNCTION rc_api_scope_allows");
        currentBuilder.Operations.OfType<InsertDataOperation>().Should().ContainSingle(
            "the legal-template seed remains a separate, deterministic migration operation");
        currentBuilder.Operations.Should().HaveCount(3);

        var optimizedMigration = new AddEffectiveCapabilityScopeAuthority();
        var optimizedBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddEffectiveCapabilityScopeAuthority).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(optimizedMigration, [optimizedBuilder]);
        optimizedBuilder.Operations.OfType<SqlOperation>().Should().ContainSingle()
            .Which.Sql.Should().BeSameAs(
                FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260725);
        optimizedBuilder.Operations.Should().ContainSingle(
            "the immutable optimization migration replaces only current scope authority and adds " +
            "the relational capability-scope authority");

        var businessClockMigration = new UseBusinessClockForCapabilityScopes();
        var businessClockBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(UseBusinessClockForCapabilityScopes).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(businessClockMigration, [businessClockBuilder]);
        businessClockBuilder.Operations.OfType<SqlOperation>().Should().ContainSingle()
            .Which.Sql.Should().BeSameAs(
                FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260727);

        var down = typeof(OptimizeRlsRequestScope).GetMethod(
            "Down", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var act = () => down.Invoke(l15Migration, [new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL")]);
        act.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<NotSupportedException>()
            .WithMessage("*intentionally irreversible*production latency defect*database backup*");
    }

    [Fact]
    public void SafeSuppliedLegalNoticeTemplateV3_IsTwoDeterministicInsertsAndOneSetBasedUpgrade()
    {
        var migration = new AddSafeSuppliedLegalNoticeTemplateV3();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddSafeSuppliedLegalNoticeTemplateV3).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var insert = builder.Operations.OfType<InsertDataOperation>().Should().ContainSingle().Subject;
        insert.Table.Should().Be("SystemNoticeTemplateVersions");
        insert.Values.GetLength(0).Should().Be(2);
        insert.Values.GetLength(1).Should().Be(9);
        insert.Values.Cast<object?>().Should().Contain(8).And.Contain(9);

        var sql = builder.Operations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        var normalizedSql = Regex.Replace(sql, @"\s+", " ");
        normalizedSql.Should().Contain("WITH qualifying AS MATERIALIZED")
            .And.Contain("policy.\"Mode\" = 'Draft'")
            .And.Contain("policy.\"Classification\" = 'Legal'")
            .And.Contain("current_template.\"IsCustomized\" = FALSE")
            .And.Contain("current_template.\"Subject\" = supplied_v2.\"Subject\"")
            .And.Contain("current_template.\"Body\" = supplied_v2.\"Body\"")
            .And.Contain("current_template.\"BasedOnSystemTemplateVersionId\" IN (6, 7)")
            .And.Contain("current_template.\"JurisdictionReviewedAtUtc\" IS NULL")
            .And.Contain("policy.\"JurisdictionReviewedAtUtc\" IS NULL")
            .And.Contain("INSERT INTO \"WorkspaceNoticeTemplateVersions\"")
            .And.Contain("UPDATE \"TenantNoticePolicies\" AS policy")
            .And.Contain(
                "policy.\"WorkspaceNoticeTemplateVersionId\" = qualifying.\"CurrentTemplateId\"");
        normalizedSql.Should().NotContain("DELETE FROM")
            .And.NotContain("DO $$")
            .And.NotContain("LOOP");
        builder.Operations.Should().HaveCount(2);

        var down = typeof(AddSafeSuppliedLegalNoticeTemplateV3).GetMethod(
            "Down", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var act = () => down.Invoke(
            migration,
            [new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL")]);
        act.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<NotSupportedException>()
            .WithMessage("*append-only*immutable workspace history*");
    }

    [Fact]
    public void EffectiveCapabilityScopeAuthority_IsApiOnlyCurrentAndFailClosed()
    {
        var delta = FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260727;
        var normalizedDelta = Regex.Replace(delta, @"\s+", " ");

        normalizedDelta.Should().Contain(
            "WHEN NULLIF(current_setting('app.auth_session_id', true), '') IS NOT NULL THEN EXISTS");
        normalizedDelta.IndexOf(
                "WHEN NULLIF(current_setting('app.auth_session_id', true), '') IS NOT NULL",
                StringComparison.Ordinal)
            .Should().BeLessThan(
                normalizedDelta.IndexOf(
                    "receipt.\"CommandType\" = 'auth.account.bootstrap'",
                    StringComparison.Ordinal),
                "a supplied invalid or stale session must never fall through to registration bootstrap");
        normalizedDelta.Should().Contain(
            "CREATE OR REPLACE FUNCTION rc_api_effective_capability_scopes(");
        normalizedDelta.Should().Contain("request_scope AS MATERIALIZED");
        normalizedDelta.Should().Contain("public.rc_api_scope_allows(target_portfolio_id)");
        normalizedDelta.Should().Contain(
            "capability.\"Key\" = ANY(capability_keys)");
        normalizedDelta.Should().Contain(
            "capability.\"AuthorizationTargetKind\"::text = authorization_target_kind");
        normalizedDelta.Should().Contain("WITH business_clock AS MATERIALIZED");
        normalizedDelta.Should().Contain(
            "WHEN 'Frozen' THEN clock.\"SimAnchorUtc\"");
        normalizedDelta.Should().Contain(
            "clock.\"SimAnchorUtc\" + (CURRENT_TIMESTAMP - clock.\"RealAnchorUtc\")");
        Regex.Matches(normalizedDelta, "WITH business_clock AS MATERIALIZED").Should().HaveCount(2);
        normalizedDelta.Should().Contain(
            "THEN EXISTS ( WITH business_clock AS MATERIALIZED");
        normalizedDelta.Should().Contain(
            "FROM business_clock CROSS JOIN public.\"AuthSessions\" session");
        normalizedDelta.Should().Contain(
            "CROSS JOIN business_clock JOIN public.\"WorkspaceAccessContexts\"");
        normalizedDelta.Should().Contain(
            "session.\"ExpiresAtUtc\" > CURRENT_TIMESTAMP");
        normalizedDelta.Should().Contain(
            "assignment.\"EffectiveFromUtc\" <= business_clock.effective_at_utc");
        normalizedDelta.Should().Contain(
            "membership.\"EffectiveFromUtc\" <= business_clock.effective_at_utc");
        normalizedDelta.Should().Contain("SELECT DISTINCT assignment.\"Id\" AS \"AssignmentId\"");
        normalizedDelta.Should().Contain(
            "REVOKE ALL ON FUNCTION rc_api_effective_capability_scopes(integer, uuid, integer, integer, bigint, text[], text) FROM PUBLIC;");
        normalizedDelta.Should().Contain(
            "REVOKE ALL ON FUNCTION rc_api_effective_capability_scopes(integer, uuid, integer, integer, bigint, text[], text) FROM rentalcommand_engine;");
        normalizedDelta.Should().Contain(
            "GRANT EXECUTE ON FUNCTION rc_api_effective_capability_scopes(integer, uuid, integer, integer, bigint, text[], text) TO rentalcommand_api;");
        normalizedDelta.Should().NotContain(
            "GRANT EXECUTE ON FUNCTION rc_api_effective_capability_scopes(integer, uuid, integer, integer, bigint, text[], text) TO rentalcommand_engine;");

        var normalizedBaseline = Regex.Replace(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260727,
            @"\s+",
            " ");
        normalizedBaseline.Should().Contain(
            Regex.Replace(
                FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260727[
                    FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260727.IndexOf(
                        "CREATE OR REPLACE FUNCTION rc_api_effective_capability_scopes",
                        StringComparison.Ordinal)..],
                @"\s+",
                " "),
            "the fresh baseline and forward migration must install the same function and ACL");
    }

    [Fact]
    public void EngineAtomicAuditPolicyHelpers_AreExecutableWithoutGrantingPreAuthAdmission()
    {
        var normalizedAuthoritySql = Regex.Replace(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSql, @"\s+", " ");
        foreach (var signature in new[]
                 {
                     "rc_pre_auth_email_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)",
                     "rc_pre_auth_account_security_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)",
                 })
        {
            normalizedAuthoritySql.Should().Contain(
                $"GRANT EXECUTE ON FUNCTION {signature} TO rentalcommand_api, rentalcommand_engine;");
        }

        foreach (var functionName in new[]
                 {
                     "rc_pre_auth_email_audit_allows",
                     "rc_pre_auth_account_security_audit_allows",
                 })
        {
            Regex.Match(
                    normalizedAuthoritySql,
                    $@"CREATE OR REPLACE FUNCTION {functionName}\(.*?ALTER FUNCTION {functionName}",
                    RegexOptions.Singleline)
                .Value.Should().Contain("SELECT session_user = 'rentalcommand_api'",
                    "Engine may evaluate this AtomicAuditLogs policy branch but must not receive pre-auth admission");
        }

        var migration = new GrantEngineAtomicAuditPolicyHelpers();
        var upBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(GrantEngineAtomicAuditPolicyHelpers).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [upBuilder]);
        var upSql = Regex.Replace(
            upBuilder.Operations.OfType<SqlOperation>().Should().ContainSingle().Which.Sql, @"\s+", " ");
        upSql.Should().Contain(
            "GRANT EXECUTE ON FUNCTION rc_pre_auth_email_audit_allows( integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb) TO rentalcommand_engine;");
        upSql.Should().Contain(
            "GRANT EXECUTE ON FUNCTION rc_pre_auth_account_security_audit_allows( integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb) TO rentalcommand_engine;");
        upSql.Should().NotContain("GRANT SELECT");
        upSql.Should().NotContain("ALTER FUNCTION");
        upSql.Should().NotContain("BYPASSRLS");

        var downBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(GrantEngineAtomicAuditPolicyHelpers).GetMethod(
                "Down", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [downBuilder]);
        var downSql = Regex.Replace(
            downBuilder.Operations.OfType<SqlOperation>().Should().ContainSingle().Which.Sql, @"\s+", " ");
        downSql.Should().Contain(
            "REVOKE EXECUTE ON FUNCTION rc_pre_auth_email_audit_allows( integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb) FROM rentalcommand_engine;");
        downSql.Should().Contain(
            "REVOKE EXECUTE ON FUNCTION rc_pre_auth_account_security_audit_allows( integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb) FROM rentalcommand_engine;");
    }

    [Fact]
    public void EngineStoredFileAppendMigration_GrantsOnlyInsertAndSequenceUsage()
    {
        var migration = new GrantEngineStoredFileAppend();
        var upBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(GrantEngineStoredFileAppend).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [upBuilder]);
        var upSql = Regex.Replace(
            upBuilder.Operations.OfType<SqlOperation>().Should().ContainSingle().Which.Sql, @"\s+", " ");
        upSql.Should().Contain("GRANT INSERT ON TABLE \"StoredFiles\" TO rentalcommand_engine;");
        upSql.Should().Contain("GRANT USAGE, SELECT ON SEQUENCE");
        upSql.Should().NotContain("GRANT UPDATE");
        upSql.Should().NotContain("GRANT DELETE");
        upSql.Should().NotContain("BYPASSRLS");

        var downBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(GrantEngineStoredFileAppend).GetMethod(
                "Down", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [downBuilder]);
        var downSql = Regex.Replace(
            downBuilder.Operations.OfType<SqlOperation>().Should().ContainSingle().Which.Sql, @"\s+", " ");
        downSql.Should().Contain("REVOKE INSERT ON TABLE \"StoredFiles\" FROM rentalcommand_engine;");
        downSql.Should().Contain("REVOKE USAGE, SELECT ON SEQUENCE");
    }

    [Fact]
    public void EngineLegalDocumentArtifactAppendMigration_GrantsOnlyInsertAndSequenceUsage()
    {
        var migration = new GrantEngineLegalDocumentArtifactAppend();
        var upBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(GrantEngineLegalDocumentArtifactAppend).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [upBuilder]);
        var upSql = Regex.Replace(
            upBuilder.Operations.OfType<SqlOperation>().Should().ContainSingle().Which.Sql, @"\s+", " ");
        upSql.Should().Contain("GRANT INSERT ON TABLE \"LegalDocumentArtifacts\" TO rentalcommand_engine;");
        upSql.Should().Contain("GRANT USAGE, SELECT ON SEQUENCE");
        upSql.Should().NotContain("GRANT UPDATE");
        upSql.Should().NotContain("GRANT DELETE");
        upSql.Should().NotContain("BYPASSRLS");

        var downBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(GrantEngineLegalDocumentArtifactAppend).GetMethod(
                "Down", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [downBuilder]);
        var downSql = Regex.Replace(
            downBuilder.Operations.OfType<SqlOperation>().Should().ContainSingle().Which.Sql, @"\s+", " ");
        downSql.Should().Contain("REVOKE INSERT ON TABLE \"LegalDocumentArtifacts\" FROM rentalcommand_engine;");
        downSql.Should().Contain("REVOKE USAGE, SELECT ON SEQUENCE");
    }

    [Fact]
    public void EngineLeaseAddendumExecutionMigration_GrantsUpdateWithoutCreateOrDelete()
    {
        var migration = new GrantEngineLeaseAddendumExecutionUpdate();
        var upBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(GrantEngineLeaseAddendumExecutionUpdate).GetMethod(
                "Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [upBuilder]);
        var upSql = Regex.Replace(
            upBuilder.Operations.OfType<SqlOperation>().Should().ContainSingle().Which.Sql, @"\s+", " ");
        upSql.Should().Contain("GRANT UPDATE ON TABLE \"LeaseAddenda\" TO rentalcommand_engine;");
        upSql.Should().NotContain("GRANT INSERT");
        upSql.Should().NotContain("GRANT DELETE");
        upSql.Should().NotContain("BYPASSRLS");

        var downBuilder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(GrantEngineLeaseAddendumExecutionUpdate).GetMethod(
                "Down", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [downBuilder]);
        var downSql = Regex.Replace(
            downBuilder.Operations.OfType<SqlOperation>().Should().ContainSingle().Which.Sql, @"\s+", " ");
        downSql.Should().Contain("REVOKE UPDATE ON TABLE \"LeaseAddenda\" FROM rentalcommand_engine;");
    }

    [Fact]
    public void PublicApplicationScope_IsOpaqueTokenBoundAndCannotMutateWorkspaceRows()
    {
        CreateSql.Should().Contain(
            "CREATE OR REPLACE FUNCTION rc_public_application_scope_allows(target_portfolio_id integer)");
        CreateSql.Should().Contain("portfolio.\"PublicApplicationToken\" =");
        CreateSql.Should().Contain("current_setting('app.public_application_token', true)");
        CreateSql.Should().Contain("portfolio.\"Status\" = 1");
        CreateSql.Should().MatchRegex(
            @"ALTER FUNCTION rc_public_application_scope_allows\(integer\)\s+OWNER TO rentalcommand_rls_authority;");
        CreateSql.Should().MatchRegex(
            @"GRANT EXECUTE ON FUNCTION rc_public_application_scope_allows\(integer\)\s+TO rentalcommand_api, rentalcommand_engine;");
        CreateSql.Should().Contain("WHEN session_user IS DISTINCT FROM 'rentalcommand_api'");

        foreach (var table in new[]
                 {
                     "Portfolios", "Properties", "Units", "LeaseManagements",
                     "LeaseAgreements", "UnitOperationalPeriods",
                 })
        {
            CreateSql.Should().Contain(
                $"CREATE POLICY public_application_select ON \"{table}\" FOR SELECT");
        }

        CreateSql.Should().Contain(
            "CREATE POLICY public_application_insert ON \"RentalApplications\" FOR INSERT");
        CreateSql.Should().Contain(
            "CREATE POLICY public_application_select ON \"RentalApplications\" FOR SELECT");
        CreateSql.Should().Contain(
            "CREATE POLICY public_application_insert ON \"AtomicAuditLogs\" FOR INSERT");
        CreateSql.Should().Contain(
            "CREATE POLICY public_application_select ON \"AtomicAuditLogs\" FOR SELECT");
        CreateSql.Should().Contain(
            "CREATE POLICY public_application_insert ON \"OutboxMessages\" FOR INSERT");
        CreateSql.Should().Contain(
            "CREATE POLICY public_application_select ON \"OutboxMessages\" FOR SELECT");
        CreateSql.Should().NotContain("public_application_update");
        CreateSql.Should().NotContain("public_application_delete");
        DropSql.Should().Contain(
            "DROP FUNCTION IF EXISTS rc_public_application_scope_allows(integer);");
    }

    [Fact]
    public void PublicSigningScope_IsTokenHashBoundAndLimitedToSigningResources()
    {
        CreateSql.Should().Contain(
            "CREATE OR REPLACE FUNCTION rc_public_signing_scope_allows(target_portfolio_id integer)");
        CreateSql.Should().Contain("current_setting('app.public_signing_token_hash', true)");
        CreateSql.Should().Contain("signer.\"TokenHash\" = current_setting('app.public_signing_token_hash', true)");

        foreach (var table in new[]
                 {
                     "Portfolios", "SignatureSigners", "SignatureRequests", "SignatureAuditEvents",
                     "LegalDocumentArtifacts", "StoredFiles", "PendingFileUploads", "AtomicAuditLogs",
                 })
        {
            CreateSql.Should().Contain($"public_signing_select ON \"{table}\"");
        }

        CreateSql.Should().Contain("public_signing_update ON \"SignatureSigners\"");
        CreateSql.Should().Contain("public_signing_update ON \"SignatureRequests\"");
        CreateSql.Should().Contain("public_signing_insert ON \"SignatureAuditEvents\"");
        CreateSql.Should().Contain("public_signing_insert ON \"StoredFiles\"");
        CreateSql.Should().Contain("public_signing_insert ON \"PendingFileUploads\"");
        CreateSql.Should().Contain("public_signing_insert ON \"AtomicAuditLogs\"");
        CreateSql.Should().NotContain("public_signing_delete");
        DropSql.Should().Contain(
            "DROP FUNCTION IF EXISTS rc_public_signing_scope_allows(integer);");
    }

    [Fact]
    public void EffectiveAccessContextProjection_ComputesTheTotalBeforeOuterSelection()
    {
        var normalizedSql = Regex.Replace(CreateSql, @"\s+", " ");

        normalizedSql.Should().Contain("count(*) OVER ()::integer AS \"TotalEffectiveContexts\"");
        normalizedSql.Should().Contain(
            "FROM options ORDER BY options.\"WorkspaceName\", options.\"AccessContextId\"");
    }

    [Fact]
    public void AccessEnvelopeSessionRead_RequiresExactLiveCoordinatesAndRuntimeIdentity()
    {
        var normalizedSql = Regex.Replace(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSql, @"\s+", " ");

        normalizedSql.Should().Contain(
            "CREATE OR REPLACE FUNCTION rc_get_access_envelope_for_session( target_auth_session_id uuid, target_user_id integer, target_access_context_id integer, target_access_revision bigint, effective_at_utc timestamp with time zone)");
        normalizedSql.Should().Contain("FROM public.\"AuthSessions\" auth_session");
        normalizedSql.Should().Contain("JOIN public.\"WorkspaceAccessContexts\" access_context");
        normalizedSql.Should().Contain("JOIN public.\"vw_access_envelopes\" envelope");
        normalizedSql.Should().Contain("session_user = 'rentalcommand_api'");
        normalizedSql.Should().Contain("auth_session.\"Id\" = target_auth_session_id");
        normalizedSql.Should().Contain("auth_session.\"UserId\" = target_user_id");
        normalizedSql.Should().Contain(
            "auth_session.\"ActiveAccessContextId\" = target_access_context_id");
        normalizedSql.Should().Contain("auth_session.\"Status\" = 'Active'");
        normalizedSql.Should().Contain("auth_session.\"RevokedAtUtc\" IS NULL");
        normalizedSql.Should().Contain("auth_session.\"ExpiresAtUtc\" > effective_at_utc");
        normalizedSql.Should().Contain(
            "access_context.\"AccessRevision\" = target_access_revision");
        normalizedSql.Should().Contain(
            "public.rc_access_context_is_effective( target_access_context_id, target_user_id, effective_at_utc)");
        normalizedSql.Should().Contain(
            "ALTER FUNCTION rc_get_access_envelope_for_session(uuid, integer, integer, bigint, timestamp with time zone) OWNER TO rentalcommand_rls_authority;");
        normalizedSql.Should().Contain(
            "REVOKE ALL ON FUNCTION rc_get_access_envelope_for_session(uuid, integer, integer, bigint, timestamp with time zone) FROM PUBLIC;");
        normalizedSql.Should().Contain(
            "GRANT EXECUTE ON FUNCTION rc_get_access_envelope_for_session(uuid, integer, integer, bigint, timestamp with time zone) TO rentalcommand_api;");
        DropSql.Should().Contain(
            "DROP FUNCTION IF EXISTS rc_get_access_envelope_for_session(\n  uuid, integer, integer, bigint, timestamp with time zone);");
    }

    private static string ExtractSqlSlice(string sql, string startMarker, string endMarker)
    {
        var start = sql.IndexOf(startMarker, StringComparison.Ordinal);
        var end = sql.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException($"Could not extract SQL slice starting with {startMarker}.");
        }

        return sql[start..end];
    }
}
