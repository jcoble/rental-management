using Microsoft.EntityFrameworkCore;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// One-off backfill: gives every EXISTING portfolio that has ZERO owners a primary self-owner derived
/// from its administering user. Portfolios created before the self-owner feature never got one, so their
/// getting-started "owner" task stays stuck even though the landlord is, in fact, the owner.
///
/// Idempotent (skips any portfolio that already has an owner) and gated by configuration
/// (<c>Backfill:SelfOwners</c>, default OFF). It WRITES owner rows, so it must be explicitly enabled per
/// environment — never run unsupervised on production.
/// </summary>
public sealed class SelfOwnerBackfillService
{
    private readonly RentalCommandDbContext _db;
    private readonly ISelfOwnerProvisioner _selfOwnerProvisioner;
    private readonly ILogger<SelfOwnerBackfillService> _logger;

    public SelfOwnerBackfillService(
        RentalCommandDbContext db,
        ISelfOwnerProvisioner selfOwnerProvisioner,
        ILogger<SelfOwnerBackfillService> logger)
    {
        _db = db;
        _selfOwnerProvisioner = selfOwnerProvisioner;
        _logger = logger;
    }

    /// <summary>
    /// Creates a self-owner for every portfolio with no owner entities. Returns the number of owners
    /// created. Safe to run repeatedly.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        // Portfolios that currently have NO (non-deleted) owner entities.
        var portfolioIds = await _db.Portfolios
            .Where(p => p.DeletedAt == null && !_db.OwnerEntities.Any(o => o.PortfolioId == p.Id))
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (portfolioIds.Count == 0)
        {
            _logger.LogInformation("Self-owner backfill: no portfolios without owners; nothing to do.");
            return 0;
        }

        var created = 0;
        foreach (var portfolioId in portfolioIds)
        {
            // The administering (non-tenant) user for the portfolio supplies the owner's name/email.
            var user = await _db.Users
                .Where(u => u.WorkspaceAccessContexts.Any(context =>
                    context.PortfolioId == portfolioId && context.Membership != null))
                .OrderBy(u => u.Id)
                .FirstOrDefaultAsync(ct);

            if (user is null)
            {
                _logger.LogWarning(
                    "Self-owner backfill: portfolio {PortfolioId} has no administering user; skipped.",
                    portfolioId);
                continue;
            }

            var owner = await _selfOwnerProvisioner.EnsureSelfOwnerAsync(user, portfolioId, ct);
            if (owner != null)
            {
                created++;
            }
        }

        _logger.LogInformation(
            "Self-owner backfill complete: created {Created} self-owner(s) across {Scanned} portfolio(s) without owners.",
            created, portfolioIds.Count);
        return created;
    }
}
