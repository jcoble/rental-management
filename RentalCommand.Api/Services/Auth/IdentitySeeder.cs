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
/// Idempotent startup seeder. On a fresh database it ensures:
///   * the Identity roles drawn from <see cref="UserRole"/> (Admin, Manager, Agent, Owner, Tenant),
///   * a default <see cref="Portfolio"/>, and
///   * a default Admin <see cref="ApplicationUser"/> (email-confirmed, scoped to that Portfolio)
/// so login can be smoke-tested without any manual data setup. Controlled by <see cref="SeedSettings"/>
/// (off by default; enabled in Development). Safe to run on every boot — every step is a no-op when the
/// target already exists.
/// </summary>
public class IdentitySeeder
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole<int>> _roleManager;
    private readonly RentalCommandDbContext _dbContext;
    private readonly SeedSettings _settings;
    private readonly ITenantPortalProvisioningService _portalProvisioning;
    private readonly ILogger<IdentitySeeder> _logger;
    private readonly TimeProvider _timeProvider;

    public IdentitySeeder(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        RentalCommandDbContext dbContext,
        IOptions<SeedSettings> settings,
        ITenantPortalProvisioningService portalProvisioning,
        ILogger<IdentitySeeder> logger,
        TimeProvider timeProvider)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _dbContext = dbContext;
        _settings = settings.Value;
        _portalProvisioning = portalProvisioning;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        // Identity roles (Admin/Manager/Agent/Owner/Tenant) are STRUCTURAL, not seed data: the whole
        // authorization model and self-service signup (which assigns the owner the Admin role) depend
        // on them existing. Ensure them in EVERY environment — independent of the Seed:Enabled gate,
        // which only controls the demo admin user / portfolio / demo data (Development).
        await EnsureRolesAsync();

        if (!_settings.Enabled)
        {
            _logger.LogDebug("Identity seeding disabled (Seed:Enabled=false); roles ensured, skipping admin/demo seed.");
            return;
        }

        // If any user already exists, the system has been bootstrapped — don't re-seed the admin
        // password or duplicate the portfolio, but still ensure the UserAccount row exists
        // (back-fills databases seeded before the UserAccount step was added).
        if (await _userManager.Users.AnyAsync(ct))
        {
            _logger.LogInformation("Users already present; skipping default admin seed.");
            var existingPortfolio = await _dbContext.Portfolios
                .FirstOrDefaultAsync(p => p.Name == _settings.PortfolioName, ct);
            if (existingPortfolio != null)
            {
                await EnsureAdminUserAccountAsync(existingPortfolio.Id);
                await EnsureTenantPortalAccountsAsync(existingPortfolio.Id, ct);
            }
            return;
        }

        var portfolio = await EnsureDefaultPortfolioAsync(ct);
        await EnsureAdminUserAsync(portfolio.Id);
        await EnsureTenantPortalAccountsAsync(portfolio.Id, ct);
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

    private async Task<Portfolio> EnsureDefaultPortfolioAsync(CancellationToken ct)
    {
        var existing = await _dbContext.Portfolios
            .FirstOrDefaultAsync(p => p.Name == _settings.PortfolioName, ct);
        if (existing != null)
        {
            return existing;
        }

        var now = _timeProvider.UtcNow();
        var portfolio = new Portfolio
        {
            Name = _settings.PortfolioName,
            ManagementCompanyName = _settings.ManagementCompanyName,
            Status = PortfolioStatus.Active,
            Currency = "USD",
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.Portfolios.Add(portfolio);
        await _dbContext.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded default portfolio {PortfolioName} (id {PortfolioId}).", portfolio.Name, portfolio.Id);
        return portfolio;
    }

    private async Task EnsureAdminUserAsync(int portfolioId)
    {
        if (await _userManager.FindByEmailAsync(_settings.AdminEmail) != null)
        {
            // Identity user already exists; ensure the UserAccount row exists too (idempotent).
            await EnsureAdminUserAccountAsync(portfolioId);
            return;
        }

        var now = _timeProvider.UtcNow();

        var admin = new ApplicationUser
        {
            UserName = _settings.AdminEmail,
            Email = _settings.AdminEmail,
            EmailConfirmed = true, // dev convenience: skip the email-verify gate so login works immediately
            DisplayName = _settings.AdminDisplayName,
            PortfolioId = portfolioId,
            CreatedAt = now
        };

        var createResult = await _userManager.CreateAsync(admin, _settings.AdminPassword);
        if (!createResult.Succeeded)
        {
            _logger.LogError("Failed to seed admin user {Email}: {Errors}", _settings.AdminEmail,
                string.Join("; ", createResult.Errors.Select(e => e.Description)));
            return;
        }

        var roleResult = await _userManager.AddToRoleAsync(admin, nameof(UserRole.Admin));
        if (!roleResult.Succeeded)
        {
            _logger.LogError("Failed to add admin role to {Email}: {Errors}", _settings.AdminEmail,
                string.Join("; ", roleResult.Errors.Select(e => e.Description)));
        }

        // Create the domain UserAccount so the admin appears in team-member list queries.
        // Mirrors the pattern in AdminUsersController.Create.
        var accountExists = await _dbContext.UserAccounts
            .AnyAsync(u => u.PortfolioId == portfolioId && u.Email == _settings.AdminEmail);

        if (!accountExists)
        {
            _dbContext.UserAccounts.Add(new UserAccount
            {
                PortfolioId  = portfolioId,
                Email        = _settings.AdminEmail,
                DisplayName  = _settings.AdminDisplayName,
                PasswordHash = string.Empty, // Identity owns the credential
                Role         = UserRole.Admin,
                IsActive     = true,
                CreatedAt    = now,
                UpdatedAt    = now,
            });
            await _dbContext.SaveChangesAsync();
        }

        _logger.LogInformation(
            "Seeded default admin {Email} (id {UserId}) in role Admin, scoped to portfolio {PortfolioId}.",
            _settings.AdminEmail, admin.Id, portfolioId);
    }

    /// <summary>
    /// Idempotent helper: if the Identity user already exists but has no UserAccount row
    /// (e.g. existing DB from before this fix), creates the missing row so the admin
    /// appears in the team-member list without wiping and re-seeding.
    /// </summary>
    private async Task EnsureAdminUserAccountAsync(int portfolioId)
    {
        var accountExists = await _dbContext.UserAccounts
            .AnyAsync(u => u.PortfolioId == portfolioId && u.Email == _settings.AdminEmail);

        if (accountExists)
            return;

        var now = _timeProvider.UtcNow();
        _dbContext.UserAccounts.Add(new UserAccount
        {
            PortfolioId  = portfolioId,
            Email        = _settings.AdminEmail,
            DisplayName  = _settings.AdminDisplayName,
            PasswordHash = string.Empty,
            Role         = UserRole.Admin,
            IsActive     = true,
            CreatedAt    = now,
            UpdatedAt    = now,
        });
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Back-filled missing UserAccount for seeded admin {Email} in portfolio {PortfolioId}.",
            _settings.AdminEmail, portfolioId);
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
