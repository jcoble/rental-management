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
    // A DML keyword must own a table target. PostgreSQL row-lock clauses such as FOR UPDATE OF
    // are query operations and must not consume an UPDATE mutation permit. Likewise, the
    // UPDATE SET fragment in INSERT ... ON CONFLICT ... DO UPDATE SET belongs to the insert;
    // SET is not a second mutation target.
    private static readonly Regex RawDml = new(
        @"\b(?:(?<insert>INSERT\s+INTO)|(?<update>UPDATE)(?!\s+(?:OF|SKIP|NOWAIT|SET)\b)|(?<delete>DELETE\s+FROM))\s+(?<table>""[^""]+""|[A-Za-z_][A-Za-z0-9_$]*)",
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
            foreach (var target in ClassifyRawDmlTargets(command.CommandText))
            {
                _scope.GuardRawDml(target.TableName, target.Operation);
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

    internal static AtomicRawDmlOperation? ClassifyRawDml(string commandText)
        => ClassifyRawDmlTargets(commandText).FirstOrDefault()?.Operation;

    internal static IReadOnlyList<AtomicRawDmlTarget> ClassifyRawDmlTargets(string commandText)
    {
        return RawDml.Matches(commandText)
            .Cast<Match>()
            .Select(match => new AtomicRawDmlTarget(
                match.Groups["table"].Value.Trim('"'),
                match.Groups["insert"].Success
                    ? AtomicRawDmlOperation.Insert
                    : match.Groups["update"].Success
                        ? AtomicRawDmlOperation.Update
                        : AtomicRawDmlOperation.Delete))
            .Distinct()
            .ToArray();
    }

    private static bool Targets(string sql, IReadOnlyEntityType entityType) =>
        entityType.GetTableName() is { } table
        && sql.Contains($"\"{table}\"", StringComparison.Ordinal);
}
