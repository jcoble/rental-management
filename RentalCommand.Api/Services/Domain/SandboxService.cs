using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ISandboxService"/>
public sealed class SandboxService : ISandboxService
{
    private readonly RentalCommandDbContext _db;
    private readonly ISelfOwnerProvisioner _selfOwnerProvisioner;
    private readonly Auth.DemoDataSeeder _demoSeeder;
    private readonly ILogger<SandboxService> _logger;
    private readonly TimeProvider _timeProvider;

    public SandboxService(
        RentalCommandDbContext db,
        ISelfOwnerProvisioner selfOwnerProvisioner,
        Auth.DemoDataSeeder demoSeeder,
        ILogger<SandboxService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _selfOwnerProvisioner = selfOwnerProvisioner;
        _demoSeeder = demoSeeder;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<SandboxStateResponse?> GetStateAsync(int portfolioId, CancellationToken ct = default)
    {
        var portfolio = await _db.Portfolios
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);

        return portfolio is null ? null : ToState(portfolio);
    }

    public async Task<SandboxStateResponse?> GoLiveAsync(int portfolioId, CancellationToken ct = default)
    {
        // Scope strictly to the caller's own portfolio (IDOR guard): we only ever load + mutate this id.
        var portfolio = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);

        if (portfolio is null)
        {
            return null;
        }

        // Idempotent: already Live → nothing to wipe, just return the current state.
        if (!portfolio.IsSandbox)
        {
            return ToState(portfolio);
        }

        // Transactional: the data wipe and the flag flip commit together. A failure rolls everything
        // back so we can never end up Live-but-still-holding-demo-data (or vice-versa).
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        await WipePortfolioDataAsync(portfolioId, ct);

        portfolio.IsSandbox = false;
        portfolio.SandboxSeededAtUtc = null;
        portfolio.UpdatedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        // The wipe removed the demo owners, so the fresh Live portfolio has none. Re-create the primary
        // self-owner from the landlord's own account so the getting-started "owner" step is satisfied
        // before they add a property (the wipe's SetNull FK already cleared the stale OwnerEntityId).
        // Best-effort: if no admin user resolves, skip rather than fail the graduation.
        await EnsureSelfOwnerAfterWipeAsync(portfolioId, ct);

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Portfolio {PortfolioId} graduated from Sandbox to Live — demo data wiped.", portfolioId);

