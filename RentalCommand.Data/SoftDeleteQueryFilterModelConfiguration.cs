using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data;

internal static class SoftDeleteQueryFilterModelConfiguration
{
    internal static void ConfigureSoftDeleteQueryFilters(this ModelBuilder modelBuilder)
    {
        // ----------------------------------------------------------------------------------------
        // Soft-delete consistency across required relationships.
        //
        // A soft-deletable principal (Portfolio/Expense/RentalApplication/WorkOrder, each with
        // a `DeletedAt == null` global filter) was the REQUIRED end of relationships whose dependent
        // rows had NO matching filter. EF Core flagged every one of these with warning EF10622
        // ("required entity is filtered out").
        //
        // Fix: give each such dependent a query filter that matches its principal's soft-delete state
        // by walking the required navigation. Because the relationship is required, EF emits an INNER
        // JOIN to the (already-filtered) principal set on every query of the dependent — so a row
        // whose principal is soft-deleted simply disappears everywhere, including the scalar-FK
        // aggregates. This is the single, declarative source of truth the audit asked for; no per-site
        // service change and no denormalized DeletedAt column on the leaf tables is required.
        //
        // Identity / global / infra tables (AspNet*, AtomicAuditLog, OutboxMessage, EngineWorkerHeartbeat,
        // ProviderInboxEvent) are intentionally NOT filtered here — they are not soft-deletable and
        // several legitimately outlive any single business row.
        // ----------------------------------------------------------------------------------------

        // Dependent of Loan (Loan has its own `DeletedAt == null`). The amortization rows disappear
        // when the loan is soft-deleted, so a deleted loan's interest never leaks into a report.
        modelBuilder.Entity<LoanPayment>().HasQueryFilter(e => e.Loan!.DeletedAt == null);
        modelBuilder.Entity<LoanPaymentCorrection>()
            .HasQueryFilter(e => e.LoanPayment!.Loan!.DeletedAt == null);

        // Dependents of Portfolio (Portfolio has `DeletedAt == null`).
        modelBuilder.Entity<AccountingConnection>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<AccountingEntityMapping>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<AccountingSyncMap>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<Appointment>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<BankConnection>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<BankStatement>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<OAuthState>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<BankTransaction>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<Conversation>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<DeviceToken>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<Inspection>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<Notification>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<NotificationReadState>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<AutomationSettings>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<MessagingProviderSettings>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<WorkspaceLlmCredential>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<LlmUsageEvidence>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<PropertyOwnership>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<QueuedJob>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<ScanBatch>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<ScanDraft>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<VendorDispatch>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<VendorRating>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);

        // Dependents of RentalApplication (RentalApplication has `DeletedAt == null`); nav is `Application`.
        modelBuilder.Entity<AdverseActionNotice>().HasQueryFilter(e => e.Application!.DeletedAt == null);
        modelBuilder.Entity<ApplicantScreening>().HasQueryFilter(e => e.Application!.DeletedAt == null);
        modelBuilder.Entity<ApplicantScreeningMilestone>().HasQueryFilter(e => e.ApplicantScreening!.Application!.DeletedAt == null);
        // Application financial accounts and entries are immutable accounting history. They remain
        // queryable after the mutable application is soft-deleted; every reader must scope them by
        // PortfolioId and must not recover deleted applicant PII through the application navigation.

        // Dependent of Expense (Expense has `DeletedAt == null`).
        modelBuilder.Entity<ExpenseAllocation>().HasQueryFilter(e => e.Expense!.DeletedAt == null);
        modelBuilder.Entity<ExpenseLineItem>().HasQueryFilter(e => e.Expense!.DeletedAt == null);

        // Dependent of WorkOrder (WorkOrder has `DeletedAt == null`).
        modelBuilder.Entity<WorkOrderStatusEvent>().HasQueryFilter(e => e.WorkOrder!.DeletedAt == null);

        // Transitive (grandchild) dependents: their direct parent is now itself filtered above, so
        // EF flags the same EF10622 one level down. Chain the filter through to the root soft-deletable
        // principal's `DeletedAt` so the whole sub-tree disappears when an ancestor is soft-deleted.
        modelBuilder.Entity<ConversationMessage>().HasQueryFilter(e => e.Conversation!.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<InspectionItem>().HasQueryFilter(e => e.Inspection!.Portfolio!.DeletedAt == null);

        modelBuilder.Entity<LedgerAccount>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<JournalEntry>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<RecurringTenantCharge>().HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<AccountingConversionReconciliation>()
            .HasQueryFilter(e => e.Portfolio!.DeletedAt == null);
        modelBuilder.Entity<JournalLine>()
            .HasQueryFilter(e => e.JournalEntry!.Portfolio!.DeletedAt == null);
    }
}
