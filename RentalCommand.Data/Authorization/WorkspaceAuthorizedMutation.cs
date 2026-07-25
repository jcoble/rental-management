using Microsoft.EntityFrameworkCore.Storage;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Keeps the authoritative session/revision/capability/property proof and the protected write in
/// the same database transaction. Callers must execute the authorized query again inside
/// <paramref name="mutation"/>; a controller pre-check is never sufficient for a write.
/// </summary>
public static class WorkspaceAuthorizedMutation
{
    public static async Task<TResult> ExecuteAuthorizedMutationAsync<TResult>(
        this RentalCommandDbContext db,
        Func<CancellationToken, Task<TResult>> mutation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        if (db.Database.CurrentTransaction is not null)
        {
            return await mutation(ct);
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await mutation(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
