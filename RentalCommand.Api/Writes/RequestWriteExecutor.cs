using RentalCommand.Core.Atomic;

namespace RentalCommand.Api.Writes;

/// <summary>HTTP request adapter for the shared local write executor.</summary>
public interface IRequestWriteExecutor
{
    Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        string idempotencyKey,
        TransactionalWrite<TCommand, TResult> write,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull;
}

[WriteEntryPoint(WriteEntryPointKind.Transactional)]
internal sealed class RequestWriteExecutor(IWriteExecutor executor) : IRequestWriteExecutor
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

        return executor.ExecuteAsync(normalizedKey, write, ct);
    }
}
