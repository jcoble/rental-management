using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Data;
using RentalCommand.Data.Auth;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Result from a Google sign-in exchange. On failure <see cref="Success"/> is false and
/// <see cref="Error"/> contains a safe-to-log (not safe-to-return) message.
/// </summary>
public class GoogleAuthResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public LoginResponse? Response { get; set; }
    public TokenResult? Tokens { get; set; }

    public static GoogleAuthResult Ok(LoginResponse response, TokenResult tokens) =>
        new() { Success = true, Response = response, Tokens = tokens };

    public static GoogleAuthResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public interface IGoogleAuthService
{
    /// <summary>
    /// Exchanges a Google authorization <paramref name="code"/> for an id_token, validates it,
    /// finds or creates the local user, and issues our own JWT token pair.
    /// Returns a failure result when Google sign-in is not configured.
    /// </summary>
    Task<GoogleAuthResult> AuthenticateAsync(string code, string redirectUri, CancellationToken ct = default);

    /// <summary>
    /// Native (mobile) sign-in: validates a Google <paramref name="idToken"/> obtained on-device
    /// (e.g. via <c>google_sign_in</c> configured with a <c>serverClientId</c>), then finds/creates
    /// the user and issues our own JWT pair. Skips the authorization-code exchange the web flow uses.
    /// </summary>
    Task<GoogleAuthResult> AuthenticateWithIdTokenAsync(string idToken, CancellationToken ct = default);
}

/// <summary>
/// Implements <see cref="IGoogleAuthService"/> by exchanging the code at Google's token endpoint,
/// validating the returned id_token via the tokeninfo endpoint, then finding or creating a local
/// <see cref="ApplicationUser"/> and issuing our JWT pair.
/// The entire flow is inert (returns 501-equivalent) when Google credentials are not configured.
/// </summary>
public sealed class GoogleAuthService : IGoogleAuthService
{
    private readonly RentalCommandDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GoogleAuthOptions _options;
    private readonly ICanonicalAccountBootstrapService _accountBootstrap;
    private readonly IAuthService _authService;
    private readonly IRequestWriteExecutor _writes;
    private readonly ILogger<GoogleAuthService> _logger;

    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string TokenInfoEndpoint = "https://oauth2.googleapis.com/tokeninfo";

    public GoogleAuthService(
        RentalCommandDbContext db,
        UserManager<ApplicationUser> userManager,
        IHttpClientFactory httpClientFactory,
        IOptions<GoogleAuthOptions> options,
        ICanonicalAccountBootstrapService accountBootstrap,
        IAuthService authService,
        IRequestWriteExecutor writes,
        ILogger<GoogleAuthService> logger)
    {
        _db = db;
        _userManager = userManager;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _accountBootstrap = accountBootstrap;
        _authService = authService;
        _writes = writes;
        _logger = logger;
    }

