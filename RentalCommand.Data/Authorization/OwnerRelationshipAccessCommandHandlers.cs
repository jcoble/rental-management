using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
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
            EffectiveFromUtc = now,
            EffectiveToUtc = command.EffectiveToUtc,
            GrantedAtUtc = now,
            GrantedByUserId = command.ActorUserId,
            Reason = command.Reason.Trim(),
        };
        attempt.Persistence.Add(access);
        target.Context.AdvanceRevision(command.ExpectedTargetAccessRevision);
        target.Context.UpdatedAtUtc = now;
        attempt.BindSemanticAudit(access, OwnerRelationshipAccessCommandSupport.Audit(
            command, access.Id, AuditLogOperation.Created, "Owner portal relationship granted"));
        await attempt.FlushBusinessAsync(ct);
        return new(OwnerRelationshipAccessMutationOutcome.Applied, command.OwnerEntityId,
            command.TargetAccessContextId, access.Id, target.Context.AccessRevision);
    }

    public Task AuthorizeReplayAsync(
        GrantOwnerUserAccessCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        OwnerRelationshipAccessCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class ActivateOwnerPortalAccessHandler
    : IAtomicCommandHandler<ActivateOwnerPortalAccessCommand, ActivateOwnerPortalAccessMutationResult>,
      IAtomicReplayAuthorizer<ActivateOwnerPortalAccessCommand>
{
    public async Task<ActivateOwnerPortalAccessMutationResult> HandleAsync(
        ActivateOwnerPortalAccessCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        if (!Uri.TryCreate(command.WebBaseUrl, UriKind.Absolute, out var webBaseUri) ||
            webBaseUri.Scheme is not ("http" or "https"))
        {
            throw new DomainValidationException("A valid web application URL is required for account activation.");
        }

        var changedAtUtc = await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, attempt, null, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.ApplicationUser, command.EmailLockId, ct);

        var owner = await attempt.Persistence.Query<OwnerEntity>()
            .AsNoTracking()
            .Where(candidate =>
                candidate.PortfolioId == command.PortfolioId &&
                candidate.Id == command.OwnerEntityId &&
                candidate.DeletedAt == null)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Name,
                candidate.Email,
                candidate.IsPrimary,
            })
            .SingleOrDefaultAsync(ct);
        if (owner is null)
        {
            return Result(ActivateOwnerPortalAccessMutationOutcome.NotFound, command.OwnerEntityId);
        }
        if (owner.IsPrimary)
        {
            return Result(
                ActivateOwnerPortalAccessMutationOutcome.PrimaryOwnerNotSupported,
                owner.Id,
                owner.Email);
        }

        var email = owner.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            return Result(ActivateOwnerPortalAccessMutationOutcome.MissingOwnerEmail, owner.Id, owner.Email);
        }
        if (email.Length > 256 || !email.Contains('@', StringComparison.Ordinal))
        {
            return Result(ActivateOwnerPortalAccessMutationOutcome.Invalid, owner.Id, email);
        }
        if (command.EffectiveToUtc is not null && command.EffectiveToUtc <= command.EffectiveFromUtc)
        {
            return Result(ActivateOwnerPortalAccessMutationOutcome.Invalid, owner.Id, email);
        }

        var normalizedEmail = email.ToUpperInvariant();
        var user = await attempt.Persistence.Query<ApplicationUser>()
            .SingleOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, ct);
        var userCreated = false;
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                NormalizedUserName = normalizedEmail,
                Email = email,
                NormalizedEmail = normalizedEmail,
                EmailConfirmed = false,
                DisplayName = DisplayName(owner.Name, email),
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = changedAtUtc,
            };
            attempt.Persistence.Add(user);
            userCreated = true;
        }

        WorkspaceAccessContext? context = null;
        var existingContextId = userCreated
            ? null
            : await attempt.Persistence.Query<WorkspaceAccessContext>()
                .AsNoTracking()
                .Where(candidate =>
                    candidate.UserId == user.Id &&
                    candidate.PortfolioId == command.PortfolioId)
                .Select(candidate => (int?)candidate.Id)
                .SingleOrDefaultAsync(ct);
        if (existingContextId is not null)
        {
            await attempt.Locking.AcquireAsync(
                AtomicLockResource.WorkspaceAccessContext, existingContextId.Value, ct);
            context = await attempt.Persistence.Query<WorkspaceAccessContext>()
                .Include(candidate => candidate.Membership)
                .SingleAsync(candidate => candidate.Id == existingContextId.Value, ct);
            if (context.Status != WorkspaceAccessContextStatus.Active ||
                context.SuspendedAtUtc is not null ||
                context.RevokedAtUtc is not null)
            {
                return Result(
                    ActivateOwnerPortalAccessMutationOutcome.InactiveWorkspaceAccess,
                    owner.Id,
                    email,
                    user.Id,
                    context.Id,
                    null,
                    context.AccessRevision);
            }
        }

        context ??= new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = command.PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Owner,
            CreatedAtUtc = changedAtUtc,
            UpdatedAtUtc = changedAtUtc,
        };
        context.LastAuthorizedExperience ??= WorkspaceExperience.Owner;

        var existingAccess = context.Id <= 0
            ? null
            : await attempt.Persistence.Query<OwnerUserAccess>()
                .SingleOrDefaultAsync(access =>
                    access.PortfolioId == command.PortfolioId &&
                    access.AccessContextId == context.Id &&
                    access.ApplicationUserId == context.UserId &&
                    access.OwnerEntityId == owner.Id &&
                    access.RevokedAtUtc == null,
                    ct);

        var requiresAccountActivation = string.IsNullOrEmpty(user.PasswordHash);
        WorkspaceMembership? invitationMembership = context.Membership;
        var pendingInvitation = invitationMembership is null
            ? null
            : await attempt.Persistence.Query<WorkspaceInvitation>()
                .AsNoTracking()
                .Where(invitation =>
                    invitation.PortfolioId == command.PortfolioId &&
                    invitation.WorkspaceMembershipId == invitationMembership.Id &&
                    invitation.InvitedUserId == user.Id &&
                    invitation.AcceptedAtUtc == null &&
                    invitation.RevokedAtUtc == null &&
                    invitation.ExpiresAtUtc > changedAtUtc)
                .OrderByDescending(invitation => invitation.CreatedAtUtc)
                .Select(invitation => new PendingInvitation(invitation.ExpiresAtUtc))
                .FirstOrDefaultAsync(ct);
        if (existingAccess is not null && (!requiresAccountActivation || pendingInvitation is not null))
        {
            return Result(
                requiresAccountActivation
                    ? ActivateOwnerPortalAccessMutationOutcome.InvitationPending
                    : ActivateOwnerPortalAccessMutationOutcome.AlreadyActive,
                owner.Id,
                email,
                user.Id,
                context.Id,
                existingAccess.Id,
                context.AccessRevision,
                requiresAccountActivation,
                pendingInvitation?.ExpiresAtUtc);
        }

        if (requiresAccountActivation && invitationMembership is null)
        {
            invitationMembership = new WorkspaceMembership
            {
                AccessContext = context,
                PortfolioId = command.PortfolioId,
                Status = WorkspaceMembershipStatus.Active,
                DefaultExperience = WorkspaceExperience.Owner,
                EffectiveFromUtc = changedAtUtc,
                CreatedAtUtc = changedAtUtc,
                UpdatedAtUtc = changedAtUtc,
            };
            attempt.Persistence.Add(invitationMembership);
        }

        OwnerUserAccess access = existingAccess ?? new()
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = command.PortfolioId,
            AccessContext = context,
            ApplicationUser = user,
            OwnerEntityId = owner.Id,
            EffectiveFromUtc = changedAtUtc,
            EffectiveToUtc = command.EffectiveToUtc,
            GrantedAtUtc = changedAtUtc,
            GrantedByUserId = command.ActorUserId,
            Reason = command.Reason.Trim(),
        };
        if (existingAccess is null)
        {
            attempt.Persistence.Add(access);
            context.AdvanceRevision(context.AccessRevision);
            context.UpdatedAtUtc = changedAtUtc;
        }

        WorkspaceInvitation? invitation = null;
        if (requiresAccountActivation && pendingInvitation is null && invitationMembership is not null)
        {
            var rawToken = CreateInvitationToken();
            invitation = new WorkspaceInvitation
            {
                PortfolioId = command.PortfolioId,
                WorkspaceMembership = invitationMembership,
                InvitedUser = user,
                InvitedByUserId = command.ActorUserId,
                TokenHash = CreateWorkspaceMembershipHandler.HashInvitationToken(rawToken),
                CreatedAtUtc = changedAtUtc,
                ExpiresAtUtc = changedAtUtc.AddDays(7),
            };
            attempt.Persistence.Add(invitation);
            attempt.StageOutbox(BuildOwnerActivationEmail(
                command,
                invitationMembership,
                user,
                rawToken,
                changedAtUtc));
        }

        await attempt.FlushBusinessAsync(ct);
        if (userCreated)
        {
            attempt.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
                command.PortfolioId, nameof(ApplicationUser), user.Id,
                AuditLogOperation.Created, command.ActorUserId, "Owner portal account invited",
                new
                {
                    command.OwnerEntityId,
                    ContextId = context.Id,
                    RequiresAccountActivation = true,
                }));
        }
        if (existingAccess is null)
        {
            attempt.StageSemanticEvent(OwnerRelationshipAccessCommandSupport.Audit(
                command.PortfolioId,
                command.OwnerEntityId,
                context.Id,
                context.AccessRevision,
                command.ActorUserId,
                access.Id,
                AuditLogOperation.Created,
                "Owner portal relationship granted"));
        }
        if (invitation is not null && invitationMembership is not null)
        {
            attempt.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
                command.PortfolioId, nameof(WorkspaceInvitation), (int)invitation.Id,
                AuditLogOperation.Created, command.ActorUserId, "Owner portal invitation queued",
                new
                {
                    command.OwnerEntityId,
                    UserId = user.Id,
                    AccessContextId = context.Id,
                    WorkspaceMembershipId = invitationMembership.Id,
                    invitation.ExpiresAtUtc,
                }));
        }

        return Result(
            requiresAccountActivation
                ? ActivateOwnerPortalAccessMutationOutcome.InvitationPending
                : ActivateOwnerPortalAccessMutationOutcome.Activated,
            owner.Id,
            email,
            user.Id,
            context.Id,
            access.Id,
            context.AccessRevision,
            requiresAccountActivation,
            pendingInvitation?.ExpiresAtUtc ?? invitation?.ExpiresAtUtc);
    }

    public Task AuthorizeReplayAsync(
        ActivateOwnerPortalAccessCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, persistence, ct);

    private static void Validate(ActivateOwnerPortalAccessCommand command)
    {
        if (command.PortfolioId <= 0 || command.OwnerEntityId <= 0 ||
            command.ActorUserId <= 0 || command.ActorAuthSessionId == Guid.Empty ||
            command.ActorAccessContextId <= 0 || command.ActorAccessRevision <= 0 ||
            command.EmailLockId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason) ||
            command.Reason.Length > 500)
        {
            throw new ArgumentException("Owner portal activation, actor context, and reason are required.");
        }
    }

    private static ActivateOwnerPortalAccessMutationResult Result(
        ActivateOwnerPortalAccessMutationOutcome outcome,
        int ownerEntityId,
        string? ownerEmail = null,
        int? userId = null,
        int? targetAccessContextId = null,
        int? ownerUserAccessId = null,
        long? accessRevision = null,
        bool requiresAccountActivation = false,
        DateTime? invitationExpiresAtUtc = null) => new(
        outcome,
        ownerEntityId,
        ownerEmail,
        userId,
        targetAccessContextId,
        ownerUserAccessId,
        accessRevision,
        requiresAccountActivation,
        invitationExpiresAtUtc);

    private static string DisplayName(string ownerName, string email)
    {
        var trimmed = ownerName.Trim();
        if (trimmed.Length is > 0 and <= 200)
        {
            return trimmed;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        var fallback = at > 0 ? email[..at] : email;
        return fallback.Length <= 200 ? fallback : fallback[..200];
    }

    private static string CreateInvitationToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static OutboxMessage BuildOwnerActivationEmail(
        ActivateOwnerPortalAccessCommand command,
        WorkspaceMembership membership,
        ApplicationUser user,
        string rawToken,
        DateTime createdAtUtc)
    {
        var tokenHash = CreateWorkspaceMembershipHandler.HashInvitationToken(rawToken);
        var link = $"{command.WebBaseUrl.TrimEnd('/')}/activate-team?token={Uri.EscapeDataString(rawToken)}";
        var greeting = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email! : user.DisplayName;
        var subject = "Activate your Rental Command owner portal";
        var body = $"""
            Hi {greeting},

            You've been invited to use Rental Command's owner portal. Set your password to activate your account:

            {link}

            This secure link expires in 7 days. If you weren't expecting this invitation, you can ignore this email.

            - The Rental Command Team
            """;
        var htmlBody = $"""
            <!DOCTYPE html>
            <html><body style="font-family:-apple-system,Segoe UI,Roboto,sans-serif;line-height:1.6;color:#1a1a2e;">
              <p>Hi {WebUtility.HtmlEncode(greeting)},</p>
              <p>You've been invited to use Rental Command's owner portal.</p>
              <p><a href="{WebUtility.HtmlEncode(link)}">Set your password and activate your account</a>.</p>
              <p style="color:#6b7280;font-size:13px;">This secure link expires in 7 days. If you weren't expecting this invitation, you can ignore this email.</p>
              <p>- The Rental Command Team</p>
            </body></html>
            """;
        return new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "email",
            Payload = JsonSerializer.Serialize(new { to = user.Email, subject, body, htmlBody }),
            IdempotencyKey = $"owner-portal-invitation:{membership.PortfolioId}:{command.OwnerEntityId}:{tokenHash[..16]}",
            CreatedAtUtc = createdAtUtc,
            NextAttemptAtUtc = createdAtUtc,
        };
    }

    private sealed record PendingInvitation(DateTime ExpiresAtUtc);
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

    public static AtomicSemanticAudit Audit(
        int portfolioId,
        int ownerEntityId,
        int targetAccessContextId,
        long accessRevision,
        int actorUserId,
        int accessId,
        AuditLogOperation operation,
        string reason) => new(
            portfolioId,
            nameof(OwnerUserAccess),
            accessId,
            operation,
            actorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                OwnerEntityId = ownerEntityId,
                TargetAccessContextId = targetAccessContextId,
                AccessRevision = accessRevision,
            }),
            ChangeReason: reason);
}
