using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Manages push-notification device tokens. Registration is an upsert by selected workspace
/// and token value so a re-installing Flutter app never creates duplicate rows inside one
/// workspace. Unregistration is scoped to the calling user so a user cannot remove another
/// user's token.
/// </summary>
public interface IDeviceService
{
    /// <summary>
    /// Upserts the device token for the current workspace. If a row with <paramref name="token"/>
    /// already exists in that workspace, its <c>UserId</c>, <c>Platform</c>, and <c>LastSeenAt</c> are updated;
    /// otherwise a new row is inserted with <c>CreatedAt</c> = <c>LastSeenAt</c> = UtcNow.
    /// </summary>
    Task RegisterAsync(WorkspaceReadScope scope, string token, string platform,
        string operationKey, CancellationToken ct = default);

    /// <summary>
    /// Deletes the row whose <c>Token</c> matches <paramref name="token"/> and whose
    /// <c>UserId</c> matches the current workspace scope. Returns <c>true</c> if a row was removed.
    /// </summary>
    Task<bool> UnregisterAsync(WorkspaceReadScope scope, string token,
        string operationKey, CancellationToken ct = default);
}
