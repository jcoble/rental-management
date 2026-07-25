using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RentalCommand.Core.Atomic;
using RentalCommand.Data;

namespace RentalCommand.TestCommon;

/// <summary>
/// Lightweight test boundary for service-level tests that do not attach the production atomic
/// interceptors. It preserves the production ownership rule: one outer transaction, nested joins,
/// and one final flush.
/// </summary>
public sealed class TestAtomicInfrastructureUnitOfWork :
    IAtomicInfrastructureUnitOfWork,
    IAtomicExecutionState
{
    private readonly RentalCommandDbContext _db;
    private int _depth;

    public TestAtomicInfrastructureUnitOfWork(RentalCommandDbContext db) => _db = db;

    public bool IsActive => false;
    public bool IsInfrastructureActive => _depth > 0;
    public bool AllowsUnconvertedWrites => false;

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
        if (_depth > 0)
        {
            return await action(ct);
        }

        IDbContextTransaction? transaction = null;
        _depth++;
        try
        {
            if (_db.Database.IsRelational())
            {
                transaction = await _db.Database.BeginTransactionAsync(ct);
            }
            var result = await action(ct);
            await _db.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            _depth--;
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
