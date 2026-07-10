using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace RentalCommand.Data.Atomic;

/// <summary>Rejects transaction begin/commit/rollback calls not made by the atomic executor.</summary>
internal sealed class AtomicTransactionLifecycleInterceptor : DbTransactionInterceptor
{
    private readonly AtomicAuditScope _scope;

    public AtomicTransactionLifecycleInterceptor(AtomicAuditScope scope) => _scope = scope;

    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result)
    {
        _scope.GuardTransactionLifecycle("begin");
        return result;
    }

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        _scope.GuardTransactionLifecycle("begin");
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult TransactionCommitting(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result)
    {
        _scope.GuardTransactionLifecycle("commit");
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        _scope.GuardTransactionLifecycle("commit");
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult TransactionRollingBack(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result)
    {
        _scope.GuardTransactionLifecycle("rollback");
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        _scope.GuardTransactionLifecycle("rollback");
        return ValueTask.FromResult(result);
    }
}
