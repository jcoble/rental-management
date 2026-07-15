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
    public void AssignedWorkUpdate_IsNotBlockedByTheManagementShellPolicy()
    {
        typeof(TechnicianController).Should()
            .BeDerivedFrom<AuthenticatedPortfolioControllerBase>();
        typeof(TechnicianController).Should()
            .NotBeDerivedFrom<ManagementControllerBase>(
                "assigned-work-only technicians are intentionally denied the general Management shell");
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
    public void HttpCapabilityPolicies_AreOnlyUsedForWorkspaceTargetCapabilities()
    {
        var capabilityKinds = AccessCatalog.Capabilities.ToDictionary(
            capability => capability.Key,
            capability => capability.AuthorizationTargetKind);
        var controllerTypes = typeof(ManagementControllerBase).Assembly.GetTypes()
            .Where(type => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(type));

        var declaredPolicies = controllerTypes.SelectMany(controller =>
                controller.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
                    .Select(attribute => (Member: (MemberInfo)controller, attribute.Policy))
                .Concat(controller.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .SelectMany(method => method.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
                        .Select(attribute => (Member: (MemberInfo)method, attribute.Policy)))))
            .Where(item => item.Policy?.StartsWith(CapabilityPolicy.Prefix, StringComparison.Ordinal) == true)
            .ToList();

        declaredPolicies.Should().NotBeEmpty();
        foreach (var (member, policy) in declaredPolicies)
        {
            var capabilityKey = policy![CapabilityPolicy.Prefix.Length..];
            capabilityKinds.Should().ContainKey(capabilityKey);
            capabilityKinds[capabilityKey].Should().Be(
                CapabilityAuthorizationTargetKind.Workspace,
                $"{member.DeclaringType?.Name}.{member.Name} is authorized before a property or work-order target can be loaded");
        }
    }

    [Theory]
    [InlineData(nameof(SandboxController.GoLive))]
    [InlineData(nameof(SandboxController.OnboardingChoice))]
    public void SandboxLifecycleMutations_RequireWorkspaceDestructiveAuthority(string methodName)
    {
        var method = typeof(SandboxController).GetMethod(methodName)!;

        method.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Should().ContainSingle(attribute =>
                attribute.Policy == CapabilityPolicy.For(CapabilityKeys.AccountDestructiveActions));
    }

    [Fact]
    public async Task CanonicalAdministratorAssignment_AdmitsManagementWithoutRoleClaims()
    {
        using var sqlite = new SqliteTestContext();
        var db = sqlite.Db;
        var (active, user) = await SeedAccessAsync(
            db,
            "admin@example.test",
            RoleProfileKeys.WorkspaceAdministrator,
            WorkspaceExperience.Management,
            MembershipRoleAssignmentScopeKind.AllProperties);
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

    [Fact]
    public async Task AssignedWorkOnlyTechnician_IsDeniedTheManagementShell()
    {
        using var sqlite = new SqliteTestContext();
        var db = sqlite.Db;
        var (active, user) = await SeedAccessAsync(
            db,
            "technician@example.test",
            RoleProfileKeys.MaintenanceTechnician,
            WorkspaceExperience.Maintenance,
            MembershipRoleAssignmentScopeKind.AssignedWorkOrders);
        var http = new DefaultHttpContext();
        http.Items[CanonicalAccessContextHttpItem.Key] = active;
        var auth = new AuthorizationHandlerContext(
            [new CanonicalManagementRequirement()],
            new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("sub", user.Id.ToString())], "Bearer")),
            http);

        await new CanonicalManagementAuthorizationHandler(db, TimeProvider.System).HandleAsync(auth);

        auth.HasSucceeded.Should().BeFalse(
            "a technician must use assignment-scoped endpoints and never retrieve Management Unit or money projections");
    }

    private static async Task<(ActiveAccessContext Active, ApplicationUser User)> SeedAccessAsync(
        RentalCommandDbContext db,
        string email,
        string roleProfileKey,
        WorkspaceExperience experience,
        MembershipRoleAssignmentScopeKind scopeKind)
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = roleProfileKey,
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
            DefaultExperience = experience,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Add(new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolio.Id,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == roleProfileKey).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = scopeKind,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return (new ActiveAccessContext(
            Guid.NewGuid(), user.Id, context.Id, portfolio.Id, 1,
            experience, membership.Id, experience), user);
    }
}
