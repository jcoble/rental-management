using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Atomic;
using Microsoft.Extensions.Options;

namespace RentalCommand.Api.Services.Auth;

public enum AuthErrorType
{
    None,
    Unauthorized,
    BadRequest,
    NotFound
}

public class AuthResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public LoginResponse? Response { get; set; }
    public TokenResult? Tokens { get; set; }
    public IEnumerable<string>? ValidationErrors { get; set; }
    public AuthErrorType ErrorType { get; set; }
    public IReadOnlyList<EffectiveAccessContextOption>? AccessContexts { get; set; }

    /// <summary>
    /// Email-confirmation token surfaced to the controller. Since no email transport is wired in
    /// Phase 0, registration returns this so the confirm-email flow can be exercised end to end.
    /// </summary>
    public string? EmailConfirmationToken { get; set; }
    public int? UserId { get; set; }

    public static AuthResult Ok(LoginResponse response, TokenResult tokens) =>
        new() { Success = true, Response = response, Tokens = tokens };

    public static AuthResult Fail(string error, AuthErrorType errorType = AuthErrorType.Unauthorized) =>
        new() { Success = false, Error = error, ErrorType = errorType };

    public static AuthResult ValidationFail(string error, IEnumerable<string> details) =>
        new() { Success = false, Error = error, ValidationErrors = details, ErrorType = AuthErrorType.BadRequest };

    public static AuthResult ContextSelectionRequired(
        IReadOnlyList<EffectiveAccessContextOption> contexts) =>
        new()
        {
            Success = false,
            Error = "Select a workspace to continue.",
            ErrorType = AuthErrorType.BadRequest,
            AccessContexts = contexts,
        };

    /// <summary>Registration succeeded but the user must confirm their email before logging in.</summary>
    public static AuthResult RegistrationPending(int userId, string emailConfirmationToken) =>
        new() { Success = true, UserId = userId, EmailConfirmationToken = emailConfirmationToken };
}

public class AuthUserResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public UserDto? User { get; set; }
    public AuthErrorType ErrorType { get; set; }

    public static AuthUserResult Ok(UserDto user) => new() { Success = true, User = user };

    public static AuthUserResult Fail(string error, AuthErrorType errorType = AuthErrorType.NotFound) =>
        new() { Success = false, Error = error, ErrorType = errorType };
}

public interface IAuthService
{
    Task<AuthResult> LoginAsync(string email, string password, int? accessContextId = null, string? ipAddress = null, string? userAgent = null);
    Task<AuthResult> LoginExternalAsync(int userId, int? accessContextId = null, CancellationToken ct = default);
    Task<AuthResult> RegisterAsync(RegisterRequest request, string operationKey, CancellationToken ct = default);
    Task<AuthResult> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null);
    Task<AuthUserResult> ConfirmEmailAsync(string userId, string token, string operationKey, CancellationToken ct = default);
    Task<AuthUserResult> GetCurrentUserAsync(string userId);

    /// <summary>
    /// Reset-password stub. Generates a reset token via Identity but does not send an email
    /// (no transport in Phase 0). Always succeeds to avoid email enumeration; returns the token
    /// for development convenience.
    /// </summary>
    Task<string?> GeneratePasswordResetTokenAsync(string email, string operationKey, CancellationToken ct = default);
    Task<AuthUserResult> ResetPasswordAsync(
        string userId, string token, string newPassword, string operationKey, CancellationToken ct = default);

    /// <summary>
    /// Re-sends the email-confirmation message for an unverified account. Always reports success
    /// to the caller (no account enumeration): a missing email or an already-confirmed account is
    /// a silent no-op. Only an existing, still-unconfirmed account actually generates a fresh token
    /// and enqueues the email.
    /// </summary>
    Task<AuthUserResult> ResendVerificationEmailAsync(string email, string operationKey, CancellationToken ct = default);

    /// <summary>
    /// Changes the signed-in user's password. Rejects accounts with no local password (external
    /// login only, e.g. Google) and surfaces Identity's password-policy/validation failures.
    /// </summary>
    Task<AuthUserResult> ChangePasswordAsync(
        ActiveAccessContext active,
        string currentPassword,
        string newPassword,
        string operationKey,
        CancellationToken ct = default);

    Task<UserDto> MapToUserDtoAsync(ApplicationUser user);
}

