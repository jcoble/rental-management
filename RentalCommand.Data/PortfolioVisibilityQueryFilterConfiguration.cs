using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

/// <summary>
/// Closes the global-query-filter graph below a soft-deleted portfolio and the two
/// soft-deletable aggregate roots which own required children. Every expression is a
/// navigation predicate that EF translates into SQL; no row is materialized to decide
/// visibility.
/// </summary>
internal static class PortfolioVisibilityQueryFilterConfiguration
{
    internal static void ConfigurePortfolioVisibilityQueryFilters(this ModelBuilder modelBuilder)
    {
        ConfigureApplicationFinance(modelBuilder);
        ConfigureAccounting(modelBuilder);
        ConfigureListings(modelBuilder);
        ConfigureEvictions(modelBuilder);
        ConfigureLeaseRelationships(modelBuilder);
        ConfigureLeaseLegalArtifacts(modelBuilder);
        ConfigureTenantAccounts(modelBuilder);
        ConfigureSignatures(modelBuilder);
        ConfigureNotifications(modelBuilder);
    }

    private static void ConfigureAccounting(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountingMappingPromotionJob>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<PlaidTokenExchangeAttempt>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
    }

    private static void ConfigureApplicationFinance(ModelBuilder modelBuilder)
    {
        // Application finance is immutable history. An application's own soft deletion must not
        // erase its accounting evidence; only the owning portfolio's visibility applies here.
        modelBuilder.Entity<ApplicationFinancialAccount>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<ApplicationFinancialEntry>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
    }

    private static void ConfigureListings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RentalListing>()
            .HasQueryFilter(row => row.DeletedAt == null && row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<ListingPhoto>()
            .HasQueryFilter(row =>
                row.RentalListing!.DeletedAt == null &&
                row.RentalListing.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<ListingPublication>()
            .HasQueryFilter(row =>
                row.RentalListing!.DeletedAt == null &&
                row.RentalListing.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<ExternalListingSignal>()
            .HasQueryFilter(row =>
                row.ListingPublication!.RentalListing!.DeletedAt == null &&
                row.ListingPublication.RentalListing.Portfolio!.DeletedAt == null);
    }

    private static void ConfigureEvictions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EvictionCase>()
            .HasQueryFilter(row => row.DeletedAt == null && row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<EvictionCaseRespondent>()
            .HasQueryFilter(row =>
                row.EvictionCase!.DeletedAt == null &&
                row.EvictionCase.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<EvictionCaseEvent>()
            .HasQueryFilter(row =>
                row.DeletedAt == null &&
                row.EvictionCase!.DeletedAt == null &&
                row.EvictionCase.Portfolio!.DeletedAt == null);
    }

    private static void ConfigureLeaseRelationships(ModelBuilder modelBuilder)
    {
        // These rows carry lifecycle and access history. They are not soft-deleted merely because
        // a related tenant, unit, or document later changes; the portfolio is the visibility root.
        modelBuilder.Entity<LeaseManagement>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<LeaseManagementParty>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<TenantUserAccess>()
            .HasQueryFilter(row =>
                row.Portfolio!.DeletedAt == null &&
                row.AccessContext!.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<UnitOperationalPeriod>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
    }

    private static void ConfigureLeaseLegalArtifacts(ModelBuilder modelBuilder)
    {
        // Issued legal rows remain visible for their complete history. The filter is deliberately
        // portfolio-only and never interprets supersession, execution, or governing status.
        modelBuilder.Entity<LegalDocumentArtifact>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<LeaseAgreement>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<LeaseAgreementSigner>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<LeaseAddendum>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<LeaseAddendumSigner>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<LeaseAddendumFinancialEffect>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<LeaseRenewalAddendumDecision>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
    }

    private static void ConfigureTenantAccounts(ModelBuilder modelBuilder)
    {
        // Closed accounts and immutable ledger/deposit rows remain queryable. Portfolio deletion,
        // not account lifecycle state, controls ordinary visibility.
        modelBuilder.Entity<TenantAccount>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<TenantAccountConditionPeriod>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<TenantPaymentAttempt>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<TenantLedgerEntry>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<TenantLedgerAllocation>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<TenantAutopayEnrollment>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<SecurityDepositAccount>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<SecurityDepositEntry>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
    }

    private static void ConfigureSignatures(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SignatureRequest>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<SignatureSigner>()
            .HasQueryFilter(row => row.SignatureRequest!.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<SignatureAuditEvent>()
            .HasQueryFilter(row => row.SignatureRequest!.Portfolio!.DeletedAt == null);
    }

    private static void ConfigureNotifications(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NoticeDraft>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<UserAlertPreference>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<TeamRoutingRule>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<TeamRoutingRuleRecipient>()
            .HasQueryFilter(row => row.Rule!.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<WorkspaceNoticeTemplateVersion>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<TenantNoticePolicy>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<RenderedNotice>()
            .HasQueryFilter(row => row.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<NoticeDeliveryEvidence>()
            .HasQueryFilter(row => row.RenderedNotice!.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<TenantNoticeWorkItem>()
            .HasQueryFilter(row => row.Policy!.Portfolio!.DeletedAt == null);
    }
}
