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

/// <summary>The portal-login state of a tenant, surfaced to staff and toggled by them.</summary>
public enum TenantPortalAccess
{
    /// <summary>No Identity login exists for this tenant.</summary>
    None,

    /// <summary>A login exists and the tenant can sign in.</summary>
    Active,

    /// <summary>A login exists but is turned off (locked out) — the tenant cannot sign in.</summary>
    Disabled,
}

/// <summary>Outcome of <see cref="ITenantPortalProvisioningService.SetPortalAccessAsync"/>.</summary>
public enum SetPortalAccessOutcome
{
    /// <summary>The access state was updated (the resulting state is on the result).</summary>
    Updated,

    /// <summary>No tenant with that id exists in the given portfolio (IDOR guard / soft-deleted).</summary>
    TenantNotFound,

    /// <summary>Enabling needs an email to provision a login, and the tenant has none.</summary>
    NoEmail,

    /// <summary>The update failed (Identity error, or the email belongs to another portfolio/tenant).</summary>
    Failed,
}

/// <summary>Result of toggling a tenant's portal access.</summary>
/// <param name="Outcome">What happened (drives the controller's status code).</param>
/// <param name="Access">The resulting portal-access state.</param>
/// <param name="Email">The tenant's sign-in email, when known.</param>
/// <param name="Error">Human-readable reason when <see cref="Outcome"/> is <see cref="SetPortalAccessOutcome.Failed"/>.</param>
public sealed record SetPortalAccessResult(
    SetPortalAccessOutcome Outcome,
    TenantPortalAccess Access,
    string? Email = null,
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

    /// <summary>
    /// Turns the tenant's portal access on or off (scoped to <paramref name="portfolioId"/> — the IDOR
    /// guard). Disabling locks the Identity login out indefinitely so the tenant can't sign in; enabling
    /// ensures a login exists (provisioning one if needed) and clears the lock. Idempotent.
    /// </summary>
    Task<SetPortalAccessResult> SetPortalAccessAsync(int tenantId, int portfolioId, bool enabled, CancellationToken ct = default);
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

    /// <summary>
    /// The lockout end we write to mark a portal login as intentionally turned off. A far-future
    /// sentinel so it is distinguishable from a transient failed-login auto-lockout (minutes).
    /// </summary>
    public static readonly DateTimeOffset PortalDisabledUntil = DateTimeOffset.MaxValue;

    /// <summary>
    /// True when a login's lockout end marks an intentional "portal access off" (the far-future
    /// sentinel), as opposed to a short failed-login auto-lockout. Threshold is well beyond any
    /// auto-lockout window, so a tenant who simply fat-fingered their password is never read as disabled.
    /// </summary>
    public static bool IsPortalDisabled(DateTimeOffset? lockoutEnd)
        => lockoutEnd is { } end && end > DateTimeOffset.UtcNow.AddYears(50);

    public async Task<SetPortalAccessResult> SetPortalAccessAsync(
        int tenantId, int portfolioId, bool enabled, CancellationToken ct = default)
    {
        if (enabled)
        {
            // Enabling ensures the login exists (provisioning one if needed), then clears any lock.
            var ensure = await EnsurePortalAccountForTenantAsync(tenantId, portfolioId, ct);
            switch (ensure.Status)
            {
                case PortalAccountStatus.TenantNotFound:
                    return new SetPortalAccessResult(SetPortalAccessOutcome.TenantNotFound, TenantPortalAccess.None);
                case PortalAccountStatus.NoEmail:
                    return new SetPortalAccessResult(SetPortalAccessOutcome.NoEmail, TenantPortalAccess.None, ensure.Email);
                case PortalAccountStatus.Failed:
                    return new SetPortalAccessResult(SetPortalAccessOutcome.Failed, TenantPortalAccess.None, ensure.Email, ensure.Error);
            }

            var user = await _dbContext.Users
                .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.PortfolioId == portfolioId, ct);
            if (user == null)
            {
                return new SetPortalAccessResult(SetPortalAccessOutcome.Failed, TenantPortalAccess.None, ensure.Email,
                    "Portal login could not be loaded after provisioning.");
            }

            // Clear the disable lock and any accumulated failed-login count so the tenant can sign in.
            await _userManager.SetLockoutEndDateAsync(user, null);
            await _userManager.ResetAccessFailedCountAsync(user);

            _logger.LogInformation(
                "Enabled portal access for tenant {TenantId} ({Email}) in portfolio {PortfolioId}.",
                tenantId, user.Email, portfolioId);
            return new SetPortalAccessResult(SetPortalAccessOutcome.Updated, TenantPortalAccess.Active, user.Email);
        }

        // Disabling locks the login out indefinitely. If there is no login, there's nothing to disable.
        var existing = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.PortfolioId == portfolioId, ct);

        if (existing == null)
        {
            // Scope the existence check to the portfolio so a cross-portfolio id still looks not-found.
            var tenantExists = await _dbContext.Tenants
                .AnyAsync(t => t.Id == tenantId && t.PortfolioId == portfolioId, ct);
            return tenantExists
                ? new SetPortalAccessResult(SetPortalAccessOutcome.Updated, TenantPortalAccess.None)
                : new SetPortalAccessResult(SetPortalAccessOutcome.TenantNotFound, TenantPortalAccess.None);
        }

        await _userManager.SetLockoutEnabledAsync(existing, true);
        await _userManager.SetLockoutEndDateAsync(existing, PortalDisabledUntil);

        _logger.LogInformation(
            "Disabled portal access for tenant {TenantId} ({Email}) in portfolio {PortfolioId}.",
            tenantId, existing.Email, portfolioId);
        return new SetPortalAccessResult(SetPortalAccessOutcome.Updated, TenantPortalAccess.Disabled, existing.Email);
    }
}
