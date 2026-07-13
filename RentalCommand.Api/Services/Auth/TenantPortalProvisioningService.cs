using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
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

    /// <summary>A login exists but its tenant relationship grants are revoked.</summary>
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
/// Provisions a login identity and explicit context-scoped tenant relationships. Tenant access is not
/// an Identity role, a UserAccount row, or a direct user-to-tenant foreign key.
/// </summary>
public interface ITenantPortalProvisioningService
{
    /// <summary>
    /// Ensures the tenant (scoped to <paramref name="portfolioId"/> — the IDOR guard) has an Identity
    /// login and relationship grant. Idempotent: a second call reports
    /// <see cref="PortalAccountStatus.AlreadyExisted"/>. The login uses the shared
    /// <see cref="SeedSettings.TenantPassword"/> (same credential the seeder issues).
    /// </summary>
    Task<PortalAccountResult> EnsurePortalAccountForTenantAsync(int tenantId, int portfolioId, CancellationToken ct = default);

    /// <summary>
    /// Turns the tenant's portal access on or off (scoped to <paramref name="portfolioId"/> — the IDOR
    /// guard). Disabling revokes this tenant's relationship grants without disabling the identity's
    /// unrelated workspace access. Enabling ensures a login and current relationship grants exist.
    /// </summary>
    Task<SetPortalAccessResult> SetPortalAccessAsync(int tenantId, int portfolioId, bool enabled, CancellationToken ct = default);
}

/// <inheritdoc cref="ITenantPortalProvisioningService"/>
public class TenantPortalProvisioningService : ITenantPortalProvisioningService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RentalCommandDbContext _dbContext;
    private readonly SeedSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantPortalProvisioningService> _logger;

    public TenantPortalProvisioningService(
        UserManager<ApplicationUser> userManager,
        RentalCommandDbContext dbContext,
        IOptions<SeedSettings> settings,
        TimeProvider timeProvider,
        ILogger<TenantPortalProvisioningService> logger)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _settings = settings.Value;
        _timeProvider = timeProvider;
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

        var now = _timeProvider.UtcNow();
        bool created;
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);

        var identityUser = await _userManager.FindByEmailAsync(email);
        if (identityUser == null)
        {
            identityUser = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = displayName,
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
            var changed = false;
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

        var context = await _dbContext.WorkspaceAccessContexts.SingleOrDefaultAsync(candidate =>
            candidate.UserId == identityUser.Id && candidate.PortfolioId == portfolioId, ct);
        var contextWasCreated = context is null;
        context ??= new WorkspaceAccessContext
        {
            User = identityUser,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        if (contextWasCreated)
        {
            _dbContext.WorkspaceAccessContexts.Add(context);
        }

        var businessDate = DateOnly.FromDateTime(now);
        var missingPartyIds = await _dbContext.LeaseManagementParties
            .Where(party => party.PortfolioId == portfolioId && party.TenantId == tenant.Id &&
                party.EffectiveFrom <= businessDate &&
                (party.EffectiveThrough == null || party.EffectiveThrough >= businessDate) &&
                !_dbContext.TenantUserAccesses.Any(access =>
                    access.ApplicationUserId == identityUser.Id &&
                    access.PortfolioId == portfolioId && access.RevokedAtUtc == null &&
                    access.LeaseManagementPartyId == party.Id))
            .Select(party => party.Id)
            .ToListAsync(ct);
        foreach (var partyId in missingPartyIds)
        {
            _dbContext.TenantUserAccesses.Add(new TenantUserAccess
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolioId,
                AccessContext = context,
                ApplicationUserId = identityUser.Id,
                LeaseManagementPartyId = partyId,
                GrantedAtUtc = now,
                GrantedByUserId = identityUser.Id,
                Reason = "Tenant portal provisioning",
            });
        }
        if (!contextWasCreated && missingPartyIds.Count > 0)
        {
            context.AdvanceRevision(context.AccessRevision);
            context.UpdatedAtUtc = now;
        }

        await _dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "Ensured tenant portal account for tenant {TenantId} ({Email}) in portfolio {PortfolioId}: {Status}.",
            tenant.Id, email, portfolioId, created ? "created" : "already existed");

        return new PortalAccountResult(
            created ? PortalAccountStatus.Created : PortalAccountStatus.AlreadyExisted,
            email,
            displayName);
    }

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

            var email = await _dbContext.TenantUserAccesses
                .Where(access => access.PortfolioId == portfolioId && access.RevokedAtUtc == null &&
                    access.LeaseManagementParty!.TenantId == tenantId)
                .OrderBy(access => access.Id)
                .Select(access => access.ApplicationUser!.Email)
                .FirstOrDefaultAsync(ct);
            if (email == null)
            {
                return new SetPortalAccessResult(SetPortalAccessOutcome.Failed, TenantPortalAccess.None, ensure.Email,
                    "Portal login could not be loaded after provisioning.");
            }

            _logger.LogInformation(
                "Enabled portal access for tenant {TenantId} ({Email}) in portfolio {PortfolioId}.",
                tenantId, email, portfolioId);
            return new SetPortalAccessResult(SetPortalAccessOutcome.Updated, TenantPortalAccess.Active, email);
        }

        var now = _timeProvider.UtcNow();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        var targetIdentity = await _dbContext.TenantUserAccesses
            .Where(access => access.PortfolioId == portfolioId && access.RevokedAtUtc == null &&
                access.LeaseManagementParty!.TenantId == tenantId)
            .OrderBy(access => access.Id)
            .Select(access => new { access.AccessContextId, Email = access.ApplicationUser!.Email })
            .FirstOrDefaultAsync(ct);

        if (targetIdentity == null)
        {
            var tenantExists = await _dbContext.Tenants
                .AnyAsync(t => t.Id == tenantId && t.PortfolioId == portfolioId, ct);
            return tenantExists
                ? new SetPortalAccessResult(SetPortalAccessOutcome.Updated, TenantPortalAccess.None)
                : new SetPortalAccessResult(SetPortalAccessOutcome.TenantNotFound, TenantPortalAccess.None);
        }

        var context = await _dbContext.WorkspaceAccessContexts
            .SingleAsync(candidate => candidate.Id == targetIdentity.AccessContextId, ct);
        var accesses = await _dbContext.TenantUserAccesses
            .Where(access => access.AccessContextId == context.Id && access.RevokedAtUtc == null &&
                access.LeaseManagementParty!.TenantId == tenantId)
            .ToListAsync(ct);
        foreach (var access in accesses)
        {
            access.RevokedAtUtc = now;
            access.RevokedByUserId = context.UserId;
            access.Reason = "Tenant portal access disabled";
        }
        context.AdvanceRevision(context.AccessRevision);
        context.UpdatedAtUtc = now;
        await _dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "Disabled portal access for tenant {TenantId} ({Email}) in portfolio {PortfolioId}.",
            tenantId, targetIdentity.Email, portfolioId);
        return new SetPortalAccessResult(SetPortalAccessOutcome.Updated, TenantPortalAccess.Disabled, targetIdentity.Email);
    }
}
