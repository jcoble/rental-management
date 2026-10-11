using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Data.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

public static class WorkspaceTeamWriteSupport
{
    public static TransactionalWrite<CreateWorkspaceMembershipCommand, CreateWorkspaceMembershipResult> Write(
        RentalCommandDbContext db,
        CreateWorkspaceMembershipCommand command)
    {
        var handler = new CreateWorkspaceMembershipRule(db);
        return Build("workspace-team.membership.create", "workspace-team.membership.create.v1",
            command, handler.ExecuteAsync, handler.AuthorizeReplayAsync);
    }

    public static TransactionalWrite<AddWorkspaceRoleAssignmentCommand, WorkspaceTeamMutationResult> Write(
        RentalCommandDbContext db, WorkspaceAccessRevisionGuard guard,
        IMembershipAssignmentScopeValidator validator, AddWorkspaceRoleAssignmentCommand command)
    {
        var handler = new AddWorkspaceRoleAssignmentRule(db, guard, validator);
        return Build("workspace-team.assignment.add", "workspace-team.mutation.v1",
            command, handler.ExecuteAsync, handler.AuthorizeReplayAsync);
    }

    public static TransactionalWrite<EndWorkspaceRoleAssignmentCommand, WorkspaceTeamMutationResult> Write(
        RentalCommandDbContext db, WorkspaceAccessRevisionGuard guard,
        IMembershipAssignmentScopeValidator validator, EndWorkspaceRoleAssignmentCommand command)
    {
        var handler = new EndWorkspaceRoleAssignmentRule(db, guard, validator);
        return Build("workspace-team.assignment.end", "workspace-team.mutation.v1",
            command, handler.ExecuteAsync, handler.AuthorizeReplayAsync);
    }

    public static TransactionalWrite<ReplaceWorkspaceAssignmentPropertyScopeCommand, WorkspaceTeamMutationResult> Write(
        RentalCommandDbContext db, WorkspaceAccessRevisionGuard guard,
        IMembershipAssignmentScopeValidator validator, ReplaceWorkspaceAssignmentPropertyScopeCommand command)
    {
        var handler = new ReplaceWorkspaceAssignmentPropertyScopeRule(db, guard, validator);
        return Build("workspace-team.assignment.properties.replace", "workspace-team.mutation.v1",
            command, handler.ExecuteAsync, handler.AuthorizeReplayAsync);
    }

    public static TransactionalWrite<ChangeWorkspaceMembershipStatusCommand, WorkspaceTeamMutationResult> Write(
        RentalCommandDbContext db, WorkspaceAccessRevisionGuard guard,
        IMembershipAssignmentScopeValidator validator, ChangeWorkspaceMembershipStatusCommand command)
    {
        var handler = new ChangeWorkspaceMembershipStatusRule(db, guard, validator);
        return Build("workspace-team.membership.status", "workspace-team.mutation.v1",
            command, handler.ExecuteAsync, handler.AuthorizeReplayAsync);
    }

    public static TransactionalWrite<ActivateWorkspaceInvitationCommand, ActivateWorkspaceInvitationResult> Write(
        RentalCommandDbContext db,
        ActivateWorkspaceInvitationCommand command)
    {
        var handler = new ActivateWorkspaceInvitationRule(db);
        return Build(
            "workspace-invitation.activate", "workspace-invitation-activation-result:v1", command,
            handler.ExecuteAsync, handler.AuthorizeReplayAsync);
    }

    private static TransactionalWrite<TCommand, TResult> Build<TCommand, TResult>(
        string operationName,
        string resultContract,
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<TResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull => new(
            operationName,  command, resultContract,
            WriteLockPlan.None, executeAsync, authorizeReplayAsync);

}

internal static class WorkspaceTeamAuthoritySupport
{
    public static async Task<DateTime> LockAndAuthorizeActorAsync(
        IWorkspaceTeamAuthorityCommand command,
        IAtomicCommandContext context,
        int? targetAccessContextId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var lockIds = targetAccessContextId is > 0
            ? new[] { command.ActorAccessContextId, targetAccessContextId.Value }.Distinct().Order().ToArray()
            : new[] { command.ActorAccessContextId };
        foreach (var contextId in lockIds)
        {
            await context.AcquireLockAsync("WorkspaceAccessContext", contextId, ct);
        }

        var utcNow = await context.ReadDatabaseClockUtcAsync(ct);
        await AuthorizeActorAsync(command, db, utcNow, ct);
        return utcNow;
    }

