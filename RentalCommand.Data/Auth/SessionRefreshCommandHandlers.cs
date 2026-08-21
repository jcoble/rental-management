using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Auth;

public sealed class IssueSessionRefreshCredentialHandler
    : IAtomicCommandHandler<IssueSessionRefreshCredentialCommand, SessionRefreshMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public IssueSessionRefreshCredentialHandler(RentalCommandDbContext db) => _db = db;

    public Task<SessionRefreshMutationResult> HandleAsync(
        IssueSessionRefreshCredentialCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw AuthSessionWriteSupport.RetiredPath();

    public async Task<SessionRefreshMutationResult> ExecuteAsync(
        IssueSessionRefreshCredentialCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.AuthSessionId == Guid.Empty ||
            command.RefreshTokenFamilyId == Guid.Empty ||
            command.CredentialId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        ValidateHash(command.TokenHash, nameof(command.TokenHash));
        ValidateIssueTimes(command);

        await context.AcquireLockAsync(
            "RefreshTokenFamily",
            command.AuthSessionId,
            ct);

        var session = await _db.Set<AuthSession>()
            .Where(item => item.Id == command.AuthSessionId)
            .Select(item => new SessionTarget(
                item,
                item.ActiveAccessContext!.PortfolioId,
                item.ActiveAccessContextId,
                !item.RefreshTokenFamilies.Any() &&
                item.Status == AuthSessionStatus.Active &&
                item.RevokedAtUtc == null &&
                item.ExpiresAtUtc > command.IssuedAtUtc &&
                command.AbsoluteFamilyExpiresAtUtc <= item.ExpiresAtUtc &&
                AccessAuthorityDbFunctions.IsEffective(
                    item.ActiveAccessContextId,
                    item.UserId,
                    command.IssuedAtUtc)))
            .SingleOrDefaultAsync(ct);

        if (session is null || !session.CanIssue)
        {
            return Rejected(command.AuthSessionId, command.RefreshTokenFamilyId, command.CredentialId);
        }

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
            TokenHash = command.TokenHash,
            IssuedAtUtc = command.IssuedAtUtc,
            ExpiresAtUtc = command.ExpiresAtUtc,
        };
        family.Credentials.Add(credential);
        _db.Add(family);
        session.Entity.LastSeenAtUtc = command.IssuedAtUtc;

        context.StageSemanticEvent(Audit(
            session.PortfolioId,
            session.AccessContextId,
            AuditLogOperation.Updated,
            "Refresh credential family issued",
            new { command.AuthSessionId, command.RefreshTokenFamilyId, command.CredentialId }));

        return new SessionRefreshMutationResult(
            SessionRefreshMutationStatus.Issued,
            command.AuthSessionId,
            command.RefreshTokenFamilyId,
            command.CredentialId);
    }

    public Task AuthorizeReplayAsync(
        IssueSessionRefreshCredentialCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw AuthSessionWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        IssueSessionRefreshCredentialCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.AuthSessionId == Guid.Empty ||
            command.RefreshTokenFamilyId == Guid.Empty ||
            command.CredentialId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        ValidateHash(command.TokenHash, nameof(command.TokenHash));
        ValidateIssueTimes(command);

        var exactCredentialExists = await _db.Set<AuthSessionRefreshCredential>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(credential =>
                credential.Id == command.CredentialId &&
                credential.RefreshTokenFamilyId == command.RefreshTokenFamilyId &&
                credential.TokenHash == command.TokenHash &&
                credential.IssuedAtUtc == command.IssuedAtUtc &&
                credential.ExpiresAtUtc == command.ExpiresAtUtc &&
                credential.RefreshTokenFamily!.AuthSessionId == command.AuthSessionId &&
                credential.RefreshTokenFamily.AbsoluteExpiresAtUtc == command.AbsoluteFamilyExpiresAtUtc,
                ct);
        if (exactCredentialExists)
        {
            return;
        }

        var rejectedIssueStillSafe = await _db.Set<AuthSession>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(session =>
                session.Id == command.AuthSessionId &&
                (session.Status != AuthSessionStatus.Active ||
                 session.RevokedAtUtc != null ||
                 session.ExpiresAtUtc <= command.IssuedAtUtc ||
                 session.RefreshTokenFamilies.Any()),
                ct);
        if (!rejectedIssueStillSafe)
        {
            throw new UnauthorizedAccessException("The original refresh issue target is unavailable.");
        }
    }

    private static void ValidateIssueTimes(IssueSessionRefreshCredentialCommand command)
    {
        if (command.ExpiresAtUtc <= command.IssuedAtUtc ||
            command.AbsoluteFamilyExpiresAtUtc <= command.IssuedAtUtc ||
            command.ExpiresAtUtc > command.AbsoluteFamilyExpiresAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command.ExpiresAtUtc),
                "Credential and family expiry must follow issuance, and credential expiry cannot exceed the family.");
        }
    }

    internal static void ValidateHash(string hash, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash, parameterName);
        if (hash.Length > 128)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Token hashes cannot exceed 128 characters.");
        }
    }

    internal static SessionRefreshMutationResult Rejected(
        Guid sessionId,
        Guid familyId,
        Guid credentialId) =>
        new(SessionRefreshMutationStatus.Rejected, sessionId, familyId, credentialId);

    internal static AtomicSemanticAudit Audit(
        int portfolioId,
        int accessContextId,
        AuditLogOperation operation,
        string reason,
        object values) =>
        new(
            portfolioId,
            nameof(WorkspaceAccessContext),
            accessContextId,
            operation,
            ActorLabel: "authentication:refresh-token",
            NewValues: JsonSerializer.Serialize(values),
            ChangeReason: reason);

    private sealed record SessionTarget(
        AuthSession Entity,
        int PortfolioId,
        int AccessContextId,
        bool CanIssue);
}

