using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Production application boundary for the new atomic session credential model. Bearer material
/// exists only in this process: commands, receipts, audits, and database rows receive hashes and
/// credential ids. A replay reconstructs the same bearer from the credential id stored in the
/// receipt result.
/// </summary>
public interface IAtomicAuthSessionCredentialService
{
    Task<AtomicLoginContextChallengeOutcome> IssueContextSelectionChallengeAsync(
        int userId,
        CancellationToken ct = default);

    Task<AtomicAuthSessionStartOutcome> StartAsync(
        AtomicAuthSessionStartRequest request,
        CancellationToken ct = default);

    Task<AtomicAuthSessionRotationOutcome> RotateAsync(
        AtomicAuthSessionRotationRequest request,
        CancellationToken ct = default);

    Task<SwitchAuthSessionContextResult> SwitchContextAsync(
        SwitchAuthSessionContextCommand command,
        Guid operationId,
        CancellationToken ct = default);

    Task<RevokeAuthSessionResult> RevokeSessionAsync(
        RevokeAuthSessionCommand command,
        Guid operationId,
        CancellationToken ct = default);
}

public sealed record AtomicLoginContextChallengeOutcome(
    bool Issued,
    Guid ChallengeId,
    string? ChallengeBearer,
    DateTime ExpiresAtUtc);

public sealed record AtomicAuthSessionStartRequest(
    Guid OperationId,
    int UserId,
    int SelectedAccessContextId,
    long ExpectedAccessRevision,
    Guid? ContextSelectionChallengeId = null,
    string? ContextSelectionChallengeBearer = null);

public sealed record AtomicAuthSessionStartOutcome(
    bool Started,
    Guid AuthSessionId,
    int UserId,
    int AccessContextId,
    int PortfolioId,
    long AccessRevision,
    string? RefreshBearer,
    AtomicCommandDisposition Disposition);

public sealed record AtomicAuthSessionRotationRequest(
    Guid OperationId,
    string PresentedBearer);

public sealed record AtomicAuthSessionRotationOutcome(
    SessionRefreshMutationStatus Status,
    Guid AuthSessionId,
    Guid RefreshTokenFamilyId,
    string? ReplacementBearer,
    AtomicCommandDisposition? Disposition,
    int? UserId = null,
    int? AccessContextId = null,
    int? PortfolioId = null,
    long? AccessRevision = null);

public sealed class AtomicAuthSessionCredentialService : IAtomicAuthSessionCredentialService
{
    private static readonly AtomicJsonResultCodec<LoginContextSelectionChallengeResult> ChallengeCodec =
        new("auth-context-selection-challenge-result:v1");
    private static readonly AtomicJsonResultCodec<StartAuthSessionResult> StartCodec =
        new("auth-session-start-result:v1");

    private static readonly AtomicJsonResultCodec<SessionRefreshMutationResult> RotationCodec =
        new("auth-session-refresh-rotation-result:v1");
    private static readonly AtomicJsonResultCodec<SwitchAuthSessionContextResult> SwitchCodec =
        new("auth-session-context-switch-result:v1");
    private static readonly AtomicJsonResultCodec<RevokeAuthSessionResult> RevokeCodec =
        new("auth-session-revoke-result:v1");

    private readonly IAtomicUnitOfWork _atomic;
    private readonly RefreshCredentialTokenFactory _tokens;
    private readonly AtomicAuthSessionCredentialOptions _options;
    private readonly IAuthSecurityClock _securityClock;

    public AtomicAuthSessionCredentialService(
        IAtomicUnitOfWork atomic,
        RefreshCredentialTokenFactory tokens,
        IOptions<AtomicAuthSessionCredentialOptions> options,
        IAuthSecurityClock securityClock)
    {
        _atomic = atomic;
        _tokens = tokens;
        _options = options.Value;
        _securityClock = securityClock;
        ValidateLifetimePolicy(_options);
    }

    public async Task<AtomicAuthSessionStartOutcome> StartAsync(
        AtomicAuthSessionStartRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty ||
            request.UserId <= 0 ||
            request.SelectedAccessContextId <= 0 || request.ExpectedAccessRevision <= 0 ||
            request.ContextSelectionChallengeId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if ((request.ContextSelectionChallengeId is null) !=
            (request.ContextSelectionChallengeBearer is null))
        {
            throw new ArgumentException(
                "A context-selection challenge id and bearer must be supplied together.",
                nameof(request));
        }

        var now = _securityClock.UtcNow();
        var authSessionId = DeriveOperationGuid(request.OperationId, "session");
        var refreshTokenFamilyId = DeriveOperationGuid(request.OperationId, "family");
        var credentialId = DeriveOperationGuid(request.OperationId, "credential");
        var candidateBearer = _tokens.CreateBearer(credentialId);
        var credentialExpiresAt = now.AddDays(_options.CredentialLifetimeDays);
        var familyExpiresAt = now.AddDays(_options.FamilyAbsoluteLifetimeDays);
        var sessionExpiresAt = now.AddDays(_options.SessionLifetimeDays);
        var command = new StartAuthSessionCommand(
            request.UserId,
            request.SelectedAccessContextId,
            request.ExpectedAccessRevision,
            authSessionId,
            refreshTokenFamilyId,
            credentialId,
            _tokens.HashBearer(candidateBearer),
            now,
            sessionExpiresAt,
            credentialExpiresAt,
            familyExpiresAt,
            request.ContextSelectionChallengeId,
            request.ContextSelectionChallengeBearer is null
                ? null
                : _tokens.HashBearer(request.ContextSelectionChallengeBearer));

        var outcome = await _atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForStart(request.OperationId),
            command,
            StartCodec,
            ct);

