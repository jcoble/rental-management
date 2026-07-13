using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

internal static class WorkspaceTeamAuthoritySupport
{
    public static async Task<DateTime> LockAndAuthorizeActorAsync(
        IWorkspaceTeamAuthorityCommand command,
        IAtomicWriteAttempt attempt,
        int? targetAccessContextId,
        CancellationToken ct)
    {
        var lockIds = targetAccessContextId is > 0
            ? new[] { command.ActorAccessContextId, targetAccessContextId.Value }.Distinct().Order().ToArray()
            : new[] { command.ActorAccessContextId };
        foreach (var contextId in lockIds)
        {
            await attempt.Locking.AcquireAsync(AtomicLockResource.WorkspaceAccessContext, contextId, ct);
        }

        var utcNow = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        await AuthorizeActorAsync(command, attempt.Persistence, utcNow, ct);
        return utcNow;
    }

    public static async Task AuthorizeReplayAsync(
        IWorkspaceTeamAuthorityCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var utcNow = await persistence.ReadDatabaseClockUtcAsync(ct);
        await AuthorizeActorAsync(command, persistence, utcNow, ct);
    }

    private static async Task AuthorizeActorAsync(
        IWorkspaceTeamAuthorityCommand command,
        IAtomicPersistenceSession persistence,
        DateTime utcNow,
        CancellationToken ct)
    {
        var sessions = persistence.Query<AuthSession>().AsNoTracking();
        var memberships = persistence.Query<WorkspaceMembership>().AsNoTracking();
        var assignments = persistence.Query<MembershipRoleAssignment>().AsNoTracking();
        var actor = await persistence.Query<WorkspaceAccessContext>()
            .AsNoTracking()
            .Where(context =>
                context.Id == command.ActorAccessContextId &&
                context.UserId == command.ActorUserId &&
                context.PortfolioId == command.PortfolioId &&
                context.Status == WorkspaceAccessContextStatus.Active &&
                context.SuspendedAtUtc == null &&
                context.RevokedAtUtc == null &&
                sessions.Any(session =>
                    session.Id == command.ActorAuthSessionId &&
                    session.UserId == command.ActorUserId &&
                    session.ActiveAccessContextId == context.Id &&
                    session.Status == AuthSessionStatus.Active &&
                    session.RevokedAtUtc == null &&
                    session.ExpiresAtUtc > utcNow))
            .Select(context => new
            {
                context.AccessRevision,
                CanManageTeam = memberships.Any(membership =>
                    membership.AccessContextId == context.Id &&
                    membership.PortfolioId == context.PortfolioId &&
                    membership.Status == WorkspaceMembershipStatus.Active &&
                    membership.SuspendedAtUtc == null &&
                    membership.RevokedAtUtc == null &&
                    membership.EffectiveFromUtc <= utcNow &&
                    (membership.EffectiveToUtc == null || membership.EffectiveToUtc > utcNow) &&
                    assignments.Any(assignment =>
                        assignment.WorkspaceMembershipId == membership.Id &&
                        assignment.PortfolioId == membership.PortfolioId &&
                        assignment.Status == MembershipRoleAssignmentStatus.Active &&
                        assignment.SuspendedAtUtc == null &&
                        assignment.RevokedAtUtc == null &&
                        assignment.EffectiveFromUtc <= utcNow &&
                        (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties &&
                        assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                            profileCapability.CapabilityDefinition!.Key == CapabilityKeys.TeamManage &&
                            profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                                CapabilityAuthorizationTargetKind.Workspace)))
            })
            .SingleOrDefaultAsync(ct)
            ?? throw new AccessContextUnavailableException();

        if (actor.AccessRevision != command.ActorAccessRevision)
        {
            throw new StaleAccessRevisionException(command.ActorAccessRevision, actor.AccessRevision);
        }

        if (!actor.CanManageTeam)
        {
            throw new UnauthorizedAccessException("The active assignment does not grant team.manage.");
        }
    }

