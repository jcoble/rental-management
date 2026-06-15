using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Creates the FIRST owner for a portfolio from the landlord's own account — the "self-owner".
///
/// The role model is: platform operator (super-admin) → the landlord who signs up. The landlord IS the
/// first <see cref="OwnerEntity"/>, so onboarding should never make them perform a separate "add an
/// owner" step before they can add a property. This provisioner auto-creates that owner (flagged
/// <see cref="OwnerEntity.IsPrimary"/>) and links it back to the user via
/// <see cref="ApplicationUser.OwnerEntityId"/>.
///
/// Every method is idempotent and self-contained so it can run at the three moments a portfolio first
/// has (or regains) a real, empty owner list — registration, Google sign-in, and Go-Live (which wipes
/// the demo owners) — as well as a one-off backfill, without ever creating a duplicate.
/// </summary>
public interface ISelfOwnerProvisioner
{
    /// <summary>
    /// Ensures the portfolio has a primary self-owner derived from <paramref name="user"/>, creating it
    /// if absent and pointing <see cref="ApplicationUser.OwnerEntityId"/> at it. No-op (beyond ensuring
    /// the link) when a primary owner already exists. The owner and the user link are persisted via the
    /// shared <see cref="RentalCommandDbContext"/> — when called inside a caller's transaction the writes
    /// participate in it. Returns the resolved primary owner.
    /// </summary>
    Task<OwnerEntity?> EnsureSelfOwnerAsync(ApplicationUser user, int portfolioId, CancellationToken ct = default);
}

/// <inheritdoc cref="ISelfOwnerProvisioner"/>
public sealed class SelfOwnerProvisioner : ISelfOwnerProvisioner
{
    private readonly RentalCommandDbContext _db;
    private readonly ILogger<SelfOwnerProvisioner> _logger;

    public SelfOwnerProvisioner(
        RentalCommandDbContext db,
        ILogger<SelfOwnerProvisioner> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<OwnerEntity?> EnsureSelfOwnerAsync(ApplicationUser user, int portfolioId, CancellationToken ct = default)
    {
        // Already have a primary owner? Just make sure the user is linked to it, then we're done.
        var existingPrimary = await _db.OwnerEntities
            .FirstOrDefaultAsync(o => o.PortfolioId == portfolioId && o.IsPrimary, ct);
        if (existingPrimary != null)
        {
            await EnsureUserLinkAsync(user, existingPrimary.Id, ct);
            return existingPrimary;
        }

        var now = DateTime.UtcNow;
        var owner = new OwnerEntity
        {
            PortfolioId = portfolioId,
            // The landlord defaults to a Person; they can edit this to an LLC/Trust at any time.
            OwnerEntityType = OwnerEntityType.Person,
            Name = ResolveOwnerName(user),
            Email = string.IsNullOrWhiteSpace(user.Email) ? null : user.Email,
            IsPrimary = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.OwnerEntities.Add(owner);
        await _db.SaveChangesAsync(ct);

        await EnsureUserLinkAsync(user, owner.Id, ct);

        _logger.LogInformation(
            "Auto-created primary self-owner {OwnerId} ({OwnerName}) for portfolio {PortfolioId} from user {Email} (id {UserId}).",
            owner.Id, owner.Name, portfolioId, user.Email, user.Id);

        return owner;
    }

    /// <summary>
    /// Owner name from the account: the display name if set, otherwise the local part of the email
    /// ("jane" from "jane@example.com"), otherwise a safe placeholder the user can rename.
    /// </summary>
    private static string ResolveOwnerName(ApplicationUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            return user.DisplayName.Trim();
        }

        var email = user.Email;
        if (!string.IsNullOrWhiteSpace(email))
        {
            var at = email.IndexOf('@');
            var local = at > 0 ? email[..at] : email;
            if (!string.IsNullOrWhiteSpace(local))
            {
                return local.Trim();
            }
        }

        return "Me (primary owner)";
    }

    /// <summary>
    /// Point <see cref="ApplicationUser.OwnerEntityId"/> at the primary owner when not already set,
    /// writing through the shared DbContext (Identity's store uses the same context, so the passed user
    /// is tracked; if a detached instance is ever passed we attach and mark just this column modified).
    /// </summary>
    private async Task EnsureUserLinkAsync(ApplicationUser user, int ownerEntityId, CancellationToken ct)
    {
        if (user.OwnerEntityId == ownerEntityId)
        {
            return;
        }

        user.OwnerEntityId = ownerEntityId;

        var entry = _db.Entry(user);
        if (entry.State == EntityState.Detached)
        {
            _db.Attach(user);
        }
        entry.Property(u => u.OwnerEntityId).IsModified = true;

        await _db.SaveChangesAsync(ct);
    }
}
