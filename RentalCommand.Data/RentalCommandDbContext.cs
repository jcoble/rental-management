using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Data;

/// <summary>
/// Primary application + user-only Identity database context. Uses an <c>int</c> Identity key
/// so the auth user PK matches every domain entity. Workspace authorization is capability-based.
/// </summary>
public class RentalCommandDbContext : IdentityUserContext<ApplicationUser, int>
{
    public RentalCommandDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardImmutableNoticeVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardImmutableNoticeVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardImmutableNoticeVersions()
    {
        // ChangeTracker.Entries<T>() normally runs DetectChanges across every tracked entity.
        // Doing that before EF invokes SaveChanges interceptors can make an unrelated invalid
        // authority-key mutation throw EF's generic key error before the authorization interceptor
        // can reject it with the canonical access-boundary exception. Inspect only the immutable
        // version entries here and compare their snapshots without triggering global detection.
        var autoDetectChanges = ChangeTracker.AutoDetectChangesEnabled;
        try
        {
            ChangeTracker.AutoDetectChangesEnabled = false;
            var changedSystemVersion = HasImmutableVersionMutation<SystemNoticeTemplateVersion>();
            var changedWorkspaceVersion = HasImmutableVersionMutation<WorkspaceNoticeTemplateVersion>();
            if (changedSystemVersion || changedWorkspaceVersion)
                throw new InvalidOperationException("Notice template versions are immutable; create a successor version instead.");
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    private bool HasImmutableVersionMutation<TEntity>() where TEntity : class =>
        ChangeTracker.Entries<TEntity>().Any(entry =>
            entry.State == EntityState.Deleted ||
            entry.State == EntityState.Modified ||
            (entry.State == EntityState.Unchanged &&
             entry.Properties.Any(property => !Equals(property.OriginalValue, property.CurrentValue))));

    // Domain entities
    public DbSet<Portfolio> Portfolios => Set<Portfolio>();
    public DbSet<OwnerEntity> OwnerEntities => Set<OwnerEntity>();
    public DbSet<PropertyOwnership> PropertyOwnerships => Set<PropertyOwnership>();
    public DbSet<OwnerUserAccess> OwnerUserAccesses => Set<OwnerUserAccess>();
    public DbSet<OwnerDistribution> OwnerDistributions => Set<OwnerDistribution>();
    public DbSet<OwnerContribution> OwnerContributions => Set<OwnerContribution>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<RentalListing> RentalListings => Set<RentalListing>();
    public DbSet<ListingPhoto> ListingPhotos => Set<ListingPhoto>();
    public DbSet<ListingPublication> ListingPublications => Set<ListingPublication>();
    public DbSet<ExternalListingSignal> ExternalListingSignals => Set<ExternalListingSignal>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<LeaseManagement> LeaseManagements => Set<LeaseManagement>();
    public DbSet<LeaseManagementParty> LeaseManagementParties => Set<LeaseManagementParty>();
    public DbSet<TenantUserAccess> TenantUserAccesses => Set<TenantUserAccess>();
    public DbSet<UnitOperationalPeriod> UnitOperationalPeriods => Set<UnitOperationalPeriod>();
    public DbSet<LeaseAgreementStatusProjection> LeaseAgreementStatusProjections =>
        Set<LeaseAgreementStatusProjection>();
    public DbSet<UnitOccupancyProjection> UnitOccupancyProjections => Set<UnitOccupancyProjection>();
    public DbSet<LeaseManagementLifecycleProjection> LeaseManagementLifecycleProjections =>
        Set<LeaseManagementLifecycleProjection>();
    public DbSet<LeaseReconciliationExceptionProjection> LeaseReconciliationExceptionProjections =>
        Set<LeaseReconciliationExceptionProjection>();
    public DbSet<LeaseAddendumStatusProjection> LeaseAddendumStatusProjections =>
        Set<LeaseAddendumStatusProjection>();
    public DbSet<TenantAccountBalanceProjection> TenantAccountBalanceProjections =>
        Set<TenantAccountBalanceProjection>();
    public DbSet<SecurityDepositBalanceProjection> SecurityDepositBalanceProjections =>
        Set<SecurityDepositBalanceProjection>();
    public DbSet<TenantChargeBalanceProjection> TenantChargeBalanceProjections =>
        Set<TenantChargeBalanceProjection>();
    public DbSet<MorningBriefingCandidateProjection> MorningBriefingCandidateProjections =>
        Set<MorningBriefingCandidateProjection>();
    public DbSet<LegalDocumentArtifact> LegalDocumentArtifacts => Set<LegalDocumentArtifact>();
    public DbSet<LegalDocumentSourceVersion> LegalDocumentSourceVersions => Set<LegalDocumentSourceVersion>();
    public DbSet<LeaseAgreement> LeaseAgreements => Set<LeaseAgreement>();
    public DbSet<LeaseAgreementSigner> LeaseAgreementSigners => Set<LeaseAgreementSigner>();
    public DbSet<LeaseAddendum> LeaseAddenda => Set<LeaseAddendum>();
    public DbSet<LeaseAddendumSigner> LeaseAddendumSigners => Set<LeaseAddendumSigner>();
    public DbSet<LeaseAddendumFinancialEffect> LeaseAddendumFinancialEffects =>
        Set<LeaseAddendumFinancialEffect>();
    public DbSet<LeaseRenewalAddendumDecision> LeaseRenewalAddendumDecisions =>
        Set<LeaseRenewalAddendumDecision>();
    public DbSet<TenantAccount> TenantAccounts => Set<TenantAccount>();
    public DbSet<TenantAccountConditionPeriod> TenantAccountConditionPeriods =>
        Set<TenantAccountConditionPeriod>();
    public DbSet<TenantLedgerEntry> TenantLedgerEntries => Set<TenantLedgerEntry>();
    public DbSet<TenantLedgerAllocation> TenantLedgerAllocations => Set<TenantLedgerAllocation>();
    public DbSet<TenantPaymentAttempt> TenantPaymentAttempts => Set<TenantPaymentAttempt>();
    public DbSet<TenantAutopayEnrollment> TenantAutopayEnrollments => Set<TenantAutopayEnrollment>();
    public DbSet<SecurityDepositAccount> SecurityDepositAccounts => Set<SecurityDepositAccount>();
    public DbSet<SecurityDepositEntry> SecurityDepositEntries => Set<SecurityDepositEntry>();
    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();
    public DbSet<DocumentTemplateField> DocumentTemplateFields => Set<DocumentTemplateField>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpenseAllocation> ExpenseAllocations => Set<ExpenseAllocation>();
    public DbSet<ExpenseLineItem> ExpenseLineItems => Set<ExpenseLineItem>();
    public DbSet<CapitalAsset> CapitalAssets => Set<CapitalAsset>();
    public DbSet<PropertyDisposition> PropertyDispositions => Set<PropertyDisposition>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorDispatch> VendorDispatches => Set<VendorDispatch>();
    public DbSet<VendorRating> VendorRatings => Set<VendorRating>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderResponsibility> WorkOrderResponsibilities => Set<WorkOrderResponsibility>();
    public DbSet<TechnicianWorkEntry> TechnicianWorkEntries => Set<TechnicianWorkEntry>();
    public DbSet<WorkOrderStatusEvent> WorkOrderStatusEvents => Set<WorkOrderStatusEvent>();
    public DbSet<RecurringMaintenanceTask> RecurringMaintenanceTasks => Set<RecurringMaintenanceTask>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Inspection> Inspections => Set<Inspection>();
    public DbSet<InspectionItem> InspectionItems => Set<InspectionItem>();
    public DbSet<InspectionTemplate> InspectionTemplates => Set<InspectionTemplate>();
    public DbSet<InspectionTemplateItem> InspectionTemplateItems => Set<InspectionTemplateItem>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationReadState> NotificationReadStates => Set<NotificationReadState>();
    public DbSet<AutomationSettings> AutomationSettings => Set<AutomationSettings>();
    public DbSet<MessagingProviderSettings> MessagingProviderSettings => Set<MessagingProviderSettings>();
    public DbSet<WorkspaceLlmCredential> WorkspaceLlmCredentials => Set<WorkspaceLlmCredential>();
    public DbSet<LlmUsageEvidence> LlmUsageEvidence => Set<LlmUsageEvidence>();
    public DbSet<UserAlertPreference> UserAlertPreferences => Set<UserAlertPreference>();
    public DbSet<TeamRoutingRule> TeamRoutingRules => Set<TeamRoutingRule>();
    public DbSet<TeamRoutingRuleRecipient> TeamRoutingRuleRecipients => Set<TeamRoutingRuleRecipient>();
    public DbSet<TenantNoticePolicy> TenantNoticePolicies => Set<TenantNoticePolicy>();
    public DbSet<SystemNoticeTemplateVersion> SystemNoticeTemplateVersions => Set<SystemNoticeTemplateVersion>();
    public DbSet<WorkspaceNoticeTemplateVersion> WorkspaceNoticeTemplateVersions => Set<WorkspaceNoticeTemplateVersion>();
    public DbSet<RenderedNotice> RenderedNotices => Set<RenderedNotice>();
    public DbSet<NoticeDeliveryEvidence> NoticeDeliveryEvidence => Set<NoticeDeliveryEvidence>();
    public DbSet<TenantNoticeWorkItem> TenantNoticeWorkItems => Set<TenantNoticeWorkItem>();
    public DbSet<BankConnection> BankConnections => Set<BankConnection>();
    public DbSet<BankStatement> BankStatements => Set<BankStatement>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<PlaidTokenExchangeAttempt> PlaidTokenExchangeAttempts => Set<PlaidTokenExchangeAttempt>();
    public DbSet<NoticeDraft> NoticeDrafts => Set<NoticeDraft>();
    public DbSet<RentalApplication> RentalApplications => Set<RentalApplication>();
    public DbSet<ApplicationFinancialAccount> ApplicationFinancialAccounts => Set<ApplicationFinancialAccount>();
    public DbSet<ApplicationFinancialEntry> ApplicationFinancialEntries => Set<ApplicationFinancialEntry>();
    public DbSet<ApplicantScreening> ApplicantScreenings => Set<ApplicantScreening>();
    public DbSet<ApplicantScreeningMilestone> ApplicantScreeningMilestones => Set<ApplicantScreeningMilestone>();
    public DbSet<AdverseActionNotice> AdverseActionNotices => Set<AdverseActionNotice>();

    // Native e-signature (envelope + per-signer tokens + append-only audit trail)
    public DbSet<SignatureRequest> SignatureRequests => Set<SignatureRequest>();
    public DbSet<SignatureSigner> SignatureSigners => Set<SignatureSigner>();
    public DbSet<SignatureAuditEvent> SignatureAuditEvents => Set<SignatureAuditEvent>();

    // Auth + audit + infrastructure entities (Task 3)
    public DbSet<AtomicCommandReceipt> AtomicCommandReceipts => Set<AtomicCommandReceipt>();
    public DbSet<AtomicAuditLog> AtomicAuditLogs => Set<AtomicAuditLog>();
    public DbSet<WorkspaceAccessContext> WorkspaceAccessContexts => Set<WorkspaceAccessContext>();
    public DbSet<EffectiveOwnerAccessProjection> EffectiveOwnerAccess => Set<EffectiveOwnerAccessProjection>();
    public DbSet<EffectiveTenantAccessProjection> EffectiveTenantAccess => Set<EffectiveTenantAccessProjection>();
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();
    public DbSet<WorkspaceInvitation> WorkspaceInvitations => Set<WorkspaceInvitation>();
    public DbSet<RoleProfile> RoleProfiles => Set<RoleProfile>();
    public DbSet<CapabilityDefinition> CapabilityDefinitions => Set<CapabilityDefinition>();
    public DbSet<RoleProfileCapability> RoleProfileCapabilities => Set<RoleProfileCapability>();
    public DbSet<MembershipRoleAssignment> MembershipRoleAssignments => Set<MembershipRoleAssignment>();
    public DbSet<MembershipRoleAssignmentProperty> MembershipRoleAssignmentProperties =>
        Set<MembershipRoleAssignmentProperty>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<AuthSessionRefreshTokenFamily> AuthSessionRefreshTokenFamilies =>
        Set<AuthSessionRefreshTokenFamily>();
    public DbSet<AuthSessionRefreshCredential> AuthSessionRefreshCredentials =>
        Set<AuthSessionRefreshCredential>();
    public DbSet<LoginContextSelectionChallenge> LoginContextSelectionChallenges =>
        Set<LoginContextSelectionChallenge>();
    public DbSet<AccessEnvelopeProjectionRow> AccessEnvelopeProjectionRows =>
        Set<AccessEnvelopeProjectionRow>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<QueuedJob> QueuedJobs => Set<QueuedJob>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<PendingFileUpload> PendingFileUploads => Set<PendingFileUpload>();
    public DbSet<ScanDraft> ScanDrafts => Set<ScanDraft>();
    public DbSet<ScanBatch> ScanBatches => Set<ScanBatch>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<ProviderInboxEvent> ProviderInboxEvents => Set<ProviderInboxEvent>();

    // Mortgages / debt service + recurring costs (true cash-flow + year-end picture)
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<LoanPayment> LoanPayments => Set<LoanPayment>();
    public DbSet<LoanPaymentCorrection> LoanPaymentCorrections => Set<LoanPaymentCorrection>();
    public DbSet<RecurringExpense> RecurringExpenses => Set<RecurringExpense>();
    public DbSet<EvictionCase> EvictionCases => Set<EvictionCase>();
    public DbSet<EvictionCaseRespondent> EvictionCaseRespondents => Set<EvictionCaseRespondent>();
    public DbSet<EvictionCaseEvent> EvictionCaseEvents => Set<EvictionCaseEvent>();

    // Engine resilience — worker heartbeats written by each background worker every poll cycle
    public DbSet<EngineWorkerHeartbeat> EngineWorkerHeartbeats => Set<EngineWorkerHeartbeat>();

    // --- Accounting-integration backbone (provider-agnostic; QuickBooks is provider #1) ---
    public DbSet<AccountingConnection> AccountingConnections => Set<AccountingConnection>();
    public DbSet<OAuthState> OAuthStates => Set<OAuthState>();
    public DbSet<AccountingEntityMapping> AccountingEntityMappings => Set<AccountingEntityMapping>();
    public DbSet<AccountingMappingPromotionJob> AccountingMappingPromotionJobs => Set<AccountingMappingPromotionJob>();
    public DbSet<AccountingSyncMap> AccountingSyncMaps => Set<AccountingSyncMap>();
    public DbSet<AccountingParkedTransaction> AccountingParkedTransactions => Set<AccountingParkedTransaction>();

    // Double-entry general-ledger foundation.
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<RecurringTenantCharge> RecurringTenantCharges => Set<RecurringTenantCharge>();
    public DbSet<AccountingConversionReconciliation> AccountingConversionReconciliations =>
        Set<AccountingConversionReconciliation>();

    /// <summary>Single-row (Id = 1) controllable simulation clock — non-prod only. Global (no RLS policy).</summary>
    public DbSet<SimulationClock> SimulationClocks => Set<SimulationClock>();

    /// <summary>Dev-only API→Engine command queue for on-demand scheduled-job runs. Global (no RLS policy).</summary>
    public DbSet<SimWorkerCommand> SimWorkerCommands => Set<SimWorkerCommand>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configures the ASP.NET Identity schema (AspNetUsers/Roles/etc.) with int keys.
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.ConfigureWorkspaceAccessKernel();
        modelBuilder.ConfigureWorkOrderResponsibilities();
        AccessAuthorityDbFunctions.Configure(modelBuilder);
        modelBuilder.ConfigureLeaseRelationshipKernel();
        modelBuilder.ConfigureLeaseLegalArtifacts();
        modelBuilder.ConfigureTenantAccountKernel();
        modelBuilder.ConfigureLeaseLifecycleProjections();
        modelBuilder.ConfigureMorningBriefingCandidateProjection();
        modelBuilder.ConfigureAccountStatusProjections();
        BusinessDateDbFunction.Configure(modelBuilder);
        ScheduleEDepreciationDbFunction.Configure(modelBuilder);
        SqlNumericFunctions.Configure(modelBuilder);
        modelBuilder.ConfigureApplicationFinance();
        modelBuilder.ConfigureAccountingFoundation();
        modelBuilder.ConfigureSimulationInfrastructure();
        modelBuilder.ConfigureIdentityAudit();
        modelBuilder.ConfigureOwners();
        modelBuilder.ConfigureDocumentsAndScanning();
        modelBuilder.ConfigureNotificationsAndAutomation();
        modelBuilder.ConfigureBanking();
        modelBuilder.ConfigureAccountingIntegrations();
        modelBuilder.ConfigureLeasingApplications();
        modelBuilder.ConfigureEsign();
        modelBuilder.ConfigureAutomationJobs();
        modelBuilder.ConfigurePortfoliosAndProperties();
        modelBuilder.ConfigureTenants();
        modelBuilder.ConfigureExpensesAndAssets();
        modelBuilder.ConfigureEvictions();
        modelBuilder.ConfigureVendors();
        modelBuilder.ConfigureWorkOrders();
        modelBuilder.ConfigureOperations();
        modelBuilder.ConfigureConversations();
        modelBuilder.ConfigureProviderInfrastructure();
        modelBuilder.ConfigureSoftDeleteQueryFilters();
        // Close the required-relationship filter graph for the canonical lease/account/access,
        // listing, notification, signature, eviction, and application-finance aggregates. Keep
        // this last: several entities above already have soft-delete filters which this extension
        // deliberately composes with portfolio visibility rather than replacing.
        modelBuilder.ConfigurePortfolioVisibilityQueryFilters();
    }
}
