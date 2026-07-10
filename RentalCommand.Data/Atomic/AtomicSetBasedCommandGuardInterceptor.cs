using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Atomic;

/// <summary>Blocks raw set-based DML on auditable tables; only the exact executor gets a lease.</summary>
internal sealed class AtomicSetBasedCommandGuardInterceptor : DbCommandInterceptor
{
    private readonly AtomicAuditScope _scope;

    public AtomicSetBasedCommandGuardInterceptor(AtomicAuditScope scope) => _scope = scope;

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Guard(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(command, eventData);
        return ValueTask.FromResult(result);
    }

    private void Guard(DbCommand command, CommandEventData eventData)
    {
        var operation = eventData.CommandSource switch
        {
            CommandSource.ExecuteUpdate => AuditLogOperation.Updated,
            CommandSource.ExecuteDelete => AuditLogOperation.Deleted,
            _ => (AuditLogOperation?)null,
        };
        if (operation is null || eventData.Context is null)
        {
            return;
        }

        var entityType = eventData.Context.Model.GetEntityTypes()
            .FirstOrDefault(type => typeof(IAuditable).IsAssignableFrom(type.ClrType)
                && Targets(command.CommandText, type));
        if (entityType is not null)
        {
            _scope.GuardSetBasedCommand(entityType.ClrType, operation.Value);
        }
    }

    private static bool Targets(string sql, IReadOnlyEntityType entityType) =>
        entityType.GetTableName() is { } table
        && sql.Contains($"\"{table}\"", StringComparison.Ordinal);
}