        var value = outcome.Value;
        return new AtomicAuthSessionStartOutcome(
            value.Started,
            value.AuthSessionId,
            value.UserId,
            value.AccessContextId,
            value.PortfolioId,
            value.AccessRevision,
            value.Started ? _tokens.CreateBearer(value.CredentialId) : null,
            outcome.Disposition);
    }

    private static Guid DeriveOperationGuid(Guid operationId, string purpose)
    {
        var material = Encoding.UTF8.GetBytes($"auth-session-start:{operationId:N}:{purpose}");
        try
        {
            return new Guid(SHA256.HashData(material).AsSpan(0, 16));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public async Task<AtomicLoginContextChallengeOutcome> IssueContextSelectionChallengeAsync(
        int userId,
        CancellationToken ct = default)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        var now = _securityClock.UtcNow();
        var challengeId = Guid.NewGuid();
        var challengeBearer = _tokens.CreateBearer(Guid.NewGuid());
        var expiresAt = now.AddMinutes(5);
        var operationId = Guid.NewGuid();
        var outcome = await _atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForContextSelectionChallenge(operationId),
            new IssueLoginContextSelectionChallengeCommand(
                userId,
                challengeId,
                _tokens.HashBearer(challengeBearer),
                now,
                expiresAt),
            ChallengeCodec,
            ct);

        return new AtomicLoginContextChallengeOutcome(
            outcome.Value.Issued,
            outcome.Value.ChallengeId,
            outcome.Value.Issued ? challengeBearer : null,
            outcome.Value.ExpiresAtUtc);
    }

    public async Task<AtomicAuthSessionRotationOutcome> RotateAsync(
        AtomicAuthSessionRotationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(request.OperationId));
        }

        if (!_tokens.TryValidateAndReadCredentialId(request.PresentedBearer, out var presentedCredentialId))
        {
            return new AtomicAuthSessionRotationOutcome(
                SessionRefreshMutationStatus.Rejected,
                Guid.Empty,
                Guid.Empty,
                null,
                null);
        }

        var now = _securityClock.UtcNow();
        var replacementCredentialId = Guid.NewGuid();
        var replacementCandidate = _tokens.CreateBearer(replacementCredentialId);
        var replacementExpiresAt = now.AddDays(_options.CredentialLifetimeDays);
        var command = new RotateSessionRefreshCredentialCommand(
            presentedCredentialId,
            _tokens.HashBearer(request.PresentedBearer),
            replacementCredentialId,
            _tokens.HashBearer(replacementCandidate),
            now,
            replacementExpiresAt);

        var outcome = await _atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForRotation(presentedCredentialId),
            command,
            RotationCodec,
            ct);
        var value = outcome.Value;
        var mayReturnReplacement =
            value.Status is SessionRefreshMutationStatus.Rotated or SessionRefreshMutationStatus.Recovered &&
            value.ReplacementCredentialId is not null;

        return new AtomicAuthSessionRotationOutcome(
            value.Status,
            value.AuthSessionId,
            value.RefreshTokenFamilyId,
            mayReturnReplacement
                ? _tokens.CreateBearer(value.ReplacementCredentialId!.Value)
                : null,
            outcome.Disposition,
            value.UserId,
            value.AccessContextId,
            value.PortfolioId,
            value.AccessRevision);
    }

    public async Task<SwitchAuthSessionContextResult> SwitchContextAsync(
        SwitchAuthSessionContextCommand command,
        Guid operationId,
        CancellationToken ct = default)
    {
        var outcome = await _atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForContextSwitch(operationId),
            command,
            SwitchCodec,
            ct);
        return outcome.Value;
    }

    public async Task<RevokeAuthSessionResult> RevokeSessionAsync(
        RevokeAuthSessionCommand command,
        Guid operationId,
        CancellationToken ct = default)
    {
        var outcome = await _atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForSessionRevocation(operationId),
            command,
            RevokeCodec,
            ct);
        return outcome.Value;
    }

    private static void ValidateLifetimePolicy(AtomicAuthSessionCredentialOptions options)
    {
        if (options.CredentialLifetimeDays <= 0 ||
            options.FamilyAbsoluteLifetimeDays < options.CredentialLifetimeDays ||
            options.SessionLifetimeDays < options.FamilyAbsoluteLifetimeDays)
        {
            throw new InvalidOperationException(
                "Atomic auth-session lifetimes must be positive and ordered credential <= family <= session.");
        }
    }
}
