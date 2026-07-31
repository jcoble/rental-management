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
    private readonly RentalCommandDbContext _db;

    public IssueLoginContextSelectionChallengeHandler(RentalCommandDbContext db) => _db = db;

    public async Task<LoginContextSelectionChallengeResult> HandleAsync(
        IssueLoginContextSelectionChallengeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ValidateChallenge(command);
        await context.AcquireLockAsync(
            "LoginContextSelectionChallenge",
            command.ChallengeId,
            ct);

        // Pre-login RLS cannot see authority tables because no AuthSession exists yet. The DB-owned
        // projection returns the first audit root and total effective-context count in one statement.
        var projectedRoot = await AtomicEffectiveLoginContextQueries.ReadRootAsync(_db,
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

        _db.Add(new LoginContextSelectionChallenge
        {
            Id = command.ChallengeId,
            UserId = command.UserId,
            TokenHash = command.ChallengeTokenHash,
            CreatedAtUtc = command.IssuedAtUtc,
            ExpiresAtUtc = command.ExpiresAtUtc,
        });
        context.StageSemanticEvent(new AtomicSemanticAudit(
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

    public async Task AuthorizeReplayAsync(
        IssueLoginContextSelectionChallengeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ValidateChallenge(command);

        var exactChallengeExists = await _db.Set<LoginContextSelectionChallenge>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(challenge =>
                challenge.Id == command.ChallengeId &&
                challenge.UserId == command.UserId &&
                challenge.TokenHash == command.ChallengeTokenHash &&
                challenge.CreatedAtUtc == command.IssuedAtUtc &&
                challenge.ExpiresAtUtc == command.ExpiresAtUtc,
                ct);
        if (exactChallengeExists)
        {
            return;
        }

        var projectedRoot = await AtomicEffectiveLoginContextQueries.ReadRootAsync(
            _db,
            command.UserId,
            command.IssuedAtUtc,
            ct);
        if (projectedRoot is null || projectedRoot.TotalEffectiveContexts < 2)
        {
            return;
        }

        throw new UnauthorizedAccessException("The original login context-selection challenge is unavailable.");
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
    private readonly RentalCommandDbContext _db;

    public StartAuthSessionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<StartAuthSessionResult> HandleAsync(
        StartAuthSessionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        if (command.ContextSelectionChallengeId is { } challengeId)
        {
            await context.AcquireLockAsync(
                "LoginContextSelectionChallenge",
                challengeId,
                ct);
        }

        // A login has no AuthSession yet, so ordinary RLS must not be widened to expose authority
        // tables. Read only the selected effective context through the DB-owned SECURITY DEFINER
        // projection; the function also supplies the total count in this same SQL statement.
        var effectiveAtUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var target = await AtomicEffectiveLoginContextQueries.ReadAsync(_db,
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

            challenge = await _db.Set<LoginContextSelectionChallenge>()
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
        _db.Add(session);
        if (challenge is not null)
        {
            challenge.ConsumedAtUtc = command.IssuedAtUtc;
        }

        context.StageSemanticEvent(new AtomicSemanticAudit(
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
        StartAuthSessionCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var effectiveAtUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var target = await AtomicEffectiveLoginContextQueries.ReadAsync(_db,
            command.UserId,
            command.SelectedAccessContextId,
            effectiveAtUtc,
            ct) ?? throw new UnauthorizedAccessException("The selected workspace is no longer available.");
        if (target.AccessRevision != command.ExpectedAccessRevision)
        {
            throw new UnauthorizedAccessException("The selected workspace access revision changed.");
        }
        var exactSessionExists = await _db.Set<AuthSession>()
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
                session.ExpiresAtUtc > effectiveAtUtc &&
                session.RefreshTokenFamilies.Any(family =>
                    family.Id == command.RefreshTokenFamilyId &&
                    family.AbsoluteExpiresAtUtc == command.AbsoluteFamilyExpiresAtUtc &&
                    family.Credentials.Any(credential =>
                        credential.Id == command.CredentialId &&
                        credential.TokenHash == command.CredentialTokenHash &&
                        credential.IssuedAtUtc == command.IssuedAtUtc &&
                        credential.ExpiresAtUtc == command.CredentialExpiresAtUtc)),
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
