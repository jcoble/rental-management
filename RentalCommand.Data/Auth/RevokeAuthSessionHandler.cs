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
    public async Task<RevokeAuthSessionResult> HandleAsync(
        RevokeAuthSessionCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        var session = await attempt.Persistence.Query<AuthSession>()
            .Include(item => item.RefreshTokenFamilies)
            .ThenInclude(item => item.Credentials)
            .SingleOrDefaultAsync(item =>
                item.Id == command.AuthSessionId &&
                item.UserId == command.UserId &&
                item.ActiveAccessContextId == command.AccessContextId,
                ct)
            ?? throw new UnauthorizedAccessException("Authentication session is unavailable.");
        var access = await attempt.Persistence.Query<WorkspaceAccessContext>()
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

        attempt.StageSemanticEvent(new AtomicSemanticAudit(
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
}
