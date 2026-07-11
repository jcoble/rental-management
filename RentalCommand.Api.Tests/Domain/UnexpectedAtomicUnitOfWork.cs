using RentalCommand.Core.Atomic;

namespace RentalCommand.Api.Tests.Domain;

internal sealed class UnexpectedAtomicUnitOfWork : IAtomicUnitOfWork
{
    public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        IAtomicResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull =>
        throw new InvalidOperationException(
            $"Atomic command '{identity.CommandType}' was not expected in this short-circuit test.");
}
