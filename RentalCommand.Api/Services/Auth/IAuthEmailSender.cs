using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Sends transactional auth emails (email confirmation, password reset) via the DB outbox.
/// Enqueueing is receipt-backed and fails with the owning auth operation instead of silently
/// losing a confirmation or reset message.
/// </summary>
public interface IAuthEmailSender
{
    Task SendEmailConfirmationAsync(
        ApplicationUser user,
        int? expectedPortfolioId,
        string token,
        string operationKey,
        CancellationToken ct = default);
    Task SendPasswordResetAsync(
        ApplicationUser user,
        int? expectedPortfolioId,
        string token,
        string operationKey,
        CancellationToken ct = default);
}