public sealed class RotateSessionRefreshCredentialHandler
    : IAtomicCommandHandler<RotateSessionRefreshCredentialCommand, SessionRefreshMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public RotateSessionRefreshCredentialHandler(RentalCommandDbContext db) => _db = db;

    private const string ReuseReason = "Refresh credential reuse detected";

    public Task<SessionRefreshMutationResult> HandleAsync(
        RotateSessionRefreshCredentialCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw AuthSessionWriteSupport.RetiredPath();

    public async Task<SessionRefreshMutationResult> ExecuteAsync(
        RotateSessionRefreshCredentialCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        IssueSessionRefreshCredentialHandler.ValidateHash(
            command.PresentedTokenHash,
            nameof(command.PresentedTokenHash));
        IssueSessionRefreshCredentialHandler.ValidateHash(
            command.ReplacementTokenHash,
            nameof(command.ReplacementTokenHash));
        if (command.OperationId == Guid.Empty || command.ReplacementCredentialId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
        if (command.ReplacementExpiresAtUtc <= command.PresentedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command.ReplacementExpiresAtUtc),
                "Replacement expiry must follow presentation.");
        }

        var located = await _db.Set<AuthSessionRefreshCredential>()
            // Refresh is anonymous by design, so no workspace RLS scope exists yet. These three
            // credential/session tables are global auth state; their model filters traverse into
            // workspace-scoped tables and would hide every valid credential from this request.
            // Authority is validated below through the DB-owned effective-context projection.
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.TokenHash == command.PresentedTokenHash)
            .Select(item => new
            {
                item.Id,
                item.RefreshTokenFamilyId,
                AuthSessionId = item.RefreshTokenFamily!.AuthSessionId,
            })
            .SingleOrDefaultAsync(ct);
        if (located is null)
        {
            return IssueSessionRefreshCredentialHandler.Rejected(
                Guid.Empty,
                Guid.Empty,
                Guid.Empty);
        }

        await context.AcquireLockAsync(
            "RefreshTokenFamily",
            located.RefreshTokenFamilyId,
            ct);

        // Re-read after the family lock. The pre-lock lookup discovers only the immutable family id;
        // every eligibility decision below is made against state protected by that transaction lock.
        var target = await LocateAsync(
            command.PresentedTokenHash,
            command.PresentedAtUtc,
            ct);
        if (target is null)
        {
            return IssueSessionRefreshCredentialHandler.Rejected(
                located.AuthSessionId,
                located.RefreshTokenFamilyId,
                located.Id);
        }

        var authority = await AtomicEffectiveLoginContextQueries.EstablishPreAuthenticatedScopeAsync(_db,
            target.Session.Id,
            target.Session.UserId,
            target.AccessContextId,
            command.PresentedAtUtc,
            ct);
        if (authority is null)
        {
            return IssueSessionRefreshCredentialHandler.Rejected(
                target.Session.Id,
                target.Family.Id,
                target.Credential.Id);
        }

        if (target.Credential.ConsumedAtUtc is not null)
        {
            if (target.IsEligible &&
                target.Credential.ConsumedByOperationId == command.OperationId &&
                target.Credential.ReplacedByCredentialId is { } priorReplacementId)
            {
                return new SessionRefreshMutationResult(
                    SessionRefreshMutationStatus.Recovered,
                    target.Session.Id,
                    target.Family.Id,
                    target.Credential.Id,
                    priorReplacementId,
                    target.Session.UserId,
                    target.AccessContextId,
                    authority.PortfolioId,
                    authority.AccessRevision);
            }

            RevokeForReuse(
                target,
                authority.PortfolioId,
                command.PresentedAtUtc,
                context);
            return new SessionRefreshMutationResult(
                SessionRefreshMutationStatus.ReuseDetected,
                target.Session.Id,
                target.Family.Id,
                target.Credential.Id,
                target.Credential.ReplacedByCredentialId);
        }

        if (target.AnotherLiveCredentialExists)
        {
            // Two live leaves are a compromised/contradictory family. Refuse to choose one branch.
            target.Credential.ConsumedAtUtc = command.PresentedAtUtc;
            RevokeForReuse(
                target,
                authority.PortfolioId,
                command.PresentedAtUtc,
                context);
            return new SessionRefreshMutationResult(
                SessionRefreshMutationStatus.ReuseDetected,
                target.Session.Id,
                target.Family.Id,
                target.Credential.Id);
        }

        if (!target.IsEligible)
        {
            return IssueSessionRefreshCredentialHandler.Rejected(
                target.Session.Id,
                target.Family.Id,
                target.Credential.Id);
        }

        var replacement = new AuthSessionRefreshCredential
        {
            Id = command.ReplacementCredentialId,
            RefreshTokenFamilyId = target.Family.Id,
            TokenHash = command.ReplacementTokenHash,
            IssuedAtUtc = command.PresentedAtUtc,
            ExpiresAtUtc = command.ReplacementExpiresAtUtc < target.Family.AbsoluteExpiresAtUtc
                ? command.ReplacementExpiresAtUtc
                : target.Family.AbsoluteExpiresAtUtc,
        };

        target.Credential.ConsumedAtUtc = command.PresentedAtUtc;
        target.Credential.ConsumedByOperationId = command.OperationId;
        target.Credential.ReplacedByCredential = replacement;
        target.Session.LastSeenAtUtc = command.PresentedAtUtc;
        _db.Add(replacement);

        context.StageSemanticEvent(IssueSessionRefreshCredentialHandler.Audit(
            authority.PortfolioId,
            target.AccessContextId,
            AuditLogOperation.Updated,
            "Refresh credential rotated",
            new
            {
                AuthSessionId = target.Session.Id,
                RefreshTokenFamilyId = target.Family.Id,
                CredentialId = target.Credential.Id,
                ReplacementCredentialId = replacement.Id,
                command.OperationId,
            }));

        return new SessionRefreshMutationResult(
            SessionRefreshMutationStatus.Rotated,
            target.Session.Id,
            target.Family.Id,
            target.Credential.Id,
            replacement.Id,
            target.Session.UserId,
            target.AccessContextId,
            authority.PortfolioId,
            authority.AccessRevision);
    }

    public Task AuthorizeReplayAsync(
        RotateSessionRefreshCredentialCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw AuthSessionWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        RotateSessionRefreshCredentialCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        IssueSessionRefreshCredentialHandler.ValidateHash(
            command.PresentedTokenHash,
            nameof(command.PresentedTokenHash));
        IssueSessionRefreshCredentialHandler.ValidateHash(
            command.ReplacementTokenHash,
            nameof(command.ReplacementTokenHash));
        if (command.OperationId == Guid.Empty || command.ReplacementCredentialId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
        if (command.ReplacementExpiresAtUtc <= command.PresentedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(command.ReplacementExpiresAtUtc));
        }

        var exactRotationExists = await _db.Set<AuthSessionRefreshCredential>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(credential =>
                credential.TokenHash == command.PresentedTokenHash &&
                credential.ConsumedByOperationId == command.OperationId &&
                credential.ReplacedByCredentialId == command.ReplacementCredentialId &&
                _db.Set<AuthSessionRefreshCredential>()
                    .IgnoreQueryFilters()
                    .Any(replacement =>
                        replacement.Id == command.ReplacementCredentialId &&
                        replacement.TokenHash == command.ReplacementTokenHash &&
                        replacement.RefreshTokenFamilyId == credential.RefreshTokenFamilyId),
                ct);
        if (exactRotationExists)
        {
            return;
        }

        var reuseOrRejectedPathStillOwned = await _db.Set<AuthSessionRefreshCredential>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(credential =>
                credential.TokenHash == command.PresentedTokenHash &&
                (credential.RevokedAtUtc != null ||
                 credential.RefreshTokenFamily!.RevokedAtUtc != null ||
                 credential.RefreshTokenFamily.AuthSession!.RevokedAtUtc != null ||
                 credential.RefreshTokenFamily.AuthSession.Status != AuthSessionStatus.Active),
                ct);
        if (!reuseOrRejectedPathStillOwned)
        {
            throw new RefreshTokenRotationOwnershipException();
        }
    }

    private async Task<RefreshTarget?> LocateAsync(
        string tokenHash,
        DateTime presentedAtUtc,
        CancellationToken ct)
    {
        return await _db.Set<AuthSessionRefreshCredential>()
            // See the pre-lock lookup above. The effective workspace context is deliberately
            // resolved in a separate security-definer projection after this global auth read.
            .IgnoreQueryFilters()
            .Where(item => item.TokenHash == tokenHash)
            .Select(item => new RefreshTarget(
                item,
                item.RefreshTokenFamily!,
                item.RefreshTokenFamily!.AuthSession!,
                item.RefreshTokenFamily!.AuthSession!.ActiveAccessContextId,
                item.RevokedAtUtc == null &&
                item.ExpiresAtUtc > presentedAtUtc &&
                item.RefreshTokenFamily!.RevokedAtUtc == null &&
                item.RefreshTokenFamily!.AbsoluteExpiresAtUtc > presentedAtUtc &&
                item.RefreshTokenFamily!.AuthSession!.Status == AuthSessionStatus.Active &&
                item.RefreshTokenFamily!.AuthSession!.RevokedAtUtc == null &&
                item.RefreshTokenFamily!.AuthSession!.ExpiresAtUtc > presentedAtUtc,
                item.RefreshTokenFamily!.Credentials.Any(candidate =>
                    candidate.Id != item.Id &&
                    candidate.ConsumedAtUtc == null &&
                    candidate.RevokedAtUtc == null)))
            .SingleOrDefaultAsync(ct);
    }

    private static void RevokeForReuse(
        RefreshTarget target,
        int portfolioId,
        DateTime now,
        IAtomicCommandContext context)
    {
        target.Credential.ReuseDetectedAtUtc ??= now;
        target.Credential.RevokedAtUtc ??= now;
        target.Credential.RevocationReason ??= ReuseReason;
        target.Family.ReuseDetectedAtUtc ??= now;
        target.Family.RevokedAtUtc ??= now;
        target.Family.RevocationReason ??= ReuseReason;
        target.Session.Status = AuthSessionStatus.Revoked;
        target.Session.RevokedAtUtc ??= now;
        target.Session.RevocationReason ??= ReuseReason;

        context.StageSemanticEvent(IssueSessionRefreshCredentialHandler.Audit(
            portfolioId,
            target.AccessContextId,
            AuditLogOperation.Updated,
            ReuseReason,
            new
            {
                AuthSessionId = target.Session.Id,
                RefreshTokenFamilyId = target.Family.Id,
                CredentialId = target.Credential.Id,
                DetectedAtUtc = now,
            }));
    }

    private sealed record RefreshTarget(
        AuthSessionRefreshCredential Credential,
        AuthSessionRefreshTokenFamily Family,
        AuthSession Session,
        int AccessContextId,
        bool IsEligible,
        bool AnotherLiveCredentialExists);
}
