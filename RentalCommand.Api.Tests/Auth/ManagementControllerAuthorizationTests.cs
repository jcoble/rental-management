using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.Controllers;

namespace RentalCommand.Api.Tests.Auth;

/// <summary>
/// Security invariant (CRITICAL function-level authorization): every staff-facing management controller
/// must EXCLUDE the <c>Tenant</c> role. Tenants are issued JWTs for the portal, so a bare
/// <c>[Authorize]</c> (any authenticated principal) would let a tenant reach payments, other tenants'
/// PII, property/lease CRUD, owner financials, and the sandbox go-live wipe. The fix routes those
/// controllers through <see cref="ManagementControllerBase"/> (<c>[Authorize(Roles = "Admin,Manager,Agent,Owner")]</c>);
/// these tests fail if a future controller is added to that base without the gate, or if the gate is
/// loosened to admit <c>Tenant</c>.
///
/// The four deliberately tenant-reachable controllers (PortalController, NotificationsController,
/// DocumentsController, DevicesController) derive from the plain <see cref="AuthenticatedPortfolioControllerBase"/>
/// and are intentionally NOT covered here — they scope to the caller via <c>GetUserId()</c>/<c>GetTenantIdOrNull()</c>.
/// </summary>
public class ManagementControllerAuthorizationTests
{
    private const string TenantRole = "Tenant";

    /// <summary>Every concrete controller that derives from <see cref="ManagementControllerBase"/>.</summary>
    public static IEnumerable<object[]> ManagementControllers() =>
        typeof(ManagementControllerBase).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true }
                        && typeof(ManagementControllerBase).IsAssignableFrom(t))
            .Select(t => new object[] { t });

    [Fact]
    public void ManagementBase_StaffRoles_ExcludesTenant_AndIsTheFullStaffSet()
    {
        // The single source of truth for the gate must never silently include Tenant, and must carry the
        // full staff set (Agent is a real staff role — omitting it would lock out legitimate staff).
        var roles = ManagementControllerBase.StaffRoles
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        roles.Should().NotContain(TenantRole);
        roles.Should().BeEquivalentTo(new[] { "Admin", "Manager", "Agent", "Owner" });
    }

    [Fact]
    public void ManagementBase_CarriesRoleGate_ThatExcludesTenant()
    {
        var authorize = typeof(ManagementControllerBase)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault(a => !string.IsNullOrWhiteSpace(a.Roles));

        authorize.Should().NotBeNull("the management base must gate by role");
        RolesOf(authorize!).Should().NotContain(TenantRole);
    }

    [Fact]
    public void ThereIsAtLeastOneManagementController()
    {
        // Guards against the enumeration silently going empty (e.g. a refactor that renames the base),
        // which would make every [Theory] below vacuously pass.
        ManagementControllers().Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ManagementControllers))]
    public void EveryManagementController_RequiresAStaffRole_AndNeverAdmitsTenant(Type controller)
    {
        // All [Authorize] attributes in effect for this controller (its own + inherited base), as ASP.NET
        // Core combines them with AND. The effective access requires being in EVERY non-empty Roles list.
        var roleRestrictions = controller
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Where(a => !string.IsNullOrWhiteSpace(a.Roles))
            .Select(RolesOf)
            .ToList();

        roleRestrictions.Should().NotBeEmpty(
            $"{controller.Name} is a management controller and must carry a role restriction (none found — a bare [Authorize] would admit a Tenant)");

        // AND-combined: a principal must satisfy each restriction. If ANY restriction omits Tenant, a
        // tenant can never pass. Assert that here so the gate provably excludes tenants.
        roleRestrictions.Should().Contain(
            roles => !roles.Contains(TenantRole),
            $"{controller.Name} must have at least one role restriction that excludes '{TenantRole}'");

        // Stronger: NO restriction on a management controller should list Tenant at all — listing it would
        // signal an intent to admit tenants onto a management surface, which is exactly what we forbid.
        roleRestrictions.Should().OnlyContain(
            roles => !roles.Contains(TenantRole),
            $"no role restriction on management controller {controller.Name} may include '{TenantRole}'");
    }

    private static string[] RolesOf(AuthorizeAttribute attr) =>
        (attr.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
