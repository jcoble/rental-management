using RentalCommand.Data.Authorization;

namespace RentalCommand.Data;

/// <summary>
/// PostgreSQL objects installed after EF creates the clean foundation schema. The table sets below
/// are an explicit audit of the current mapped model; adding a mapped table requires deliberately
/// classifying it as portfolio-scoped, a transitive child, or a true global/auth/system table.
/// </summary>
internal static class FoundationBaselinePostgreSql
{
    private const string ApiRole = "rentalcommand_api";
    private const string EngineRole = "rentalcommand_engine";

    internal static IReadOnlyList<string> CreateStatements { get; } =
    [
        CreateAuditSearchInfrastructure,
        LeaseEffectiveClockSql.CreateEffectiveNowUtc,
        LeaseEffectiveClockSql.CreateBusinessDate,
        LeaseAgreementStatusViewSql.Create,
        LeaseAddendumStatusViewSql.Create,
        TenantChargeBalanceViewSql.Create,
        TenantAccountBalanceViewSql.Create,
        SecurityDepositBalanceViewSql.Create,
        UnitOccupancyViewSql.Create,
        LeaseManagementLifecycleViewSql.Create,
        LeaseReconciliationExceptionViewSql.Create,
        RelationshipAccessProjectionSql.Create,
        AccessEnvelopeViewSql.Create,
        AccountingParkedTransactionViewSql.Create,
        BuildRolesAndGrantSql(),
        BuildCreateRlsSql(),
    ];

    internal static IReadOnlyList<string> DropStatements { get; } =
    [
        BuildDropRlsSql(),
        BuildRevokeRoleGrantsSql(),
        AccountingParkedTransactionViewSql.Drop,
        AccessEnvelopeViewSql.Drop,
        RelationshipAccessProjectionSql.Drop,
        LeaseReconciliationExceptionViewSql.Drop,
        LeaseManagementLifecycleViewSql.Drop,
        UnitOccupancyViewSql.Drop,
        SecurityDepositBalanceViewSql.Drop,
        TenantAccountBalanceViewSql.Drop,
        TenantChargeBalanceViewSql.Drop,
        LeaseAddendumStatusViewSql.Drop,
        LeaseAgreementStatusViewSql.Drop,
        LeaseEffectiveClockSql.DropBusinessDate,
        LeaseEffectiveClockSql.DropEffectiveNowUtc,
        DropAuditSearchIndexes,
    ];

    /// <summary>Mapped tables with a required PortfolioId and a direct tenant-isolation policy.</summary>
    internal static IReadOnlyList<string> DirectPortfolioTables { get; } =
    [
        "AccountingConnections",
        "AccountingEntityMappings",
        "AccountingMappingPromotionJobs",
        "AccountingSyncMaps",
        "AdverseActionNotices",
        "ApplicantScreeningMilestones",
        "ApplicantScreenings",
        "ApplicationFinancialAccounts",
        "ApplicationFinancialEntries",
        "Appointments",
        "AtomicAuditLogs",
        "AuditLogs",
        "BankConnections",
        "BankTransactions",
        "CapitalAssets",
        "Conversations",
        "DeviceTokens",
        "DocumentTemplates",
        "EvictionCaseEvents",
        "EvictionCaseRespondents",
        "EvictionCases",
        "ExternalListingSignals",
        "Expenses",
        "Inspections",
        "InspectionItems",
        "LeaseAddenda",
        "LeaseAddendumFinancialEffects",
        "LeaseAddendumSigners",
        "LeaseAgreements",
        "LeaseAgreementSigners",
        "LeaseManagementParties",
        "LeaseManagements",
        "LeaseRenewalAddendumDecisions",
        "LegalDocumentArtifacts",
        "ListingPhotos",
        "ListingPublications",
        "Loans",
        "LoanPayments",
        "MembershipRoleAssignmentProperties",
        "MembershipRoleAssignments",
        "NoticeDeliveryEvidence",
        "NoticeDrafts",
        "NotificationPreferences",
        "Notifications",
        "NotificationSettings",
        "OAuthStates",
        "OwnerDistributions",
        "OwnerEntities",
        "OwnerUserAccesses",
        "Owners",
        "PendingFileUploads",
        "PlaidTokenExchangeAttempts",
        "PortalMessages",
        "Properties",
        "PropertyDispositions",
        "QueuedJobs",
        "RecurringExpenses",
        "RecurringMaintenanceTasks",
        "RenderedNotices",
        "RentalApplications",
        "RentalListings",
        "ScanBatches",
        "ScanDrafts",
        "SecurityDepositAccounts",
        "SecurityDepositEntries",
        "SignatureAuditEvents",
        "SignatureRequests",
        "SignatureSigners",
        "StoredFiles",
        "TeamRoutingRuleRecipients",
        "TeamRoutingRules",
        "TenantAccountConditionPeriods",
        "TenantAccounts",
        "TenantAutopayEnrollments",
        "TenantLedgerAllocations",
        "TenantLedgerEntries",
        "TenantNoticePolicies",
        "TenantNoticeWorkItems",
        "TenantPaymentAttempts",
        "Tenants",
        "TenantUserAccesses",
        "UnitOperationalPeriods",
        "Units",
        "UserAccounts",
        "UserAlertPreferences",
        "VendorDispatches",
        "VendorRatings",
        "Vendors",
        "WorkspaceAccessContexts",
        "WorkspaceMemberships",
        "WorkspaceNoticeTemplateVersions",
        "WorkOrders",
        "WorkOrderStatusEvents",
    ];

