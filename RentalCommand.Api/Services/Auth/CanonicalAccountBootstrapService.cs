using System.Security.Cryptography;
using System.Text;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Services.Auth;

public sealed record CanonicalAccountBootstrapResult(
    ApplicationUser? User,
    int? PortfolioId,
    int? AccessContextId,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => User is not null;
}

public sealed record CanonicalWorkspaceBootstrapOptions(
    string PortfolioName,
    string ManagementCompanyName);

public interface ICanonicalAccountBootstrapService
{
    Task<CanonicalAccountBootstrapResult> CreateAsync(
        string email,
        string displayName,
        string? password,
        bool emailConfirmed,
        string operationKey,
        CanonicalWorkspaceBootstrapOptions? workspace = null,
        CancellationToken ct = default);
}

/// <summary>
/// Prepares Identity validation and password hashing outside the database transaction, then executes
/// the only fresh-account bootstrap through the receipt-backed atomic kernel.
/// </summary>
public sealed class CanonicalAccountBootstrapService : ICanonicalAccountBootstrapService
{
    private static readonly AtomicJsonResultCodec<BootstrapAccountResult> ResultCodec =
        new("auth-account-bootstrap-result:v1");

    private readonly UserManager<ApplicationUser> _users;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly byte[] _intentKey;

    public CanonicalAccountBootstrapService(
        UserManager<ApplicationUser> users,
        IAtomicUnitOfWork atomic,
        IOptions<AtomicAuthSessionCredentialOptions> credentialOptions)
    {
        _users = users;
        _atomic = atomic;
        _intentKey = Convert.FromBase64String(credentialOptions.Value.SigningKey);
    }

    public async Task<CanonicalAccountBootstrapResult> CreateAsync(
        string email,
        string displayName,
        string? password,
        bool emailConfirmed,
        string operationKey,
        CanonicalWorkspaceBootstrapOptions? workspace = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var normalizedEmail = _users.NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return Failure("Invalid email address");
        }

        var candidate = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName.Trim(),
            EmailConfirmed = emailConfirmed,
            LockoutEnabled = true,
        };
        var validationErrors = new List<string>();
        // Identity's user validator also performs a state-dependent uniqueness query. Duplicate
        // admission belongs inside the locked receipt transaction so an ambiguous retry can replay
        // the completed bootstrap instead of failing before it reaches the receipt.
        if (!new EmailAddressAttribute().IsValid(candidate.Email))
        {
            validationErrors.Add("Invalid email address");
        }
        if (password is not null)
        {
            foreach (var validator in _users.PasswordValidators)
            {
                var validation = await validator.ValidateAsync(_users, candidate, password);
                if (!validation.Succeeded)
                {
                    validationErrors.AddRange(validation.Errors.Select(error => error.Description));
                }
            }
        }
        if (validationErrors.Count > 0)
        {
            return new CanonicalAccountBootstrapResult(null, null, null, validationErrors.Distinct().ToArray());
        }

        var defaultPortfolioName = string.IsNullOrWhiteSpace(candidate.DisplayName)
            ? "My Portfolio"
            : $"{candidate.DisplayName}'s Portfolio";
        var defaultManagementCompanyName = string.IsNullOrWhiteSpace(candidate.DisplayName)
            ? "My Company"
            : candidate.DisplayName;
        var portfolioName = string.IsNullOrWhiteSpace(workspace?.PortfolioName)
            ? defaultPortfolioName
            : workspace.PortfolioName.Trim();
        var managementCompanyName = string.IsNullOrWhiteSpace(workspace?.ManagementCompanyName)
            ? defaultManagementCompanyName
            : workspace.ManagementCompanyName.Trim();
        var ownerName = string.IsNullOrWhiteSpace(candidate.DisplayName)
            ? email.Split('@')[0]
            : candidate.DisplayName;
        var passwordHash = password is null ? null : _users.PasswordHasher.HashPassword(candidate, password);
        var intentHash = CreateIntentHash(normalizedEmail, password ?? "external-account", emailConfirmed.ToString());
        var command = new BootstrapAccountCommand(
            email.Trim(),
            normalizedEmail,
            candidate.DisplayName,
            passwordHash,
            intentHash,
            emailConfirmed,
            portfolioName,
            managementCompanyName,
            ownerName,
            CreateLockId(normalizedEmail));
        var keyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationKey)))
            .ToLowerInvariant();
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("auth.account.bootstrap", $"email:{keyDigest}"),
            command,
            ResultCodec,
            ct);
        if (outcome.Value.Outcome == BootstrapAccountOutcome.DuplicateEmail)
        {
            return Failure("Email is already registered");
        }

        var user = await _users.FindByIdAsync(outcome.Value.UserId.ToString());
        return user is null
            ? Failure("Account setup did not return a user record.")
            : new CanonicalAccountBootstrapResult(
                user,
                outcome.Value.PortfolioId,
                outcome.Value.AccessContextId,
                []);
    }

    internal string CreateIntentHash(params string[] values)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join("\0", values));
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(_intentKey, payload)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static Guid CreateLockId(string normalizedEmail)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static CanonicalAccountBootstrapResult Failure(string error) =>
        new(null, null, null, [error]);
}