public class AuthService : IAuthService
{
    private static readonly AtomicJsonResultCodec<ChangePasswordResult> ChangePasswordCodec =
        new("auth-password-change-result:v1");
    private static readonly AtomicJsonResultCodec<ConfirmAccountEmailResult> ConfirmEmailCodec =
        new("auth-email-confirm-result:v1");
    private static readonly AtomicJsonResultCodec<ResetAccountPasswordResult> ResetPasswordCodec =
        new("auth-password-reset-result:v1");
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IAtomicAuthSessionCredentialService _atomicCredentials;
    private readonly ICanonicalAccessTokenService _canonicalTokens;
    private readonly IEffectiveAccessContextSelectionQuery _contextSelection;
    private readonly IAccessEnvelopeQuery _accessEnvelopes;
    private readonly AtomicAuthSessionCredentialOptions _credentialOptions;
    private readonly IAuthEmailSender _emailSender;
    private readonly ICanonicalAccountBootstrapService _accountBootstrap;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly ILogger<AuthService> _logger;
    private readonly TimeProvider _timeProvider;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IAtomicAuthSessionCredentialService atomicCredentials,
        ICanonicalAccessTokenService canonicalTokens,
        IEffectiveAccessContextSelectionQuery contextSelection,
        IAccessEnvelopeQuery accessEnvelopes,
        IOptions<AtomicAuthSessionCredentialOptions> credentialOptions,
        IAuthEmailSender emailSender,
        ICanonicalAccountBootstrapService accountBootstrap,
        IAtomicUnitOfWork atomic,
        ILogger<AuthService> logger,
        TimeProvider timeProvider)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _atomicCredentials = atomicCredentials;
        _canonicalTokens = canonicalTokens;
        _contextSelection = contextSelection;
        _accessEnvelopes = accessEnvelopes;
        _credentialOptions = credentialOptions.Value;
        _emailSender = emailSender;
        _accountBootstrap = accountBootstrap;
        _atomic = atomic;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<AuthResult> LoginAsync(string email, string password, int? accessContextId = null, string? ipAddress = null, string? userAgent = null)
    {
        var user = await _userManager.FindByEmailAsync(email);
        if (user == null)
        {
            return AuthResult.Fail("Invalid email or password");
        }

        // lockoutOnFailure: true enables Identity's lockout (configured in Program.cs: 5 attempts / 5 min).
        // CheckPasswordSignInAsync also rejects an already-locked-out identity up front. Relationship
        // access is evaluated separately when selecting a canonical workspace context.
        var result = await _signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            if (result.IsLockedOut)
            {
                return AuthResult.Fail("Account is locked. Try again later.");
            }
            return AuthResult.Fail("Invalid email or password");
        }

        // Email-confirm gate: block login until the address is verified.
        if (!user.EmailConfirmed)
        {
            return AuthResult.Fail("EMAIL_NOT_VERIFIED: Please verify your email address before logging in.");
        }

        return await StartCanonicalLoginAsync(user, accessContextId);
    }

    public async Task<AuthResult> LoginExternalAsync(
        int userId,
        int? accessContextId = null,
        CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.EmailConfirmed)
        {
            return AuthResult.Fail("The external account is unavailable.");
        }

        return await StartCanonicalLoginAsync(user, accessContextId, ct);
    }

    private async Task<AuthResult> StartCanonicalLoginAsync(
        ApplicationUser user,
        int? accessContextId,
        CancellationToken ct = default)
    {
        // Password/external-provider verification and the email-confirmation gate have already
        // succeeded. The DB exposes only the narrow effective-context option projection here; it
        // does not grant the runtime API generic cross-workspace table access.
        var now = _timeProvider.UtcNow();
        var contexts = await _contextSelection.ListAsync(user.Id, now, ct);
        if (contexts.Count == 0)
        {
            return AuthResult.Fail("This account has no active workspace access.");
        }

        var selected = accessContextId is null
            ? contexts.Count == 1 ? contexts[0] : null
            : contexts.SingleOrDefault(item => item.AccessContextId == accessContextId.Value);
        if (selected is null)
        {
            return contexts.Count > 1 && accessContextId is null
                ? AuthResult.ContextSelectionRequired(contexts)
                : AuthResult.Fail("The selected workspace is not available.", AuthErrorType.BadRequest);
        }

        Guid? challengeId = null;
        string? challengeBearer = null;
        if (contexts.Count > 1)
        {
            var challenge = await _atomicCredentials.IssueContextSelectionChallengeAsync(user.Id, ct);
            if (!challenge.Issued || challenge.ChallengeBearer is null)
            {
                return AuthResult.Fail("Unable to authorize workspace selection.");
            }

            challengeId = challenge.ChallengeId;
            challengeBearer = challenge.ChallengeBearer;
        }

        var session = await _atomicCredentials.StartAsync(new AtomicAuthSessionStartRequest(
            Guid.NewGuid(), user.Id, selected.AccessContextId, selected.AccessRevision,
            challengeId, challengeBearer), ct);
        if (!session.Started || session.RefreshBearer is null)
        {
            return AuthResult.Fail("The selected workspace is no longer available.");
        }

        return await BuildCanonicalAuthResultAsync(
            user,
            session.AuthSessionId,
            session.AccessContextId,
            session.AccessRevision,
            session.RefreshBearer);
    }

    public async Task<AuthResult> RegisterAsync(
        RegisterRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var bootstrap = await _accountBootstrap.CreateAsync(
            request.Email,
            request.DisplayName,
            request.Password,
            emailConfirmed: false,
            operationKey: operationKey,
            ct: ct);
        if (!bootstrap.Succeeded)
        {
            var duplicate = bootstrap.Errors.Contains("Email is already registered");
            return duplicate
                ? AuthResult.Fail("Email is already registered", AuthErrorType.BadRequest)
                : AuthResult.ValidationFail("Registration failed", bootstrap.Errors);
        }

        var user = bootstrap.User!;
        var emailToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        await _emailSender.SendEmailConfirmationAsync(
            user, bootstrap.PortfolioId, emailToken, $"{operationKey}:verification", ct);
        _logger.LogInformation(
            "Registered user {Email} (id {UserId}) with one canonical Administrator context; awaiting email verification.",
            request.Email, user.Id);
        return AuthResult.RegistrationPending(user.Id, emailToken);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null)
    {
        var rotation = await _atomicCredentials.RotateAsync(
            new AtomicAuthSessionRotationRequest(Guid.NewGuid(), refreshToken));
        if (rotation.Status is not (SessionRefreshMutationStatus.Rotated or SessionRefreshMutationStatus.Recovered) ||
            rotation.ReplacementBearer is null)
        {
            return AuthResult.Fail("Invalid or expired refresh token");
        }

        // The atomic rotation transaction validates the live session and effective context and
        // returns only receipt-safe canonical coordinates. No cross-workspace follow-up read is
        // needed by the runtime API credential.
        if (rotation.UserId is null || rotation.AccessContextId is null || rotation.AccessRevision is null)
        {
            return AuthResult.Fail("Invalid or expired refresh token");
        }

        var user = await _userManager.FindByIdAsync(rotation.UserId.Value.ToString());

        if (user == null)
        {
            return AuthResult.Fail("User not found");
        }

        if (!user.EmailConfirmed)
        {
            return AuthResult.Fail("EMAIL_NOT_VERIFIED: Please verify your email address before logging in.");
        }

        return await BuildCanonicalAuthResultAsync(
            user,
            rotation.AuthSessionId,
            rotation.AccessContextId.Value,
            rotation.AccessRevision.Value,
            rotation.ReplacementBearer);
    }

    private async Task<AuthResult> BuildCanonicalAuthResultAsync(
        ApplicationUser user,
        Guid sessionId,
        int accessContextId,
        long accessRevision,
        string refreshBearer)
    {
        var envelope = await _accessEnvelopes.GetAsync(user.Id, accessContextId);
        if (envelope is null || envelope.SelectedContext.AccessRevision != accessRevision)
        {
            return AuthResult.Fail("The selected workspace access changed. Sign in again.");
        }

        var access = _canonicalTokens.Issue(new CanonicalAccessCoordinates(
            user.Id, sessionId, accessContextId, accessRevision));
        var refreshExpiresAt = _timeProvider.UtcNow().AddDays(_credentialOptions.CredentialLifetimeDays);
        var tokens = new TokenResult
        {
            AccessToken = access.Token,
            AccessTokenExpiration = access.ExpiresAtUtc,
            RefreshToken = refreshBearer,
            RefreshTokenExpiration = refreshExpiresAt,
        };

        return AuthResult.Ok(new LoginResponse
        {
            AccessToken = access.Token,
            AccessTokenExpiration = access.ExpiresAtUtc,
            Access = envelope,
            User = new UserDto
            {
                Id = user.Id,
                Email = user.Email ?? string.Empty,
                DisplayName = user.DisplayName ?? user.Email ?? string.Empty,
                EmailVerified = user.EmailConfirmed,
            },
        }, tokens);
    }

    public async Task<AuthUserResult> ConfirmEmailAsync(
        string userId,
        string token,
        string operationKey,
        CancellationToken ct = default)
    {
        if (!int.TryParse(userId, out var parsedUserId) || parsedUserId <= 0)
        {
            return AuthUserResult.Fail("User not found");
        }
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return AuthUserResult.Fail("User not found");
        }

        var tokenValid = await _userManager.VerifyUserTokenAsync(
            user,
            _userManager.Options.Tokens.EmailConfirmationTokenProvider,
            UserManager<ApplicationUser>.ConfirmEmailTokenPurpose,
            token);
        var command = new ConfirmAccountEmailCommand(
            parsedUserId,
            user.SecurityStamp ?? string.Empty,
            tokenValid,
            CreateAuthIntentHash(parsedUserId.ToString(), token, "confirm-email"));
        var result = (await _atomic.ExecuteAsync(
            AuthIdentity("auth.email.confirm", parsedUserId, operationKey),
            command,
            ConfirmEmailCodec,
            ct)).Value;
        if (result.Outcome is ConfirmAccountEmailOutcome.InvalidToken)
        {
            return AuthUserResult.Fail("Email confirmation failed: invalid or expired token", AuthErrorType.BadRequest);
        }
        if (result.Outcome is ConfirmAccountEmailOutcome.UserNotFound)
            return AuthUserResult.Fail("User not found");

        user.EmailConfirmed = true;
        return AuthUserResult.Ok(await MapToUserDtoAsync(user));
    }

    public async Task<AuthUserResult> GetCurrentUserAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return AuthUserResult.Fail("User not found");
        }

        return AuthUserResult.Ok(await MapToUserDtoAsync(user));
    }

    public async Task<string?> GeneratePasswordResetTokenAsync(
        string email,
        string operationKey,
        CancellationToken ct = default)
    {
        var user = await _userManager.FindByEmailAsync(email);
        if (user == null)
        {
            // Do not reveal whether the email exists.
            return null;
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        await _emailSender.SendPasswordResetAsync(user, null, token, operationKey, ct);
        _logger.LogInformation("Password reset token generated for {Email} (id {UserId}). Reset email enqueued.", email, user.Id);
        return token;
    }

    public async Task<AuthUserResult> ResetPasswordAsync(
        string userId,
        string token,
        string newPassword,
        string operationKey,
        CancellationToken ct = default)
    {
        if (!int.TryParse(userId, out var parsedUserId) || parsedUserId <= 0)
        {
            return AuthUserResult.Fail("Invalid or expired reset link.", AuthErrorType.BadRequest);
        }
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return AuthUserResult.Fail("Invalid or expired reset link.", AuthErrorType.BadRequest);
        }

        var validationErrors = new List<IdentityError>();
        foreach (var validator in _userManager.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(_userManager, user, newPassword);
            if (!validation.Succeeded) validationErrors.AddRange(validation.Errors);
        }
        if (validationErrors.Count > 0)
        {
            return AuthUserResult.Fail(
                string.Join("; ", validationErrors.Select(error => error.Description)),
                AuthErrorType.BadRequest);
        }

        var tokenValid = await _userManager.VerifyUserTokenAsync(
            user,
            _userManager.Options.Tokens.PasswordResetTokenProvider,
            UserManager<ApplicationUser>.ResetPasswordTokenPurpose,
            token);
        var command = new ResetAccountPasswordCommand(
            parsedUserId,
            user.SecurityStamp ?? string.Empty,
            tokenValid,
            _userManager.PasswordHasher.HashPassword(user, newPassword),
            CreateAuthIntentHash(parsedUserId.ToString(), token, newPassword, "reset-password"));
        var result = (await _atomic.ExecuteAsync(
            AuthIdentity("auth.password.reset", parsedUserId, operationKey),
            command,
            ResetPasswordCodec,
            ct)).Value;
        if (result.Outcome != ResetAccountPasswordOutcome.Reset)
            return AuthUserResult.Fail("Invalid or expired reset link.", AuthErrorType.BadRequest);

        user.EmailConfirmed = true;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        return AuthUserResult.Ok(await MapToUserDtoAsync(user));
    }

    public async Task<AuthUserResult> ResendVerificationEmailAsync(
        string email,
        string operationKey,
        CancellationToken ct = default)
    {
        var user = await _userManager.FindByEmailAsync(email);

        // Do not reveal whether the email exists, or whether it is already confirmed, to the
        // caller in a way that distinguishes accounts. Both branches return success; only an
        // existing, still-unconfirmed account actually sends an email.
        if (user == null)
        {
            _logger.LogInformation("Resend-verification requested for an unknown email; no-op.");
            return AuthUserResult.Ok(null!);
        }

        if (user.EmailConfirmed)
        {
            // Carry an "already verified" marker so the controller can optionally tailor copy,
            // without leaking it as an enumeration signal (the controller still returns neutral text).
            return new AuthUserResult { Success = true, Error = "Email is already verified" };
        }

        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        await _emailSender.SendEmailConfirmationAsync(user, null, token, operationKey, ct);
        _logger.LogInformation("Verification email resent for {Email} (id {UserId}).", email, user.Id);
        return AuthUserResult.Ok(null!);
    }

    public async Task<AuthUserResult> ChangePasswordAsync(
        ActiveAccessContext active,
        string currentPassword,
        string newPassword,
        string operationKey,
        CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(active.UserId.ToString());
        if (user == null)
        {
            return AuthUserResult.Fail("User not found");
        }

        var validationErrors = new List<IdentityError>();
        foreach (var validator in _userManager.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(_userManager, user, newPassword);
            if (!validation.Succeeded)
            {
                validationErrors.AddRange(validation.Errors);
            }
        }
        if (validationErrors.Count > 0)
        {
            return AuthUserResult.Fail(
                string.Join("; ", validationErrors.Select(error => error.Description)),
                AuthErrorType.BadRequest);
        }

        var command = new ChangePasswordCommand(
            active.SessionId,
            active.UserId,
            active.AccessContextId,
            active.AccessRevision,
            currentPassword,
            newPassword,
            CreatePasswordIntentHash(active.UserId, currentPassword, newPassword));
        var keyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationKey)))
            .ToLowerInvariant();
        ChangePasswordResult changed;
        try
        {
            changed = (await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "auth.password.change",
                    $"{active.UserId}:{active.AccessContextId}:{keyDigest}"),
                command,
                ChangePasswordCodec,
                ct)).Value;
        }
        catch (UnauthorizedAccessException)
        {
            return AuthUserResult.Fail("Active access context not found", AuthErrorType.BadRequest);
        }

        if (changed.Outcome != ChangePasswordOutcome.Changed)
        {
            return changed.Outcome switch
            {
                ChangePasswordOutcome.UserNotFound => AuthUserResult.Fail("User not found"),
                ChangePasswordOutcome.NoLocalPassword => AuthUserResult.Fail(
                    "This account signs in with Google and has no password to change.",
                    AuthErrorType.BadRequest),
                ChangePasswordOutcome.CurrentPasswordIncorrect => AuthUserResult.Fail(
                    "The current password is incorrect.", AuthErrorType.BadRequest),
                _ => AuthUserResult.Fail("Active access context not found", AuthErrorType.BadRequest),
            };
        }

        _logger.LogInformation("Password changed for user {UserId}.", active.UserId);
        return AuthUserResult.Ok(await MapToUserDtoAsync(user));
    }

    private string CreatePasswordIntentHash(int userId, string currentPassword, string newPassword)
    {
        var key = Convert.FromBase64String(_credentialOptions.SigningKey);
        var payload = Encoding.UTF8.GetBytes($"{userId}\0{currentPassword}\0{newPassword}");
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(key, payload)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private string CreateAuthIntentHash(params string[] values)
    {
        var key = Convert.FromBase64String(_credentialOptions.SigningKey);
        var payload = Encoding.UTF8.GetBytes(string.Join("\0", values));
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(key, payload)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static AtomicCommandIdentity AuthIdentity(string commandType, int userId, string operationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var keyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationKey)))
            .ToLowerInvariant();
        return new AtomicCommandIdentity(commandType, $"{userId}:{keyDigest}");
    }

    public Task<UserDto> MapToUserDtoAsync(ApplicationUser user)
    {
        return Task.FromResult(new UserDto
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName ?? user.Email ?? string.Empty,
            EmailVerified = user.EmailConfirmed
        });
    }
}
