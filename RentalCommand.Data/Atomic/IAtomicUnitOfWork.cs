using System.Text.Json;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Executes a database-only callback under a retry-safe explicit transaction. The callback must use
/// <see cref="IAtomicWriteAttempt.DbContext"/>; capturing a request DbContext would reuse a poisoned
/// tracker after a transient failure and is forbidden.
/// </summary>
public interface IAtomicUnitOfWork
{
    Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TResult>(
        AtomicCommandIdentity command,
        IAtomicResultCodec<TResult> resultCodec,
        Func<IAtomicWriteAttempt, CancellationToken, Task<TResult>> dbOnlyCallback,
        CancellationToken ct = default)
        where TResult : notnull;

    /// <summary>
    /// Persists a semantic audit as its own idempotent atomic command. This is the only supported
    /// audit-only durability path; <see cref="IAuditTrailService.LogAsync"/> itself only stages.
    /// </summary>
    Task<AtomicCommandOutcome<AtomicAuditCommandResult>> ExecuteAuditOnlyAsync(
        AtomicCommandIdentity command,
        AtomicAuditEntry audit,
        CancellationToken ct = default);
}

public interface IAtomicWriteAttempt
{
    Guid AttemptId { get; }
    RentalCommandDbContext DbContext { get; }
    IAuditTrailService AuditTrail { get; }

    /// <summary>
    /// Flushes tracked business state to obtain generated values. The owner transaction remains open,
    /// and generated audit rows remain attempt-staged until the final companion flush.
    /// </summary>
    Task<AtomicBusinessFlush> FlushBusinessAsync(CancellationToken ct = default);

    /// <summary>Stages an outbox companion for the owner's final flush.</summary>
    void StageOutbox(OutboxMessage message);

    /// <summary>
    /// Stages the required semantic audit and grants exactly one subsequent ExecuteUpdate or
    /// ExecuteDelete against <paramref name="entityType"/>. Stage first, then execute the set-based DML.
    /// </summary>
    Task StageSetBasedAuditAsync(
        string entityType,
        AtomicAuditEntry audit,
        CancellationToken ct = default);

    /// <summary>Enriches exactly the mutation returned by the corresponding business flush.</summary>
    Task StageExactAuditAsync(
        AuditMutationDescriptor mutation,
        AtomicAuditEntry audit,
        CancellationToken ct = default);
}

public interface IAtomicResultCodec<TResult>
    where TResult : notnull
{
    /// <summary>Stable versioned result contract persisted in the command receipt.</summary>
    string ContractName { get; }
    string Serialize(TResult result);
    TResult Deserialize(string json);
}

/// <summary>Explicit JSON receipt codec; callers must choose a stable, versioned contract name.</summary>
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

public sealed record AtomicAuditEntry(
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

public sealed record AtomicAuditCommandResult(string CommandType, string IdempotencyKey);

public sealed record AtomicBusinessFlush(
    int RowsAffected,
    IReadOnlyList<AuditMutationDescriptor> Mutations);

public sealed class AtomicReceiptInvariantException : InvalidOperationException
{
    public AtomicReceiptInvariantException(string message) : base(message) { }
}
