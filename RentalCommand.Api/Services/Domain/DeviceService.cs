using System.Security.Cryptography;
using System.Text;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IDeviceService"/>
public class DeviceService : IDeviceService
{
    private readonly IAtomicUnitOfWork _atomic;

    public DeviceService(IAtomicUnitOfWork atomic)
    {
        _atomic = atomic;
    }

    /// <inheritdoc/>
    public async Task RegisterAsync(
        WorkspaceReadScope scope,
        string token,
        string platform,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.DeviceRegister, 0, TokenHash(token), operationKey,
            new AtomicDeviceMutationRequest(token, platform));
        await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> UnregisterAsync(
        WorkspaceReadScope scope,
        string token,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.DeviceUnregister, 0, TokenHash(token), operationKey,
            new AtomicDeviceMutationRequest(token, null));
        var outcome = await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
        return outcome.Value.Found;
    }

    private static string TokenHash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())))
            .ToLowerInvariant()[..24];
}
