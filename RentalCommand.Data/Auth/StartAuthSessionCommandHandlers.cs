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

        // Pre-login RLS cannot see authority tables because no AuthSession exists yet. The DB-owned
        // projection returns the first audit root and total effective-context count in one statement.
        var projectedRoot = await attempt.Persistence.ReadEffectiveLoginContextRootAsync(
            command.UserId, command.IssuedAtUtc, ct);
        var auditRoot = projectedRoot is null
            ? null
            : new ChallengeAuditRoot(
                projectedRoot.AccessContextId,
                projectedRoot.PortfolioId,
                projectedRoot.TotalEffectiveContexts);

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
    : IAtomicCommandHandler<StartAuthSessionCommand, StartAuthSessionResult>,
      IAtomicReplayAuthorizer<StartAuthSessionCommand>
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

        // A login has no AuthSession yet, so ordinary RLS must not be widened to expose authority
        // tables. Read only the selected effective context through the DB-owned SECURITY DEFINER
        // projection; the function also supplies the total count in this same SQL statement.
        var effectiveAtUtc = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var target = await attempt.Persistence.ReadEffectiveLoginContextAsync(
            command.UserId,
            command.SelectedAccessContextId,
            effectiveAtUtc,
            ct);

        if (target is null || target.AccessRevision != command.ExpectedAccessRevision)
        {
            return Rejected(command);
        }

        LoginContextSelectionChallenge? challenge = null;
        if (target.TotalEffectiveContexts > 1)
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
            ActiveAccessContextId = target.AccessContextId,
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
            target.AccessContextId,
            AuditLogOperation.Updated,
            command.UserId,
            ActorLabel: "authentication:session",
            NewValues: JsonSerializer.Serialize(new
            {
                command.AuthSessionId,
                command.UserId,
                AuditRootAccessContextId = target.AccessContextId,
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
            target.AccessContextId,
            target.PortfolioId,
            target.AccessRevision,
            command.RefreshTokenFamilyId,
            command.CredentialId);
    }

    public async Task AuthorizeReplayAsync(
        StartAuthSessionCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var effectiveAtUtc = await persistence.ReadDatabaseClockUtcAsync(ct);
        var target = await persistence.ReadEffectiveLoginContextAsync(
            command.UserId,
            command.SelectedAccessContextId,
            effectiveAtUtc,
            ct) ?? throw new UnauthorizedAccessException("The selected workspace is no longer available.");
        if (target.AccessRevision != command.ExpectedAccessRevision)
        {
            throw new UnauthorizedAccessException("The selected workspace access revision changed.");
        }
        var exactSessionExists = await persistence.Query<AuthSession>()
            // Pre-auth replay has no portfolio GUC yet. The effective-context projection above
            // already proves the live workspace boundary; bypass only the AuthSession model's
            // soft-delete join so this exact global auth row remains visible with blank RLS scope.
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(session =>
                session.Id == command.AuthSessionId &&
                session.UserId == command.UserId &&
                session.ActiveAccessContextId == target.AccessContextId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > effectiveAtUtc,
                ct);
        if (!exactSessionExists)
        {
            throw new UnauthorizedAccessException("The original authentication session is unavailable.");
        }
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
            command.ExpectedAccessRevision <= 0 ||
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

}