    /// <summary>
    /// The only nullable portfolio table. Null rows are shared inspection templates; non-null rows
    /// belong to exactly one workspace.
    /// </summary>
    internal static IReadOnlyList<string> NullablePortfolioTables { get; } =
    [
        "InspectionTemplates",
    ];

    /// <summary>Mapped children whose portfolio scope is enforced through their protected parent.</summary>
    internal static IReadOnlyList<ChildPolicy> ChildPortfolioTables { get; } =
    [
        new("ConversationMessages", "Conversations", "ConversationId"),
        new("DocumentTemplateFields", "DocumentTemplates", "DocumentTemplateId"),
        new("ExpenseLineItems", "Expenses", "ExpenseId"),
        new("InspectionTemplateItems", "InspectionTemplates", "TemplateId"),
    ];

    /// <summary>
    /// Current mapped base tables intentionally excluded from RLS. They are still explicitly granted
    /// to the API role because authentication, authorization catalogs, and durable system workers use
    /// the same runtime connection; exclusion here means global/system scope, not unrestricted data.
    /// </summary>
    internal static IReadOnlyList<string> GlobalAuthAndSystemTables { get; } =
    [
        "AspNetRoleClaims",
        "AspNetRoles",
        "AspNetUserClaims",
        "AspNetUserLogins",
        "AspNetUserRoles",
        "AspNetUsers",
        "AspNetUserTokens",
        "AtomicCommandReceipts",
        "AuthSessionRefreshCredentials",
        "AuthSessionRefreshTokenFamilies",
        "AuthSessions",
        "CapabilityDefinitions",
        "EngineWorkerHeartbeats",
        "LoginContextSelectionChallenges",
        "OutboxMessages",
        "ProviderInboxEvents",
        "RefreshTokens",
        "RoleProfileCapabilities",
        "RoleProfiles",
        "SimWorkerCommands",
        "SimulationClocks",
        "SystemNoticeTemplateVersions",
    ];

    /// <summary>
    /// Objects from the retired model that must never be pulled into the clean baseline allowlists.
    /// These names are negative assertions only; the clean baseline creates none of them.
    /// </summary>
    internal static IReadOnlyList<string> DeletedLegacyObjects { get; } =
    [
        "AutopayEnrollments",
        "LeaseTenants",
        "Leases",
        "NoticeTemplates",
        "OpeningBalances",
        "Payments",
        "PaymentTransactions",
        "SecurityDepositHoldings",
        "vw_accounting_transactions",
    ];

    internal static IReadOnlyList<string> SecurityInvokerViews { get; } =
    [
        "vw_lease_agreement_status",
        "vw_lease_addendum_status",
        "vw_tenant_charge_balances",
        "vw_tenant_account_balances",
        "vw_security_deposit_balances",
        "vw_unit_occupancy",
        "vw_lease_management_lifecycle",
        "vw_lease_reconciliation_exceptions",
        "vw_effective_owner_access",
        "vw_effective_tenant_access",
        "vw_access_envelopes",
        "vw_accounting_parked_transactions",
    ];

    private static IReadOnlyList<string> MappedTables { get; } =
        DirectPortfolioTables
            .Append("Portfolios")
            .Concat(NullablePortfolioTables)
            .Concat(ChildPortfolioTables.Select(policy => policy.Table))
            .Concat(GlobalAuthAndSystemTables)
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToArray();

    // Evidence and financial-history rows may be appended but never rewritten by either runtime.
    private static readonly HashSet<string> AppendOnlyTables = new(StringComparer.Ordinal)
    {
        "AtomicAuditLogs", "AuditLogs", "NoticeDeliveryEvidence", "RenderedNotices",
        "SecurityDepositEntries", "SignatureAuditEvents", "TenantLedgerAllocations",
        "TenantLedgerEntries", "WorkspaceNoticeTemplateVersions",
    };

