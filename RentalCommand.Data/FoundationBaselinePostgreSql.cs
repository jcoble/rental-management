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

    private static readonly Lazy<IReadOnlyList<string>> CreateStatementsValue = new(() =>
    [
        CreateAuditSearchInfrastructure,
        LeaseEffectiveClockSql.CreateEffectiveNowUtc,
        LeaseEffectiveClockSql.CreateBusinessDate,
        ScheduleEDepreciationFunctionSql.Create,
        LeaseAgreementStatusViewSql.Create,
        LeaseAddendumStatusViewSql.Create,
        TenantChargeBalanceViewSql.Create,
        TenantAccountBalanceViewSql.Create,
        SecurityDepositBalanceViewSql.Create,
        UnitOccupancyViewSql.Create,
        LeaseManagementLifecycleViewSql.Create,
        LeaseReconciliationExceptionViewSql.Create,
        MorningBriefingCandidateViewSql.Create,
        RelationshipAccessProjectionSql.Create,
        AccessEnvelopeViewSql.Create,
        AccountingParkedTransactionViewSql.Create,
        CreateWorkOrderResponsibilityInfrastructure,
        BuildRolesAndGrantSql(),
        RlsAuthorityFunctionSql,
        BuildCreateRlsSql(),
        CreateSandboxGraduationGlobalDeleteGuards,
    ]);

    internal static IReadOnlyList<string> CreateStatements => CreateStatementsValue.Value;

    private static readonly Lazy<IReadOnlyList<string>> DropStatementsValue = new(() =>
    [
        DropSandboxGraduationGlobalDeleteGuards,
        BuildDropRlsSql(),
        DropRlsAuthorityFunctions,
        BuildRevokeRoleGrantsSql(),
        DropWorkOrderResponsibilityInfrastructure,
        AccountingParkedTransactionViewSql.Drop,
        AccessEnvelopeViewSql.Drop,
        RelationshipAccessProjectionSql.Drop,
        LeaseReconciliationExceptionViewSql.Drop,
        MorningBriefingCandidateViewSql.Drop,
        LeaseManagementLifecycleViewSql.Drop,
        UnitOccupancyViewSql.Drop,
        SecurityDepositBalanceViewSql.Drop,
        TenantAccountBalanceViewSql.Drop,
        TenantChargeBalanceViewSql.Drop,
        LeaseAddendumStatusViewSql.Drop,
        LeaseAgreementStatusViewSql.Drop,
        LeaseEffectiveClockSql.DropBusinessDate,
        LeaseEffectiveClockSql.DropEffectiveNowUtc,
        ScheduleEDepreciationFunctionSql.Drop,
        DropAuditSearchIndexes,
    ]);

    internal static IReadOnlyList<string> DropStatements => DropStatementsValue.Value;

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
        "BankConnections",
        "BankTransactions",
        "CapitalAssets",
        "Conversations",
        "DeviceTokens",
        "DocumentTemplateFields",
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
        "LegalDocumentSourceVersions",
        "ListingPhotos",
        "ListingPublications",
        "Loans",
        "LoanPayments",
        "MembershipRoleAssignmentProperties",
        "MembershipRoleAssignments",
        "NoticeDeliveryEvidence",
        "NoticeDrafts",
        "AutomationSettings",
        "MessagingProviderSettings",
        "Notifications",
        "NotificationReadStates",
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
        "TechnicianWorkEntries",
        "UnitOperationalPeriods",
        "Units",
        "UserAlertPreferences",
        "VendorDispatches",
        "VendorRatings",
        "Vendors",
        "WorkspaceAccessContexts",
        "WorkspaceMemberships",
        "WorkspaceNoticeTemplateVersions",
        "WorkOrders",
        "WorkOrderResponsibilities",
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
        "AspNetUserClaims",
        "AspNetUserLogins",
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
        "RoleProfileCapabilities",
        "RoleProfiles",
        "SimWorkerCommands",
        "SimulationClocks",
        "SystemNoticeTemplateVersions",
        "WorkspaceInvitations",
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
        "RefreshTokens",
        "UserAccounts",
        "AspNetRoleClaims",
        "AspNetRoles",
        "AspNetUserRoles",
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
        "vw_morning_briefing_candidates",
        "vw_effective_owner_access",
        "vw_effective_tenant_access",
        "vw_access_envelopes",
        "vw_accounting_parked_transactions",
    ];

    /// <summary>
    /// Complete base-table read surface of the NOLOGIN RLS authority. This includes transitive
    /// dependencies of the security-invoker tenant-access view and its effective-clock functions;
    /// adding a relation to an authority function must update this reviewed inventory.
    /// </summary>
    internal static IReadOnlyList<string> RlsAuthoritySelectTables { get; } =
    [
        "AspNetUsers",
        "AtomicCommandReceipts",
        "AuthSessions",
        "CapabilityDefinitions",
        "LeaseManagementParties",
        "LeaseManagements",
        "LoginContextSelectionChallenges",
        "MembershipRoleAssignmentProperties",
        "MembershipRoleAssignments",
        "OwnerEntities",
        "OwnerUserAccesses",
        "Portfolios",
        "Properties",
        "RoleProfileCapabilities",
        "RoleProfiles",
        "SimulationClocks",
        "SystemNoticeTemplateVersions",
        "TenantAccounts",
        "TenantUserAccesses",
        "Units",
        "WorkOrders",
        "WorkOrderResponsibilities",
        "WorkspaceAccessContexts",
        "WorkspaceMemberships",
        "WorkspaceNoticeTemplateVersions",
    ];

    internal static IReadOnlyList<string> RlsAuthoritySelectViews { get; } =
    [
        "vw_effective_tenant_access",
    ];

    internal static IReadOnlyList<string> RlsAuthorityInsertTables { get; } =
    [
        "AutomationSettings",
        "MembershipRoleAssignments",
        "OwnerEntities",
        "OwnerUserAccesses",
        "Portfolios",
        "TeamRoutingRules",
        "TenantNoticePolicies",
        "UserAlertPreferences",
        "WorkspaceAccessContexts",
        "WorkspaceMemberships",
        "WorkspaceNoticeTemplateVersions",
    ];

    internal static IReadOnlyList<string> RlsAuthorityExecuteFunctions { get; } =
    [
        "rc_business_date(integer)",
        "rc_effective_now_utc(integer)",
    ];

    internal static IReadOnlyList<string> RlsAuthorityOwnedFunctions { get; } =
    [
        "rc_api_scope_allows(integer)",
        "rc_api_resource_scope_allows(integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean)",
        "rc_account_bootstrap_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text)",
        "rc_pre_auth_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)",
        "rc_pre_auth_email_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)",
        "rc_sandbox_graduation_allows(integer)",
        "rc_access_context_is_effective(integer, integer, timestamp with time zone)",
        "rc_list_effective_access_contexts(integer, timestamp with time zone)",
        "rc_bootstrap_initial_workspace(integer, text, text, text, text, timestamp with time zone)",
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
        "AtomicAuditLogs", "NoticeDeliveryEvidence", "RenderedNotices",
        "SecurityDepositEntries", "SignatureAuditEvents", "TenantLedgerAllocations",
        "TenantLedgerEntries", "LegalDocumentSourceVersions", "WorkspaceNoticeTemplateVersions",
    };

    // Catalogs and supplied system templates are data, not runtime configuration mutation surfaces.
    private static readonly HashSet<string> ApiReadOnlyTables = new(StringComparer.Ordinal)
    {
        "CapabilityDefinitions", "EngineWorkerHeartbeats",
        "RoleProfileCapabilities", "RoleProfiles", "SystemNoticeTemplateVersions",
    };

    // These canonical resource-bearing tables need a stricter boundary than workspace isolation.
    // Their replacement policies keep selected-property, assigned-work, Owner, and Tenant scopes
    // in PostgreSQL even if an application query accidentally omits its endpoint predicate.
    private static readonly HashSet<string> ResourcePolicyTables = new(StringComparer.Ordinal)
    {
        "Conversations", "LeaseAddenda", "LeaseAgreements", "LeaseManagementParties",
        "LeaseManagements", "LeaseRenewalAddendumDecisions", "Properties", "SecurityDepositAccounts",
        "TenantAutopayEnrollments", "TenantLedgerAllocations", "TenantLedgerEntries",
        "TenantAccountConditionPeriods", "TenantPaymentAttempts", "TechnicianWorkEntries", "Units",
        "VendorDispatches", "WorkOrderResponsibilities", "WorkOrders", "WorkOrderStatusEvents",
    };

    // DELETE is deliberately exceptional. Durable leases, accounts, legal artifacts, ledgers, and
    // history are absent even though the development sandbox currently hard-deletes some of them.
    private static readonly HashSet<string> ApiDeleteTables = new(StringComparer.Ordinal)
    {
        "AccountingConnections", "Appointments", "AspNetUserClaims", "AspNetUserLogins",
        "AspNetUsers", "AspNetUserTokens", "DeviceTokens",
        "DocumentTemplateFields", "ExpenseLineItems", "InspectionItems", "Inspections",
        "InspectionTemplateItems", "InspectionTemplates", "LeaseAddendumFinancialEffects",
        "LeaseAddendumSigners", "LeaseAgreementSigners", "MembershipRoleAssignmentProperties",
        "OAuthStates", "TeamRoutingRuleRecipients", "TeamRoutingRules",
    };

    // Exact set-based delete inventory in SandboxService.WipePortfolioDataAsync. Portfolio-owned
    // operational facts are removed when a sandbox graduates; reusable workspace/security/device
    // configuration is explicitly classified in SandboxGraduationPreservedTables below. Tables
    // absent from ApiDeleteTables are durable canonical/history surfaces whose DELETE policy accepts
    // only the dedicated SandboxGraduation bypass reason.
    internal static readonly IReadOnlySet<string> SandboxGraduationDeleteTables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "AccountingEntityMappings", "AccountingMappingPromotionJobs", "AccountingSyncMaps",
            "AdverseActionNotices", "ApplicantScreeningMilestones", "ApplicantScreenings",
            "ApplicationFinancialAccounts", "ApplicationFinancialEntries",
            "Appointments", "AtomicAuditLogs", "AtomicCommandReceipts",
            "BankTransactions", "CapitalAssets", "ConversationMessages",
            "Conversations", "DocumentTemplateFields", "DocumentTemplates", "EvictionCaseEvents",
            "EvictionCaseRespondents", "EvictionCases", "ExpenseLineItems", "Expenses",
            "ExternalListingSignals", "InspectionItems", "Inspections", "LeaseAddenda",
            "LeaseAddendumFinancialEffects", "LeaseAddendumSigners", "LeaseAgreements",
            "LeaseAgreementSigners", "LeaseManagementParties", "LeaseManagements",
            "LeaseRenewalAddendumDecisions", "LegalDocumentArtifacts", "ListingPhotos",
            "ListingPublications", "LoanPayments", "Loans", "NoticeDeliveryEvidence",
            "MembershipRoleAssignmentProperties",
            "NoticeDrafts", "Notifications", "NotificationReadStates", "OAuthStates", "OutboxMessages", "OwnerDistributions",
            "OwnerEntities", "OwnerUserAccesses", "Owners", "PendingFileUploads",
            "PlaidTokenExchangeAttempts", "PortalMessages", "Properties", "PropertyDispositions",
            "ProviderInboxEvents", "QueuedJobs", "RecurringExpenses", "RecurringMaintenanceTasks",
            "RenderedNotices", "RentalApplications", "RentalListings", "ScanBatches", "ScanDrafts",
            "SecurityDepositAccounts", "SecurityDepositEntries", "SignatureAuditEvents",
            "SignatureRequests", "SignatureSigners", "StoredFiles", "TenantAccountConditionPeriods", "TenantAccounts",
            "TenantAutopayEnrollments", "TenantLedgerAllocations", "TenantLedgerEntries",
            "TenantNoticeWorkItems", "TenantPaymentAttempts", "TenantUserAccesses", "TeamRoutingRuleRecipients",
            "TeamRoutingRules", "TechnicianWorkEntries", "Tenants",
            "UnitOperationalPeriods", "Units", "VendorDispatches", "VendorRatings",
            "Vendors", "WorkOrderResponsibilities", "WorkOrders", "WorkOrderStatusEvents",
        };

    // Explicit complement of the portfolio/transitive model above. These rows are workspace identity,
    // authorization, device registration, or reusable configuration and must survive graduation.
    // Global system catalogs and the Portfolio row are classified separately by the baseline model.
    internal static readonly IReadOnlySet<string> SandboxGraduationPreservedTables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "AccountingConnections", "BankConnections",
            "DeviceTokens", "InspectionTemplateItems", "InspectionTemplates",
            "LegalDocumentSourceVersions", "MembershipRoleAssignments",
            "AutomationSettings", "MessagingProviderSettings", "TenantNoticePolicies",
            "UserAlertPreferences", "WorkspaceAccessContexts", "WorkspaceMemberships",
            "WorkspaceNoticeTemplateVersions",
        };

    internal static readonly IReadOnlySet<string> SandboxGraduationGlobalDeleteTables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "AtomicCommandReceipts", "OutboxMessages", "ProviderInboxEvents",
        };

    // These tables require row-level classification: templates use explicit seed provenance, their
    // fields follow the parent, and files referenced by preserved user templates survive.
    internal static readonly IReadOnlySet<string> SandboxGraduationSelectiveDeleteTables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "DocumentTemplateFields", "DocumentTemplates", "StoredFiles",
            "AccountingEntityMappings", "AccountingSyncMaps", "MembershipRoleAssignmentProperties",
            "TeamRoutingRuleRecipients", "TeamRoutingRules",
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
        "AutomationSettings", "MessagingProviderSettings", "Notifications", "NotificationReadStates", "OutboxMessages",
        "OwnerDistributions", "OwnerEntities", "OwnerUserAccesses", "Owners", "PendingFileUploads",
        "PlaidTokenExchangeAttempts", "PortalMessages", "Portfolios", "Properties",
        "PropertyDispositions", "ProviderInboxEvents", "QueuedJobs", "RecurringExpenses",
        "RecurringMaintenanceTasks", "RentalApplications", "RentalListings",
        "ScanBatches", "ScanDrafts", "SecurityDepositAccounts", "SignatureRequests",
        "SignatureSigners", "SimWorkerCommands", "SimulationClocks", "StoredFiles",
        "TeamRoutingRules", "TenantAccountConditionPeriods", "TenantAccounts",
        "TenantAutopayEnrollments", "TenantNoticePolicies", "TenantNoticeWorkItems",
        "TenantPaymentAttempts", "TenantUserAccesses", "TechnicianWorkEntries", "Tenants", "UnitOperationalPeriods",
        "Units", "UserAlertPreferences", "VendorDispatches", "VendorRatings",
        "Vendors", "WorkOrderResponsibilities", "WorkOrderStatusEvents", "WorkOrders", "WorkspaceAccessContexts",
        "WorkspaceInvitations", "WorkspaceMemberships",
    };

    // Engine reads are broad across portfolio data because projection views use security_invoker,
    // but authentication credentials and refresh/session material are intentionally API-only.
    private static readonly HashSet<string> EngineDeniedTables = new(StringComparer.Ordinal)
    {
        "AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens",
        "AuthSessionRefreshCredentials", "AuthSessionRefreshTokenFamilies", "AuthSessions",
        "LoginContextSelectionChallenges", "NotificationReadStates", "OAuthStates", "PlaidTokenExchangeAttempts",
        "WorkspaceInvitations",
    };

    private static readonly HashSet<string> EngineAppendOnlyTables = new(StringComparer.Ordinal)
    {
        "AtomicAuditLogs", "NoticeDeliveryEvidence", "Notifications",
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
        "AspNetUsers", "CapabilityDefinitions", "CapitalAssets",
        "DeviceTokens", "DocumentTemplateFields", "DocumentTemplates", "EvictionCaseEvents",
        "EvictionCaseRespondents", "EvictionCases", "ExternalListingSignals", "InspectionItems",
        "Inspections", "LeaseAddenda", "LeaseAddendumFinancialEffects", "LeaseAddendumSigners",
        "LeaseAgreementSigners", "LeaseManagementParties", "LeaseManagements",
        "LeaseRenewalAddendumDecisions", "LegalDocumentArtifacts", "LegalDocumentSourceVersions", "ListingPhotos",
        "ListingPublications", "LoanPayments", "Loans", "MembershipRoleAssignmentProperties",
        "MembershipRoleAssignments", "AutomationSettings", "MessagingProviderSettings",
        "OwnerDistributions", "OwnerEntities", "OwnerUserAccesses", "Owners", "PortalMessages",
        "Portfolios", "Properties", "PropertyDispositions", "QueuedJobs", "RentalApplications",
        "RentalListings", "RoleProfileCapabilities", "RoleProfiles", "SimulationClocks",
        "StoredFiles", "SystemNoticeTemplateVersions", "TeamRoutingRuleRecipients",
        "TeamRoutingRules", "TenantAccountConditionPeriods", "TenantAutopayEnrollments",
        "TenantNoticePolicies", "TenantUserAccesses", "TechnicianWorkEntries", "Tenants", "UnitOperationalPeriods", "Units",
        "UserAlertPreferences", "VendorDispatches", "VendorRatings", "Vendors",
        "WorkOrderResponsibilities",
        "WorkspaceAccessContexts", "WorkspaceMemberships",
    };

    private static readonly HashSet<string> EngineViews = new(StringComparer.Ordinal)
    {
        "vw_lease_agreement_status", "vw_lease_addendum_status", "vw_tenant_charge_balances",
        "vw_tenant_account_balances", "vw_security_deposit_balances", "vw_unit_occupancy",
        "vw_lease_management_lifecycle", "vw_lease_reconciliation_exceptions",
        "vw_morning_briefing_candidates",
        "vw_effective_tenant_access", "vw_accounting_parked_transactions",
    };

    private const string CreateAuditSearchInfrastructure = """
        CREATE INDEX IF NOT EXISTS "IX_AtomicAuditLogs_EntityType_trgm"
          ON "AtomicAuditLogs" USING gin (lower("EntityType") gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS "IX_AtomicAuditLogs_ActorLabel_trgm"
          ON "AtomicAuditLogs" USING gin (lower("ActorLabel") gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS "IX_AtomicAuditLogs_IpAddress_trgm"
          ON "AtomicAuditLogs" USING gin (lower("IpAddress") gin_trgm_ops);
        """;

    private const string DropAuditSearchIndexes = """
        DROP INDEX IF EXISTS "IX_AtomicAuditLogs_IpAddress_trgm";
        DROP INDEX IF EXISTS "IX_AtomicAuditLogs_ActorLabel_trgm";
        DROP INDEX IF EXISTS "IX_AtomicAuditLogs_EntityType_trgm";
        """;

    private static string BuildRolesAndGrantSql()
    {
        var statements = new List<string>
        {
            """
            DO $role$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rentalcommand_api') THEN
                CREATE ROLE rentalcommand_api LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1;
              ELSIF EXISTS (
                SELECT 1 FROM pg_roles runtime_role
                WHERE runtime_role.rolname = 'rentalcommand_api'
                  AND (NOT runtime_role.rolcanlogin OR runtime_role.rolsuper OR runtime_role.rolinherit
                    OR runtime_role.rolcreatedb OR runtime_role.rolcreaterole OR runtime_role.rolreplication
                    OR runtime_role.rolbypassrls OR runtime_role.rolconnlimit <> -1
                    OR runtime_role.rolvaliduntil IS NOT NULL OR runtime_role.rolconfig IS NOT NULL)
              ) OR EXISTS (
                SELECT 1
                FROM pg_roles runtime_role
                JOIN pg_auth_members inherited_membership
                  ON inherited_membership.member = runtime_role.oid
                  OR inherited_membership.roleid = runtime_role.oid
                WHERE runtime_role.rolname = 'rentalcommand_api'
              ) THEN
                RAISE EXCEPTION 'Existing role rentalcommand_api has incompatible cluster-wide attributes';
              END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rentalcommand_engine') THEN
                CREATE ROLE rentalcommand_engine LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1;
              ELSIF EXISTS (
                SELECT 1 FROM pg_roles runtime_role
                WHERE runtime_role.rolname = 'rentalcommand_engine'
                  AND (NOT runtime_role.rolcanlogin OR runtime_role.rolsuper OR runtime_role.rolinherit
                    OR runtime_role.rolcreatedb OR runtime_role.rolcreaterole OR runtime_role.rolreplication
                    OR runtime_role.rolbypassrls OR runtime_role.rolconnlimit <> -1
                    OR runtime_role.rolvaliduntil IS NOT NULL OR runtime_role.rolconfig IS NOT NULL)
              ) OR EXISTS (
                SELECT 1
                FROM pg_roles runtime_role
                JOIN pg_auth_members inherited_membership
                  ON inherited_membership.member = runtime_role.oid
                  OR inherited_membership.roleid = runtime_role.oid
                WHERE runtime_role.rolname = 'rentalcommand_engine'
              ) THEN
                RAISE EXCEPTION 'Existing role rentalcommand_engine has incompatible cluster-wide attributes';
              END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rentalcommand_rls_authority') THEN
                CREATE ROLE rentalcommand_rls_authority NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION BYPASSRLS CONNECTION LIMIT -1;
              ELSIF EXISTS (
                SELECT 1 FROM pg_roles runtime_role
                WHERE runtime_role.rolname = 'rentalcommand_rls_authority'
                  AND (runtime_role.rolcanlogin OR runtime_role.rolsuper OR runtime_role.rolinherit
                    OR runtime_role.rolcreatedb OR runtime_role.rolcreaterole OR runtime_role.rolreplication
                    OR NOT runtime_role.rolbypassrls OR runtime_role.rolconnlimit <> -1
                    OR runtime_role.rolvaliduntil IS NOT NULL OR runtime_role.rolconfig IS NOT NULL)
              ) OR EXISTS (
                SELECT 1
                FROM pg_roles runtime_role
                JOIN pg_auth_members inherited_membership
                  ON inherited_membership.member = runtime_role.oid
                  OR inherited_membership.roleid = runtime_role.oid
                WHERE runtime_role.rolname = 'rentalcommand_rls_authority'
              ) THEN
                RAISE EXCEPTION 'Existing role rentalcommand_rls_authority has incompatible cluster-wide attributes';
              END IF;
            END
            $role$;
            """,
            "DO $grant$ BEGIN EXECUTE format('GRANT CONNECT ON DATABASE %I TO rentalcommand_api', current_database()); END $grant$;",
            "DO $grant$ BEGIN EXECUTE format('GRANT CONNECT ON DATABASE %I TO rentalcommand_engine', current_database()); END $grant$;",
            "GRANT USAGE ON SCHEMA public TO rentalcommand_api;",
            "GRANT USAGE ON SCHEMA public TO rentalcommand_engine;",
            "GRANT USAGE ON SCHEMA public TO rentalcommand_rls_authority;",
        };

        statements.AddRange(RlsAuthoritySelectTables.Select(table =>
            $"GRANT SELECT ON TABLE {Quote(table)} TO rentalcommand_rls_authority;"));
        statements.AddRange(RlsAuthoritySelectViews.Select(view =>
            $"GRANT SELECT ON TABLE {Quote(view)} TO rentalcommand_rls_authority;"));
        statements.AddRange(RlsAuthorityInsertTables.Select(table =>
            $"GRANT INSERT ON TABLE {Quote(table)} TO rentalcommand_rls_authority;"));
        statements.AddRange(RlsAuthorityExecuteFunctions.Select(function =>
            $"GRANT EXECUTE ON FUNCTION {function} TO rentalcommand_rls_authority;"));
        statements.Add(BuildSequenceGrantSql(
            "rentalcommand_rls_authority", RlsAuthorityInsertTables, revoke: false));

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
        statements.AddRange(RlsAuthorityExecuteFunctions.Select(function =>
            $"REVOKE EXECUTE ON FUNCTION {function} FROM rentalcommand_rls_authority;"));
        statements.AddRange(RlsAuthorityInsertTables.Select(table =>
            $"REVOKE INSERT ON TABLE {Quote(table)} FROM rentalcommand_rls_authority;"));
        statements.AddRange(RlsAuthoritySelectViews.Select(view =>
            $"REVOKE SELECT ON TABLE {Quote(view)} FROM rentalcommand_rls_authority;"));
        statements.AddRange(RlsAuthoritySelectTables.Select(table =>
            $"REVOKE SELECT ON TABLE {Quote(table)} FROM rentalcommand_rls_authority;"));
        statements.Add(BuildSequenceGrantSql(
            "rentalcommand_rls_authority", RlsAuthorityInsertTables, revoke: true));
        statements.Add("REVOKE USAGE ON SCHEMA public FROM rentalcommand_rls_authority;");
        statements.Add("DO $revoke$ BEGIN EXECUTE format('REVOKE CONNECT ON DATABASE %I FROM rentalcommand_api', current_database()); END $revoke$;");
        statements.Add("DO $revoke$ BEGIN EXECUTE format('REVOKE CONNECT ON DATABASE %I FROM rentalcommand_engine', current_database()); END $revoke$;");

        // Roles and their membership are cluster-wide provisioning objects. Down removes only this
        // database's grants; revoking current_user membership here could break another database that
        // deliberately shares the same runtime roles.
        return string.Join(Environment.NewLine, statements);
    }

    private static string BuildCreateRlsSql()
    {
        var statements = new List<string>();
        statements.AddRange(DirectPortfolioTables
            .Where(table => !ResourcePolicyTables.Contains(table))
            .Select(table =>
            table == "AtomicAuditLogs"
                ? CreateAtomicAuditPolicySql()
                : RequiresSandboxGraduationDelete(table)
                ? CreateSandboxGraduationDeletePolicySql(
                    table,
                    PortfolioPredicate,
                    DirectSandboxGraduationPredicate)
                : CreatePolicySql(table, PortfolioPredicate)));
        statements.AddRange(BuildResourcePoliciesSql());
        statements.Add(CreatePolicySql("Portfolios", PortfolioSelfPredicate));
        statements.Add(BuildInspectionTemplatePoliciesSql());
        statements.AddRange(ChildPortfolioTables.Where(policy => policy.Table != "InspectionTemplateItems")
            .Select(policy => RequiresSandboxGraduationDelete(policy.Table)
                ? CreateSandboxGraduationDeletePolicySql(
                    policy.Table,
                    ChildPredicate(policy),
                    ChildSandboxGraduationPredicate(policy))
                : CreatePolicySql(policy.Table, ChildPredicate(policy))));
        statements.Add(BuildInspectionTemplateItemPoliciesSql());
        return string.Join(Environment.NewLine, statements);
    }

    private const string CreateWorkOrderResponsibilityInfrastructure = """
        ALTER TABLE "WorkOrderResponsibilities"
          ADD CONSTRAINT "EX_WorkOrderResponsibilities_NoMemberOverlap"
          EXCLUDE USING gist (
            "PortfolioId" WITH =,
            "WorkOrderId" WITH =,
            "WorkspaceMembershipId" WITH =,
            tstzrange("EffectiveFromUtc", COALESCE("EffectiveToUtc", 'infinity'::timestamptz), '[)') WITH &&);
        ALTER TABLE "WorkOrderResponsibilities"
          ADD CONSTRAINT "EX_WorkOrderResponsibilities_NoPrimaryOverlap"
          EXCLUDE USING gist (
            "PortfolioId" WITH =,
            "WorkOrderId" WITH =,
            tstzrange("EffectiveFromUtc", COALESCE("EffectiveToUtc", 'infinity'::timestamptz), '[)') WITH &&)
          WHERE ("Kind" = 'Primary');

        CREATE OR REPLACE FUNCTION rc_guard_work_order_responsibility_history()
        RETURNS trigger LANGUAGE plpgsql AS $function$
        BEGIN
          IF TG_OP = 'DELETE' THEN
            -- DELETE authority is owned by the table's RLS policy. Ordinary runtime requests have
            -- no delete grant; the audited sandbox-graduation policy is the one permitted exception.
            RETURN OLD;
          END IF;
          IF OLD."EffectiveToUtc" IS NOT NULL OR
             NEW."Id" <> OLD."Id" OR NEW."PortfolioId" <> OLD."PortfolioId" OR
             NEW."PropertyId" <> OLD."PropertyId" OR NEW."WorkOrderId" <> OLD."WorkOrderId" OR
             NEW."WorkspaceMembershipId" <> OLD."WorkspaceMembershipId" OR
             NEW."MembershipRoleAssignmentId" <> OLD."MembershipRoleAssignmentId" OR
             NEW."Kind" <> OLD."Kind" OR NEW."EffectiveFromUtc" <> OLD."EffectiveFromUtc" OR
             NEW."AssignedByUserId" <> OLD."AssignedByUserId" OR
             NEW."AssignedByAccessContextId" <> OLD."AssignedByAccessContextId" OR
             NEW."AssignedReason" <> OLD."AssignedReason" OR NEW."AssignedAtUtc" <> OLD."AssignedAtUtc" OR
             NEW."EffectiveToUtc" IS NULL OR NEW."EndedAtUtc" <> NEW."EffectiveToUtc" OR
             NEW."EndedByUserId" IS NULL OR NEW."EndedByAccessContextId" IS NULL OR NEW."EndedReason" IS NULL
          THEN
            RAISE EXCEPTION 'Work-order responsibility rows are append-preserved and may only be ended once';
          END IF;
          RETURN NEW;
        END;
        $function$;
        CREATE TRIGGER "TR_WorkOrderResponsibilities_AppendPreserved"
          BEFORE UPDATE OR DELETE ON "WorkOrderResponsibilities"
          FOR EACH ROW EXECUTE FUNCTION rc_guard_work_order_responsibility_history();
        """;

    private const string DropWorkOrderResponsibilityInfrastructure = """
        DROP TRIGGER IF EXISTS "TR_WorkOrderResponsibilities_AppendPreserved" ON "WorkOrderResponsibilities";
        DROP FUNCTION IF EXISTS rc_guard_work_order_responsibility_history();
        ALTER TABLE "WorkOrderResponsibilities"
          DROP CONSTRAINT IF EXISTS "EX_WorkOrderResponsibilities_NoPrimaryOverlap";
        ALTER TABLE "WorkOrderResponsibilities"
          DROP CONSTRAINT IF EXISTS "EX_WorkOrderResponsibilities_NoMemberOverlap";
        """;

    /// <summary>
    /// Validates an ordinary API request against canonical session/access rows. The function owner
    /// is a NOLOGIN role so FORCE RLS cannot recursively filter the two authority tables while the
    /// runtime API, Engine, and authority logins remain unable to assume the role.
    /// </summary>
    internal const string RlsAuthorityFunctionSql = """
        CREATE OR REPLACE FUNCTION rc_api_scope_allows(target_portfolio_id integer)
        RETURNS boolean
        LANGUAGE sql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
          SELECT CASE
            WHEN session_user = 'rentalcommand_engine' THEN TRUE
            WHEN session_user IS DISTINCT FROM 'rentalcommand_api' OR target_portfolio_id IS NULL OR target_portfolio_id <= 0 THEN FALSE
            ELSE EXISTS (
              SELECT 1
              FROM public."AuthSessions" session
              JOIN public."WorkspaceAccessContexts" access_context
                ON access_context."Id" = session."ActiveAccessContextId"
               AND access_context."UserId" = session."UserId"
              LEFT JOIN public."WorkspaceMemberships" membership
                ON membership."AccessContextId" = access_context."Id"
              WHERE session."Id" = NULLIF(current_setting('app.auth_session_id', true), '')::uuid
                AND session."UserId" = NULLIF(current_setting('app.current_user_id', true), '')::integer
                AND access_context."Id" = NULLIF(current_setting('app.current_access_context_id', true), '')::integer
                AND access_context."PortfolioId" = target_portfolio_id
                AND access_context."AccessRevision" = NULLIF(current_setting('app.access_revision', true), '')::bigint
                AND session."Status" = 'Active'
                AND session."RevokedAtUtc" IS NULL
                AND session."ExpiresAtUtc" > CURRENT_TIMESTAMP
                AND access_context."Status" = 'Active'
                AND access_context."SuspendedAtUtc" IS NULL
                AND access_context."RevokedAtUtc" IS NULL
                AND (membership."Id" IS NULL OR (
                  membership."Status" = 'Active'
                  AND membership."PortfolioId" = target_portfolio_id
                  AND membership."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                  AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > CURRENT_TIMESTAMP)
                  AND membership."SuspendedAtUtc" IS NULL
                  AND membership."RevokedAtUtc" IS NULL
                ))
            )
          END;
        $function$;

        ALTER FUNCTION rc_api_scope_allows(integer) OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_api_scope_allows(integer) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_api_scope_allows(integer)
          TO rentalcommand_api, rentalcommand_engine;

        CREATE OR REPLACE FUNCTION rc_api_resource_scope_allows(
          target_portfolio_id integer,
          target_property_id integer,
          target_unit_id integer,
          target_work_order_id integer,
          target_lease_management_id integer,
          target_tenant_account_id integer,
          target_tenant_id integer,
          allow_owner boolean,
          allow_tenant boolean,
          allow_assigned_work boolean)
        RETURNS boolean
        LANGUAGE sql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
          WITH resource AS (
            SELECT
              COALESCE(
                target_property_id,
                (SELECT unit."PropertyId"
                 FROM public."Units" unit
                 WHERE unit."Id" = target_unit_id AND unit."PortfolioId" = target_portfolio_id),
                (SELECT work_order."PropertyId"
                 FROM public."WorkOrders" work_order
                 WHERE work_order."Id" = target_work_order_id
                   AND work_order."PortfolioId" = target_portfolio_id),
                (SELECT relationship."PropertyId"
                 FROM public."LeaseManagements" relationship
                 WHERE relationship."Id" = target_lease_management_id
                   AND relationship."PortfolioId" = target_portfolio_id),
                (SELECT relationship."PropertyId"
                 FROM public."TenantAccounts" account
                 JOIN public."LeaseManagements" relationship
                   ON relationship."Id" = account."LeaseManagementId"
                  AND relationship."PortfolioId" = account."PortfolioId"
                 WHERE account."Id" = target_tenant_account_id
                   AND account."PortfolioId" = target_portfolio_id)) AS property_id,
              COALESCE(
                target_unit_id,
                (SELECT work_order."UnitId"
                 FROM public."WorkOrders" work_order
                 WHERE work_order."Id" = target_work_order_id
                   AND work_order."PortfolioId" = target_portfolio_id),
                (SELECT relationship."UnitId"
                 FROM public."LeaseManagements" relationship
                 WHERE relationship."Id" = target_lease_management_id
                   AND relationship."PortfolioId" = target_portfolio_id),
                (SELECT relationship."UnitId"
                 FROM public."TenantAccounts" account
                 JOIN public."LeaseManagements" relationship
                   ON relationship."Id" = account."LeaseManagementId"
                  AND relationship."PortfolioId" = account."PortfolioId"
                 WHERE account."Id" = target_tenant_account_id
                   AND account."PortfolioId" = target_portfolio_id)) AS unit_id,
              COALESCE(
                target_lease_management_id,
                (SELECT work_order."LeaseManagementId"
                 FROM public."WorkOrders" work_order
                 WHERE work_order."Id" = target_work_order_id
                   AND work_order."PortfolioId" = target_portfolio_id),
                (SELECT account."LeaseManagementId"
                 FROM public."TenantAccounts" account
                 WHERE account."Id" = target_tenant_account_id
                   AND account."PortfolioId" = target_portfolio_id)) AS lease_management_id
          )
          SELECT CASE
            WHEN session_user = 'rentalcommand_engine' THEN TRUE
            WHEN NOT public.rc_api_scope_allows(target_portfolio_id) THEN FALSE
            ELSE EXISTS (
              SELECT 1
              FROM resource
              JOIN public."WorkspaceMemberships" membership
                ON membership."AccessContextId" = NULLIF(
                     current_setting('app.current_access_context_id', true), '')::integer
               AND membership."PortfolioId" = target_portfolio_id
              JOIN public."MembershipRoleAssignments" assignment
                ON assignment."WorkspaceMembershipId" = membership."Id"
               AND assignment."PortfolioId" = target_portfolio_id
              WHERE membership."Status" = 'Active'
                AND membership."SuspendedAtUtc" IS NULL
                AND membership."RevokedAtUtc" IS NULL
                AND membership."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > CURRENT_TIMESTAMP)
                AND assignment."Status" = 'Active'
                AND assignment."SuspendedAtUtc" IS NULL
                AND assignment."RevokedAtUtc" IS NULL
                AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                AND (assignment."ScopeKind" = 'AllProperties'
                  OR (assignment."ScopeKind" = 'SelectedProperties'
                    AND resource.property_id IS NOT NULL
                    AND EXISTS (
                      SELECT 1
                      FROM public."MembershipRoleAssignmentProperties" selected_property
                      WHERE selected_property."MembershipRoleAssignmentId" = assignment."Id"
                        AND selected_property."PortfolioId" = target_portfolio_id
                        AND selected_property."PropertyId" = resource.property_id)))
            ) OR (allow_owner AND EXISTS (
              SELECT 1
              FROM resource
              JOIN public."Properties" property
                ON property."Id" = resource.property_id
               AND property."PortfolioId" = target_portfolio_id
              JOIN public."OwnerUserAccesses" owner_access
                ON owner_access."OwnerEntityId" = property."OwnerEntityId"
               AND owner_access."PortfolioId" = target_portfolio_id
               AND owner_access."AccessContextId" = NULLIF(
                     current_setting('app.current_access_context_id', true), '')::integer
               AND owner_access."ApplicationUserId" = NULLIF(
                     current_setting('app.current_user_id', true), '')::integer
              WHERE owner_access."RevokedAtUtc" IS NULL
                AND owner_access."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (owner_access."EffectiveToUtc" IS NULL OR owner_access."EffectiveToUtc" > CURRENT_TIMESTAMP)
            )) OR (allow_tenant AND EXISTS (
              SELECT 1
              FROM resource
              JOIN public."vw_effective_tenant_access" tenant_access
                ON tenant_access."AccessContextId" = NULLIF(
                     current_setting('app.current_access_context_id', true), '')::integer
               AND tenant_access."UserId" = NULLIF(
                     current_setting('app.current_user_id', true), '')::integer
               AND tenant_access."PortfolioId" = target_portfolio_id
               AND tenant_access."AccessRevision" = NULLIF(
                     current_setting('app.access_revision', true), '')::bigint
              WHERE (resource.property_id IS NULL OR tenant_access."PropertyId" = resource.property_id)
                AND (resource.unit_id IS NULL OR tenant_access."UnitId" = resource.unit_id)
                AND (resource.lease_management_id IS NULL
                     OR tenant_access."LeaseManagementId" = resource.lease_management_id)
                AND (target_tenant_account_id IS NULL
                     OR tenant_access."TenantAccountId" = target_tenant_account_id)
                AND (target_tenant_id IS NULL OR tenant_access."TenantId" = target_tenant_id)
                AND (target_work_order_id IS NULL OR EXISTS (
                  SELECT 1
                  FROM public."WorkOrders" work_order
                  WHERE work_order."Id" = target_work_order_id
                    AND work_order."PortfolioId" = target_portfolio_id
                    AND work_order."TenantId" = tenant_access."TenantId"
                    AND work_order."LeaseManagementId" = tenant_access."LeaseManagementId"))
            )) OR (allow_assigned_work AND target_work_order_id IS NOT NULL AND EXISTS (
              SELECT 1
              FROM public."WorkspaceMemberships" membership
              JOIN public."MembershipRoleAssignments" assignment
                ON assignment."WorkspaceMembershipId" = membership."Id"
               AND assignment."PortfolioId" = membership."PortfolioId"
              JOIN public."WorkOrderResponsibilities" responsibility
                ON responsibility."WorkspaceMembershipId" = membership."Id"
               AND responsibility."MembershipRoleAssignmentId" = assignment."Id"
               AND responsibility."PortfolioId" = target_portfolio_id
               AND responsibility."WorkOrderId" = target_work_order_id
              WHERE membership."AccessContextId" = NULLIF(
                      current_setting('app.current_access_context_id', true), '')::integer
                AND membership."PortfolioId" = target_portfolio_id
                AND membership."Status" = 'Active'
                AND membership."SuspendedAtUtc" IS NULL
                AND membership."RevokedAtUtc" IS NULL
                AND membership."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > CURRENT_TIMESTAMP)
                AND assignment."ScopeKind" = 'AssignedWorkOrders'
                AND assignment."Status" = 'Active'
                AND assignment."SuspendedAtUtc" IS NULL
                AND assignment."RevokedAtUtc" IS NULL
                AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                AND responsibility."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (responsibility."EffectiveToUtc" IS NULL
                     OR responsibility."EffectiveToUtc" > CURRENT_TIMESTAMP)
            ))
          END;
        $function$;

        ALTER FUNCTION rc_api_resource_scope_allows(integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean)
          OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_api_resource_scope_allows(integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean)
          FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_api_resource_scope_allows(integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean)
          TO rentalcommand_api, rentalcommand_engine;

        CREATE OR REPLACE FUNCTION rc_account_bootstrap_audit_allows(
          target_portfolio_id integer,
          target_attempt_id uuid,
          target_command_type text,
          target_command_idempotency_key text,
          target_mutation_ordinal bigint,
          target_user_id integer,
          target_entity_type text,
          target_entity_id integer,
          target_operation integer,
          target_actor_label text,
          target_change_reason text)
        RETURNS boolean
        LANGUAGE sql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
          SELECT session_user = 'rentalcommand_api'
             AND target_portfolio_id IS NOT NULL
             AND target_portfolio_id > 0
             AND target_attempt_id IS NOT NULL
             AND target_command_type = 'auth.account.bootstrap'
             AND target_command_idempotency_key ~ '^email:[0-9a-f]{64}$'
             AND target_mutation_ordinal = 1
             AND target_user_id IS NOT NULL
             AND target_user_id > 0
             AND target_entity_type = 'ApplicationUser'
             AND target_entity_id = target_user_id
             AND target_operation = 0
             AND target_actor_label = 'authentication:registration'
             AND target_change_reason = 'Canonical account and initial workspace created'
             AND EXISTS (
               SELECT 1
               FROM public."AtomicCommandReceipts" receipt
               JOIN public."WorkspaceAccessContexts" access_context
                 ON access_context."UserId" = target_user_id
                AND access_context."PortfolioId" = target_portfolio_id
               WHERE receipt."AttemptId" = target_attempt_id
                 AND receipt."CommandType" = target_command_type
                 AND receipt."IdempotencyKey" = target_command_idempotency_key
                 -- INSERT ... RETURNING also evaluates the SELECT policy. Limit that visibility
                 -- to the transaction that inserted this exact receipt; after commit, xmin can no
                 -- longer equal the caller's current transaction id.
                 AND receipt.xmin = pg_current_xact_id()::xid
             );
        $function$;

        ALTER FUNCTION rc_account_bootstrap_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text)
          OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_account_bootstrap_audit_allows(
          integer, uuid, text, text, bigint, integer, text, integer, integer, text, text)
          FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_account_bootstrap_audit_allows(
          integer, uuid, text, text, bigint, integer, text, integer, integer, text, text)
          TO rentalcommand_api, rentalcommand_engine;

        CREATE OR REPLACE FUNCTION rc_pre_auth_audit_allows(
          target_portfolio_id integer,
          target_attempt_id uuid,
          target_command_type text,
          target_command_idempotency_key text,
          target_mutation_ordinal bigint,
          target_user_id integer,
          target_entity_type text,
          target_entity_id integer,
          target_operation integer,
          target_actor_label text,
          target_change_reason text,
          target_new_values jsonb)
        RETURNS boolean
        LANGUAGE sql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
          SELECT session_user = 'rentalcommand_api'
             AND target_portfolio_id IS NOT NULL
             AND target_portfolio_id > 0
             AND target_attempt_id IS NOT NULL
             AND target_command_idempotency_key ~ '^operation:[0-9a-f]{32}$'
             AND target_mutation_ordinal = 1
             AND target_user_id IS NOT NULL
             AND target_user_id > 0
             AND target_entity_type = 'WorkspaceAccessContext'
             AND target_entity_id > 0
             AND target_operation = 1
             AND target_new_values IS NOT NULL
             AND target_new_values ->> 'AuditRootAccessContextId' = target_entity_id::text
             AND target_new_values ->> 'UserId' = target_user_id::text
             AND EXISTS (
               SELECT 1
               FROM public."AtomicCommandReceipts" receipt
               JOIN public."WorkspaceAccessContexts" access_context
                ON access_context."Id" = target_entity_id
                AND access_context."UserId" = target_user_id
                AND access_context."PortfolioId" = target_portfolio_id
                AND access_context."Status" = 'Active'
                AND access_context."SuspendedAtUtc" IS NULL
                AND access_context."RevokedAtUtc" IS NULL
               WHERE receipt."AttemptId" = target_attempt_id
                 AND receipt."CommandType" = target_command_type
                 AND receipt."IdempotencyKey" = target_command_idempotency_key
                 AND receipt.xmin = pg_current_xact_id()::xid
             )
             AND (
               (target_command_type = 'auth-session:start'
                AND target_actor_label = 'authentication:session'
                AND target_change_reason = 'Authentication session started'
                AND EXISTS (
                  SELECT 1
                  FROM public."AuthSessions" session
                  WHERE session."UserId" = target_user_id
                    AND session."ActiveAccessContextId" = target_entity_id
                    AND session."Id"::text = target_new_values ->> 'AuthSessionId'
                    AND session."Status" = 'Active'
                    AND session."RevokedAtUtc" IS NULL
                    AND session."ExpiresAtUtc" > CURRENT_TIMESTAMP
                    AND session.xmin = pg_current_xact_id()::xid
                ))
               OR
               (target_command_type = 'auth-context-selection:issue'
                AND target_actor_label = 'authentication:context-selection'
                AND target_change_reason = 'Login context selection challenge issued'
                AND EXISTS (
                  SELECT 1
                  FROM public."LoginContextSelectionChallenges" challenge
                  WHERE challenge."UserId" = target_user_id
                    AND challenge."Id"::text = target_new_values ->> 'ChallengeId'
                    AND challenge."ConsumedAtUtc" IS NULL
                    AND challenge.xmin = pg_current_xact_id()::xid
                ))
             );
        $function$;

        ALTER FUNCTION rc_pre_auth_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)
          OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_pre_auth_audit_allows(
          integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)
          FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_pre_auth_audit_allows(
          integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)
          TO rentalcommand_api, rentalcommand_engine;

        CREATE OR REPLACE FUNCTION rc_sandbox_graduation_allows(target_portfolio_id integer)
        RETURNS boolean
        LANGUAGE sql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
          SELECT session_user = 'rentalcommand_api'
             AND target_portfolio_id IS NOT NULL
             AND target_portfolio_id > 0
             AND public.rc_api_scope_allows(target_portfolio_id)
             AND EXISTS (
               SELECT 1
               FROM public."Portfolios" portfolio
               JOIN public."WorkspaceAccessContexts" access_context
                 ON access_context."Id" = NULLIF(current_setting('app.current_access_context_id', true), '')::integer
                AND access_context."PortfolioId" = portfolio."Id"
               JOIN public."WorkspaceMemberships" membership
                 ON membership."AccessContextId" = access_context."Id"
                AND membership."PortfolioId" = portfolio."Id"
               JOIN public."MembershipRoleAssignments" assignment
                 ON assignment."WorkspaceMembershipId" = membership."Id"
                AND assignment."PortfolioId" = portfolio."Id"
               JOIN public."RoleProfileCapabilities" role_capability
                 ON role_capability."RoleProfileId" = assignment."RoleProfileId"
               JOIN public."CapabilityDefinitions" capability
                 ON capability."Id" = role_capability."CapabilityDefinitionId"
               WHERE portfolio."Id" = target_portfolio_id
                 AND portfolio."IsSandbox"
                 AND portfolio."DeletedAt" IS NULL
                 AND membership."Status" = 'Active'
                 AND membership."SuspendedAtUtc" IS NULL
                 AND membership."RevokedAtUtc" IS NULL
                 AND membership."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                 AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > CURRENT_TIMESTAMP)
                 AND assignment."Status" = 'Active'
                 AND assignment."SuspendedAtUtc" IS NULL
                 AND assignment."RevokedAtUtc" IS NULL
                 AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                 AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                 AND capability."Key" = 'account.destructive-actions');
        $function$;

        ALTER FUNCTION rc_sandbox_graduation_allows(integer) OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_sandbox_graduation_allows(integer) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_sandbox_graduation_allows(integer)
          TO rentalcommand_api;

        CREATE OR REPLACE FUNCTION rc_access_context_is_effective(
          target_access_context_id integer,
          target_user_id integer,
          effective_at_utc timestamp with time zone)
        RETURNS boolean
        LANGUAGE sql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
          SELECT EXISTS (
            SELECT 1
            FROM public."WorkspaceAccessContexts" context
            WHERE context."Id" = target_access_context_id
              AND context."UserId" = target_user_id
              AND context."Status" = 'Active'
              AND context."SuspendedAtUtc" IS NULL
              AND context."RevokedAtUtc" IS NULL
              AND (EXISTS (
                    SELECT 1
                    FROM public."WorkspaceMemberships" membership
                    WHERE membership."AccessContextId" = context."Id"
                      AND membership."PortfolioId" = context."PortfolioId"
                      AND membership."Status" = 'Active'
                      AND membership."SuspendedAtUtc" IS NULL
                      AND membership."RevokedAtUtc" IS NULL
                      AND membership."EffectiveFromUtc" <= effective_at_utc
                      AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > effective_at_utc)
                      AND EXISTS (
                        SELECT 1 FROM public."MembershipRoleAssignments" assignment
                        WHERE assignment."WorkspaceMembershipId" = membership."Id"
                          AND assignment."PortfolioId" = membership."PortfolioId"
                          AND assignment."Status" = 'Active'
                          AND assignment."SuspendedAtUtc" IS NULL
                          AND assignment."RevokedAtUtc" IS NULL
                          AND assignment."EffectiveFromUtc" <= effective_at_utc
                          AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > effective_at_utc)))
                OR EXISTS (
                    SELECT 1 FROM public."OwnerUserAccesses" owner_access
                    WHERE owner_access."AccessContextId" = context."Id"
                      AND owner_access."ApplicationUserId" = context."UserId"
                      AND owner_access."PortfolioId" = context."PortfolioId"
                      AND owner_access."RevokedAtUtc" IS NULL
                      AND owner_access."EffectiveFromUtc" <= effective_at_utc
                      AND (owner_access."EffectiveToUtc" IS NULL OR owner_access."EffectiveToUtc" > effective_at_utc))
                OR EXISTS (
                    SELECT 1 FROM public."vw_effective_tenant_access" tenant_access
                    WHERE tenant_access."AccessContextId" = context."Id"
                      AND tenant_access."UserId" = context."UserId"
                      AND tenant_access."PortfolioId" = context."PortfolioId")));
        $function$;

        ALTER FUNCTION rc_access_context_is_effective(integer, integer, timestamp with time zone)
          OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_access_context_is_effective(integer, integer, timestamp with time zone) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_access_context_is_effective(integer, integer, timestamp with time zone)
          TO rentalcommand_api;

        CREATE OR REPLACE FUNCTION rc_list_effective_access_contexts(
          target_user_id integer,
          effective_at_utc timestamp with time zone)
        RETURNS TABLE (
          "AccessContextId" integer,
          "PortfolioId" integer,
          "WorkspaceName" text,
          "AccessRevision" bigint,
          "DefaultExperience" text,
          "TotalEffectiveContexts" integer)
        LANGUAGE sql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
          WITH effective_contexts AS (
            SELECT context."Id", context."PortfolioId", context."AccessRevision",
                   portfolio."Name",
                   membership."Id" AS "MembershipId",
                   membership."DefaultExperience"
            FROM public."WorkspaceAccessContexts" context
            JOIN public."Portfolios" portfolio
              ON portfolio."Id" = context."PortfolioId" AND portfolio."DeletedAt" IS NULL
            LEFT JOIN public."WorkspaceMemberships" membership
              ON membership."AccessContextId" = context."Id"
             AND membership."PortfolioId" = context."PortfolioId"
             AND membership."Status" = 'Active'
             AND membership."SuspendedAtUtc" IS NULL
             AND membership."RevokedAtUtc" IS NULL
             AND membership."EffectiveFromUtc" <= effective_at_utc
             AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > effective_at_utc)
            WHERE context."UserId" = target_user_id
              AND context."Status" = 'Active'
              AND context."SuspendedAtUtc" IS NULL
              AND context."RevokedAtUtc" IS NULL
              AND ((membership."Id" IS NOT NULL AND EXISTS (
                    SELECT 1 FROM public."MembershipRoleAssignments" assignment
                    WHERE assignment."WorkspaceMembershipId" = membership."Id"
                      AND assignment."PortfolioId" = membership."PortfolioId"
                      AND assignment."Status" = 'Active'
                      AND assignment."SuspendedAtUtc" IS NULL
                      AND assignment."RevokedAtUtc" IS NULL
                      AND assignment."EffectiveFromUtc" <= effective_at_utc
                      AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > effective_at_utc)))
                OR EXISTS (
                    SELECT 1 FROM public."OwnerUserAccesses" owner_access
                    WHERE owner_access."AccessContextId" = context."Id"
                      AND owner_access."ApplicationUserId" = context."UserId"
                      AND owner_access."PortfolioId" = context."PortfolioId"
                      AND owner_access."RevokedAtUtc" IS NULL
                      AND owner_access."EffectiveFromUtc" <= effective_at_utc
                      AND (owner_access."EffectiveToUtc" IS NULL OR owner_access."EffectiveToUtc" > effective_at_utc))
                OR EXISTS (
                    SELECT 1 FROM public."vw_effective_tenant_access" tenant_access
                    WHERE tenant_access."AccessContextId" = context."Id"
                      AND tenant_access."UserId" = context."UserId"
                      AND tenant_access."PortfolioId" = context."PortfolioId"))
          ), options AS (
            SELECT effective_context."Id" AS "AccessContextId",
                   effective_context."PortfolioId",
                   effective_context."Name"::text AS "WorkspaceName",
                   effective_context."AccessRevision",
                   COALESCE(
                     CASE WHEN effective_context."MembershipId" IS NOT NULL THEN
                       COALESCE(
                         (SELECT role_profile."DefaultExperience"
                          FROM public."MembershipRoleAssignments" assignment
                          JOIN public."RoleProfiles" role_profile ON role_profile."Id" = assignment."RoleProfileId"
                          WHERE assignment."WorkspaceMembershipId" = effective_context."MembershipId"
                            AND assignment."PortfolioId" = effective_context."PortfolioId"
                            AND assignment."Status" = 'Active'
                            AND assignment."SuspendedAtUtc" IS NULL
                            AND assignment."RevokedAtUtc" IS NULL
                            AND assignment."EffectiveFromUtc" <= effective_at_utc
                            AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > effective_at_utc)
                          ORDER BY CASE role_profile."DefaultExperience"
                            WHEN 'Management' THEN 1 WHEN 'Leasing' THEN 2
                            WHEN 'Maintenance' THEN 3 WHEN 'Owner' THEN 4 ELSE 5 END
                          LIMIT 1),
                         effective_context."DefaultExperience")
                     END,
                     CASE WHEN EXISTS (
                       SELECT 1 FROM public."OwnerUserAccesses" owner_access
                       WHERE owner_access."AccessContextId" = effective_context."Id"
                         AND owner_access."ApplicationUserId" = target_user_id
                         AND owner_access."PortfolioId" = effective_context."PortfolioId"
                         AND owner_access."RevokedAtUtc" IS NULL
                         AND owner_access."EffectiveFromUtc" <= effective_at_utc
                         AND (owner_access."EffectiveToUtc" IS NULL OR owner_access."EffectiveToUtc" > effective_at_utc))
                     THEN 'Owner' ELSE 'Tenant' END)::text AS "DefaultExperience"
            FROM effective_contexts effective_context)
          SELECT options.*, count(*) OVER ()::integer AS "TotalEffectiveContexts"
          FROM options
          ORDER BY options."WorkspaceName", options."AccessContextId";
        $function$;

        ALTER FUNCTION rc_list_effective_access_contexts(integer, timestamp with time zone)
          OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_list_effective_access_contexts(integer, timestamp with time zone) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_list_effective_access_contexts(integer, timestamp with time zone)
          TO rentalcommand_api;

        CREATE OR REPLACE FUNCTION rc_pre_auth_email_audit_allows(
          target_portfolio_id integer,
          target_attempt_id uuid,
          target_command_type text,
          target_command_idempotency_key text,
          target_mutation_ordinal bigint,
          target_user_id integer,
          target_entity_type text,
          target_entity_id integer,
          target_operation integer,
          target_actor_label text,
          target_change_reason text,
          target_new_values jsonb)
        RETURNS boolean
        LANGUAGE sql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
          SELECT session_user = 'rentalcommand_api'
             AND target_portfolio_id IS NOT NULL
             AND target_portfolio_id > 0
             AND target_attempt_id IS NOT NULL
             AND target_command_type IN (
               'auth.email.email-confirmation',
               'auth.email.password-reset')
             AND target_command_idempotency_key ~
               ('^' || target_user_id::text || ':[0-9a-f]{64}$')
             AND target_mutation_ordinal = 1
             AND target_user_id IS NOT NULL
             AND target_user_id > 0
             AND target_entity_type = 'ApplicationUser'
             AND target_entity_id = target_user_id
             AND target_operation = 1
             AND target_actor_label = 'authentication:email-outbox'
             AND target_change_reason = 'Transactional account email enqueued'
             AND target_new_values IS NOT NULL
             AND target_new_values ->> 'TargetUserId' = target_user_id::text
             AND target_command_type = 'auth.email.' || (target_new_values ->> 'EmailKind')
             AND target_new_values ->> 'DeliveryIdempotencyKey' =
               'auth:' || (target_new_values ->> 'EmailKind') || ':' || target_command_idempotency_key
             AND EXISTS (
               SELECT 1
               FROM public."AspNetUsers" user_row
               WHERE user_row."Id" = target_user_id)
             AND EXISTS (
               SELECT 1
               FROM (
                 SELECT option."AccessContextId", option."PortfolioId"
                 FROM public.rc_list_effective_access_contexts(
                   target_user_id, clock_timestamp()) option
                 ORDER BY option."AccessContextId"
                 LIMIT 1) root
               WHERE root."PortfolioId" = target_portfolio_id
                 AND root."AccessContextId" =
                   (target_new_values ->> 'AuditRootAccessContextId')::integer)
             AND EXISTS (
               SELECT 1
               FROM public."AtomicCommandReceipts" receipt
               WHERE receipt."AttemptId" = target_attempt_id
                 AND receipt."CommandType" = target_command_type
                 AND receipt."IdempotencyKey" = target_command_idempotency_key
                 AND receipt.xmin = pg_current_xact_id()::xid);
        $function$;

        ALTER FUNCTION rc_pre_auth_email_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)
          OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_pre_auth_email_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)
          FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_pre_auth_email_audit_allows(integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)
          TO rentalcommand_api;

        CREATE OR REPLACE FUNCTION rc_bootstrap_initial_workspace(
          target_user_id integer,
          portfolio_name text,
          management_company_name text,
          owner_name text,
          owner_email text,
          created_at_utc timestamp with time zone)
        RETURNS TABLE ("PortfolioId" integer, "AccessContextId" integer)
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
        DECLARE
          new_portfolio_id integer;
          new_owner_entity_id integer;
          new_access_context_id integer;
          new_membership_id integer;
        BEGIN
          IF session_user IS DISTINCT FROM 'rentalcommand_api' THEN
            RAISE EXCEPTION 'Initial workspace bootstrap is API-only' USING ERRCODE = '42501';
          END IF;
          IF target_user_id IS NULL OR target_user_id <= 0
             OR NOT EXISTS (SELECT 1 FROM public."AspNetUsers" user_row WHERE user_row."Id" = target_user_id) THEN
            RAISE EXCEPTION 'Initial workspace bootstrap requires a persisted user' USING ERRCODE = '23503';
          END IF;
          IF EXISTS (SELECT 1 FROM public."WorkspaceAccessContexts" context WHERE context."UserId" = target_user_id) THEN
            RAISE EXCEPTION 'User already belongs to a workspace' USING ERRCODE = '23505';
          END IF;

          INSERT INTO public."Portfolios"
            ("Name", "ManagementCompanyName", "TimeZone", "Status", "Currency", "Settings",
             "IsSandbox", "CreatedAt", "UpdatedAt")
          VALUES
            (portfolio_name, management_company_name, 'America/New_York', 1, 'USD',
             '{"onboarding":{"choice":"pending"}}', FALSE, created_at_utc, created_at_utc)
          RETURNING "Id" INTO new_portfolio_id;

          INSERT INTO public."OwnerEntities"
            ("PortfolioId", "OwnerEntityType", "Name", "Email", "IsPrimary", "CreatedAt", "UpdatedAt")
          VALUES
            (new_portfolio_id, 0, owner_name, NULLIF(owner_email, ''), TRUE, created_at_utc, created_at_utc)
          RETURNING "Id" INTO new_owner_entity_id;

          INSERT INTO public."WorkspaceAccessContexts"
            ("UserId", "PortfolioId", "Status", "AccessRevision", "LastAuthorizedExperience",
             "CreatedAtUtc", "UpdatedAtUtc")
          VALUES
            (target_user_id, new_portfolio_id, 'Active', 1, 'Management', created_at_utc, created_at_utc)
          RETURNING "Id" INTO new_access_context_id;

          INSERT INTO public."WorkspaceMemberships"
            ("AccessContextId", "PortfolioId", "Status", "DefaultExperience", "EffectiveFromUtc",
             "CreatedAtUtc", "UpdatedAtUtc")
          VALUES
            (new_access_context_id, new_portfolio_id, 'Active', 'Management', created_at_utc,
             created_at_utc, created_at_utc)
          RETURNING "Id" INTO new_membership_id;

          INSERT INTO public."MembershipRoleAssignments"
            ("WorkspaceMembershipId", "PortfolioId", "RoleProfileId", "Status", "ScopeKind",
             "EffectiveFromUtc", "CreatedAtUtc", "UpdatedAtUtc")
          VALUES
            (new_membership_id, new_portfolio_id, 1, 'Active', 'AllProperties', created_at_utc,
             created_at_utc, created_at_utc);

          INSERT INTO public."OwnerUserAccesses"
            ("PortfolioId", "AccessContextId", "ApplicationUserId", "OwnerEntityId", "EffectiveFromUtc",
             "GrantedAtUtc", "GrantedByUserId", "Reason")
          VALUES
            (new_portfolio_id, new_access_context_id, target_user_id, new_owner_entity_id, created_at_utc,
             created_at_utc, target_user_id, 'Initial workspace owner relationship');

          INSERT INTO public."AutomationSettings"
            ("PortfolioId", "EnableRentCharges", "RentChargeLeadDays", "EnableLateFees", "LateFeeGraceDays",
             "EnableLeaseExpiryReminders", "LeaseExpiryReminderDays", "EnableRecurringMaintenance",
             "EnableMorningBriefing", "MorningBriefingSendHourLocal", "MorningBriefingIncludeEmpty",
             "CreatedAtUtc", "UpdatedAtUtc")
          VALUES
            (new_portfolio_id, FALSE, 5, FALSE, 5, TRUE, 60, TRUE, TRUE, 8, FALSE,
             created_at_utc, created_at_utc);

          INSERT INTO public."UserAlertPreferences"
            ("PortfolioId", "UserId", "EnableInApp", "EnableMobilePush", "EnableEmail", "EnableSms",
             "CreatedAtUtc", "UpdatedAtUtc")
          VALUES
            (new_portfolio_id, target_user_id, TRUE, TRUE, TRUE, FALSE, created_at_utc, created_at_utc);

          INSERT INTO public."TeamRoutingRules"
            ("PortfolioId", "Topic", "UseWorkspaceAdministratorFallback", "CreatedAtUtc", "UpdatedAtUtc")
          SELECT new_portfolio_id, topic, TRUE, created_at_utc, created_at_utc
          FROM unnest(ARRAY[
            'RentAndMoney',
            'ApplicationsAndLeasing',
            'WorkOrders',
            'OwnerStatementsAndDecisions',
            'AccountAndSecurity',
            'MorningBriefing'
          ]) AS topic;

          INSERT INTO public."WorkspaceNoticeTemplateVersions"
            ("PortfolioId", "SystemKey", "Version", "BasedOnSystemTemplateVersionId", "IsCustomized",
             "Subject", "Body", "JurisdictionCode", "CreatedByUserId", "CreatedAtUtc")
          SELECT new_portfolio_id, system."SystemKey", system."Version", system."Id", FALSE,
                 system."Subject", system."Body", system."JurisdictionCode", target_user_id, created_at_utc
          FROM public."SystemNoticeTemplateVersions" system
          WHERE system."Version" = 1;

          INSERT INTO public."TenantNoticePolicies"
            ("PortfolioId", "AutomationKey", "Mode", "Classification", "LeadDays", "SendHourLocal",
             "SendTenantPortal", "SendMobilePush", "SendEmail", "SendSms", "IncludePrimaryTenant",
             "IncludeCoTenant", "IncludeEligibleGuarantor", "IncludeOccupant", "FailureBehavior",
             "WorkspaceNoticeTemplateVersionId", "CreatedAtUtc", "UpdatedAtUtc")
          SELECT workspace."PortfolioId", workspace."SystemKey", 'Draft', system."Classification",
                 CASE WHEN workspace."SystemKey" IN
                   ('lease-renewal-offer', 'month-to-month-offer', 'lease-non-renewal') THEN 60 ELSE 5 END,
                 9, TRUE, FALSE, TRUE, FALSE, TRUE, TRUE, FALSE, FALSE, 'StopAndRequireReview',
                 workspace."Id", created_at_utc, created_at_utc
          FROM public."WorkspaceNoticeTemplateVersions" workspace
          JOIN public."SystemNoticeTemplateVersions" system
            ON system."Id" = workspace."BasedOnSystemTemplateVersionId"
          WHERE workspace."PortfolioId" = new_portfolio_id;

          RETURN QUERY SELECT new_portfolio_id, new_access_context_id;
        END;
        $function$;

        ALTER FUNCTION rc_bootstrap_initial_workspace(integer, text, text, text, text, timestamp with time zone)
          OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_bootstrap_initial_workspace(integer, text, text, text, text, timestamp with time zone)
          FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_bootstrap_initial_workspace(integer, text, text, text, text, timestamp with time zone)
          TO rentalcommand_api;
        """;

    private const string DropRlsAuthorityFunctions = """
        DROP FUNCTION IF EXISTS rc_bootstrap_initial_workspace(integer, text, text, text, text, timestamp with time zone);
        DROP FUNCTION IF EXISTS rc_pre_auth_email_audit_allows(
          integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb);
        DROP FUNCTION IF EXISTS rc_list_effective_access_contexts(integer, timestamp with time zone);
        DROP FUNCTION IF EXISTS rc_access_context_is_effective(integer, integer, timestamp with time zone);
        DROP FUNCTION IF EXISTS rc_sandbox_graduation_allows(integer);
        DROP FUNCTION IF EXISTS rc_pre_auth_audit_allows(
          integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb);
        DROP FUNCTION IF EXISTS rc_account_bootstrap_audit_allows(
          integer, uuid, text, text, bigint, integer, text, integer, integer, text, text);
        DROP FUNCTION IF EXISTS rc_api_resource_scope_allows(
          integer, integer, integer, integer, integer, integer, integer, boolean, boolean, boolean);
        DROP FUNCTION IF EXISTS rc_api_scope_allows(integer);
        """;

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
          USING ({predicate})
          WITH CHECK ({predicate});
        """;

    private static IEnumerable<string> BuildResourcePoliciesSql()
    {
        var teamProperty = ResourcePredicate("\"PortfolioId\"", "\"Id\"", "NULL", "NULL", "NULL", "NULL", "NULL");
        var propertyRead = ResourcePredicate(
            "\"PortfolioId\"", "\"Id\"", "NULL", "NULL", "NULL", "NULL", "NULL",
            allowOwner: true, allowTenant: true);
        yield return CreateResourcePolicySql("Properties", propertyRead, teamProperty);

        var unitTeam = ResourcePredicate("\"PortfolioId\"", "\"PropertyId\"", "\"Id\"", "NULL", "NULL", "NULL", "NULL");
        var unitRead = ResourcePredicate(
            "\"PortfolioId\"", "\"PropertyId\"", "\"Id\"", "NULL", "NULL", "NULL", "NULL",
            allowOwner: true, allowTenant: true);
        yield return CreateResourcePolicySql("Units", unitRead, unitTeam);

        var relationshipTeam = ResourcePredicate(
            "\"PortfolioId\"", "\"PropertyId\"", "\"UnitId\"", "NULL", "\"Id\"", "NULL", "NULL");
        var relationshipRead = ResourcePredicate(
            "\"PortfolioId\"", "\"PropertyId\"", "\"UnitId\"", "NULL", "\"Id\"", "NULL", "NULL",
            allowOwner: true, allowTenant: true);
        yield return CreateResourcePolicySql("LeaseManagements", relationshipRead, relationshipTeam);

        var agreementTeam = ResourcePredicate(
            "\"PortfolioId\"", "NULL", "NULL", "NULL", "\"LeaseManagementId\"", "NULL", "NULL");
        var agreementRead = ResourcePredicate(
            "\"PortfolioId\"", "NULL", "NULL", "NULL", "\"LeaseManagementId\"", "NULL", "NULL",
            allowOwner: true, allowTenant: true);
        yield return CreateResourcePolicySql("LeaseAgreements", agreementRead, agreementTeam);
        foreach (var table in new[]
                 {
                     "LeaseAddenda", "LeaseManagementParties", "LeaseRenewalAddendumDecisions",
                 })
        {
            yield return CreateResourcePolicySql(table, agreementRead, agreementTeam);
        }

        var accountTeam = ResourcePredicate(
            "\"PortfolioId\"", "NULL", "NULL", "NULL", "\"LeaseManagementId\"", "\"Id\"", "NULL");
        var accountRead = ResourcePredicate(
            "\"PortfolioId\"", "NULL", "NULL", "NULL", "\"LeaseManagementId\"", "\"Id\"", "NULL",
            allowOwner: true, allowTenant: true);
        yield return CreateResourcePolicySql("TenantAccounts", accountRead, accountTeam);
        var securityDepositRead = ResourcePredicate(
            "\"PortfolioId\"", "NULL", "NULL", "NULL", "NULL", "\"TenantAccountId\"", "NULL",
            allowOwner: true, allowTenant: true);
        var securityDepositWrite = ResourcePredicate(
            "\"PortfolioId\"", "NULL", "NULL", "NULL", "NULL", "\"TenantAccountId\"", "NULL");
        yield return CreateResourcePolicySql("SecurityDepositAccounts", securityDepositRead, securityDepositWrite);

        foreach (var (table, ownerMayRead) in new (string Table, bool OwnerMayRead)[]
                 {
                     ("TenantAccountConditionPeriods", true),
                     ("TenantAutopayEnrollments", false),
                     ("TenantLedgerAllocations", true),
                     ("TenantLedgerEntries", true),
                     ("TenantPaymentAttempts", false),
                 })
        {
            var tenantAccountRead = ResourcePredicate(
                "\"PortfolioId\"", "NULL", "NULL", "NULL", "NULL", "\"TenantAccountId\"", "NULL",
                allowOwner: ownerMayRead, allowTenant: true);
            var tenantAccountWrite = ResourcePredicate(
                "\"PortfolioId\"", "NULL", "NULL", "NULL", "NULL", "\"TenantAccountId\"", "NULL",
                allowTenant: true);
            // Tenant-account commands may append or update their own payment/autopay facts. The
            // account relationship is the write boundary; unrelated same-workspace accounts remain
            // invisible and fail WITH CHECK.
            yield return CreateResourcePolicySql(table, tenantAccountRead, tenantAccountWrite);
        }

        var workOrderTeam = ResourcePredicate(
            "\"PortfolioId\"", "\"PropertyId\"", "\"UnitId\"", "\"Id\"",
            "\"LeaseManagementId\"", "NULL", "\"TenantId\"", allowAssignedWork: true);
        var workOrderRead = ResourcePredicate(
            "\"PortfolioId\"", "\"PropertyId\"", "\"UnitId\"", "\"Id\"",
            "\"LeaseManagementId\"", "NULL", "\"TenantId\"",
            allowOwner: true, allowTenant: true, allowAssignedWork: true);
        var workOrderInsert = ResourcePredicate(
            "\"PortfolioId\"", "\"PropertyId\"", "\"UnitId\"", "NULL",
            "\"LeaseManagementId\"", "NULL", "\"TenantId\"", allowTenant: true);
        yield return CreateResourcePolicySql("WorkOrders", workOrderRead, workOrderTeam, workOrderInsert);

        var workTimelineRead = ResourcePredicate(
            "\"PortfolioId\"", "NULL", "NULL", "\"WorkOrderId\"", "NULL", "NULL", "NULL",
            allowOwner: true, allowTenant: true, allowAssignedWork: true);
        var workChildAssignedWrite = ResourcePredicate(
            "\"PortfolioId\"", "NULL", "NULL", "\"WorkOrderId\"", "NULL", "NULL", "NULL",
            allowAssignedWork: true);
        var workTimelineInsert = ResourcePredicate(
            "\"PortfolioId\"", "NULL", "NULL", "\"WorkOrderId\"", "NULL", "NULL", "NULL",
            allowTenant: true, allowAssignedWork: true);
        yield return CreateResourcePolicySql(
            "WorkOrderStatusEvents", workTimelineRead, workChildAssignedWrite, workTimelineInsert);
        yield return CreateResourcePolicySql("TechnicianWorkEntries", workChildAssignedWrite, workChildAssignedWrite);
        yield return CreateResourcePolicySql("WorkOrderResponsibilities", workChildAssignedWrite,
            ResourcePredicate(
                "\"PortfolioId\"", "\"PropertyId\"", "NULL", "\"WorkOrderId\"",
                "NULL", "NULL", "NULL"));
        yield return CreateResourcePolicySql("VendorDispatches",
            ResourcePredicate(
                "\"PortfolioId\"", "NULL", "NULL", "\"WorkOrderId\"",
                "NULL", "NULL", "NULL"),
            ResourcePredicate(
                "\"PortfolioId\"", "NULL", "NULL", "\"WorkOrderId\"",
                "NULL", "NULL", "NULL"));

        var conversationRead = ResourcePredicate(
            "\"PortfolioId\"", "\"PropertyId\"", "NULL", "\"WorkOrderId\"",
            "NULL", "NULL", "\"TenantId\"", allowTenant: true, allowAssignedWork: true);
        var conversationWrite = ResourcePredicate(
            "\"PortfolioId\"", "\"PropertyId\"", "NULL", "\"WorkOrderId\"",
            "NULL", "NULL", "\"TenantId\"", allowTenant: true, allowAssignedWork: true);
        yield return CreateResourcePolicySql("Conversations", conversationRead, conversationWrite);
    }

    private static string ResourcePredicate(
        string portfolioId,
        string propertyId,
        string unitId,
        string workOrderId,
        string leaseManagementId,
        string tenantAccountId,
        string tenantId,
        bool allowOwner = false,
        bool allowTenant = false,
        bool allowAssignedWork = false) =>
        $"rc_api_resource_scope_allows({portfolioId}, {propertyId}, {unitId}, {workOrderId}, " +
        $"{leaseManagementId}, {tenantAccountId}, {tenantId}, " +
        $"{allowOwner.ToString().ToUpperInvariant()}, {allowTenant.ToString().ToUpperInvariant()}, " +
        $"{allowAssignedWork.ToString().ToUpperInvariant()})";

    private static string CreateResourcePolicySql(
        string table,
        string readPredicate,
        string writePredicate,
        string? insertPredicate = null) => $"""
        ALTER TABLE {Quote(table)} ENABLE ROW LEVEL SECURITY;
        ALTER TABLE {Quote(table)} FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_select ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_insert ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_update ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_delete ON {Quote(table)};
        CREATE POLICY tenant_select ON {Quote(table)} FOR SELECT USING ({readPredicate});
        CREATE POLICY tenant_insert ON {Quote(table)} FOR INSERT WITH CHECK ({insertPredicate ?? writePredicate});
        CREATE POLICY tenant_update ON {Quote(table)} FOR UPDATE USING ({writePredicate}) WITH CHECK ({writePredicate});
        CREATE POLICY tenant_delete ON {Quote(table)} FOR DELETE USING ({DirectSandboxGraduationPredicate});
        """;

    private static string CreateSandboxGraduationDeletePolicySql(
        string table,
        string ordinaryPredicate,
        string sandboxDeletePredicate) => $"""
        ALTER TABLE {Quote(table)} ENABLE ROW LEVEL SECURITY;
        ALTER TABLE {Quote(table)} FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_select ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_insert ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_update ON {Quote(table)};
        DROP POLICY IF EXISTS tenant_delete ON {Quote(table)};
        CREATE POLICY tenant_select ON {Quote(table)} FOR SELECT USING ({ordinaryPredicate});
        CREATE POLICY tenant_insert ON {Quote(table)} FOR INSERT WITH CHECK ({ordinaryPredicate});
        CREATE POLICY tenant_update ON {Quote(table)} FOR UPDATE USING ({ordinaryPredicate}) WITH CHECK ({ordinaryPredicate});
        CREATE POLICY tenant_delete ON {Quote(table)} FOR DELETE USING
          ({sandboxDeletePredicate});
        """;

    private static string CreateAtomicAuditPolicySql() => $"""
        ALTER TABLE "AtomicAuditLogs" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "AtomicAuditLogs" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "AtomicAuditLogs";
        DROP POLICY IF EXISTS tenant_select ON "AtomicAuditLogs";
        DROP POLICY IF EXISTS tenant_insert ON "AtomicAuditLogs";
        DROP POLICY IF EXISTS tenant_update ON "AtomicAuditLogs";
        DROP POLICY IF EXISTS tenant_delete ON "AtomicAuditLogs";
        CREATE POLICY tenant_select ON "AtomicAuditLogs" FOR SELECT USING
          ({PortfolioPredicate} OR rc_account_bootstrap_audit_allows(
            "PortfolioId", "AttemptId", "CommandType", "CommandIdempotencyKey", "MutationOrdinal",
            "UserId", "EntityType", "EntityId", "Operation", "ActorLabel", "ChangeReason")
           OR rc_pre_auth_audit_allows(
            "PortfolioId", "AttemptId", "CommandType", "CommandIdempotencyKey", "MutationOrdinal",
            "UserId", "EntityType", "EntityId", "Operation", "ActorLabel", "ChangeReason", "NewValues")
           OR rc_pre_auth_email_audit_allows(
            "PortfolioId", "AttemptId", "CommandType", "CommandIdempotencyKey", "MutationOrdinal",
            "UserId", "EntityType", "EntityId", "Operation", "ActorLabel", "ChangeReason", "NewValues"));
        CREATE POLICY tenant_insert ON "AtomicAuditLogs" FOR INSERT WITH CHECK
          ({PortfolioPredicate} OR rc_account_bootstrap_audit_allows(
            "PortfolioId", "AttemptId", "CommandType", "CommandIdempotencyKey", "MutationOrdinal",
            "UserId", "EntityType", "EntityId", "Operation", "ActorLabel", "ChangeReason")
           OR rc_pre_auth_audit_allows(
            "PortfolioId", "AttemptId", "CommandType", "CommandIdempotencyKey", "MutationOrdinal",
            "UserId", "EntityType", "EntityId", "Operation", "ActorLabel", "ChangeReason", "NewValues")
           OR rc_pre_auth_email_audit_allows(
            "PortfolioId", "AttemptId", "CommandType", "CommandIdempotencyKey", "MutationOrdinal",
            "UserId", "EntityType", "EntityId", "Operation", "ActorLabel", "ChangeReason", "NewValues"));
        CREATE POLICY tenant_update ON "AtomicAuditLogs" FOR UPDATE USING ({PortfolioPredicate})
          WITH CHECK ({PortfolioPredicate});
        CREATE POLICY tenant_delete ON "AtomicAuditLogs" FOR DELETE USING
          ({DirectSandboxGraduationPredicate});
        """;

    private static bool RequiresSandboxGraduationDelete(string table) =>
        SandboxGraduationDeleteTables.Contains(table) && !ApiDeleteTables.Contains(table);

    internal const string CreateSandboxGraduationGlobalDeleteGuards = """
        CREATE OR REPLACE FUNCTION rc_require_sandbox_graduation_delete()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $function$
        DECLARE
          target_portfolio_id integer;
          row_portfolio_id integer;
        BEGIN
          IF session_user IS DISTINCT FROM 'rentalcommand_api' THEN
            RAISE EXCEPTION '% may be deleted only during sandbox graduation', TG_TABLE_NAME
              USING ERRCODE = '42501';
          END IF;

          target_portfolio_id := NULLIF(current_setting('app.current_portfolio_id', true), '')::integer;
          IF target_portfolio_id IS NULL OR target_portfolio_id <= 0 THEN
            RAISE EXCEPTION 'Sandbox graduation requires an explicit portfolio scope'
              USING ERRCODE = '42501';
          END IF;

          IF NOT rc_sandbox_graduation_allows(target_portfolio_id) THEN
            RAISE EXCEPTION 'Caller is not authorized to graduate sandbox portfolio %', target_portfolio_id
              USING ERRCODE = '42501';
          END IF;

          IF TG_TABLE_NAME = 'AtomicCommandReceipts' THEN
            IF NOT EXISTS (
              SELECT 1
              FROM "AtomicAuditLogs" audit
              WHERE audit."AttemptId" = (to_jsonb(OLD) ->> 'AttemptId')::uuid
                AND audit."PortfolioId" = target_portfolio_id
            ) THEN
              RAISE EXCEPTION 'AtomicCommandReceipts row is outside sandbox graduation portfolio %', target_portfolio_id
                USING ERRCODE = '42501';
            END IF;
          ELSE
            row_portfolio_id := NULLIF(to_jsonb(OLD) ->> 'PortfolioId', '')::integer;
            IF row_portfolio_id IS DISTINCT FROM target_portfolio_id THEN
              RAISE EXCEPTION '% row is outside sandbox graduation portfolio %', TG_TABLE_NAME, target_portfolio_id
                USING ERRCODE = '42501';
            END IF;
          END IF;

          RETURN OLD;
        END;
        $function$;

        CREATE TRIGGER trg_atomic_command_receipt_sandbox_delete
          BEFORE DELETE ON "AtomicCommandReceipts"
          FOR EACH ROW EXECUTE FUNCTION rc_require_sandbox_graduation_delete();

        CREATE TRIGGER trg_outbox_message_sandbox_delete
          BEFORE DELETE ON "OutboxMessages"
          FOR EACH ROW EXECUTE FUNCTION rc_require_sandbox_graduation_delete();

        CREATE TRIGGER trg_provider_inbox_event_sandbox_delete
          BEFORE DELETE ON "ProviderInboxEvents"
          FOR EACH ROW EXECUTE FUNCTION rc_require_sandbox_graduation_delete();
        """;

    internal const string DropSandboxGraduationGlobalDeleteGuards = """
        DROP TRIGGER IF EXISTS trg_provider_inbox_event_sandbox_delete ON "ProviderInboxEvents";
        DROP TRIGGER IF EXISTS trg_outbox_message_sandbox_delete ON "OutboxMessages";
        DROP TRIGGER IF EXISTS trg_atomic_command_receipt_sandbox_delete ON "AtomicCommandReceipts";
        DROP FUNCTION IF EXISTS rc_require_sandbox_graduation_delete();
        """;

    private const string PortfolioPredicate = "rc_api_scope_allows(\"PortfolioId\")";

    private const string DirectSandboxGraduationPredicate =
        "rc_sandbox_graduation_allows(\"PortfolioId\")";

    private const string NullablePortfolioReadPredicate =
        "(\"PortfolioId\" IS NULL OR rc_api_scope_allows(\"PortfolioId\"))";

    private const string NullablePortfolioWritePredicate =
        "(\"PortfolioId\" IS NOT NULL AND rc_api_scope_allows(\"PortfolioId\"))";

    private const string PortfolioSelfPredicate = "rc_api_scope_allows(\"Id\")";

    private static string ChildPredicate(ChildPolicy policy) =>
        "EXISTS (SELECT 1 FROM " +
        $"{Quote(policy.ParentTable)} AS parent WHERE parent.\"Id\" = " +
        $"{Quote(policy.Table)}.{Quote(policy.ForeignKey)} " +
        "AND rc_api_scope_allows(parent.\"PortfolioId\"))";

    private static string ChildSandboxGraduationPredicate(ChildPolicy policy) =>
        "EXISTS (SELECT 1 FROM " +
        $"{Quote(policy.ParentTable)} AS parent WHERE parent.\"Id\" = " +
        $"{Quote(policy.Table)}.{Quote(policy.ForeignKey)} " +
        "AND rc_sandbox_graduation_allows(parent.\"PortfolioId\"))";

    private static string BuildInspectionTemplatePoliciesSql() => $"""
        ALTER TABLE "InspectionTemplates" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE "InspectionTemplates" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS tenant_isolation ON "InspectionTemplates";
        DROP POLICY IF EXISTS tenant_select ON "InspectionTemplates";
        DROP POLICY IF EXISTS tenant_insert ON "InspectionTemplates";
        DROP POLICY IF EXISTS tenant_update ON "InspectionTemplates";
        DROP POLICY IF EXISTS tenant_delete ON "InspectionTemplates";
        CREATE POLICY tenant_select ON "InspectionTemplates" FOR SELECT USING ({NullablePortfolioReadPredicate});
        CREATE POLICY tenant_insert ON "InspectionTemplates" FOR INSERT WITH CHECK ({NullablePortfolioWritePredicate});
        CREATE POLICY tenant_update ON "InspectionTemplates" FOR UPDATE USING ({NullablePortfolioWritePredicate}) WITH CHECK ({NullablePortfolioWritePredicate});
        CREATE POLICY tenant_delete ON "InspectionTemplates" FOR DELETE USING ({NullablePortfolioWritePredicate});
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
          (EXISTS
            (SELECT 1 FROM "InspectionTemplates" parent
             WHERE parent."Id" = "InspectionTemplateItems"."TemplateId"
               AND (parent."PortfolioId" IS NULL OR rc_api_scope_allows(parent."PortfolioId"))));
        CREATE POLICY tenant_insert ON "InspectionTemplateItems" FOR INSERT WITH CHECK
          (EXISTS
            (SELECT 1 FROM "InspectionTemplates" parent WHERE parent."Id" = "InspectionTemplateItems"."TemplateId"
             AND parent."PortfolioId" IS NOT NULL AND rc_api_scope_allows(parent."PortfolioId")));
        CREATE POLICY tenant_update ON "InspectionTemplateItems" FOR UPDATE USING
          (EXISTS
            (SELECT 1 FROM "InspectionTemplates" parent WHERE parent."Id" = "InspectionTemplateItems"."TemplateId"
             AND parent."PortfolioId" IS NOT NULL AND rc_api_scope_allows(parent."PortfolioId")))
          WITH CHECK
          (EXISTS
            (SELECT 1 FROM "InspectionTemplates" parent WHERE parent."Id" = "InspectionTemplateItems"."TemplateId"
             AND parent."PortfolioId" IS NOT NULL AND rc_api_scope_allows(parent."PortfolioId")));
        CREATE POLICY tenant_delete ON "InspectionTemplateItems" FOR DELETE USING
          (EXISTS
            (SELECT 1 FROM "InspectionTemplates" parent WHERE parent."Id" = "InspectionTemplateItems"."TemplateId"
             AND parent."PortfolioId" IS NOT NULL AND rc_api_scope_allows(parent."PortfolioId")));
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

        // Sandbox graduation is a normal authenticated API command whose DELETE authority is
        // admitted row-by-row by rc_sandbox_graduation_allows. The login receives no alternate
        // role or bypass credential; outside that one canonical capability-checked sandbox scope,
        // FORCE RLS (or the global-table delete guards) still rejects every durable delete.
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
