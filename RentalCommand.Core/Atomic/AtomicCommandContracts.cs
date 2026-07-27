using System.Text.Json;
using System.Linq.Expressions;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Atomic;

/// <summary>Explicit marker for immutable, service-free command DTOs.</summary>
public interface IAtomicCommandData;

/// <summary>Explicit marker for immutable, service-free result DTOs.</summary>
public interface IAtomicResultData;

/// <summary>
/// Explicit allowlist marker for pure handler dependencies. Implementations are recursively
/// inspected before a handler is constructed; remote, persistence, provider, and factory
/// dependencies remain forbidden even through a marked wrapper.
/// </summary>
public interface IAtomicTransactionSafeDependency;

/// <summary>
/// Read-only indication that the current scoped DbContext is executing an admitted atomic command.
/// Legacy infrastructure uses this only to stand down while the atomic kernel owns the write.
/// </summary>
public interface IAtomicExecutionState : IAtomicTransactionSafeDependency
{
    bool IsActive { get; }
    bool IsInfrastructureActive { get; }
    bool AllowsUnconvertedWrites { get; }
}

/// <summary>
/// Deliberately narrow receiptless workflows owned by platform infrastructure rather than an end-user
/// business command. These operations still run in one kernel-owned transaction and are never an
/// escape hatch for ordinary domain mutations.
/// </summary>
public enum AtomicInfrastructureOperation
{
    DemoSeed,
    SandboxTransition,
    PortfolioQaDelivery,
    SimulationClock,
    SimulationWorkerCommand,
    EngineHeartbeat,
}

/// <summary>
/// Runs an explicitly classified infrastructure workflow in one kernel-owned transaction. Nested
/// infrastructure calls join their owner transaction; callers cannot begin, commit, or roll back it.
/// </summary>
public interface IAtomicInfrastructureUnitOfWork
{
    Task ExecuteAsync(
        AtomicInfrastructureOperation operation,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default);

    Task<TResult> ExecuteAsync<TResult>(
        AtomicInfrastructureOperation operation,
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken ct = default);
}

/// <summary>
/// Infrastructure-neutral entry point for retry-safe, receipt-backed database commands. A handler
/// is resolved from the fresh physical-attempt scope; callers cannot close over a DbContext.
/// </summary>
public interface IAtomicUnitOfWork
{
    Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        IAtomicResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull;
}

/// <summary>Application command code resolved once per physical execution-strategy attempt.</summary>
public interface IAtomicCommandHandler<in TCommand, TResult>
    where TCommand : notnull, IAtomicCommandData
    where TResult : notnull
{
    Task<TResult> HandleAsync(
        TCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct);
}

/// <summary>
/// Optional authorization hook for receipt replays. The atomic kernel invokes this inside the
/// receipt transaction before deserializing or returning a stored result. Implementations receive
/// only the original command shape and the restricted persistence session; they must perform no
/// business writes.
/// </summary>
public interface IAtomicReplayAuthorizer<in TCommand>
    where TCommand : notnull, IAtomicCommandData
{
    Task AuthorizeReplayAsync(
        TCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct);
}

/// <summary>
/// Marker for network/remote dependencies. Atomic command handlers must stage durable intent in
/// the database and cannot directly depend on types carrying this marker.
/// </summary>
public interface IAtomicRemoteDependency;

/// <summary>
/// Narrow kernel lease for durable infrastructure metadata written before a business command.
/// It deliberately exposes named operations rather than arbitrary table/DML permissions.
/// </summary>
public interface IAtomicInfrastructureWriteGate
{
    IDisposable BeginPendingFileUploadAdmission();

    /// <summary>
    /// Admits exactly one conflict-safe insert of untrusted external listing metadata before it
    /// enters user-confirmed business state. It grants no update or delete permission.
    /// </summary>
    IDisposable BeginExternalListingSignalAdmission();

    /// <summary>Admits one conflict-safe scheduler insertion into the tenant-notice work queue.</summary>
    IDisposable BeginTenantNoticeCandidateGeneration();

    /// <summary>Admits one token-fenced claim statement over the tenant-notice work queue.</summary>
    IDisposable BeginTenantNoticeWorkItemClaim();

    /// <summary>Admits one token-fenced completion statement over the tenant-notice work queue.</summary>
    IDisposable BeginTenantNoticeWorkItemCompletion();

    /// <summary>Admits one token-fenced release statement over the tenant-notice work queue.</summary>
    IDisposable BeginTenantNoticeWorkItemRelease();

