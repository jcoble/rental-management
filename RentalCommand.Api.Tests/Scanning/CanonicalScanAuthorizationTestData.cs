using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Scanning;

internal sealed record CanonicalScanTestAuthorization(
    WorkspaceReadScope Scope,
    ActiveAccessContext HttpAccessContext);

/// <summary>
/// Seeds the same durable authorization graph that scan review re-reads in production. Tests must
/// never fabricate a read scope without its current session, access revision, effective membership,
/// role assignment, and property-target capabilities existing in the database.
/// </summary>
internal static class CanonicalScanAuthorizationTestData
{
    public static CanonicalScanTestAuthorization SeedWorkspaceAdministrator(
        RentalCommandDbContext db,
        int portfolioId,
        int userId,
        Guid sessionId,
        int accessContextId = 1)
    {
        var now = DateTime.UtcNow;
        var portfolio = db.Portfolios.IgnoreQueryFilters()
            .SingleOrDefault(candidate => candidate.Id == portfolioId);
        if (portfolio is null)
        {
            portfolio = new Portfolio
            {
                Id = portfolioId,
                Name = $"Portfolio {portfolioId}",
                ManagementCompanyName = "Test Co",
                TimeZone = "UTC",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Portfolios.Add(portfolio);
        }

        var user = db.Users.SingleOrDefault(candidate => candidate.Id == userId);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = userId,
                UserName = $"scan-user-{userId}@example.test",
                NormalizedUserName = $"SCAN-USER-{userId}@EXAMPLE.TEST",
                Email = $"scan-user-{userId}@example.test",
                NormalizedEmail = $"SCAN-USER-{userId}@EXAMPLE.TEST",
                DisplayName = $"Scan User {userId}",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = now,
            };
            db.Users.Add(user);
        }

        db.SaveChanges();

        var accessContext = new WorkspaceAccessContext
        {
            Id = accessContextId,
            UserId = userId,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
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
            Id = sessionId,
            UserId = userId,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };

        db.AddRange(assignment, session);
        db.SaveChanges();

        var scope = new WorkspaceReadScope(
            portfolioId,
            userId,
            sessionId,
            accessContext.Id,
            accessContext.AccessRevision);
        var httpAccessContext = new ActiveAccessContext(
            sessionId,
            userId,
            accessContext.Id,
            portfolioId,
            accessContext.AccessRevision,
            accessContext.LastAuthorizedExperience,
            membership.Id,
            membership.DefaultExperience);
        return new CanonicalScanTestAuthorization(scope, httpAccessContext);
    }
}
