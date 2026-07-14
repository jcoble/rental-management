using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Development-only startup seeder. The default administrator is created by the same atomic canonical
/// account bootstrap used by password and Google registration; no Admin Identity role or UserAccount
/// bootstrap is maintained in parallel.
/// </summary>
public class IdentitySeeder
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SeedSettings _settings;
    private readonly ICanonicalAccountBootstrapService _accountBootstrap;
    private readonly ILogger<IdentitySeeder> _logger;

    public IdentitySeeder(
        UserManager<ApplicationUser> userManager,
        IOptions<SeedSettings> settings,
        ICanonicalAccountBootstrapService accountBootstrap,
        ILogger<IdentitySeeder> logger)
    {
        _userManager = userManager;
        _settings = settings.Value;
        _accountBootstrap = accountBootstrap;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!_settings.Enabled)
        {
            _logger.LogDebug("Identity seeding disabled (Seed:Enabled=false); skipping admin/demo seed.");
            return;
        }

        // This is fresh-database development seed only. No legacy backfill or repair path remains.
        if (await _userManager.Users.AnyAsync(ct))
        {
            _logger.LogInformation("Users already present; skipping default admin seed.");
            return;
        }

        var bootstrap = await _accountBootstrap.CreateAsync(
            _settings.AdminEmail,
            _settings.AdminDisplayName,
            _settings.AdminPassword,
            emailConfirmed: true,
            operationKey: $"seed:{_settings.AdminEmail.Trim().ToUpperInvariant()}",
            workspace: new CanonicalWorkspaceBootstrapOptions(
                _settings.PortfolioName,
                _settings.ManagementCompanyName),
            ct: ct);
        if (!bootstrap.Succeeded || bootstrap.User is not { } seededUser
            || bootstrap.PortfolioId is not int portfolioId)
        {
            _logger.LogError(
                "Failed to seed canonical administrator {Email}: {Errors}",
                _settings.AdminEmail,
                string.Join("; ", bootstrap.Errors));
            return;
        }

        _logger.LogInformation(
            "Seeded canonical administrator {Email} (id {UserId}) in workspace {PortfolioId}.",
            _settings.AdminEmail,
            seededUser.Id,
            portfolioId);
    }

}
