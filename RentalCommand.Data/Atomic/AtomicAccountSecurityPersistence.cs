using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;

namespace RentalCommand.Data.Atomic;

internal sealed class AtomicAccountSecurityPersistence : IAtomicAccountSecurityPersistence
{
    private readonly RentalCommandDbContext _db;

    public AtomicAccountSecurityPersistence(RentalCommandDbContext db) => _db = db;

    public async Task<AtomicInitialWorkspaceBootstrap> BootstrapInitialWorkspaceAsync(
        int userId,
        string portfolioName,
        string managementCompanyName,
        string ownerName,
        string ownerEmail,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        var row = await _db.Database.SqlQuery<InitialWorkspaceBootstrapRow>($"""
                SELECT * FROM rc_bootstrap_initial_workspace(
                    {userId}, {portfolioName}, {managementCompanyName}, {ownerName},
                    {ownerEmail}, {createdAtUtc})
                """)
            .SingleAsync(ct);
        return new AtomicInitialWorkspaceBootstrap(row.PortfolioId, row.AccessContextId);
    }

    private sealed class InitialWorkspaceBootstrapRow
    {
        public int PortfolioId { get; set; }
        public int AccessContextId { get; set; }
    }
}
