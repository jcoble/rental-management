using System.Runtime.CompilerServices;
using RentalCommand.Core.Atomic;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Sandbox;

public static class SandboxLifecyclePersistence
{
    public static IReadOnlyList<string> SandboxGraduationDeleteOrder { get; } =
    [
        "AtomicCommandReceipts", "SignatureAuditEvents", "NoticeDeliveryEvidence", "LlmUsageEvidence",
        "ExternalListingSignals", "ListingPhotos", "ListingPublications",
        "ApplicantScreeningMilestones", "ApplicantScreenings", "AdverseActionNotices",
        "ApplicationFinancialEntries",
        "EvictionCaseEvents", "EvictionCaseRespondents", "ExpenseAllocations", "ExpenseLineItems", "InspectionItems",
        "DocumentTemplateFields", "ConversationMessages", "TechnicianWorkEntries",
        "WorkOrderResponsibilities", "WorkOrderStatusEvents",
        "TeamRoutingRuleRecipients", "TeamRoutingRules", "MembershipRoleAssignmentProperties",
        "OwnerDistributions", "OwnerUserAccesses", "VendorRatings", "VendorDispatches",
        "LeaseRenewalAddendumDecisions", "SignatureSigners", "SignatureRequests", "NoticeDrafts",
        "TenantNoticeWorkItems", "RenderedNotices", "NotificationReadStates",
        "Notifications", "QueuedJobs",
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
        "ScanBatches", "Conversations", "Loans", "StoredFiles", "Units", "PropertyOwnerships",
        "Properties", "Tenants", "Vendors", "OwnerEntities",
        "OAuthStates", "AtomicAuditLogs",
    ];

