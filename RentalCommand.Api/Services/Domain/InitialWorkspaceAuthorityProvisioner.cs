using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Creates the complete authority graph for the person who creates a new workspace. This is used
/// by every fresh-workspace entry point so a sole landlord is both the primary legal owner and the
/// workspace administrator without relying on an Identity role or a legacy account row.
/// </summary>
public interface IInitialWorkspaceAuthorityProvisioner
{
    Task<WorkspaceAccessContext> ProvisionAsync(
        ApplicationUser user,
        Portfolio portfolio,
        CancellationToken ct = default);
}

/// <inheritdoc cref="IInitialWorkspaceAuthorityProvisioner"/>
public sealed class InitialWorkspaceAuthorityProvisioner : IInitialWorkspaceAuthorityProvisioner
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public InitialWorkspaceAuthorityProvisioner(
        RentalCommandDbContext db,
        TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<WorkspaceAccessContext> ProvisionAsync(
        ApplicationUser user,
        Portfolio portfolio,
        CancellationToken ct = default)
    {
        if (user.Id <= 0)
        {
            throw new InvalidOperationException("The workspace creator must be persisted first.");
        }

        if (portfolio.Id <= 0)
        {
            throw new InvalidOperationException("The workspace must be persisted first.");
        }

        var now = _timeProvider.UtcNow();
        var owner = new OwnerEntity
        {
            PortfolioId = portfolio.Id,
            OwnerEntityType = OwnerEntityType.Person,
            Name = ResolveOwnerName(user),
            Email = string.IsNullOrWhiteSpace(user.Email) ? null : user.Email,
            IsPrimary = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var administratorAssignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolio.Id,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var ownerAccess = new OwnerUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            AccessContext = context,
            ApplicationUser = user,
            OwnerEntity = owner,
            EffectiveFromUtc = now,
            GrantedAtUtc = now,
            GrantedByUser = user,
            Reason = "Initial workspace owner relationship",
        };

        _db.AddRange(owner, administratorAssignment, ownerAccess);
        await _db.SaveChangesAsync(ct);
        return context;
    }

    private static string ResolveOwnerName(ApplicationUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            return user.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            var at = user.Email.IndexOf('@');
            var local = at > 0 ? user.Email[..at] : user.Email;
            if (!string.IsNullOrWhiteSpace(local))
            {
                return local.Trim();
            }
        }

        return "Me (primary owner)";
    }
}
