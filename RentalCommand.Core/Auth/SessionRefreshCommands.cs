using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Auth;

public static class SessionRefreshCommandIdentity
{
    public static AtomicCommandIdentity ForStart(Guid operationId) =>
        Create("auth-session:start", operationId);

    public static AtomicCommandIdentity ForContextSelectionChallenge(Guid operationId) =>
        Create("auth-context-selection:issue", operationId);

    public static AtomicCommandIdentity ForContextSwitch(Guid operationId) =>
        Create("auth-context:switch", operationId);

    public static AtomicCommandIdentity ForSessionRevocation(Guid operationId) =>
        Create("auth-session:revoke", operationId);

    public static AtomicCommandIdentity ForIssue(Guid operationId) =>
        Create("session-refresh:issue", operationId);

    public static AtomicCommandIdentity ForRotation(Guid operationId) =>
        Create("session-refresh:rotate", operationId);

    private static AtomicCommandIdentity Create(string commandType, Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(operationId));
        }

        // A caller-retained operation UUID is stable across request retries without placing the raw
        // refresh credential (or any reversible representation of it) in the receipt identity.
        return new AtomicCommandIdentity(commandType, $"operation:{operationId:N}");
    }
}

public sealed record IssueLoginContextSelectionChallengeCommand(
    int UserId,
    Guid ChallengeId,
    string ChallengeTokenHash,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc) : IAtomicCommandData;

public sealed record LoginContextSelectionChallengeResult(
    bool Issued,
    Guid ChallengeId,
    int UserId,
    DateTime ExpiresAtUtc) : IAtomicResultData;

public sealed record StartAuthSessionCommand(
    int UserId,
    int SelectedAccessContextId,
    Guid AuthSessionId,
    Guid RefreshTokenFamilyId,
    Guid CredentialId,
    string CredentialTokenHash,
    DateTime IssuedAtUtc,
    DateTime SessionExpiresAtUtc,
    DateTime CredentialExpiresAtUtc,
    DateTime AbsoluteFamilyExpiresAtUtc,
    Guid? ContextSelectionChallengeId = null,
    string? ContextSelectionChallengeTokenHash = null) : IAtomicCommandData;

public sealed record StartAuthSessionResult(
    bool Started,
    Guid AuthSessionId,
    int UserId,
    int AccessContextId,
    int PortfolioId,
    long AccessRevision,
    Guid RefreshTokenFamilyId,
    Guid CredentialId) : IAtomicResultData;

public sealed record SwitchAuthSessionContextCommand(
    Guid AuthSessionId,
    int UserId,
    int CurrentAccessContextId,
    long CurrentAccessRevision,
    int SelectedAccessContextId,
    DateTime ChangedAtUtc) : IAtomicCommandData;

public sealed record SwitchAuthSessionContextResult(
    bool Switched,
    Guid AuthSessionId,
    int UserId,
    int AccessContextId,
    int PortfolioId,
    long AccessRevision) : IAtomicResultData;

public sealed record RevokeAuthSessionCommand(
    Guid AuthSessionId,
    int UserId,
    int AccessContextId,
    long AccessRevision,
    DateTime RevokedAtUtc,
    string Reason) : IAtomicCommandData;

public sealed record RevokeAuthSessionResult(
    bool Revoked,
    Guid AuthSessionId) : IAtomicResultData;

public sealed record IssueSessionRefreshCredentialCommand(
    Guid AuthSessionId,
    Guid RefreshTokenFamilyId,
    Guid CredentialId,
    string TokenHash,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime AbsoluteFamilyExpiresAtUtc) : IAtomicCommandData;

public sealed record RotateSessionRefreshCredentialCommand(
    Guid OperationId,
    string PresentedTokenHash,
    Guid ReplacementCredentialId,
    string ReplacementTokenHash,
    DateTime PresentedAtUtc,
    DateTime ReplacementExpiresAtUtc) : IAtomicCommandData;

public enum SessionRefreshMutationStatus
{
    Issued,
    Rotated,
    Recovered,
    ReuseDetected,
    Rejected,
}

/// <summary>
/// Receipt-safe refresh mutation result. It deliberately contains no bearer credential; the caller
/// may return only the candidate whose hash and id are confirmed by a successful result.
/// </summary>
public sealed record SessionRefreshMutationResult(
    SessionRefreshMutationStatus Status,
    Guid AuthSessionId,
    Guid RefreshTokenFamilyId,
    Guid CredentialId,
    Guid? ReplacementCredentialId = null) : IAtomicResultData;
