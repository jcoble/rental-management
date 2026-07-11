using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Data.Authorization;

/// <summary>One SQL projection for all effective login contexts belonging to a verified user.</summary>
public sealed class EffectiveAccessContextSelectionQuery : IEffectiveAccessContextSelectionQuery
{
    private readonly RentalCommandDbContext _db;

    public EffectiveAccessContextSelectionQuery(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<EffectiveAccessContextOption>> ListAsync(
        int userId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return Array.Empty<EffectiveAccessContextOption>();
        }

        var effectiveContexts = _db.WorkspaceAccessContexts
            .AsNoTracking()
            .WhereEffectiveTeamAccess(
                _db.WorkspaceMemberships.AsNoTracking(),
                _db.MembershipRoleAssignments.AsNoTracking(),
                userId,
                utcNow);

        return await effectiveContexts
            .OrderBy(context => context.Portfolio!.Name)
            .ThenBy(context => context.Id)
            .Select(context => new EffectiveAccessContextOption(
                context.Id,
                context.PortfolioId,
                context.Portfolio!.Name,
                context.AccessRevision,
                context.Membership!.DefaultExperience,
                effectiveContexts.Count()))
            .ToListAsync(cancellationToken);
    }
}
