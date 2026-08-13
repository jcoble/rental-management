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
        var codec = new AtomicJsonResultCodec<TResult>(write.ResultContract);
        var operation = new TransactionalWriteHandler<TCommand, TResult>(write);
        return runner.ExecuteAsync(identity, write.Request, codec, operation, ct);
    }

    private sealed class TransactionalWriteHandler<TCommand, TResult>(
        TransactionalWrite<TCommand, TResult> write)
        : IAtomicCommandHandler<TCommand, TResult>
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        public async Task<TResult> HandleAsync(
            TCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            foreach (var writeLock in write.LockPlan.Locks)
            {
                await writeLock.AcquireAsync(context, ct);
            }

            return await write.ExecuteAsync(command, context, ct);
        }

        public Task AuthorizeReplayAsync(
            TCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) =>
            write.AuthorizeReplayAsync(command, context, ct);
    }
}
