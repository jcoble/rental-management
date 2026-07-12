using RentalCommand.Core.Atomic;

namespace RentalCommand.Engine.Tests.Automation;

internal sealed class CapturingAtomicUnitOfWork : IAtomicUnitOfWork
{
    private readonly object _result;

    public CapturingAtomicUnitOfWork(object result) => _result = result;

    public object? Command { get; private set; }

    public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        IAtomicResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        Command = command;
        return Task.FromResult(new AtomicCommandOutcome<TResult>(
            (TResult)_result,
            AtomicCommandDisposition.Executed,
            Guid.NewGuid()));
    }
}