    // Catalogs and supplied system templates are data, not runtime configuration mutation surfaces.
    private static readonly HashSet<string> ApiReadOnlyTables = new(StringComparer.Ordinal)
    {
        "AspNetRoleClaims", "AspNetRoles", "CapabilityDefinitions", "EngineWorkerHeartbeats",
        "RoleProfileCapabilities", "RoleProfiles", "SystemNoticeTemplateVersions",
    };

    // DELETE is deliberately exceptional. Durable leases, accounts, legal artifacts, ledgers, and
    // history are absent even though the development sandbox currently hard-deletes some of them.
    private static readonly HashSet<string> ApiDeleteTables = new(StringComparer.Ordinal)
    {
        "AccountingConnections", "Appointments", "AspNetUserClaims", "AspNetUserLogins",
        "AspNetUserRoles", "AspNetUsers", "AspNetUserTokens", "DeviceTokens",
        "DocumentTemplateFields", "ExpenseLineItems", "InspectionItems", "Inspections",
        "InspectionTemplateItems", "InspectionTemplates", "LeaseAddendumFinancialEffects",
        "LeaseAddendumSigners", "LeaseAgreementSigners", "MembershipRoleAssignmentProperties",
        "OAuthStates", "TeamRoutingRuleRecipients",
    };