    /// <summary>Admits one token-fenced block statement over the tenant-notice work queue.</summary>
    IDisposable BeginTenantNoticeWorkItemBlock();
}

/// <summary>
/// Stages a semantic audit event inside the currently admitted atomic command. Implementations must
/// reject calls made outside that command transaction; audit rows may never be committed separately
/// from their business mutation.
/// </summary>
public interface IAtomicAuditEventSink : IAtomicTransactionSafeDependency
{
    void EnsureActive();
    void Stage(AtomicSemanticAudit audit);
}

/// <summary>Capabilities owned by the current physical attempt; it deliberately exposes no ORM.</summary>
public interface IAtomicWriteAttempt
{
    Guid AttemptId { get; }
    Guid AuditScopeId { get; }
    IAtomicPersistenceSession Persistence { get; }
    IAtomicSetBasedPersistence SetBased { get; }
    IAtomicBankingPersistence Banking { get; }
    IAtomicAccountingPersistence Accounting { get; }
    IAtomicLockingPersistence Locking { get; }
    IAtomicScanConfirmationPersistence ScanConfirmation { get; }
    IAtomicScheduledFinancePersistence ScheduledFinance { get; }
    IAtomicProviderInboxPersistence ProviderInbox { get; }
    IAtomicProviderPaymentPersistence ProviderPayments { get; }
    IAtomicTenantMoneyPersistence TenantMoney { get; }
    IAtomicPendingFileUploadPersistence PendingFileUploads { get; }
    IAtomicLeaseMutationPersistence Leasing { get; }
    IAtomicListingPersistence Listings { get; }
    IAtomicUnitImportPersistence UnitImports { get; }
    IAtomicCoreCsvImportPersistence CoreCsvImports { get; }
    IAtomicNotificationPersistence Notifications { get; }
    IAtomicNoticeDraftPersistence NoticeDrafts { get; }
    IAtomicPaymentCsvImportPersistence PaymentCsvImports { get; }
    IAtomicInspectionPersistence Inspections { get; }
    IAtomicAccountSecurityPersistence AccountSecurity { get; }
    IAtomicWorkspaceExperiencePersistence WorkspaceExperiences { get; }

    /// <summary>Flushes tracked business rows while the owner transaction remains open.</summary>
    Task<AtomicBusinessFlush> FlushBusinessAsync(CancellationToken ct = default);

    /// <summary>
    /// Binds semantic detail to this exact tracked object before its next flush. No tuple lookup or
    /// entity-wide permit is used; the subsequent mutation descriptor carries the same reference.
    /// </summary>
    void BindSemanticAudit(object entityReference, AtomicSemanticAudit audit);

    /// <summary>Uses one PostgreSQL wall-clock value for every tracked audit in this attempt.</summary>
    void UseDatabaseWallClockForAudit(DateTime occurredAtUtc);

    /// <summary>Enriches only the exact mutation descriptor returned by a business flush.</summary>
    void EnrichMutation(AtomicAuditMutation mutation, AtomicSemanticAudit audit);

    /// <summary>Stages an explicit semantic event that is not a tracked-entity mutation.</summary>
    void StageSemanticEvent(AtomicSemanticAudit audit);

    /// <summary>Stages an explicit event at a timestamp read from PostgreSQL's wall clock.</summary>
    void StageSemanticEvent(AtomicSemanticAudit audit, DateTime occurredAtUtc);

    /// <summary>Stages an outbox companion for the owner's final flush.</summary>
    void StageOutbox(OutboxMessage message);
}

/// <summary>Kernel-owned set-based listing mutations that cannot be expressed as tracked rows.</summary>
public interface IAtomicListingPersistence
{
    /// <summary>
    /// Validates an exact listing photo permutation and applies it in PostgreSQL without
    /// materializing the manifest or issuing per-photo updates.
    /// </summary>
    Task<AtomicListingPhotoOrderResult> ReorderPhotosAsync(
        int portfolioId,
        int rentalListingId,
        int[] photoIds,
        CancellationToken ct = default);
}