    public static async Task<RoleProfile> LoadAndValidateRoleAsync(
        string roleProfileKey,
        MembershipRoleAssignmentScopeKind scopeKind,
        IReadOnlyCollection<int> selectedPropertyIds,
        int portfolioId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleProfileKey);
        if (!AccessCatalog.Roles.Any(role => role.Key == roleProfileKey))
        {
            throw new DomainValidationException("Only the four canonical Team role profiles are accepted.");
        }

        var role = await persistence.Query<RoleProfile>()
            .SingleOrDefaultAsync(profile => profile.Key == roleProfileKey, ct)
            ?? throw new DomainValidationException("The canonical Team role profile is unavailable.");
        ValidateScope(roleProfileKey, scopeKind, selectedPropertyIds);
        await ValidatePropertiesAsync(portfolioId, selectedPropertyIds, scopeKind, persistence, ct);
        return role;
    }

    public static void ValidateScope(
        string roleProfileKey,
        MembershipRoleAssignmentScopeKind scopeKind,
        IReadOnlyCollection<int> selectedPropertyIds)
    {
        var allowed = roleProfileKey switch
        {
            RoleProfileKeys.WorkspaceAdministrator =>
                scopeKind == MembershipRoleAssignmentScopeKind.AllProperties,
            RoleProfileKeys.PropertyManager or RoleProfileKeys.LeasingAgent =>
                scopeKind is MembershipRoleAssignmentScopeKind.AllProperties or
                    MembershipRoleAssignmentScopeKind.SelectedProperties,
            RoleProfileKeys.MaintenanceTechnician =>
                scopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders,
            _ => false,
        };
        if (!allowed)
        {
            throw new DomainValidationException($"Role {roleProfileKey} cannot use {scopeKind} scope.");
        }

        if (scopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties && selectedPropertyIds.Count == 0)
        {
            throw new DomainValidationException("Selected-property scope requires at least one property.");
        }

        if (scopeKind != MembershipRoleAssignmentScopeKind.SelectedProperties && selectedPropertyIds.Count != 0)
        {
            throw new DomainValidationException($"{scopeKind} scope cannot contain selected properties.");
        }
    }

    public static async Task ValidatePropertiesAsync(
        int portfolioId,
        IReadOnlyCollection<int> selectedPropertyIds,
        MembershipRoleAssignmentScopeKind scopeKind,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        if (scopeKind != MembershipRoleAssignmentScopeKind.SelectedProperties)
        {
            return;
        }

        var ids = selectedPropertyIds.Distinct().ToArray();
        if (ids.Length != selectedPropertyIds.Count || ids.Any(id => id <= 0))
        {
            throw new DomainValidationException("Selected properties must be unique positive identifiers.");
        }

        var matchingCount = await persistence.Query<Property>()
            .AsNoTracking()
            .CountAsync(property => ids.Contains(property.Id) && property.PortfolioId == portfolioId, ct);
        if (matchingCount != ids.Length)
        {
            throw new DomainValidationException("Every selected property must belong to the current workspace.");
        }
    }

    public static MembershipRoleAssignment NewAssignment(
        WorkspaceMembership membership,
        int portfolioId,
        RoleProfile role,
        MembershipRoleAssignmentScopeKind scopeKind,
        IEnumerable<int> selectedPropertyIds,
        DateTime effectiveFromUtc,
        DateTime changedAtUtc)
    {
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolioId,
            RoleProfileId = role.Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = scopeKind,
            EffectiveFromUtc = effectiveFromUtc,
            CreatedAtUtc = changedAtUtc,
            UpdatedAtUtc = changedAtUtc,
        };
        foreach (var propertyId in selectedPropertyIds)
        {
            assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
            {
                MembershipRoleAssignment = assignment,
                PropertyId = propertyId,
                PortfolioId = portfolioId,
            });
        }

        return assignment;
    }

    public static AtomicSemanticAudit Audit(
        int portfolioId,
        string entityType,
        int entityId,
        AuditLogOperation operation,
        int actorUserId,
        string reason,
        object values) =>
        new(portfolioId, entityType, entityId, operation, actorUserId,
            NewValues: JsonSerializer.Serialize(values), ChangeReason: reason);
}