    public static async Task WipePortfolioDataAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        CancellationToken ct)
    {
        await DeleteAsync(db, context, "AtomicCommandReceipts", $"""
            DELETE FROM "AtomicCommandReceipts" AS receipt
            WHERE EXISTS (
                SELECT 1
                FROM "AtomicAuditLogs" AS audit
                WHERE audit."AttemptId" = receipt."AttemptId"
                  AND audit."PortfolioId" = {portfolioId})
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteSimpleAsync(db, context, "SignatureAuditEvents", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "NoticeDeliveryEvidence", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "LlmUsageEvidence", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "ExternalListingSignals", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "ListingPhotos", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "ListingPublications", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "ApplicantScreeningMilestones", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "ApplicantScreenings", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "AdverseActionNotices", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "ApplicationFinancialEntries", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "EvictionCaseEvents", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "EvictionCaseRespondents", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "ExpenseAllocations", portfolioId, ct);
        await DeleteAsync(db, context, "ExpenseLineItems", $"""
            DELETE FROM "ExpenseLineItems" AS child
            WHERE EXISTS (
                SELECT 1
                FROM "Expenses" AS parent
                WHERE parent."Id" = child."ExpenseId"
                  AND parent."PortfolioId" = {portfolioId})
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteSimpleAsync(db, context, "InspectionItems", portfolioId, ct);
        await DeleteAsync(db, context, "DocumentTemplateFields", $"""
            DELETE FROM "DocumentTemplateFields" AS child
            WHERE child."PortfolioId" = {portfolioId}
              AND EXISTS (
                  SELECT 1
                  FROM "DocumentTemplates" AS parent
                  WHERE parent."Id" = child."DocumentTemplateId"
                    AND parent."PortfolioId" = child."PortfolioId"
                    AND parent."IsSandboxSeeded")
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteAsync(db, context, "ConversationMessages", $"""
            DELETE FROM "ConversationMessages" AS child
            WHERE EXISTS (
                SELECT 1
                FROM "Conversations" AS parent
                WHERE parent."Id" = child."ConversationId"
                  AND parent."PortfolioId" = {portfolioId})
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteSimpleAsync(db, context, "TechnicianWorkEntries", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "WorkOrderResponsibilities", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "WorkOrderStatusEvents", portfolioId, ct);
        await DeleteAsync(db, context, "TeamRoutingRuleRecipients", $"""
            DELETE FROM "TeamRoutingRuleRecipients" AS recipient
            WHERE EXISTS (
                SELECT 1
                FROM "TeamRoutingRules" AS rule
                WHERE rule."Id" = recipient."TeamRoutingRuleId"
                  AND rule."PortfolioId" = {portfolioId}
                  AND rule."PropertyId" IS NOT NULL)
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteAsync(db, context, "TeamRoutingRules", $"""
            DELETE FROM "TeamRoutingRules" AS rule
            WHERE rule."PortfolioId" = {portfolioId}
              AND rule."PropertyId" IS NOT NULL
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteSimpleAsync(db, context, "MembershipRoleAssignmentProperties", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "OwnerDistributions", portfolioId, ct);
        await DeleteAsync(db, context, "OwnerUserAccesses", $"""
            DELETE FROM "OwnerUserAccesses" AS access
            WHERE access."PortfolioId" = {portfolioId}
              AND NOT EXISTS (
                  SELECT 1
                  FROM "OwnerEntities" AS owner
                  WHERE owner."Id" = access."OwnerEntityId"
                    AND owner."PortfolioId" = {portfolioId}
                    AND owner."IsPrimary"
                    AND owner."DeletedAt" IS NULL)
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteSimpleAsync(db, context, "VendorRatings", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "VendorDispatches", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "LeaseRenewalAddendumDecisions", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "SignatureSigners", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "SignatureRequests", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "NoticeDrafts", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "TenantNoticeWorkItems", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "RenderedNotices", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "NotificationReadStates", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "Notifications", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "QueuedJobs", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "PendingFileUploads", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "PlaidTokenExchangeAttempts", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "AccountingMappingPromotionJobs", portfolioId, ct);
        await DeleteAsync(db, context, "AccountingSyncMaps", $"""
            DELETE FROM "AccountingSyncMaps"
            WHERE "PortfolioId" = {portfolioId}
              AND "LocalEntityId" IS NOT NULL
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteAsync(db, context, "AccountingEntityMappings", $"""
            DELETE FROM "AccountingEntityMappings"
            WHERE "PortfolioId" = {portfolioId}
              AND "LocalEntityId" IS NOT NULL
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteSimpleAsync(db, context, "BankTransactions", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "LoanPayments", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "PropertyDispositions", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "ProviderInboxEvents", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "OutboxMessages", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "SecurityDepositEntries", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "TenantLedgerAllocations", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "TenantLedgerEntries", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "TenantPaymentAttempts", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "TenantAutopayEnrollments", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "TenantAccountConditionPeriods", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "SecurityDepositAccounts", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "TenantAccounts", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "LeaseAgreementSigners", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "LeaseAddendumSigners", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "LeaseAddendumFinancialEffects", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "EvictionCases", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "Inspections", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "Appointments", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "ApplicationFinancialAccounts", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "RentalApplications", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "Expenses", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "WorkOrders", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "RecurringMaintenanceTasks", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "CapitalAssets", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "RecurringExpenses", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "RentalListings", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "TenantUserAccesses", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "LeaseManagementParties", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "UnitOperationalPeriods", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "LeaseAddenda", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "LeaseAgreements", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "LeaseManagements", portfolioId, ct);
        await DeleteAsync(db, context, "LegalDocumentArtifacts", $"""
            DELETE FROM "LegalDocumentArtifacts" AS artifact
            WHERE artifact."PortfolioId" = {portfolioId}
              AND NOT EXISTS (
                  SELECT 1
                  FROM "LegalDocumentSourceVersions" AS source
                  WHERE source."PortfolioId" = {portfolioId}
                    AND source."SourceLegalDocumentArtifactId" = artifact."Id")
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteAsync(db, context, "DocumentTemplates", $"""
            DELETE FROM "DocumentTemplates" AS template
            WHERE template."PortfolioId" = {portfolioId}
              AND template."IsSandboxSeeded"
              AND NOT EXISTS (
                  SELECT 1
                  FROM "LegalDocumentSourceVersions" AS source
                  WHERE source."PortfolioId" = {portfolioId}
                    AND source."DocumentTemplateId" = template."Id")
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteSimpleAsync(db, context, "ScanDrafts", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "ScanBatches", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "Conversations", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "Loans", portfolioId, ct);
        await DeleteAsync(db, context, "StoredFiles", $"""
            DELETE FROM "StoredFiles" AS file
            WHERE file."PortfolioId" = {portfolioId}
              AND NOT EXISTS (
                  SELECT 1
                  FROM "DocumentTemplates" AS template
                  WHERE template."PortfolioId" = {portfolioId}
                    AND (template."OriginalStoredFileId" = file."Id"
                         OR template."CompiledStoredFileId" = file."Id"))
              AND NOT EXISTS (
                  SELECT 1
                  FROM "LegalDocumentSourceVersions" AS source
                  WHERE source."PortfolioId" = {portfolioId}
                    AND source."SourceStoredFileId" = file."Id")
              AND NOT EXISTS (
                  SELECT 1
                  FROM "LegalDocumentArtifacts" AS artifact
                  WHERE artifact."PortfolioId" = {portfolioId}
                    AND artifact."StoredFileId" = file."Id")
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteSimpleAsync(db, context, "Units", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "PropertyOwnerships", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "Properties", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "Tenants", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "Vendors", portfolioId, ct);
        await DeleteAsync(db, context, "OwnerEntities", $"""
            DELETE FROM "OwnerEntities"
            WHERE "PortfolioId" = {portfolioId}
              AND NOT "IsPrimary"
            RETURNING 1 AS "Value"
            """, ct);
        await DeleteSimpleAsync(db, context, "OAuthStates", portfolioId, ct);
        await DeleteSimpleAsync(db, context, "AtomicAuditLogs", portfolioId, ct);
    }

    private static Task<List<int>> DeleteSimpleAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        string tableName,
        int portfolioId,
        CancellationToken ct) =>
        DeleteAsync(
            db,
            context,
            tableName,
            FormattableStringFactory.Create(
                "DELETE FROM \"" + tableName + "\"\n" +
                "WHERE \"PortfolioId\" = {0}\n" +
                "RETURNING 1 AS \"Value\"",
                portfolioId),
            ct);

    private static Task<List<int>> DeleteAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        string tableName,
        FormattableString sql,
        CancellationToken ct) =>
        db.ExecuteAtomicSqlMutationAsync<int>(
            context,
            sql,
            [new AtomicSqlMutationTarget(tableName, AtomicSqlMutationOperation.Delete)],
            ct);
}
