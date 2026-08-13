using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Resolves the command's ordinary scoped handler, then hands it to the transaction runner.
/// Handler discovery is dependency-injection wiring, not part of the transaction kernel.
/// </summary>
[WriteEntryPoint(WriteEntryPointKind.LegacyAtomic)]
internal sealed class AtomicUnitOfWork : IAtomicUnitOfWork
{
    private readonly IServiceProvider _services;
    private readonly AtomicTransactionRunner _runner;

    public AtomicUnitOfWork(
        IServiceProvider services,
        AtomicTransactionRunner runner)
    {
        _services = services;
        _runner = runner;
    }

    public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var handler = _services.GetRequiredService<IAtomicCommandHandler<TCommand, TResult>>();
        return _runner.ExecuteAsync(identity, command, resultCodec, handler, ct);
    }
}
