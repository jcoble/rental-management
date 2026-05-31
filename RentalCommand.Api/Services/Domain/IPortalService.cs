using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Tenant/owner-facing read-only access into the portal. Every query is doubly scoped: by the caller's
/// portfolio id AND by their tenant id (from JWT claims), so a tenant only ever sees their own leases,
/// payments/balance, work orders, and portal messages. There are no write operations here (those go
/// through the staff-facing controllers).
/// </summary>
public interface IPortalService
{
    Task<IReadOnlyList<LeaseResponse>> GetLeasesAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<PortalBalanceResponse> GetBalanceAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<PortalPaymentResponse>> GetPaymentsAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkOrderResponse>> GetWorkOrdersAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<PortalMessageResponse>> GetMessagesAsync(int portfolioId, int tenantId, CancellationToken ct = default);
}
