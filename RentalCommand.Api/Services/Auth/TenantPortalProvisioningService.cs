using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Auth;

/// <summary>Outcome of ensuring a tenant has a portal login.</summary>
public enum PortalAccountStatus
{
    /// <summary>A new Identity login was created for the tenant.</summary>
    Created,

    /// <summary>An Identity login for the tenant's email already existed (now ensured linked + confirmed).</summary>
    AlreadyExisted,

    /// <summary>The tenant has no email, so no login could be provisioned.</summary>
    NoEmail,

    /// <summary>No tenant with that id exists in the given portfolio (IDOR guard / soft-deleted).</summary>
    TenantNotFound,

    /// <summary>Provisioning failed (Identity error, or the email belongs to a different portfolio/tenant).</summary>
    Failed,
}

/// <summary>Result of <see cref="ITenantPortalProvisioningService.EnsurePortalAccountForTenantAsync"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Email">The tenant's email (when known), so callers can surface it in a message.</param>
/// <param name="DisplayName">The tenant's display name (when known).</param>
/// <param name="Error">Human-readable reason when <see cref="Status"/> is <see cref="PortalAccountStatus.Failed"/>.</param>
public sealed record PortalAccountResult(
    PortalAccountStatus Status,
    string? Email = null,
    string? DisplayName = null,
    string? Error = null);

/// <summary>
/// Provisions a single tenant's portal login on demand. Owns the per-tenant logic the startup
/// <see cref="IdentitySeeder"/> originally inlined (find Identity user by email → create or link →
/// assign the Tenant role → upsert the domain <see cref="UserAccount"/>), so a tenant added after
/// boot can be granted access without a restart. The seeder now loops over this same method, and
/// <c>TenantController</c> calls it for the "grant portal access" staff action.
/// </summary>
public interface ITenantPortalProvisioningService
{
    /// <summary>
    /// Ensures the tenant (scoped to <paramref name="portfolioId"/> — the IDOR guard) has an Identity
    /// login with the Tenant role and a matching <see cref="UserAccount"/>. Idempotent: a second call
    /// reports <see cref="PortalAccountStatus.AlreadyExisted"/>. The login uses the shared
    /// <see cref="SeedSettings.TenantPassword"/> (same credential the seeder issues).
    /// </summary>
    Task<PortalAccountResult> EnsurePortalAccountForTenantAsync(int tenantId, int portfolioId, CancellationToken ct = default);
}

/// <inheritdoc cref="ITenantPortalProvisioningService"/>
public class TenantPortalProvisioningService : ITenantPortalProvisioningService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RentalCommandDbContext _dbContext;
    private readonly SeedSettings _settings;
    private readonly ILogger<TenantPortalProvisioningService> _logger;

    public TenantPortalProvisioningService(
        UserManager<ApplicationUser> userManager,
        RentalCommandDbContext dbContext,
        IOptions<SeedSettings> settings,
        ILogger<TenantPortalProvisioningService> logger)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<PortalAccountResult> EnsurePortalAccountForTenantAsync(int tenantId, int portfolioId, CancellationToken ct = default)
    {
        // Scope the load to the portfolio so a tenant from another portfolio is treated as not-found
        // (the IDOR guard for the controller path; also the per-tenant reload for the seeder loop).
        var tenant = await _dbContext.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId && t.PortfolioId == portfolioId)
            .Select(t => new { t.Id, t.Email, t.FirstName, t.LastName })
            .FirstOrDefaultAsync(ct);

        if (tenant == null)
        {
            return new PortalAccountResult(PortalAccountStatus.TenantNotFound);
        }

        var email = tenant.Email?.Trim();
        var displayName = $"{tenant.FirstName} {tenant.LastName}".Trim();

        if (string.IsNullOrWhiteSpace(email))
        {
            return new PortalAccountResult(
                PortalAccountStatus.NoEmail,
                DisplayName: string.IsNullOrWhiteSpace(displayName) ? null : displayName);
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = email;
        }

        var now = DateTime.UtcNow;
        bool created;

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
                var errors = string.Join("; ", createResult.Errors.Select(e => e.Description));
                _logger.LogWarning(
                    "Failed to provision tenant portal user for tenant {TenantId} ({Email}): {Errors}",
                    tenant.Id, email, errors);
                return new PortalAccountResult(PortalAccountStatus.Failed, email, displayName, errors);
            }

            created = true;
        }
        else
        {
            // The email already belongs to an Identity user. Only link/confirm it when it is unclaimed
            // or already this tenant's — never re-point another portfolio's or another tenant's login.
            if (identityUser.PortfolioId.HasValue && identityUser.PortfolioId.Value != portfolioId)
            {
                _logger.LogWarning(
                    "Cannot grant portal access for {Email}: Identity user belongs to portfolio {ExistingPortfolioId}, not {PortfolioId}.",
                    email, identityUser.PortfolioId.Value, portfolioId);
                return new PortalAccountResult(PortalAccountStatus.Failed, email, displayName,
                    "That email is already used by an account in another portfolio.");
            }

            if (identityUser.TenantId.HasValue && identityUser.TenantId.Value != tenant.Id)
            {
                _logger.LogWarning(
                    "Cannot grant portal access for {Email}: Identity user already belongs to tenant {ExistingTenantId}, not {TenantId}.",
                    email, identityUser.TenantId.Value, tenant.Id);
                return new PortalAccountResult(PortalAccountStatus.Failed, email, displayName,
                    "That email is already used by another tenant's account.");
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
            }

            created = false;
        }

        var roles = await _userManager.GetRolesAsync(identityUser);
        if (!roles.Contains(nameof(UserRole.Tenant)))
        {
            var roleResult = await _userManager.AddToRoleAsync(identityUser, nameof(UserRole.Tenant));
            if (!roleResult.Succeeded)
            {
                _logger.LogWarning(
                    "Failed to assign Tenant role to portal user {Email}: {Errors}",
                    email, string.Join("; ", roleResult.Errors.Select(e => e.Description)));
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

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Ensured tenant portal account for tenant {TenantId} ({Email}) in portfolio {PortfolioId}: {Status}.",
            tenant.Id, email, portfolioId, created ? "created" : "already existed");

        return new PortalAccountResult(
            created ? PortalAccountStatus.Created : PortalAccountStatus.AlreadyExisted,
            email,
            displayName);
    }
}