    // Exact set-based delete inventory in SandboxService.WipePortfolioDataAsync. Tables absent from
    // ApiDeleteTables are durable canonical/history surfaces whose DELETE policy accepts only the
    // dedicated SandboxGraduation bypass reason.
    internal static readonly IReadOnlySet<string> SandboxGraduationDeleteTables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "AdverseActionNotices", "ApplicantScreeningMilestones", "ApplicantScreenings",
            "Appointments", "AuditLogs", "BankConnections", "BankTransactions",
            "ConversationMessages", "Conversations", "Expenses", "InspectionItems", "Inspections",
            "LeaseAddenda", "LeaseAddendumFinancialEffects", "LeaseAddendumSigners",
            "LeaseAgreements", "LeaseAgreementSigners", "LeaseManagementParties",
            "LeaseManagements", "LeaseRenewalAddendumDecisions", "NoticeDrafts", "OwnerEntities",
            "Owners", "Properties", "RecurringMaintenanceTasks", "RentalApplications", "ScanBatches",
            "ScanDrafts", "SecurityDepositAccounts", "SecurityDepositEntries", "StoredFiles",
            "TenantAccountConditionPeriods", "TenantAccounts", "TenantAutopayEnrollments",
            "TenantLedgerAllocations", "TenantLedgerEntries", "TenantPaymentAttempts",
            "TenantUserAccesses", "Tenants", "UnitOperationalPeriods", "Units", "VendorDispatches",
            "VendorRatings", "Vendors", "WorkOrders", "WorkOrderStatusEvents",
        };

    private static readonly HashSet<string> ApiMutableTables = new(StringComparer.Ordinal)
    {
        "AccountingEntityMappings", "AccountingMappingPromotionJobs", "AccountingSyncMaps",
        "AdverseActionNotices", "ApplicantScreeningMilestones", "ApplicantScreenings",
        "ApplicationFinancialAccounts", "ApplicationFinancialEntries", "AtomicCommandReceipts",
        "AuthSessionRefreshCredentials", "AuthSessionRefreshTokenFamilies", "AuthSessions",
        "BankConnections", "BankTransactions", "CapitalAssets", "ConversationMessages",
        "Conversations", "DocumentTemplates", "EvictionCaseEvents", "EvictionCaseRespondents",
        "EvictionCases", "Expenses", "ExternalListingSignals", "LeaseAddenda", "LeaseAgreements",
        "LeaseManagementParties", "LeaseManagements", "LeaseRenewalAddendumDecisions",
        "LegalDocumentArtifacts", "ListingPhotos", "ListingPublications", "LoanPayments", "Loans",
        "LoginContextSelectionChallenges", "MembershipRoleAssignments", "NoticeDrafts",
        "NotificationPreferences", "NotificationSettings", "Notifications", "OutboxMessages",
        "OwnerDistributions", "OwnerEntities", "OwnerUserAccesses", "Owners", "PendingFileUploads",
        "PlaidTokenExchangeAttempts", "PortalMessages", "Portfolios", "Properties",
        "PropertyDispositions", "ProviderInboxEvents", "QueuedJobs", "RecurringExpenses",
        "RecurringMaintenanceTasks", "RefreshTokens", "RentalApplications", "RentalListings",
        "ScanBatches", "ScanDrafts", "SecurityDepositAccounts", "SignatureRequests",
        "SignatureSigners", "SimWorkerCommands", "SimulationClocks", "StoredFiles",
        "TeamRoutingRules", "TenantAccountConditionPeriods", "TenantAccounts",
        "TenantAutopayEnrollments", "TenantNoticePolicies", "TenantNoticeWorkItems",
        "TenantPaymentAttempts", "TenantUserAccesses", "Tenants", "UnitOperationalPeriods",
        "Units", "UserAccounts", "UserAlertPreferences", "VendorDispatches", "VendorRatings",
        "Vendors", "WorkOrderStatusEvents", "WorkOrders", "WorkspaceAccessContexts",
        "WorkspaceMemberships",
    };

    // Engine reads are broad across portfolio data because projection views use security_invoker,
    // but authentication credentials and refresh/session material are intentionally API-only.
    private static readonly HashSet<string> EngineDeniedTables = new(StringComparer.Ordinal)
    {
        "AspNetRoleClaims", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens",
        "AuthSessionRefreshCredentials", "AuthSessionRefreshTokenFamilies", "AuthSessions",
        "LoginContextSelectionChallenges", "OAuthStates", "PlaidTokenExchangeAttempts",
        "RefreshTokens",
    };

    private static readonly HashSet<string> EngineAppendOnlyTables = new(StringComparer.Ordinal)
    {
        "AtomicAuditLogs", "AuditLogs", "NoticeDeliveryEvidence", "Notifications",
        "RenderedNotices", "SecurityDepositEntries", "SignatureAuditEvents",
        "TenantLedgerAllocations", "TenantLedgerEntries", "WorkspaceNoticeTemplateVersions",
        "WorkOrderStatusEvents",
    };

    // Worker claim state is mutable; this list is intentionally separate from the API matrix.
    private static readonly HashSet<string> EngineMutableTables = new(StringComparer.Ordinal)
    {
        "AccountingConnections", "AccountingEntityMappings", "AccountingMappingPromotionJobs",
        "AccountingSyncMaps", "AtomicCommandReceipts", "BankConnections", "BankTransactions",
        "Conversations", "ConversationMessages", "EngineWorkerHeartbeats", "Expenses",
        "ExpenseLineItems", "LeaseAgreements", "NoticeDrafts", "PendingFileUploads",
        "OutboxMessages", "ProviderInboxEvents", "RecurringExpenses", "RecurringMaintenanceTasks", "ScanBatches",
        "ScanDrafts", "SecurityDepositAccounts", "SignatureRequests", "SignatureSigners",
        "SimWorkerCommands", "TenantAccounts", "TenantNoticeWorkItems", "TenantPaymentAttempts",
        "WorkOrders",
    };

    private static readonly HashSet<string> EngineSystemTemplateTables = new(StringComparer.Ordinal)
    {
        "InspectionTemplates", "InspectionTemplateItems",
    };

    private static readonly HashSet<string> EngineReadOnlyTables = new(StringComparer.Ordinal)
    {
        "AdverseActionNotices", "ApplicantScreeningMilestones", "ApplicantScreenings",
        "ApplicationFinancialAccounts", "ApplicationFinancialEntries", "Appointments",
        "AspNetRoles", "AspNetUserRoles", "AspNetUsers", "CapabilityDefinitions", "CapitalAssets",
        "DeviceTokens", "DocumentTemplateFields", "DocumentTemplates", "EvictionCaseEvents",
        "EvictionCaseRespondents", "EvictionCases", "ExternalListingSignals", "InspectionItems",
        "Inspections", "LeaseAddenda", "LeaseAddendumFinancialEffects", "LeaseAddendumSigners",
        "LeaseAgreementSigners", "LeaseManagementParties", "LeaseManagements",
        "LeaseRenewalAddendumDecisions", "LegalDocumentArtifacts", "ListingPhotos",
        "ListingPublications", "LoanPayments", "Loans", "MembershipRoleAssignmentProperties",
        "MembershipRoleAssignments", "NotificationPreferences", "NotificationSettings",
        "OwnerDistributions", "OwnerEntities", "OwnerUserAccesses", "Owners", "PortalMessages",
        "Portfolios", "Properties", "PropertyDispositions", "QueuedJobs", "RentalApplications",
        "RentalListings", "RoleProfileCapabilities", "RoleProfiles", "SimulationClocks",
        "StoredFiles", "SystemNoticeTemplateVersions", "TeamRoutingRuleRecipients",
        "TeamRoutingRules", "TenantAccountConditionPeriods", "TenantAutopayEnrollments",
        "TenantNoticePolicies", "TenantUserAccesses", "Tenants", "UnitOperationalPeriods", "Units",
        "UserAccounts", "UserAlertPreferences", "VendorDispatches", "VendorRatings", "Vendors",
        "WorkspaceAccessContexts", "WorkspaceMemberships",
    };

    private static readonly HashSet<string> EngineViews = new(StringComparer.Ordinal)
    {
        "vw_lease_agreement_status", "vw_lease_addendum_status", "vw_tenant_charge_balances",
        "vw_tenant_account_balances", "vw_security_deposit_balances", "vw_unit_occupancy",
        "vw_lease_management_lifecycle", "vw_lease_reconciliation_exceptions",
        "vw_effective_tenant_access", "vw_accounting_parked_transactions",
    };

    private const string CreateAuditSearchInfrastructure = """
        CREATE EXTENSION IF NOT EXISTS pg_trgm;
        CREATE INDEX IF NOT EXISTS "IX_AuditLogs_EntityType_trgm"
          ON "AuditLogs" USING gin (lower("EntityType") gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS "IX_AuditLogs_ActorLabel_trgm"
          ON "AuditLogs" USING gin (lower("ActorLabel") gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS "IX_AuditLogs_IpAddress_trgm"
          ON "AuditLogs" USING gin (lower("IpAddress") gin_trgm_ops);
        """;

    private const string DropAuditSearchIndexes = """
        DROP INDEX IF EXISTS "IX_AuditLogs_IpAddress_trgm";
        DROP INDEX IF EXISTS "IX_AuditLogs_ActorLabel_trgm";
        DROP INDEX IF EXISTS "IX_AuditLogs_EntityType_trgm";
        """;

    private static string BuildRolesAndGrantSql()
    {
        var statements = new List<string>
        {
            """
            DO $role$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rentalcommand_api') THEN
                CREATE ROLE rentalcommand_api NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
              ELSE
                ALTER ROLE rentalcommand_api NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
              END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rentalcommand_engine') THEN
                CREATE ROLE rentalcommand_engine NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
              ELSE
                ALTER ROLE rentalcommand_engine NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
              END IF;
            END
            $role$;
            """,
            "DO $grant$ BEGIN EXECUTE format('GRANT CONNECT ON DATABASE %I TO rentalcommand_api', current_database()); END $grant$;",
            "DO $grant$ BEGIN EXECUTE format('GRANT CONNECT ON DATABASE %I TO rentalcommand_engine', current_database()); END $grant$;",
            "DO $grant$ BEGIN EXECUTE format('GRANT rentalcommand_api, rentalcommand_engine TO %I', current_user); END $grant$;",
            "GRANT USAGE ON SCHEMA public TO rentalcommand_api;",
            "GRANT USAGE ON SCHEMA public TO rentalcommand_engine;",
        };

        statements.AddRange(MappedTables.Select(table =>
            GrantTableSql(table, ApiRole, ApiOperations(table))).OfType<string>());
        statements.AddRange(MappedTables.Select(table =>
            GrantTableSql(table, EngineRole, EngineOperations(table))).OfType<string>());
        statements.AddRange(SecurityInvokerViews.Select(view =>
            $"GRANT SELECT ON TABLE {Quote(view)} TO {ApiRole};"));
        statements.AddRange(EngineViews.Select(view =>
            $"GRANT SELECT ON TABLE {Quote(view)} TO {EngineRole};"));
        statements.Add(BuildSequenceGrantSql(ApiRole, MappedTables.Where(table => ApiOperations(table).HasFlag(TableOperation.Insert)), revoke: false));
        statements.Add(BuildSequenceGrantSql(EngineRole, MappedTables.Where(table => EngineOperations(table).HasFlag(TableOperation.Insert)), revoke: false));

        return string.Join(Environment.NewLine, statements);
    }

    private static string BuildRevokeRoleGrantsSql()
    {
        var statements = new List<string>();
        statements.AddRange(SecurityInvokerViews
            .Select(view => $"REVOKE SELECT ON TABLE {Quote(view)} FROM {ApiRole};")
            .Concat(EngineViews.Select(view => $"REVOKE SELECT ON TABLE {Quote(view)} FROM {EngineRole};")));
        statements.AddRange(MappedTables.Select(table =>
            RevokeTableSql(table, ApiRole, ApiOperations(table))).OfType<string>());
        statements.AddRange(MappedTables.Select(table =>
            RevokeTableSql(table, EngineRole, EngineOperations(table))).OfType<string>());
        statements.Add(BuildSequenceGrantSql(ApiRole, MappedTables.Where(table => ApiOperations(table).HasFlag(TableOperation.Insert)), revoke: true));
        statements.Add(BuildSequenceGrantSql(EngineRole, MappedTables.Where(table => EngineOperations(table).HasFlag(TableOperation.Insert)), revoke: true));
        statements.Add("REVOKE USAGE ON SCHEMA public FROM rentalcommand_api;");
        statements.Add("REVOKE USAGE ON SCHEMA public FROM rentalcommand_engine;");
        statements.Add("DO $revoke$ BEGIN EXECUTE format('REVOKE rentalcommand_api, rentalcommand_engine FROM %I', current_user); END $revoke$;");
        statements.Add("DO $revoke$ BEGIN EXECUTE format('REVOKE CONNECT ON DATABASE %I FROM rentalcommand_api', current_database()); END $revoke$;");
        statements.Add("DO $revoke$ BEGIN EXECUTE format('REVOKE CONNECT ON DATABASE %I FROM rentalcommand_engine', current_database()); END $revoke$;");

        // Roles are shared provisioning objects. Down removes this baseline's grants but never drops them.
        return string.Join(Environment.NewLine, statements);
    }

    private static string BuildCreateRlsSql()
    {
        var statements = new List<string>();
        statements.AddRange(DirectPortfolioTables.Select(table =>
            RequiresSandboxGraduationDelete(table)
                ? CreateSandboxGraduationDeletePolicySql(table, PortfolioPredicate)
                : CreatePolicySql(table, PortfolioPredicate)));
        statements.Add(CreatePolicySql("Portfolios", PortfolioSelfPredicate));
        statements.Add(BuildInspectionTemplatePoliciesSql());
        statements.AddRange(ChildPortfolioTables.Where(policy => policy.Table != "InspectionTemplateItems")
            .Select(policy => RequiresSandboxGraduationDelete(policy.Table)
                ? CreateSandboxGraduationDeletePolicySql(policy.Table, ChildPredicate(policy))
                : CreatePolicySql(policy.Table, ChildPredicate(policy))));
        statements.Add(BuildInspectionTemplateItemPoliciesSql());
        return string.Join(Environment.NewLine, statements);
    }

    private static string BuildDropRlsSql()
    {
        var tables = ChildPortfolioTables.Select(policy => policy.Table)
            .Concat(NullablePortfolioTables)
            .Append("Portfolios")
            .Concat(DirectPortfolioTables.Reverse());

        return string.Join(Environment.NewLine, tables.Select(table => $"""
            DROP POLICY IF EXISTS tenant_select ON {Quote(table)};
            DROP POLICY IF EXISTS tenant_insert ON {Quote(table)};
            DROP POLICY IF EXISTS tenant_update ON {Quote(table)};
            DROP POLICY IF EXISTS tenant_delete ON {Quote(table)};
            DROP POLICY IF EXISTS tenant_isolation ON {Quote(table)};
            ALTER TABLE {Quote(table)} NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE {Quote(table)} DISABLE ROW LEVEL SECURITY;
            """));
    }

    private static string CreatePolicySql(string table, string predicate) => $"""
        ALTER TABLE {Quote(table)} ENABLE ROW LEVEL SECURITY;
        ALTER TABLE {Quote(table)} FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON {Quote(table)};
        CREATE POLICY tenant_isolation ON {Quote(table)}
          USING {predicate}
          WITH CHECK {predicate};
        """;

    private static string CreateSandboxGraduationDeletePolicySql(string table, string predicate) => $"""
        ALTER TABLE {Quote(table)} ENABLE ROW LEVEL SECURITY;
        ALTER TABLE {Quote(table)} FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_select ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_insert ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_update ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_delete ON {Quote(table)};
        CREATE POLICY tenant_select ON {Quote(table)} FOR SELECT USING {predicate};
        CREATE POLICY tenant_insert ON {Quote(table)} FOR INSERT WITH CHECK {predicate};
        CREATE POLICY tenant_update ON {Quote(table)} FOR UPDATE USING {predicate} WITH CHECK {predicate};
        CREATE POLICY tenant_delete ON {Quote(table)} FOR DELETE USING
          (current_setting('app.rls_bypass_reason', true) = 'SandboxGraduation');
        """;

    private static bool RequiresSandboxGraduationDelete(string table) =>
        SandboxGraduationDeleteTables.Contains(table) && !ApiDeleteTables.Contains(table);

    private const string PortfolioPredicate =
        "(\"PortfolioId\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
        "OR current_setting('app.is_admin', true) = 'true')";

    private const string NullablePortfolioReadPredicate =
        "(\"PortfolioId\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
        "OR \"PortfolioId\" IS NULL " +
        "OR current_setting('app.is_admin', true) = 'true')";

    private const string NullablePortfolioWritePredicate =
        "(\"PortfolioId\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
        "OR pg_has_role(current_user, 'rentalcommand_engine', 'USAGE'))";

    private const string PortfolioSelfPredicate =
        "(\"Id\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
        "OR current_setting('app.is_admin', true) = 'true')";

    private static string ChildPredicate(ChildPolicy policy) =>
        "(current_setting('app.is_admin', true) = 'true' OR EXISTS (SELECT 1 FROM " +
        $"{Quote(policy.ParentTable)} AS parent WHERE parent.\"Id\" = " +
        $"{Quote(policy.Table)}.{Quote(policy.ForeignKey)}))";

    private static string BuildInspectionTemplatePoliciesSql() => $"""
        ALTER TABLE "InspectionTemplates" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "InspectionTemplates" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "InspectionTemplates";
        DROP POLICY IF EXISTS tenant_select ON "InspectionTemplates";
        DROP POLICY IF EXISTS tenant_insert ON "InspectionTemplates";
        DROP POLICY IF EXISTS tenant_update ON "InspectionTemplates";
        DROP POLICY IF EXISTS tenant_delete ON "InspectionTemplates";
        CREATE POLICY tenant_select ON "InspectionTemplates" FOR SELECT USING {NullablePortfolioReadPredicate};
        CREATE POLICY tenant_insert ON "InspectionTemplates" FOR INSERT WITH CHECK {NullablePortfolioWritePredicate};
        CREATE POLICY tenant_update ON "InspectionTemplates" FOR UPDATE USING {NullablePortfolioWritePredicate} WITH CHECK {NullablePortfolioWritePredicate};
        CREATE POLICY tenant_delete ON "InspectionTemplates" FOR DELETE USING {NullablePortfolioWritePredicate};
        """;

    private static string BuildInspectionTemplateItemPoliciesSql() => """
        ALTER TABLE "InspectionTemplateItems" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "InspectionTemplateItems" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "InspectionTemplateItems";
        DROP POLICY IF EXISTS tenant_select ON "InspectionTemplateItems";
        DROP POLICY IF EXISTS tenant_insert ON "InspectionTemplateItems";
        DROP POLICY IF EXISTS tenant_update ON "InspectionTemplateItems";
        DROP POLICY IF EXISTS tenant_delete ON "InspectionTemplateItems";
        CREATE POLICY tenant_select ON "InspectionTemplateItems" FOR SELECT USING
          (pg_has_role(current_user, 'rentalcommand_engine', 'USAGE') OR EXISTS
            (SELECT 1 FROM "InspectionTemplates" parent WHERE parent."Id" = "InspectionTemplateItems"."TemplateId"));
        CREATE POLICY tenant_insert ON "InspectionTemplateItems" FOR INSERT WITH CHECK
          (pg_has_role(current_user, 'rentalcommand_engine', 'USAGE') OR EXISTS
            (SELECT 1 FROM "InspectionTemplates" parent WHERE parent."Id" = "InspectionTemplateItems"."TemplateId"
             AND parent."PortfolioId" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int));
        CREATE POLICY tenant_update ON "InspectionTemplateItems" FOR UPDATE USING
          (pg_has_role(current_user, 'rentalcommand_engine', 'USAGE') OR EXISTS
            (SELECT 1 FROM "InspectionTemplates" parent WHERE parent."Id" = "InspectionTemplateItems"."TemplateId"
             AND parent."PortfolioId" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int))
          WITH CHECK
          (pg_has_role(current_user, 'rentalcommand_engine', 'USAGE') OR EXISTS
            (SELECT 1 FROM "InspectionTemplates" parent WHERE parent."Id" = "InspectionTemplateItems"."TemplateId"
             AND parent."PortfolioId" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int));
        CREATE POLICY tenant_delete ON "InspectionTemplateItems" FOR DELETE USING
          (pg_has_role(current_user, 'rentalcommand_engine', 'USAGE') OR EXISTS
            (SELECT 1 FROM "InspectionTemplates" parent WHERE parent."Id" = "InspectionTemplateItems"."TemplateId"
             AND parent."PortfolioId" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int));
        """;

    private static TableOperation ApiOperations(string table)
    {
        var baseOperations = ApiReadOnlyTables.Contains(table)
            ? TableOperation.Select
            : AppendOnlyTables.Contains(table)
                ? TableOperation.Select | TableOperation.Insert
                : ApiDeleteTables.Contains(table)
                    ? TableOperation.All
                    : ApiMutableTables.Contains(table)
                        ? TableOperation.Select | TableOperation.Insert | TableOperation.Update
                        : throw new InvalidOperationException(
                            $"Mapped table {table} has no explicit API grant classification.");

        return SandboxGraduationDeleteTables.Contains(table)
            ? baseOperations | TableOperation.Delete
            : baseOperations;
    }

    private static TableOperation EngineOperations(string table)
    {
        if (EngineDeniedTables.Contains(table)) return TableOperation.None;
        if (EngineSystemTemplateTables.Contains(table)) return TableOperation.All;
        if (EngineAppendOnlyTables.Contains(table)) return TableOperation.Select | TableOperation.Insert;
        if (EngineMutableTables.Contains(table)) return TableOperation.Select | TableOperation.Insert | TableOperation.Update;
        if (EngineReadOnlyTables.Contains(table)) return TableOperation.Select;
        throw new InvalidOperationException($"Mapped table {table} has no explicit engine grant classification.");
    }

    private static string? GrantTableSql(string table, string role, TableOperation operations) =>
        operations == TableOperation.None ? null :
            $"GRANT {OperationSql(operations)} ON TABLE {Quote(table)} TO {role};";

    private static string? RevokeTableSql(string table, string role, TableOperation operations) =>
        operations == TableOperation.None ? null :
            $"REVOKE {OperationSql(operations)} ON TABLE {Quote(table)} FROM {role};";

    private static string OperationSql(TableOperation operations) => string.Join(", ", new[]
    {
        (TableOperation.Select, "SELECT"), (TableOperation.Insert, "INSERT"),
        (TableOperation.Update, "UPDATE"), (TableOperation.Delete, "DELETE"),
    }.Where(item => operations.HasFlag(item.Item1)).Select(item => item.Item2));

    private static string BuildSequenceGrantSql(string role, IEnumerable<string> insertTables, bool revoke)
    {
        var tableNames = string.Join(", ", insertTables.Select(SqlLiteral));
        var operation = revoke ? "REVOKE USAGE, SELECT ON SEQUENCE" : "GRANT USAGE, SELECT ON SEQUENCE";
        var roleClause = revoke ? $"FROM {role}" : $"TO {role}";
        return $"""
            DO $sequences$
            DECLARE
              sequence_name text;
            BEGIN
              FOR sequence_name IN
                SELECT DISTINCT format('%I.%I', sequence_namespace.nspname, sequence.relname)
                FROM pg_class AS base_table
                JOIN pg_namespace AS base_namespace ON base_namespace.oid = base_table.relnamespace
                JOIN pg_depend AS dependency
                  ON dependency.refobjid = base_table.oid
                 AND dependency.refclassid = 'pg_class'::regclass
                 AND dependency.deptype IN ('a', 'i')
                JOIN pg_class AS sequence
                  ON sequence.oid = dependency.objid
                 AND sequence.relkind = 'S'
                JOIN pg_namespace AS sequence_namespace ON sequence_namespace.oid = sequence.relnamespace
                WHERE base_namespace.nspname = 'public'
                  AND base_table.relname = ANY (ARRAY[{tableNames}]::text[])
              LOOP
                EXECUTE '{operation} ' || sequence_name || ' {roleClause}';
              END LOOP;
            END
            $sequences$;
            """;
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string SqlLiteral(string value) => $"'{value.Replace("'", "''")}'";

    internal sealed record ChildPolicy(string Table, string ParentTable, string ForeignKey);

    [Flags]
    private enum TableOperation
    {
        None = 0,
        Select = 1,
        Insert = 2,
        Update = 4,
        Delete = 8,
        All = Select | Insert | Update | Delete,
    }
}

