namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Manages push-notification device tokens. Registration is an upsert by token value so a
/// re-installing Flutter app never creates duplicate rows. Unregistration is scoped to the
/// calling user so a user cannot remove another user's token.
/// </summary>
public interface IDeviceService
{
    /// <summary>
    /// Upserts the device token. If a row with <paramref name="token"/> already exists its
    /// <c>UserId</c>, <c>PortfolioId</c>, <c>Platform</c>, and <c>LastSeenAt</c> are updated;
    /// otherwise a new row is inserted with <c>CreatedAt</c> = <c>LastSeenAt</c> = UtcNow.
    /// </summary>
    Task RegisterAsync(int portfolioId, int userId, string token, string platform, CancellationToken ct = default);

    /// <summary>
    /// Deletes the row whose <c>Token</c> matches <paramref name="token"/> and whose
    /// <c>UserId</c> matches <paramref name="userId"/>. Returns <c>true</c> if a row was removed.
    /// </summary>
    Task<bool> UnregisterAsync(int userId, string token, CancellationToken ct = default);
}
