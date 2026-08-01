using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Atomic;

/// <summary>
/// Attempt metadata and commit-coupled audit/outbox operations. Domain handlers inject the actual
/// scoped RentalCommandDbContext directly; this contract is not a persistence/session abstraction.
/// </summary>
public interface IAtomicCommandContext
{
    bool IsActive { get; }
    Guid AttemptId { get; }
    Guid AtomicReceiptId { get; }
    DateTime BusinessNowUtc { get; }
    Task<DateTime> ReadDatabaseClockUtcAsync(CancellationToken ct = default);
    Task AcquireLockAsync(string lockNamespace, int aggregateId, CancellationToken ct = default);
    Task AcquireLockAsync(string lockNamespace, Guid aggregateId, CancellationToken ct = default);

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
