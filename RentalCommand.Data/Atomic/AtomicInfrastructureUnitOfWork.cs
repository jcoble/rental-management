using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Kernel-owned transaction boundary for a small allowlist of platform infrastructure workflows.
/// Unlike business commands these operations have no meaningful caller retry identity, so they do
/// not create command receipts. Auditable tracked mutations still produce canonical atomic audit
/// rows, and every nested save participates in the same transaction.
/// </summary>
internal sealed class AtomicInfrastructureUnitOfWork : IAtomicInfrastructureUnitOfWork
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _auditScope;

    public AtomicInfrastructureUnitOfWork(
        RentalCommandDbContext db,
        AtomicAuditScope auditScope)
    {
        _db = db;
        _auditScope = auditScope;
    }

    public Task ExecuteAsync(
        AtomicInfrastructureOperation operation,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default) =>
        ExecuteAsync<object?>(operation, async innerCt =>
        {
            await action(innerCt);
            return null;
        }, ct);

    public async Task<TResult> ExecuteAsync<TResult>(
        AtomicInfrastructureOperation operation,
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_auditScope.IsInfrastructureActive)
        {
            return await action(ct);
        }
        if (_auditScope.IsActive)
        {
            throw new AtomicArchitectureException(
                "Infrastructure workflows cannot be opened inside receipt-backed business commands.");
        }

        var attemptId = Guid.NewGuid();
        using var attemptLease = _auditScope.BeginInfrastructureAttempt(operation, attemptId);
        IDbContextTransaction? transaction = null;
        try
        {
            using (_auditScope.BeginExecutorTransactionLifecycle())
            {
                transaction = await _db.Database.BeginTransactionAsync(ct);
            }

            var result = await action(ct);
            await _db.SaveChangesAsync(ct);

            var auditRows = _auditScope.TakeRows();
            if (auditRows.Count > 0)
            {
                _db.AtomicAuditLogs.AddRange(auditRows);
                await _db.SaveChangesAsync(ct);
            }

            using (_auditScope.BeginExecutorTransactionLifecycle())
            {
                await transaction.CommitAsync(ct);
            }
            return result;
        }
        catch
        {
            if (transaction is not null && _db.Database.CurrentTransaction is not null)
            {
                try
                {
                    using (_auditScope.BeginExecutorTransactionLifecycle())
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                }
                catch
                {
                    // Preserve the workflow failure; disposal still releases the transaction.
                }
            }

            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
