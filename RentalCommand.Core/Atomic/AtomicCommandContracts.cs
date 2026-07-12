using System.Text.Json;
using System.Linq.Expressions;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Accounting;
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
    bool AllowsUnconvertedWrites { get; }
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
    IAtomicPendingFileUploadPersistence PendingFileUploads { get; }

    /// <summary>Flushes tracked business rows while the owner transaction remains open.</summary>
    Task<AtomicBusinessFlush> FlushBusinessAsync(CancellationToken ct = default);

    /// <summary>
    /// Binds semantic detail to this exact tracked object before its next flush. No tuple lookup or
    /// entity-wide permit is used; the subsequent mutation descriptor carries the same reference.
    /// </summary>
    void BindSemanticAudit(object entityReference, AtomicSemanticAudit audit);

    /// <summary>Enriches only the exact mutation descriptor returned by a business flush.</summary>
    void EnrichMutation(AtomicAuditMutation mutation, AtomicSemanticAudit audit);

    /// <summary>Stages an explicit semantic event that is not a tracked-entity mutation.</summary>
    void StageSemanticEvent(AtomicSemanticAudit audit);

    /// <summary>Stages an outbox companion for the owner's final flush.</summary>
    void StageOutbox(OutboxMessage message);
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
    string? CanonicalEntityType,
    int? CanonicalEntityId);

/// <summary>
/// Restricted scan persistence boundary. Implementations own the EF-tracked draft/file objects and
/// transaction lock; command handlers receive only immutable facts and cannot escape to a DbContext.
/// </summary>
public interface IAtomicScanConfirmationPersistence
{
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
    IQueryable<TEntity> Query<TEntity>() where TEntity : class;
    void Add<TEntity>(TEntity entity) where TEntity : class;
    void AddRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;
    void Remove<TEntity>(TEntity entity) where TEntity : class;
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

public sealed class AtomicArchitectureException : InvalidOperationException
{
    public AtomicArchitectureException(string message) : base(message) { }
}