    public static async Task AuthorizeReplayAsync(
        IWorkspaceTeamAuthorityCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var utcNow = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        await AuthorizeActorAsync(command, db, utcNow, ct);
    }

    public static void EnsureExpectedRevision(WorkspaceAccessContext context, long expectedRevision)
    {
        if (context.AccessRevision != expectedRevision)
        {
            throw new StaleAccessRevisionException(expectedRevision, context.AccessRevision);
        }
    }

    public static async Task ValidateAndFlushMutationAsync(
        IWorkspaceAccessMutationCommand command,
        IAtomicCommandContext context,
        RentalCommandDbContext db,
        WorkspaceAccessRevisionGuard accessRevisionGuard,
        IMembershipAssignmentScopeValidator assignmentScopeValidator,
        CancellationToken ct)
    {
        var validation = await accessRevisionGuard.ValidatePendingMutationAsync(
            db,
            command.AccessContextId,
            command.ExpectedRevision,
            ct);
        await context.FlushBusinessAsync(ct);
        var assignmentIds = validation.ExistingAssignmentIdsToValidate
            .Concat(validation.AssignmentEntitiesToValidate.Select(assignment => assignment.Id))
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        await assignmentScopeValidator.ValidateAsync(assignmentIds, ct);
    }

    private static async Task AuthorizeActorAsync(
        IWorkspaceTeamAuthorityCommand command,
        RentalCommandDbContext db,
        DateTime utcNow,
        CancellationToken ct)
    {
        if (command is IWorkspaceAccessMutationCommand mutation &&
            mutation.AccessContextId == command.ActorAccessContextId)
        {
            throw new UnauthorizedAccessException(
                "Team members cannot change their own role, scope, or membership status.");
        }

        var sessions = db.Set<AuthSession>().AsNoTracking();
        var memberships = db.Set<WorkspaceMembership>().AsNoTracking();
        var assignments = db.Set<MembershipRoleAssignment>().AsNoTracking();
        var actor = await db.Set<WorkspaceAccessContext>()
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
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleProfileKey);
        if (!AccessCatalog.Roles.Any(role => role.Key == roleProfileKey))
        {
            throw new DomainValidationException("Only the four canonical Team role profiles are accepted.");
        }

        var role = await db.Set<RoleProfile>()
            .SingleOrDefaultAsync(profile => profile.Key == roleProfileKey, ct)
            ?? throw new DomainValidationException("The canonical Team role profile is unavailable.");
        ValidateScope(roleProfileKey, scopeKind, selectedPropertyIds);
        await ValidatePropertiesAsync(portfolioId, selectedPropertyIds, scopeKind, db, ct);
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
        RentalCommandDbContext db,
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

        var matchingCount = await db.Set<Property>()
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

public sealed class CreateWorkspaceMembershipRule
{
    private readonly RentalCommandDbContext _db;

    public CreateWorkspaceMembershipRule(RentalCommandDbContext db) => _db = db;

    public async Task<CreateWorkspaceMembershipResult> ExecuteAsync(
        CreateWorkspaceMembershipCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var changedAtUtc = await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, context, null, _db, ct);
        var email = command.Email.Trim();
        var displayName = command.DisplayName.Trim();
        if (email.Length is 0 or > 256 || displayName.Length is 0 or > 200)
        {
            throw new DomainValidationException("A valid email and display name are required.");
        }
        if (!Uri.TryCreate(command.WebBaseUrl, UriKind.Absolute, out var webBaseUri) ||
            webBaseUri.Scheme is not ("http" or "https"))
        {
            throw new DomainValidationException("A valid web application URL is required for account activation.");
        }

