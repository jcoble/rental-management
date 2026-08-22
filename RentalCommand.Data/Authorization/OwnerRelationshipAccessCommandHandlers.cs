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

public static class OwnerRelationshipAccessWriteSupport
{
    public static TransactionalWrite<ActivateOwnerPortalAccessCommand, ActivateOwnerPortalAccessMutationResult> Write(
        RentalCommandDbContext db,
        ActivateOwnerPortalAccessCommand command)
    {
        var handler = new ActivateOwnerPortalAccessHandler(db);
        return new TransactionalWrite<ActivateOwnerPortalAccessCommand, ActivateOwnerPortalAccessMutationResult>(
            "owner-entity.portal-access.activate", WriteIdempotencyPolicy.Required, command,
            "owner-portal-access.activation.v1", WriteLockPlan.None,
            handler.ExecuteAsync, handler.AuthorizeReplayAsync);
    }

    public static TransactionalWrite<RevokeOwnerPortalAccessCommand, RevokeOwnerPortalAccessMutationResult> Write(
        RentalCommandDbContext db,
        RevokeOwnerPortalAccessCommand command)
    {
        var handler = new RevokeOwnerPortalAccessHandler(db);
        return new TransactionalWrite<RevokeOwnerPortalAccessCommand, RevokeOwnerPortalAccessMutationResult>(
            "owner-entity.portal-access.revoke", WriteIdempotencyPolicy.Required, command,
            "owner-portal-access.revocation.v1", WriteLockPlan.None,
            handler.ExecuteAsync, handler.AuthorizeReplayAsync);
    }

    internal static InvalidOperationException RetiredPath() => new(
        "Legacy owner relationship writes are retired; use the shared write executor.");
}

