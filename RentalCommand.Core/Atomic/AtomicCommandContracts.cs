using System.Text.Json;

namespace RentalCommand.Core.Atomic;

/// <summary>Explicit marker for immutable, service-free command DTOs.</summary>
public interface IAtomicCommandData;

/// <summary>
/// Receipt-backed transaction boundary over the caller's scoped database context.
/// </summary>
public interface IAtomicUnitOfWork
{
    Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull;
}

/// <summary>Application command code executed on the same scoped context as the transaction owner.</summary>
public interface IAtomicCommandHandler<in TCommand, TResult>
    where TCommand : notnull, IAtomicCommandData
    where TResult : notnull
{
    Task<TResult> HandleAsync(
        TCommand command,
        IAtomicCommandContext context,
        CancellationToken ct);

    Task AuthorizeReplayAsync(
        TCommand command,
        IAtomicCommandContext context,
        CancellationToken ct);
}

public sealed record AtomicSqlMutationTarget(
    string TableName,
    AtomicSqlMutationOperation Operation);

public enum AtomicSqlMutationOperation
{
    Insert,
    Update,
    Delete,
}

/// <summary>Explicit JSON receipt codec with a stable, versioned contract name.</summary>
public sealed class AtomicJsonResultCodec<TResult>
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
}

public sealed record AtomicCommandOutcome<TResult>(
    TResult Value,
    AtomicCommandDisposition Disposition,
    Guid AttemptId)
    where TResult : notnull;

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
