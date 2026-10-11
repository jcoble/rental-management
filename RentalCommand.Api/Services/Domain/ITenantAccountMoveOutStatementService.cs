using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>Builds the move-out statement for one exact, staff-authorized tenant account.</summary>
public interface ITenantAccountMoveOutStatementService
{
    Task<byte[]?> GetAsync(
        WorkspaceReadScope scope,
        int tenantAccountId,
        CancellationToken ct = default);
}
