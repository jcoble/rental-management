using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Auditing;

/// <summary>
/// Rejects set-based DML against auditable tables unless the atomic attempt staged a semantic audit
/// first and granted a one-shot permit through IAtomicWriteAttempt.StageSetBasedAuditAsync.
/// ExecuteUpdate/ExecuteDelete bypass the change tracker, so the generic audit interceptor cannot
/// infer row identity or old/new values for them.
/// </summary>
public sealed class AuditableCommandGuardInterceptor : DbCommandInterceptor
{
    private readonly IAuditScope _scope;

    public AuditableCommandGuardInterceptor(IAuditScope scope)
    {
        _scope = scope;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Guard(command, eventData);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(command, eventData);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Guard(DbCommand command, CommandEventData eventData)
    {
        if (eventData.CommandSource is not (
            CommandSource.ExecuteUpdate
            or CommandSource.ExecuteDelete
            or CommandSource.BulkUpdate))
        {
            return;
        }

        var context = eventData.Context;
        if (context is null)
        {
            return;
        }

        var entityType = context.Model.GetEntityTypes()
            .Where(type => typeof(IAuditable).IsAssignableFrom(type.ClrType))
            .FirstOrDefault(type => CommandTargets(command.CommandText, type));
        if (entityType is null)
        {
            return;
        }

        var name = entityType.ClrType.Name;
        if (!_scope.IsActive)
        {
            throw new InvalidOperationException(
                $"Set-based write to auditable entity {name} is outside IAtomicUnitOfWork.");
        }

        if (!_scope.TryConsumeSetBasedMutation(name))
        {
            throw new InvalidOperationException(
                $"ExecuteUpdate/ExecuteDelete for auditable entity {name} requires a semantic " +
                "audit staged first through IAtomicWriteAttempt.StageSetBasedAuditAsync.");
        }
    }

    private static bool CommandTargets(string commandText, IReadOnlyEntityType entityType)
    {
        var tableName = entityType.GetTableName();
        return tableName is not null
            && commandText.Contains($"\"{tableName}\"", StringComparison.Ordinal);
    }
}
