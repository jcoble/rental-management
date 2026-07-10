using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Auth;

public static class SessionRefreshCommandIdentity
{
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

public sealed record IssueSessionRefreshCredentialCommand(
    Guid AuthSessionId,
    Guid RefreshTokenFamilyId,
    Guid CredentialId,
    string TokenHash,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime AbsoluteFamilyExpiresAtUtc) : IAtomicCommandData;

public sealed record RotateSessionRefreshCredentialCommand(
    string PresentedTokenHash,
    Guid ReplacementCredentialId,
    string ReplacementTokenHash,
    DateTime PresentedAtUtc,
    DateTime ReplacementExpiresAtUtc) : IAtomicCommandData;

public enum SessionRefreshMutationStatus
{
    Issued,
    Rotated,
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
    Guid? ReplacementCredentialId = null,
    string? CredentialTokenHash = null,
    string? ReplacementTokenHash = null) : IAtomicResultData;
