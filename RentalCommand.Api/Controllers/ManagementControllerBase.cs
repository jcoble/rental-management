using Microsoft.AspNetCore.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Base for the STAFF-facing portfolio management controllers. Adds a function-level role gate on top
/// of <see cref="AuthenticatedPortfolioControllerBase"/>: every endpoint requires one of the portfolio's
/// staff roles — <c>Admin</c>, <c>Manager</c>, <c>Agent</c>, or <c>Owner</c> (the same staff set used by
/// <c>AutomationNotifier</c> and <c>NotificationService.IsStaffUserAsync</c>; everything in
/// <see cref="RentalCommand.Core.Enums.UserRole"/> except <see cref="RentalCommand.Core.Enums.UserRole.Tenant"/>).
///
/// <para>
/// The <c>Tenant</c> role is deliberately EXCLUDED. Tenants are issued JWTs so they can use the portal,
/// but they must never reach management surfaces (all payments, other tenants' PII, property/lease CRUD,
/// owner financials, or the sandbox go-live wipe). The handful of portfolio-scoped endpoints a tenant
/// legitimately calls — the portal, their own per-user notifications, their own document upload, and
/// device/push registration — live on controllers that derive from the plain
/// <see cref="AuthenticatedPortfolioControllerBase"/> instead (PortalController, NotificationsController,
/// DocumentsController, DevicesController), each scoping to the caller via <c>GetUserId()</c>/
/// <c>GetTenantIdOrNull()</c>.
/// </para>
/// <para>
/// Multiple <c>[Authorize]</c> attributes combine with AND (ASP.NET Core authorization), so a derived
/// management controller may ADD a tighter role list — e.g. <c>BankingController</c> →
/// <c>Admin,Manager</c>, <c>AdminUsersController</c> → <c>Admin</c> — and the effective requirement is the
/// intersection. A derived controller can never RELAX this base requirement back to admit a tenant, which
/// is why the four tenant-reachable controllers are kept OFF this base rather than overriding it.
/// </para>
/// <para>
/// <see cref="RentalCommand.Api.Tests.Auth.ManagementControllerAuthorizationTests"/> asserts that every
/// concrete controller deriving from this base carries a role restriction that excludes <c>Tenant</c>, so
/// the gate can never silently regress.
/// </para>
/// </summary>
[Authorize(Roles = StaffRoles)]
public abstract class ManagementControllerBase : AuthenticatedPortfolioControllerBase
{
    /// <summary>
    /// The portfolio's staff roles, comma-joined for <c>[Authorize(Roles = ...)]</c>. Mirrors the
    /// <see cref="RentalCommand.Core.Enums.UserRole"/> staff members (every value except
    /// <see cref="RentalCommand.Core.Enums.UserRole.Tenant"/>) and the staff-role lists used by the
    /// notification/SMS services, kept as a single source of truth so the gate and its meta-test agree.
    /// </summary>
    public const string StaffRoles = "Admin,Manager,Agent,Owner";
}
