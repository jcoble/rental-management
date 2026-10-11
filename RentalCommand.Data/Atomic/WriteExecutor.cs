using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Runs delegate-style application writes through the existing atomic transaction runner.
/// Receipt, fingerprint, replay, audit, outbox, raw-write, and transaction behavior remain owned
/// by that runner and its scoped collaborators.
/// </summary>
internal sealed class WriteExecutor(AtomicTransactionRunner runner) : IWriteExecutor
{
    public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        string idempotencyKey,
        TransactionalWrite<TCommand, TResult> write,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var normalizedKey = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 200)
        {
            throw new ArgumentException(
                "A request key is required and cannot exceed 200 characters.",
                nameof(idempotencyKey));
        }
        ArgumentNullException.ThrowIfNull(write);

        var identity = new AtomicCommandIdentity(write.OperationName, normalizedKey);
        return runner.ExecuteAsync(identity, write, ct);
    }
}
