using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Authorization;

namespace RentalCommand.TestCommon;

/// <summary>
/// No-op <see cref="ITenantPortalProvisioningService"/> for tests that construct a service which
/// auto-provisions tenant portal logins (<c>TenantService</c>, <c>ApplicationService</c>) but do not
/// exercise that path. Reports <see cref="PortalAccountStatus.AlreadyExisted"/> without touching Identity.
/// </summary>
public sealed class NoopTenantPortalProvisioningService : ITenantPortalProvisioningService
{
    public Task<PortalAccountResult> EnsurePortalAccountForTenantAsync(
        int tenantId, int portfolioId, CancellationToken ct = default)
        => Task.FromResult(new PortalAccountResult(PortalAccountStatus.AlreadyExisted));

    public Task<PortalAccountResult> EnsurePortalAccountForTenantAuthorizedAsync(
        WorkspaceReadScope scope, int tenantId, CancellationToken ct = default)
        => Task.FromResult(new PortalAccountResult(PortalAccountStatus.AlreadyExisted));

    public Task<SetPortalAccessResult> SetPortalAccessAsync(
        int tenantId, int portfolioId, bool enabled, CancellationToken ct = default)
        => Task.FromResult(new SetPortalAccessResult(
            SetPortalAccessOutcome.Updated,
            enabled ? TenantPortalAccess.Active : TenantPortalAccess.Disabled));

    public Task<SetPortalAccessResult> SetPortalAccessAuthorizedAsync(
        WorkspaceReadScope scope, int tenantId, bool enabled, CancellationToken ct = default)
        => Task.FromResult(new SetPortalAccessResult(
            SetPortalAccessOutcome.Updated,
            enabled ? TenantPortalAccess.Active : TenantPortalAccess.Disabled));
}
