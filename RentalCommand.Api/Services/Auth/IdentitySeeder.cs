using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
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
    private readonly ILogger<IdentitySeeder> _logger;

    public IdentitySeeder(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<int>> roleManager,
        RentalCommandDbContext dbContext,
        IOptions<SeedSettings> settings,
        ILogger<IdentitySeeder> logger)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _dbContext = dbContext;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!_settings.Enabled)
        {
            _logger.LogDebug("Identity seeding disabled (Seed:Enabled=false); skipping.");
            return;
        }

        await EnsureRolesAsync();

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

        var now = DateTime.UtcNow;
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

        var now = DateTime.UtcNow;

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

        var now = DateTime.UtcNow;
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
        var tenants = await _dbContext.Tenants
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.Email != null && t.Email != "")
            .OrderBy(t => t.Id)
            .Select(t => new
            {
                t.Id,
                t.Email,
                t.FirstName,
                t.LastName,
            })
            .ToListAsync(ct);

        if (tenants.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var created = 0;
        var linked = 0;

        foreach (var tenant in tenants)
        {
            var email = tenant.Email?.Trim();
            if (string.IsNullOrWhiteSpace(email))
            {
                continue;
            }

            var displayName = $"{tenant.FirstName} {tenant.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = email;
            }

            var identityUser = await _userManager.FindByEmailAsync(email);
            if (identityUser == null)
            {
                identityUser = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    DisplayName = displayName,
                    PortfolioId = portfolioId,
                    TenantId = tenant.Id,
                    CreatedAt = now,
                };

                var createResult = await _userManager.CreateAsync(identityUser, _settings.TenantPassword);
                if (!createResult.Succeeded)
                {
                    _logger.LogWarning(
                        "Failed to seed tenant portal user for tenant {TenantId} ({Email}): {Errors}",
                        tenant.Id,
                        email,
                        string.Join("; ", createResult.Errors.Select(e => e.Description)));
                    continue;
                }

                created++;
            }
            else
            {
                if (identityUser.PortfolioId.HasValue && identityUser.PortfolioId.Value != portfolioId)
                {
                    _logger.LogWarning(
                        "Skipping tenant portal link for {Email}: Identity user belongs to portfolio {ExistingPortfolioId}, not {PortfolioId}.",
                        email,
                        identityUser.PortfolioId.Value,
                        portfolioId);
                    continue;
                }

                if (identityUser.TenantId.HasValue && identityUser.TenantId.Value != tenant.Id)
                {
                    _logger.LogWarning(
                        "Skipping tenant portal link for {Email}: Identity user already belongs to tenant {ExistingTenantId}, not {TenantId}.",
                        email,
                        identityUser.TenantId.Value,
                        tenant.Id);
                    continue;
                }

                var changed = false;
                if (identityUser.PortfolioId != portfolioId)
                {
                    identityUser.PortfolioId = portfolioId;
                    changed = true;
                }

                if (identityUser.TenantId != tenant.Id)
                {
                    identityUser.TenantId = tenant.Id;
                    changed = true;
                }

                if (!identityUser.EmailConfirmed)
                {
                    identityUser.EmailConfirmed = true;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(identityUser.DisplayName))
                {
                    identityUser.DisplayName = displayName;
                    changed = true;
                }

                if (changed)
                {
                    await _userManager.UpdateAsync(identityUser);
                    linked++;
                }
            }

            var roles = await _userManager.GetRolesAsync(identityUser);
            if (!roles.Contains(nameof(UserRole.Tenant)))
            {
                var roleResult = await _userManager.AddToRoleAsync(identityUser, nameof(UserRole.Tenant));
                if (!roleResult.Succeeded)
                {
                    _logger.LogWarning(
                        "Failed to assign Tenant role to portal user {Email}: {Errors}",
                        email,
                        string.Join("; ", roleResult.Errors.Select(e => e.Description)));
                }
            }

            var account = await _dbContext.UserAccounts
                .FirstOrDefaultAsync(u => u.PortfolioId == portfolioId && u.Email == email, ct);

            if (account == null)
            {
                _dbContext.UserAccounts.Add(new UserAccount
                {
                    PortfolioId = portfolioId,
                    TenantId = tenant.Id,
                    Email = email,
                    DisplayName = displayName,
                    PasswordHash = string.Empty,
                    Role = UserRole.Tenant,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                var changed = false;
                if (account.TenantId != tenant.Id)
                {
                    account.TenantId = tenant.Id;
                    changed = true;
                }

                if (account.Role != UserRole.Tenant)
                {
                    account.Role = UserRole.Tenant;
                    changed = true;
                }

                if (changed)
                {
                    account.UpdatedAt = now;
                }
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        if (created > 0 || linked > 0)
        {
            _logger.LogInformation(
                "Ensured tenant portal accounts for portfolio {PortfolioId}: {Created} created, {Linked} linked.",
                portfolioId,
                created,
                linked);
        }
    }
}