public sealed class CreateWorkspaceMembershipHandler
    : IAtomicCommandHandler<CreateWorkspaceMembershipCommand, CreateWorkspaceMembershipResult>,
      IAtomicReplayAuthorizer<CreateWorkspaceMembershipCommand>
{
    public async Task<CreateWorkspaceMembershipResult> HandleAsync(
        CreateWorkspaceMembershipCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var changedAtUtc = await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, attempt, null, ct);
        var email = command.Email.Trim();
        var displayName = command.DisplayName.Trim();
        if (email.Length is 0 or > 256 || displayName.Length is 0 or > 200)
        {
            throw new DomainValidationException("A valid email and display name are required.");
        }

        var role = await WorkspaceTeamAuthoritySupport.LoadAndValidateRoleAsync(
            command.RoleProfileKey, command.ScopeKind, command.SelectedPropertyIds,
            command.PortfolioId, attempt.Persistence, ct);
        var normalizedEmail = email.ToUpperInvariant();
        var user = await attempt.Persistence.Query<ApplicationUser>()
            .SingleOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, ct);
        WorkspaceAccessContext? context = null;
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                NormalizedUserName = normalizedEmail,
                Email = email,
                NormalizedEmail = normalizedEmail,
                EmailConfirmed = false,
                DisplayName = displayName,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = changedAtUtc,
            };
            attempt.Persistence.Add(user);
        }
        else
        {
            var existingContextId = await attempt.Persistence.Query<WorkspaceAccessContext>()
                .AsNoTracking()
                .Where(candidate =>
                    candidate.UserId == user.Id &&
                    candidate.PortfolioId == command.PortfolioId)
                .Select(candidate => (int?)candidate.Id)
                .SingleOrDefaultAsync(ct);
            if (existingContextId is not null)
            {
                await attempt.Locking.AcquireAsync(
                    AtomicLockResource.WorkspaceAccessContext,
                    existingContextId.Value,
                    ct);
                context = await attempt.Persistence.Query<WorkspaceAccessContext>()
                    .Include(candidate => candidate.Membership)
                    .SingleAsync(candidate => candidate.Id == existingContextId.Value, ct);
                if (context.Membership is not null)
                {
                    throw new DomainValidationException("This person is already a Team member in the workspace.");
                }
                if (context.Status != WorkspaceAccessContextStatus.Active ||
                    context.SuspendedAtUtc is not null ||
                    context.RevokedAtUtc is not null)
                {
                    throw new DomainValidationException(
                        "This person's existing workspace access is not active and cannot receive a Team assignment.");
                }

                context.UpdatedAtUtc = changedAtUtc;
                context.AdvanceRevision(context.AccessRevision);
            }
        }

        context ??= new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = command.PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = changedAtUtc,
            UpdatedAtUtc = changedAtUtc,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = command.PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = role.DefaultExperience,
            EffectiveFromUtc = command.EffectiveFromUtc,
            CreatedAtUtc = changedAtUtc,
            UpdatedAtUtc = changedAtUtc,
        };
        var assignment = WorkspaceTeamAuthoritySupport.NewAssignment(
            membership, command.PortfolioId, role, command.ScopeKind,
            command.SelectedPropertyIds, command.EffectiveFromUtc, changedAtUtc);
        attempt.Persistence.Add(assignment);
        await attempt.FlushBusinessAsync(ct);

        attempt.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
            command.PortfolioId, nameof(WorkspaceMembership), membership.Id,
            AuditLogOperation.Created, command.ActorUserId, "Workspace member invited",
            new
            {
                AccessContextId = context.Id,
                WorkspaceMembershipId = membership.Id,
                AssignmentId = assignment.Id,
                role.Key,
                command.ScopeKind,
            }));
        return new CreateWorkspaceMembershipResult(
            user.Id, context.Id, membership.Id, assignment.Id, context.AccessRevision,
            string.IsNullOrEmpty(user.PasswordHash));
    }

    public Task AuthorizeReplayAsync(CreateWorkspaceMembershipCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class AddWorkspaceRoleAssignmentHandler
    : IAtomicCommandHandler<AddWorkspaceRoleAssignmentCommand, WorkspaceTeamMutationResult>,
      IAtomicReplayAuthorizer<AddWorkspaceRoleAssignmentCommand>
{
    public async Task<WorkspaceTeamMutationResult> HandleAsync(
        AddWorkspaceRoleAssignmentCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var changedAtUtc = await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, attempt, command.TargetAccessContextId, ct);
        var target = await LoadTargetAsync(command, attempt.Persistence, ct);
        EnsureActive(target);
        var role = await WorkspaceTeamAuthoritySupport.LoadAndValidateRoleAsync(
            command.RoleProfileKey, command.ScopeKind, command.SelectedPropertyIds,
            command.PortfolioId, attempt.Persistence, ct);
        var assignment = WorkspaceTeamAuthoritySupport.NewAssignment(
            target.Membership, command.PortfolioId, role, command.ScopeKind,
            command.SelectedPropertyIds, command.EffectiveFromUtc, changedAtUtc);
        attempt.Persistence.Add(assignment);
        target.Context.UpdatedAtUtc = changedAtUtc;
        target.Context.AdvanceRevision(command.ExpectedRevision);
        attempt.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
            command.PortfolioId, nameof(MembershipRoleAssignment), command.TargetAccessContextId,
            AuditLogOperation.Created, command.ActorUserId, "Team role assignment added",
            new { role.Key, command.ScopeKind, Revision = command.ExpectedRevision + 1 }));
        return Result(target, assignment.Id, command.ExpectedRevision + 1);
    }

    public Task AuthorizeReplayAsync(AddWorkspaceRoleAssignmentCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, persistence, ct);

    internal static async Task<TeamTarget> LoadTargetAsync(
        IWorkspaceAccessMutationCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var target = await persistence.Query<WorkspaceAccessContext>()
            .Where(context => context.Id == command.AccessContextId)
            .Select(context => new TeamTarget(context, context.Membership!))
            .SingleOrDefaultAsync(ct)
            ?? throw new AccessContextUnavailableException();
        ChangeWorkspaceAssignmentScopeHandler.EnsureExpectedRevision(target.Context, command.ExpectedRevision);
        if (target.Membership is null)
        {
            throw new DomainValidationException("The target is not a Team member.");
        }
        return target;
    }

    internal static WorkspaceTeamMutationResult Result(TeamTarget target, int? assignmentId, long revision) =>
        new(target.Context.Id, target.Membership.Id, assignmentId, revision,
            target.Context.Status, target.Membership.Status);

    internal sealed record TeamTarget(WorkspaceAccessContext Context, WorkspaceMembership Membership);

    internal static void EnsureActive(TeamTarget target)
    {
        if (target.Context.Status != WorkspaceAccessContextStatus.Active ||
            target.Membership.Status != WorkspaceMembershipStatus.Active)
        {
            throw new DomainValidationException("Role and scope changes require an active Team membership.");
        }
    }
}

