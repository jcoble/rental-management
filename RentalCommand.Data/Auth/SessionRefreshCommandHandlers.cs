using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Auth;

public sealed class IssueSessionRefreshCredentialHandler
    : IAtomicCommandHandler<IssueSessionRefreshCredentialCommand, SessionRefreshMutationResult>
{
    public async Task<SessionRefreshMutationResult> HandleAsync(
        IssueSessionRefreshCredentialCommand command,
        IAtomicWriteAttempt attempt,
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

        await attempt.Locking.AcquireAsync(
            AtomicLockResource.RefreshTokenFamily,
            command.AuthSessionId,
            ct);

        var session = await attempt.Persistence.Query<AuthSession>()
            .Where(item => item.Id == command.AuthSessionId)
            .Select(item => new SessionTarget(
                item,
                item.ActiveAccessContext!.PortfolioId,
                item.ActiveAccessContextId,
                !item.RefreshTokenFamilies.Any() &&
                item.Status == AuthSessionStatus.Active &&
                item.RevokedAtUtc == null &&
                item.ExpiresAtUtc > command.IssuedAtUtc &&
                command.AbsoluteFamilyExpiresAtUtc <= item.ExpiresAtUtc))
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
        attempt.Persistence.Add(family);
        session.Entity.LastSeenAtUtc = command.IssuedAtUtc;

        attempt.StageSemanticEvent(Audit(
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
    private const string ReuseReason = "Refresh credential reuse detected";

    public async Task<SessionRefreshMutationResult> HandleAsync(
        RotateSessionRefreshCredentialCommand command,
        IAtomicWriteAttempt attempt,
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

        var located = await attempt.Persistence.Query<AuthSessionRefreshCredential>()
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

        await attempt.Locking.AcquireAsync(
            AtomicLockResource.RefreshTokenFamily,
            located.RefreshTokenFamilyId,
            ct);

        // Re-read after the family lock. The pre-lock lookup discovers only the immutable family id;
        // every eligibility decision below is made against state protected by that transaction lock.
        var target = await LocateAsync(
            attempt,
            command.PresentedTokenHash,
            command.PresentedAtUtc,
            command.ReplacementExpiresAtUtc,
            ct);
        if (target is null)
        {
            return IssueSessionRefreshCredentialHandler.Rejected(
                located.AuthSessionId,
                located.RefreshTokenFamilyId,
                located.Id);
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
                    priorReplacementId);
            }

            RevokeForReuse(target, command.PresentedAtUtc, attempt);
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
            RevokeForReuse(target, command.PresentedAtUtc, attempt);
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
            ExpiresAtUtc = command.ReplacementExpiresAtUtc,
        };

        target.Credential.ConsumedAtUtc = command.PresentedAtUtc;
        target.Credential.ConsumedByOperationId = command.OperationId;
        target.Credential.ReplacedByCredential = replacement;
        target.Session.LastSeenAtUtc = command.PresentedAtUtc;
        attempt.Persistence.Add(replacement);

        attempt.StageSemanticEvent(IssueSessionRefreshCredentialHandler.Audit(
            target.PortfolioId,
            target.Session.ActiveAccessContextId,
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
            replacement.Id);
    }

    private static async Task<RefreshTarget?> LocateAsync(
        IAtomicWriteAttempt attempt,
        string tokenHash,
        DateTime presentedAtUtc,
        DateTime replacementExpiresAtUtc,
        CancellationToken ct) =>
        await attempt.Persistence.Query<AuthSessionRefreshCredential>()
            .Where(item => item.TokenHash == tokenHash)
            .Select(item => new RefreshTarget(
                item,
                item.RefreshTokenFamily!,
                item.RefreshTokenFamily!.AuthSession!,
                item.RefreshTokenFamily!.AuthSession!.ActiveAccessContext!.PortfolioId,
                item.RefreshTokenFamily!.AuthSession!.ActiveAccessContextId,
                item.RevokedAtUtc == null &&
                item.ExpiresAtUtc > presentedAtUtc &&
                item.RefreshTokenFamily!.RevokedAtUtc == null &&
                item.RefreshTokenFamily!.AbsoluteExpiresAtUtc > presentedAtUtc &&
                replacementExpiresAtUtc <= item.RefreshTokenFamily!.AbsoluteExpiresAtUtc &&
                item.RefreshTokenFamily!.AuthSession!.Status == AuthSessionStatus.Active &&
                item.RefreshTokenFamily!.AuthSession!.RevokedAtUtc == null &&
                item.RefreshTokenFamily!.AuthSession!.ExpiresAtUtc > presentedAtUtc,
                item.RefreshTokenFamily!.Credentials.Any(candidate =>
                    candidate.Id != item.Id &&
                    candidate.ConsumedAtUtc == null &&
                    candidate.RevokedAtUtc == null)))
            .SingleOrDefaultAsync(ct);

    private static void RevokeForReuse(
        RefreshTarget target,
        DateTime now,
        IAtomicWriteAttempt attempt)
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

        attempt.StageSemanticEvent(IssueSessionRefreshCredentialHandler.Audit(
            target.PortfolioId,
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
        int PortfolioId,
        int AccessContextId,
        bool IsEligible,
        bool AnotherLiveCredentialExists);
}
