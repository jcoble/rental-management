using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Development-only startup seeder. The default administrator is created by the same atomic canonical
/// account bootstrap used by password and Google registration; no Admin Identity role or UserAccount
/// bootstrap is maintained in parallel.
/// </summary>
public class IdentitySeeder
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole<int>> _roleManager;
    private readonly RentalCommandDbContext _dbContext;
    private readonly SeedSettings _settings;
    private readonly ITenantPortalProvisioningService _portalProvisioning;
    private readonly ICanonicalAccountBootstrapService _accountBootstrap;
    private readonly ILogger<IdentitySeeder> _logger;
    private readonly TimeProvider _timeProvider;

    public IdentitySeeder(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        RentalCommandDbContext dbContext,
        IOptions<SeedSettings> settings,
        ITenantPortalProvisioningService portalProvisioning,
        ICanonicalAccountBootstrapService accountBootstrap,
        ILogger<IdentitySeeder> logger,
        TimeProvider timeProvider)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _dbContext = dbContext;
        _settings = settings.Value;
        _portalProvisioning = portalProvisioning;
        _accountBootstrap = accountBootstrap;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        // Tenant portal identity is still a separate relationship-auth surface. Management access no
        // longer consumes any of these roles, and fresh administrator bootstrap never writes one.
        await EnsureRolesAsync();

        if (!_settings.Enabled)
        {
            _logger.LogDebug("Identity seeding disabled (Seed:Enabled=false); roles ensured, skipping admin/demo seed.");
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
            ct);
        if (!bootstrap.Succeeded || bootstrap.User?.PortfolioId is not int portfolioId)
        {
            _logger.LogError(
                "Failed to seed canonical administrator {Email}: {Errors}",
                _settings.AdminEmail,
                string.Join("; ", bootstrap.Errors));
            return;
        }

        var portfolio = await _dbContext.Portfolios.SingleAsync(row => row.Id == portfolioId, ct);
        portfolio.Name = _settings.PortfolioName;
        portfolio.ManagementCompanyName = _settings.ManagementCompanyName;
        portfolio.UpdatedAt = _timeProvider.UtcNow();
        await _dbContext.SaveChangesAsync(ct);
        await EnsureTenantPortalAccountsAsync(portfolioId, ct);
        _logger.LogInformation(
            "Seeded canonical administrator {Email} (id {UserId}) in workspace {PortfolioId}.",
            _settings.AdminEmail,
            bootstrap.User.Id,
            portfolioId);
    }

    private async Task EnsureRolesAsync()
    {
        foreach (var role in Enum.GetNames<UserRole>())
        {
            if (!await _roleManager.RoleExistsAsync(role))
            {
                var result = await _roleManager.CreateAsync(new IdentityRole<int>(role));
                if (result.Succeeded)
                {
                    _logger.LogInformation("Seeded role {Role}.", role);
                }
                else
                {
                    _logger.LogError("Failed to seed role {Role}: {Errors}", role,
                        string.Join("; ", result.Errors.Select(e => e.Description)));
                }
            }
        }
    }

    private async Task EnsureTenantPortalAccountsAsync(int portfolioId, CancellationToken ct)
    {
        // Load the candidate tenant ids in ONE query (the "has an email" filter runs DB-side), then
        // provision each through the shared service. Account creation is inherently per-tenant — Identity's
        // UserManager has no batch API — so this loop is per-user round-trips, not in-memory aggregation;
        // it's startup-only and idempotent. The on-demand staff endpoint reuses the same per-tenant method.
        var tenantIds = await _dbContext.Tenants
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.Email != null && t.Email != "")
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);

        if (tenantIds.Count == 0)
        {
            return;
        }

        var created = 0;
        var existed = 0;

        foreach (var tenantId in tenantIds)
        {
            var result = await _portalProvisioning.EnsurePortalAccountForTenantAsync(tenantId, portfolioId, ct);
            switch (result.Status)
            {
                case PortalAccountStatus.Created:
                    created++;
                    break;
                case PortalAccountStatus.AlreadyExisted:
                    existed++;
                    break;
            }
        }

        if (created > 0 || existed > 0)
        {
            _logger.LogInformation(
                "Ensured tenant portal accounts for portfolio {PortfolioId}: {Created} created, {Existed} already existed.",
                portfolioId,
                created,
                existed);
        }
    }
}
