using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Exact set-based mutation path. It constructs the portfolio/id predicate itself, executes one SQL
/// statement, and requires exactly one affected row so a broad caller query cannot slip through.
/// </summary>
public interface IAtomicSetBasedMutationExecutor
{
    Task UpdateAsync<TEntity>(
        AtomicSetBasedTarget target,
        AtomicSemanticAudit audit,
        Action<UpdateSettersBuilder<TEntity>> setters,
        CancellationToken ct = default)
        where TEntity : class, IAuditable, IPortfolioScoped;

    Task DeleteAsync<TEntity>(
        AtomicSetBasedTarget target,
        AtomicSemanticAudit audit,
        CancellationToken ct = default)
        where TEntity : class, IAuditable, IPortfolioScoped;
}

internal sealed class AtomicSetBasedMutationExecutor : IAtomicSetBasedMutationExecutor
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;
    private readonly TimeProvider _timeProvider;

    public AtomicSetBasedMutationExecutor(
        RentalCommandDbContext db,
        AtomicAuditScope scope,
        TimeProvider timeProvider)
    {
        _db = db;
        _scope = scope;
        _timeProvider = timeProvider;
    }

    public async Task UpdateAsync<TEntity>(
        AtomicSetBasedTarget target,
        AtomicSemanticAudit audit,
        Action<UpdateSettersBuilder<TEntity>> setters,
        CancellationToken ct = default)
        where TEntity : class, IAuditable, IPortfolioScoped
    {
        ArgumentNullException.ThrowIfNull(setters);
        ValidateTarget<TEntity>(target, AuditLogOperation.Updated);
        using var lease = _scope.BeginSetBasedMutation(target, audit);
        var affected = await ExactQuery<TEntity>(target).ExecuteUpdateAsync(setters, ct);
        RequireOneRow(target, affected);
        _scope.CompleteSetBasedMutation(target, audit, _timeProvider.GetUtcNow().UtcDateTime);
    }

    public async Task DeleteAsync<TEntity>(
        AtomicSetBasedTarget target,
        AtomicSemanticAudit audit,
        CancellationToken ct = default)
        where TEntity : class, IAuditable, IPortfolioScoped
    {
        ValidateTarget<TEntity>(target, AuditLogOperation.Deleted);
        using var lease = _scope.BeginSetBasedMutation(target, audit);
        var affected = await ExactQuery<TEntity>(target).ExecuteDeleteAsync(ct);
        RequireOneRow(target, affected);
        _scope.CompleteSetBasedMutation(target, audit, _timeProvider.GetUtcNow().UtcDateTime);
    }

    private IQueryable<TEntity> ExactQuery<TEntity>(AtomicSetBasedTarget target)
        where TEntity : class, IAuditable, IPortfolioScoped =>
        _db.Set<TEntity>().Where(entity =>
            EF.Property<int>(entity, "Id") == target.EntityId
            && EF.Property<int>(entity, nameof(IPortfolioScoped.PortfolioId)) == target.PortfolioId);

    private static void ValidateTarget<TEntity>(
        AtomicSetBasedTarget target,
        AuditLogOperation operation)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.EntityClrType != typeof(TEntity)
            || target.PortfolioId <= 0
            || target.EntityId <= 0
            || target.Operation != operation)
        {
            throw new ArgumentException(
                $"Set-based target must identify one exact {typeof(TEntity).Name} {operation} row.",
                nameof(target));
        }
    }

    private static void RequireOneRow(AtomicSetBasedTarget target, int affected)
    {
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Exact set-based mutation for {target.EntityClrType.Name}/{target.EntityId} affected {affected} rows.");
        }
    }
}
