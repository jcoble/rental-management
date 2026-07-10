using System.Text.Json;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Atomic;

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
        where TCommand : notnull
        where TResult : notnull;
}

/// <summary>Application command code resolved once per physical execution-strategy attempt.</summary>
public interface IAtomicCommandHandler<in TCommand, TResult>
    where TCommand : notnull
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

/// <summary>Capabilities owned by the current physical attempt; it deliberately exposes no ORM.</summary>
public interface IAtomicWriteAttempt
{
    Guid AttemptId { get; }
    Guid AuditScopeId { get; }

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
    private readonly JsonSerializerOptions? _options;

    public AtomicJsonResultCodec(string contractName, JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractName);
        if (contractName.Length > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractName),
                "Result contract cannot exceed 200 characters.");
        }

        ContractName = contractName;
        _options = options;
    }

    public string ContractName { get; }
    public string Serialize(TResult result) => JsonSerializer.Serialize(result, _options);

    public TResult Deserialize(string json) =>
        JsonSerializer.Deserialize<TResult>(json, _options)
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
