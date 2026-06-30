using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Sends transactional auth emails (email confirmation, password reset) via the DB outbox.
/// Sending is best-effort — failures are logged and swallowed so they never block registration.
/// </summary>
public interface IAuthEmailSender
{
    Task SendEmailConfirmationAsync(ApplicationUser user, string token, CancellationToken ct = default);
    Task SendPasswordResetAsync(ApplicationUser user, string token, CancellationToken ct = default);

    /// <summary>
    /// Emails a freshly added team member their sign-in details: their login email, the
    /// temporary password an admin set for them, the role they were added as, and a login link.
    /// </summary>
    Task SendTeamInviteAsync(ApplicationUser user, string temporaryPassword, string roleName, CancellationToken ct = default);

    /// <summary>
    /// Emails a tenant their resident-portal invite: a friendly greeting, their sign-in email, the
    /// shared temporary password, and a login link. Sent on demand by staff (not when the tenant is
    /// created). Best-effort — failures are logged and swallowed.
    /// </summary>
    Task SendTenantPortalInviteAsync(ApplicationUser user, string temporaryPassword, CancellationToken ct = default);
}
