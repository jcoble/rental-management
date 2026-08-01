using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Auth;

public sealed class RevokeAuthSessionHandler
    : IAtomicCommandHandler<RevokeAuthSessionCommand, RevokeAuthSessionResult>
{
    private readonly RentalCommandDbContext _db;

    public RevokeAuthSessionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RevokeAuthSessionResult> HandleAsync(
        RevokeAuthSessionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        var session = await _db.Set<AuthSession>()
            .Include(item => item.RefreshTokenFamilies)
            .ThenInclude(item => item.Credentials)
            .SingleOrDefaultAsync(item =>
                item.Id == command.AuthSessionId &&
                item.UserId == command.UserId &&
                item.ActiveAccessContextId == command.AccessContextId,
                ct)
            ?? throw new UnauthorizedAccessException("Authentication session is unavailable.");
        var access = await _db.Set<WorkspaceAccessContext>()
            .WhereEffective()
            .Where(item => item.Id == command.AccessContextId && item.UserId == command.UserId)
            .Select(item => new { item.AccessRevision, item.PortfolioId })
            .SingleOrDefaultAsync(ct);
        if (access is null || access.AccessRevision != command.AccessRevision)
        {
            throw new UnauthorizedAccessException("The access envelope is stale.");
        }

        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = command.RevokedAtUtc;
        session.RevocationReason = command.Reason;
        foreach (var family in session.RefreshTokenFamilies.Where(item => item.RevokedAtUtc == null))
        {
            family.RevokedAtUtc = command.RevokedAtUtc;
            family.RevocationReason = command.Reason;
            foreach (var credential in family.Credentials.Where(item => item.RevokedAtUtc == null))
            {
                credential.RevokedAtUtc = command.RevokedAtUtc;
                credential.RevocationReason = command.Reason;
            }
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
        return new RevokeAuthSessionResult(true, session.Id);
    }

    public async Task AuthorizeReplayAsync(
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
