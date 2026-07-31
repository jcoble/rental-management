using RentalCommand.Core.Atomic;

namespace RentalCommand.Engine.Tests.Automation;

internal sealed class CapturingAtomicUnitOfWork : IAtomicUnitOfWork
{
    private readonly object _result;
    private readonly Exception? _exception;

    public CapturingAtomicUnitOfWork(object result) => _result = result;

    public CapturingAtomicUnitOfWork(Exception exception)
    {
        _result = new object();
        _exception = exception;
    }

    public object? Command { get; private set; }

    public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        if (_exception is not null)
        {
            throw _exception;
        }

        Command = command;
        return Task.FromResult(new AtomicCommandOutcome<TResult>(
            (TResult)_result,
            AtomicCommandDisposition.Executed,
            Guid.NewGuid()));
    }
}
