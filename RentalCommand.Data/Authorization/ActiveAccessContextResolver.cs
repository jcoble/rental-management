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
        var context = await _db.AuthSessions
            .AsNoTracking()
            .Where(session =>
                session.Id == sessionId &&
                session.UserId == userId &&
                session.ActiveAccessContextId == accessContextId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > utcNow &&
                session.ActiveAccessContext!.Status == WorkspaceAccessContextStatus.Active &&
                session.ActiveAccessContext.RevokedAtUtc == null)
            .Select(session => new ActiveAccessContext(
                session.Id,
                session.UserId,
                session.ActiveAccessContextId,
                session.ActiveAccessContext!.PortfolioId,
                session.ActiveAccessContext.AccessRevision,
                session.ActiveAccessContext.LastAuthorizedExperience,
                session.ActiveAccessContext.Membership != null &&
                session.ActiveAccessContext.Membership.Status == WorkspaceMembershipStatus.Active &&
                session.ActiveAccessContext.Membership.SuspendedAtUtc == null &&
                session.ActiveAccessContext.Membership.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership.EffectiveFromUtc <= utcNow &&
                (session.ActiveAccessContext.Membership.EffectiveToUtc == null ||
                 session.ActiveAccessContext.Membership.EffectiveToUtc > utcNow)
                    ? session.ActiveAccessContext.Membership.Id
                    : null,
                session.ActiveAccessContext.Membership != null &&
                session.ActiveAccessContext.Membership.Status == WorkspaceMembershipStatus.Active &&
                session.ActiveAccessContext.Membership.SuspendedAtUtc == null &&
                session.ActiveAccessContext.Membership.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership.EffectiveFromUtc <= utcNow &&
                (session.ActiveAccessContext.Membership.EffectiveToUtc == null ||
                 session.ActiveAccessContext.Membership.EffectiveToUtc > utcNow)
                    ? session.ActiveAccessContext.Membership.DefaultExperience
                    : null))
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
