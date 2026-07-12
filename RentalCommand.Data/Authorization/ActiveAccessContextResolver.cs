using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Resolves session, selected context, workspace, current revision, and effective membership in one
/// translated database projection. The caller-provided revision is then compared with the projected
/// authoritative value so stale mutations fail distinctly from unavailable sessions.
/// </summary>
public sealed class ActiveAccessContextResolver : IActiveAccessContextResolver
{
    private readonly RentalCommandDbContext _db;

    public ActiveAccessContextResolver(RentalCommandDbContext db) => _db = db;

    public async Task<ActiveAccessContext> ResolveAsync(
        Guid sessionId,
        int userId,
        int accessContextId,
        long presentedAccessRevision,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        var effectiveContexts = _db.WorkspaceAccessContexts.AsNoTracking().WhereEffectiveAccess(
            _db.WorkspaceMemberships.AsNoTracking(),
            _db.MembershipRoleAssignments.AsNoTracking(),
            _db.OwnerUserAccesses.AsNoTracking(),
            _db.EffectiveTenantAccess.AsNoTracking(),
            userId,
            utcNow);
        var effectiveMemberships = _db.WorkspaceMemberships.AsNoTracking().WhereEffective(utcNow);

        var context = await (
                from session in _db.AuthSessions.AsNoTracking()
                join accessContext in effectiveContexts
                    on new { Id = session.ActiveAccessContextId, session.UserId }
                    equals new { accessContext.Id, accessContext.UserId }
                join membership in effectiveMemberships
                    on accessContext.Id equals membership.AccessContextId into memberships
                from membership in memberships.DefaultIfEmpty()
                where session.Id == sessionId &&
                      session.UserId == userId &&
                      session.ActiveAccessContextId == accessContextId &&
                      session.Status == AuthSessionStatus.Active &&
                      session.RevokedAtUtc == null &&
                      session.ExpiresAtUtc > utcNow
                select new ActiveAccessContext(
                    session.Id,
                    session.UserId,
                    accessContext.Id,
                    accessContext.PortfolioId,
                    accessContext.AccessRevision,
                    accessContext.LastAuthorizedExperience,
                    membership == null ? null : membership.Id,
                    membership == null ? null : membership.DefaultExperience))
            .SingleOrDefaultAsync(cancellationToken);

        if (context is null)
        {
            throw new AccessContextUnavailableException();
        }

        if (context.AccessRevision != presentedAccessRevision)
        {
            throw new StaleAccessRevisionException(presentedAccessRevision, context.AccessRevision);
        }

        return context;
    }
}