public sealed class EndWorkspaceRoleAssignmentHandler
    : IAtomicCommandHandler<EndWorkspaceRoleAssignmentCommand, WorkspaceTeamMutationResult>,
      IAtomicReplayAuthorizer<EndWorkspaceRoleAssignmentCommand>
{
    public async Task<WorkspaceTeamMutationResult> HandleAsync(
        EndWorkspaceRoleAssignmentCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var changedAtUtc = await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, attempt, command.TargetAccessContextId, ct);
        var target = await AddWorkspaceRoleAssignmentHandler.LoadTargetAsync(command, attempt.Persistence, ct);
        AddWorkspaceRoleAssignmentHandler.EnsureActive(target);
        var assignment = await attempt.Persistence.Query<MembershipRoleAssignment>()
            .SingleOrDefaultAsync(item => item.Id == command.AssignmentId &&
                                          item.WorkspaceMembershipId == target.Membership.Id &&
                                          item.PortfolioId == command.PortfolioId, ct)
            ?? throw new DomainValidationException("The role assignment does not belong to this member.");
        if (command.EffectiveToUtc <= assignment.EffectiveFromUtc)
        {
            throw new DomainValidationException("Assignment end must be after its effective start.");
        }
        assignment.EffectiveToUtc = command.EffectiveToUtc;
        assignment.UpdatedAtUtc = changedAtUtc;
        target.Context.UpdatedAtUtc = changedAtUtc;
        target.Context.AdvanceRevision(command.ExpectedRevision);
        attempt.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
            command.PortfolioId, nameof(MembershipRoleAssignment), assignment.Id,
            AuditLogOperation.Updated, command.ActorUserId, "Team role assignment ended",
            new { command.EffectiveToUtc, Revision = command.ExpectedRevision + 1 }));
        return AddWorkspaceRoleAssignmentHandler.Result(target, assignment.Id, command.ExpectedRevision + 1);
    }

    public Task AuthorizeReplayAsync(EndWorkspaceRoleAssignmentCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class ReplaceWorkspaceAssignmentPropertyScopeHandler
    : IAtomicCommandHandler<ReplaceWorkspaceAssignmentPropertyScopeCommand, WorkspaceTeamMutationResult>,
      IAtomicReplayAuthorizer<ReplaceWorkspaceAssignmentPropertyScopeCommand>
{
    public async Task<WorkspaceTeamMutationResult> HandleAsync(
        ReplaceWorkspaceAssignmentPropertyScopeCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var changedAtUtc = await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, attempt, command.TargetAccessContextId, ct);
        var target = await AddWorkspaceRoleAssignmentHandler.LoadTargetAsync(command, attempt.Persistence, ct);
        AddWorkspaceRoleAssignmentHandler.EnsureActive(target);
        var assignment = await attempt.Persistence.Query<MembershipRoleAssignment>()
            .Where(item => item.Id == command.AssignmentId &&
                           item.WorkspaceMembershipId == target.Membership.Id &&
                           item.PortfolioId == command.PortfolioId)
            .Select(item => new { Entity = item, RoleKey = item.RoleProfile!.Key })
            .SingleOrDefaultAsync(ct)
            ?? throw new DomainValidationException("The role assignment does not belong to this member.");
        WorkspaceTeamAuthoritySupport.ValidateScope(
            assignment.RoleKey, MembershipRoleAssignmentScopeKind.SelectedProperties,
            command.SelectedPropertyIds);
        await WorkspaceTeamAuthoritySupport.ValidatePropertiesAsync(
            command.PortfolioId, command.SelectedPropertyIds,
            MembershipRoleAssignmentScopeKind.SelectedProperties, attempt.Persistence, ct);

        await attempt.Persistence.Query<MembershipRoleAssignmentProperty>()
            .Where(scope => scope.MembershipRoleAssignmentId == command.AssignmentId &&
                            scope.PortfolioId == command.PortfolioId)
            .ExecuteDeleteAsync(ct);
        foreach (var propertyId in command.SelectedPropertyIds)
        {
            attempt.Persistence.Add(new MembershipRoleAssignmentProperty
            {
                MembershipRoleAssignment = assignment.Entity,
                PropertyId = propertyId,
                PortfolioId = command.PortfolioId,
            });
        }
        assignment.Entity.ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties;
        assignment.Entity.UpdatedAtUtc = changedAtUtc;
        target.Context.UpdatedAtUtc = changedAtUtc;
        target.Context.AdvanceRevision(command.ExpectedRevision);
        attempt.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
            command.PortfolioId, nameof(MembershipRoleAssignment), assignment.Entity.Id,
            AuditLogOperation.Updated, command.ActorUserId, "Selected-property scope replaced",
            new { PropertyIds = command.SelectedPropertyIds, Revision = command.ExpectedRevision + 1 }));
        return AddWorkspaceRoleAssignmentHandler.Result(
            target, assignment.Entity.Id, command.ExpectedRevision + 1);
    }

    public Task AuthorizeReplayAsync(ReplaceWorkspaceAssignmentPropertyScopeCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class ChangeWorkspaceMembershipStatusHandler
    : IAtomicCommandHandler<ChangeWorkspaceMembershipStatusCommand, WorkspaceTeamMutationResult>,
      IAtomicReplayAuthorizer<ChangeWorkspaceMembershipStatusCommand>
{
    public async Task<WorkspaceTeamMutationResult> HandleAsync(
        ChangeWorkspaceMembershipStatusCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var changedAtUtc = await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, attempt, command.TargetAccessContextId, ct);
        var target = await AddWorkspaceRoleAssignmentHandler.LoadTargetAsync(command, attempt.Persistence, ct);
        Apply(command, target, changedAtUtc);
        target.Context.UpdatedAtUtc = changedAtUtc;
        target.Membership.UpdatedAtUtc = changedAtUtc;
        target.Context.AdvanceRevision(command.ExpectedRevision);
        attempt.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
            command.PortfolioId, nameof(WorkspaceMembership), target.Membership.Id,
            AuditLogOperation.Updated, command.ActorUserId, $"Team membership {command.Action}",
            new
            {
                command.Action,
                AccessContextStatus = target.Context.Status,
                MembershipStatus = target.Membership.Status,
                Revision = command.ExpectedRevision + 1 }));
        return AddWorkspaceRoleAssignmentHandler.Result(target, null, command.ExpectedRevision + 1);
    }

    public Task AuthorizeReplayAsync(ChangeWorkspaceMembershipStatusCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, persistence, ct);

    private static void Apply(
        ChangeWorkspaceMembershipStatusCommand command,
        AddWorkspaceRoleAssignmentHandler.TeamTarget target,
        DateTime changedAtUtc)
    {
        switch (command.Action)
        {
            case WorkspaceMembershipStatusAction.Suspend
                when target.Context.Status == WorkspaceAccessContextStatus.Active &&
                     target.Membership.Status == WorkspaceMembershipStatus.Active:
                target.Context.Status = WorkspaceAccessContextStatus.Suspended;
                target.Context.SuspendedAtUtc = changedAtUtc;
                target.Membership.Status = WorkspaceMembershipStatus.Suspended;
                target.Membership.SuspendedAtUtc = changedAtUtc;
                return;
            case WorkspaceMembershipStatusAction.Reactivate
                when target.Context.Status == WorkspaceAccessContextStatus.Suspended &&
                     target.Membership.Status == WorkspaceMembershipStatus.Suspended:
                target.Context.Status = WorkspaceAccessContextStatus.Active;
                target.Context.SuspendedAtUtc = null;
                target.Membership.Status = WorkspaceMembershipStatus.Active;
                target.Membership.SuspendedAtUtc = null;
                return;
            case WorkspaceMembershipStatusAction.Revoke
                when (target.Context.Status is WorkspaceAccessContextStatus.Active or
                    WorkspaceAccessContextStatus.Suspended) &&
                     (target.Membership.Status is WorkspaceMembershipStatus.Active or
                    WorkspaceMembershipStatus.Suspended):
                target.Context.Status = WorkspaceAccessContextStatus.Revoked;
                target.Context.SuspendedAtUtc = null;
                target.Context.RevokedAtUtc = changedAtUtc;
                target.Membership.Status = WorkspaceMembershipStatus.Revoked;
                target.Membership.SuspendedAtUtc = null;
                target.Membership.RevokedAtUtc = changedAtUtc;
                target.Membership.EffectiveToUtc ??= changedAtUtc;
                return;
            default:
                throw new DomainValidationException(
                    $"Cannot {command.Action} a {target.Context.Status}/{target.Membership.Status} membership.");
        }
    }
}
