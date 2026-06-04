using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data;

/// <summary>
/// Default <see cref="ISandboxGuard"/>: reads <c>Portfolio.IsSandbox</c> for the given portfolio id.
/// Lives in Data so both the API (Stripe/e-sign guards) and the Engine (outbox dispatch guard) share
/// one implementation over the same <see cref="RentalCommandDbContext"/>. Fail-open: an unknown/unscoped
/// portfolio is treated as Live (not sandbox) so a genuine system message is never silently dropped.
/// </summary>
public sealed class SandboxGuard : ISandboxGuard
{
    private readonly RentalCommandDbContext _db;

    public SandboxGuard(RentalCommandDbContext db) => _db = db;

    public async Task<bool> IsSandboxAsync(int? portfolioId, CancellationToken ct = default)
    {
        if (portfolioId is not int id || id <= 0)
        {
            return false;
        }

        // AnyAsync(IsSandbox) returns false for both "Live" and "no such portfolio" — exactly the
        // fail-open-to-Live behaviour we want.
        return await _db.Portfolios.AnyAsync(p => p.Id == id && p.IsSandbox, ct);
    }
}