public sealed class ActivateOwnerPortalAccessHandler
    : IAtomicCommandHandler<ActivateOwnerPortalAccessCommand, ActivateOwnerPortalAccessMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public ActivateOwnerPortalAccessHandler(RentalCommandDbContext db) => _db = db;

    public Task<ActivateOwnerPortalAccessMutationResult> HandleAsync(
        ActivateOwnerPortalAccessCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw OwnerRelationshipAccessWriteSupport.RetiredPath();

    public async Task<ActivateOwnerPortalAccessMutationResult> ExecuteAsync(
        ActivateOwnerPortalAccessCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        if (!Uri.TryCreate(command.WebBaseUrl, UriKind.Absolute, out var webBaseUri) ||
            webBaseUri.Scheme is not ("http" or "https"))
        {
            throw new DomainValidationException("A valid web application URL is required for account activation.");
        }

        await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, context, null, _db, ct);
        var changedAtUtc = Utc(command.EffectiveFromUtc);
        var effectiveFromUtc = await context.ReadDatabaseClockUtcAsync(ct);
        context.UseDatabaseWallClockForAudit(changedAtUtc);
        await context.AcquireLockAsync("OwnerEntity", command.OwnerEntityId, ct);
        await context.AcquireLockAsync("ApplicationUser", command.EmailLockId, ct);

        var owner = await _db.Set<OwnerEntity>()
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
        var user = await _db.Set<ApplicationUser>()
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
                LockoutEnabled = true,
                DisplayName = DisplayName(owner.Name, email),
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = changedAtUtc,
            };
            _db.Add(user);
            userCreated = true;
        }

        WorkspaceAccessContext? accessContext = null;
        var existingContextId = userCreated
            ? null
            : await _db.Set<WorkspaceAccessContext>()
                .AsNoTracking()
                .Where(candidate =>
                    candidate.UserId == user.Id &&
                    candidate.PortfolioId == command.PortfolioId)
                .Select(candidate => (int?)candidate.Id)
                .SingleOrDefaultAsync(ct);
        if (existingContextId is not null)
        {
            await context.AcquireLockAsync(
                "WorkspaceAccessContext", existingContextId.Value, ct);
            accessContext = await _db.Set<WorkspaceAccessContext>()
                .Include(candidate => candidate.Membership)
                .SingleAsync(candidate => candidate.Id == existingContextId.Value, ct);
            if (accessContext.Status != WorkspaceAccessContextStatus.Active ||
                accessContext.SuspendedAtUtc is not null ||
                accessContext.RevokedAtUtc is not null)
            {
                return Result(
                    ActivateOwnerPortalAccessMutationOutcome.InactiveWorkspaceAccess,
                    owner.Id,
                    email,
                    user.Id,
                    accessContext.Id,
                    null,
                    accessContext.AccessRevision);
            }
        }

        accessContext ??= new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = command.PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Owner,
            CreatedAtUtc = changedAtUtc,
            UpdatedAtUtc = changedAtUtc,
        };
        accessContext.LastAuthorizedExperience ??= WorkspaceExperience.Owner;

        var existingAccess = accessContext.Id <= 0
            ? null
            : await _db.Set<OwnerUserAccess>()
                .SingleOrDefaultAsync(access =>
                    access.PortfolioId == command.PortfolioId &&
                    access.AccessContextId == accessContext.Id &&
                    access.ApplicationUserId == accessContext.UserId &&
                    access.OwnerEntityId == owner.Id &&
                    access.RevokedAtUtc == null,
                    ct);

        var requiresAccountActivation = string.IsNullOrEmpty(user.PasswordHash);
        WorkspaceMembership? invitationMembership = accessContext.Membership;
        var accessEffectiveDatesRepaired = RepairFutureEffectiveOwnerPortalAccess(
            invitationMembership,
            existingAccess,
            effectiveFromUtc,
            changedAtUtc);
        var pendingInvitation = invitationMembership is null
            ? null
            : await _db.Set<WorkspaceInvitation>()
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

        if (requiresAccountActivation && invitationMembership is null)
        {
            invitationMembership = new WorkspaceMembership
            {
                AccessContext = accessContext,
                PortfolioId = command.PortfolioId,
                Status = WorkspaceMembershipStatus.Active,
                DefaultExperience = WorkspaceExperience.Owner,
                EffectiveFromUtc = effectiveFromUtc,
                CreatedAtUtc = changedAtUtc,
                UpdatedAtUtc = changedAtUtc,
            };
            _db.Add(invitationMembership);
        }

        var ownerAssignmentCreated = invitationMembership is not null &&
            await EnsureOwnerPortalRoleAssignmentAsync(
                _db,
                invitationMembership,
                command.PortfolioId,
                effectiveFromUtc,
                changedAtUtc,
                ct);

        if (existingAccess is not null && (ownerAssignmentCreated || accessEffectiveDatesRepaired))
        {
            accessContext.AdvanceRevision(accessContext.AccessRevision);
            accessContext.UpdatedAtUtc = changedAtUtc;
        }

        if (existingAccess is not null && (!requiresAccountActivation || pendingInvitation is not null))
        {
            await context.FlushBusinessAsync(ct);
            return Result(
                requiresAccountActivation
                    ? ActivateOwnerPortalAccessMutationOutcome.InvitationPending
                    : ActivateOwnerPortalAccessMutationOutcome.AlreadyActive,
                owner.Id,
                email,
                user.Id,
                accessContext.Id,
                existingAccess.Id,
                accessContext.AccessRevision,
                requiresAccountActivation,
                pendingInvitation?.ExpiresAtUtc);
        }

        OwnerUserAccess access = existingAccess ?? new()
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = command.PortfolioId,
            AccessContext = accessContext,
            ApplicationUser = user,
            OwnerEntityId = owner.Id,
            EffectiveFromUtc = effectiveFromUtc,
            EffectiveToUtc = command.EffectiveToUtc,
            GrantedAtUtc = changedAtUtc,
            GrantedByUserId = command.ActorUserId,
            Reason = command.Reason.Trim(),
        };
        if (existingAccess is null)
        {
            _db.Add(access);
            accessContext.AdvanceRevision(accessContext.AccessRevision);
            accessContext.UpdatedAtUtc = changedAtUtc;
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
            _db.Add(invitation);
            context.StageOutbox(BuildOwnerActivationEmail(
                command,
                invitationMembership,
                user,
                rawToken,
                changedAtUtc));
        }

        await context.FlushBusinessAsync(ct);
        if (userCreated)
        {
            context.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
                command.PortfolioId, nameof(ApplicationUser), user.Id,
                AuditLogOperation.Created, command.ActorUserId, "Owner portal account invited",
                new
                {
                    command.OwnerEntityId,
                    ContextId = accessContext.Id,
                    RequiresAccountActivation = true,
                }), changedAtUtc);
        }
        if (existingAccess is null)
        {
            context.StageSemanticEvent(OwnerRelationshipAccessCommandSupport.Audit(
                command.PortfolioId,
                command.OwnerEntityId,
                accessContext.Id,
                accessContext.AccessRevision,
                command.ActorUserId,
                access.Id,
                AuditLogOperation.Created,
                "Owner portal relationship granted"), changedAtUtc);
        }
        if (invitation is not null && invitationMembership is not null)
        {
            context.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
                command.PortfolioId, nameof(WorkspaceInvitation), (int)invitation.Id,
                AuditLogOperation.Created, command.ActorUserId, "Owner portal invitation queued",
                new
                {
                    command.OwnerEntityId,
                    UserId = user.Id,
                    AccessContextId = accessContext.Id,
                    WorkspaceMembershipId = invitationMembership.Id,
                    invitation.ExpiresAtUtc,
                }), changedAtUtc);
        }

        return Result(
            requiresAccountActivation
                ? ActivateOwnerPortalAccessMutationOutcome.InvitationPending
                : ActivateOwnerPortalAccessMutationOutcome.Activated,
            owner.Id,
            email,
            user.Id,
            accessContext.Id,
            access.Id,
            accessContext.AccessRevision,
            requiresAccountActivation,
            pendingInvitation?.ExpiresAtUtc ?? invitation?.ExpiresAtUtc);
    }

    public Task AuthorizeReplayAsync(
        ActivateOwnerPortalAccessCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, _db, ct);

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

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static bool RepairFutureEffectiveOwnerPortalAccess(
        WorkspaceMembership? membership,
        OwnerUserAccess? access,
        DateTime effectiveFromUtc,
        DateTime changedAtUtc)
    {
        var changed = false;
        if (membership is not null &&
            membership.Status == WorkspaceMembershipStatus.Active &&
            membership.SuspendedAtUtc is null &&
            membership.RevokedAtUtc is null &&
            membership.EffectiveFromUtc > effectiveFromUtc &&
            (membership.EffectiveToUtc is null || membership.EffectiveToUtc > effectiveFromUtc))
        {
            membership.EffectiveFromUtc = effectiveFromUtc;
            membership.UpdatedAtUtc = changedAtUtc;
            changed = true;
        }

        if (access is not null &&
            access.RevokedAtUtc is null &&
            access.EffectiveFromUtc > effectiveFromUtc &&
            (access.EffectiveToUtc is null || access.EffectiveToUtc > effectiveFromUtc))
        {
            access.EffectiveFromUtc = effectiveFromUtc;
            changed = true;
        }

        return changed;
    }

    private static async Task<bool> EnsureOwnerPortalRoleAssignmentAsync(
        RentalCommandDbContext db,
        WorkspaceMembership membership,
        int portfolioId,
        DateTime effectiveFromUtc,
        DateTime changedAtUtc,
        CancellationToken ct)
    {
        if (membership.DefaultExperience != WorkspaceExperience.Owner)
        {
            return false;
        }

        var ownerRoleProfileId = AccessCatalog.Roles.Single(role => role.Key == RoleProfileKeys.OwnerPortal).Id;
        if (membership.Id > 0 && await db.Set<MembershipRoleAssignment>().AnyAsync(assignment =>
                assignment.WorkspaceMembershipId == membership.Id &&
                assignment.PortfolioId == portfolioId &&
                assignment.RoleProfileId == ownerRoleProfileId &&
                assignment.Status == MembershipRoleAssignmentStatus.Active &&
                assignment.SuspendedAtUtc == null &&
                assignment.RevokedAtUtc == null &&
                assignment.EffectiveFromUtc <= effectiveFromUtc &&
                (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > effectiveFromUtc),
                ct))
        {
            return false;
        }

        db.Add(new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolioId,
            RoleProfileId = ownerRoleProfileId,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = effectiveFromUtc,
            CreatedAtUtc = changedAtUtc,
            UpdatedAtUtc = changedAtUtc,
        });
        return true;
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

