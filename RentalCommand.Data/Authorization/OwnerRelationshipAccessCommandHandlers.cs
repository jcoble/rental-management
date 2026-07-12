using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

public sealed class GrantOwnerUserAccessHandler
    : IAtomicCommandHandler<GrantOwnerUserAccessCommand, OwnerRelationshipAccessMutationResult>,
      IAtomicReplayAuthorizer<GrantOwnerUserAccessCommand>
{
    public async Task<OwnerRelationshipAccessMutationResult> HandleAsync(
        GrantOwnerUserAccessCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        OwnerRelationshipAccessCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.TargetAccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var target = await OwnerRelationshipAccessCommandSupport.AuthorizedTarget(command, attempt, now)
            .Select(context => new
            {
                Context = context,
                OwnerExists = attempt.Persistence.Query<OwnerEntity>().Any(owner =>
                    owner.Id == command.OwnerEntityId && owner.PortfolioId == command.PortfolioId && owner.DeletedAt == null),
                Existing = attempt.Persistence.Query<OwnerUserAccess>().FirstOrDefault(access =>
                    access.AccessContextId == context.Id && access.OwnerEntityId == command.OwnerEntityId &&
                    access.PortfolioId == command.PortfolioId && access.RevokedAtUtc == null),
            })
            .SingleOrDefaultAsync(ct);
        if (target is null || !target.OwnerExists)
        {
            return new(OwnerRelationshipAccessMutationOutcome.NotFound, command.OwnerEntityId,
                command.TargetAccessContextId, null, command.ExpectedTargetAccessRevision);
        }
        if (target.Existing is not null)
        {
            return new(OwnerRelationshipAccessMutationOutcome.AlreadyActive, command.OwnerEntityId,
                command.TargetAccessContextId, target.Existing.Id, target.Context.AccessRevision);
        }
        if (command.EffectiveToUtc is not null && command.EffectiveToUtc <= command.EffectiveFromUtc)
        {
            return new(OwnerRelationshipAccessMutationOutcome.Invalid, command.OwnerEntityId,
                command.TargetAccessContextId, null, target.Context.AccessRevision);
        }

        var access = new OwnerUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = command.PortfolioId,
            AccessContext = target.Context,
            ApplicationUserId = target.Context.UserId,
            OwnerEntityId = command.OwnerEntityId,
            EffectiveFromUtc = command.EffectiveFromUtc,
            EffectiveToUtc = command.EffectiveToUtc,
            GrantedAtUtc = now,
            GrantedByUserId = command.ActorUserId,
            Reason = command.Reason.Trim(),
        };
        attempt.Persistence.Add(access);
        target.Context.AdvanceRevision(command.ExpectedTargetAccessRevision);
        target.Context.UpdatedAtUtc = now;
        await attempt.FlushBusinessAsync(ct);
        attempt.BindSemanticAudit(access, OwnerRelationshipAccessCommandSupport.Audit(
            command, access.Id, AuditLogOperation.Created, "Owner portal relationship granted"));
        return new(OwnerRelationshipAccessMutationOutcome.Applied, command.OwnerEntityId,
            command.TargetAccessContextId, access.Id, target.Context.AccessRevision);
    }

    public Task AuthorizeReplayAsync(
        GrantOwnerUserAccessCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        OwnerRelationshipAccessCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class RevokeOwnerUserAccessHandler
    : IAtomicCommandHandler<RevokeOwnerUserAccessCommand, OwnerRelationshipAccessMutationResult>,
      IAtomicReplayAuthorizer<RevokeOwnerUserAccessCommand>
{
    public async Task<OwnerRelationshipAccessMutationResult> HandleAsync(
        RevokeOwnerUserAccessCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        OwnerRelationshipAccessCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.TargetAccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var target = await OwnerRelationshipAccessCommandSupport.AuthorizedTarget(command, attempt, now)
            .Select(context => new
            {
                Context = context,
                Access = attempt.Persistence.Query<OwnerUserAccess>().FirstOrDefault(access =>
                    access.Id == command.OwnerUserAccessId && access.AccessContextId == context.Id &&
                    access.OwnerEntityId == command.OwnerEntityId && access.PortfolioId == command.PortfolioId),
            })
            .SingleOrDefaultAsync(ct);
        if (target?.Access is null)
        {
            return new(OwnerRelationshipAccessMutationOutcome.NotFound, command.OwnerEntityId,
                command.TargetAccessContextId, command.OwnerUserAccessId, command.ExpectedTargetAccessRevision);
        }
        if (target.Access.RevokedAtUtc is not null)
        {
            return new(OwnerRelationshipAccessMutationOutcome.AlreadyRevoked, command.OwnerEntityId,
                command.TargetAccessContextId, target.Access.Id, target.Context.AccessRevision);
        }

        target.Access.RevokedAtUtc = now;
        target.Access.RevokedByUserId = command.ActorUserId;
        target.Access.Reason = command.Reason.Trim();
        target.Context.AdvanceRevision(command.ExpectedTargetAccessRevision);
        target.Context.UpdatedAtUtc = now;
        attempt.BindSemanticAudit(target.Access, OwnerRelationshipAccessCommandSupport.Audit(
            command, target.Access.Id, AuditLogOperation.Updated, "Owner portal relationship revoked"));
        return new(OwnerRelationshipAccessMutationOutcome.Applied, command.OwnerEntityId,
            command.TargetAccessContextId, target.Access.Id, target.Context.AccessRevision);
    }

    public Task AuthorizeReplayAsync(
        RevokeOwnerUserAccessCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        OwnerRelationshipAccessCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

internal static class OwnerRelationshipAccessCommandSupport
{
    public static IQueryable<WorkspaceAccessContext> AuthorizedTarget(
        IOwnerRelationshipAccessCommand command,
        IAtomicWriteAttempt attempt,
        DateTime now)
    {
        var authorizedActors = AuthorizedActors(command, attempt.Persistence, now);
        return attempt.Persistence.Query<WorkspaceAccessContext>()
            .Where(context =>
                context.Id == command.TargetAccessContextId && context.PortfolioId == command.PortfolioId &&
                context.AccessRevision == command.ExpectedTargetAccessRevision &&
                context.Status == WorkspaceAccessContextStatus.Active && context.SuspendedAtUtc == null &&
                context.RevokedAtUtc == null && authorizedActors.Any());
    }

    private static IQueryable<WorkspaceAccessContext> AuthorizedActors(
        IOwnerRelationshipAccessCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now)
    {
        var actorAssignments = persistence.Query<MembershipRoleAssignment>().WhereEffective(now);
        return persistence.Query<WorkspaceAccessContext>().Where(actor =>
                   actor.Id == command.ActorAccessContextId && actor.UserId == command.ActorUserId &&
                   actor.PortfolioId == command.PortfolioId && actor.AccessRevision == command.ActorAccessRevision &&
                   actor.Status == WorkspaceAccessContextStatus.Active && actor.SuspendedAtUtc == null &&
                   actor.RevokedAtUtc == null && actor.Membership != null &&
                   persistence.Query<AuthSession>().Any(session =>
                       session.Id == command.ActorAuthSessionId && session.UserId == command.ActorUserId &&
                       session.ActiveAccessContextId == actor.Id &&
                       session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null &&
                       session.ExpiresAtUtc > now) &&
                   actorAssignments.Any(assignment =>
                       assignment.WorkspaceMembershipId == actor.Membership.Id &&
                       assignment.PortfolioId == command.PortfolioId &&
                       assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties &&
                       assignment.RoleProfile!.Capabilities.Any(capability =>
                           capability.CapabilityDefinition!.Key == CapabilityKeys.TeamManage)));
    }

    public static async Task AuthorizeReplayAsync(
        IOwnerRelationshipAccessCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        var targetContexts = persistence.Query<WorkspaceAccessContext>().Where(context =>
            context.Id == command.TargetAccessContextId && context.PortfolioId == command.PortfolioId);
        var authorized = await AuthorizedActors(command, persistence, now)
            .AnyAsync(actor => targetContexts.Any(), ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException("The actor no longer has authority to manage owner relationships.");
        }
    }

    public static void Validate(IOwnerRelationshipAccessCommand command)
    {
        if (command.PortfolioId <= 0 || command.OwnerEntityId <= 0 || command.TargetAccessContextId <= 0 ||
            command.ExpectedTargetAccessRevision <= 0 || command.ActorUserId <= 0 ||
            command.ActorAuthSessionId == Guid.Empty || command.ActorAccessContextId <= 0 ||
            command.ActorAccessRevision <= 0 || string.IsNullOrWhiteSpace(command.Reason) ||
            command.Reason.Length > 500)
        {
            throw new ArgumentException("Owner relationship, target context, actor context, and reason are required.");
        }
    }

    public static AtomicSemanticAudit Audit(
        IOwnerRelationshipAccessCommand command,
        int accessId,
        AuditLogOperation operation,
        string reason) => new(
            command.PortfolioId,
            nameof(OwnerUserAccess),
            accessId,
            operation,
            command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                command.OwnerEntityId,
                command.TargetAccessContextId,
                AccessRevision = command.ExpectedTargetAccessRevision + 1,
            }),
            ChangeReason: reason);
}
