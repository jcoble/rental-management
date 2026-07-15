using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

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
    public void SandboxGraduation_AddsDeleteWithoutWeakeningAppendOnlyRows()
    {
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, DELETE ON TABLE \"AtomicAuditLogs\" TO rentalcommand_api;");
        CreateSql.Should().Contain(
            "GRANT SELECT, INSERT, DELETE ON TABLE \"TenantLedgerEntries\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"AtomicAuditLogs\" TO rentalcommand_api;");
        CreateSql.Should().NotContain(
            "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE \"TenantLedgerEntries\" TO rentalcommand_api;");
    }

    [Fact]
    public void DurableDeletePolicy_RequiresDatabaseValidatedSandboxAuthority()
    {
        CreateSql.Should().Contain(
            "CREATE POLICY tenant_delete ON \"TenantLedgerEntries\" FOR DELETE USING " +
            "(rc_sandbox_graduation_allows(\"PortfolioId\"));");
        CreateSql.Should().Contain(
            "CREATE POLICY tenant_select ON \"TenantLedgerEntries\" FOR SELECT USING " +
            "(rc_api_resource_scope_allows(\"PortfolioId\", NULL, NULL, NULL, NULL, \"TenantAccountId\", NULL, TRUE, TRUE, FALSE));");
        CreateSql.Should().Contain(
            "CREATE POLICY tenant_insert ON \"TenantLedgerEntries\" FOR INSERT WITH CHECK " +
            "(rc_api_resource_scope_allows(\"PortfolioId\", NULL, NULL, NULL, NULL, \"TenantAccountId\", NULL, FALSE, TRUE, FALSE));");
        CreateSql.Should().Contain(
            "CREATE POLICY tenant_update ON \"TenantLedgerEntries\" FOR UPDATE USING " +
            "(rc_api_resource_scope_allows(\"PortfolioId\", NULL, NULL, NULL, NULL, \"TenantAccountId\", NULL, FALSE, TRUE, FALSE)) WITH CHECK " +
            "(rc_api_resource_scope_allows(\"PortfolioId\", NULL, NULL, NULL, NULL, \"TenantAccountId\", NULL, FALSE, TRUE, FALSE));");
        CreateSql.Should().NotContain("app.rls_bypass_reason");
        CreateSql.Should().NotContain("app.is_admin");
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
            "LeaseManagements", "LoginContextSelectionChallenges", "MembershipRoleAssignmentProperties", "MembershipRoleAssignments",
            "OwnerEntities", "OwnerUserAccesses", "Portfolios", "Properties", "RoleProfileCapabilities",
            "RoleProfiles", "SimulationClocks", "SystemNoticeTemplateVersions", "TenantAccounts",
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
}
