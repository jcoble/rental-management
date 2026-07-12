using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

public sealed class ManagementControllerAuthorizationTests
{
    public static IEnumerable<object[]> ManagementControllers() =>
        typeof(ManagementControllerBase).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true } &&
                           typeof(ManagementControllerBase).IsAssignableFrom(type))
            .Select(type => new object[] { type });

    [Fact]
    public void ManagementBase_UsesCanonicalPolicy_AndNoLegacyRoleGate()
    {
        var attributes = typeof(ManagementControllerBase)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ToList();

        attributes.Should().ContainSingle(attribute =>
            attribute.Policy == CanonicalManagementPolicy.Name);
        attributes.Should().OnlyContain(attribute => string.IsNullOrWhiteSpace(attribute.Roles));
    }

    [Fact]
    public void ThereIsAtLeastOneManagementController() =>
        ManagementControllers().Should().NotBeEmpty();

    [Theory]
    [MemberData(nameof(ManagementControllers))]
    public void EveryManagementController_InheritsCanonicalPolicy_AndNoRoleAttribute(Type controller)
    {
        var attributes = controller
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ToList();

        attributes.Should().Contain(attribute =>
            attribute.Policy == CanonicalManagementPolicy.Name);
        attributes.Should().OnlyContain(attribute => string.IsNullOrWhiteSpace(attribute.Roles),
            "canonical tokens deliberately contain no Identity role claims");
    }

    [Fact]
    public async Task CanonicalAdministratorAssignment_AdmitsManagementWithoutRoleClaims()
    {
        using var sqlite = new SqliteTestContext();
        var db = sqlite.Db;
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = "admin@example.test",
            NormalizedUserName = "ADMIN@EXAMPLE.TEST",
            Email = "admin@example.test",
            NormalizedEmail = "ADMIN@EXAMPLE.TEST",
            DisplayName = "Administrator",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var portfolio = await db.Portfolios.SingleAsync(portfolio => portfolio.Id == 1);
        db.Add(user);
        await db.SaveChangesAsync();
        var context = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolio.Id,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Add(assignment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var active = new ActiveAccessContext(
            Guid.NewGuid(), user.Id, context.Id, portfolio.Id, 1,
            WorkspaceExperience.Management, membership.Id, WorkspaceExperience.Management);
        var http = new DefaultHttpContext();
        http.Items[CanonicalAccessContextHttpItem.Key] = active;
        var auth = new AuthorizationHandlerContext(
            [new CanonicalManagementRequirement()],
            new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("sub", user.Id.ToString())], "Bearer")),
            http);

        await new CanonicalManagementAuthorizationHandler(db, TimeProvider.System).HandleAsync(auth);

        auth.HasSucceeded.Should().BeTrue();
        auth.User.IsInRole("Admin").Should().BeFalse();
    }
}
