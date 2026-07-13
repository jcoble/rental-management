using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Creates the FIRST owner for a portfolio from the landlord's own account — the "self-owner".
///
/// The role model is: platform operator (super-admin) → the landlord who signs up. The landlord IS the
/// first <see cref="OwnerEntity"/>, so onboarding should never make them perform a separate "add an
/// owner" step before they can add a property. This provisioner auto-creates that owner (flagged
/// <see cref="OwnerEntity.IsPrimary"/>) and links it back to the user via
/// an explicit <see cref="OwnerUserAccess"/> beneath the user's workspace access context.
///
/// Every method is idempotent and self-contained so it can run at the three moments a portfolio first
/// has (or regains) a real, empty owner list — registration, Google sign-in, and Go-Live (which wipes
/// the demo owners) — without ever creating a duplicate.
/// </summary>
public interface ISelfOwnerProvisioner
{
    /// <summary>
    /// Ensures the portfolio has a primary self-owner derived from <paramref name="user"/>, creating it
    /// if absent and granting an explicit owner relationship when needed. No-op when both already exist.
    /// The owner and relationship are persisted via the
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
    private readonly TimeProvider _timeProvider;

    public SelfOwnerProvisioner(
        RentalCommandDbContext db,
        ILogger<SelfOwnerProvisioner> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<OwnerEntity?> EnsureSelfOwnerAsync(ApplicationUser user, int portfolioId, CancellationToken ct = default)
    {
        var existingPrimary = await _db.OwnerEntities
            .FirstOrDefaultAsync(o => o.PortfolioId == portfolioId && o.IsPrimary, ct);
        var now = _timeProvider.UtcNow();
        var owner = existingPrimary ?? new OwnerEntity
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
        if (existingPrimary is null)
        {
            _db.OwnerEntities.Add(owner);
        }

        var context = await _db.WorkspaceAccessContexts
            .SingleOrDefaultAsync(candidate =>
                candidate.UserId == user.Id && candidate.PortfolioId == portfolioId, ct);
        var contextWasCreated = context is null;
        context ??= new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Owner,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        if (contextWasCreated)
        {
            _db.WorkspaceAccessContexts.Add(context);
        }

        var alreadyGranted = existingPrimary is not null && await _db.OwnerUserAccesses.AnyAsync(access =>
            access.AccessContextId == context.Id && access.OwnerEntityId == owner.Id &&
            access.PortfolioId == portfolioId && access.RevokedAtUtc == null &&
            access.EffectiveFromUtc <= now &&
            (access.EffectiveToUtc == null || access.EffectiveToUtc > now), ct);
        if (!alreadyGranted)
        {
            _db.OwnerUserAccesses.Add(new OwnerUserAccess
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolioId,
                AccessContext = context,
                ApplicationUser = user,
                OwnerEntity = owner,
                EffectiveFromUtc = now,
                GrantedAtUtc = now,
                GrantedByUser = user,
                Reason = "Primary self-owner relationship",
            });
            if (!contextWasCreated)
            {
                context.AdvanceRevision(context.AccessRevision);
                context.UpdatedAtUtc = now;
            }
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Ensured primary self-owner {OwnerId} ({OwnerName}) for portfolio {PortfolioId} from user {Email} (id {UserId}).",
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

}
