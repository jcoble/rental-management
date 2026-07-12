using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

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
            .WhereEffectiveAccess(
                _db.WorkspaceMemberships.AsNoTracking(),
                _db.MembershipRoleAssignments.AsNoTracking(),
                _db.OwnerUserAccesses.AsNoTracking(),
                _db.EffectiveTenantAccess.AsNoTracking(),
                userId,
                utcNow);
        var effectiveAssignments = _db.MembershipRoleAssignments
            .AsNoTracking()
            .WhereEffective(utcNow);

        return await effectiveContexts
            .OrderBy(context => context.Portfolio!.Name)
            .ThenBy(context => context.Id)
            .Select(context => new EffectiveAccessContextOption(
                context.Id,
                context.PortfolioId,
                context.Portfolio!.Name,
                context.AccessRevision,
                context.Membership != null && effectiveAssignments
                    .Where(assignment =>
                        assignment.WorkspaceMembershipId == context.Membership!.Id &&
                        assignment.PortfolioId == context.PortfolioId)
                    .Select(assignment => assignment.RoleProfile!.DefaultExperience)
                    .Distinct()
                    .Contains(context.Membership!.DefaultExperience)
                    ? context.Membership.DefaultExperience
                    : context.Membership != null && effectiveAssignments.Any(assignment =>
                        assignment.WorkspaceMembershipId == context.Membership.Id &&
                        assignment.PortfolioId == context.PortfolioId)
                        ? effectiveAssignments
                            .Where(assignment =>
                                assignment.WorkspaceMembershipId == context.Membership!.Id &&
                                assignment.PortfolioId == context.PortfolioId)
                            .Select(assignment => assignment.RoleProfile!.DefaultExperience)
                            .Distinct()
                            .OrderBy(experience =>
                                experience == WorkspaceExperience.Management ? 1 :
                                experience == WorkspaceExperience.Leasing ? 2 :
                                experience == WorkspaceExperience.Maintenance ? 3 :
                                experience == WorkspaceExperience.Owner ? 4 : 5)
                            .First()
                        : _db.OwnerUserAccesses.Any(access =>
                            access.AccessContextId == context.Id && access.PortfolioId == context.PortfolioId &&
                            access.RevokedAtUtc == null && access.EffectiveFromUtc <= utcNow &&
                            (access.EffectiveToUtc == null || access.EffectiveToUtc > utcNow))
                            ? WorkspaceExperience.Owner
                            : WorkspaceExperience.Tenant,
                effectiveContexts.Count()))
            .ToListAsync(cancellationToken);
    }
}
