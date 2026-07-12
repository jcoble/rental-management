using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Auth;

public sealed class IssueLoginContextSelectionChallengeHandler
    : IAtomicCommandHandler<IssueLoginContextSelectionChallengeCommand, LoginContextSelectionChallengeResult>
{
    public async Task<LoginContextSelectionChallengeResult> HandleAsync(
        IssueLoginContextSelectionChallengeCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        ValidateChallenge(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.LoginContextSelectionChallenge,
            command.ChallengeId,
            ct);

        // The count and every effective-state predicate remain in one translated SQL statement.
        // Team assignments and Owner/Tenant relationships share this one DB-side effective-state predicate.
        var effectiveContexts = attempt.Persistence.Query<WorkspaceAccessContext>()
            .AsNoTracking()
            .WhereEffectiveAccess(
                attempt.Persistence.Query<WorkspaceMembership>().AsNoTracking(),
                attempt.Persistence.Query<MembershipRoleAssignment>().AsNoTracking(),
                attempt.Persistence.Query<OwnerUserAccess>().AsNoTracking(),
                attempt.Persistence.Query<EffectiveTenantAccessProjection>().AsNoTracking(),
                command.UserId,
                command.IssuedAtUtc);
        var auditRoot = await effectiveContexts
            .OrderBy(context => context.Id)
            .Select(context => new ChallengeAuditRoot(
                context.Id,
                context.PortfolioId,
                effectiveContexts.Count()))
            .FirstOrDefaultAsync(ct);

        if (auditRoot is null || auditRoot.EffectiveContextCount < 2)
        {
            return new LoginContextSelectionChallengeResult(
                false,
                command.ChallengeId,
                command.UserId,
                command.ExpiresAtUtc);
        }

        attempt.Persistence.Add(new LoginContextSelectionChallenge
        {
            Id = command.ChallengeId,
            UserId = command.UserId,
            TokenHash = command.ChallengeTokenHash,
            CreatedAtUtc = command.IssuedAtUtc,
            ExpiresAtUtc = command.ExpiresAtUtc,
        });
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            auditRoot.PortfolioId,
            nameof(WorkspaceAccessContext),
            auditRoot.AccessContextId,
            AuditLogOperation.Updated,
            command.UserId,
            ActorLabel: "authentication:context-selection",
            NewValues: JsonSerializer.Serialize(new
            {
                command.ChallengeId,
                command.UserId,
                AuditRootAccessContextId = auditRoot.AccessContextId,
                auditRoot.EffectiveContextCount,
                command.ExpiresAtUtc,
            }),
            ChangeReason: "Login context selection challenge issued"));

        return new LoginContextSelectionChallengeResult(
            true,
            command.ChallengeId,
            command.UserId,
            command.ExpiresAtUtc);
    }

    private static void ValidateChallenge(IssueLoginContextSelectionChallengeCommand command)
    {
        if (command.UserId <= 0 || command.ChallengeId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(command.UserId));
        }

        IssueSessionRefreshCredentialHandler.ValidateHash(
            command.ChallengeTokenHash,
            nameof(command.ChallengeTokenHash));
        if (command.ExpiresAtUtc <= command.IssuedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(command.ExpiresAtUtc));
        }
    }

    private sealed record ChallengeAuditRoot(
        int AccessContextId,
        int PortfolioId,
        int EffectiveContextCount);
}

