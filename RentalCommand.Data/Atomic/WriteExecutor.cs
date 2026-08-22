using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Runs delegate-style application writes through the existing atomic transaction runner.
/// Receipt, fingerprint, replay, audit, outbox, raw-write, and transaction behavior remain owned
/// by that runner and its scoped collaborators.
/// </summary>
[WriteEntryPoint(WriteEntryPointKind.Transactional)]
internal sealed class WriteExecutor(AtomicTransactionRunner runner) : IWriteExecutor
{
    public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        string idempotencyKey,
        TransactionalWrite<TCommand, TResult> write,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(write);
        if (write.IdempotencyPolicy != WriteIdempotencyPolicy.Required)
        {
            throw new AtomicArchitectureException("Local writes require receipt-backed idempotency.");
        }

        var identity = new AtomicCommandIdentity(write.OperationName, idempotencyKey);
        return runner.ExecuteAsync(identity, write, ct);
    }
}