/// <summary>Database-owned bulk notification mutations used by receipt-backed commands.</summary>
public interface IAtomicNotificationPersistence
{
    /// <summary>
    /// Inserts one caller-specific read state if the notification is still visible. Existing read
    /// state is preserved, making retries and concurrent attempts idempotent.
    /// </summary>
    Task<bool> MarkReadAsync(
        int portfolioId,
        int notificationId,
        int userId,
        bool includeStaffOnlyNotifications,
        DateTime readAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Marks every currently visible unread notification for one caller as read in one SQL
    /// statement. Tenant-message filtering and affected-row computation stay in PostgreSQL.
    /// </summary>
    Task<int> MarkAllReadAsync(
        int portfolioId,
        int userId,
        bool includeStaffOnlyNotifications,
        DateTime readAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Clears the unread counter for one authorized conversation in one SQL statement. The caller
    /// owns authorization and supplies whether the tenant or landlord counter is being cleared.
    /// </summary>
    Task<int> MarkConversationReadAsync(
        int portfolioId,
        int conversationId,
        bool tenantViewer,
        DateTime readAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves due workspaces, authorized recipients, preferences, destinations, and the bounded
    /// 25-item digest in one PostgreSQL statement.
    /// </summary>
    Task<IReadOnlyList<AtomicMorningBriefingDigest>> ReadDueMorningBriefingsAsync(
        DateTime evaluationUtc,
        CancellationToken ct = default);
}

public sealed class AtomicMorningBriefingDigest : IAtomicResultData
{
    public int PortfolioId { get; set; }
    public string PortfolioName { get; set; } = string.Empty;
    public int UserId { get; set; }
    public string LocalDate { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public bool EnableEmail { get; set; }
    public bool EnableSms { get; set; }
    public bool EnablePush { get; set; }
    public int ItemCount { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string DeviceTokensJson { get; set; } = "[]";
}

/// <summary>
/// Kernel-owned tenant-notice generation. Candidate selection, authorization, rendering,
/// deduplication, insertion, and response projection remain one PostgreSQL statement.
/// </summary>
public interface IAtomicNoticeDraftPersistence
{
    Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> GenerateClaimedBatchAsync(
        Guid claimToken,
        CancellationToken ct = default);

    Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> GenerateManualAsync(
        WorkspaceReadScope scope,
        int? recipientTenantId,
        int? leaseManagementId,
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        string? noticeType,
        DateTime securityNowUtc,
        CancellationToken ct = default);
}

/// <summary>One row returned by the database-owned notice draft INSERT/SELECT command.</summary>
public sealed class AtomicGeneratedTenantNoticeDraft : IAtomicResultData
{
    public long? WorkItemId { get; init; }
    public int DraftId { get; init; }
    public bool WasCreated { get; init; }
    public int CreatedCount { get; init; }
    public int PortfolioId { get; init; }
    public int LeaseManagementId { get; init; }
    public int TenantAccountId { get; init; }
    public int RecipientLeaseManagementPartyId { get; init; }
    public int? LeaseAgreementId { get; init; }
    public int? LeaseAddendumId { get; init; }
    public long? TenantLedgerEntryId { get; init; }
    public int RecipientTenantId { get; init; }
    public int? PropertyId { get; init; }
    public string TenantName { get; init; } = string.Empty;
    public string? PropertyName { get; init; }
    public string? UnitNumber { get; init; }
    public string NoticeType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTime TriggerDate { get; init; }
    public int? ConversationId { get; init; }
    public string? ApprovedChannels { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public DateTime? DismissedAt { get; init; }
}

public sealed record AtomicListingPhotoOrderResult(bool IsValid, bool HasChanges);

/// <summary>
/// Narrow persistence boundary for the database-owned fresh-workspace bootstrap. The function
/// creates the complete authority graph in one PostgreSQL statement after the Identity user has
/// received its generated key inside the same receipt transaction.
/// </summary>
public interface IAtomicAccountSecurityPersistence
{
    Task<RentalCommand.Core.Auth.AtomicInitialWorkspaceBootstrap> BootstrapInitialWorkspaceAsync(
        int userId,
        string portfolioName,
        string managementCompanyName,
        string ownerName,
        string ownerEmail,
        DateTime createdAtUtc,
        CancellationToken ct = default);

    Task<int> DeleteFreshWorkspaceSuppliedNoticeTemplateVersionsAsync(
        int userId,
        int portfolioId,
        IReadOnlyList<int> templateVersionIds,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically validates and consumes one Team invitation through the database-owned pre-login
    /// authority boundary. A missing result means the token is invalid, expired, already consumed,
    /// or no longer points at active workspace authority.
    /// </summary>
    Task<AtomicWorkspaceInvitationActivation?> ActivateWorkspaceInvitationAsync(
        long invitationId,
        int invitedUserId,
        string tokenHash,
        string passwordHash,
        string newSecurityStamp,
        string newConcurrencyStamp,
        CancellationToken ct = default);
}

public sealed record AtomicWorkspaceInvitationActivation(
    int PortfolioId,
    int WorkspaceMembershipId,
    int AccessContextId,
    int InvitedUserId,
    DateTime AcceptedAtUtc);

/// <summary>Kernel-owned set-based inspection mutations that cannot be expressed as tracked rows.</summary>
public interface IAtomicInspectionPersistence
{
    /// <summary>
    /// Validates and applies one exact checklist permutation in PostgreSQL. The inspection touch and
    /// every item position change remain in the caller's receipt transaction.
    /// </summary>
    Task<AtomicInspectionItemOrderResult> ReorderItemsAsync(
        int portfolioId,
        int inspectionId,
        int[] itemIds,
        DateTime updatedAtUtc,
        CancellationToken ct = default);
}

public sealed record AtomicInspectionItemOrderResult(
    bool InspectionExists,
    int InspectionStatus,
    bool IsValid,
    bool HasChanges);

/// <summary>One parsed Unit CSV row admitted to the database-owned bulk import.</summary>
public sealed record AtomicUnitImportRow(
    int RowNumber,
    int? PropertyId,
    string? PropertyName,
    string UnitNumber,
    decimal Bedrooms,
    decimal Bathrooms,
    decimal MarketRent,
    string[] Errors) : IAtomicCommandData;

/// <summary>Database-owned validation and insert outcome for one Unit CSV row.</summary>
public sealed record AtomicUnitImportRowResult(
    int RowNumber,
    bool Valid,
    bool IsDuplicate,
    int? CreatedId,
    int? PropertyId,
    string UnitNumber,
    decimal Bedrooms,
    decimal Bathrooms,
    decimal MarketRent,
    string[] Errors) : IAtomicResultData;

public sealed record AtomicUnitImportBatchResult(
    bool Authorized,
    IReadOnlyList<AtomicUnitImportRowResult> Rows,
    IReadOnlyList<AtomicUnitImportRowResult> CreatedRows,
    int TotalRows,
    int ValidRows,
    int CreatedCount,
    int DuplicateRows) : IAtomicResultData;

/// <summary>
/// Resolves property references, detects duplicates, inserts valid Units, and returns the entire
/// batch result in one PostgreSQL statement. No caller may materialize reference tables or issue
/// per-row database work.
/// </summary>
public interface IAtomicUnitImportPersistence
{
    Task<AtomicUnitImportBatchResult> ImportAsync(
        WorkspaceReadScope scope,
        IReadOnlyList<AtomicUnitImportRow> rows,
        DateTime createdAtUtc,
        CancellationToken ct = default);
}

/// <summary>
/// Read-only Unit CSV preview. It uses the exact PostgreSQL validator as the atomic import but
/// never opens an atomic command receipt and never inserts Units.
/// </summary>
public interface IUnitCsvImportPreviewQuery
{
    Task<AtomicUnitImportBatchResult> PreviewAsync(
        WorkspaceReadScope scope,
        IReadOnlyList<AtomicUnitImportRow> rows,
        CancellationToken ct = default);
}

public enum AtomicCoreCsvImportDomain
{
    Property,
    Tenant,
    Expense,
    Loan,
}

public sealed record AtomicCoreCsvImportRowResult(
    int RowNumber,
    bool Valid,
    bool IsDuplicate,
    int? CreatedId,
    int? RelatedId,
    string[] Errors) : IAtomicResultData;

public sealed record AtomicCoreCsvImportBatchResult(
    bool Authorized,
    IReadOnlyList<AtomicCoreCsvImportRowResult> Rows,
    IReadOnlyList<AtomicCoreCsvImportRowResult> CreatedRows,
    int TotalRows,
    int ValidRows,
    int CreatedCount,
    int DuplicateRows) : IAtomicResultData;

/// <summary>
/// Executes one typed Property, Tenant, Expense, or Loan CSV batch as one PostgreSQL statement inside the owning
/// command transaction. Resolution, authorization, classification, insertion, and result counts
/// remain database-owned.
/// </summary>
public interface IAtomicCoreCsvImportPersistence
{
    Task<AtomicCoreCsvImportBatchResult> ImportAsync(
        WorkspaceReadScope scope,
        AtomicCoreCsvImportDomain domain,
        string rowsJson,
        DateTime createdAtUtc,
        CancellationToken ct = default);
}

/// <summary>
/// Read-only typed CSV preview. It opens no receipt and admits no write permit or DML.
/// </summary>
public interface ICoreCsvImportPreviewQuery
{
    Task<AtomicCoreCsvImportBatchResult> PreviewAsync(
        WorkspaceReadScope scope,
        AtomicCoreCsvImportDomain domain,
        string rowsJson,
        CancellationToken ct = default);
}

public sealed record AtomicPaymentCsvImportRowResult(
    int RowNumber,
    bool Valid,
    bool IsDuplicate,
    long? CreatedId,
    int? TenantAccountId,
    string[] Errors) : IAtomicResultData;

public sealed record AtomicPaymentCsvImportBatchResult(
    bool Authorized,
    IReadOnlyList<AtomicPaymentCsvImportRowResult> Rows,
    IReadOnlyList<AtomicPaymentCsvImportRowResult> CreatedRows,
    int TotalRows,
    int ValidRows,
    int CreatedCount,
    int DuplicateRows) : IAtomicResultData;

public interface IAtomicPaymentCsvImportPersistence
{
    Task<AtomicPaymentCsvImportBatchResult> ImportAsync(
        WorkspaceReadScope scope,
        string rowsJson,
        DateTime createdAtUtc,
        CancellationToken ct = default);
}

public interface IPaymentCsvImportPreviewQuery
{
    Task<AtomicPaymentCsvImportBatchResult> PreviewAsync(
        WorkspaceReadScope scope,
        string rowsJson,
        CancellationToken ct = default);
}

/// <summary>
/// Row-locking persistence boundary for already-bounded scheduled-finance claims. Eligibility is
/// rechecked with the claim token and lease expiry in PostgreSQL before any tracked row is exposed.
/// </summary>
public interface IAtomicScheduledFinancePersistence
{
    Task<IReadOnlyList<Loan>> LockDebtServiceClaimsAsync(
        int[] loanIds,
        Guid claimToken,
        DateTime businessDateUtc,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<int, AtomicLoanPaymentTail>> LoadLoanPaymentTailsAsync(
        int[] loanIds,
        CancellationToken ct = default);

    Task<IReadOnlyList<RecurringExpense>> LockRecurringExpenseClaimsAsync(
        int[] recurringExpenseIds,
        Guid claimToken,
        DateTime businessDateUtc,
        CancellationToken ct = default);

    Task<IReadOnlyList<RecurringMaintenanceTask>> LockRecurringMaintenanceClaimsAsync(
        int[] recurringMaintenanceTaskIds,
        Guid claimToken,
        DateTime businessDateUtc,
        CancellationToken ct = default);
}

public sealed record AtomicLoanPaymentTail(int LoanId, string PeriodKey, decimal BalanceAfter);

/// <summary>
/// Transaction-scoped provider-inbox ownership boundary. PostgreSQL row-locks the event and
/// rechecks its owner, opaque claim token, and live database-clock lease before returning a tracked row.
/// </summary>
public interface IAtomicProviderInboxPersistence
{
    Task<ProviderInboxEvent?> LockOwnedAsync(
        long providerInboxEventId,
        string claimOwner,
        Guid claimToken,
        CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL-fenced state transitions for durable provider payment attempts. The claim and
/// transition execute inside the owning atomic command transaction; callers never update attempt
/// state, provider timestamps, retry facts, or claim fields through tracked EF mutations.
/// </summary>
public interface IAtomicProviderPaymentPersistence
{
    Task<Guid?> ClaimExactAsync(
        long paymentAttemptId,
        int tenantAccountId,
        int portfolioId,
        string claimOwner,
        CancellationToken ct = default);

    Task<bool> TransitionAsync(
        long paymentAttemptId,
        int tenantAccountId,
        int portfolioId,
        Guid claimToken,
        TenantPaymentAttemptState state,
        string? providerObjectId,
        string? failureCode,
        string? failureReason,
        DateTime? nextAttemptAtUtc,
        CancellationToken ct = default);
}

/// <summary>Set-based tenant-ledger operations owned by the current atomic attempt.</summary>
public interface IAtomicTenantMoneyPersistence
{
    /// <summary>
    /// Posts a bounded batch of agreement-backed scheduled rent charges. Eligibility, period
    /// generation, proration, duplicate suppression, ordering, and paging are one PostgreSQL
    /// statement; the returned rows are only the entries inserted by this attempt.
    /// </summary>
    Task<IReadOnlyList<AtomicScheduledTenantCharge>> PostScheduledRentChargesAsync(
        int batchSize,
        CancellationToken ct = default);

    /// <summary>
    /// Posts a bounded batch of late-fee charges from open rent-charge balances. Grace-period
    /// eligibility, state caps, duplicate suppression, ordering, and paging remain DB-side.
    /// </summary>
    Task<IReadOnlyList<AtomicScheduledTenantCharge>> PostScheduledLateFeesAsync(
        int batchSize,
        string stateCapsJson,
        CancellationToken ct = default);

    Task<AtomicLedgerAllocationSummary> AllocateOldestChargesAsync(
        int portfolioId,
        int tenantAccountId,
        long creditEntryId,
        decimal availableAmount,
        string businessKeyPrefix,
        int createdByUserId,
        DateTime allocatedAtUtc,
        string? entryType = null,
        CancellationToken ct = default);

    /// <summary>
    /// Allocates a bounded imported-receipt batch in one PostgreSQL statement. Deposit receipts
    /// settle deposit charges; ordinary receipts settle non-deposit charges. Ordering and all
    /// open-balance arithmetic stay database-side.
    /// </summary>
    Task<AtomicLedgerAllocationSummary> AllocateImportedReceiptsAsync(
        long[] creditEntryIds,
        DateTime allocatedAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Appends exact negative compensating rows for every live allocation attached to either side
    /// of one ledger entry. Selection and insertion are one PostgreSQL statement.
    /// </summary>
    Task<AtomicLedgerAllocationSummary> ReverseEntryAllocationsAsync(
        int portfolioId,
        int tenantAccountId,
        long ledgerEntryId,
        string businessKeyPrefix,
        int createdByUserId,
        DateTime allocatedAtUtc,
        CancellationToken ct = default);
}

public sealed class AtomicScheduledTenantCharge
{
    public long LedgerEntryId { get; set; }
    public int PortfolioId { get; set; }
    public int TenantAccountId { get; set; }
    public int LeaseAgreementId { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public DateOnly DueOn { get; set; }
    public string BusinessKey { get; set; } = string.Empty;
    public int CreatedByUserId { get; set; }
}

public sealed class AtomicLedgerAllocationSummary
{
    public int AllocationCount { get; set; }
    public decimal AllocatedAmount { get; set; }
}

/// <summary>One exact pending-blob admission expected by an atomic finalizer.</summary>
public sealed record AtomicPendingFileUploadExpectation(
    Guid Id,
    string Purpose,
    string OperationKeyHash,
    string RequestFingerprint,
    string StoragePath,
    string FileName,
    string ContentType,
    long SizeBytes);

/// <summary>
/// Locks and returns only a complete, exact prepared upload-admission set. Implementations must
/// validate the full expectation in one database statement and acquire row locks in deterministic
/// order so cleanup cannot claim or abandon an admission during finalization.
/// </summary>
public interface IAtomicPendingFileUploadPersistence
{
    Task<IReadOnlyList<PendingFileUpload>> LockPreparedSetAsync(
        int portfolioId,
        int actorScopeId,
        IReadOnlyList<AtomicPendingFileUploadExpectation> expectations,
        CancellationToken ct = default);
}

/// <summary>Small, fixed namespace of transaction-scoped aggregate locks owned by the kernel.</summary>
public enum AtomicLockResource
{
    SignatureRequest = 1,
    WorkOrder = 2,
    Conversation = 3,
    RefreshTokenFamily = 4,
    ScanDraft = 5,
    VendorDispatch = 6,
    AccountingConnection = 7,
    BankConnection = 8,
    BankTransaction = 9,
    StoredFile = 10,
    AuthSession = 11,
    LoginContextSelectionChallenge = 12,
    Unit = 13,
    LeaseManagement = 14,
    TenantAccount = 15,
    RentalApplication = 16,
    WorkspaceAccessContext = 17,
    Property = 18,
    Portfolio = 19,
    TenantIdentityEmail = 20,
    ApplicationUser = 21,
    OwnerEntity = 22,
    Tenant = 23,
    Vendor = 24,
    Inspection = 25,
    InspectionTemplate = 26,
    InspectionItem = 27,
    NoticeDraft = 28,
    OwnerDistribution = 29,
}

public enum AtomicScanDraftClaimOutcome
{
    Claimed,
    NotFound,
    NotReady,
    Rejected,
    TargetMismatch,
    StalePreparation,
    AlreadyConfirmed,
}

/// <summary>Data snapshot returned by the kernel-owned scan-draft claim operation.</summary>
public sealed record AtomicScanDraftClaim(
    AtomicScanDraftClaimOutcome Outcome,
    int PortfolioId,
    int DraftId,
    string TargetEntityType,
    int? SourceStoredFileId,
    string? ExtractedFieldsJson,
    string? SourceLabel,
    string? CanonicalEntityType,
    int? CanonicalEntityId);

/// <summary>
/// Restricted scan persistence boundary. Implementations own the EF-tracked draft/file objects and
/// transaction lock; command handlers receive only immutable facts and cannot escape to a DbContext.
/// </summary>
public interface IAtomicScanConfirmationPersistence
{
    Task<bool> CanCreateAuthorizedAsync(
        WorkspaceReadScope scope,
        string? targetEntityType,
        int? propertyId,
        DateTime utcNow,
        CancellationToken ct = default);

    Task<AtomicScanDraftClaim> TryClaimAsync(
        int portfolioId,
        int draftId,
        string expectedTargetEntityType,
        string expectedDraftFingerprint,
        int confirmedByUserId,
        CancellationToken ct = default);

    Task FinalizeAsync(
        AtomicScanDraftClaim claim,
        string entityType,
        int entityId,
        int confirmedByUserId,
        DateTime confirmedAtUtc,
        CancellationToken ct = default);

    Task<bool> RejectAuthorizedAsync(
        WorkspaceReadScope scope,
        int draftId,
        string? reason,
        DateTime rejectedAtUtc,
        CancellationToken ct = default);

    Task<bool> IsAuthorizedForReviewAsync(
        WorkspaceReadScope scope,
        int draftId,
        DateTime utcNow,
        CancellationToken ct = default);
}

/// <summary>
/// Serializes commands that must make a decision across several rows in one aggregate. The kernel
/// maps the fixed resource/id pair to a database transaction lock; handlers cannot author SQL.
/// </summary>
public interface IAtomicLockingPersistence
{
    Task AcquireAsync(AtomicLockResource resource, int aggregateId, CancellationToken ct = default);
    Task AcquireAsync(AtomicLockResource resource, Guid aggregateId, CancellationToken ct = default);
}

/// <summary>
/// Restricted persistence capability for handlers. It intentionally exposes no DbContext,
/// DatabaseFacade, connection, transaction, raw SQL, or service-provider escape hatch.
/// </summary>
public interface IAtomicPersistenceSession
{
    Guid SessionId { get; }

    /// <summary>
    /// Reads PostgreSQL's real wall clock for security eligibility checks. Business and simulation
    /// clocks must not be used to decide whether a session, membership, or assignment is live.
    /// </summary>
    Task<DateTime> ReadDatabaseClockUtcAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads one effective pre-login context through the DB-owned SECURITY DEFINER projection.
    /// This is deliberately narrower than exposing authority tables before an AuthSession exists.
    /// </summary>
    Task<AtomicEffectiveLoginContext?> ReadEffectiveLoginContextAsync(
        int userId,
        int selectedAccessContextId,
        DateTime effectiveAtUtc,
        CancellationToken ct = default);

    Task<AtomicEffectiveLoginContext?> ReadEffectiveLoginContextRootAsync(
        int userId,
        DateTime effectiveAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Revalidates an anonymous authentication transition against the live session and DB-owned
    /// effective-context projection, then establishes that exact workspace as transaction-local
    /// RLS scope. This is not a general scope override: no caller-supplied portfolio or revision is
    /// accepted, and no scope is established unless PostgreSQL resolves the complete tuple.
    /// </summary>
    Task<AtomicEffectiveLoginContext?> EstablishPreAuthenticatedWorkspaceScopeAsync(
        Guid authSessionId,
        int userId,
        int selectedAccessContextId,
        DateTime effectiveAtUtc,
        CancellationToken ct = default);

    Task<bool> IsScanDraftAuthorizedForReviewAsync(
        WorkspaceReadScope scope,
        int draftId,
        DateTime utcNow,
        CancellationToken ct = default);

    Task<bool> CanCreateScanDraftAsync(
        WorkspaceReadScope scope,
        string? targetEntityType,
        int? propertyId,
        DateTime utcNow,
        CancellationToken ct = default);

    /// <summary>Reads the simulation-aware business date for one portfolio in PostgreSQL.</summary>
    Task<DateOnly> ReadBusinessDateAsync(int portfolioId, CancellationToken ct = default);

    /// <summary>Reads one paired real-clock/business-date sample in a single SQL statement.</summary>
    Task<AtomicCommandTimes> ReadCommandTimesAsync(int portfolioId, CancellationToken ct = default);

    /// <summary>
    /// Revalidates one caller-selected experience against the canonical session, access revision,
    /// and database-owned access-envelope projection in one PostgreSQL query.
    /// </summary>
    Task<bool> IsWorkspaceExperienceAvailableAsync(
        WorkspaceReadScope scope,
        WorkspaceExperience experience,
        CancellationToken ct = default);

    IQueryable<TEntity> Query<TEntity>() where TEntity : class;
    void Add<TEntity>(TEntity entity) where TEntity : class;
    void AddRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;
    void Remove<TEntity>(TEntity entity) where TEntity : class;
}

public sealed record AtomicEffectiveLoginContext(
    int AccessContextId,
    int PortfolioId,
    long AccessRevision,
    int TotalEffectiveContexts);

public sealed record AtomicCommandTimes(DateTime WallClockUtc, DateOnly BusinessDate);

/// <summary>Database-owned mutation for the caller's current workspace display preference.</summary>
public interface IAtomicWorkspaceExperiencePersistence
{
    Task<bool> SelectAsync(
        WorkspaceReadScope scope,
        WorkspaceExperience experience,
        CancellationToken ct = default);
}

/// <summary>Exact, audited set-based mutations constructed by the persistence kernel.</summary>
public interface IAtomicSetBasedPersistence
{
    Task UpdatePropertyAsync<TEntity, TProperty>(
        int portfolioId,
        int entityId,
        AtomicSemanticAudit audit,
        Expression<Func<TEntity, TProperty>> property,
        TProperty value,
        CancellationToken ct = default)
        where TEntity : class, RentalCommand.Core.Interfaces.IAuditable, RentalCommand.Core.Interfaces.IPortfolioScoped;

    Task DeleteAsync<TEntity>(
        int portfolioId,
        int entityId,
        AtomicSemanticAudit audit,
        CancellationToken ct = default)
        where TEntity : class, RentalCommand.Core.Interfaces.IAuditable, RentalCommand.Core.Interfaces.IPortfolioScoped;
}

public interface IAtomicResultCodec<TResult>
    where TResult : notnull
{
    string ContractName { get; }
    string Serialize(TResult result);
    TResult Deserialize(string json);
}

/// <summary>Explicit JSON receipt codec with a stable, versioned contract name.</summary>
public sealed class AtomicJsonResultCodec<TResult> : IAtomicResultCodec<TResult>
    where TResult : notnull
{
    public AtomicJsonResultCodec(string contractName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);
        if (contractName.Length > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractName),
                "Result contract cannot exceed 200 characters.");
        }

        ContractName = contractName;
    }

    public string ContractName { get; }
    public string Serialize(TResult result) => JsonSerializer.Serialize(result);

    public TResult Deserialize(string json) =>
        JsonSerializer.Deserialize<TResult>(json)
        ?? throw new InvalidOperationException(
            $"Receipt result for contract '{ContractName}' deserialized to null.");
}

public enum AtomicCommandDisposition
{
    Executed,
    Replayed,
    Joined,
}

public sealed record AtomicCommandOutcome<TResult>(
    TResult Value,
    AtomicCommandDisposition Disposition,
    Guid AttemptId)
    where TResult : notnull;

/// <summary>Rich audit detail. Entity identity must match the exact mutation it enriches.</summary>
public sealed record AtomicSemanticAudit(
    int PortfolioId,
    string EntityType,
    int EntityId,
    AuditLogOperation Operation,
    int? UserId = null,
    string? ActorLabel = null,
    string? OldValues = null,
    string? NewValues = null,
    string? ChangeReason = null,
    string? IpAddress = null);

/// <summary>Exact, attempt-local handle for one tracked mutation and its audit ordinal.</summary>
public sealed record AtomicAuditMutation(
    Guid AttemptId,
    long MutationOrdinal,
    object EntityReference,
    string EntityType,
    int EntityId,
    AuditLogOperation Operation);

public sealed record AtomicBusinessFlush(
    int RowsAffected,
    IReadOnlyList<AtomicAuditMutation> Mutations);

public sealed class AtomicReceiptInvariantException : InvalidOperationException
{
    public AtomicReceiptInvariantException(string message) : base(message) { }
}

/// <summary>
/// A caller reused a command idempotency key for a different business payload. This is a stable
/// request conflict, not a receipt corruption or server failure.
/// </summary>
public sealed class AtomicIdempotencyConflictException : InvalidOperationException
{
    public AtomicIdempotencyConflictException()
        : base("The Idempotency-Key has already been used for a different request payload.") { }
}

public sealed class AtomicArchitectureException : InvalidOperationException
{
    public AtomicArchitectureException(string message) : base(message) { }
}
