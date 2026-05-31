using Microsoft.AspNetCore.Identity;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Rehash-on-first-login support for migrated accounts.
///
/// Rental Command is a brand-new project, so there are no legacy Lifecycle users to import — the old
/// SQLite database is gone and this service will find nothing to do at runtime today. It exists as a
/// small, reusable piece so that <em>if</em> a future import lands <see cref="ApplicationUser"/> rows
/// with a <c>null</c> PasswordHash (the convention for "credential could not be migrated; force a reset"),
/// the login path already knows how to detect them and route the user into the password-reset flow
/// instead of returning a misleading "invalid password" error.
/// </summary>
public interface IUserMigrationService
{
    /// <summary>
    /// True when the account exists but has no usable password (a migrated/seeded account whose
    /// credential must be (re)established via the reset-password flow before it can sign in).
    /// Backed by Identity's <see cref="UserManager{TUser}.HasPasswordAsync"/> so it stays correct
    /// regardless of how the hash was (or was not) populated.
    /// </summary>
    Task<bool> RequiresPasswordResetAsync(ApplicationUser user);

    /// <summary>
    /// Begins the rehash flow for a migrated user: generates a password-reset token (the caller
    /// delivers it via email once a transport is wired in Phase 4). Returns the token for dev use.
    /// </summary>
    Task<string> BeginPasswordRehashAsync(ApplicationUser user);
}

public class UserMigrationService : IUserMigrationService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<UserMigrationService> _logger;

    public UserMigrationService(UserManager<ApplicationUser> userManager, ILogger<UserMigrationService> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public Task<bool> RequiresPasswordResetAsync(ApplicationUser user) =>
        // HasPasswordAsync returns false when PasswordHash is null/empty.
        _userManager.HasPasswordAsync(user).ContinueWith(t => !t.Result);

    public async Task<string> BeginPasswordRehashAsync(ApplicationUser user)
    {
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        _logger.LogInformation(
            "Migrated user {UserId} ({Email}) has no password set; issued a reset token to rehash on first login.",
            user.Id, user.Email);
        return token;
    }
}
