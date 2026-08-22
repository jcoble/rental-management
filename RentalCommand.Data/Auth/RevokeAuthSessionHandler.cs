using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Auth;

public sealed class RevokeAuthSessionHandler
{
    private readonly RentalCommandDbContext _db;

    public RevokeAuthSessionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RevokeAuthSessionResult> ExecuteAsync(
        RevokeAuthSessionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        var access = await _db.Set<WorkspaceAccessContext>()
            .WhereEffective()
            .Where(item => item.Id == command.AccessContextId && item.UserId == command.UserId)
            .Select(item => new { item.AccessRevision, item.PortfolioId })
            .SingleOrDefaultAsync(ct);
        if (access is null || access.AccessRevision != command.AccessRevision)
        {
            throw new UnauthorizedAccessException("The access envelope is stale.");
        }

        var familyIds = _db.Set<AuthSessionRefreshTokenFamily>()
            .IgnoreQueryFilters()
            .Where(family =>
                family.AuthSessionId == command.AuthSessionId &&
                family.AuthSession!.UserId == command.UserId &&
                family.AuthSession.ActiveAccessContextId == command.AccessContextId)
            .Select(family => family.Id);

        await _db.Set<AuthSessionRefreshCredential>()
            .IgnoreQueryFilters()
            .Where(credential =>
                credential.RevokedAtUtc == null &&
                familyIds.Contains(credential.RefreshTokenFamilyId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(credential => credential.RevokedAtUtc, command.RevokedAtUtc)
                .SetProperty(credential => credential.RevocationReason, command.Reason), ct);

        await _db.Set<AuthSessionRefreshTokenFamily>()
            .IgnoreQueryFilters()
            .Where(family =>
                family.AuthSessionId == command.AuthSessionId &&
                family.AuthSession!.UserId == command.UserId &&
                family.AuthSession.ActiveAccessContextId == command.AccessContextId &&
                family.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(family => family.RevokedAtUtc, command.RevokedAtUtc)
                .SetProperty(family => family.RevocationReason, command.Reason), ct);

        var revokedSessions = await _db.Set<AuthSession>()
            .IgnoreQueryFilters()
            .Where(session =>
                session.Id == command.AuthSessionId &&
                session.UserId == command.UserId &&
                session.ActiveAccessContextId == command.AccessContextId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.Status, AuthSessionStatus.Revoked)
                .SetProperty(session => session.RevokedAtUtc, command.RevokedAtUtc)
                .SetProperty(session => session.RevocationReason, command.Reason), ct);
        if (revokedSessions != 1)
        {
            throw new UnauthorizedAccessException("Authentication session is unavailable.");
        }

        context.StageSemanticEvent(new AtomicSemanticAudit(
            access.PortfolioId,
            nameof(AuthSession),
            command.AccessContextId,
            AuditLogOperation.Updated,
            command.UserId,
            ActorLabel: "authentication:logout",
            NewValues: JsonSerializer.Serialize(new { command.AuthSessionId, command.Reason }),
            ChangeReason: "Authentication session revoked"));
        return new RevokeAuthSessionResult(true, command.AuthSessionId);
    }

    public Task AuthorizeReplayAsync(
        RevokeAuthSessionCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw AuthSessionWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        RevokeAuthSessionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.AuthSessionId == Guid.Empty || command.UserId <= 0 ||
            command.AccessContextId <= 0 || command.AccessRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);

        var authorized = await _db.Set<AuthSession>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(session =>
                session.Id == command.AuthSessionId &&
                session.UserId == command.UserId &&
                session.ActiveAccessContextId == command.AccessContextId &&
                _db.Set<WorkspaceAccessContext>()
                    .IgnoreQueryFilters()
                    .Any(access =>
                        access.Id == command.AccessContextId &&
                        access.UserId == command.UserId &&
                        access.AccessRevision == command.AccessRevision),
                ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException("The original authentication session ownership is unavailable.");
        }
    }
}