public sealed class RevokeOwnerPortalAccessHandler
    : IAtomicCommandHandler<RevokeOwnerPortalAccessCommand, RevokeOwnerPortalAccessMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public RevokeOwnerPortalAccessHandler(RentalCommandDbContext db) => _db = db;

    public Task<RevokeOwnerPortalAccessMutationResult> HandleAsync(
        RevokeOwnerPortalAccessCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw OwnerRelationshipAccessWriteSupport.RetiredPath();

    public async Task<RevokeOwnerPortalAccessMutationResult> ExecuteAsync(
        RevokeOwnerPortalAccessCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, context, null, _db, ct);
        await context.AcquireLockAsync("OwnerEntity", command.OwnerEntityId, ct);

        var changedAtUtc = Utc(command.ChangedAtUtc);
        context.UseDatabaseWallClockForAudit(changedAtUtc);
        var accesses = await BuildRevocationTargetQuery(
                command.PortfolioId,
                command.OwnerEntityId)
            .ToListAsync(ct);
        if (accesses.Count == 0)
        {
            var prior = await _db.Set<OwnerEntity>()
                .AsNoTracking()
                .Where(owner =>
                    owner.PortfolioId == command.PortfolioId &&
                    owner.Id == command.OwnerEntityId &&
                    owner.DeletedAt == null)
                .Select(owner => new
                {
                    owner.Email,
                    Prior = _db.Set<OwnerUserAccess>()
                        .Where(access =>
                            access.PortfolioId == command.PortfolioId &&
                            access.OwnerEntityId == owner.Id)
                        .OrderByDescending(access => access.Id)
                        .Select(access => new
                        {
                            access.Id,
                            access.AccessContextId,
                            access.AccessContext!.AccessRevision,
                        })
                        .FirstOrDefault(),
                })
                .SingleOrDefaultAsync(ct);
            return prior is null
                ? Result(RevokeOwnerPortalAccessMutationOutcome.NotFound, command.OwnerEntityId)
                : Result(
                    RevokeOwnerPortalAccessMutationOutcome.AlreadyRevoked,
                    command.OwnerEntityId,
                    prior.Email,
                    targetAccessContextId: prior.Prior?.AccessContextId,
                    ownerUserAccessId: prior.Prior?.Id,
                    accessRevision: prior.Prior?.AccessRevision);
        }

        var advancedContextIds = new HashSet<int>();
        foreach (var access in accesses)
        {
            var accessContext = access.AccessContext
                ?? throw new InvalidOperationException("Owner portal access has no access context.");
            await context.AcquireLockAsync("WorkspaceAccessContext", accessContext.Id, ct);
            var revokedAtUtc = changedAtUtc < access.GrantedAtUtc
                ? access.GrantedAtUtc
                : changedAtUtc;

            access.RevokedAtUtc = revokedAtUtc;
            access.RevokedByUserId = command.ActorUserId;
            access.Reason = command.Reason.Trim();
            if (revokedAtUtc > access.EffectiveFromUtc)
            {
                access.EffectiveToUtc = revokedAtUtc;
            }
            context.BindSemanticAudit(access, OwnerRelationshipAccessCommandSupport.Audit(
                command.PortfolioId,
                command.OwnerEntityId,
                accessContext.Id,
                accessContext.AccessRevision + 1,
                command.ActorUserId,
                access.Id,
                AuditLogOperation.Updated,
                "Owner portal relationship revoked"));

            if (accessContext.Membership is { } membership)
            {
                foreach (var invitation in membership.Invitations)
                {
                    invitation.RevokedAtUtc = revokedAtUtc;
                    context.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
                        command.PortfolioId,
                        nameof(WorkspaceInvitation),
                        (int)invitation.Id,
                        AuditLogOperation.Updated,
                        command.ActorUserId,
                        "Owner portal invitation revoked",
                        new { command.OwnerEntityId, accessContext.Id }),
                        changedAtUtc);
                }

                foreach (var assignment in membership.RoleAssignments)
                {
                    assignment.Status = MembershipRoleAssignmentStatus.Revoked;
                    assignment.SuspendedAtUtc = null;
                    assignment.RevokedAtUtc = revokedAtUtc;
                    if (revokedAtUtc > assignment.EffectiveFromUtc)
                    {
                        assignment.EffectiveToUtc = revokedAtUtc;
                    }
                    assignment.UpdatedAtUtc = revokedAtUtc;
                    context.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
                        command.PortfolioId,
                        nameof(MembershipRoleAssignment),
                        assignment.Id,
                        AuditLogOperation.Updated,
                        command.ActorUserId,
                        "Owner portal role assignment revoked",
                        new { command.OwnerEntityId, accessContext.Id }),
                        changedAtUtc);
                }
            }

            if (advancedContextIds.Add(accessContext.Id))
            {
                accessContext.AdvanceRevision(accessContext.AccessRevision);
                accessContext.UpdatedAtUtc = revokedAtUtc;
            }
        }

        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(OwnerEntity),
                entityId = command.OwnerEntityId,
                operation = "update",
                data = new { },
            }),
            IdempotencyKey = $"{command.DeliveryIdempotencyKey}:owner-portal-revoke-data-update",
            CreatedAtUtc = changedAtUtc,
            NextAttemptAtUtc = changedAtUtc,
        });
        await context.FlushBusinessAsync(ct);

        var first = accesses[0];
        return Result(
            RevokeOwnerPortalAccessMutationOutcome.Revoked,
            command.OwnerEntityId,
            first.OwnerEntity?.Email,
            accesses.Count,
            first.AccessContextId,
            first.Id,
            first.AccessContext!.AccessRevision);
    }

    public Task AuthorizeReplayAsync(
        RevokeOwnerPortalAccessCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        return WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, _db, ct);
    }

    internal IQueryable<OwnerUserAccess> BuildRevocationTargetQuery(
        int portfolioId,
        int ownerEntityId)
    {
        var ownerRoleProfileId = AccessCatalog.Roles
            .Single(role => role.Key == RoleProfileKeys.OwnerPortal)
            .Id;
        return _db.Set<OwnerUserAccess>()
            .Where(access =>
                access.PortfolioId == portfolioId &&
                access.OwnerEntityId == ownerEntityId &&
                access.RevokedAtUtc == null &&
                access.OwnerEntity!.DeletedAt == null)
            .Include(access => access.OwnerEntity)
            .Include(access => access.AccessContext)
                .ThenInclude(accessContext => accessContext!.Membership)
                    .ThenInclude(membership => membership!.Invitations.Where(invitation =>
                        invitation.AcceptedAtUtc == null &&
                        invitation.RevokedAtUtc == null))
            .Include(access => access.AccessContext)
                .ThenInclude(accessContext => accessContext!.Membership)
                    .ThenInclude(membership => membership!.RoleAssignments.Where(assignment =>
                        assignment.RoleProfileId == ownerRoleProfileId &&
                        assignment.Status == MembershipRoleAssignmentStatus.Active &&
                        assignment.SuspendedAtUtc == null &&
                        assignment.RevokedAtUtc == null))
            .AsSingleQuery();
    }

    private static void Validate(RevokeOwnerPortalAccessCommand command)
    {
        if (command.PortfolioId <= 0 || command.OwnerEntityId <= 0 ||
            command.ActorUserId <= 0 || command.ActorAuthSessionId == Guid.Empty ||
            command.ActorAccessContextId <= 0 || command.ActorAccessRevision <= 0 ||
            string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 500 ||
            string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
        {
            throw new ArgumentException(
                "Owner portal revocation, actor context, reason, and delivery identity are required.");
        }
    }

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static RevokeOwnerPortalAccessMutationResult Result(
        RevokeOwnerPortalAccessMutationOutcome outcome,
        int ownerEntityId,
        string? ownerEmail = null,
        int revokedRelationshipCount = 0,
        int? targetAccessContextId = null,
        int? ownerUserAccessId = null,
        long? accessRevision = null) => new(
        outcome,
        ownerEntityId,
        ownerEmail,
        revokedRelationshipCount,
        targetAccessContextId,
        ownerUserAccessId,
        accessRevision);
}

internal static class OwnerRelationshipAccessCommandSupport
{
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
