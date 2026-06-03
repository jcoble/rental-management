using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Tenant/owner-facing access to the portal. Every query is doubly scoped: by the caller's portfolio id
/// AND by their tenant id (from JWT claims), so a tenant only ever sees their own leases, payments/balance,
/// and work orders. Tenant messaging now lives in the threaded <see cref="IConversationService"/>.
/// </summary>
public interface IPortalService
{
    Task<IReadOnlyList<LeaseResponse>> GetLeasesAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<PortalBalanceResponse> GetBalanceAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<PortalPaymentResponse>> GetPaymentsAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkOrderResponse>> GetWorkOrdersAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<WorkOrderResponse?> CreateTenantWorkOrderAsync(int portfolioId, int tenantId, CreateTenantWorkOrderRequest request, CancellationToken ct = default);
}