        var role = await WorkspaceTeamAuthoritySupport.LoadAndValidateRoleAsync(
            command.RoleProfileKey, command.ScopeKind, command.SelectedPropertyIds,
            command.PortfolioId, _db, ct);
        var normalizedEmail = email.ToUpperInvariant();
        var user = await _db.Set<ApplicationUser>()
            .SingleOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, ct);
        WorkspaceAccessContext? accessContext = null;
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
                DisplayName = displayName,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = changedAtUtc,
            };
            _db.Add(user);
        }
        else
        {
            var existingContextId = await _db.Set<WorkspaceAccessContext>()
                .AsNoTracking()
                .Where(candidate =>
                    candidate.UserId == user.Id &&
                    candidate.PortfolioId == command.PortfolioId)
                .Select(candidate => (int?)candidate.Id)
                .SingleOrDefaultAsync(ct);
            if (existingContextId is not null)
            {
                await context.AcquireLockAsync(
                    "WorkspaceAccessContext",
                    existingContextId.Value,
                    ct);
                accessContext = await _db.Set<WorkspaceAccessContext>()
                    .Include(candidate => candidate.Membership)
                    .SingleAsync(candidate => candidate.Id == existingContextId.Value, ct);
                if (accessContext.Membership is not null)
                {
                    throw new DomainValidationException("This person is already a Team member in the workspace.");
                }
                if (accessContext.Status != WorkspaceAccessContextStatus.Active ||
                    accessContext.SuspendedAtUtc is not null ||
                    accessContext.RevokedAtUtc is not null)
                {
                    throw new DomainValidationException(
                        "This person's existing workspace access is not active and cannot receive a Team assignment.");
                }

                accessContext.UpdatedAtUtc = changedAtUtc;
                accessContext.AdvanceRevision(accessContext.AccessRevision);
            }
        }

        accessContext ??= new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = command.PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = role.DefaultExperience,
            CreatedAtUtc = changedAtUtc,
            UpdatedAtUtc = changedAtUtc,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = command.PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = role.DefaultExperience,
            EffectiveFromUtc = changedAtUtc,
            CreatedAtUtc = changedAtUtc,
            UpdatedAtUtc = changedAtUtc,
        };
        var assignment = WorkspaceTeamAuthoritySupport.NewAssignment(
            membership, command.PortfolioId, role, command.ScopeKind,
            command.SelectedPropertyIds, changedAtUtc, changedAtUtc);
        _db.Add(assignment);
        await context.FlushBusinessAsync(ct);

        var requiresAccountActivation = string.IsNullOrEmpty(user.PasswordHash);
        if (requiresAccountActivation)
        {
            var rawToken = CreateInvitationToken();
            var invitation = new WorkspaceInvitation
            {
                PortfolioId = command.PortfolioId,
                WorkspaceMembershipId = membership.Id,
                InvitedUserId = user.Id,
                InvitedByUserId = command.ActorUserId,
                TokenHash = HashInvitationToken(rawToken),
                CreatedAtUtc = changedAtUtc,
                ExpiresAtUtc = changedAtUtc.AddDays(7),
            };
            _db.Add(invitation);
            context.StageOutbox(BuildActivationEmail(
                command, membership, role.DisplayName, user, rawToken, changedAtUtc));
        }

        context.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
            command.PortfolioId, nameof(WorkspaceMembership), membership.Id,
            AuditLogOperation.Created, command.ActorUserId, "Workspace member invited",
            new
            {
                AccessContextId = accessContext.Id,
                WorkspaceMembershipId = membership.Id,
                AssignmentId = assignment.Id,
                role.Key,
                command.ScopeKind,
            }));
        return new CreateWorkspaceMembershipResult(
            user.Id, accessContext.Id, membership.Id, assignment.Id, accessContext.AccessRevision,
            requiresAccountActivation);
    }

    public Task AuthorizeReplayAsync(CreateWorkspaceMembershipCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, _db, ct);

    private static string CreateInvitationToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static string HashInvitationToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static OutboxMessage BuildActivationEmail(
        CreateWorkspaceMembershipCommand command,
        WorkspaceMembership membership,
        string roleDisplayName,
        ApplicationUser user,
        string rawToken,
        DateTime createdAtUtc)
    {
        var link = $"{command.WebBaseUrl.TrimEnd('/')}/activate-team?token={Uri.EscapeDataString(rawToken)}";
        var greeting = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email! : user.DisplayName;
        var subject = "Activate your Rental Command account";
        var body = $"""
            Hi {greeting},

            You've been added to Rental Command as {roleDisplayName}. Set your password to activate your account:

            {link}

            This secure link expires in 7 days. If you weren't expecting this invitation, you can ignore this email.

            – The Rental Command Team
            """;
        var htmlBody = $"""
            <!DOCTYPE html>
            <html><body style="font-family:-apple-system,Segoe UI,Roboto,sans-serif;line-height:1.6;color:#1a1a2e;">
              <p>Hi {WebUtility.HtmlEncode(greeting)},</p>
              <p>You've been added to Rental Command as <strong>{WebUtility.HtmlEncode(roleDisplayName)}</strong>.</p>
              <p><a href="{WebUtility.HtmlEncode(link)}">Set your password and activate your account</a>.</p>
              <p style="color:#6b7280;font-size:13px;">This secure link expires in 7 days. If you weren't expecting this invitation, you can ignore this email.</p>
              <p>– The Rental Command Team</p>
            </body></html>
            """;
        return new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "email",
            Payload = JsonSerializer.Serialize(new { to = user.Email, subject, body, htmlBody }),
            IdempotencyKey = $"workspace-invitation:{membership.Id}:activation-v1",
            CreatedAtUtc = createdAtUtc,
            NextAttemptAtUtc = createdAtUtc,
        };
    }
}

