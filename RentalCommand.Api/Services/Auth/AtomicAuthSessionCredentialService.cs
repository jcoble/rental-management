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
    Task<AtomicAuthSessionStartOutcome> StartAsync(
        AtomicAuthSessionStartRequest request,
        CancellationToken ct = default);

    Task<AtomicAuthSessionRotationOutcome> RotateAsync(
        AtomicAuthSessionRotationRequest request,
        CancellationToken ct = default);
}

public sealed record AtomicAuthSessionStartRequest(
    Guid OperationId,
    int UserId,
    int SelectedAccessContextId,
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
    AtomicCommandDisposition? Disposition);

public sealed class AtomicAuthSessionCredentialService : IAtomicAuthSessionCredentialService
{
    private static readonly AtomicJsonResultCodec<StartAuthSessionResult> StartCodec =
        new("auth-session-start-result:v1");

    private static readonly AtomicJsonResultCodec<SessionRefreshMutationResult> RotationCodec =
        new("auth-session-refresh-rotation-result:v1");

    private readonly IAtomicUnitOfWork _atomic;
    private readonly RefreshCredentialTokenFactory _tokens;
    private readonly AtomicAuthSessionCredentialOptions _options;
    private readonly TimeProvider _timeProvider;

    public AtomicAuthSessionCredentialService(
        IAtomicUnitOfWork atomic,
        RefreshCredentialTokenFactory tokens,
        IOptions<AtomicAuthSessionCredentialOptions> options,
        TimeProvider timeProvider)
    {
        _atomic = atomic;
        _tokens = tokens;
        _options = options.Value;
        _timeProvider = timeProvider;
        ValidateLifetimePolicy(_options);
    }

    public async Task<AtomicAuthSessionStartOutcome> StartAsync(
        AtomicAuthSessionStartRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty ||
            request.UserId <= 0 ||
            request.SelectedAccessContextId <= 0 ||
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

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var credentialId = Guid.NewGuid();
        var candidateBearer = _tokens.CreateBearer(credentialId);
        var credentialExpiresAt = now.AddDays(_options.CredentialLifetimeDays);
        var familyExpiresAt = now.AddDays(_options.FamilyAbsoluteLifetimeDays);
        var sessionExpiresAt = now.AddDays(_options.SessionLifetimeDays);
        var command = new StartAuthSessionCommand(
            request.UserId,
            request.SelectedAccessContextId,
            Guid.NewGuid(),
            Guid.NewGuid(),
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

    public async Task<AtomicAuthSessionRotationOutcome> RotateAsync(
        AtomicAuthSessionRotationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(request.OperationId));
        }

        if (!_tokens.TryValidateAndReadCredentialId(request.PresentedBearer, out _))
        {
            return new AtomicAuthSessionRotationOutcome(
                SessionRefreshMutationStatus.Rejected,
                Guid.Empty,
                Guid.Empty,
                null,
                null);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var replacementCredentialId = Guid.NewGuid();
        var replacementCandidate = _tokens.CreateBearer(replacementCredentialId);
        var replacementExpiresAt = now.AddDays(_options.CredentialLifetimeDays);
        var command = new RotateSessionRefreshCredentialCommand(
            request.OperationId,
            _tokens.HashBearer(request.PresentedBearer),
            replacementCredentialId,
            _tokens.HashBearer(replacementCandidate),
            now,
            replacementExpiresAt);

        var outcome = await _atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForRotation(request.OperationId),
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
            outcome.Disposition);
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