/// <summary>Canonical SQL home for the current provider-neutral parked-accounting read model.</summary>
internal static class AccountingParkedTransactionViewSql
{
    public const string Drop = "DROP VIEW IF EXISTS \"vw_accounting_parked_transactions\";";

    public const string Create = """
        CREATE VIEW "vw_accounting_parked_transactions" WITH (security_invoker = true) AS
        SELECT
            mapping."Id",
            mapping."PortfolioId",
            mapping."AccountingConnectionId",
            mapping."ExternalType",
            mapping."ExternalId",
            mapping."MetadataJson" ->> 'CustomerExternalId' AS "CustomerExternalId",
            mapping."MetadataJson" ->> 'VendorExternalId' AS "VendorExternalId",
            mapping."MetadataJson" ->> 'AccountExternalId' AS "AccountExternalId",
            mapping."MetadataJson" ->> 'ClassExternalId' AS "ClassExternalId",
            mapping."MetadataJson" ->> 'DepositAccountExternalId' AS "DepositAccountExternalId",
            COALESCE(NULLIF(mapping."MetadataJson" ->> 'Amount', '')::numeric, 0) AS "Amount",
            COALESCE(
              NULLIF(mapping."MetadataJson" ->> 'TxnDateUtc', '')::timestamptz,
              '-infinity'::timestamptz) AS "TxnDateUtc",
            mapping."MetadataJson" ->> 'PaymentMethod' AS "PaymentMethod",
            mapping."MetadataJson" ->> 'ReferenceNumber' AS "ReferenceNumber",
            mapping."MetadataJson" ->> 'SourceKind' AS "SourceKind"
        FROM "AccountingSyncMaps" AS mapping
        WHERE mapping."MetadataJson" IS NOT NULL;
        """;
}