        return ToState(portfolio);
    }

    public async Task<SandboxStateResponse?> ApplyOnboardingChoiceAsync(
        int portfolioId, OnboardingChoice choice, CancellationToken ct = default)
    {
        // Scope strictly to the caller's own portfolio (IDOR guard): we only ever load + mutate this id.
        var portfolio = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);

        if (portfolio is null)
        {
            return null;
        }

        // Idempotent: a decision was already recorded → do not re-seed or wipe; just return current state.
        // This makes the first-login gate safe against double-submits and repeat logins racing the redirect.
        if (!PortfolioOnboarding.IsPending(portfolio.Settings))
        {
            return ToState(portfolio);
        }

        var now = _timeProvider.UtcNow();

        if (choice == OnboardingChoice.Sandbox)
        {
            // Seed the demo dataset, then flip to a seeded Sandbox. The seed is fast and server-side; the
            // ~20s "Setting up your sandbox…" animation is client-side theater (no artificial server delay).
            // SeedPortfolioAsync is itself idempotent + atomic, so a retry can't double-seed.
            await _demoSeeder.SeedPortfolioAsync(portfolioId, ct);

            portfolio.IsSandbox = true;
            portfolio.SandboxSeededAtUtc = now;
        }
        else
        {
            // Live: an empty real portfolio. Registration already created it empty (no demo data) with the
            // self-owner provisioned, so there is nothing to seed or wipe — just stamp the decision below.
            portfolio.IsSandbox = false;
            portfolio.SandboxSeededAtUtc = null;
        }

        portfolio.Settings = PortfolioOnboarding.WriteChoice(portfolio.Settings, choice);
        portfolio.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Portfolio {PortfolioId} recorded first-login onboarding choice: {Choice}.", portfolioId, choice);

        return ToState(portfolio);
    }

    /// <summary>
    /// After a Go-Live wipe, recreates the portfolio's primary self-owner from its administering user.
    /// Resolves the owner-user as the earliest Admin-role-eligible account scoped to the portfolio; if no
    /// such user exists (e.g. a test fixture with no Identity users) it is a safe no-op.
    /// </summary>
    private async Task EnsureSelfOwnerAfterWipeAsync(int portfolioId, CancellationToken ct)
    {
        var user = await _db.Users
            .Where(u => u.WorkspaceAccessContexts.Any(context =>
                context.PortfolioId == portfolioId && context.Membership != null))
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(ct);

        if (user is null)
        {
            _logger.LogWarning(
                "Go-Live for portfolio {PortfolioId}: no administering user found; skipped self-owner creation.",
                portfolioId);
            return;
        }

        await _selfOwnerProvisioner.EnsureSelfOwnerAsync(user, portfolioId, ct);
    }

    // Kept beside the executable deletes so IntegrationTests can prove that this service and the
    // PostgreSQL grant/RLS contract classify exactly the same inventory. Order is child-to-parent.
    internal static IReadOnlyList<string> SandboxGraduationDeleteOrder { get; } =
    [
        "AtomicCommandReceipts", "SignatureAuditEvents", "NoticeDeliveryEvidence",
        "ExternalListingSignals", "ListingPhotos", "ListingPublications",
        "ApplicantScreeningMilestones", "ApplicantScreenings", "AdverseActionNotices",
        "ApplicationFinancialEntries",
        "EvictionCaseEvents", "EvictionCaseRespondents", "ExpenseLineItems", "InspectionItems",
        "DocumentTemplateFields", "ConversationMessages", "WorkOrderStatusEvents",
        "TeamRoutingRuleRecipients", "TeamRoutingRules", "MembershipRoleAssignmentProperties",
        "OwnerDistributions", "OwnerUserAccesses", "VendorRatings", "VendorDispatches",
        "LeaseRenewalAddendumDecisions", "SignatureSigners", "SignatureRequests", "NoticeDrafts",
        "TenantNoticeWorkItems", "RenderedNotices", "PortalMessages", "Notifications", "QueuedJobs",
        "PendingFileUploads", "PlaidTokenExchangeAttempts", "AccountingMappingPromotionJobs",
        "AccountingSyncMaps", "AccountingEntityMappings",
        "BankTransactions", "LoanPayments",
        "PropertyDispositions", "ProviderInboxEvents", "OutboxMessages", "SecurityDepositEntries",
        "TenantLedgerAllocations", "TenantLedgerEntries", "TenantPaymentAttempts",
        "TenantAutopayEnrollments", "TenantAccountConditionPeriods", "SecurityDepositAccounts",
        "TenantAccounts", "LeaseAgreementSigners", "LeaseAddendumSigners",
        "LeaseAddendumFinancialEffects", "EvictionCases", "Inspections", "Appointments",
        "ApplicationFinancialAccounts", "RentalApplications", "Expenses", "WorkOrders",
        "RecurringMaintenanceTasks", "CapitalAssets", "RecurringExpenses", "RentalListings",
        "TenantUserAccesses",
        "LeaseManagementParties", "UnitOperationalPeriods", "LeaseAddenda", "LeaseAgreements",
        "LeaseManagements", "LegalDocumentArtifacts", "DocumentTemplates", "ScanDrafts",
        "ScanBatches", "Conversations", "Loans", "StoredFiles", "Units",
        "Properties", "Tenants", "Vendors", "OwnerEntities",
        "Owners", "OAuthStates", "AtomicAuditLogs", "AuditLogs",
    ];

    /// <summary>
    /// Deletes every sandbox-owned operational/domain row for one portfolio in child-to-parent order.
    /// Every filter, relationship lookup, and delete remains a database-side statement; no entity graph
    /// is materialized. Workspace identity/security, device registrations, inspection templates, and
    /// reusable notification configuration are deliberately outside this inventory and survive.
    ///
    /// Atomic receipts are transitively scoped through their portfolio-owned audit rows. Outbox/inbox
    /// rows carry nullable PortfolioId because system workers process them globally, but portfolio-owned
    /// rows are stale sandbox work and must not dispatch after graduation. The exact reason-coded RLS
    /// lease and PostgreSQL delete guards admit these otherwise-durable deletes only here.
    /// </summary>
    private async Task WipePortfolioDataAsync(int portfolioId, CancellationToken ct)
    {
        await _db.AtomicCommandReceipts
            .Where(receipt => _db.AtomicAuditLogs.Any(audit =>
                audit.AttemptId == receipt.AttemptId && audit.PortfolioId == portfolioId))
            .ExecuteDeleteAsync(ct);
        await _db.SignatureAuditEvents.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.NoticeDeliveryEvidence.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ExternalListingSignals.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ListingPhotos.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ListingPublications.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ApplicantScreeningMilestones.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ApplicantScreenings.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.AdverseActionNotices.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ApplicationFinancialEntries.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.EvictionCaseEvents.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.EvictionCaseRespondents.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ExpenseLineItems.IgnoreQueryFilters()
            .Where(e => _db.Expenses.IgnoreQueryFilters()
                .Any(parent => parent.Id == e.ExpenseId && parent.PortfolioId == portfolioId))
            .ExecuteDeleteAsync(ct);
        await _db.InspectionItems.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.DocumentTemplateFields.IgnoreQueryFilters()
            .Where(e => _db.DocumentTemplates.IgnoreQueryFilters()
                .Any(parent => parent.Id == e.DocumentTemplateId
                    && parent.PortfolioId == portfolioId
                    && parent.IsSandboxSeeded))
            .ExecuteDeleteAsync(ct);
        await _db.ConversationMessages.IgnoreQueryFilters()
            .Where(e => _db.Conversations.IgnoreQueryFilters()
                .Any(parent => parent.Id == e.ConversationId && parent.PortfolioId == portfolioId))
            .ExecuteDeleteAsync(ct);
        await _db.WorkOrderStatusEvents.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TeamRoutingRuleRecipients.IgnoreQueryFilters()
            .Where(recipient => _db.TeamRoutingRules.IgnoreQueryFilters().Any(rule =>
                rule.Id == recipient.TeamRoutingRuleId
                && rule.PortfolioId == portfolioId
                && rule.PropertyId != null))
            .ExecuteDeleteAsync(ct);
        await _db.TeamRoutingRules.IgnoreQueryFilters()
            .Where(rule => rule.PortfolioId == portfolioId && rule.PropertyId != null)
            .ExecuteDeleteAsync(ct);
        await _db.MembershipRoleAssignmentProperties.IgnoreQueryFilters()
            .Where(scope => scope.PortfolioId == portfolioId)
            .ExecuteDeleteAsync(ct);
        await _db.OwnerDistributions.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.OwnerUserAccesses.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.VendorRatings.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.VendorDispatches.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseRenewalAddendumDecisions.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.SignatureSigners.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.SignatureRequests.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.NoticeDrafts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantNoticeWorkItems.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.RenderedNotices.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.PortalMessages.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Notifications.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.QueuedJobs.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.PendingFileUploads.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.PlaidTokenExchangeAttempts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.AccountingMappingPromotionJobs.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.AccountingSyncMaps.IgnoreQueryFilters()
            .Where(mapping => mapping.PortfolioId == portfolioId && mapping.LocalEntityId != null)
            .ExecuteDeleteAsync(ct);
        await _db.AccountingEntityMappings.IgnoreQueryFilters()
            .Where(mapping => mapping.PortfolioId == portfolioId && mapping.LocalEntityId != null)
            .ExecuteDeleteAsync(ct);
        await _db.BankTransactions.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LoanPayments.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.PropertyDispositions.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ProviderInboxEvents
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.OutboxMessages
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.SecurityDepositEntries.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantLedgerAllocations.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantLedgerEntries.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantPaymentAttempts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantAutopayEnrollments.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantAccountConditionPeriods.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.SecurityDepositAccounts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantAccounts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseAgreementSigners.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseAddendumSigners.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseAddendumFinancialEffects.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.EvictionCases.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Inspections.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Appointments.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ApplicationFinancialAccounts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.RentalApplications.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Expenses.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.WorkOrders.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.RecurringMaintenanceTasks.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.CapitalAssets.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.RecurringExpenses.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.RentalListings.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantUserAccesses.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseManagementParties.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.UnitOperationalPeriods.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseAddenda.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseAgreements.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseManagements.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LegalDocumentArtifacts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId
                && !_db.LegalDocumentSourceVersions.IgnoreQueryFilters().Any(source =>
                    source.PortfolioId == portfolioId
                    && source.SourceLegalDocumentArtifactId == e.Id))
            .ExecuteDeleteAsync(ct);
        await _db.DocumentTemplates.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId
                && e.IsSandboxSeeded
                && !_db.LegalDocumentSourceVersions.IgnoreQueryFilters().Any(source =>
                    source.PortfolioId == portfolioId
                    && source.DocumentTemplateId == e.Id))
            .ExecuteDeleteAsync(ct);
        await _db.ScanDrafts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ScanBatches.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Conversations.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Loans.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.StoredFiles.IgnoreQueryFilters()
            .Where(file => file.PortfolioId == portfolioId
                && !_db.DocumentTemplates.IgnoreQueryFilters().Any(template =>
                    template.PortfolioId == portfolioId
                    && (template.OriginalStoredFileId == file.Id
                        || template.CompiledStoredFileId == file.Id))
                && !_db.LegalDocumentSourceVersions.IgnoreQueryFilters().Any(source =>
                    source.PortfolioId == portfolioId
                    && source.SourceStoredFileId == file.Id)
                && !_db.LegalDocumentArtifacts.IgnoreQueryFilters().Any(artifact =>
                    artifact.PortfolioId == portfolioId
                    && artifact.StoredFileId == file.Id))
            .ExecuteDeleteAsync(ct);
        await _db.Units.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Properties.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Tenants.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Vendors.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.OwnerEntities.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Owners.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.OAuthStates.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.AtomicAuditLogs.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.AuditLogs.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
    }

    private static SandboxStateResponse ToState(Core.Entities.Portfolio p) => new()
    {
        PortfolioId = p.Id,
        IsSandbox = p.IsSandbox,
        SandboxSeededAtUtc = p.SandboxSeededAtUtc,
        OnboardingChoicePending = PortfolioOnboarding.IsPending(p.Settings),
    };
}
