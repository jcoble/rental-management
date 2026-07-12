using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Auth;

public sealed class SwitchAuthSessionContextHandler
    : IAtomicCommandHandler<SwitchAuthSessionContextCommand, SwitchAuthSessionContextResult>
{
    public async Task<SwitchAuthSessionContextResult> HandleAsync(
        SwitchAuthSessionContextCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.AuthSessionId == Guid.Empty || command.UserId <= 0 ||
            command.CurrentAccessContextId <= 0 || command.CurrentAccessRevision <= 0 ||
            command.SelectedAccessContextId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        var session = await attempt.Persistence.Query<AuthSession>()
            .SingleOrDefaultAsync(item =>
                item.Id == command.AuthSessionId &&
                item.UserId == command.UserId &&
                item.ActiveAccessContextId == command.CurrentAccessContextId &&
                item.Status == AuthSessionStatus.Active &&
                item.RevokedAtUtc == null &&
                item.ExpiresAtUtc > command.ChangedAtUtc,
                ct)
            ?? throw new UnauthorizedAccessException("The current authentication session is unavailable.");

        var currentRevision = await attempt.Persistence.Query<WorkspaceAccessContext>()
            .WhereEffective()
            .Where(item => item.Id == command.CurrentAccessContextId && item.UserId == command.UserId)
            .Select(item => (long?)item.AccessRevision)
            .SingleOrDefaultAsync(ct);
        if (currentRevision != command.CurrentAccessRevision)
        {
            throw new UnauthorizedAccessException("The current access envelope is stale.");
        }

        var selected = await attempt.Persistence.Query<WorkspaceAccessContext>()
            .WhereEffectiveAccess(
                attempt.Persistence.Query<WorkspaceMembership>(),
                attempt.Persistence.Query<MembershipRoleAssignment>(),
                attempt.Persistence.Query<OwnerUserAccess>(),
                attempt.Persistence.Query<EffectiveTenantAccessProjection>(),
                command.UserId,
                command.ChangedAtUtc)
            .Where(item => item.Id == command.SelectedAccessContextId)
            .Select(item => new { item.Id, item.PortfolioId, item.AccessRevision })
            .SingleOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException("The selected access context is unavailable.");

        session.ActiveAccessContextId = selected.Id;
        session.LastSeenAtUtc = command.ChangedAtUtc;
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            selected.PortfolioId,
            nameof(AuthSession),
            selected.Id,
            AuditLogOperation.Updated,
            command.UserId,
            ActorLabel: "authentication:context-switch",
            NewValues: JsonSerializer.Serialize(new
            {
                command.AuthSessionId,
                command.CurrentAccessContextId,
                SelectedAccessContextId = selected.Id,
                selected.AccessRevision,
            }),
            ChangeReason: "Active workspace context switched"));

        return new SwitchAuthSessionContextResult(
            true,
            session.Id,
            command.UserId,
            selected.Id,
            selected.PortfolioId,
            selected.AccessRevision);
    }
}