public sealed class StartAuthSessionHandler
    : IAtomicCommandHandler<StartAuthSessionCommand, StartAuthSessionResult>
{
    public async Task<StartAuthSessionResult> HandleAsync(
        StartAuthSessionCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        if (command.ContextSelectionChallengeId is { } challengeId)
        {
            await attempt.Locking.AcquireAsync(
                AtomicLockResource.LoginContextSelectionChallenge,
                challengeId,
                ct);
        }

        var effectiveContexts = attempt.Persistence.Query<WorkspaceAccessContext>()
            .WhereEffectiveAccess(
                attempt.Persistence.Query<WorkspaceMembership>(),
                attempt.Persistence.Query<MembershipRoleAssignment>(),
                attempt.Persistence.Query<OwnerUserAccess>(),
                attempt.Persistence.Query<EffectiveTenantAccessProjection>(),
                command.UserId,
                command.IssuedAtUtc);

        // Selected context and total effective-context count are projected by PostgreSQL together.
        var target = await effectiveContexts
            .Where(context => context.Id == command.SelectedAccessContextId)
            .Select(context => new SelectedContext(
                context,
                context.PortfolioId,
                context.AccessRevision,
                effectiveContexts.Count()))
            .SingleOrDefaultAsync(ct);

        if (target is null)
        {
            return Rejected(command);
        }

        LoginContextSelectionChallenge? challenge = null;
        if (target.EffectiveContextCount > 1)
        {
            if (command.ContextSelectionChallengeId is null ||
                string.IsNullOrWhiteSpace(command.ContextSelectionChallengeTokenHash))
            {
                return Rejected(command);
            }

            challenge = await attempt.Persistence.Query<LoginContextSelectionChallenge>()
                .SingleOrDefaultAsync(item =>
                    item.Id == command.ContextSelectionChallengeId &&
                    item.UserId == command.UserId &&
                    item.TokenHash == command.ContextSelectionChallengeTokenHash &&
                    item.ConsumedAtUtc == null &&
                    item.ExpiresAtUtc > command.IssuedAtUtc,
                    ct);
            if (challenge is null)
            {
                return Rejected(command);
            }
        }
        else if (command.ContextSelectionChallengeId is not null ||
                 command.ContextSelectionChallengeTokenHash is not null)
        {
            return Rejected(command);
        }

        var session = new AuthSession
        {
            Id = command.AuthSessionId,
            UserId = command.UserId,
            ActiveAccessContextId = target.Context.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = command.IssuedAtUtc,
            LastSeenAtUtc = command.IssuedAtUtc,
            ExpiresAtUtc = command.SessionExpiresAtUtc,
        };
        var family = new AuthSessionRefreshTokenFamily
        {
            Id = command.RefreshTokenFamilyId,
            AuthSessionId = command.AuthSessionId,
            CreatedAtUtc = command.IssuedAtUtc,
            AbsoluteExpiresAtUtc = command.AbsoluteFamilyExpiresAtUtc,
        };
        var credential = new AuthSessionRefreshCredential
        {
            Id = command.CredentialId,
            RefreshTokenFamilyId = command.RefreshTokenFamilyId,
            TokenHash = command.CredentialTokenHash,
            IssuedAtUtc = command.IssuedAtUtc,
            ExpiresAtUtc = command.CredentialExpiresAtUtc,
        };
        family.Credentials.Add(credential);
        session.RefreshTokenFamilies.Add(family);
        attempt.Persistence.Add(session);
        if (challenge is not null)
        {
            challenge.ConsumedAtUtc = command.IssuedAtUtc;
        }

        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            target.PortfolioId,
            nameof(WorkspaceAccessContext),
            target.Context.Id,
            AuditLogOperation.Updated,
            command.UserId,
            ActorLabel: "authentication:session",
            NewValues: JsonSerializer.Serialize(new
            {
                command.AuthSessionId,
                AuditRootAccessContextId = target.Context.Id,
                target.AccessRevision,
                command.RefreshTokenFamilyId,
                command.CredentialId,
                command.ContextSelectionChallengeId,
            }),
            ChangeReason: "Authentication session started"));

        return new StartAuthSessionResult(
            true,
            command.AuthSessionId,
            command.UserId,
            target.Context.Id,
            target.PortfolioId,
            target.AccessRevision,
            command.RefreshTokenFamilyId,
            command.CredentialId);
    }

    private static StartAuthSessionResult Rejected(StartAuthSessionCommand command) =>
        new(
            false,
            command.AuthSessionId,
            command.UserId,
            command.SelectedAccessContextId,
            0,
            0,
            command.RefreshTokenFamilyId,
            command.CredentialId);

    private static void Validate(StartAuthSessionCommand command)
    {
        if (command.UserId <= 0 ||
            command.SelectedAccessContextId <= 0 ||
            command.AuthSessionId == Guid.Empty ||
            command.RefreshTokenFamilyId == Guid.Empty ||
            command.CredentialId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        IssueSessionRefreshCredentialHandler.ValidateHash(
            command.CredentialTokenHash,
            nameof(command.CredentialTokenHash));
        if (command.SessionExpiresAtUtc <= command.IssuedAtUtc ||
            command.CredentialExpiresAtUtc <= command.IssuedAtUtc ||
            command.AbsoluteFamilyExpiresAtUtc <= command.IssuedAtUtc ||
            command.CredentialExpiresAtUtc > command.AbsoluteFamilyExpiresAtUtc ||
            command.AbsoluteFamilyExpiresAtUtc > command.SessionExpiresAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(command.SessionExpiresAtUtc));
        }

        if (command.ContextSelectionChallengeId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(command.ContextSelectionChallengeId));
        }

        if ((command.ContextSelectionChallengeId is null) !=
            (command.ContextSelectionChallengeTokenHash is null))
        {
            throw new ArgumentException(
                "A context-selection challenge id and token hash must be supplied together.",
                nameof(command));
        }

        if (command.ContextSelectionChallengeTokenHash is { } hash)
        {
            IssueSessionRefreshCredentialHandler.ValidateHash(
                hash,
                nameof(command.ContextSelectionChallengeTokenHash));
        }
    }

    private sealed record SelectedContext(
        WorkspaceAccessContext Context,
        int PortfolioId,
        long AccessRevision,
        int EffectiveContextCount);
}
