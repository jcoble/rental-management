using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

internal static class CanonicalScopeTestFixture
{
    internal static WorkspaceReadScope SeedAdministratorScope(
        this RentalCommandDbContext db,
        int portfolioId,
        string fixtureName)
    {
        db.Database.InstallCanonicalLeaseProjectionViewsForSqlite();

        var now = DateTime.UtcNow;
        var normalizedFixtureName = fixtureName.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant();
        var email = $"{normalizedFixtureName}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = $"{fixtureName} Administrator",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };

        db.AddRange(assignment, session);
        db.SaveChanges();

        return new WorkspaceReadScope(
            portfolioId,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    internal static WorkspaceReadScope SeedPropertyManagerScope(
        this RentalCommandDbContext db,
        int portfolioId,
        int propertyId,
        string fixtureName)
        => db.SeedSelectedPropertyScope(
            portfolioId, propertyId, fixtureName, RoleProfileKeys.PropertyManager, "Property Manager");

    internal static WorkspaceReadScope SeedLeasingAgentScope(
        this RentalCommandDbContext db,
        int portfolioId,
        int propertyId,
        string fixtureName)
        => db.SeedSelectedPropertyScope(
            portfolioId, propertyId, fixtureName, RoleProfileKeys.LeasingAgent, "Leasing Agent");

    private static WorkspaceReadScope SeedSelectedPropertyScope(
        this RentalCommandDbContext db,
        int portfolioId,
        int propertyId,
        string fixtureName,
        string roleProfileKey,
        string actorLabel)
    {
        db.Database.InstallCanonicalLeaseProjectionViewsForSqlite();

        var now = DateTime.UtcNow;
        var normalizedFixtureName = fixtureName.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant();
        var email = $"{normalizedFixtureName}-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = $"{fixtureName} {actorLabel}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == roleProfileKey).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            SelectedProperties =
            [
                new MembershipRoleAssignmentProperty
                {
                    PortfolioId = portfolioId,
                    PropertyId = propertyId,
                },
            ],
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };

        db.AddRange(assignment, session);
        db.SaveChanges();

        return new WorkspaceReadScope(
            portfolioId,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }
}