public sealed class ActivateWorkspaceInvitationRule
{
    private readonly RentalCommandDbContext _db;

    public ActivateWorkspaceInvitationRule(RentalCommandDbContext db) => _db = db;

    public async Task<ActivateWorkspaceInvitationResult> ExecuteAsync(
        ActivateWorkspaceInvitationCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync(
            "ApplicationUser", command.InvitedUserId, ct);

        var activation = await AtomicAccountSecurityPersistence.ActivateWorkspaceInvitationAsync(_db,
            context, command.InvitationId,
            command.InvitedUserId,
            command.TokenHash,
            command.PasswordHash,
            command.NewSecurityStamp,
            command.NewConcurrencyStamp,
            ct);
        if (activation is null)
        {
            return Invalid(command.InvitedUserId);
        }

        context.UseDatabaseWallClockForAudit(activation.AcceptedAtUtc);
        context.StageSemanticEvent(new AtomicSemanticAudit(
            activation.PortfolioId,
            nameof(ApplicationUser),
            activation.InvitedUserId,
            AuditLogOperation.Updated,
            activation.InvitedUserId,
            ActorLabel: "authentication:account-security",
            NewValues: JsonSerializer.Serialize(new
            {
                SecurityEvent = "WorkspaceInvitationActivated",
                SecurityIntentHash = command.TokenHash,
                TargetUserId = activation.InvitedUserId,
                AuditRootAccessContextId = activation.AccessContextId,
                command.InvitationId,
                activation.WorkspaceMembershipId,
            }),
            ChangeReason: "Workspace invitation activated"), activation.AcceptedAtUtc);

        return new ActivateWorkspaceInvitationResult(
            ActivateWorkspaceInvitationOutcome.Activated,
            activation.InvitedUserId,
            activation.PortfolioId,
            activation.AccessContextId);
    }

    public async Task AuthorizeReplayAsync(
        ActivateWorkspaceInvitationCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var utcNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var remainsUsable = await _db.Set<WorkspaceInvitation>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(invitation =>
                invitation.Id == command.InvitationId &&
                invitation.InvitedUserId == command.InvitedUserId &&
                invitation.TokenHash == command.TokenHash &&
                invitation.AcceptedAtUtc == null &&
                invitation.RevokedAtUtc == null &&
                invitation.ExpiresAtUtc > utcNow &&
                invitation.InvitedUser!.PasswordHash == null,
                ct);
        if (!remainsUsable)
        {
            throw new UnauthorizedAccessException(
                "This activation link is invalid, expired, or has already been used.");
        }
    }

    private static ActivateWorkspaceInvitationResult Invalid(int userId) =>
        new(ActivateWorkspaceInvitationOutcome.Invalid, userId, 0, 0);