    public async Task<GoogleAuthResult> AuthenticateAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return GoogleAuthResult.Fail("Google sign-in is not configured.");
        }

        try
        {
            // Step 1: Exchange authorization code for tokens.
            var idToken = await ExchangeCodeAsync(code, redirectUri, ct);
            if (idToken == null)
            {
                return GoogleAuthResult.Fail("Failed to exchange authorization code with Google.");
            }

            // Step 2: Validate the id_token and extract claims.
            var claims = await ValidateIdTokenAsync(idToken, ct);
            if (claims == null)
            {
                return GoogleAuthResult.Fail("Google id_token validation failed.");
            }

            // Steps 3-4 (find/create the user, provision, issue tokens) are shared with the
            // native id_token flow below.
            return await CompleteSignInAsync(claims, ct);
        }
        catch (Exception ex)
        {
            // Log detail internally; caller must NOT leak this to the response.
            _logger.LogError(ex, "Unexpected error during Google sign-in exchange.");
            return GoogleAuthResult.Fail("Unexpected error during Google sign-in.");
        }
    }

    public async Task<GoogleAuthResult> AuthenticateWithIdTokenAsync(string idToken, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return GoogleAuthResult.Fail("Google sign-in is not configured.");
        }

        try
        {
            // Native clients (mobile) obtain the id_token on-device, so there is no authorization
            // code to exchange — validate the id_token directly, then run the shared completion.
            var claims = await ValidateIdTokenAsync(idToken, ct);
            if (claims == null)
            {
                return GoogleAuthResult.Fail("Google id_token validation failed.");
            }

            return await CompleteSignInAsync(claims, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Google id_token sign-in.");
            return GoogleAuthResult.Fail("Unexpected error during Google sign-in.");
        }
    }

    /// <summary>
    /// Shared completion for both the web authorization-code flow and the native id_token flow:
    /// validates the email claim, finds/creates the local user through the one canonical account
    /// bootstrap, and starts the same workspace-scoped session used by password login.
    /// </summary>
    private async Task<GoogleAuthResult> CompleteSignInAsync(Dictionary<string, string> claims, CancellationToken ct)
    {
        if (!claims.TryGetValue("email", out var email) || string.IsNullOrWhiteSpace(email))
        {
            return GoogleAuthResult.Fail("Google id_token did not contain an email claim.");
        }

        var user = await _userManager.FindByEmailAsync(email);
        claims.TryGetValue("sub", out var googleSubject);
        if (string.IsNullOrWhiteSpace(googleSubject))
        {
            return GoogleAuthResult.Fail("Google id_token did not contain a subject claim.");
        }
        var subjectHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(googleSubject)))
            .ToLowerInvariant();
        if (user is null)
        {
            claims.TryGetValue("name", out var displayName);
            var bootstrap = await _accountBootstrap.CreateAsync(
                email,
                string.IsNullOrWhiteSpace(displayName) ? email : displayName,
                password: null,
                emailConfirmed: true,
                termsPrivacyAccepted: false,
                operationKey: $"google:{subjectHash}",
                ct: ct);
            if (!bootstrap.Succeeded || bootstrap.User is null)
            {
                _logger.LogWarning(
                    "Canonical account bootstrap failed for Google sign-in {Email}: {Errors}",
                    email,
                    string.Join("; ", bootstrap.Errors));
                return GoogleAuthResult.Fail("Failed to create the Rental Command account.");
            }

            user = bootstrap.User;
            _logger.LogInformation(
                "Created Google user {Email} (id {UserId}) with canonical workspace access.",
                email,
                user.Id);
        }
        else if (!user.EmailConfirmed)
        {
            var command = new ConfirmGoogleAccountEmailCommand(
                user.Id,
                user.SecurityStamp ?? string.Empty,
                subjectHash);
            var handler = new ConfirmGoogleAccountEmailHandler(_db);
            var confirmed = (await _writes.ExecuteAsync($"{user.Id}:{subjectHash}",
                AuthSessionWriteSupport.Write(command, handler.ExecuteAsync, handler.AuthorizeAsync), ct)).Value;
            if (confirmed.Outcome is ConfirmAccountEmailOutcome.UserNotFound)
            {
                _logger.LogWarning(
                    "Could not confirm Google-verified account {Email}: account disappeared.", email);
                return GoogleAuthResult.Fail("Failed to verify the Rental Command account.");
            }
            user.EmailConfirmed = true;
        }

        var auth = await _authService.LoginExternalAsync(user.Id, ct: ct);
        return auth.Success && auth.Response is not null && auth.Tokens is not null
            ? GoogleAuthResult.Ok(auth.Response, auth.Tokens)
            : GoogleAuthResult.Fail(auth.Error ?? "Unable to start a Rental Command session.");
    }

    private async Task<string?> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("GoogleAuth");

        var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        });

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(TokenEndpoint, formContent, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP request to Google token endpoint failed.");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Google token endpoint returned {StatusCode}. Body: {Body}", response.StatusCode, body);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("id_token", out var idTokenElement))
        {
            _logger.LogWarning("Google token response did not contain id_token.");
            return null;
        }

        return idTokenElement.GetString();
    }

    private async Task<Dictionary<string, string>?> ValidateIdTokenAsync(string idToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("GoogleAuth");

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync($"{TokenInfoEndpoint}?id_token={Uri.EscapeDataString(idToken)}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP request to Google tokeninfo endpoint failed.");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Google tokeninfo returned {StatusCode}. Body: {Body}", response.StatusCode, body);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            claims[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }

        // Verify aud matches our ClientId.
        if (!claims.TryGetValue("aud", out var aud) || aud != _options.ClientId)
        {
            _logger.LogWarning("Google tokeninfo aud mismatch. Expected {ClientId}, got {Aud}.", _options.ClientId, aud);
            return null;
        }

        // Require a verified email.
        if (!claims.TryGetValue("email_verified", out var emailVerified) ||
            !string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Google account email is not verified for token.");
            return null;
        }

        return claims;
    }

}
