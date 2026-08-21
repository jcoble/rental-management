using RentalCommand.Core.Atomic;
using RentalCommand.Engine.Writes;

namespace RentalCommand.Engine.Tests.Automation;

internal sealed class CapturingJobStepWriteExecutor : IJobStepWriteExecutor
{
    private readonly object _result;
    private readonly Exception? _exception;

    public CapturingJobStepWriteExecutor(object result) => _result = result;

    public CapturingJobStepWriteExecutor(Exception exception)
    {
        _result = new object();
        _exception = exception;
    }

    public object? Command { get; private set; }
    public string? StepKey { get; private set; }
    public string? OperationName { get; private set; }
    public string? ResultContract { get; private set; }

    public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        string stepKey,
        TransactionalWrite<TCommand, TResult> write,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        if (_exception is not null)
        {
            throw _exception;
        }

        StepKey = stepKey;
        Command = write.Request;
        OperationName = write.OperationName;
        ResultContract = write.ResultContract;
        return Task.FromResult(new AtomicCommandOutcome<TResult>(
            (TResult)_result,
            AtomicCommandDisposition.Executed,
            Guid.NewGuid()));
    }
}