    private static void Validate(ActivateWorkspaceInvitationCommand command)
    {
        if (command.InvitationId <= 0 || command.InvitedUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
        if (command.TokenHash.Length != 64 || !command.TokenHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A SHA-256 invitation token hash is required.", nameof(command));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PasswordHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.NewSecurityStamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.NewConcurrencyStamp);
    }
}

public sealed class AddWorkspaceRoleAssignmentRule
{
    private readonly RentalCommandDbContext _db;
    private readonly WorkspaceAccessRevisionGuard _accessRevisionGuard;
    private readonly IMembershipAssignmentScopeValidator _assignmentScopeValidator;

    public AddWorkspaceRoleAssignmentRule(
        RentalCommandDbContext db,
        WorkspaceAccessRevisionGuard accessRevisionGuard,
        IMembershipAssignmentScopeValidator assignmentScopeValidator)
    {
        _db = db;
        _accessRevisionGuard = accessRevisionGuard;
        _assignmentScopeValidator = assignmentScopeValidator;
    }

    public async Task<WorkspaceTeamMutationResult> ExecuteAsync(
        AddWorkspaceRoleAssignmentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var changedAtUtc = await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, context, command.TargetAccessContextId, _db, ct);
        var target = await LoadTargetAsync(command, _db, ct);
        EnsureActive(target);
        var role = await WorkspaceTeamAuthoritySupport.LoadAndValidateRoleAsync(
            command.RoleProfileKey, command.ScopeKind, command.SelectedPropertyIds,
            command.PortfolioId, _db, ct);
        var assignment = WorkspaceTeamAuthoritySupport.NewAssignment(
            target.Membership, command.PortfolioId, role, command.ScopeKind,
            command.SelectedPropertyIds, changedAtUtc, changedAtUtc);
        _db.Add(assignment);
        target.Context.UpdatedAtUtc = changedAtUtc;
        target.Context.AdvanceRevision(command.ExpectedRevision);
        context.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
            command.PortfolioId, nameof(MembershipRoleAssignment), command.TargetAccessContextId,
            AuditLogOperation.Created, command.ActorUserId, "Team role assignment added",
            new { role.Key, command.ScopeKind, Revision = command.ExpectedRevision + 1 }));
        await WorkspaceTeamAuthoritySupport.ValidateAndFlushMutationAsync(
            command, context, _db, _accessRevisionGuard, _assignmentScopeValidator, ct);
        return Result(target, assignment.Id, command.ExpectedRevision + 1);
    }

    public Task AuthorizeReplayAsync(AddWorkspaceRoleAssignmentCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, _db, ct);

    internal static async Task<TeamTarget> LoadTargetAsync(
        IWorkspaceAccessMutationCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var target = await db.Set<WorkspaceAccessContext>()
            .Where(context => context.Id == command.AccessContextId)
            .Select(context => new TeamTarget(context, context.Membership!))
            .SingleOrDefaultAsync(ct)
            ?? throw new AccessContextUnavailableException();
        WorkspaceTeamAuthoritySupport.EnsureExpectedRevision(target.Context, command.ExpectedRevision);
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

public sealed class EndWorkspaceRoleAssignmentRule
{
    private readonly RentalCommandDbContext _db;
    private readonly WorkspaceAccessRevisionGuard _accessRevisionGuard;
    private readonly IMembershipAssignmentScopeValidator _assignmentScopeValidator;

    public EndWorkspaceRoleAssignmentRule(
        RentalCommandDbContext db,
        WorkspaceAccessRevisionGuard accessRevisionGuard,
        IMembershipAssignmentScopeValidator assignmentScopeValidator)
    {
        _db = db;
        _accessRevisionGuard = accessRevisionGuard;
        _assignmentScopeValidator = assignmentScopeValidator;
    }

    public async Task<WorkspaceTeamMutationResult> ExecuteAsync(
        EndWorkspaceRoleAssignmentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var changedAtUtc = await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, context, command.TargetAccessContextId, _db, ct);
        var target = await AddWorkspaceRoleAssignmentRule.LoadTargetAsync(command, _db, ct);
        AddWorkspaceRoleAssignmentRule.EnsureActive(target);
        var assignment = await _db.Set<MembershipRoleAssignment>()
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
        context.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
            command.PortfolioId, nameof(MembershipRoleAssignment), assignment.Id,
            AuditLogOperation.Updated, command.ActorUserId, "Team role assignment ended",
            new { command.EffectiveToUtc, Revision = command.ExpectedRevision + 1 }));
        await WorkspaceTeamAuthoritySupport.ValidateAndFlushMutationAsync(
            command, context, _db, _accessRevisionGuard, _assignmentScopeValidator, ct);
        return AddWorkspaceRoleAssignmentRule.Result(target, assignment.Id, command.ExpectedRevision + 1);
    }

    public Task AuthorizeReplayAsync(EndWorkspaceRoleAssignmentCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class ReplaceWorkspaceAssignmentPropertyScopeRule
{
    private readonly RentalCommandDbContext _db;
    private readonly WorkspaceAccessRevisionGuard _accessRevisionGuard;
    private readonly IMembershipAssignmentScopeValidator _assignmentScopeValidator;

    public ReplaceWorkspaceAssignmentPropertyScopeRule(
        RentalCommandDbContext db,
        WorkspaceAccessRevisionGuard accessRevisionGuard,
        IMembershipAssignmentScopeValidator assignmentScopeValidator)
    {
        _db = db;
        _accessRevisionGuard = accessRevisionGuard;
        _assignmentScopeValidator = assignmentScopeValidator;
    }

    public async Task<WorkspaceTeamMutationResult> ExecuteAsync(
        ReplaceWorkspaceAssignmentPropertyScopeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var changedAtUtc = await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, context, command.TargetAccessContextId, _db, ct);
        var target = await AddWorkspaceRoleAssignmentRule.LoadTargetAsync(command, _db, ct);
        AddWorkspaceRoleAssignmentRule.EnsureActive(target);
        var assignment = await _db.Set<MembershipRoleAssignment>()
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
            MembershipRoleAssignmentScopeKind.SelectedProperties, _db, ct);

        var obsoleteScopes = await _db.Set<MembershipRoleAssignmentProperty>()
            .Where(scope => scope.MembershipRoleAssignmentId == command.AssignmentId &&
                            scope.PortfolioId == command.PortfolioId &&
                            !command.SelectedPropertyIds.Contains(scope.PropertyId))
            .ToListAsync(ct);
        foreach (var scope in obsoleteScopes)
        {
            _db.Remove(scope);
        }

        var existingSelectedPropertyIds = await _db.Set<MembershipRoleAssignmentProperty>()
            .AsNoTracking()
            .Where(scope => scope.MembershipRoleAssignmentId == command.AssignmentId &&
                            scope.PortfolioId == command.PortfolioId &&
                            command.SelectedPropertyIds.Contains(scope.PropertyId))
            .Select(scope => scope.PropertyId)
            .ToArrayAsync(ct);
        foreach (var propertyId in command.SelectedPropertyIds.Except(existingSelectedPropertyIds))
        {
            _db.Add(new MembershipRoleAssignmentProperty
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
        context.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
            command.PortfolioId, nameof(MembershipRoleAssignment), assignment.Entity.Id,
            AuditLogOperation.Updated, command.ActorUserId, "Selected-property scope replaced",
            new { PropertyIds = command.SelectedPropertyIds, Revision = command.ExpectedRevision + 1 }));
        await WorkspaceTeamAuthoritySupport.ValidateAndFlushMutationAsync(
            command, context, _db, _accessRevisionGuard, _assignmentScopeValidator, ct);
        return AddWorkspaceRoleAssignmentRule.Result(
            target, assignment.Entity.Id, command.ExpectedRevision + 1);
    }

    public Task AuthorizeReplayAsync(ReplaceWorkspaceAssignmentPropertyScopeCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class ChangeWorkspaceMembershipStatusRule
{
    private readonly RentalCommandDbContext _db;
    private readonly WorkspaceAccessRevisionGuard _accessRevisionGuard;
    private readonly IMembershipAssignmentScopeValidator _assignmentScopeValidator;

    public ChangeWorkspaceMembershipStatusRule(
        RentalCommandDbContext db,
        WorkspaceAccessRevisionGuard accessRevisionGuard,
        IMembershipAssignmentScopeValidator assignmentScopeValidator)
    {
        _db = db;
        _accessRevisionGuard = accessRevisionGuard;
        _assignmentScopeValidator = assignmentScopeValidator;
    }

    public async Task<WorkspaceTeamMutationResult> ExecuteAsync(
        ChangeWorkspaceMembershipStatusCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var changedAtUtc = await WorkspaceTeamAuthoritySupport.LockAndAuthorizeActorAsync(
            command, context, command.TargetAccessContextId, _db, ct);
        var target = await AddWorkspaceRoleAssignmentRule.LoadTargetAsync(command, _db, ct);
        Apply(command, target, changedAtUtc);
        target.Context.UpdatedAtUtc = changedAtUtc;
        target.Membership.UpdatedAtUtc = changedAtUtc;
        target.Context.AdvanceRevision(command.ExpectedRevision);
        context.StageSemanticEvent(WorkspaceTeamAuthoritySupport.Audit(
            command.PortfolioId, nameof(WorkspaceMembership), target.Membership.Id,
            AuditLogOperation.Updated, command.ActorUserId, $"Team membership {command.Action}",
            new
            {
                command.Action,
                AccessContextStatus = target.Context.Status,
                MembershipStatus = target.Membership.Status,
                Revision = command.ExpectedRevision + 1
            }));
        await WorkspaceTeamAuthoritySupport.ValidateAndFlushMutationAsync(
            command, context, _db, _accessRevisionGuard, _assignmentScopeValidator, ct);
        return AddWorkspaceRoleAssignmentRule.Result(target, null, command.ExpectedRevision + 1);
    }

    public Task AuthorizeReplayAsync(ChangeWorkspaceMembershipStatusCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        WorkspaceTeamAuthoritySupport.AuthorizeReplayAsync(command, _db, ct);

    private static void Apply(
        ChangeWorkspaceMembershipStatusCommand command,
        AddWorkspaceRoleAssignmentRule.TeamTarget target,
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
