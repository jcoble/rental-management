using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

/// <summary>Guarded raw-SQL helpers on the actual scoped DbContext owned by the command.</summary>
public static class AtomicDbContextExtensions
{
    public static Task<List<TResult>> QuerySqlAsync<TResult>(
        this RentalCommandDbContext db,
        FormattableString sql,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sql);
        return db.Database.SqlQuery<TResult>(sql).ToListAsync(ct);
    }

    public static async Task<List<TResult>> ExecuteAtomicSqlMutationAsync<TResult>(
        this RentalCommandDbContext db,
        IAtomicCommandContext context,
        FormattableString sql,
        IReadOnlyCollection<AtomicSqlMutationTarget> mutationTargets,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(mutationTargets);

        if (context is not AtomicCommandContext owner || !owner.Owns(db) || !context.IsActive)
        {
            throw new AtomicArchitectureException(
                "Atomic SQL mutation requires the exact scoped DbContext and active command context.");
        }

        var declaredTargets = mutationTargets.Select(ToRawDmlTarget).ToArray();
        var parsedTargets = AtomicSetBasedCommandGuardInterceptor
            .ClassifyRawDmlTargets(sql.Format)
            .ToArray();
        if (declaredTargets.Length == 0 || !RawDmlTargetsEqual(declaredTargets, parsedTargets))
        {
            throw new AtomicArchitectureException(
                "Atomic SQL mutation targets must exactly match the parsed raw-DML target set.");
        }

        using var lease = owner.AuditScope.BeginInternalRawDmlBatch(declaredTargets);
        return await db.Database.SqlQuery<TResult>(sql).ToListAsync(ct);
    }

    private static AtomicRawDmlTarget ToRawDmlTarget(AtomicSqlMutationTarget target) =>
        new(
            target.TableName,
            target.Operation switch
            {
                AtomicSqlMutationOperation.Insert => AtomicRawDmlOperation.Insert,
                AtomicSqlMutationOperation.Update => AtomicRawDmlOperation.Update,
                AtomicSqlMutationOperation.Delete => AtomicRawDmlOperation.Delete,
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            });

    private static bool RawDmlTargetsEqual(
        IEnumerable<AtomicRawDmlTarget> declaredTargets,
        IEnumerable<AtomicRawDmlTarget> parsedTargets)
    {
        static IEnumerable<string> Normalize(IEnumerable<AtomicRawDmlTarget> targets) =>
            targets
                .Select(target => $"{target.Operation}:{target.TableName}")
                .Order(StringComparer.Ordinal);

        return Normalize(declaredTargets).SequenceEqual(Normalize(parsedTargets));
    }
}
