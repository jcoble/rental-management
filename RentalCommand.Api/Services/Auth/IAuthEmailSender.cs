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
}
