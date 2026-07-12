using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.RegularExpressions;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Blocks caller-authored raw DML and set-based writes; only kernel-owned exact operations get a lease.
/// </summary>
internal sealed class AtomicSetBasedCommandGuardInterceptor : DbCommandInterceptor
{
    private static readonly Regex RawDml = new(
        @"\b(?:(?<insert>INSERT\s+INTO)|(?<update>UPDATE)|(?<delete>DELETE\s+FROM))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
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

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Guard(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Guard(command, eventData);
        return ValueTask.FromResult(result);
    }

    private void Guard(DbCommand command, CommandEventData eventData)
    {
        if (!_scope.IsActive && _scope.AllowsUnconvertedWrites)
        {
            return;
        }

        if (eventData.CommandSource is CommandSource.ExecuteSqlRaw or CommandSource.FromSqlQuery)
        {
            var match = RawDml.Match(command.CommandText);
            if (match.Success)
            {
                var rawOperation = match.Groups["insert"].Success
                    ? AtomicRawDmlOperation.Insert
                    : match.Groups["update"].Success
                        ? AtomicRawDmlOperation.Update
                        : AtomicRawDmlOperation.Delete;
                _scope.GuardRawDml(command.CommandText, rawOperation);
            }

            return;
        }

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
