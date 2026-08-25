using RentalCommand.Core.Atomic;

namespace RentalCommand.Engine.Writes;

/// <summary>Engine job-step adapter for the shared local write executor.</summary>
public interface IJobStepWriteExecutor
{
    Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        string stepKey,
        TransactionalWrite<TCommand, TResult> write,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull;
}

internal sealed class JobStepWriteExecutor(IWriteExecutor executor) : IJobStepWriteExecutor
{
    public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        string stepKey,
        TransactionalWrite<TCommand, TResult> write,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var normalizedKey = stepKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 200)
        {
            throw new ArgumentException(
                "A job-step key is required and cannot exceed 200 characters.",
                nameof(stepKey));
        }

        return executor.ExecuteAsync(normalizedKey, write, ct);
    }
}
